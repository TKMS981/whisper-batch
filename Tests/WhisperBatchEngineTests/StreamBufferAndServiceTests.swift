import Foundation
import Testing
import WhisperBatchCore
@testable import WhisperBatchEngine

/// 配列から順に読み出すだけの音声
private final class ArraySource: AudioSampleSource {
    private let samples: [Float]
    private var position = 0
    private(set) var requests: [Int] = []

    init(count: Int) { samples = (0 ..< count).map(Float.init) }

    func read(maxFrames: Int) async throws -> [Float] {
        requests.append(maxFrames)
        let end = min(position + maxFrames, samples.count)
        defer { position = end }
        return Array(samples[position ..< end])
    }
}

@Suite("音声の読み込み窓")
struct AudioStreamBufferTests {
    @Test func fillsUpToCapacityAndTracksTime() async throws {
        let source = ArraySource(count: 25)
        var window = AudioStreamBuffer(source: source, sampleRate: 10, capacity: 10)
        try await window.fill()
        #expect(window.samples == (0 ..< 10).map(Float.init))
        #expect(!window.isExhausted)
        #expect(window.startTime == 0)
        #expect(window.duration == 1)

        window.consume(4)
        #expect(window.samples.first == 4)
        #expect(window.startTime == 0.4)
        #expect(window.time(atFrame: 2) == 0.6)

        // 空いた分だけ読み足す
        try await window.fill()
        #expect(source.requests == [10, 4])
        #expect(window.samples == (4 ..< 14).map(Float.init))
    }

    @Test func becomesExhaustedAtTheEnd() async throws {
        let source = ArraySource(count: 12)
        var window = AudioStreamBuffer(source: source, sampleRate: 10, capacity: 10)
        try await window.fill()
        window.consume(10)
        try await window.fill()
        #expect(window.isExhausted)
        #expect(window.samples == [10, 11])
        // 終端に達した後は読みに行かない
        try await window.fill()
        #expect(source.requests == [10, 10])
        // 窓の長さを超える分や負の値は無視する
        window.consume(100)
        #expect(window.isEmpty)
        #expect(window.startTime == 1.2)
        window.consume(-1)
        #expect(window.startTime == 1.2)
    }

    /// エンジンと同じ使い方で、すべてのサンプルを一度ずつ処理できる
    @Test func everySampleIsProcessedOnce() async throws {
        var window = AudioStreamBuffer(source: ArraySource(count: 1_003), sampleRate: 100, capacity: 64)
        var seen: [Float] = []
        while true {
            try await window.fill()
            if window.isEmpty { break }
            let used = window.isExhausted ? window.count : 37
            #expect(window.samples.first == Float(window.consumedFrames))
            seen += window.samples[..<used]
            window.consume(used)
            if window.isExhausted { break }
        }
        #expect(seen == (0 ..< 1_003).map(Float.init))
    }
}

// MARK: - TranscriptionService

/// ファイル名で結果を変える認識の代わり。"bad" を含むと失敗し、"slow" を含むとキャンセルされるまで待つ。
private final class FakeTranscriber: Transcriber, Sendable {
    func transcribe(fileURL: URL, options: TranscriptionOptions,
                    progress: @escaping @Sendable (Double) -> Void) async throws -> Transcript {
        let name = fileURL.lastPathComponent
        if name.contains("bad") { throw WhisperBatchError.transcriptionFailed(code: -1) }
        if name.contains("slow") { try await Task.sleep(for: .seconds(60)) }
        progress(1)
        return Transcript(segments: [TranscriptSegment(start: 0, end: 1, text: name)], language: "ja")
    }
}

@MainActor
private final class OutcomeBox {
    var outcomes: [BatchOutcome?] = []
}

@MainActor
private func makeService(
    loader: TranscriptionService.TranscriberLoading? = nil,
    inspect: TranscriptionService.MediaInspecting? = nil
) -> TranscriptionService {
    TranscriptionService(
        loadTranscriber: loader ?? { _, _ in FakeTranscriber() },
        inspectMedia: inspect ?? { url in
            if url.lastPathComponent.contains("broken") { throw WhisperBatchError.unsupportedFormat(name: url.lastPathComponent) }
            return MediaInfo(duration: 1, kind: .audio)
        })
}

@MainActor
@discardableResult
private func add(_ names: [String], to service: TranscriptionService) -> [UUID] {
    service.add(names.map { FileCandidate(url: URL(fileURLWithPath: "/tmp/wb-test/\($0)"), bookmark: nil) }).added
}

@Suite("一括処理（TranscriptionService）", .serialized)
@MainActor
struct TranscriptionServiceTests {
    init() { TestLocalization.setUp() }

    private func run(_ service: TranscriptionService) async -> BatchOutcome? {
        let box = OutcomeBox()
        service.start(model: ModelCatalog.defaultModel, language: .japanese) { box.outcomes.append($0) }
        #expect(service.isRunning)
        await service.waitUntilFinished()
        #expect(!service.isRunning)
        #expect(!service.isLoadingModel)
        #expect(box.outcomes.count == 1)
        return box.outcomes.first ?? nil
    }

    @Test func oneFailureDoesNotStopTheBatch() async {
        let service = makeService()
        let ids = add(["a.wav", "bad.wav", "c.wav"], to: service)
        let outcome = await run(service)
        #expect(outcome == .partialFailure(succeeded: 2, failed: 1))
        #expect(service.queue.item(ids[0])?.transcript?.segments.first?.text == "a.wav")
        #expect(service.queue.item(ids[1])?.status.isFailed == true)
        #expect(service.queue.item(ids[2])?.status == .completed)
    }

    @Test func modelLoadFailureLeavesItemsPending() async {
        let service = makeService(loader: { model, _ in throw WhisperBatchError.modelLoadFailed(modelName: model.displayName) })
        add(["a.wav"], to: service)
        let outcome = await run(service)
        #expect(outcome == .couldNotStart(ItemFailure(error: .modelLoadFailed(modelName: ModelCatalog.defaultModel.displayName))))
        #expect(service.queue.counts.pending == 1)
    }

    /// 追加直後の確認で読み込めなかった項目も、その回の失敗として 1 回だけ数える
    @Test func unreadableFilesAreCountedOnce() async {
        let service = makeService()
        let ids = add(["a.wav", "broken.wav"], to: service)
        let outcome = await run(service)
        #expect(outcome == .partialFailure(succeeded: 1, failed: 1))
        #expect(service.queue.item(ids[1])?.status.isFailed == true)
        // 失敗した項目は待機中に戻せる
        #expect(service.retry([ids[1]]) == 1)
        #expect(service.queue.item(ids[1])?.status == .pending)
    }

    @Test func cancellationStopsAndKeepsTheRestPending() async throws {
        let service = makeService()
        let ids = add(["slow.wav", "b.wav"], to: service)
        let box = OutcomeBox()
        service.start(model: ModelCatalog.defaultModel, language: .japanese) { box.outcomes.append($0) }
        let deadline = ContinuousClock.now + .seconds(10)
        while service.queue.item(ids[0])?.status.isProcessing != true, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(5))
        }
        service.cancel()
        await service.waitUntilFinished()
        #expect(box.outcomes == [.cancelled(succeeded: 0, failed: 0, remaining: 1)])
        #expect(service.queue.item(ids[0])?.status == .cancelled)
        #expect(service.queue.item(ids[1])?.status == .pending)
    }

    @Test func nothingToDoGivesNoOutcome() async {
        let service = makeService()
        #expect(await run(service) == nil)
    }
}
