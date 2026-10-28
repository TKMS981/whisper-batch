import Foundation
import Synchronization
import Testing
import WhisperBatchCore
@testable import WhisperBatchEngine

// 実ファイルを使う結合テスト。次の環境変数を設定したときだけ実行する。
//   WHISPER_BATCH_TEST_AUDIO_DIR    : テスト音声のフォルダ（scripts/make-test-audio.sh で作成）
//   WHISPER_BATCH_TEST_MODEL        : ggml モデルファイルのパス（例: ggml-large-v3-turbo-q5_0.bin）
//   WHISPER_BATCH_TEST_DOWNLOAD_DIR : モデルのダウンロードを試す保存先（tiny を取得する）

private let env = ProcessInfo.processInfo.environment
private let audioDir = env["WHISPER_BATCH_TEST_AUDIO_DIR"].map { URL(fileURLWithPath: $0) }
private let modelPath = env["WHISPER_BATCH_TEST_MODEL"].map { URL(fileURLWithPath: $0) }
private let downloadDir = env["WHISPER_BATCH_TEST_DOWNLOAD_DIR"].map { URL(fileURLWithPath: $0) }

private func audio(_ name: String) -> URL { audioDir!.appendingPathComponent(name) }

/// モデルを読み込んで body を実行する。コンテキストは body の終了後に解放される。
/// （ggml の Metal バックエンドは、コンテキストを解放しないままプロセスが終了すると終了処理で異常終了する）
private func withContext<T>(_ body: (WhisperContext) async throws -> T) async throws -> T {
    let url = try #require(modelPath)
    let model = ModelCatalog.all.first { $0.fileName == url.lastPathComponent } ?? ModelCatalog.all[0]
    let ctx = try await WhisperContext.load(model: model, fileURL: url)
    return try await body(ctx)
}

@Suite("音声の読み込み", .enabled(if: audioDir != nil))
struct AudioDecodingTests {
    init() { TestLocalization.setUp() }

    @Test(arguments: ["ja1.wav", "ja1.mp3", "ja1.m4a", "ja1_alac.m4a", "ja1.aac", "ja1.aiff", "ja1.caf", "ja1.flac",
                      "ja1.mp4", "ja1.mov", "ja1_stereo48k.wav", "ja1.ogg", "ja1.opus"])
    func decodesTo16kMono(file: String) async throws {
        let reader = try await AudioSampleReader(url: audio(file))
        var total = 0
        while true {
            let chunk = try await reader.read(maxFrames: 50_000)
            total += chunk.count
            if chunk.count < 50_000 { break }
        }
        let seconds = Double(total) / AudioSampleReader.sampleRate
        // 元音声は約 7.7 秒（エンコーダのパディング分の誤差を許す）
        #expect(abs(seconds - 7.68) < 0.3, "\(file): \(seconds)s")
        #expect(abs(reader.duration - 7.68) < 0.3)
    }

    @Test(arguments: ["ja1.webm", "broken.mp3"])
    func unreadableFilesFailWithReadableError(file: String) async {
        do {
            let reader = try await AudioSampleReader(url: audio(file))
            _ = try await reader.read(maxFrames: 16_000)
            Issue.record("\(file) は読み込めないはず")
        } catch let error as WhisperBatchError {
            print(file, "→", error.errorDescription ?? "")
            #expect(error.errorDescription?.contains(file) == true)
        } catch {
            Issue.record("WhisperBatchError ではない: \(error)")
        }
    }

    @Test func missingFile() async {
        await #expect(throws: WhisperBatchError.fileNotFound(name: "nothing.wav")) {
            _ = try await AudioSampleReader(url: audio("nothing.wav"))
        }
    }
}

@Suite("Whisper による認識", .enabled(if: audioDir != nil && modelPath != nil), .serialized)
struct WhisperTranscriptionTests {
    init() { TestLocalization.setUp() }

    @Test func transcribesJapanese() async throws {
        try await withContext { ctx in
            let progress = ProgressRecorder()
            let t = try await TranscriptionEngine.transcribe(
                fileURL: audio("ja1.wav"), context: ctx, options: .init(language: .japanese),
                progress: { progress.record($0) })
            let text = TranscriptFormatter.plainText(t)
            print("ja1:", text)
            #expect(!t.isEmpty)
            #expect(t.language == "ja")
            #expect(text.contains("テスト") || text.contains("晴天"))
            #expect(progress.last == 1)
        }
    }

    @Test func autoDetectsEnglish() async throws {
        try await withContext { ctx in
            let t = try await TranscriptionEngine.transcribe(
                fileURL: audio("en1.aiff"), context: ctx, options: .init(language: .auto), progress: { _ in })
            print("en1:", TranscriptFormatter.plainText(t))
            #expect(t.language == "en")
            #expect(TranscriptFormatter.plainText(t).lowercased().contains("english"))
        }
    }

