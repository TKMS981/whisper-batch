using WhisperBatch.Core;
using WhisperBatch.Core.Tests;
using Xunit.Abstractions;

namespace WhisperBatch.Engine.Tests;

// ReazonSpeech の結合テスト。次の環境変数を設定したときだけ実行する。
//   WHISPER_BATCH_TEST_AUDIO_DIR    : テスト音声のフォルダ（ja1.wav と、1 分以上の long.wav を使う）
//   WHISPER_BATCH_TEST_REAZON_DIR   : ReazonSpeech のファイル（encoder / decoder / joiner / tokens.txt）を置いたフォルダ
//   WHISPER_BATCH_TEST_REAZON_MODEL : そのフォルダのモデルの ID（省略時は reazonspeech-k2-v2-int8）

public sealed class ReazonFixture : IDisposable
{
    public const string Dir = "WHISPER_BATCH_TEST_REAZON_DIR";

    public ReazonSpeechContext? Context { get; }

    public ReazonFixture()
    {
        TestLocalization.SetUp();
        if (TestEnv.Get(Dir) is not { } dir) return;
        var model = ModelCatalog.Model(TestEnv.Get("WHISPER_BATCH_TEST_REAZON_MODEL") ?? "reazonspeech-k2-v2-int8")!;
        // テスト用のフォルダにはファイルを直接置くので、保存先のサブフォルダではなくそのフォルダを使う
        Context = ReazonSpeechContext.LoadAsync(model, dir).GetAwaiter().GetResult();
    }

    public void Dispose() => Context?.Dispose();
}

public class ReazonSpeechTests(ReazonFixture fixture, ITestOutputHelper output) : IClassFixture<ReazonFixture>
{
    private ReazonSpeechContext Ctx => fixture.Context!;

    private static readonly TranscriptionOptions Japanese = new(LanguageOption.Japanese);

    [EnvFact(TestEnv.AudioDir, ReazonFixture.Dir)]
    public async Task TranscribesJapanese()
    {
        var progress = new ProgressRecorder();
        var t = await Ctx.TranscribeAsync(TestEnv.Media("ja1.wav"), Japanese, progress.Record, default);
        var text = TranscriptFormatter.PlainText(t);
        output.WriteLine($"reazon: {text} {string.Join(", ", t.Segments.Select(s => $"{s.Start:F2}-{s.End:F2}"))}");
        Assert.True(text.Contains("晴天") || text.Contains("テスト"), text);
        Assert.Equal("ja", t.Language);
        Assert.Equal(1, progress.Last);
        Assert.All(t.Segments, s => Assert.True(s.End >= s.Start));
        Assert.Null(Ctx.GpuName);
    }

    /// <summary>30 秒を超える音声は区切って認識し、時刻は音声の先頭からの秒数になる</summary>
    [EnvFact(TestEnv.AudioDir, ReazonFixture.Dir)]
    public async Task TranscribesLongAudioInPieces()
    {
        var t = await Ctx.TranscribeAsync(TestEnv.Media("long.wav"), Japanese, _ => { }, default);
        output.WriteLine(string.Join("\n", t.Segments.Select(s => $"[{s.Start:F1}-{s.End:F1}] {s.Text}")));
        Assert.True((t.Segments.LastOrDefault()?.End ?? 0) > 30);
        Assert.True(t.Segments.Zip(t.Segments.Skip(1)).All(p => p.First.Start <= p.Second.Start));
    }

    [EnvFact(TestEnv.AudioDir, ReazonFixture.Dir)]
    public async Task Cancels()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ctx.TranscribeAsync(TestEnv.Media("long.wav"), Japanese, _ => { }, cts.Token));
        // 中止した後も続けて使える
        var t = await Ctx.TranscribeAsync(TestEnv.Media("ja2.wav"), Japanese, _ => { }, default);
        Assert.NotEmpty(t.Segments);
    }

    /// <summary>モデルのファイルが揃っていないフォルダは、利用者向けのエラーになる</summary>
    [EnvFact(ReazonFixture.Dir)]
    public async Task MissingFilesFailWithReadableError()
    {
        var empty = Directory.CreateTempSubdirectory("wb-reazon-empty-").FullName;
        try
        {
            var model = ModelCatalog.Model("reazonspeech-k2-v2-int8")!;
            var e = await Assert.ThrowsAsync<WhisperBatchException>(() => ReazonSpeechContext.LoadAsync(model, empty));
            Assert.IsType<WhisperBatchError.ModelLoadFailed>(e.Error);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
