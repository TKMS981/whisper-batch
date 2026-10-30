import Foundation

/// 音声を、間（無音）で区切った発話ごとの範囲に分ける。
///
/// ReazonSpeech は 1 つの発話を前提にしたモデルで、複数の文をまとめて渡すと一部の文を出力しないことがある
/// （試験では 2〜3 文を含む 4〜8 秒の音声で起きた）。そのため、文の間の無音で区切って 1 発話ずつ認識する。
public struct UtteranceSplitter: Sendable, Equatable {
    /// 音量を測る単位（秒）
    public var frameDuration: TimeInterval
    /// この秒数以上続く無音を、発話の区切りとみなす
    public var minPause: TimeInterval
    /// 1 発話の最大の長さ（秒）。超える場合は最も静かな位置で区切る。
    public var maxUtterance: TimeInterval

    public init(frameDuration: TimeInterval = 0.02, minPause: TimeInterval = 0.25, maxUtterance: TimeInterval = 10) {
        self.frameDuration = frameDuration
        self.minPause = minPause
        self.maxUtterance = maxUtterance
    }

    public struct Result: Equatable, Sendable {
        /// 発話の範囲（サンプルの位置）。音のない範囲は含めない。
        public var utterances: [Range<Int>]
        /// 処理済みとして捨ててよいサンプル数。続きがある場合、最後の発話は途中かもしれないので残す。
        public var consumed: Int
    }

    /// - Parameter isFinal: 音声の最後まで含んでいるか
    public func split(_ samples: [Float], sampleRate: Double, isFinal: Bool) -> Result {
        let frame = max(Int(frameDuration * sampleRate), 1)
        let frameCount = samples.count / frame
        guard frameCount > 0 else {
            return Result(utterances: [], consumed: isFinal ? samples.count : 0)
        }

        // フレームごとの平均二乗振幅
        var power = [Float](repeating: 0, count: frameCount)
        samples.withUnsafeBufferPointer { buf in
            for f in 0 ..< frameCount {
                var sum: Float = 0
                for i in f * frame ..< (f + 1) * frame { sum += buf[i] * buf[i] }
                power[f] = sum / Float(frame)
            }
        }
        let silent = Self.silenceFlags(power)

        // 十分に長い無音の中央を区切りにする
        let minPauseFrames = max(Int((minPause / frameDuration).rounded()), 1)
        var cuts: [Int] = []
        var runStart: Int?
        for f in 0 ... frameCount {
            let isSilent = f < frameCount && silent[f]
            if isSilent, runStart == nil { runStart = f }
            if !isSilent, let start = runStart {
                if f - start >= minPauseFrames, start > 0, f < frameCount {
                    cuts.append((start + f) / 2 * frame)
                }
                runStart = nil
            }
        }

        // 区切りの間を発話とし、長すぎるものは最も静かな位置でさらに分ける
        let bounds = [0] + cuts + [samples.count]
        var pieces: [Range<Int>] = []
        let maxLength = Int(maxUtterance * sampleRate)
        for (a, b) in zip(bounds, bounds.dropFirst()) where b > a {
            var start = a
            while b - start > maxLength {
                let search = (start + maxLength / 2) ..< (start + maxLength)
                let cut = SilenceFinder.quietestPoint(in: samples, range: search, window: Int(0.2 * sampleRate))
                pieces.append(start ..< cut)
                start = cut
            }
            pieces.append(start ..< b)
        }

        // 続きがある場合、最後の区間は発話の途中かもしれないので次回に回す
        var consumed = samples.count
        if !isFinal, pieces.count > 1 {
            consumed = pieces.removeLast().lowerBound
        } else if !isFinal {
            // 区切りが 1 つもない短い音声は、続きを読んでから判断する
            if samples.count < maxLength { return Result(utterances: [], consumed: 0) }
        }

        // 音のない区間は認識しない
        let utterances = pieces.filter { range in
            let first = range.lowerBound / frame
            let last = min((range.upperBound + frame - 1) / frame, frameCount)
            return first < last && silent[first ..< last].contains(false)
        }
        return Result(utterances: utterances, consumed: consumed)
    }

    /// 無音とみなすフレーム。静かな部分（下位 10%）より約 9dB 大きい音を発話とする。
    /// ただし基準は最も大きい部分の -10dB〜-30dB の範囲に収める（全体が発話の場合や、雑音が大きい場合のため）。
    static func silenceFlags(_ power: [Float]) -> [Bool] {
        let sorted = power.sorted()
        let floor = sorted[sorted.count / 10]
        let peak = sorted[min(sorted.count * 99 / 100, sorted.count - 1)]
        let threshold = max(min(floor * 8, peak * 0.1), peak * 0.001, 1e-8)
        return power.map { $0 < threshold }
    }
}
