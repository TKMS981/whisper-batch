import AppKit
import Foundation
import Observation
import Synchronization
import WhisperBatchCore
import WhisperBatchEngine

/// モデルの取得状況・ダウンロード・削除を管理する
@MainActor
@Observable
final class ModelManager {
    enum State: Equatable {
        case notInstalled
        case downloading(received: Int64, total: Int64)
        case verifying
        case installed
        case failed(message: String, canResume: Bool)

        var isBusy: Bool {
            switch self {
            case .downloading, .verifying: true
            default: false
            }
        }
    }

    private(set) var states: [String: State] = [:]
    /// 保存先を用意できなかった場合のエラー
    private(set) var storageError: String?

    let storage: ModelStorage?
    @ObservationIgnored private var tasks: [String: Task<Void, Never>] = [:]
    @ObservationIgnored private var resumeData: [String: Data] = [:]

    init() {
        do {
            storage = try ModelStorage.defaultStorage()
        } catch {
            storage = nil
            storageError = L("models.storageError", error.localizedDescription)
        }
        refresh()
    }

    var storagePath: String { storage?.directory.path ?? L("models.storageUnavailable") }

    func state(for model: WhisperModel) -> State {
        states[model.id] ?? .notInstalled
    }

    func isInstalled(_ model: WhisperModel) -> Bool {
        state(for: model) == .installed
    }

    func fileURL(for model: WhisperModel) -> URL? {
        storage?.fileURL(for: model)
    }

    var installedModels: [WhisperModel] {
        ModelCatalog.all.filter { isInstalled($0) }
    }

    /// ディスク上の状態を読み直す（ダウンロード中・失敗表示中のものはそのまま）
    func refresh() {
        for model in ModelCatalog.all {
            let current = states[model.id]
            if current?.isBusy == true { continue }
            if storage?.isInstalled(model) == true {
                states[model.id] = .installed
            } else if case .failed = current {
                continue
            } else {
                states[model.id] = .notInstalled
            }
        }
    }

    func download(_ model: WhisperModel) {
        guard let storage, !state(for: model).isBusy else { return }
        states[model.id] = .downloading(received: 0, total: model.sizeBytes)
        let resume = resumeData.removeValue(forKey: model.id)
        let throttle = ProgressThrottle()
        let id = model.id
        tasks[id] = Task { [weak self] in
            do throws(ModelDownloadInterruption) {
                try await ModelDownloader.download(
                    model, into: storage, resumeData: resume,
                    progress: { received, total in
                        guard throttle.shouldReport(received: received, total: total) else { return }
                        Task { @MainActor in
                            guard let self, case .downloading = self.states[id] else { return }
                            self.states[id] = .downloading(received: received, total: total)
                        }
                    },
                    verifying: {
                        Task { @MainActor in self?.states[id] = .verifying }
                    })
                self?.finish(id, state: .installed)
            } catch {
                if error.isCancelled {
                    self?.finish(id, state: .notInstalled)
                } else {
                    if let data = error.resumeData { self?.resumeData[id] = data }
                    self?.finish(id, state: .failed(message: error.errorDescription ?? L("error.detail.unknown"),
                                                    canResume: error.resumeData != nil))
                }
            }
        }
    }

    func cancelDownload(_ model: WhisperModel) {
        tasks[model.id]?.cancel()
    }

    func cancelAllDownloads() {
        tasks.values.forEach { $0.cancel() }
    }

    var hasActiveDownloads: Bool { !tasks.isEmpty }

    func delete(_ model: WhisperModel) throws {
        guard let storage else { return }
        cancelDownload(model)
        resumeData[model.id] = nil
        try storage.delete(model)
        states[model.id] = .notInstalled
    }

    func revealInFinder() {
        guard let storage else { return }
        try? storage.ensureDirectory()
        NSWorkspace.shared.activateFileViewerSelecting([storage.directory])
    }

    private func finish(_ id: String, state: State) {
        tasks[id] = nil
        states[id] = state
    }
}

/// ダウンロード進捗の通知を 0.25% 刻みに間引く（UI 更新の回数を抑える）
private final class ProgressThrottle: Sendable {
    private let last = Mutex<Int>(-1)

    func shouldReport(received: Int64, total: Int64) -> Bool {
        guard total > 0 else { return true }
        let step = Int(received * 400 / total)
        return last.withLock { value in
            guard step != value else { return false }
            value = step
            return true
        }
    }
}
