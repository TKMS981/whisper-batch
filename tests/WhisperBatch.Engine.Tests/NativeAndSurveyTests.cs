using WhisperBatch.Core;
using WhisperBatch.Core.Tests;
using WhisperBatch.Engine.Media;
using WhisperBatch.Engine.Native;
using Xunit.Abstractions;

namespace WhisperBatch.Engine.Tests;

/// <summary>whisper.cpp の DLL（常に実行）</summary>
public class NativeRuntimeTests(ITestOutputHelper output)
{
    /// <summary>C# 側の構造体が whisper.dll（v1.9.5）と同じ並びになっている</summary>
    [Fact]
    public void StructLayoutMatchesDll()
    {
        NativeRuntime.EnsureInitialized();
        Assert.Empty(NativeRuntime.VerifyLayout());
        output.WriteLine("whisper.cpp " + NativeRuntime.Version);
    }

    /// <summary>CUDA のバックエンドが読み込まれ、GPU が見えている（NVIDIA GPU のない環境ではスキップ扱いにしない）</summary>
    [Fact]
    public void CudaBackendIsLoaded()
    {
        foreach (var d in NativeRuntime.Devices) output.WriteLine($"{d.Name}: {d.Description} (GPU={d.IsGpu})");
        Assert.Contains(NativeRuntime.Devices, d => !d.IsGpu);
        var gpu = NativeRuntime.Gpu;
        Assert.NotNull(gpu);
        Assert.StartsWith("CUDA", gpu.Name);
    }
}

/// <summary>Windows（Media Foundation）で読める形式の調査。結果を出力するだけで失敗にはしない。</summary>
public class FormatSurvey(ITestOutputHelper output)
{
    [EnvFact(TestEnv.AudioDir)]
    public void Survey()
    {
        TestLocalization.SetUp();
        foreach (var file in Directory.EnumerateFiles(TestEnv.Get(TestEnv.AudioDir)!).Order())
        {
            var name = Path.GetFileName(file);
            try
            {
                using var reader = AudioSampleReader.Open(file);
                var total = 0;
                var buf = new float[160_000];
                while (true)
                {
                    var n = reader.Read(buf, 0, buf.Length);
                    total += n;
                    if (n < buf.Length) break;
                }
                var (video, dur) = MediaSource.Probe(file);
                output.WriteLine($"OK   {name,-24} video={video,-5} duration={dur.TotalSeconds:F2}s decoded={total / 16000.0:F2}s");
            }
            catch (Exception e)
            {
                output.WriteLine($"FAIL {name,-24} {e.GetType().Name}: {ErrorMessages.UserFacing(e)}");
            }
        }
    }
}
