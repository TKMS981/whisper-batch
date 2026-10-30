import Foundation
import Synchronization
import WhisperBatchCore

/// ダウンロードが完了しなかったときのエラー。通信エラーの場合は再開用データを持つことがある。
public struct ModelDownloadInterruption: Error, Sendable {
    public enum Reason: Sendable {
        case cancelled
        case failed(WhisperBatchError)
    }

    public let reason: Reason
    public let resumeData: Data?

    public var isCancelled: Bool {
        if case .cancelled = reason { return true }
        return false
    }
}

extension ModelDownloadInterruption: LocalizedError {
    public var errorDescription: String? {
        switch reason {
        case .cancelled: L("download.cancelled")
        case .failed(let error): error.errorDescription
        }
    }
}

/// Hugging Face からモデルを取得し、ファイルごとに SHA-256 を検証してから保存する。
/// 送信するのはファイルの取得要求だけで、音声や認識結果は送らない。
public enum ModelDownloader {
    /// 複数のファイルからなるモデルは 1 ファイルずつ取得する。取得・検証済みのファイルは取得し直さない。
    /// - Parameters:
    ///   - resumeData: 前回失敗したときの再開用データ（あれば、まだ揃っていない最初のファイルを途中から再開）
    ///   - progress: (受信済みバイト, 全体バイト)。モデル全体での値。任意のスレッドから呼ばれる。
    ///   - verifying: ファイルの検証を始めるときに呼ばれる
    @concurrent
    public static func download(
        _ model: SpeechModel,
        into storage: ModelStorage,
        resumeData: Data?,
        progress: @escaping @Sendable (Int64, Int64) -> Void,
        verifying: @escaping @Sendable () -> Void
    ) async throws(ModelDownloadInterruption) {
        do {
            try FileManager.default.createDirectory(at: storage.directory(for: model), withIntermediateDirectories: true)
        } catch {
            throw .init(reason: .failed(.downloadFailed(detail: L("download.detail.cannotCreateFolder", error.localizedDescription))), resumeData: nil)
        }
        let remaining = model.files.filter { !storage.isInstalled($0, of: model) }
        let remainingBytes = remaining.reduce(Int64(0)) { $0 + $1.sizeBytes }
        let required = remainingBytes + max(remainingBytes / 10, 50_000_000)
        if let available = storage.availableCapacity(), available < required {
            throw .init(reason: .failed(.insufficientDiskSpace(required: required, available: available)), resumeData: resumeData)
        }

        let total = model.sizeBytes
        var done = total - remainingBytes
        var resume = resumeData
        for file in remaining {
            let offset = done
            try await downloadFile(file, of: model, into: storage, resumeData: resume,
                                   progress: { received, _ in progress(offset + received, total) },
                                   verifying: verifying)
            resume = nil
            done += file.sizeBytes
            progress(done, total)
        }
    }

