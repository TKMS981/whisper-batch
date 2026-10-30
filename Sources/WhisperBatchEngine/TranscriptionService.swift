import Foundation
import Observation
import WhisperBatchCore

/// 文字起こしキューと一括処理。
///
/// - 追加した項目は、すぐに長さと種類を確認する（読み込めない形式を早めに知らせる）。
/// - 一括処理では、モデルを 1 回読み込み、待機中の項目を順に処理する。1 件の失敗では止めない。
///
/// 状態の更新はすべてメインアクターで行う。認識そのものは各エンジンが専用のキューやスレッドで行う。
@MainActor
@Observable
public final class TranscriptionService {
    /// モデル（または OS の音声認識）を読み込む
    public typealias TranscriberLoading = @MainActor (SpeechModel, LanguageOption) async throws -> any Transcriber
    /// ファイルの長さと種類を調べる
    public typealias MediaInspecting = @Sendable (URL) async throws -> MediaInfo

    public private(set) var queue = TranscriptionQueue()
    public private(set) var isRunning = false
    public private(set) var isLoadingModel = false

    @ObservationIgnored private let loadTranscriber: TranscriberLoading
    @ObservationIgnored private let inspectMedia: MediaInspecting
    @ObservationIgnored private var runTask: Task<Void, Never>?
    @ObservationIgnored private var probeQueue: [UUID] = []
    @ObservationIgnored private var probeTask: Task<Void, Never>?
    /// 追加直後の確認で読み込めないと分かった項目（次の一括処理の結果に失敗として含める）
    @ObservationIgnored private var probeFailedIDs: Set<UUID> = []

    public init(loadTranscriber: @escaping TranscriberLoading,
                inspectMedia: @escaping MediaInspecting = { try await MediaInspector.inspect($0) }) {
        self.loadTranscriber = loadTranscriber
        self.inspectMedia = inspectMedia
    }

    /// アプリで使う構成（モデルは `storage` から読み込む）
    public convenience init(storage: ModelStorage?) {
        self.init(loadTranscriber: { model, language in
            try await TranscriberLoader.load(model, storage: storage, language: language)
        })
    }

    // MARK: キューの編集

    /// ファイルを追加し、追加した項目の確認を始める
    @discardableResult
    public func add(_ candidates: [FileCandidate]) -> AddReport {
        let report = queue.add(candidates)
        enqueueProbe(report.added)
        return report
    }

    /// 指定した項目を削除する（処理中の項目は残す）。削除した件数を返す。
    @discardableResult
    public func remove(_ ids: Set<UUID>) -> Int { queue.remove(ids) }

    @discardableResult
    public func removeAllPending() -> Int { queue.removeAllPending() }

    @discardableResult
    public func removeAllRemovable() -> Int { queue.removeAllRemovable() }

    /// 失敗・キャンセル・完了した項目を待機中に戻す。戻した件数を返す。
    @discardableResult
    public func retry(_ ids: Set<UUID>) -> Int {
        probeFailedIDs.subtract(ids)
        return queue.resetForRetry(ids)
    }

    // MARK: 一括処理

    /// 待機中の項目を順に文字起こしする。終わったとき（中止を含む）に結果を `onFinish` へ渡す。
    /// 何も処理しなかった場合の結果は nil。既に処理中なら何もしない。
    public func start(model: SpeechModel, language: LanguageOption,
                      onFinish: @escaping @MainActor (BatchOutcome?) -> Void = { _ in }) {
        guard !isRunning else { return }
        isRunning = true
        runTask = Task {
            let outcome = await runBatch(model: model, language: language)
            onFinish(outcome)
            isRunning = false
            isLoadingModel = false
            runTask = nil
        }
    }

    public func cancel() {
        runTask?.cancel()
    }

    /// 処理中なら、モデルの解放と一時ファイルの削除が終わるまで待つ
    public func waitUntilFinished() async {
        await runTask?.value
    }

    private func runBatch(model: SpeechModel, language: LanguageOption) async -> BatchOutcome? {
        isLoadingModel = true
        let context: any Transcriber
        do {
            context = try await loadTranscriber(model, language)
        } catch {
            return .couldNotStart(ItemFailure(error))
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
                try? queue.markFailed(id, failure: ItemFailure(error, during: .reading(fileName: item.displayName)))
                failed += 1
            }
        }
        // 追加時点で読み込めなかった項目も、この回の失敗として数える
        failed += probeFailedIDs.filter { queue.item($0)?.status.isFailed == true }.count
        probeFailedIDs.removeAll()
        // context はここでスコープを抜けて解放される
        guard succeeded + failed > 0 || cancelled else { return nil }
        return .make(succeeded: succeeded, failed: failed, wasCancelled: cancelled, remaining: queue.counts.pending)
    }

    /// 1 件分の処理。動画は音声を一時 MP3 に変換してから文字起こしし、一時ファイルは必ず削除する。
    private func process(_ item: QueueItem, context: any Transcriber, options: TranscriptionOptions) async throws -> Transcript {
        let id = item.id
        return try await SecurityScopedAccess.withAccess(url: item.url, bookmark: item.bookmark) { url in
            // 追加直後の確認が終わっていない場合もあるので、ここで種類を確かめる
            let info = try await inspectMedia(url)
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

    // MARK: 長さと種類の確認（読み込めない形式を早めに知らせる）

    private func enqueueProbe(_ ids: [UUID]) {
        probeQueue += ids
        guard probeTask == nil, !probeQueue.isEmpty else { return }
        probeTask = Task {
            while !probeQueue.isEmpty {
                let id = probeQueue.removeFirst()
                guard let item = queue.item(id), item.status == .pending else { continue }
                do {
                    let info = try await SecurityScopedAccess.withAccess(url: item.url, bookmark: item.bookmark) { url in
                        try await inspectMedia(url)
                    }
                    queue.setDuration(id, info.duration > 0 ? info.duration : nil)
                    queue.setMediaKind(id, info.kind)
                } catch {
                    // 処理が既に始まっていればそちらの結果に任せる
                    if queue.item(id)?.status == .pending {
                        try? queue.markFailed(id, failure: ItemFailure(error, during: .reading(fileName: item.displayName)))
                        probeFailedIDs.insert(id)
                    }
                }
            }
            probeTask = nil
        }
    }
}
