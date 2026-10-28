import AVFoundation
import CLAME
import Foundation
import WhisperBatchCore

/// 動画などの音声トラックを取り出し、MP3 に変換する。
///
/// - 読み込みとデコード: AVFoundation（AVAssetReader）
/// - MP3 エンコード: LAME（macOS には MP3 エンコーダーがないため）
/// どちらもアプリのプロセス内で動き、外部コマンドは実行しない。
public enum MP3Converter {
    /// 128 kbps 固定ビットレート、44.1 kHz。音声の文字起こしと聞き返しに十分な品質。
    public static let bitrateKbps: Int32 = 128
    public static let sampleRate: Double = 44_100

    /// 変換後の MP3 の大きさの目安（バイト）
    public static func estimatedSize(duration: TimeInterval) -> Int64 {
        Int64(duration * Double(bitrateKbps) * 1000 / 8 * 1.05) + 1_000_000
    }

    /// `source` の最初の音声トラックを MP3 にして `destination` に書き出す。
    /// 失敗・キャンセル時は書きかけの `destination` を削除する。元のファイルは変更しない。
    /// - Parameter progress: 0...1。任意のスレッドから呼ばれる。
    @concurrent
    public static func convert(
        source: URL,
        destination: URL,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        let name = source.lastPathComponent
        guard FileManager.default.fileExists(atPath: source.path) else {
            throw WhisperBatchError.fileNotFound(name: name)
        }
        let asset = AVURLAsset(url: source)
        let tracks: [AVAssetTrack]
        let isVideo: Bool
        do {
            tracks = try await asset.loadTracks(withMediaType: .audio)
            isVideo = !(try await asset.loadTracks(withMediaType: .video)).isEmpty
        } catch {
            throw AudioSampleReader.mapOpenError(error, name: name)
        }
        guard let track = tracks.first else {
            throw isVideo ? WhisperBatchError.videoHasNoAudio(name: name) : WhisperBatchError.noAudioTrack(name: name)
        }
        let seconds = (try? await asset.load(.duration)).map { CMTimeGetSeconds($0) } ?? 0
        let duration = seconds.isFinite ? max(seconds, 0) : 0

        // 出力先の空き容量を確認する
        let required = estimatedSize(duration: duration)
        let folder = destination.deletingLastPathComponent()
        if let available = try? folder.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
            .volumeAvailableCapacityForImportantUsage, available < required {
            throw WhisperBatchError.insufficientDiskSpace(required: required, available: available)
        }

        // ステレオ以上は 2ch、モノラルは 1ch のまま
        let formats = (try? await track.load(.formatDescriptions)) ?? []
        let sourceChannels = formats.first.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame } ?? 2
        let channels = sourceChannels >= 2 ? 2 : 1

        let reader: AVAssetReader
        do {
            reader = try AVAssetReader(asset: asset)
        } catch {
            throw AudioSampleReader.mapOpenError(error, name: name)
        }
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: channels,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false,
        ]
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: settings)
        guard reader.canAdd(output) else { throw WhisperBatchError.unsupportedFormat(name: name) }
        let provider = reader.outputProvider(for: output)
        do {
            try reader.start()
        } catch {
            throw WhisperBatchError.conversionFailed(name: name, detail: AudioSampleReader.describe(error))
        }

        let encoder = try LameEncoder(channels: Int32(channels), sampleRate: Int32(sampleRate), bitrateKbps: bitrateKbps, name: name)

        guard FileManager.default.createFile(atPath: destination.path, contents: nil) else {
            throw WhisperBatchError.conversionFailed(name: name, detail: L("convert.detail.cannotCreateFile"))
        }
        var succeeded = false
        let handle: FileHandle
        do {
            handle = try FileHandle(forWritingTo: destination)
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw WhisperBatchError.conversionFailed(name: name, detail: error.localizedDescription)
        }
        defer {
            try? handle.close()
            if !succeeded { try? FileManager.default.removeItem(at: destination) }
        }

        func write(_ data: Data) throws {
            do {
                try handle.write(contentsOf: data)
            } catch {
                let ns = error as NSError
                if ns.domain == NSPOSIXErrorDomain && ns.code == Int(ENOSPC) || ns.code == NSFileWriteOutOfSpaceError {
                    throw WhisperBatchError.insufficientDiskSpace(required: required, available: 0)
                }
                throw WhisperBatchError.conversionFailed(name: name, detail: ns.localizedDescription)
            }
        }

        progress(0)
        while true {
            try Task.checkCancellation()
            let next: CMReadySampleBuffer<CMSampleBuffer.DynamicContent>?
            do {
                // next() はデコード完了まで呼び出しスレッドをブロックするため、専用キューで実行する
                next = try await withTaskExecutorPreference(BlockingWorkExecutor.shared) {
                    try await provider.next()
                }
            } catch {
                throw WhisperBatchError.conversionFailed(name: name, detail: AudioSampleReader.describe(error))
            }
            guard let next else { break }
            let (samples, time): ([Float], Double) = next.withUnsafeSampleBuffer { buffer in
                let pts = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(buffer))
                guard let block = CMSampleBufferGetDataBuffer(buffer) else { return ([], pts) }
                let count = CMBlockBufferGetDataLength(block) / MemoryLayout<Float>.size
                guard count > 0 else { return ([], pts) }
                var out = [Float](repeating: 0, count: count)
                let status = out.withUnsafeMutableBytes { raw in
                    CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: count * MemoryLayout<Float>.size, destination: raw.baseAddress!)
                }
                return (status == kCMBlockBufferNoErr ? out : [], pts)
            }
            if !samples.isEmpty {
                try write(try encoder.encode(interleaved: samples))
            }
            if duration > 0, time.isFinite {
                progress(min(time / duration, 0.999))
            }
        }
        if reader.status == .failed {
            throw WhisperBatchError.conversionFailed(name: name, detail: AudioSampleReader.describe(reader.error))
        }
        try write(try encoder.flush())
        succeeded = true
        progress(1)
    }
}

