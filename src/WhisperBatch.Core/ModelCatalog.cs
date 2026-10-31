using static WhisperBatch.Core.Loc;

namespace WhisperBatch.Core;

/// <summary>
/// 文字起こしエンジン（認識方式）。
/// macOS 版の 3 つ目の方式（Apple の音声認識）は macOS の機能なので、Windows 版にはない。
/// </summary>
public enum SpeechEngine
{
    /// <summary>whisper.cpp で動かす OpenAI Whisper</summary>
    Whisper,
    /// <summary>sherpa-onnx で動かす ReazonSpeech（日本語専用）</summary>
    ReazonSpeech,
}

public static class SpeechEngines
{
    public static readonly SpeechEngine[] All = [SpeechEngine.Whisper, SpeechEngine.ReazonSpeech];

    /// <summary>文字列キーと設定の保存に使う名前（macOS 版と同じ）</summary>
    public static string RawValue(this SpeechEngine engine) => engine switch
    {
        SpeechEngine.ReazonSpeech => "reazonSpeech",
        _ => "whisper",
    };

    public static SpeechEngine? FromStorage(string? value) => All.Cast<SpeechEngine?>().FirstOrDefault(e => e!.Value.RawValue() == value);

    /// <summary>表示名と説明（表示言語に従う）</summary>
    public static string DisplayName(this SpeechEngine engine) => L($"engine.{engine.RawValue()}.name");
    public static string Summary(this SpeechEngine engine) => L($"engine.{engine.RawValue()}.summary");

    /// <summary>取得元とライセンスの説明</summary>
    public static string SourceDescription(this SpeechEngine engine) => L($"engine.{engine.RawValue()}.source");

    /// <summary>モデル管理の説明</summary>
    public static string ModelsMessage(this SpeechEngine engine) => L($"models.message.{engine.RawValue()}");

    /// <summary>文字起こしの言語に対応しているか</summary>
    public static bool Supports(this SpeechEngine engine, LanguageOption language) => engine switch
    {
        SpeechEngine.ReazonSpeech => language == LanguageOption.Japanese,
        _ => true,
    };

    /// <summary>言語の選択肢（この方式で選べるもの）</summary>
    public static IReadOnlyList<LanguageOption> LanguageOptions(this SpeechEngine engine) =>
        Core.LanguageOptions.All.Where(l => engine.Supports(l)).ToList();

    /// <summary>この方式で既定（おすすめ）のモデル</summary>
    public static string DefaultModelId(this SpeechEngine engine) => engine switch
    {
        SpeechEngine.ReazonSpeech => "reazonspeech-k2-v2-int8",
        _ => "large-v3-turbo-q5_0",
    };
}

/// <summary>モデルを構成する 1 ファイル</summary>
/// <param name="Name">保存するファイル名</param>
/// <param name="SizeBytes">ダウンロードサイズ（バイト）</param>
/// <param name="Sha256">SHA-256（Hugging Face の LFS メタデータと同じ値。LFS 外のファイルは取得して確認した値）</param>
public sealed record ModelFile(string Name, Uri Url, long SizeBytes, string Sha256);

/// <summary>文字起こしモデル</summary>
/// <param name="Files">ダウンロードするファイル</param>
/// <param name="Folder">保存先フォルダ内のサブフォルダ（null なら保存先の直下。Whisper は以前の版と同じ場所に置く）</param>
public sealed record SpeechModel(string Id, SpeechEngine Engine, IReadOnlyList<ModelFile> Files, string? Folder = null)
{
    /// <summary>表示名と説明（表示言語に従う）</summary>
    public string DisplayName => L($"model.{Id}.name");
    public string Summary => L($"model.{Id}.summary");

    /// <summary>ダウンロードサイズの合計（バイト）</summary>
    public long SizeBytes => Files.Sum(f => f.SizeBytes);

    /// <summary>必要な空き容量の目安。ダウンロード中の一時ファイル分として 10% と 50MB の余裕を見る。</summary>
    public long RequiredFreeSpace => RequiredFreeSpaceFor(SizeBytes);

    public static long RequiredFreeSpaceFor(long bytes) => bytes + Math.Max(bytes / 10, 50_000_000);

    public string SizeText => ByteCount.Format(SizeBytes);
    public string RequiredSpaceText => ByteCount.Format(RequiredFreeSpace);

    public bool IsDefaultForEngine => Id == Engine.DefaultModelId();

    /// <summary>一覧に出すファイル名（複数ファイルのモデルはフォルダ名）</summary>
    public string FilesLabel => Files.Count == 1 ? Files[0].Name : Folder ?? Files[0].Name;

    /// <summary>取得元のホスト名（確認ダイアログに表示する）</summary>
    public string SourceHost => Files.Count > 0 ? Files[0].Url.Host : "huggingface.co";
}

public static class ModelCatalog
{
    // 定義用（下の一覧より先に初期化する）

    /// <summary>ggerganov/whisper.cpp のコミット（2024-10-29 更新）。main が更新されても同じファイルを取得できるよう固定する。</summary>
    internal const string WhisperRevision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    internal const string ReazonRevision = "291488c8151be24d7da4bf7af26e533fad96e407";

