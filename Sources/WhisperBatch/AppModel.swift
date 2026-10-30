import AppKit
import Foundation
import Observation
import WhisperBatchCore
import WhisperBatchEngine

/// 表示言語の設定（文字起こしの言語とは別）
enum LanguagePreference: String, CaseIterable, Identifiable {
    /// macOS の優先言語に合わせる
    case system
    case japanese = "ja"
    case english = "en"

    var id: String { rawValue }

    /// macOS 全体の優先言語。このアプリ用の言語指定（AppleLanguages）の影響を受けないよう、全体の設定を直接読む。
    static var systemLanguages: [String] {
        let value = CFPreferencesCopyValue("AppleLanguages" as CFString, kCFPreferencesAnyApplication,
                                           kCFPreferencesCurrentUser, kCFPreferencesAnyHost)
        return (value as? [String]) ?? Locale.preferredLanguages
    }

    var resolved: AppLanguage {
        switch self {
        case .system: AppLanguage.preferred(from: Self.systemLanguages)
        case .japanese: .japanese
        case .english: .english
        }
    }
}

/// 画面全体の状態。状態の更新はすべてメインアクターで行う。
@MainActor
@Observable
final class AppModel {
    // MARK: キュー

    private(set) var queue = TranscriptionQueue()
    var selection: Set<UUID> = []

    // MARK: 設定（UserDefaults に保存）

    /// 文字起こしの言語
    var language: LanguageOption {
        didSet {
            UserDefaults.standard.set(language.id, forKey: Keys.language)
            models.transcriptionLanguage = language
        }
    }

    var selectedModelID: String {
        didSet {
            UserDefaults.standard.set(selectedModelID, forKey: Keys.model)
            // 方式を切り替えて戻ったときに、前に選んでいたモデルに戻せるようにする
            UserDefaults.standard.set(selectedModelID, forKey: Keys.model(for: selectedModel.engine))
        }
    }

    /// 表示言語の設定。変えると画面の文字列がすぐに切り替わる。
    var languagePreference: LanguagePreference {
        didSet { applyLanguagePreference() }
    }

    /// ファイルを追加したらすぐに文字起こしを始める（既定はオフ）
    var autoStartOnAdd: Bool {
        didSet { UserDefaults.standard.set(autoStartOnAdd, forKey: Keys.autoStartOnAdd) }
    }

    /// 現在の表示言語（画面はこの値の変化で作り直す）
    private(set) var appLanguage: AppLanguage

    var selectedModel: SpeechModel {
        ModelCatalog.model(id: selectedModelID) ?? ModelCatalog.defaultModel
    }

    var selectedEngine: SpeechEngine { selectedModel.engine }

    /// 実際に使う文字起こしの言語（ReazonSpeech は日本語専用）
    var effectiveLanguage: LanguageOption {
        selectedEngine.supports(language) ? language : (selectedEngine.languageOptions.first ?? language)
    }

    /// 方式を切り替える。前にその方式で選んでいたモデル、なければ取得済みのモデル、なければ既定のモデルにする。
    func selectEngine(_ engine: SpeechEngine) {
        guard engine != selectedEngine, !isRunning else { return }
        let saved = UserDefaults.standard.string(forKey: Keys.model(for: engine)).flatMap(ModelCatalog.model(id:))
        let model = saved.flatMap { $0.engine == engine ? $0 : nil }
            ?? models.installedModels(for: engine).first(where: \.isDefaultForEngine)
            ?? models.installedModels(for: engine).first
            ?? ModelCatalog.defaultModel(for: engine)
        selectedModelID = model.id
        // Apple の音声認識は言語の自動判定がないので、自動判定なら表示言語に近い言語にする
        if engine == .apple, language == .auto {
            language = appLanguage == .english ? .english : .japanese
        }
    }

    let models = ModelManager()
    /// 書体とテーマ
    let appearance = AppearanceSettings()

    // MARK: 処理状態

    private(set) var isRunning = false
    private(set) var isLoadingModel = false
    private(set) var isAddingFiles = false
    private(set) var lastOutcome: BatchOutcome?
    /// 追加時にスキップしたファイルの案内
    var addNotice: String?
    /// 直近の保存結果の案内
    var lastSaveMessage: String?
    /// ファイルがウィンドウの上にドラッグされている（サイドバーか右側の列のどちらか）
    var isDropTargeted: Bool { dropOverSidebar || dropOverDetail }
    private var dropOverSidebar = false
    private var dropOverDetail = false
    /// MP3 として保存中の項目と進捗
    private(set) var mp3Exports: [UUID: Double] = [:]

