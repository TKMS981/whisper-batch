using WhisperBatch.Core;
using WhisperBatch.Engine.Media;

namespace WhisperBatch.Engine;

public sealed record TranscriptionOptions(LanguageOption Language, ChunkPlanner? Planner = null)
{
    public ChunkPlanner EffectivePlanner => Planner ?? new ChunkPlanner();
}

/// <summary>
/// 1 ファイル分の文字起こし。音声をチャンクに分けて順に認識し、境界をつなぎ合わせる。
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
            var chunkFrames = (int)(planner.ChunkDuration * rate);
            var total = reader.Duration;

            var language = options.Language.Code();
            var segments = new List<TranscriptSegment>();
            var buffer = new float[chunkFrames];
            var count = 0;
            var bufferStart = 0.0; // buffer 先頭の、ファイル先頭からの秒数
            var exhausted = false;

            progress(0);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!exhausted)
                {
                    var need = chunkFrames - count;
                    var got = reader.Read(buffer, count, need);
                    if (got < need) exhausted = true;
                    count += got;
                }
                // 末尾に残ったごく短い断片は認識しない（無音から文章を作ってしまう誤認識を避ける）
                if (count == 0 || (exhausted && count / rate < 0.3 && segments.Count > 0)) break;

                // 最後以外のチャンクは、発話の切れ目（末尾付近の最も静かな位置）までを認識する
                var length = planner.CutLength(buffer.AsSpan(0, count), rate, exhausted);
                var bufferDuration = length / rate;
                var chunkStart = bufferStart;
                var result = await context.TranscribeAsync(buffer, length, language, p =>
                {
                    if (total > 0) progress(Math.Min((chunkStart + p * bufferDuration) / total, 0.999));
                }, cancellationToken).ConfigureAwait(false);

                // 自動判定は最初に発話があったチャンクで確定し、以降のチャンクも同じ言語で認識する
                if (language is null && result.Segments.Count > 0 && result.Language is { } detected)
                {
                    language = detected;
                }

                var decision = planner.Decide(result.Segments, bufferDuration, exhausted);
                foreach (var s in decision.Accepted)
                {
                    if (s.Text.Length == 0) continue;
                    RepetitionFilter.Append(segments, new TranscriptSegment(s.Start + chunkStart, s.End + chunkStart, s.Text));
                }

                if (exhausted) break;
                var drop = Math.Min(Math.Max((int)(decision.ResumeAt * rate), 1), length);
                Array.Copy(buffer, drop, buffer, 0, count - drop);
                count -= drop;
                bufferStart += drop / rate;
            }
            progress(1);
            return new Transcript(segments, language ?? options.Language.Code());
        }, CancellationToken.None);
}
