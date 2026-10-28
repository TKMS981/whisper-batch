import Foundation
import WhisperBatchCore

public struct TranscriptionOptions: Sendable {
    public var language: LanguageOption
    public var planner: ChunkPlanner

    public init(language: LanguageOption, planner: ChunkPlanner = ChunkPlanner()) {
        self.language = language
        self.planner = planner
    }
}

/// 1 ファイル分の文字起こし。音声をチャンクに分けて順に認識し、境界をつなぎ合わせる。
///
/// 前のチャンクの文章を次のチャンクの文脈（initial prompt）として渡すことはしない。
/// 試験では、渡すと境界付近で文の重複や読み飛ばしが増えたため。
public enum TranscriptionEngine {
    /// - Parameter progress: 0...1。任意のスレッドから呼ばれる。
    @concurrent
    public static func transcribe(
        fileURL: URL,
        context: WhisperContext,
        options: TranscriptionOptions,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> Transcript {
        let reader = try await AudioSampleReader(url: fileURL)
        let rate = AudioSampleReader.sampleRate
        let planner = options.planner
        let chunkFrames = Int(planner.chunkDuration * rate)
        let total = reader.duration

        var language = options.language.code
        var segments: [TranscriptSegment] = []
        var buffer: [Float] = []
        var bufferStart: TimeInterval = 0 // buffer 先頭の、ファイル先頭からの秒数
        var exhausted = false

        progress(0)
        while true {
            try Task.checkCancellation()
            if !exhausted {
                let need = chunkFrames - buffer.count
                let more = try await reader.read(maxFrames: need)
                if more.count < need { exhausted = true }
                buffer.append(contentsOf: more)
            }
            // 末尾に残ったごく短い断片は認識しない（無音から文章を作ってしまう誤認識を避ける）
            if buffer.isEmpty || (exhausted && Double(buffer.count) / rate < 0.3 && !segments.isEmpty) { break }

            // 最後以外のチャンクは、発話の切れ目（末尾付近の最も静かな位置）までを認識する
            let length = planner.cutLength(samples: buffer, sampleRate: rate, isFinal: exhausted)
            let samples = length == buffer.count ? buffer : Array(buffer[..<length])
            let bufferDuration = Double(length) / rate
            let chunkStart = bufferStart
            let result = try await context.transcribe(
                samples: samples,
                language: language,
                progress: { p in
                    guard total > 0 else { return }
                    progress(min((chunkStart + p * bufferDuration) / total, 0.999))
                }
            )
            // 自動判定は最初に発話があったチャンクで確定し、以降のチャンクも同じ言語で認識する
            if language == nil, !result.segments.isEmpty, let detected = result.language {
                language = detected
            }

            let decision = planner.decide(segments: result.segments, bufferDuration: bufferDuration, isFinal: exhausted)
            segments += decision.accepted
                .filter { !$0.text.isEmpty }
                .map { TranscriptSegment(start: $0.start + chunkStart, end: $0.end + chunkStart, text: $0.text) }

            if exhausted { break }
            let drop = min(max(Int(decision.resumeAt * rate), 1), length)
            buffer.removeFirst(drop)
            bufferStart += Double(drop) / rate
        }
        progress(1)
        return Transcript(segments: segments, language: language ?? options.language.code)
    }
}