/// LAME の薄いラッパー（1 つのタスク内で順番に使う）
private final class LameEncoder {
    private let gfp: OpaquePointer
    private let channels: Int32
    private let name: String
    private var buffer: [UInt8] = []

    init(channels: Int32, sampleRate: Int32, bitrateKbps: Int32, name: String) throws {
        guard let gfp = lame_init() else {
            throw WhisperBatchError.conversionFailed(name: name, detail: L("convert.detail.encoderInit"))
        }
        self.gfp = gfp
        self.channels = channels
        self.name = name
        lame_set_num_channels(gfp, channels)
        lame_set_in_samplerate(gfp, sampleRate)
        lame_set_out_samplerate(gfp, sampleRate)
        lame_set_brate(gfp, bitrateKbps)
        lame_set_mode(gfp, channels == 1 ? MONO : JOINT_STEREO)
        lame_set_quality(gfp, 5)
        // 先頭に空の情報フレームを書かない（後から書き戻さないため）
        lame_set_bWriteVbrTag(gfp, 0)
        guard lame_init_params(gfp) >= 0 else {
            lame_close(gfp)
            throw WhisperBatchError.conversionFailed(name: name, detail: L("convert.detail.encoderInit"))
        }
    }

    deinit {
        lame_close(gfp)
    }

    /// 交互に並んだ（インターリーブ）-1...1 の Float サンプルをエンコードする
    func encode(interleaved samples: [Float]) throws -> Data {
        let frames = samples.count / Int(channels)
        guard frames > 0 else { return Data() }
        let capacity = Int(1.25 * Double(frames)) + 7200
        if buffer.count < capacity { buffer = [UInt8](repeating: 0, count: capacity) }
        let written = samples.withUnsafeBufferPointer { pcm in
            buffer.withUnsafeMutableBufferPointer { out in
                if channels == 1 {
                    // モノラルは左右に同じ配列を渡す
                    return lame_encode_buffer_ieee_float(gfp, pcm.baseAddress, pcm.baseAddress, Int32(frames),
                                                         out.baseAddress, Int32(out.count))
                }
                return lame_encode_buffer_interleaved_ieee_float(gfp, pcm.baseAddress, Int32(frames),
                                                                 out.baseAddress, Int32(out.count))
            }
        }
        guard written >= 0 else {
            throw WhisperBatchError.conversionFailed(name: name, detail: L("convert.detail.encoderError", Int(written)))
        }
        return Data(buffer[0 ..< Int(written)])
    }

    func flush() throws -> Data {
        if buffer.count < 7200 { buffer = [UInt8](repeating: 0, count: 7200) }
        let written = buffer.withUnsafeMutableBufferPointer { out in
            lame_encode_flush(gfp, out.baseAddress, Int32(out.count))
        }
        guard written >= 0 else {
            throw WhisperBatchError.conversionFailed(name: name, detail: L("convert.detail.encoderError", Int(written)))
        }
        return Data(buffer[0 ..< Int(written)])
    }
}
