import Foundation

/// 処理中の段階
public enum ProcessingPhase: String, Equatable, Sendable {
    /// 動画から音声を取り出して MP3 に変換している
    case converting
    /// Whisper で文字起こししている
    case transcribing
}

/// 失敗の理由。表示文は表示言語に合わせてその都度作る。
public struct ItemFailure: Equatable, Sendable {
    public var error: WhisperBatchError?
    /// WhisperBatchError 以外のエラーの説明（OS が返した文など）
    public var fallbackMessage: String

    /// OS のエラーは `ErrorMapper` で分類し、分類できたものは表示言語の切り替えにも追従させる
    public init(_ error: any Error, during operation: ErrorMapper.Operation = .unspecified) {
        self.error = ErrorMapper.map(error, during: operation)
        self.fallbackMessage = userFacingMessage(for: error, during: operation)
    }

    public init(error: WhisperBatchError) {
        self.error = error
        self.fallbackMessage = error.errorDescription ?? ""
    }

    public var message: String { error?.errorDescription ?? fallbackMessage }
}

/// キュー項目の処理状態
public enum ItemStatus: Equatable, Sendable {
    case pending
    case processing(phase: ProcessingPhase, progress: Double)
    case completed
    case failed(ItemFailure)
    case cancelled

    public var name: String {
        switch self {
        case .pending: "pending"
        case .processing(let phase, _): "processing(\(phase.rawValue))"
        case .completed: "completed"
        case .failed: "failed"
        case .cancelled: "cancelled"
        }
    }

    public var isProcessing: Bool {
        if case .processing = self { return true }
        return false
    }

    /// 削除できるか（処理中の項目は先にキャンセルが必要）
    public var isRemovable: Bool { !isProcessing }

    /// もう一度処理待ちに戻せるか
    public var isRetryable: Bool {
        switch self {
        case .failed, .cancelled, .completed: true
        case .pending, .processing: false
        }
    }

    public var isFailed: Bool {
        if case .failed = self { return true }
        return false
    }
}

public struct QueueItem: Identifiable, Equatable, Sendable {
    public let id: UUID
    /// ファイルの場所（ブックマークを作り直したときに、移動先へ更新する）
    public internal(set) var url: URL
    /// サンドボックス下で後からアクセスし直すためのセキュリティスコープ付きブックマーク
    public internal(set) var bookmark: Data?
    /// 重複判定用のキー
    public internal(set) var fileKey: String
    public var displayName: String
    public var duration: TimeInterval?
    /// 読み込んで確認した種類（確認前は nil）
    public var mediaKind: MediaKind?
    public var status: ItemStatus
    public var transcript: Transcript?

    public init(id: UUID = UUID(), url: URL, bookmark: Data?, displayName: String? = nil) {
        self.id = id
        self.url = url
        self.bookmark = bookmark
        self.fileKey = TranscriptionQueue.fileKey(for: url)
        self.displayName = displayName ?? url.lastPathComponent
        self.status = .pending
    }

    public var isVideo: Bool { mediaKind == .video }

    /// 状態の短い表示（表示言語に従う）
    public var statusLabel: String {
        switch status {
        case .pending:
            return isVideo ? L("status.waitingConversion") : L("status.pending")
        case .processing(let phase, let p):
            let percent = Int((p * 100).rounded(.down))
            return phase == .converting ? L("status.converting", percent) : L("status.transcribing", percent)
        case .completed:
            return L("status.completed")
        case .failed:
            return L("status.failed")
        case .cancelled:
            return L("status.cancelled")
        }
    }
}

/// 追加候補
public struct FileCandidate: Sendable {
    public var url: URL
    public var bookmark: Data?
    public init(url: URL, bookmark: Data?) {
        self.url = url
        self.bookmark = bookmark
    }
}

/// 追加結果（利用者への通知に使う）
public struct AddReport: Equatable, Sendable {
    public var added: [UUID] = []
    /// 既にキューにあったためスキップしたファイル名
    public var duplicates: [String] = []
    /// スキップした重複に対応する既存項目
    public var existingIDsForDuplicates: [UUID] = []
    /// 形式などの理由で追加しなかったもの
    public var rejected: [Rejection] = []

    public struct Rejection: Equatable, Sendable {
        public var name: String
        public var reason: AudioFormats.RejectionReason
    }

    public init() {}

    public var hasSkipped: Bool { !duplicates.isEmpty || !rejected.isEmpty }

