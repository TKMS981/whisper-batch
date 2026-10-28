import AVFoundation
import Foundation
import WhisperBatchCore

/// AVFoundation で音声を 16kHz モノラル Float32 に変換しながら少しずつ読み出す。
/// ファイル全体をメモリに展開しないので、長時間の音声でも使用メモリは読み出し単位分に収まる。
///
/// 1 つのタスクの中で順番に使う前提のクラス（スレッドをまたいで同時に使わない）。
final class AudioSampleReader {
    static let sampleRate: Double = 16_000

    let duration: TimeInterval
    private let reader: AVAssetReader
    private let provider: AVAssetReaderOutput.Provider<CMReadySampleBuffer<CMSampleBuffer.DynamicContent>>
    private let fileName: String
    private var pending: [Float] = []
    private var pendingOffset = 0
    private var finished = false

    init(url: URL) async throws {
        fileName = url.lastPathComponent
        guard FileManager.default.fileExists(atPath: url.path) else {
            throw WhisperBatchError.fileNotFound(name: fileName)
        }
        guard FileManager.default.isReadableFile(atPath: url.path) else {
            throw WhisperBatchError.fileAccessDenied(name: fileName)
        }
        let asset = AVURLAsset(url: url)
        let tracks: [AVAssetTrack]
        do {
            tracks = try await asset.loadTracks(withMediaType: .audio)
        } catch {
            throw Self.mapOpenError(error, name: fileName)
        }
        guard let track = tracks.first else { throw WhisperBatchError.noAudioTrack(name: fileName) }

        let seconds = (try? await asset.load(.duration)).map { CMTimeGetSeconds($0) } ?? 0
        duration = seconds.isFinite ? max(seconds, 0) : 0

        do {
            reader = try AVAssetReader(asset: asset)
        } catch {
            throw Self.mapOpenError(error, name: fileName)
        }
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: Self.sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false,
        ]
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: settings)
        guard reader.canAdd(output) else {
            throw WhisperBatchError.unsupportedFormat(name: fileName)
        }
        provider = reader.outputProvider(for: output)
        do {
            try reader.start()
        } catch {
            throw WhisperBatchError.audioDecodingFailed(name: fileName, detail: Self.describe(error))
        }
    }

    deinit {
        if reader.status == .reading { reader.cancelReading() }
    }

    /// 最大 `maxFrames` サンプルを読み出す。返る数が `maxFrames` より少なければ終端に達している。
    func read(maxFrames: Int) async throws -> [Float] {
        var result: [Float] = []
        result.reserveCapacity(maxFrames)
        while result.count < maxFrames {
            if pendingOffset < pending.count {
                let take = min(maxFrames - result.count, pending.count - pendingOffset)
                result.append(contentsOf: pending[pendingOffset ..< pendingOffset + take])
                pendingOffset += take
                continue
            }
            guard !finished else { break }
            try await fillPending()
        }
        return result
    }

    private func fillPending() async throws {
        pending.removeAll(keepingCapacity: true)
        pendingOffset = 0
        let next: CMReadySampleBuffer<CMSampleBuffer.DynamicContent>?
        do {
            // next() は内部でデコード完了を待ってスレッドをブロックするため、専用キューで実行する
            next = try await withTaskExecutorPreference(BlockingWorkExecutor.shared) {
                try await provider.next()
            }
        } catch {
            finished = true
            throw WhisperBatchError.audioDecodingFailed(name: fileName, detail: Self.describe(error))
        }
        guard let next else {
            finished = true
            if reader.status == .failed {
                throw WhisperBatchError.audioDecodingFailed(name: fileName, detail: Self.describe(reader.error))
            }
            return
        }
        let samples: [Float]? = next.withUnsafeSampleBuffer { buffer in
            guard let block = CMSampleBufferGetDataBuffer(buffer) else { return [] }
            let count = CMBlockBufferGetDataLength(block) / MemoryLayout<Float>.size
            guard count > 0 else { return [] }
            var out = [Float](repeating: 0, count: count)
            let status = out.withUnsafeMutableBytes { raw in
                CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: count * MemoryLayout<Float>.size, destination: raw.baseAddress!)
            }
            return status == kCMBlockBufferNoErr ? out : nil
        }
        guard let samples else {
            throw WhisperBatchError.audioDecodingFailed(name: fileName, detail: L("error.detail.noSampleData"))
        }
        pending = samples
    }

    static func mapOpenError(_ error: any Error, name: String) -> WhisperBatchError {
        let ns = error as NSError
        if ns.domain == NSCocoaErrorDomain && (ns.code == NSFileReadNoPermissionError || ns.code == NSFileReadNoSuchFileError) {
            return ns.code == NSFileReadNoSuchFileError ? .fileNotFound(name: name) : .fileAccessDenied(name: name)
        }
        if ns.domain == NSPOSIXErrorDomain && ns.code == Int(EPERM) {
            return .fileAccessDenied(name: name)
        }
        if ns.domain == AVFoundationErrorDomain {
            switch AVError.Code(rawValue: ns.code) {
            case .fileFormatNotRecognized, .decoderNotFound, .formatUnsupported, .contentIsUnavailable, .failedToParse:
                return .unsupportedFormat(name: name)
            default:
                break
            }
        }
        return .audioDecodingFailed(name: name, detail: describe(error))
    }

    static func describe(_ error: (any Error)?) -> String {
        guard let error else { return L("error.detail.unknown") }
        let ns = error as NSError
        if let reason = ns.localizedFailureReason, !reason.isEmpty { return reason }
        return ns.localizedDescription
    }
}
