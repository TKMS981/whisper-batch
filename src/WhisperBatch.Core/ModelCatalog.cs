using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Core;

/// <summary>whisper.cpp 用 ggml 形式のモデル</summary>
/// <param name="SizeBytes">ダウンロードサイズ（バイト）</param>
/// <param name="Sha256">SHA-256（Hugging Face の LFS メタデータと同じ値）</param>
public sealed record WhisperModel(string Id, string FileName, long SizeBytes, string Sha256)
{
    /// <summary>表示名と説明（表示言語に従う）</summary>
    public string DisplayName => L($"model.{Id}.name");
    public string Summary => L($"model.{Id}.summary");

    public Uri DownloadUrl => new($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}");

    /// <summary>必要な空き容量の目安。ダウンロード中の一時ファイル分として 10% と 50MB の余裕を見る。</summary>
    public long RequiredFreeSpace => SizeBytes + Math.Max(SizeBytes / 10, 50_000_000);

    public string SizeText => ByteCount.Format(SizeBytes);
    public string RequiredSpaceText => ByteCount.Format(RequiredFreeSpace);
}

public static class ModelCatalog
{
    /// <summary>
    /// 多言語モデルのみ（日本語を扱うため .en モデルは含めない）。
    /// サイズと SHA-256 は https://huggingface.co/ggerganov/whisper.cpp の値（2026-10 確認）。
    /// </summary>
    public static readonly IReadOnlyList<WhisperModel> All =
    [
        new("tiny", "ggml-tiny.bin", 77_691_713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        new("base", "ggml-base.bin", 147_951_465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        new("small", "ggml-small.bin", 487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        new("medium", "ggml-medium.bin", 1_533_763_059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
        new("large-v3-turbo-q5_0", "ggml-large-v3-turbo-q5_0.bin", 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        new("large-v3-turbo", "ggml-large-v3-turbo.bin", 1_624_555_275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        new("large-v3", "ggml-large-v3.bin", 3_095_033_483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
    ];

    public const string DefaultModelId = "large-v3-turbo-q5_0";

    public static WhisperModel DefaultModel => Model(DefaultModelId)!;

    public static WhisperModel? Model(string? id) => All.FirstOrDefault(m => m.Id == id);
}
