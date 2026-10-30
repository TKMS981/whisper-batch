import AppKit
import Foundation
import Observation
import Synchronization
import WhisperBatchCore
import WhisperBatchEngine

/// モデルの取得状況・ダウンロード・削除を管理する。
///
/// Whisper と ReazonSpeech はファイルをダウンロードしてこのアプリの保存先に置く。
/// Apple の音声認識は言語ごとのデータを OS が管理するため、文字起こしの言語ごとに状態を問い合わせる。
@MainActor
@Observable
final class ModelManager {
    enum State: Equatable {
        case notInstalled
        case downloading(received: Int64, total: Int64)
        /// Apple の言語データを取得中（0...1）
        case installingAssets(fraction: Double)
        case verifying
        case installed
        case failed(message: String, canResume: Bool)
        /// 選んでいる文字起こしの言語に対応していない（Apple）
        case unsupported

        var isBusy: Bool {
            switch self {
            case .downloading, .installingAssets, .verifying: true
            default: false
            }
        }
    }

    /// Apple の言語データの状態を調べる対象の言語（AppModel が文字起こしの言語に合わせて設定する）
    var transcriptionLanguage: LanguageOption = .japanese {
        didSet { if oldValue != transcriptionLanguage { refreshAppleStates() } }
    }

    private(set) var states: [String: State] = [:]
    /// 保存先を用意できなかった場合のエラー
    private(set) var storageError: String?

    let storage: ModelStorage?
    @ObservationIgnored private var tasks: [String: Task<Void, Never>] = [:]
    @ObservationIgnored private var resumeData: [String: Data] = [:]
    @ObservationIgnored private var appleRefreshTask: Task<Void, Never>?

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

    func state(for model: SpeechModel) -> State {
        states[model.id] ?? .notInstalled
    }

    func isInstalled(_ model: SpeechModel) -> Bool {
        state(for: model) == .installed
    }

    func installedModels(for engine: SpeechEngine) -> [SpeechModel] {
        ModelCatalog.models(for: engine).filter { isInstalled($0) }
    }

    /// ディスク上の状態を読み直す（ダウンロード中・失敗表示中のものはそのまま）
    func refresh() {
        refreshAppleStates()
        for model in ModelCatalog.all where model.isDownloadable {
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

    /// Apple の言語データの状態を OS に問い合わせ直す
    func refreshAppleStates() {
        let language = transcriptionLanguage
        appleRefreshTask?.cancel()
        appleRefreshTask = Task { [weak self] in
            for model in ModelCatalog.models(for: .apple) {
                let status = await AppleSpeechAssets.status(for: model, language: language)
                guard let self, !Task.isCancelled, language == self.transcriptionLanguage else { return }
                if self.state(for: model).isBusy { continue }
                // 失敗の表示は、取得し直すまで残す
                if case .failed = self.state(for: model), status == .notInstalled { continue }
                self.states[model.id] = switch status {
                case .installed: .installed
                case .notInstalled: .notInstalled
                case .downloading: .installingAssets(fraction: 0)
                case .unsupportedLanguage, .unavailable: .unsupported
                }
            }
        }
    }

    func download(_ model: SpeechModel) {
        guard !state(for: model).isBusy else { return }
        if model.engine == .apple {
            installAppleAssets(model)
            return
        }
        guard let storage else { return }
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
                            guard let self, let state = self.states[id] else { return }
                            switch state {
                            case .downloading, .verifying: break
                            default: return
                            }
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

    /// Apple の言語データを OS に取得させる（取得先は Apple のサーバー）
    private func installAppleAssets(_ model: SpeechModel) {
        let id = model.id
        let language = transcriptionLanguage
        states[id] = .installingAssets(fraction: 0)
        tasks[id] = Task { [weak self] in
            do {
                try await AppleSpeechAssets.install(model, language: language) { fraction in
                    Task { @MainActor in
                        guard let self, case .installingAssets = self.states[id] else { return }
                        self.states[id] = .installingAssets(fraction: fraction)
                    }
                }
                self?.finish(id, state: .installed)
                self?.refreshAppleStates()
            } catch is CancellationError {
                self?.finish(id, state: .notInstalled)
                self?.refreshAppleStates()
            } catch {
                self?.finish(id, state: .failed(message: userFacingMessage(for: error), canResume: false))
            }
        }
    }

    func cancelDownload(_ model: SpeechModel) {
        tasks[model.id]?.cancel()
    }

    func cancelAllDownloads() {
        tasks.values.forEach { $0.cancel() }
    }

    var hasActiveDownloads: Bool { !tasks.isEmpty }

    func delete(_ model: SpeechModel) throws {
        guard let storage, model.isDownloadable else { return }
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
