import Foundation
import Testing
@testable import WhisperBatchCore

private func candidate(_ path: String) -> FileCandidate {
    FileCandidate(url: URL(fileURLWithPath: path), bookmark: nil)
}

@Suite("キューの追加と重複判定")
struct QueueAddTests {
    init() { TestLocalization.setUp() }

    @Test func addsInOrder() {
        var q = TranscriptionQueue()
        let r = q.add([candidate("/tmp/a.wav"), candidate("/tmp/b.mp3"), candidate("/tmp/c.m4a")])
        #expect(r.added.count == 3)
        #expect(q.items.map(\.displayName) == ["a.wav", "b.mp3", "c.m4a"])
        #expect(q.items.allSatisfy { $0.status == .pending })
        #expect(r.skippedSummary == nil)
    }

    @Test func skipsDuplicatesAcrossCalls() {
        var q = TranscriptionQueue()
        q.add([candidate("/tmp/a.wav")])
        let r = q.add([candidate("/tmp/a.wav"), candidate("/tmp/b.wav")])
        #expect(r.added.count == 1)
        #expect(r.duplicates == ["a.wav"])
        #expect(r.existingIDsForDuplicates == [q.items[0].id])
        #expect(q.items.count == 2)
        #expect(r.skippedSummary?.contains("既にキューにある") == true)
    }

    @Test func skipsDuplicatesWithinOneCall() {
        var q = TranscriptionQueue()
        let r = q.add([candidate("/tmp/a.wav"), candidate("/tmp/a.wav")])
        #expect(r.added.count == 1)
        #expect(r.duplicates.count == 1)
    }

    @Test func normalizesPathsForDuplicateCheck() {
        var q = TranscriptionQueue()
        q.add([candidate("/tmp/x/../a.wav")])
        let r = q.add([candidate("/tmp/./a.wav")])
        #expect(r.duplicates == ["a.wav"])
    }

    @Test func sameNameInDifferentFoldersIsNotDuplicate() {
        var q = TranscriptionQueue()
        let r = q.add([candidate("/tmp/1/a.wav"), candidate("/tmp/2/a.wav")])
        #expect(r.added.count == 2)
    }

    @Test func rejectsNonAudioFiles() {
        var q = TranscriptionQueue()
        let r = q.add([candidate("/tmp/notes.txt"), candidate("/tmp/noext"), candidate("/tmp/a.flac")])
        #expect(r.added.count == 1)
        #expect(r.rejected.map(\.name) == ["notes.txt", "noext"])
        #expect(r.skippedSummary?.contains("対応していない形式") == true)
    }

    @Test(arguments: ["wav", "mp3", "m4a", "aiff", "aif", "caf", "flac", "aac", "ogg", "opus", "mp4", "mov", "WAV"])
    func acceptsCommonFormats(ext: String) {
        #expect(AudioFormats.check(URL(fileURLWithPath: "/tmp/a.\(ext)")) == .accepted)
    }
}

@Suite("状態遷移")
struct QueueStateTests {
    init() { TestLocalization.setUp() }

    private func queue(_ n: Int) -> TranscriptionQueue {
        var q = TranscriptionQueue()
        q.add((0 ..< n).map { candidate("/tmp/\($0).wav") })
        return q
    }

    @Test func normalFlow() throws {
        var q = queue(2)
        let id = try #require(q.nextPendingID)
        try q.markProcessing(id)
        #expect(q.isProcessing)
        q.updateProgress(id, phase: .transcribing, 0.5)
        #expect(q.item(id)?.status == .processing(phase: .transcribing, progress: 0.5))
        try q.markCompleted(id, transcript: Transcript(segments: [.init(start: 0, end: 1, text: "こんにちは")], language: "ja"))
        #expect(q.item(id)?.status == .completed)
        #expect(q.nextPendingID == q.items[1].id)
        #expect(q.completedItems.count == 1)
    }

    @Test func progressIgnoresStaleAndOutOfRangeValues() throws {
        var q = queue(1)
        let id = q.items[0].id
        q.updateProgress(id, phase: .transcribing, 0.3) // 処理中でなければ無視
        #expect(q.item(id)?.status == .pending)
        try q.markProcessing(id)
        q.updateProgress(id, phase: .transcribing, 0.6)
        q.updateProgress(id, phase: .transcribing, 0.4) // 後退しない
        #expect(q.item(id)?.status == .processing(phase: .transcribing, progress: 0.6))
        q.updateProgress(id, phase: .transcribing, 7)
        #expect(q.item(id)?.status == .processing(phase: .transcribing, progress: 1))
    }

    /// 動画: 変換 → 文字起こし の順に進み、前の段階の遅れた通知は無視する
    @Test func videoPhases() throws {
        var q = queue(1)
        let id = q.items[0].id
        q.setMediaKind(id, .video)
        #expect(q.item(id)?.statusLabel == L("status.waitingConversion"))
        try q.markProcessing(id, phase: .converting)
        q.updateProgress(id, phase: .converting, 0.8)
        #expect(q.item(id)?.status == .processing(phase: .converting, progress: 0.8))
        try q.setPhase(id, .transcribing)
        q.updateProgress(id, phase: .converting, 0.9) // 変換の古い通知
        #expect(q.item(id)?.status == .processing(phase: .transcribing, progress: 0))
        q.updateProgress(id, phase: .transcribing, 0.2)
        #expect(q.item(id)?.status == .processing(phase: .transcribing, progress: 0.2))
        try q.markCompleted(id, transcript: .init(segments: [], language: nil))
        // 完了後に段階を変えることはできない
        #expect(throws: WhisperBatchError.self) { try q.setPhase(id, .converting) }
    }

