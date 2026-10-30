import Foundation
import Testing
import WhisperBatchCore
@testable import WhisperBatchEngine

// Whisper 以外の方式の結合テスト。次の環境変数を設定したときだけ実行する。
//   WHISPER_BATCH_TEST_SPEECH        : 日本語の音声ファイル（「本日は晴天なり…」など）
//   WHISPER_BATCH_TEST_LONG_SPEECH   : 1 分以上の日本語の音声ファイル（区切って認識するかの確認用、任意）
//   WHISPER_BATCH_TEST_REAZON_DIR    : ReazonSpeech のファイルを置いたフォルダ
//   WHISPER_BATCH_TEST_REAZON_MODEL  : そのフォルダのモデルの ID（省略時は reazonspeech-k2-v2-int8）
//   WHISPER_BATCH_TEST_APPLE_SPEECH  : Apple の音声認識で試す英語の音声ファイル（英語の言語データは macOS に入っていることが多い）

private let env = ProcessInfo.processInfo.environment
private let speech = env["WHISPER_BATCH_TEST_SPEECH"].map { URL(fileURLWithPath: $0) }
private let longSpeech = env["WHISPER_BATCH_TEST_LONG_SPEECH"].map { URL(fileURLWithPath: $0) }
private let reazonDir = env["WHISPER_BATCH_TEST_REAZON_DIR"].map { URL(fileURLWithPath: $0) }
private let appleSpeech = env["WHISPER_BATCH_TEST_APPLE_SPEECH"].map { URL(fileURLWithPath: $0) }

@Suite("ReazonSpeech", .enabled(if: reazonDir != nil && speech != nil), .serialized)
struct ReazonSpeechTests {
    init() { TestLocalization.setUp() }

    private func load() async throws -> ReazonSpeechContext {
        let model = try #require(ModelCatalog.model(id: env["WHISPER_BATCH_TEST_REAZON_MODEL"] ?? "reazonspeech-k2-v2-int8"))
        let storage = ModelStorage(directory: try #require(reazonDir))
        // テスト用のフォルダにはファイルを直接置くので、保存先のサブフォルダではなくそのフォルダを使う
        return try await ReazonSpeechContext.load(model: model, directory: storage.directory)
    }

    @Test func transcribesJapanese() async throws {
        let ctx = try await load()
        let progress = ProgressValues()
        let t = try await ctx.transcribe(fileURL: try #require(speech), options: .init(language: .japanese)) { progress.add($0) }
        let text = TranscriptFormatter.plainText(t)
        print("reazon:", text, t.segments.map { "\($0.start)-\($0.end)" })
        #expect(text.contains("晴天") || text.contains("テスト"))
        #expect(t.language == "ja")
        #expect(progress.last == 1)
        #expect(t.segments.allSatisfy { $0.end >= $0.start })
    }

    /// 20 秒を超える音声は区切って認識し、時刻は音声の先頭からの秒数になる
    @Test(.enabled(if: longSpeech != nil))
    func transcribesLongAudioInPieces() async throws {
        let ctx = try await load()
        let t = try await ctx.transcribe(fileURL: try #require(longSpeech), options: .init(language: .japanese)) { _ in }
        print("reazon long:", t.segments.map { String(format: "[%.1f-%.1f] %@", $0.start, $0.end, $0.text) }.joined(separator: "\n"))
        #expect((t.segments.last?.end ?? 0) > 30)
        #expect(zip(t.segments, t.segments.dropFirst()).allSatisfy { $0.start <= $1.start })
    }

    @Test func cancels() async throws {
        let ctx = try await load()
        let url = try #require(longSpeech ?? speech)
        let task = Task { try await ctx.transcribe(fileURL: url, options: .init(language: .japanese)) { _ in } }
        task.cancel()
        await #expect(throws: CancellationError.self) { _ = try await task.value }
    }
}

@Suite("Apple の音声認識", .enabled(if: appleSpeech != nil))
struct AppleSpeechTests {
    init() { TestLocalization.setUp() }

    @Test func transcribesEnglish() async throws {
        let model = try #require(ModelCatalog.model(id: "apple-speech"))
        let transcriber = try await AppleSpeechTranscriber.make(model: model, language: .english)
        let t = try await transcriber.transcribe(fileURL: try #require(appleSpeech), options: .init(language: .english)) { _ in }
        let text = TranscriptFormatter.plainText(t)
        print("apple:", text, t.segments.map { "\($0.start)-\($0.end)" })
        #expect(text.localizedCaseInsensitiveContains("meeting"))
        #expect(t.language == "en")
    }

    /// 自動判定は選べない
    @Test func rejectsAutomaticLanguage() async throws {
        let model = try #require(ModelCatalog.model(id: "apple-speech"))
        #expect(await AppleSpeechAssets.status(for: model, language: .auto) == .unsupportedLanguage)
    }
}

private final class ProgressValues: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [Double] = []
    func add(_ v: Double) { lock.lock(); values.append(v); lock.unlock() }
    var last: Double? { lock.lock(); defer { lock.unlock() }; return values.last }
}