    /// 短いチャンクに分けても、各文が欠けず二重にもならないこと
    @Test(arguments: [30.0, 45.0])
    func chunkedLongAudioHasNoGapsOrDuplicates(chunk: Double) async throws {
        try await withContext { ctx in
            let planner = ChunkPlanner(chunkDuration: chunk, tailMargin: 3)
            let t = try await TranscriptionEngine.transcribe(
                fileURL: audio("long.aiff"), context: ctx, options: .init(language: .japanese, planner: planner),
                progress: { _ in })
            let text = TranscriptFormatter.plainText(t, includeTimestamps: true)
            print("long (chunk \(chunk)s):\n" + text)
            // 時刻が単調増加している（境界で巻き戻り＝重複がない）
            for (a, b) in zip(t.segments, t.segments.dropFirst()) {
                #expect(b.start >= a.start - 0.01)
            }
            // 「N番目」の番号を数え、重複と欠落を確認する
            let numbers = text.matches(of: /(\d+)\s*番目/).compactMap { Int($0.1) }
            let counts = Dictionary(numbers.map { ($0, 1) }, uniquingKeysWith: +)
            let duplicated = counts.filter { $0.value > 1 }.keys.sorted()
            let missing = (1 ... 40).filter { counts[$0] == nil }
            print("duplicated:", duplicated, "missing:", missing)
            #expect(duplicated.isEmpty)
            // 「一番目」のように漢数字で出ることがあるため、欠落は少数まで許す
            #expect(missing.count <= 2)
        }
    }

    /// 同じモデルで続けて処理しても、前のファイルの内容に引きずられないこと
    @Test func previousFileDoesNotLeakIntoNext() async throws {
        try await withContext { ctx in
            _ = try await TranscriptionEngine.transcribe(
                fileURL: audio("long.aiff"), context: ctx, options: .init(language: .japanese), progress: { _ in })
            let t = try await TranscriptionEngine.transcribe(
                fileURL: audio("ja2.aiff"), context: ctx, options: .init(language: .japanese), progress: { _ in })
            let text = TranscriptFormatter.plainText(t)
            print("ja2 after long:", text)
            #expect(text.contains("東京") || text.contains("天気"))
            #expect(!text.contains("話題"))
        }
    }

    /// 長い音声を 1 回で処理したときに、同じ文の繰り返しが出ないこと
    @Test func singlePassLongAudioHasNoRepeats() async throws {
        try await withContext { ctx in
            // 直前に別のファイルを処理しておく（アプリでの連続処理と同じ状況）
            _ = try await TranscriptionEngine.transcribe(
                fileURL: audio("ja1.wav"), context: ctx, options: .init(language: .japanese), progress: { _ in })
            let t = try await TranscriptionEngine.transcribe(
                fileURL: audio("long.aiff"), context: ctx, options: .init(language: .japanese), progress: { _ in })
            let text = TranscriptFormatter.plainText(t)
            let numbers = text.matches(of: /(\d+)\s*番目/).compactMap { Int($0.1) }
            let counts = Dictionary(numbers.map { ($0, 1) }, uniquingKeysWith: +)
            let duplicated = counts.filter { $0.value > 1 }.keys.sorted()
            let missing = (1 ... 40).filter { counts[$0] == nil }
            print("single pass duplicated:", duplicated, "missing:", missing)
            #expect(duplicated.isEmpty)
            #expect(missing.count <= 2)
        }
    }

    @Test func cancellationStopsQuickly() async throws {
        try await withContext { ctx in
            let task = Task {
                try await TranscriptionEngine.transcribe(
                    fileURL: audio("long.aiff"), context: ctx, options: .init(language: .japanese), progress: { _ in })
            }
            try await Task.sleep(for: .milliseconds(300))
            let start = ContinuousClock.now
            task.cancel()
            await #expect(throws: CancellationError.self) { _ = try await task.value }
            #expect(ContinuousClock.now - start < .seconds(5))
        }
    }
}

@Suite("モデルのダウンロード", .enabled(if: downloadDir != nil), .serialized)
struct ModelDownloadTests {
    init() { TestLocalization.setUp() }

    let model = ModelCatalog.model(id: "tiny")!

    @Test func cancelThenDownloadAndVerify() async throws {
        let storage = ModelStorage(directory: try #require(downloadDir))
        try? storage.delete(model)

        // キャンセル
        let task = Task {
            try await ModelDownloader.download(model, into: storage, resumeData: nil, progress: { _, _ in }, verifying: {})
        }
        try await Task.sleep(for: .milliseconds(500))
        task.cancel()
        do {
            try await task.value
            Issue.record("キャンセルされるはず")
        } catch let e as ModelDownloadInterruption {
            #expect(e.isCancelled)
        }
        #expect(!storage.isInstalled(model))

        // 通常の取得と検証
        let progress = ProgressRecorder()
        let verified = Mutex(false)
        try await ModelDownloader.download(
            model, into: storage, resumeData: nil,
            progress: { done, total in progress.record(Double(done) / Double(total)) },
            verifying: { verified.withLock { $0 = true } })
        #expect(storage.isInstalled(model))
        #expect(verified.withLock { $0 })
        #expect((progress.last ?? 0) > 0.99)
        #expect(!FileManager.default.fileExists(atPath: storage.partialURL(for: model).path))

        try storage.delete(model)
        #expect(!storage.isInstalled(model))
    }
}

private final class ProgressRecorder: Sendable {
    private let values = Mutex<[Double]>([])
    func record(_ v: Double) { values.withLock { $0.append(v) } }
    var last: Double? { values.withLock { $0.last } }
}