    private static func downloadFile(
        _ file: ModelFile,
        of model: SpeechModel,
        into storage: ModelStorage,
        resumeData: Data?,
        progress: @escaping @Sendable (Int64, Int64) -> Void,
        verifying: @escaping @Sendable () -> Void
    ) async throws(ModelDownloadInterruption) {
        let partial = storage.partialURL(for: file, of: model)
        let delegate = DownloadDelegate(destination: partial, expectedSize: file.sizeBytes, progress: progress)
        let config = URLSessionConfiguration.default
        config.urlCache = nil
        config.timeoutIntervalForRequest = 60
        let session = URLSession(configuration: config, delegate: delegate, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }

        do {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                    delegate.setContinuation(continuation)
                    let task = resumeData.map { session.downloadTask(withResumeData: $0) }
                        ?? session.downloadTask(with: file.url)
                    delegate.setTask(task)
                    task.resume()
                }
            } onCancel: {
                delegate.cancel()
            }
        } catch let e as ModelDownloadInterruption {
            throw e
        } catch {
            throw .init(reason: .cancelled, resumeData: nil)
        }

        // 内容の検証
        verifying()
        let digest: String
        do {
            digest = try await ModelStorage.sha256(of: partial)
        } catch is CancellationError {
            try? FileManager.default.removeItem(at: partial)
            throw .init(reason: .cancelled, resumeData: nil)
        } catch {
            try? FileManager.default.removeItem(at: partial)
            throw .init(reason: .failed(.downloadFailed(detail: L("download.detail.cannotVerify"))), resumeData: nil)
        }
        guard digest == file.sha256 else {
            try? FileManager.default.removeItem(at: partial)
            throw .init(reason: .failed(.checksumMismatch(modelName: model.displayName)), resumeData: nil)
        }

        let final = storage.fileURL(for: file, of: model)
        do {
            if FileManager.default.fileExists(atPath: final.path) {
                try FileManager.default.removeItem(at: final)
            }
            try FileManager.default.moveItem(at: partial, to: final)
        } catch {
            throw .init(reason: .failed(.downloadFailed(detail: L("download.detail.cannotSave", error.localizedDescription))), resumeData: nil)
        }
    }
}

private final class DownloadDelegate: NSObject, URLSessionDownloadDelegate, Sendable {
    private struct State {
        var continuation: CheckedContinuation<Void, any Error>?
        var task: URLSessionDownloadTask?
        var cancelled = false
        var finishError: WhisperBatchError?
    }

    private let destination: URL
    private let expectedSize: Int64
    private let progress: @Sendable (Int64, Int64) -> Void
    private let state = Mutex(State())

    init(destination: URL, expectedSize: Int64, progress: @escaping @Sendable (Int64, Int64) -> Void) {
        self.destination = destination
        self.expectedSize = expectedSize
        self.progress = progress
    }

    func setContinuation(_ c: CheckedContinuation<Void, any Error>) {
        state.withLock { $0.continuation = c }
    }

    func setTask(_ task: URLSessionDownloadTask) {
        let alreadyCancelled = state.withLock { s in
            s.task = task
            return s.cancelled
        }
        if alreadyCancelled { task.cancel() }
    }

    func cancel() {
        let task = state.withLock { s in
            s.cancelled = true
            return s.task
        }
        task?.cancel()
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        let total = totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : expectedSize
        progress(totalBytesWritten, total)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        // 一時ファイルはこのメソッドから戻ると消えるので、ここで移動する
        var failure: WhisperBatchError?
        if let http = downloadTask.response as? HTTPURLResponse, !(200 ..< 300).contains(http.statusCode) {
            failure = .downloadHTTPError(statusCode: http.statusCode)
        } else {
            do {
                if FileManager.default.fileExists(atPath: destination.path) {
                    try FileManager.default.removeItem(at: destination)
                }
                try FileManager.default.moveItem(at: location, to: destination)
            } catch {
                failure = .downloadFailed(detail: L("download.detail.cannotMoveTemp", error.localizedDescription))
            }
        }
        if let failure { state.withLock { $0.finishError = failure } }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: (any Error)?) {
        let (continuation, cancelled, finishError) = state.withLock { s in
            let c = s.continuation
            s.continuation = nil
            return (c, s.cancelled, s.finishError)
        }
        guard let continuation else { return }
        if cancelled {
            continuation.resume(throwing: ModelDownloadInterruption(reason: .cancelled, resumeData: nil))
            return
        }
        if let error {
            let ns = error as NSError
            let resume = ns.userInfo[NSURLSessionDownloadTaskResumeData] as? Data
            let detail = ns.localizedDescription
            continuation.resume(throwing: ModelDownloadInterruption(reason: .failed(.downloadFailed(detail: detail)), resumeData: resume))
            return
        }
        if let finishError {
            continuation.resume(throwing: ModelDownloadInterruption(reason: .failed(finishError), resumeData: nil))
            return
        }
        continuation.resume()
    }
}
