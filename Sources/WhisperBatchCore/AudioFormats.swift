import Foundation
import UniformTypeIdentifiers

/// 追加できるファイルの判定。
///
/// 判定は 2 段階:
/// 1. 追加時: 拡張子の UTType が「音声」または「音声付き動画」に該当するか（ここ）
/// 2. 読み込み時: AVFoundation で実際に読めるか、動画か音声か（エンジン側）
public enum AudioFormats {
    /// ファイル選択ダイアログで許可する種類
    public static let allowedContentTypes: [UTType] = [.audio, .audiovisualContent]

    /// 対象外とした理由
    public enum RejectionReason: Equatable, Sendable {
        case noExtension
        case unknownType(ext: String)
        case notMedia(ext: String)

        public var message: String {
            switch self {
            case .noExtension: L("format.reject.noExtension")
            case .unknownType(let ext): L("format.reject.unknownType", ext)
            case .notMedia(let ext): L("format.reject.notMedia", ext)
            }
        }
    }

    public enum Check: Equatable, Sendable {
        case accepted
        case rejected(RejectionReason)
    }

    public static func check(_ url: URL) -> Check {
        let ext = url.pathExtension.lowercased()
        guard !ext.isEmpty else { return .rejected(.noExtension) }
        guard let type = UTType(filenameExtension: ext) else { return .rejected(.unknownType(ext: ext)) }
        if allowedContentTypes.contains(where: { type.conforms(to: $0) }) {
            return .accepted
        }
        return .rejected(.notMedia(ext: ext))
    }

    /// UI に表示する対応形式の説明（README の「対応形式」と同じく、実ファイルで読み込みを確認した形式）
    public static var summary: String { L("format.summary") }
}

/// ファイルの中身の種類（読み込んで確認した結果）
public enum MediaKind: Equatable, Sendable {
    case audio
    /// 映像トラックを持つファイル。音声を取り出して MP3 に変換してから文字起こしする。
    case video
}