    private static SpeechModel WhisperModel(string id, string fileName, long size, string sha256) =>
        new(id, SpeechEngine.Whisper,
            [new ModelFile(fileName, new Uri($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{WhisperRevision}/{fileName}"), size, sha256)]);

    private static ModelFile ReazonFile(string name, long size, string sha256) =>
        new(name, new Uri($"https://huggingface.co/reazon-research/reazonspeech-k2-v2/resolve/{ReazonRevision}/{name}"), size, sha256);

    private static readonly ModelFile ReazonTokens =
        ReazonFile("tokens.txt", 45_754, "2c3ac659818a48a0c04010e0593bbc4d7c8a24a054340b01131499c05fd52def");

    /// <summary>
    /// whisper.cpp 用（ggml 形式）。多言語モデルのみ（日本語を扱うため .en モデルは含めない）。
    /// https://huggingface.co/ggerganov/whisper.cpp から、コミットを固定して取得する。
    /// サイズと SHA-256 はそのコミットの LFS メタデータの値（2026-10 確認）。
    /// </summary>
    private static readonly SpeechModel[] Whisper =
    [
        WhisperModel("tiny", "ggml-tiny.bin", 77_691_713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        WhisperModel("base", "ggml-base.bin", 147_951_465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        WhisperModel("small", "ggml-small.bin", 487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        WhisperModel("medium", "ggml-medium.bin", 1_533_763_059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
        WhisperModel("large-v3-turbo-q5_0", "ggml-large-v3-turbo-q5_0.bin", 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        WhisperModel("large-v3-turbo", "ggml-large-v3-turbo.bin", 1_624_555_275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        WhisperModel("large-v3", "ggml-large-v3.bin", 3_095_033_483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
    ];

    /// <summary>
    /// ReazonSpeech k2 v2（Zipformer RNN-T、159M パラメーター、Apache-2.0）。
    /// 公式リポジトリ https://huggingface.co/reazon-research/reazonspeech-k2-v2 の ONNX ファイルを、
    /// コミットを固定して取得する。サイズと SHA-256 は LFS メタデータの値（tokens.txt は取得して確認、2026-10）。
    /// </summary>
    private static readonly SpeechModel[] Reazon =
    [
        new("reazonspeech-k2-v2-int8", SpeechEngine.ReazonSpeech,
        [
            ReazonFile("encoder-epoch-99-avg-1.int8.onnx", 154_670_139, "2c7bd08a8a99f9ddd0d9e458456577b1f6279214e51426f114f9eced44c54e1d"),
            ReazonFile("decoder-epoch-99-avg-1.int8.onnx", 2_959_337, "8f0bff94d38797b03b762634ed03211a8e303d06cc4603cdd0cf4199d6eb1485"),
            ReazonFile("joiner-epoch-99-avg-1.int8.onnx", 2_696_970, "49cc7ea1d3d35a40a27442db5e89996da64bf0e683a903dce76e99e57a12e4de"),
            ReazonTokens,
        ], "reazonspeech-k2-v2-int8"),
        new("reazonspeech-k2-v2", SpeechEngine.ReazonSpeech,
        [
            ReazonFile("encoder-epoch-99-avg-1.onnx", 592_347_848, "ecdb0b771e16104aaf8e579cb3c1e32fbd589eb641c5946d82b615bd366c5f96"),
            ReazonFile("decoder-epoch-99-avg-1.onnx", 11_767_836, "58b18211ae06265466bfa17172dab574df94f76c8bcb61a3640c28ba860e4124"),
            ReazonFile("joiner-epoch-99-avg-1.onnx", 10_720_115, "d38a81d1191c9ed6de6a1719503692e07e3e973e2364adde0abae5eaaded1174"),
            ReazonTokens,
        ], "reazonspeech-k2-v2"),
    ];

    public static readonly IReadOnlyList<SpeechModel> All = [.. Whisper, .. Reazon];

    /// <summary>以前の版の既定（設定が保存されていないときに使う）</summary>
    public static string DefaultModelId => SpeechEngine.Whisper.DefaultModelId();

    public static SpeechModel DefaultModel => Model(DefaultModelId)!;

    public static SpeechModel? Model(string? id) => All.FirstOrDefault(m => m.Id == id);

    public static IReadOnlyList<SpeechModel> Models(SpeechEngine engine) => All.Where(m => m.Engine == engine).ToList();

    public static SpeechModel DefaultModelFor(SpeechEngine engine) => Model(engine.DefaultModelId())!;

}

/// <summary>Whisper の探索方式（デコード方式）</summary>
public enum WhisperDecoding
{
    /// <summary>精度優先: 複数の候補（ビーム幅 5）を並行して比べる（Beam Search）。従来からの既定。</summary>
    BeamSearch,
    /// <summary>速度優先: 各時点で最も確からしい語だけを選ぶ（Greedy）</summary>
    Greedy,
}

public static class WhisperDecodings
{
    public static readonly WhisperDecoding[] All = [WhisperDecoding.BeamSearch, WhisperDecoding.Greedy];

    public const WhisperDecoding Default = WhisperDecoding.BeamSearch;

    /// <summary>保存に使う名前（macOS 版と同じ）</summary>
    public static string RawValue(this WhisperDecoding decoding) => decoding == WhisperDecoding.Greedy ? "greedy" : "beamSearch";

    public static WhisperDecoding FromStorage(string? value) => value == "greedy" ? WhisperDecoding.Greedy : Default;

    public static string DisplayName(this WhisperDecoding decoding) =>
        decoding == WhisperDecoding.Greedy ? L("decoding.greedy") : L("decoding.beamSearch");
}
