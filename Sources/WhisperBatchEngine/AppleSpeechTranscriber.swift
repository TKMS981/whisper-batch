import AVFoundation
import Foundation
import Speech
import WhisperBatchCore

/// macOS 標準の音声認識（Speech フレームワークの SpeechAnalyzer）で文字起こしする。
///
/// 認識はこの Mac の中で行う。言語ごとのデータ（モデル）は OS がダウンロード・管理する。
/// - `apple-speech`: SpeechTranscriber（長い音声の文字起こし向け）
/// - `apple-dictation`: DictationTranscriber（音声入力と同じ方式。対応言語が多い）
public final class AppleSpeechTranscriber: Transcriber, Sendable {
    public let model: SpeechModel
    private let locale: Locale

    private init(model: SpeechModel, locale: Locale) {
        self.model = model
        self.locale = locale
    }

    /// 言語に対応していて、言語データが用意されていることを確かめてから作る
    static func make(model: SpeechModel, language: LanguageOption) async throws -> AppleSpeechTranscriber {
        switch await AppleSpeechAssets.status(for: model, language: language) {
        case .installed(let locale):
            return AppleSpeechTranscriber(model: model, locale: locale)
        case .unavailable:
            throw WhisperBatchError.speechRecognitionUnavailable
        case .unsupportedLanguage:
            throw WhisperBatchError.languageNotSupported(modelName: model.displayName, language: language.displayName)
        case .notInstalled, .downloading:
            throw WhisperBatchError.modelNotInstalled(modelName: model.displayName)
        }
    }

    @concurrent
    public func transcribe(fileURL: URL, options: TranscriptionOptions,
                           progress: @escaping @Sendable (Double) -> Void) async throws -> Transcript {
        let reader = try await AudioSampleReader(url: fileURL)
        let total = reader.duration
        let module = AppleSpeechModule(model: model, locale: locale)
        let analyzerFormat = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [module.module])
        let analyzer = SpeechAnalyzer(modules: [module.module],
                                      options: .init(priority: .userInitiated, modelRetention: .lingering))
        try await analyzer.prepareToAnalyze(in: analyzerFormat)

        // 結果は確定したものだけを受け取り、時刻順に集める
        let collector = Task { () throws -> [TranscriptSegment] in
            var pieces: [TranscriptSegment] = []
            try await module.forEachResult { start, end, text in
                pieces.append(TranscriptSegment(start: start, end: end, text: text))
                if total > 0 { progress(min(end / total, 0.999)) }
            }
            return pieces
        }

        progress(0)
        let input = AnalyzerInputSequence(reader: reader, targetFormat: analyzerFormat)
        do {
            try await withTaskCancellationHandler {
                if let end = try await analyzer.analyzeSequence(input) {
                    try await analyzer.finalizeAndFinish(through: end)
                } else {
                    await analyzer.cancelAndFinishNow()
                }
            } onCancel: {
                Task { await analyzer.cancelAndFinishNow() }
            }
        } catch {
            collector.cancel()
            if Task.isCancelled { throw CancellationError() }
            throw error
        }
        let pieces = try await collector.value
        try Task.checkCancellation()
        progress(1)
        return Transcript(segments: SegmentMerger.merge(pieces), language: options.language.code)
    }
}

/// SpeechTranscriber と DictationTranscriber の違いを吸収する
private struct AppleSpeechModule: Sendable {
    private enum Kind: Sendable {
        case speech(SpeechTranscriber)
        case dictation(DictationTranscriber)
    }

    private let kind: Kind

    init(model: SpeechModel, locale: Locale) {
        if model.id == "apple-dictation" {
            kind = .dictation(DictationTranscriber(
                locale: locale, contentHints: [], transcriptionOptions: [.punctuation],
                reportingOptions: [], attributeOptions: [.audioTimeRange]))
        } else {
            kind = .speech(SpeechTranscriber(
                locale: locale, transcriptionOptions: [], reportingOptions: [], attributeOptions: [.audioTimeRange]))
        }
    }

