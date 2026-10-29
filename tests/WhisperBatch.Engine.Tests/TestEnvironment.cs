using System.Security.Cryptography;

// GPU とモデルを共有するテストが多いので、同時には実行しない
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WhisperBatch.Engine.Tests;

// 実ファイルを使う結合テストは、次の環境変数を設定したときだけ実行する。
//   WHISPER_BATCH_TEST_AUDIO_DIR    : テスト音声のフォルダ（scripts/make-test-audio.ps1 で作成）
//   WHISPER_BATCH_TEST_MODEL        : ggml モデルファイルのパス（例: ggml-large-v3-turbo-q5_0.bin）
//   WHISPER_BATCH_TEST_DOWNLOAD_DIR : モデルのダウンロードを試す保存先（tiny を取得する）
//   WB_SMALL_VOLUME                 : 空き容量の少ないボリューム上のフォルダ（容量不足のテスト用）
internal static class TestEnv
{
    public const string AudioDir = "WHISPER_BATCH_TEST_AUDIO_DIR";
    public const string Model = "WHISPER_BATCH_TEST_MODEL";
    public const string DownloadDir = "WHISPER_BATCH_TEST_DOWNLOAD_DIR";
    public const string SmallVolume = "WB_SMALL_VOLUME";

    public static string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    public static string Media(string name) => Path.Combine(Get(AudioDir)!, name);

    public static string TempMp3() => Path.Combine(Path.GetTempPath(), $"wb-test-{Guid.NewGuid()}.mp3");

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>基準にする音声（ja1.wav）の長さ（秒）</summary>
    public static double ReferenceDuration
    {
        get
        {
            using var reader = new NAudio.Wave.WaveFileReader(Media("ja1.wav"));
            return reader.TotalTime.TotalSeconds;
        }
    }
}

/// <summary>環境変数がすべて設定されているときだけ実行する</summary>
public sealed class EnvFactAttribute : FactAttribute
{
    public EnvFactAttribute(params string[] variables)
    {
        var missing = variables.Where(v => TestEnv.Get(v) is null).ToList();
        if (missing.Count > 0) Skip = "環境変数が未設定: " + string.Join(", ", missing);
    }
}

/// <summary>環境変数がすべて設定されているときだけ実行する</summary>
public sealed class EnvTheoryAttribute : TheoryAttribute
{
    public EnvTheoryAttribute(params string[] variables)
    {
        var missing = variables.Where(v => TestEnv.Get(v) is null).ToList();
        if (missing.Count > 0) Skip = "環境変数が未設定: " + string.Join(", ", missing);
    }
}

internal sealed class ProgressRecorder
{
    private readonly List<double> _values = [];
    public void Record(double v) { lock (_values) _values.Add(v); }
    public double? Last { get { lock (_values) return _values.Count > 0 ? _values[^1] : null; } }
    public int Count { get { lock (_values) return _values.Count; } }
}