    /// 利用者向けの要約。何もスキップしていなければ nil。
    public var skippedSummary: String? {
        guard hasSkipped else { return nil }
        var parts: [String] = []
        if !added.isEmpty { parts.append(L("add.summary.added", added.count)) }
        if !duplicates.isEmpty {
            parts.append(L("add.summary.duplicates", duplicates.count, Self.list(duplicates)))
        }
        if !rejected.isEmpty {
            let names = rejected.map { L("add.summary.rejectedItem", $0.name, $0.reason.message) }
            parts.append(L("add.summary.rejected", rejected.count, Self.list(names)))
        }
        return parts.joined(separator: "\n")
    }

    private static func list(_ names: [String], limit: Int = 3) -> String {
        let separator = L("list.separator")
        if names.count <= limit { return names.joined(separator: separator) }
        return L("list.andMore", names.prefix(limit).joined(separator: separator), names.count - limit)
    }
}

public struct StatusCounts: Equatable, Sendable {
    public var pending = 0
    public var processing = 0
    public var completed = 0
    public var failed = 0
    public var cancelled = 0
    public var total: Int { pending + processing + completed + failed + cancelled }
}

/// 文字起こしキュー。値型で、状態遷移の規則をここに集める。
public struct TranscriptionQueue: Equatable, Sendable {
    public private(set) var items: [QueueItem] = []

    public init() {}

    /// 重複判定キー。シンボリックリンクと `..` を解決したパス。
    public static func fileKey(for url: URL) -> String {
        url.standardizedFileURL.resolvingSymlinksInPath().path
    }

    // MARK: 参照

    public func item(_ id: UUID) -> QueueItem? {
        items.first { $0.id == id }
    }

    public var nextPendingID: UUID? {
        items.first { $0.status == .pending }?.id
    }

    public var hasPending: Bool { nextPendingID != nil }

    public var isProcessing: Bool { items.contains { $0.status.isProcessing } }

    public var completedItems: [QueueItem] {
        items.filter { $0.status == .completed && $0.transcript != nil }
    }

    public var counts: StatusCounts {
        var c = StatusCounts()
        for item in items {
            switch item.status {
            case .pending: c.pending += 1
            case .processing: c.processing += 1
            case .completed: c.completed += 1
            case .failed: c.failed += 1
            case .cancelled: c.cancelled += 1
            }
        }
        return c
    }

    // MARK: 追加・削除

    /// ファイルを追加する。既にキューにあるファイル（同一パス）と、同じ呼び出し内の重複はスキップする。
    @discardableResult
    public mutating func add(_ candidates: [FileCandidate]) -> AddReport {
        var report = AddReport()
        var known = Dictionary(items.map { ($0.fileKey, $0.id) }, uniquingKeysWith: { first, _ in first })
        for candidate in candidates {
            let name = candidate.url.lastPathComponent
            if case .rejected(let reason) = AudioFormats.check(candidate.url) {
                report.rejected.append(.init(name: name, reason: reason))
                continue
            }
            let key = Self.fileKey(for: candidate.url)
            if let existing = known[key] {
                report.duplicates.append(name)
                if !report.existingIDsForDuplicates.contains(existing) {
                    report.existingIDsForDuplicates.append(existing)
                }
                continue
            }
            let item = QueueItem(url: candidate.url, bookmark: candidate.bookmark)
            items.append(item)
            known[key] = item.id
            report.added.append(item.id)
        }
        return report
    }

    /// 指定した項目を削除する。処理中の項目は削除しない。削除した件数を返す。
    @discardableResult
    public mutating func remove(_ ids: Set<UUID>) -> Int {
        let before = items.count
        items.removeAll { ids.contains($0.id) && $0.status.isRemovable }
        return before - items.count
    }

    /// 待機中の項目をすべて削除する
    @discardableResult
    public mutating func removeAllPending() -> Int {
        let before = items.count
        items.removeAll { $0.status == .pending }
        return before - items.count
    }

    /// 完了した項目をすべて削除する（文字起こし結果も消える）
    @discardableResult
    public mutating func removeAllCompleted() -> Int {
        let before = items.count
        items.removeAll { $0.status == .completed }
        return before - items.count
    }

    /// 処理中以外の項目をすべて削除する
    @discardableResult
    public mutating func removeAllRemovable() -> Int {
        let before = items.count
        items.removeAll { $0.status.isRemovable }
        return before - items.count
    }

    // MARK: 状態遷移

    public mutating func setDuration(_ id: UUID, _ duration: TimeInterval?) {
        guard let i = index(id) else { return }
        items[i].duration = duration
    }

    public mutating func setMediaKind(_ id: UUID, _ kind: MediaKind) {
        guard let i = index(id) else { return }
        items[i].mediaKind = kind
    }

