using System.Diagnostics;
using WhisperBatch.Core;
using WhisperBatch.Core.Tests;
using Xunit.Abstractions;

namespace WhisperBatch.Engine.Tests;

/// <summary>（調査用）同じ音声を GPU / CPU で認識して比べる</summary>
public class BackendComparison(ITestOutputHelper output)
{
    [EnvTheory(TestEnv.AudioDir, TestEnv.Model, "WB_COMPARE")]
    [InlineData(true, true), InlineData(true, false), InlineData(false, false)]
    public async Task Compare(bool gpu, bool fa)
    {
        TestLocalization.SetUp();
        var path = TestEnv.Get(TestEnv.Model)!;
        var model = ModelCatalog.All.First(m => m.Files[0].Name == Path.GetFileName(path));
        using var ctx = await WhisperContext.LoadAsync(model, path, gpu, fa);
        var w = Stopwatch.StartNew();
        var t = await TranscriptionEngine.TranscribeAsync(TestEnv.Media("long.wav"), ctx, new TranscriptionOptions(LanguageOption.Japanese), _ => { });
        output.WriteLine($"gpu={gpu} fa={fa} device={ctx.Device.Description} {w.Elapsed.TotalSeconds:F1}s");
        foreach (var s in t.Segments.Where(s => s.Start is > 70 and < 90)) output.WriteLine($"  [{s.Start:F2}-{s.End:F2}] {s.Text}");
        output.WriteLine($"  segments={t.Segments.Count}");
    }
}
