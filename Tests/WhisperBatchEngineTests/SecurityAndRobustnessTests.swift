import AVFoundation
import Foundation
import Synchronization
import Testing
import WhisperBatchCore
import whisper
@testable import WhisperBatchEngine

@Suite("モデルの取得先の制限")
struct ModelDownloadHostTests {
    init() { TestLocalization.setUp() }

    @Test(arguments: [
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/abc/ggml-base.bin",
        "https://us.aws.cdn.hf.co/xet-bridge-us/641ab5/518970",
        "https://cdn-lfs.huggingface.co/repos/aa/bb",
        "https://cas-bridge.xethub.hf.co/xet-bridge-us/x",
        "https://HuggingFace.co:443/a",
    ])
    func allowsHuggingFace(_ string: String) {
        #expect(ModelDownloadHosts.isAllowed(URL(string: string)))
    }

    @Test(arguments: [
        "http://huggingface.co/a",                 // HTTPS 以外
        "https://example.com/ggml-base.bin",
        "https://huggingface.co.example.com/a",    // 末尾が一致しない
        "https://evilhuggingface.co/a",            // サブドメインではない
        "https://notahf.co/a",
        "https://huggingface.co@example.com/a",    // ユーザー名の形でほかのホストを指す
        "https://huggingface.co:8443/a",           // 既定以外のポート
        "file:///tmp/ggml-base.bin",
    ])
    func blocksOtherHosts(_ string: String) {
        #expect(!ModelDownloadHosts.isAllowed(URL(string: string)))
    }

    @Test func blocksMissingURL() {
        #expect(!ModelDownloadHosts.isAllowed(nil))
    }

    /// 再開用データ（NSKeyedArchiver 形式・古い形式の両方）から接続先を読む
    @Test func readsURLFromResumeData() throws {
        let dict: NSDictionary = [
            "NSURLSessionDownloadURL": "https://us.aws.cdn.hf.co/xet-bridge-us/abc",
            "NSURLSessionResumeBytesReceived": 1234,
            "NSURLSessionResumeOriginalRequest": Data([1, 2, 3]),
        ]
        // URLSession と同じく、ルートを "NSKeyedArchiveRootObjectKey" という名前で保存したもの
        let archiver = NSKeyedArchiver(requiringSecureCoding: true)
        archiver.encode(dict.mutableCopy(), forKey: "NSKeyedArchiveRootObjectKey")
        archiver.finishEncoding()
        #expect(ModelDownloadHosts.resumeURL(in: archiver.encodedData)?.host() == "us.aws.cdn.hf.co")
        let keyed = try NSKeyedArchiver.archivedData(withRootObject: dict, requiringSecureCoding: true)
        #expect(ModelDownloadHosts.resumeURL(in: keyed)?.host() == "us.aws.cdn.hf.co")
        let plain = try PropertyListSerialization.data(fromPropertyList: dict, format: .binary, options: 0)
        #expect(ModelDownloadHosts.resumeURL(in: plain)?.host() == "us.aws.cdn.hf.co")
        #expect(ModelDownloadHosts.resumeURL(in: Data("garbage".utf8)) == nil)
    }

    /// 許可していない接続先のモデルは、通信を始める前に止める
    @Test func refusesUntrustedModelBeforeConnecting() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("wb-host-test-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = ModelFile(name: "evil.bin", url: URL(string: "https://example.com/evil.bin")!, sizeBytes: 10, sha256: "00")
        let model = SpeechModel(id: "test-evil", engine: .whisper, files: [file])
        do {
            try await ModelDownloader.download(model, into: ModelStorage(directory: folder), resumeData: nil,
                                               progress: { _, _ in }, verifying: {})
            Issue.record("ダウンロードが止まらなかった")
        } catch {
            guard case .failed(let failure) = error.reason else { Issue.record("中止扱いになった"); return }
            #expect(failure == .untrustedDownloadHost(host: "example.com"))
            #expect(error.errorDescription?.contains("ログイン") == true)
        }
    }
}

@Suite("Whisper の探索方式")
struct WhisperDecodingTests {
    @Test func mapsToWhisperStrategy() {
        #expect(WhisperContext.samplingStrategy(for: .greedy) == WHISPER_SAMPLING_GREEDY)
        #expect(WhisperContext.samplingStrategy(for: .beamSearch) == WHISPER_SAMPLING_BEAM_SEARCH)
        #expect(WhisperDecoding.default == .beamSearch)
        #expect(TranscriptionOptions(language: .japanese).decoding == .beamSearch)
    }

