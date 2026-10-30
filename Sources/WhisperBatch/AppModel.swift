import AppKit
import Foundation
import Observation
import WhisperBatchCore
import WhisperBatchEngine

/// 画面全体の状態。状態の更新はすべてメインアクターで行う。
///
/// - 設定の保存は `SettingsStore`、キューと一括処理は `TranscriptionService` が受け持つ。
///   ここでは選択・案内・シート表示などの画面の状態と、設定変更に伴う画面側の処理を扱う。
@MainActor
@Observable
final class AppModel {
    // MARK: キュー

    var queue: TranscriptionQueue { transcription.queue }
    var selection: Set<UUID> = []

    // MARK: 設定（保存は SettingsStore）

    /// 文字起こしの言語
    var language: LanguageOption {
        get { settings.language }
        set {
            settings.language = newValue
            models.transcriptionLanguage = newValue
        }
    }

    var selectedModelID: String {
        get { settings.selectedModelID }
        set { settings.selectedModelID = newValue }
    }

    /// 表示言語の設定。変えると画面の文字列がすぐに切り替わる。
    var languagePreference: LanguagePreference {
        get { settings.languagePreference }
        set {
            settings.languagePreference = newValue
            applyLanguagePreference()
        }
    }

    /// ファイルを追加したらすぐに文字起こしを始める（既定はオフ）
    var autoStartOnAdd: Bool {
        get { settings.autoStartOnAdd }
        set { settings.autoStartOnAdd = newValue }
    }

    /// Whisper の探索方式（速度優先／精度優先）
    var whisperDecoding: WhisperDecoding {
        get { settings.whisperDecoding }
        set { settings.whisperDecoding = newValue }
    }

    /// 現在の表示言語（画面はこの値の変化で作り直す）
    private(set) var appLanguage: AppLanguage

    var selectedModel: SpeechModel { settings.selectedModel }

    var selectedEngine: SpeechEngine { selectedModel.engine }

    /// 実際に使う文字起こしの言語（ReazonSpeech は日本語専用）
    var effectiveLanguage: LanguageOption {
        selectedEngine.supports(language) ? language : (selectedEngine.languageOptions.first ?? language)
    }

    /// 方式を切り替える。前にその方式で選んでいたモデル、なければ取得済みのモデル、なければ既定のモデルにする。
    func selectEngine(_ engine: SpeechEngine) {
        guard engine != selectedEngine, !isRunning else { return }
        let model = settings.lastSelectedModel(for: engine)
            ?? models.installedModels(for: engine).first(where: \.isDefaultForEngine)
            ?? models.installedModels(for: engine).first
            ?? ModelCatalog.defaultModel(for: engine)
        selectedModelID = model.id
        // Apple の音声認識は言語の自動判定がないので、自動判定なら表示言語に近い言語にする
        if engine == .apple, language == .auto {
            language = appLanguage == .english ? .english : .japanese
        }
    }

    let settings: SettingsStore
    let transcription: TranscriptionService
    let models: ModelManager
    /// 書体とテーマ
    let appearance = AppearanceSettings()

    // MARK: 処理状態

    var isRunning: Bool { transcription.isRunning }
    var isLoadingModel: Bool { transcription.isLoadingModel }
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

    @ObservationIgnored private var mp3ExportTasks: [UUID: Task<Void, Never>] = [:]

    init(settings: SettingsStore = SettingsStore()) {
        let models = ModelManager()
        self.settings = settings
        self.models = models
        transcription = TranscriptionService(storage: models.storage)
        appLanguage = settings.languagePreference.resolved
        Localizer.shared.setLanguage(appLanguage)
        models.transcriptionLanguage = settings.language
        // 前回の異常終了などで残った変換用の一時ファイルを消す
        ConversionWorkspace.removeLeftovers()
    }

    /// 表示言語を切り替える（保存は SettingsStore が行う）
    private func applyLanguagePreference() {
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
            let report = transcription.add(candidates)
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
        let removed = transcription.remove(selection)
        if removed < selection.count, selection.contains(where: { queue.item($0)?.status.isProcessing == true }) {
            addNotice = L("remove.processingBlocked")
        }
        selection = selection.filter { queue.item($0) != nil }
    }

    func removeAllPending() {
        transcription.removeAllPending()
        selection = selection.filter { queue.item($0) != nil }
    }

    func removeAll() {
        transcription.removeAllRemovable()
        selection = selection.filter { queue.item($0) != nil }
        if queue.items.isEmpty { lastOutcome = nil; addNotice = nil }
    }

    func retry(_ ids: Set<UUID>) {
        if transcription.retry(ids) > 0 { lastOutcome = nil }
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

    // MARK: 一括処理（処理そのものは TranscriptionService）

    func start() {
        guard canStart else { return }
        lastOutcome = nil
        lastSaveMessage = nil
        transcription.start(model: selectedModel, language: effectiveLanguage, decoding: whisperDecoding) { [weak self] outcome in
            if let outcome { self?.lastOutcome = outcome }
        }
    }

    func cancel() {
        transcription.cancel()
    }

    /// 処理中なら中止して、モデルの解放と一時ファイルの削除が終わるまで待つ（アプリ終了時に使う）
    func shutdown() async {
        models.cancelAllDownloads()
        transcription.cancel()
        mp3ExportTasks.values.forEach { $0.cancel() }
        await transcription.waitUntilFinished()
        for task in mp3ExportTasks.values { await task.value }
        ConversionWorkspace.removeLeftovers()
    }

    var hasActiveWork: Bool { isRunning || !mp3ExportTasks.isEmpty }

    // MARK: 動画の音声を MP3 で保存（利用者が明示的に選んだときだけ）

    func saveMP3(for id: UUID) {
        guard let item = queue.item(id), mp3ExportTasks[id] == nil else { return }
        Task {
            guard let destination = await ExportService.chooseMP3Destination(for: item) else { return }
            // 保存先を選んでいる間に、ブックマークが更新されていることがある
            let item = queue.item(id) ?? item
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
                    try await SecurityScopedAccess.withAccess(
                        url: item.url, bookmark: item.bookmark,
                        onRefresh: { transcription.applyRefreshedBookmark($0, to: id) }
                    ) { url in
                        // 一時フォルダと保存先の両方に空きがあるかを、変換を始める前に確かめる
                        try await MP3Converter.convert(source: url, destination: tempURL,
                                                       finalFolders: [destination.deletingLastPathComponent()]) { p in
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
                    alert = AlertMessage(title: L("mp3.failed.title"),
                                         message: userFacingMessage(for: error, during: .writing(fileName: destination.lastPathComponent)))
                }
                if let temp { ConversionWorkspace.remove(temp) }
            }
        }
    }

    func cancelMP3Export(for id: UUID) {
        mp3ExportTasks[id]?.cancel()
    }
}