    /// 作り直したブックマークと、その指している場所に置き換える（表示名と処理状態はそのまま）
    public mutating func updateLocation(_ id: UUID, url: URL, bookmark: Data) {
        guard let i = index(id) else { return }
        items[i].url = url
        items[i].bookmark = bookmark
        items[i].fileKey = Self.fileKey(for: url)
    }

    /// pending → processing（指定した段階から始める）
    public mutating func markProcessing(_ id: UUID, phase: ProcessingPhase = .transcribing) throws {
        try transition(id, to: .processing(phase: phase, progress: 0)) { $0 == .pending }
    }

    /// 処理中の段階を進める（converting → transcribing）。進捗は 0 に戻る。
    public mutating func setPhase(_ id: UUID, _ phase: ProcessingPhase) throws {
        try transition(id, to: .processing(phase: phase, progress: 0)) { $0.isProcessing }
    }

    /// 進捗を更新する。処理中でない項目、別の段階の通知、後退する値は無視する（非同期に届く古い通知への対策）。
    public mutating func updateProgress(_ id: UUID, phase: ProcessingPhase, _ progress: Double) {
        guard let i = index(id), case .processing(let currentPhase, let current) = items[i].status,
              currentPhase == phase else { return }
        let clamped = min(max(progress, 0), 1)
        if clamped > current {
            items[i].status = .processing(phase: phase, progress: clamped)
        }
    }

    /// processing → completed
    public mutating func markCompleted(_ id: UUID, transcript: Transcript) throws {
        try transition(id, to: .completed) { $0.isProcessing }
        if let i = index(id) { items[i].transcript = transcript }
    }

    /// pending / processing → failed
    public mutating func markFailed(_ id: UUID, failure: ItemFailure) throws {
        try transition(id, to: .failed(failure)) { $0 == .pending || $0.isProcessing }
    }

    /// pending / processing → cancelled
    public mutating func markCancelled(_ id: UUID) throws {
        try transition(id, to: .cancelled) { $0 == .pending || $0.isProcessing }
    }

    /// failed / cancelled / completed → pending。結果は破棄する。戻した件数を返す。
    @discardableResult
    public mutating func resetForRetry(_ ids: Set<UUID>) -> Int {
        var n = 0
        for i in items.indices where ids.contains(items[i].id) && items[i].status.isRetryable {
            items[i].status = .pending
            items[i].transcript = nil
            n += 1
        }
        return n
    }

    // MARK: private

    private func index(_ id: UUID) -> Int? {
        items.firstIndex { $0.id == id }
    }

    private mutating func transition(_ id: UUID, to new: ItemStatus, allowedFrom: (ItemStatus) -> Bool) throws {
        guard let i = index(id) else { return } // 既に削除済みなら何もしない
        let old = items[i].status
        guard allowedFrom(old) else {
            throw WhisperBatchError.invalidStateTransition(from: old.name, to: new.name)
        }
        items[i].status = new
    }
}

/// 一括処理 1 回分の結果
public enum BatchOutcome: Equatable, Sendable {
    case allSucceeded(count: Int)
    case partialFailure(succeeded: Int, failed: Int)
    case allFailed(count: Int)
    case cancelled(succeeded: Int, failed: Int, remaining: Int)
    case couldNotStart(ItemFailure)

    public static func make(succeeded: Int, failed: Int, wasCancelled: Bool, remaining: Int) -> BatchOutcome {
        if wasCancelled {
            return .cancelled(succeeded: succeeded, failed: failed, remaining: remaining)
        }
        if failed == 0 { return .allSucceeded(count: succeeded) }
        if succeeded == 0 { return .allFailed(count: failed) }
        return .partialFailure(succeeded: succeeded, failed: failed)
    }

    public var message: String {
        switch self {
        case .allSucceeded(let n):
            return n == 1 ? L("outcome.succeeded.one") : L("outcome.succeeded.many", n)
        case .partialFailure(let s, let f):
            return L("outcome.partial", s, f)
        case .allFailed(let n):
            return L("outcome.allFailed", n)
        case .cancelled(let s, let f, let r):
            var text = f > 0 ? L("outcome.cancelled.withFailures", s, f) : L("outcome.cancelled", s)
            if r > 0 { text += " " + L("outcome.cancelled.remaining", r) }
            return text
        case .couldNotStart(let failure):
            return L("outcome.couldNotStart", failure.message)
        }
    }

    public enum Severity: Sendable { case success, warning, failure, neutral }

    public var severity: Severity {
        switch self {
        case .allSucceeded: .success
        case .partialFailure: .warning
        case .allFailed, .couldNotStart: .failure
        case .cancelled: .neutral
        }
    }
}