    /// 一括処理で選んだ方式が、認識に渡される
    @Test @MainActor func batchPassesDecodingToTranscriber() async {
        let recorder = OptionsRecorder()
        let service = TranscriptionService(loadTranscriber: { _, _ in recorder },
                                           inspectMedia: { _ in MediaInfo(duration: 1, kind: .audio) })
        service.add([FileCandidate(url: URL(fileURLWithPath: "/tmp/wb-test/a.wav"), bookmark: nil)])
        service.start(model: ModelCatalog.defaultModel, language: .english, decoding: .greedy)
        await service.waitUntilFinished()
        #expect(recorder.seen.map(\.decoding) == [.greedy])
        #expect(recorder.seen.map(\.language) == [.english])
    }
}

private final class OptionsRecorder: Transcriber, Sendable {
    private let options = Mutex<[TranscriptionOptions]>([])
    var seen: [TranscriptionOptions] { options.withLock { $0 } }

    func transcribe(fileURL: URL, options: TranscriptionOptions,
                    progress: @escaping @Sendable (Double) -> Void) async throws -> Transcript {
        self.options.withLock { $0.append(options) }
        return Transcript(segments: [], language: options.language.code)
    }
}

@Suite("空き容量の確認")
struct DiskSpaceTests {
    init() { TestLocalization.setUp() }

    @Test func passesWhenThereIsRoom() throws {
        let temp = FileManager.default.temporaryDirectory
        try DiskSpace.ensure(1, availableIn: [temp, temp])
        #expect(DiskSpace.available(at: temp) != nil)
    }

    @Test func failsForBothTemporaryAndFinalFolders() throws {
        let temp = FileManager.default.temporaryDirectory
        let home = FileManager.default.homeDirectoryForCurrentUser
        let huge = Int64.max / 2
        #expect(throws: WhisperBatchError.self) { try DiskSpace.ensure(huge, availableIn: [temp]) }
        // 1 つ目のフォルダを確かめられなくても、移動先（2 つ目）の空きは確かめる
        let unknown = URL(fileURLWithPath: "/no/such/folder/\(UUID().uuidString)")
        #expect(throws: WhisperBatchError.self) { try DiskSpace.ensure(huge, availableIn: [unknown, home]) }
    }

    /// 調べられないフォルダは確かめない（書き込み時のエラーに任せる）
    @Test func skipsFoldersThatCannotBeInspected() throws {
        try DiskSpace.ensure(Int64.max / 2, availableIn: [URL(fileURLWithPath: "/no/such/folder/\(UUID().uuidString)")])
    }
}

@Suite("ブックマークの更新")
@MainActor
struct BookmarkRefreshTests {
    @Test func refreshedBookmarkUpdatesQueueItem() {
        let service = TranscriptionService(loadTranscriber: { _, _ in OptionsRecorder() },
                                           inspectMedia: { _ in MediaInfo(duration: 1, kind: .audio) })
        let id = service.add([FileCandidate(url: URL(fileURLWithPath: "/tmp/wb-test/old/a.wav"), bookmark: Data([1]))]).added[0]
        let moved = URL(fileURLWithPath: "/tmp/wb-test/new/a.wav")
        service.applyRefreshedBookmark(.init(url: moved, bookmark: Data([2])), to: id)
        let item = service.queue.item(id)
        #expect(item?.url == moved)
        #expect(item?.bookmark == Data([2]))
        #expect(item?.fileKey == TranscriptionQueue.fileKey(for: moved))
        #expect(item?.displayName == "a.wav")
        #expect(item?.status == .pending)
    }
}

@Suite("読み込みの中止")
struct ReaderCancellationTests {
    /// 1 秒の無音の WAV を作る
    private func makeWAV() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("wb-cancel-\(UUID().uuidString).wav")
        let format = AVAudioFormat(standardFormatWithSampleRate: 16_000, channels: 1)!
        let file = try AVAudioFile(forWriting: url, settings: format.settings)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 16_000)!
        buffer.frameLength = 16_000
        try file.write(from: buffer)
        return url
    }

    @Test func readStopsWhenCancelled() async throws {
        let url = try makeWAV()
        defer { try? FileManager.default.removeItem(at: url) }
        let task = Task {
            let reader = try await AudioSampleReader(url: url)
            withUnsafeCurrentTask { $0?.cancel() }
            return try await reader.read(maxFrames: 16_000)
        }
        await #expect(throws: CancellationError.self) { _ = try await task.value }
    }

    @Test func readWorksWhenNotCancelled() async throws {
        let url = try makeWAV()
        defer { try? FileManager.default.removeItem(at: url) }
        let reader = try await AudioSampleReader(url: url)
        #expect(try await reader.read(maxFrames: 20_000).count == 16_000)
    }
}