    var module: any SpeechModule {
        switch kind {
        case .speech(let m): m
        case .dictation(let m): m
        }
    }

    /// 確定した結果を、語句ごとに (開始秒, 終了秒, 文字列) で渡す。
    /// 1 つの結果に複数の文が含まれることがあるため、語句の時刻を使って文単位にまとめ直せるようにする。
    func forEachResult(_ body: (TimeInterval, TimeInterval, String) -> Void) async throws {
        switch kind {
        case .speech(let m):
            for try await r in m.results where r.isFinal { Self.split(r.text, range: r.range, body) }
        case .dictation(let m):
            for try await r in m.results where r.isFinal { Self.split(r.text, range: r.range, body) }
        }
    }

    private static func split(_ text: AttributedString, range: CMTimeRange,
                              _ body: (TimeInterval, TimeInterval, String) -> Void) {
        var pieces: [(TimeInterval, TimeInterval, String)] = []
        var lastEnd = range.start.seconds
        for run in text.runs {
            let piece = String(text[run.range].characters)
            if let time = run.audioTimeRange, time.start.isNumeric, time.end.isNumeric {
                lastEnd = time.end.seconds
                pieces.append((time.start.seconds, time.end.seconds, piece))
            } else {
                // 時刻のない部分（句読点など）は直前の語句の終わりに付ける
                pieces.append((lastEnd, lastEnd, piece))
            }
        }
        if pieces.isEmpty || pieces.allSatisfy({ $0.0 == $0.1 }) {
            body(range.start.seconds, range.end.seconds, String(text.characters))
        } else {
            pieces.forEach { body($0.0, $0.1, $0.2) }
        }
    }
}

/// 音声ファイルを少しずつ読み、音声認識が受け付ける形式に変換して渡す。
/// 認識側が読み進めた分だけ読み込むので、長い音声でもメモリを使いすぎない。
private struct AnalyzerInputSequence: AsyncSequence, @unchecked Sendable {
    typealias Element = AnalyzerInput

    /// 1 回に渡す長さ（フレーム数、16kHz で 2 秒）
    static let framesPerBuffer = 32_000

    let reader: AudioSampleReader
    let targetFormat: AVAudioFormat?

    func makeAsyncIterator() -> Iterator {
        Iterator(reader: reader, targetFormat: targetFormat)
    }

    struct Iterator: AsyncIteratorProtocol {
        let reader: AudioSampleReader
        let sourceFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: AudioSampleReader.sampleRate,
                                         channels: 1, interleaved: false)!
        let converter: AVAudioConverter?
        var position: AVAudioFramePosition = 0
        var finished = false

        init(reader: AudioSampleReader, targetFormat: AVAudioFormat?) {
            self.reader = reader
            if let targetFormat, targetFormat != sourceFormat {
                converter = AVAudioConverter(from: sourceFormat, to: targetFormat)
            } else {
                converter = nil
            }
        }

        mutating func next() async throws -> AnalyzerInput? {
            guard !finished else { return nil }
            try Task.checkCancellation()
            let samples = try await reader.read(maxFrames: AnalyzerInputSequence.framesPerBuffer)
            guard !samples.isEmpty else {
                finished = true
                return nil
            }
            guard let source = AVAudioPCMBuffer(pcmFormat: sourceFormat, frameCapacity: AVAudioFrameCount(samples.count)) else {
                throw WhisperBatchError.transcriptionFailed(code: -1)
            }
            source.frameLength = AVAudioFrameCount(samples.count)
            samples.withUnsafeBufferPointer { buf in
                source.floatChannelData![0].update(from: buf.baseAddress!, count: samples.count)
            }
            let start = CMTime(value: position, timescale: CMTimeScale(AudioSampleReader.sampleRate))
            position += AVAudioFramePosition(samples.count)
            guard let converter else { return AnalyzerInput(buffer: source, bufferStartTime: start) }
            return AnalyzerInput(buffer: try convert(source, with: converter), bufferStartTime: start)
        }

