using WhisperBatch.Core;
using WhisperBatch.Engine.Media;

namespace WhisperBatch.Engine;

/// <param name="Decoding">Whisper の探索方式（ほかの方式では使わない）</param>
public sealed record TranscriptionOptions(LanguageOption Language, ChunkPlanner? Planner = null, WhisperDecoding Decoding = WhisperDecodings.Default)
{
    public ChunkPlanner EffectivePlanner => Planner ?? new ChunkPlanner();
}

/// <summary>読み込んだモデルで 1 ファイルずつ文字起こしする</summary>
public interface ITranscriber : IDisposable
{
    SpeechModel Model { get; }

    /// <summary>認識に使っている装置の名前（GPU を使っていなければ null）</summary>
    string? GpuName { get; }

    /// <param name="progress">0...1。任意のスレッドから呼ばれる。</param>
    Task<Transcript> TranscribeAsync(string filePath, TranscriptionOptions options, Action<double> progress, CancellationToken cancellationToken);
}

public static class TranscriberLoader
{
    /// <summary>モデルを読み込む</summary>
    /// <exception cref="WhisperBatchException">モデルが未取得・読み込めない</exception>
    public static async Task<ITranscriber> LoadAsync(SpeechModel model, ModelStorage? storage)
    {
        if (storage is null || !storage.IsInstalled(model))
        {
            throw new WhisperBatchError.ModelNotInstalled(model.DisplayName).ToException();
        }
        return model.Engine switch
        {
            SpeechEngine.ReazonSpeech => await ReazonSpeechContext.LoadAsync(model, storage.DirectoryFor(model)).ConfigureAwait(false),
            _ => await WhisperContext.LoadAsync(model, storage.FilePath(model.Files[0], model)).ConfigureAwait(false),
        };
    }
}

/// <summary>
/// 1 ファイル分の文字起こし（Whisper）。音声をチャンクに分けて順に認識し、境界をつなぎ合わせる。
///
/// 前のチャンクの文章を次のチャンクの文脈（initial prompt）として渡すことはしない。
/// 試験では、渡すと境界付近で文の重複や読み飛ばしが増えたため。
/// </summary>
public static class TranscriptionEngine
{
    /// <param name="progress">0...1。任意のスレッドから呼ばれる。</param>
    public static Task<Transcript> TranscribeAsync(
        string filePath,
        WhisperContext context,
        TranscriptionOptions options,
        Action<double> progress,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = AudioSampleReader.Open(filePath);
            const double rate = AudioSampleReader.SampleRate;
            var planner = options.EffectivePlanner;
            var total = reader.Duration;
            var window = new AudioStreamBuffer(reader, rate, (int)(planner.ChunkDuration * rate));

            var language = options.Language.Code();
            var segments = new List<TranscriptSegment>();

            progress(0);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                window.Fill(cancellationToken);
                // 末尾に残ったごく短い断片は認識しない（無音から文章を作ってしまう誤認識を避ける）
                if (window.IsEmpty || (window.IsExhausted && window.Duration < 0.3 && segments.Count > 0)) break;

                // 最後以外のチャンクは、発話の切れ目（末尾付近の最も静かな位置）までを認識する
                var length = planner.CutLength(window.Samples, rate, window.IsExhausted);
                var chunkDuration = length / rate;
                var chunkStart = window.StartTime;
                var result = await context.TranscribeAsync(window.Buffer, length, language, options.Decoding, p =>
                {
                    if (total > 0) progress(Math.Min((chunkStart + p * chunkDuration) / total, 0.999));
                }, cancellationToken).ConfigureAwait(false);

                // 自動判定は最初に発話があったチャンクで確定し、以降のチャンクも同じ言語で認識する
                if (language is null && result.Segments.Count > 0 && result.Language is { } detected)
                {
                    language = detected;
                }

                var decision = planner.Decide(result.Segments, chunkDuration, window.IsExhausted);
                foreach (var s in decision.Accepted)
                {
                    if (s.Text.Length == 0) continue;
                    RepetitionFilter.Append(segments, new TranscriptSegment(s.Start + chunkStart, s.End + chunkStart, s.Text));
                }

                if (window.IsExhausted) break;
                window.Consume(Math.Min(Math.Max((int)(decision.ResumeAt * rate), 1), length));
            }
            progress(1);
            return new Transcript(segments, language ?? options.Language.Code());
        }, CancellationToken.None);
}