    @Test func invalidTransitionsThrow() throws {
        var q = queue(1)
        let id = q.items[0].id
        #expect(throws: WhisperBatchError.self) {
            try q.markCompleted(id, transcript: Transcript(segments: [], language: nil))
        }
        try q.markProcessing(id)
        #expect(throws: WhisperBatchError.self) { try q.markProcessing(id) }
    }

    @Test func failureCanHappenBeforeProcessing() throws {
        var q = queue(1)
        let id = q.items[0].id
        let failure = ItemFailure(error: .unsupportedFormat(name: "a.wav"))
        try q.markFailed(id, failure: failure)
        #expect(q.item(id)?.status == .failed(failure))
        #expect(q.nextPendingID == nil)
    }

    @Test func retryResetsFailedCancelledAndCompleted() throws {
        var q = queue(4)
        let ids = q.items.map(\.id)
        try q.markFailed(ids[0], failure: ItemFailure(error: .nothingToExport))
        try q.markProcessing(ids[1]); try q.markCancelled(ids[1])
        try q.markProcessing(ids[2]); try q.markCompleted(ids[2], transcript: Transcript(segments: [], language: nil))
        let n = q.resetForRetry(Set(ids))
        #expect(n == 3)
        #expect(q.items.allSatisfy { $0.status == .pending && $0.transcript == nil })
    }

    @Test func transitionsOnRemovedItemAreIgnored() throws {
        var q = queue(1)
        let id = q.items[0].id
        q.remove([id])
        try q.markProcessing(id) // 例外にしない
        #expect(q.items.isEmpty)
    }
}

@Suite("削除")
struct QueueRemoveTests {
    init() { TestLocalization.setUp() }

    /// 完了した項目だけを消し、待機中・処理中・失敗は残す
    @Test func removeAllCompletedKeepsOthers() throws {
        var q = TranscriptionQueue()
        q.add([candidate("/tmp/a.wav"), candidate("/tmp/b.wav"), candidate("/tmp/c.wav"), candidate("/tmp/d.wav")])
        let ids = q.items.map(\.id)
        try q.markProcessing(ids[0]); try q.markCompleted(ids[0], transcript: Transcript(segments: [], language: "ja"))
        try q.markProcessing(ids[1])
        try q.markFailed(ids[2], failure: ItemFailure(error: .nothingToExport))
        #expect(q.removeAllCompleted() == 1)
        #expect(q.items.map(\.id) == Array(ids[1...]))
        #expect(q.removeAllCompleted() == 0)
    }

    @Test func processingItemCannotBeRemoved() throws {
        var q = TranscriptionQueue()
        q.add([candidate("/tmp/a.wav"), candidate("/tmp/b.wav"), candidate("/tmp/c.wav")])
        let ids = q.items.map(\.id)
        try q.markProcessing(ids[0])
        #expect(q.remove(Set(ids)) == 2)
        #expect(q.items.map(\.id) == [ids[0]])
    }

    @Test func removeAllPendingKeepsOthers() throws {
        var q = TranscriptionQueue()
        q.add([candidate("/tmp/a.wav"), candidate("/tmp/b.wav"), candidate("/tmp/c.wav")])
        let ids = q.items.map(\.id)
        try q.markFailed(ids[0], failure: ItemFailure(error: .nothingToExport))
        try q.markProcessing(ids[1])
        #expect(q.removeAllPending() == 1)
        #expect(q.items.map(\.id) == [ids[0], ids[1]])
        #expect(q.removeAllRemovable() == 1)
        #expect(q.items.map(\.id) == [ids[1]])
    }

    @Test func removedFileCanBeAddedAgain() {
        var q = TranscriptionQueue()
        q.add([candidate("/tmp/a.wav")])
        q.remove([q.items[0].id])
        #expect(q.add([candidate("/tmp/a.wav")]).added.count == 1)
    }
}

@Suite("一括処理の結果")
struct BatchOutcomeTests {
    init() { TestLocalization.setUp() }

    @Test func outcomes() {
        #expect(BatchOutcome.make(succeeded: 3, failed: 0, wasCancelled: false, remaining: 0) == .allSucceeded(count: 3))
        #expect(BatchOutcome.make(succeeded: 2, failed: 1, wasCancelled: false, remaining: 0) == .partialFailure(succeeded: 2, failed: 1))
        #expect(BatchOutcome.make(succeeded: 0, failed: 2, wasCancelled: false, remaining: 0) == .allFailed(count: 2))
        #expect(BatchOutcome.make(succeeded: 1, failed: 0, wasCancelled: true, remaining: 2) == .cancelled(succeeded: 1, failed: 0, remaining: 2))
        #expect(BatchOutcome.cancelled(succeeded: 1, failed: 0, remaining: 2).message.contains("2件は待機中"))
    }
}
