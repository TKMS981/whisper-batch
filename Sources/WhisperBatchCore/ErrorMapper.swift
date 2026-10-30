import Foundation

/// OS が返したエラー（ファイルの権限、空き容量、ディスクの切断、ネットワークなど）を、
/// 利用者が次に何をすればよいか分かる `WhisperBatchError` に置き換える。
///
/// 同じ OS のエラーでも、読み込み中か保存中かで取るべき行動が変わるため、何をしていたかを `Operation` で渡す。
public enum ErrorMapper {
    /// エラーが起きたときにしていたこと
    public enum Operation: Equatable, Sendable {
        /// 利用者が追加したファイルを読み込んでいた
        case reading(fileName: String)
        /// 利用者が選んだ場所へ保存していた（一時ファイルへの書き込みも含む）
        case writing(fileName: String)
        /// モデルをダウンロードしていた
        case downloading
        /// 分からない（エラーに含まれるファイルのパスから推測する）
        case unspecified
    }

    /// OS のエラーの分類
    enum Cause: Equatable {
        case notFound
        /// `isWrite` は読み書きのどちらが拒否されたか（分からなければ nil）
        case permissionDenied(isWrite: Bool?)
        case noSpace
        case readOnlyVolume
        /// 外付けディスクやネットワーク上の共有フォルダが外れた・応答しない
        case volumeUnavailable
        case offline
        case connectionInterrupted
        case serverUnreachable
    }

    /// 対応する分類がなければ nil（呼び出し側で従来のエラーにする）。
    /// 既に `WhisperBatchError` ならそのまま返す。キャンセルは nil。
    public static func map(_ error: any Error, during operation: Operation = .unspecified) -> WhisperBatchError? {
        if let error = error as? WhisperBatchError { return error }
        if error is CancellationError { return nil }
        guard let cause = cause(of: error) else { return nil }
        let name = operation.fileName ?? fileName(in: error as NSError)

        switch cause {
        case .noSpace:
            return .diskFull
        case .offline:
            return .networkOffline
        case .connectionInterrupted:
            return .networkInterrupted
        case .serverUnreachable:
            return .serverUnreachable
        case .readOnlyVolume:
            return .readOnlyVolume
        case .notFound:
            // 保存中の「見つからない」は保存先フォルダの問題なので、ファイルの案内はしない
            if case .reading(let fileName) = operation { return .fileNotFound(name: fileName) }
            if operation == .unspecified, let name { return .fileNotFound(name: name) }
            return nil
        case .volumeUnavailable:
            return name.map { .volumeUnavailable(name: $0) }
        case .permissionDenied(let isWrite):
            switch operation {
            case .reading(let fileName): return .fileAccessDenied(name: fileName)
            case .writing(let fileName): return .writeAccessDenied(fileName: fileName)
            case .downloading: return nil
            case .unspecified:
                guard let name, let isWrite else { return nil }
                return isWrite ? .writeAccessDenied(fileName: name) : .fileAccessDenied(name: name)
            }
        }
    }

    /// エラーとその原因（NSUnderlyingErrorKey）をたどって分類する
    static func cause(of error: any Error) -> Cause? {
        var current: NSError? = error as NSError
        var depth = 0
        while let ns = current, depth < 8 {
            if let cause = directCause(of: ns) { return cause }
            current = ns.userInfo[NSUnderlyingErrorKey] as? NSError
            depth += 1
        }
        return nil
    }

    private static func directCause(of ns: NSError) -> Cause? {
        switch ns.domain {
        case NSCocoaErrorDomain:
            switch ns.code {
            case NSFileNoSuchFileError, NSFileReadNoSuchFileError: return .notFound
            case NSFileReadNoPermissionError: return .permissionDenied(isWrite: false)
            case NSFileWriteNoPermissionError: return .permissionDenied(isWrite: true)
            case NSFileWriteOutOfSpaceError: return .noSpace
            case NSFileWriteVolumeReadOnlyError: return .readOnlyVolume
            default: return nil
            }
        case NSPOSIXErrorDomain:
            switch Int32(truncatingIfNeeded: ns.code) {
            case ENOENT: return .notFound
            case EACCES, EPERM: return .permissionDenied(isWrite: nil)
            case ENOSPC, EDQUOT: return .noSpace
            case EROFS: return .readOnlyVolume
            case EIO, ENXIO, ENODEV, ESTALE: return .volumeUnavailable
            case ENETDOWN, ENETUNREACH: return .offline
            case ETIMEDOUT, ECONNRESET, ECONNABORTED, ENETRESET: return .connectionInterrupted
            case EHOSTUNREACH, EHOSTDOWN, ECONNREFUSED: return .serverUnreachable
            default: return nil
            }
        case NSURLErrorDomain:
            switch ns.code {
            case NSURLErrorNotConnectedToInternet, NSURLErrorDataNotAllowed,
                 NSURLErrorInternationalRoamingOff, NSURLErrorCallIsActive:
                return .offline
            case NSURLErrorNetworkConnectionLost, NSURLErrorTimedOut:
                return .connectionInterrupted
            case NSURLErrorCannotFindHost, NSURLErrorCannotConnectToHost, NSURLErrorDNSLookupFailed,
                 NSURLErrorSecureConnectionFailed, NSURLErrorServerCertificateUntrusted,
                 NSURLErrorServerCertificateHasBadDate, NSURLErrorServerCertificateHasUnknownRoot,
                 NSURLErrorServerCertificateNotYetValid, NSURLErrorAppTransportSecurityRequiresSecureConnection:
                return .serverUnreachable
            case NSURLErrorFileDoesNotExist: return .notFound
            case NSURLErrorNoPermissionsToReadFile: return .permissionDenied(isWrite: false)
            default: return nil
            }
        default:
            return nil
        }
    }

    /// エラーに含まれるファイル名（NSFilePathErrorKey / NSURLErrorKey）
    private static func fileName(in ns: NSError) -> String? {
        if let path = ns.userInfo[NSFilePathErrorKey] as? String, !path.isEmpty {
            return (path as NSString).lastPathComponent
        }
        if let url = ns.userInfo[NSURLErrorKey] as? URL, url.isFileURL {
            return url.lastPathComponent
        }
        return nil
    }
}

extension ErrorMapper.Operation {
    var fileName: String? {
        switch self {
        case .reading(let name), .writing(let name): name
        case .downloading, .unspecified: nil
        }
    }
}