        private func convert(_ source: AVAudioPCMBuffer, with converter: AVAudioConverter) throws -> AVAudioPCMBuffer {
            let ratio = converter.outputFormat.sampleRate / converter.inputFormat.sampleRate
            let capacity = AVAudioFrameCount((Double(source.frameLength) * ratio).rounded(.up)) + 64
            guard let output = AVAudioPCMBuffer(pcmFormat: converter.outputFormat, frameCapacity: capacity) else {
                throw WhisperBatchError.transcriptionFailed(code: -1)
            }
            nonisolated(unsafe) var supplied = false
            var error: NSError?
            let status = converter.convert(to: output, error: &error) { _, inputStatus in
                if supplied {
                    inputStatus.pointee = .noDataNow
                    return nil
                }
                supplied = true
                inputStatus.pointee = .haveData
                return source
            }
            if status == .error { throw error ?? WhisperBatchError.transcriptionFailed(code: -1) }
            return output
        }
    }
}

/// Apple の音声認識の言語データ（OS が管理）の状態確認とダウンロード
public enum AppleSpeechAssets {
    public enum Status: Equatable, Sendable {
        /// この Mac では音声認識を使えない
        case unavailable
        /// この言語に対応していない
        case unsupportedLanguage
        case notInstalled
        case downloading
        case installed(Locale)
    }

    public static func status(for model: SpeechModel, language: LanguageOption) async -> Status {
        guard model.engine == .apple else { return .unavailable }
        if model.id != "apple-dictation", !SpeechTranscriber.isAvailable { return .unavailable }
        guard let id = language.localeIdentifier, let locale = await supportedLocale(for: model, Locale(identifier: id)) else {
            return .unsupportedLanguage
        }
        // 言語データが OS に入っていても、このアプリが予約していない言語は status が .supported になるため、
        // インストール済みの言語の一覧でも確かめる
        let installed = model.id == "apple-dictation"
            ? await DictationTranscriber.installedLocales
            : await SpeechTranscriber.installedLocales
        if installed.contains(where: { $0.identifier == locale.identifier }) { return .installed(locale) }
        let module = AppleSpeechModule(model: model, locale: locale).module
        switch await AssetInventory.status(forModules: [module]) {
        case .installed: return .installed(locale)
        case .downloading: return .downloading
        case .supported: return .notInstalled
        case .unsupported: return .unsupportedLanguage
        @unknown default: return .notInstalled
        }
    }

    /// 言語データをダウンロードしてインストールする（ダウンロードは OS が Apple のサーバーから行う）
    /// - Parameter progress: 0...1。任意のスレッドから呼ばれる。
    public static func install(_ model: SpeechModel, language: LanguageOption,
                               progress: @escaping @Sendable (Double) -> Void) async throws {
        guard let id = language.localeIdentifier, let locale = await supportedLocale(for: model, Locale(identifier: id)) else {
            throw WhisperBatchError.languageNotSupported(modelName: model.displayName, language: language.displayName)
        }
        let module = AppleSpeechModule(model: model, locale: locale).module
        guard let request = try await AssetInventory.assetInstallationRequest(supporting: [module]) else { return }
        let reporter = Task {
            while !Task.isCancelled {
                progress(request.progress.fractionCompleted)
                try? await Task.sleep(for: .milliseconds(500))
            }
        }
        defer { reporter.cancel() }
        try await withTaskCancellationHandler {
            try await request.downloadAndInstall()
        } onCancel: {
            request.progress.cancel()
        }
        try Task.checkCancellation()
    }

    private static func supportedLocale(for model: SpeechModel, _ locale: Locale) async -> Locale? {
        model.id == "apple-dictation"
            ? await DictationTranscriber.supportedLocale(equivalentTo: locale)
            : await SpeechTranscriber.supportedLocale(equivalentTo: locale)
    }
}