    // MARK: 画面の表示状態

    var showFileImporter = false
    var showCombineSheet = false
    var showModelManager = false
    var downloadConfirmation: SpeechModel?
    var alert: AlertMessage?

    struct AlertMessage: Identifiable {
        let id = UUID()
        var title: String
        var message: String
    }

    @ObservationIgnored private var runTask: Task<Void, Never>?
    @ObservationIgnored private var probeQueue: [UUID] = []
    @ObservationIgnored private var probeTask: Task<Void, Never>?
    @ObservationIgnored private var mp3ExportTasks: [UUID: Task<Void, Never>] = [:]
    /// 追加直後の確認で読み込めないと分かった項目（次の一括処理の結果に失敗として含める）
    @ObservationIgnored private var probeFailedIDs: Set<UUID> = []

    private enum Keys {
        static let language = "language"
        static let model = "modelID"
        static func model(for engine: SpeechEngine) -> String { "modelID.\(engine.rawValue)" }
        static let appLanguage = "appLanguage"
        static let autoStartOnAdd = "autoStartOnAdd"
        /// AppKit が読む、このアプリだけの言語指定（標準メニューやパネルの言語。次回起動時に反映）
        static let appleLanguages = "AppleLanguages"
    }

    init() {
        let defaults = UserDefaults.standard
        language = LanguageOption(storageValue: defaults.string(forKey: Keys.language))
        let savedModel = defaults.string(forKey: Keys.model)
        selectedModelID = savedModel.flatMap { ModelCatalog.model(id: $0)?.id } ?? ModelCatalog.defaultModelID
        let preference = defaults.string(forKey: Keys.appLanguage).flatMap(LanguagePreference.init(rawValue:)) ?? .system
        languagePreference = preference
        autoStartOnAdd = defaults.bool(forKey: Keys.autoStartOnAdd)
        appLanguage = preference.resolved
        Localizer.shared.setLanguage(appLanguage)
        models.transcriptionLanguage = language
        // 前回の異常終了などで残った変換用の一時ファイルを消す
        ConversionWorkspace.removeLeftovers()
    }

    private func applyLanguagePreference() {
        let defaults = UserDefaults.standard
        defaults.set(languagePreference.rawValue, forKey: Keys.appLanguage)
        if languagePreference == .system {
            defaults.removeObject(forKey: Keys.appleLanguages)
        } else {
            defaults.set([languagePreference.rawValue], forKey: Keys.appleLanguages)
        }
        let resolved = languagePreference.resolved
        Localizer.shared.setLanguage(resolved)
        // 文字列として保持している案内は前の言語のままなので消す
        addNotice = nil
        lastSaveMessage = nil
        appLanguage = resolved
    }

    func setDropTargeted(_ targeted: Bool, inSidebar: Bool) {
        if inSidebar { dropOverSidebar = targeted } else { dropOverDetail = targeted }
    }

    // MARK: 参照

    var selectedItems: [QueueItem] {
        queue.items.filter { selection.contains($0.id) }
    }

    var singleSelectedItem: QueueItem? {
        selection.count == 1 ? queue.item(selection.first!) : nil
    }

    var canStart: Bool { startBlockReason == nil }

    /// 開始できない理由（開始できるなら nil）
    var startBlockReason: String? {
        if isRunning { return L("start.block.running") }
        if queue.items.isEmpty { return L("start.block.empty") }
        if !queue.hasPending { return L("start.block.noPending") }
        if selectedEngine == .apple, language == .auto { return L("start.block.appleNeedsLanguage") }
        switch models.state(for: selectedModel) {
        case .installed: return nil
        case .unsupported: return L("start.block.languageUnsupported", effectiveLanguage.displayName)
        default: return selectedModel.isDownloadable ? L("start.block.noModel") : L("start.block.noAssets")
        }
    }

    var hasCompletedItems: Bool { !queue.completedItems.isEmpty }

    // MARK: 追加・削除

