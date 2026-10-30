import Foundation
import SherpaOnnxC
import WhisperBatchCore

/// sherpa-onnx で読み込んだ ReazonSpeech（k2 / Zipformer RNN-T）のモデルを 1 つ保持する。
///
/// このモデルは 1 つの発話を前提にしているため、音声を間（無音）で区切って 1 発話ずつ認識する
/// （複数の文をまとめて渡すと一部の文を出力しないことがある）。各発話の前後には、
/// ReazonSpeech の公式ライブラリと同じく 0.9 秒の無音を足す（足さないと発話の始まりを落としやすい）。
/// 認識は数秒ブロックするので、whisper と同じく専用のシリアルキューで実行する。
public final class ReazonSpeechContext: @unchecked Sendable {
    public let model: SpeechModel
    private let recognizer: OpaquePointer
    private let queue: DispatchQueue

    /// 一度に読み込んで発話を探す長さ（秒）
    static let blockDuration: TimeInterval = 30
    /// 発話の前後に足す無音（秒）
    static let padding: TimeInterval = 0.9

    private init(model: SpeechModel, recognizer: OpaquePointer, queue: DispatchQueue) {
        self.model = model
        self.recognizer = recognizer
        self.queue = queue
    }

    deinit {
        SherpaOnnxDestroyOfflineRecognizer(recognizer)
    }

    public static func load(model: SpeechModel, directory: URL) async throws -> ReazonSpeechContext {
        func path(containing part: String) throws -> String {
            guard let file = model.files.first(where: { $0.name.contains(part) }) else {
                throw WhisperBatchError.modelLoadFailed(modelName: model.displayName)
            }
            return directory.appendingPathComponent(file.name).path
        }
        let encoder = try path(containing: "encoder")
        let decoder = try path(containing: "decoder")
        let joiner = try path(containing: "joiner")
        let tokens = try path(containing: "tokens")
        let threads = Int32(WhisperContext.recommendedThreadCount)

        let queue = DispatchQueue(label: "WhisperBatch.reazonspeech", qos: .userInitiated)
        let handle: RecognizerHandle = await withCheckedContinuation { continuation in
            queue.async {
                // 設定の文字列は作成時に複製されるので、作成後に解放してよい
                let strings = [encoder, decoder, joiner, tokens, "cpu", "modified_beam_search"].map { strdup($0) }
                defer { strings.forEach { free($0) } }
                var config = SherpaOnnxOfflineRecognizerConfig()
                config.feat_config.sample_rate = Int32(AudioSampleReader.sampleRate)
                config.feat_config.feature_dim = 80
                config.model_config.transducer.encoder = UnsafePointer(strings[0])
                config.model_config.transducer.decoder = UnsafePointer(strings[1])
                config.model_config.transducer.joiner = UnsafePointer(strings[2])
                config.model_config.tokens = UnsafePointer(strings[3])
                config.model_config.provider = UnsafePointer(strings[4])
                config.model_config.num_threads = threads
                config.model_config.debug = 0
                config.decoding_method = UnsafePointer(strings[5])
                config.max_active_paths = 4
                let recognizer = SherpaOnnxCreateOfflineRecognizer(&config)
                continuation.resume(returning: RecognizerHandle(pointer: recognizer))
            }
        }
        guard let recognizer = handle.pointer else { throw WhisperBatchError.modelLoadFailed(modelName: model.displayName) }
        return ReazonSpeechContext(model: model, recognizer: recognizer, queue: queue)
    }

    /// 1 区間（16kHz モノラル）を認識し、文字ごとの (文字列, 区間先頭からの秒) を返す
    private func decode(_ samples: [Float]) async -> [(text: String, start: TimeInterval)] {
        await withCheckedContinuation { continuation in
            queue.async { [self] in
                continuation.resume(returning: Self.run(recognizer: recognizer, samples: samples))
            }
        }
    }

    private static func run(recognizer: OpaquePointer, samples: [Float]) -> [(text: String, start: TimeInterval)] {
        guard let stream = SherpaOnnxCreateOfflineStream(recognizer) else { return [] }
        defer { SherpaOnnxDestroyOfflineStream(stream) }
        samples.withUnsafeBufferPointer { buf in
            SherpaOnnxAcceptWaveformOffline(stream, Int32(AudioSampleReader.sampleRate), buf.baseAddress, Int32(buf.count))
        }
        SherpaOnnxDecodeOfflineStream(recognizer, stream)
        guard let result = SherpaOnnxGetOfflineStreamResult(stream) else { return [] }
        defer { SherpaOnnxDestroyOfflineRecognizerResult(result) }
        let r = result.pointee
        let count = Int(r.count)
        if count > 0, let tokens = r.tokens_arr, let times = r.timestamps {
            return (0 ..< count).compactMap { i in
                guard let token = tokens[i] else { return nil }
                return (String(cString: token), TimeInterval(times[i]))
            }
        }
        // 時刻がない場合は全体を 1 つにする
        guard let text = r.text else { return [] }
        let whole = String(cString: text)
        return whole.isEmpty ? [] : [(whole, 0)]
    }
}

/// 作成したレコグナイザーをキューから受け取るための箱（以降はこのクラスのキューの上でだけ使う）
private struct RecognizerHandle: @unchecked Sendable {
    let pointer: OpaquePointer?
}

extension ReazonSpeechContext: Transcriber {
    @concurrent
    public func transcribe(fileURL: URL, options: TranscriptionOptions,
                           progress: @escaping @Sendable (Double) -> Void) async throws -> Transcript {
        let reader = try await AudioSampleReader(url: fileURL)
        let rate = AudioSampleReader.sampleRate
        let splitter = UtteranceSplitter()
        let pad = [Float](repeating: 0, count: Int(Self.padding * rate))
        let total = reader.duration
        var window = AudioStreamBuffer(source: reader, sampleRate: rate, capacity: Int(Self.blockDuration * rate))

        var segments: [TranscriptSegment] = []

        progress(0)
        while true {
            try Task.checkCancellation()
            try await window.fill()
            let result = splitter.split(window.samples, sampleRate: rate, isFinal: window.isExhausted)
            for range in result.utterances {
                try Task.checkCancellation()
                let tokens = await decode(pad + window.samples[range] + pad)
                let text = tokens.map(\.text).joined().trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { continue }
                // 時刻は足した無音の分を差し引き、発話の範囲に収める
                let utteranceStart = window.time(atFrame: range.lowerBound)
                let utteranceEnd = window.time(atFrame: range.upperBound)
                let first = tokens.first.map { utteranceStart + $0.start - Self.padding } ?? utteranceStart
                let start = min(max(first, utteranceStart), utteranceEnd)
                segments.append(TranscriptSegment(start: start, end: utteranceEnd, text: text))
                if total > 0 { progress(min(utteranceEnd / total, 0.999)) }
            }
            window.consume(result.consumed)
            if window.isExhausted && (window.isEmpty || result.consumed == 0) { break }
        }
        progress(1)
        return Transcript(segments: segments, language: "ja")
    }
}
