import Foundation

/// 16kHz モノラルのサンプルを順に読み出せるもの（`AudioSampleReader`。テストでは配列で代用する）
protocol AudioSampleSource {
    /// 最大 `maxFrames` サンプルを読み出す。返る数が `maxFrames` より少なければ終端に達している。
    func read(maxFrames: Int) async throws -> [Float]
}

extension AudioSampleReader: AudioSampleSource {}

/// 音声を決まった長さ（窓）まで読み込み、処理し終えた分を先頭から捨てて、空いた分を読み足すためのバッファ。
///
/// ```
/// var window = AudioStreamBuffer(source: reader, sampleRate: rate, capacity: frames)
/// while true {
///     try await window.fill()
///     // window.samples を処理し、使い終えたサンプル数を決める
///     window.consume(used)
///     if window.isExhausted { break }
/// }
/// ```
/// 1 つのタスクの中で順番に使う（`AudioSampleReader` と同じ前提）。
struct AudioStreamBuffer<Source: AudioSampleSource> {
    let sampleRate: Double
    /// 窓の長さ（サンプル数）。`fill()` はここまで読み込む。
    let capacity: Int
    /// 窓の中のサンプル。先頭はファイル先頭から `startTime` 秒の位置。
    private(set) var samples: [Float] = []
    /// 読み出し元が終端に達した（以降の `fill()` では何も読まない）
    private(set) var isExhausted = false
    /// これまでに捨てたサンプル数（＝窓の先頭の、ファイル先頭からのサンプル位置）
    private(set) var consumedFrames = 0

    private let source: Source

    init(source: Source, sampleRate: Double, capacity: Int) {
        precondition(capacity > 0, "capacity must be positive")
        self.source = source
        self.sampleRate = sampleRate
        self.capacity = capacity
    }

    var count: Int { samples.count }
    var isEmpty: Bool { samples.isEmpty }
    /// 窓に入っている音声の長さ（秒）
    var duration: TimeInterval { Double(samples.count) / sampleRate }
    /// 窓の先頭の、ファイル先頭からの秒数
    var startTime: TimeInterval { Double(consumedFrames) / sampleRate }

    /// 窓の中の位置を、ファイル先頭からの秒数にする
    func time(atFrame frame: Int) -> TimeInterval {
        Double(consumedFrames + frame) / sampleRate
    }

    /// 窓が `capacity` になるまで読み足す。終端に達したら `isExhausted` になる。
    mutating func fill() async throws {
        let need = capacity - samples.count
        guard !isExhausted, need > 0 else { return }
        let more = try await source.read(maxFrames: need)
        if more.count < need { isExhausted = true }
        samples.append(contentsOf: more)
    }

    /// 処理し終えた先頭 `frames` サンプルを捨てる（窓の長さを超える分は無視する）
    mutating func consume(_ frames: Int) {
        let n = min(max(frames, 0), samples.count)
        guard n > 0 else { return }
        samples.removeFirst(n)
        consumedFrames += n
    }
}