    func addFiles(_ urls: [URL]) {
        guard !urls.isEmpty else { return }
        isAddingFiles = true
        Task {
            let candidates = await SecurityScopedAccess.prepareCandidates(urls)
            isAddingFiles = false
            let report = queue.add(candidates)
            addNotice = report.skippedSummary
            if candidates.isEmpty {
                addNotice = L("add.noneFound")
            }
            if !report.added.isEmpty {
                lastOutcome = nil
                if selection.isEmpty { selection = [report.added[0]] }
            } else if !report.existingIDsForDuplicates.isEmpty {
                // 重複だけだった場合は、既にある項目を選択して場所を示す
                selection = Set(report.existingIDsForDuplicates)
            }
            enqueueProbe(report.added)
            if autoStartOnAdd, !report.added.isEmpty { startAutomatically() }
        }
    }

    /// 追加と同時に開始する設定のとき。処理中なら追加分も今の処理の続きで扱われる。
    private func startAutomatically() {
        guard !isRunning else { return }
        if canStart {
            start()
        } else if let reason = startBlockReason {
            let notice = L("autoStart.blocked", reason)
            addNotice = addNotice.map { $0 + "\n" + notice } ?? notice
        }
    }

    func removeSelected() {
        let removed = queue.remove(selection)
        if removed < selection.count, selection.contains(where: { queue.item($0)?.status.isProcessing == true }) {
            addNotice = L("remove.processingBlocked")
        }
        selection = selection.filter { queue.item($0) != nil }
    }

    func removeAllPending() {
        queue.removeAllPending()
        selection = selection.filter { queue.item($0) != nil }
    }

    func removeAll() {
        queue.removeAllRemovable()
        selection = selection.filter { queue.item($0) != nil }
        if queue.items.isEmpty { lastOutcome = nil; addNotice = nil }
    }

    func retry(_ ids: Set<UUID>) {
        probeFailedIDs.subtract(ids)
        if queue.resetForRetry(ids) > 0 { lastOutcome = nil }
    }

    func revealInFinder(_ ids: Set<UUID>) {
        let urls = queue.items.filter { ids.contains($0.id) }.map(\.url)
        NSWorkspace.shared.activateFileViewerSelecting(urls)
    }

    // MARK: 書き出し結果

    func present(_ outcome: ExportService.Outcome) {
        switch outcome {
        case .saved(let message): lastSaveMessage = message
        case .cancelled: break
        case .failed(let message): alert = AlertMessage(title: L("export.failed.title"), message: message)
        }
    }

    // MARK: 長さと種類の確認（読み込めない形式を早めに知らせる）

    private func enqueueProbe(_ ids: [UUID]) {
        probeQueue += ids
        guard probeTask == nil else { return }
        probeTask = Task {
            while !probeQueue.isEmpty {
                let id = probeQueue.removeFirst()
                guard let item = queue.item(id), item.status == .pending else { continue }
                do {
                    let info = try await SecurityScopedAccess.withAccess(url: item.url, bookmark: item.bookmark) { url in
                        try await MediaInspector.inspect(url)
                    }
                    queue.setDuration(id, info.duration > 0 ? info.duration : nil)
                    queue.setMediaKind(id, info.kind)
                } catch {
                    // 処理が既に始まっていればそちらの結果に任せる
                    if queue.item(id)?.status == .pending {
                        try? queue.markFailed(id, failure: ItemFailure(error))
                        probeFailedIDs.insert(id)
                    }
                }
            }
            probeTask = nil
        }
    }

    // MARK: 一括処理

    func start() {
        guard canStart else { return }
        let model = selectedModel
        lastOutcome = nil
        lastSaveMessage = nil
        isRunning = true
        runTask = Task { await runBatch(model: model, language: effectiveLanguage) }
    }

    func cancel() {
        runTask?.cancel()
    }

    /// 処理中なら中止して、モデルの解放と一時ファイルの削除が終わるまで待つ（アプリ終了時に使う）
    func shutdown() async {
        models.cancelAllDownloads()
        runTask?.cancel()
        mp3ExportTasks.values.forEach { $0.cancel() }
        await runTask?.value
        for task in mp3ExportTasks.values { await task.value }
        ConversionWorkspace.removeLeftovers()
    }

    var hasActiveWork: Bool { isRunning || !mp3ExportTasks.isEmpty }

