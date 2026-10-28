import Foundation

/// whisper.cpp 用 ggml 形式のモデル
public struct WhisperModel: Identifiable, Hashable, Sendable {
    public let id: String
    public let fileName: String
    /// ダウンロードサイズ（バイト）
    public let sizeBytes: Int64
    /// SHA-256（Hugging Face の LFS メタデータと同じ値）
    public let sha256: String

    /// 表示名と説明（表示言語に従う）
    public var displayName: String { L("model.\(id).name") }
    public var summary: String { L("model.\(id).summary") }

    public var downloadURL: URL {
        URL(string: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/\(fileName)")!
    }

    /// 必要な空き容量の目安。ダウンロード中の一時ファイル分として 10% と 50MB の余裕を見る。
    public var requiredFreeSpace: Int64 {
        sizeBytes + max(sizeBytes / 10, 50_000_000)
    }

    public var sizeText: String { ByteCountFormatter.fileSize.string(fromByteCount: sizeBytes) }
    public var requiredSpaceText: String { ByteCountFormatter.fileSize.string(fromByteCount: requiredFreeSpace) }
}

public enum ModelCatalog {
    /// 多言語モデルのみ（日本語を扱うため .en モデルは含めない）。
    /// サイズと SHA-256 は https://huggingface.co/ggerganov/whisper.cpp の値（2026-10 確認）。
    public static let all: [WhisperModel] = [
        WhisperModel(
            id: "tiny", fileName: "ggml-tiny.bin",
            sizeBytes: 77_691_713,
            sha256: "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        WhisperModel(
            id: "base", fileName: "ggml-base.bin",
            sizeBytes: 147_951_465,
            sha256: "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        WhisperModel(
            id: "small", fileName: "ggml-small.bin",
            sizeBytes: 487_601_967,
            sha256: "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        WhisperModel(
            id: "medium", fileName: "ggml-medium.bin",
            sizeBytes: 1_533_763_059,
            sha256: "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
        WhisperModel(
            id: "large-v3-turbo-q5_0", fileName: "ggml-large-v3-turbo-q5_0.bin",
            sizeBytes: 574_041_195,
            sha256: "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        WhisperModel(
            id: "large-v3-turbo", fileName: "ggml-large-v3-turbo.bin",
            sizeBytes: 1_624_555_275,
            sha256: "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        WhisperModel(
            id: "large-v3", fileName: "ggml-large-v3.bin",
            sizeBytes: 3_095_033_483,
            sha256: "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
    ]

    public static let defaultModelID = "large-v3-turbo-q5_0"

    public static var defaultModel: WhisperModel { model(id: defaultModelID)! }

    public static func model(id: String) -> WhisperModel? {
        all.first { $0.id == id }
    }
}
