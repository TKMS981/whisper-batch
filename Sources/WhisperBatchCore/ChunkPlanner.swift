import Foundation

/// 長時間音声を一定長の区間（チャンク）に分けて認識するときの、区間のつなぎ方を決める。
///
/// 方針: チャンク末尾付近で終わる（＝途中で切れている可能性がある）区間は採用せず、
/// その区間の開始位置から次のチャンクを始める。区間の境目は Whisper が判断した発話の切れ目なので、
/// 文の途中で切れたり、同じ部分が二重に出力されたりしにくい。
public struct ChunkPlanner: Sendable, Equatable {
    /// 1 回に認識する音声の長さ（秒）
    public var chunkDuration: TimeInterval
    /// 末尾からこの秒数以内に終わる区間は「切れているかもしれない」とみなす
    public var tailMargin: TimeInterval
    /// 1 回で最低限進める長さ（秒）。無限ループ防止。
    public var minimumAdvance: TimeInterval
    /// チャンクの終わりを、末尾からこの秒数の範囲で最も静かな位置（発話の切れ目）に合わせる
    public var boundarySearch: TimeInterval
    /// 静かな位置を探すときの幅（秒）。広いほど、語の途中の短い無音ではなく文の間の無音を選びやすい。
    public var silenceWindow: TimeInterval

    public init(chunkDuration: TimeInterval = 600, tailMargin: TimeInterval = 5,
                minimumAdvance: TimeInterval? = nil, boundarySearch: TimeInterval = 20,
                silenceWindow: TimeInterval = 0.2) {
        precondition(chunkDuration > tailMargin && tailMargin >= 0)
        self.chunkDuration = chunkDuration
        self.tailMargin = tailMargin
        self.minimumAdvance = minimumAdvance ?? chunkDuration / 2
        self.boundarySearch = min(boundarySearch, chunkDuration / 2)
        self.silenceWindow = silenceWindow
    }

    /// 認識に渡す長さ（サンプル数）を決める。最後のチャンク以外は、末尾付近で最も静かな位置で切る。
    /// 単語の途中で音声が切れると、Whisper が末尾を推測で補って同じ文を繰り返すことがあるため。
    public func cutLength(samples: [Float], sampleRate: Double, isFinal: Bool) -> Int {
        guard !isFinal else { return samples.count }
        let search = Int(boundarySearch * sampleRate)
        let guardBand = Int(0.2 * sampleRate)
        let lower = max(samples.count - search, 0)
        let upper = samples.count - guardBand
        guard upper - lower > guardBand else { return samples.count }
        return SilenceFinder.quietestPoint(in: samples, range: lower ..< upper, window: Int(silenceWindow * sampleRate))
    }

    public struct Decision: Equatable, Sendable {
        /// 採用する区間（時刻はチャンク先頭からの相対秒）
        public var accepted: [TranscriptSegment]
        /// 次のチャンクをチャンク先頭から何秒の位置で始めるか
        public var resumeAt: TimeInterval
    }

    /// - Parameters:
    ///   - segments: このチャンクの認識結果（時刻はチャンク先頭からの相対秒、昇順）
    ///   - bufferDuration: このチャンクの実際の長さ（秒）
    ///   - isFinal: 音声の最後まで含んでいるか
    public func decide(segments: [TranscriptSegment], bufferDuration: TimeInterval, isFinal: Bool) -> Decision {
        if isFinal {
            return Decision(accepted: segments, resumeAt: bufferDuration)
        }
        let boundary = bufferDuration - tailMargin
        guard !segments.isEmpty else {
            // 発話なし。末尾の余白分だけ残して先へ進む。
            return Decision(accepted: [], resumeAt: max(boundary, min(minimumAdvance, bufferDuration)))
        }

        if let i = segments.firstIndex(where: { $0.end > boundary }) {
            let cut = segments[i].start
            if cut >= minimumAdvance {
                return Decision(accepted: Array(segments[..<i]), resumeAt: cut)
            }
            // 1 つの区間が長すぎて境界をまたぐ場合は、その区間まで採用して区間の終わりから再開する
            let resume = min(max(segments[i].end, minimumAdvance), bufferDuration)
            return Decision(accepted: Array(segments[...i]), resumeAt: resume)
        }

        // すべての区間が境界より前に終わっている。最後の区間の後ろは無音か、
        // Whisper が拾いきれなかった発話の可能性があるので、そこから読み直す。
        let lastEnd = segments.last!.end
        let resume = lastEnd >= minimumAdvance ? lastEnd : boundary
        return Decision(accepted: segments, resumeAt: resume)
    }
}

/// 音量の小さい位置を探す
public enum SilenceFinder {
    /// range の中で、window 幅の平均二乗振幅が最も小さい区間の中央の位置を返す。
    /// 同じくらい静かな区間が複数あれば、後ろ（チャンクを長く取れる方）を選ぶ。
    public static func quietestPoint(in samples: [Float], range: Range<Int>, window: Int) -> Int {
        let range = range.clamped(to: 0 ..< samples.count)
        let window = max(window, 1)
        guard range.count > window else { return range.isEmpty ? samples.count : range.upperBound }
        let hop = max(window / 2, 1)
        var best = range.upperBound
        var bestEnergy = Float.infinity
        samples.withUnsafeBufferPointer { buf in
            var start = range.lowerBound
            while start + window <= range.upperBound {
                var sum: Float = 0
                for i in start ..< start + window { sum += buf[i] * buf[i] }
                let energy = sum / Float(window)
                if energy <= bestEnergy * 1.0001 {
                    bestEnergy = energy
                    best = start + window / 2
                }
                start += hop
            }
        }
        return best
    }
}