    private func runBatch(model: SpeechModel, language: LanguageOption) async {
        defer {
            isRunning = false
            isLoadingModel = false
            runTask = nil
        }

        isLoadingModel = true
        let context: any Transcriber
        do {
            context = try await TranscriberLoader.load(model, storage: models.storage, language: language)
        } catch {
            lastOutcome = .couldNotStart(ItemFailure(error))
            return
        }
        isLoadingModel = false

        let options = TranscriptionOptions(language: language)
        var succeeded = 0
        var failed = 0
        var cancelled = false

        while let id = queue.nextPendingID {
            if Task.isCancelled { cancelled = true; break }
            guard let item = queue.item(id) else { continue }
            do { try queue.markProcessing(id, phase: item.isVideo ? .converting : .transcribing) } catch { continue }
            do {
                let transcript = try await process(item, context: context, options: options)
                try queue.markCompleted(id, transcript: transcript)
                succeeded += 1
            } catch is CancellationError {
                try? queue.markCancelled(id)
                cancelled = true
                break
            } catch {
                // 1 件の失敗では止めず、次のファイルへ進む
                try? queue.markFailed(id, failure: ItemFailure(error))
                failed += 1
            }
        }
        // 追加時点で読み込めなかった項目も、この回の失敗として数える
        failed += probeFailedIDs.filter { queue.item($0)?.status.isFailed == true }.count
        probeFailedIDs.removeAll()
        if succeeded + failed > 0 || cancelled {
            lastOutcome = .make(succeeded: succeeded, failed: failed, wasCancelled: cancelled, remaining: queue.counts.pending)
        }
        // context はここでスコープを抜けて解放される
    }

    /// 1 件分の処理。動画は音声を一時 MP3 に変換してから文字起こしし、一時ファイルは必ず削除する。
    private func process(_ item: QueueItem, context: any Transcriber, options: TranscriptionOptions) async throws -> Transcript {
        let id = item.id
        return try await SecurityScopedAccess.withAccess(url: item.url, bookmark: item.bookmark) { url in
            // 追加直後の確認が終わっていない場合もあるので、ここで種類を確かめる
            let info = try await MediaInspector.inspect(url)
            queue.setMediaKind(id, info.kind)
            if info.duration > 0 { queue.setDuration(id, info.duration) }

            guard info.kind == .video else {
                try queue.setPhase(id, .transcribing)
                return try await transcribe(url, id: id, context: context, options: options)
            }

            try queue.setPhase(id, .converting)
            let mp3 = try ConversionWorkspace.makeTemporaryMP3URL()
            defer { ConversionWorkspace.remove(mp3) }
            try await MP3Converter.convert(source: url, destination: mp3) { p in
                Task { @MainActor in self.queue.updateProgress(id, phase: .converting, p) }
            }
            try queue.setPhase(id, .transcribing)
            return try await transcribe(mp3, id: id, context: context, options: options)
        }
    }

    private func transcribe(_ url: URL, id: UUID, context: any Transcriber, options: TranscriptionOptions) async throws -> Transcript {
        try await context.transcribe(fileURL: url, options: options) { p in
            Task { @MainActor in self.queue.updateProgress(id, phase: .transcribing, p) }
        }
    }

    // MARK: 動画の音声を MP3 で保存（利用者が明示的に選んだときだけ）

    func saveMP3(for id: UUID) {
        guard let item = queue.item(id), mp3ExportTasks[id] == nil else { return }
        Task {
            guard let destination = await ExportService.chooseMP3Destination(for: item) else { return }
            mp3Exports[id] = 0
            mp3ExportTasks[id] = Task {
                defer {
                    mp3Exports[id] = nil
                    mp3ExportTasks[id] = nil
                }
                var temp: URL?
                do {
                    let tempURL = try ConversionWorkspace.makeTemporaryMP3URL()
                    temp = tempURL
                    try await SecurityScopedAccess.withAccess(url: item.url, bookmark: item.bookmark) { url in
                        try await MP3Converter.convert(source: url, destination: tempURL) { p in
                            Task { @MainActor in
                                if let current = self.mp3Exports[id], p > current { self.mp3Exports[id] = p }
                            }
                        }
                    }
                    try ExportService.moveIntoPlace(tempURL, to: destination)
                    temp = nil
                    lastSaveMessage = L("mp3.saved", destination.lastPathComponent)
                } catch is CancellationError {
                    // 中止した場合は何も残さない
                } catch {
                    alert = AlertMessage(title: L("mp3.failed.title"), message: userFacingMessage(for: error))
                }
                if let temp { ConversionWorkspace.remove(temp) }
            }
        }
    }

    func cancelMP3Export(for id: UUID) {
        mp3ExportTasks[id]?.cancel()
    }
}
