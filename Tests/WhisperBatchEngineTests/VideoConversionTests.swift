import CryptoKit
import Foundation
import Testing
import WhisperBatchCore
@testable import WhisperBatchEngine

// 動画の MP3 変換。WHISPER_BATCH_TEST_AUDIO_DIR（scripts/make-test-audio.sh で作成）が必要。
// 認識まで確かめるテストは WHISPER_BATCH_TEST_MODEL も必要。

private let env = ProcessInfo.processInfo.environment
private let audioDir = env["WHISPER_BATCH_TEST_AUDIO_DIR"].map { URL(fileURLWithPath: $0) }
private let modelPath = env["WHISPER_BATCH_TEST_MODEL"].map { URL(fileURLWithPath: $0) }
private func media(_ name: String) -> URL { audioDir!.appendingPathComponent(name) }

private func sha256(_ url: URL) throws -> String {
    SHA256.hash(data: try Data(contentsOf: url)).map { String(format: "%02x", $0) }.joined()
}

private func tempMP3() -> URL {
    FileManager.default.temporaryDirectory.appendingPathComponent("wb-test-\(UUID().uuidString).mp3")
}

@Suite("動画の MP3 変換", .enabled(if: audioDir != nil))
struct VideoConversionTests {
    init() { TestLocalization.setUp() }

    /// macOS（AVFoundation）で読めることを確認した動画の組み合わせ
    @Test(arguments: ["ja1.mp4", "ja1_hevc.mp4", "ja1.mov", "ja1_alac.mov", "ja1_pcm.mov", "ja1_prores.mov",
                      "ja1.m4v", "ja1.3gp", "ja1.mpg", "ja1.ts"])
    func convertsVideoAudioToMP3(file: String) async throws {
        let source = media(file)
        let before = try sha256(source)
        let info = try await MediaInspector.inspect(source)
        #expect(info.kind == .video)
        #expect(abs(info.duration - 7.68) < 0.3)

        let out = tempMP3()
        defer { try? FileManager.default.removeItem(at: out) }
        try await MP3Converter.convert(source: source, destination: out, progress: { _ in })
        // 出力は MP3 として読める音声で、長さが元と同じ
        let mp3 = try await MediaInspector.inspect(out)
        #expect(mp3.kind == .audio)
        #expect(abs(mp3.duration - info.duration) < 0.3)
        let header = try FileHandle(forReadingFrom: out).read(upToCount: 2) ?? Data()
        #expect(header.count == 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0, "MPEG フレーム同期で始まる")
        // 元の動画は変わらない
        #expect(try sha256(source) == before)
    }

    @Test func audioFilesAreNotTreatedAsVideo() async throws {
        #expect(try await MediaInspector.inspect(media("ja1.m4a")).kind == .audio)
        #expect(try await MediaInspector.inspect(media("ja1.wav")).kind == .audio)
    }

    @Test(arguments: ["ja1.mkv", "ja1_video.webm", "ja1.avi"])
    func unreadableVideosFailClearly(file: String) async {
        do {
            _ = try await MediaInspector.inspect(media(file))
            Issue.record("\(file) は読み込めないはず")
        } catch let error as WhisperBatchError {
            #expect(error.errorDescription?.contains(file) == true)
        } catch {
            Issue.record("WhisperBatchError ではない: \(error)")
        }
    }

    @Test func videoWithoutAudio() async {
        await #expect(throws: WhisperBatchError.videoHasNoAudio(name: "silent.mp4")) {
            _ = try await MediaInspector.inspect(media("silent.mp4"))
        }
        let out = tempMP3()
        await #expect(throws: WhisperBatchError.videoHasNoAudio(name: "silent.mp4")) {
            try await MP3Converter.convert(source: media("silent.mp4"), destination: out, progress: { _ in })
        }
        #expect(!FileManager.default.fileExists(atPath: out.path))
    }

    @Test func missingSourceIsReportedAsNotFound() async {
        await #expect(throws: WhisperBatchError.fileNotFound(name: "nothing.mp4")) {
            try await MP3Converter.convert(source: media("nothing.mp4"), destination: tempMP3(), progress: { _ in })
        }
    }

    /// 途中でキャンセルすると、書きかけの MP3 は残らない
    @Test func cancellationRemovesPartialFile() async throws {
        let out = tempMP3()
        let progress = Recorder()
        let task = Task {
            try await MP3Converter.convert(source: media("long.mp4"), destination: out, progress: { progress.set($0) })
        }
        while progress.value < 0.05 { try await Task.sleep(for: .milliseconds(10)) }
        task.cancel()
        await #expect(throws: CancellationError.self) { try await task.value }
        #expect(!FileManager.default.fileExists(atPath: out.path))
    }

    /// 空き容量が足りない場所へは書き出さない（WB_SMALL_VOLUME に小さいボリュームのパスを指定したときだけ）
    @Test(.enabled(if: env["WB_SMALL_VOLUME"] != nil))
    func insufficientDiskSpace() async {
        let out = URL(fileURLWithPath: env["WB_SMALL_VOLUME"]!).appendingPathComponent("out.mp3")
        do {
            try await MP3Converter.convert(source: media("long.mp4"), destination: out, progress: { _ in })
            Issue.record("容量不足になるはず")
        } catch let error as WhisperBatchError {
            if case .insufficientDiskSpace = error {} else { Issue.record("想定外のエラー: \(error)") }
        } catch {
            Issue.record("想定外のエラー: \(error)")
        }
        #expect(!FileManager.default.fileExists(atPath: out.path))
    }

    /// 動画 → MP3 → Whisper の流れで文字起こしできる
    @Test(.enabled(if: modelPath != nil))
    func transcribesVideoThroughMP3() async throws {
        let url = try #require(modelPath)
        let model = ModelCatalog.all.first { $0.fileName == url.lastPathComponent } ?? ModelCatalog.all[0]
        let ctx = try await WhisperContext.load(model: model, fileURL: url)
        let mp3 = tempMP3()
        defer { try? FileManager.default.removeItem(at: mp3) }
        try await MP3Converter.convert(source: media("ja1.mov"), destination: mp3, progress: { _ in })
        let t = try await TranscriptionEngine.transcribe(fileURL: mp3, context: ctx, options: .init(language: .japanese), progress: { _ in })
        let text = TranscriptFormatter.plainText(t)
        print("video:", text)
        #expect(text.contains("晴天") || text.contains("テスト"))
    }
}

private final class Recorder: @unchecked Sendable {
    private let lock = NSLock()
    private var _value = 0.0
    func set(_ v: Double) { lock.lock(); _value = v; lock.unlock() }
    var value: Double { lock.lock(); defer { lock.unlock() }; return _value }
}
