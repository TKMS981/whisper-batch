import Foundation

/// アプリ全体で使うエラー。`errorDescription` は利用者にそのまま表示できる文にする（表示言語に従う）。
public enum WhisperBatchError: Error, Equatable, Sendable {
    // 入力ファイル
    case fileNotFound(name: String)
    case fileAccessDenied(name: String)
    case unsupportedFormat(name: String)
    case noAudioTrack(name: String)
    case videoHasNoAudio(name: String)
    case audioDecodingFailed(name: String, detail: String)
    /// ファイルのあるディスク（外付け・ネットワーク上）に接続できない
    case volumeUnavailable(name: String)

    // 動画の変換
    case conversionFailed(name: String, detail: String)

    // モデル
    case modelNotInstalled(modelName: String)
    case modelLoadFailed(modelName: String)
    case insufficientDiskSpace(required: Int64, available: Int64)
    case downloadFailed(detail: String)
    case downloadHTTPError(statusCode: Int)
    case checksumMismatch(modelName: String)
    case languageNotSupported(modelName: String, language: String)
    case speechRecognitionUnavailable

    // 通信
    case networkOffline
    case networkInterrupted
    case serverUnreachable

    // 保存先
    /// 空き容量がなくなった（必要量が分からない場合）
    case diskFull
    case writeAccessDenied(fileName: String)
    case readOnlyVolume

    // 認識
    case transcriptionFailed(code: Int32)

    // 出力
    case exportFailed(fileName: String, detail: String)
    case nothingToExport

    // キュー
    case invalidStateTransition(from: String, to: String)
}

extension WhisperBatchError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .fileNotFound(let name):
            return L("error.fileNotFound", name)
        case .fileAccessDenied(let name):
            return L("error.fileAccessDenied", name)
        case .unsupportedFormat(let name):
            return L("error.unsupportedFormat", name)
        case .noAudioTrack(let name):
            return L("error.noAudioTrack", name)
        case .videoHasNoAudio(let name):
            return L("error.videoHasNoAudio", name)
        case .audioDecodingFailed(let name, let detail):
            return L("error.audioDecodingFailed", name, detail)
        case .volumeUnavailable(let name):
            return L("error.volumeUnavailable", name)
        case .conversionFailed(let name, let detail):
            return L("error.conversionFailed", name, detail)
        case .modelNotInstalled(let modelName):
            return L("error.modelNotInstalled", modelName)
        case .modelLoadFailed(let modelName):
            return L("error.modelLoadFailed", modelName)
        case .insufficientDiskSpace(let required, let available):
            let f = ByteCountFormatter.fileSize
            return L("error.insufficientDiskSpace", f.string(fromByteCount: required), f.string(fromByteCount: available))
        case .downloadFailed(let detail):
            return L("error.downloadFailed", detail)
        case .downloadHTTPError(let statusCode):
            return L("error.downloadHTTPError", statusCode)
        case .checksumMismatch(let modelName):
            return L("error.checksumMismatch", modelName)
        case .languageNotSupported(let modelName, let language):
            return L("error.languageNotSupported", modelName, language)
        case .speechRecognitionUnavailable:
            return L("error.speechRecognitionUnavailable")
        case .networkOffline:
            return L("error.networkOffline")
        case .networkInterrupted:
            return L("error.networkInterrupted")
        case .serverUnreachable:
            return L("error.serverUnreachable")
        case .diskFull:
            return L("error.diskFull")
        case .writeAccessDenied(let fileName):
            return L("error.writeAccessDenied", fileName)
        case .readOnlyVolume:
            return L("error.readOnlyVolume")
        case .transcriptionFailed(let code):
            return L("error.transcriptionFailed", code)
        case .exportFailed(let fileName, let detail):
            return L("error.exportFailed", fileName, detail)
        case .nothingToExport:
            return L("error.nothingToExport")
        case .invalidStateTransition(let from, let to):
            return L("error.invalidStateTransition", from, to)
        }
    }
}

extension ByteCountFormatter {
    /// ファイルサイズ表示用（1000 単位。Finder の表示と揃える）
    public static var fileSize: ByteCountFormatter {
        let f = ByteCountFormatter()
        f.countStyle = .file
        f.allowedUnits = [.useMB, .useGB, .useKB]
        return f
    }
}

/// どんなエラーでも利用者向けの文にする。OS のエラーは、分かる範囲で次の行動を含む文にする（`ErrorMapper`）。
public func userFacingMessage(for error: any Error, during operation: ErrorMapper.Operation = .unspecified) -> String {
    if let e = ErrorMapper.map(error, during: operation), let d = e.errorDescription { return d }
    if let e = error as? LocalizedError, let d = e.errorDescription { return d }
    return (error as NSError).localizedDescription
}
