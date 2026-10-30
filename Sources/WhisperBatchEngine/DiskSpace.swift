import Foundation
import WhisperBatchCore

/// 書き込み前の空き容量の確認
public enum DiskSpace {
    /// `folder` のあるボリュームの空き容量（バイト）。調べられなければ nil。
    public static func available(at folder: URL) -> Int64? {
        let values = try? folder.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        return values?.volumeAvailableCapacityForImportantUsage
    }

    /// `folders` のそれぞれに `required` バイト書き込めるか確かめる。
    /// 同じボリュームにあるフォルダは 1 回だけ数える（一時ファイルを同じボリュームの保存先へ移すときは容量が増えないため）。
    /// 空き容量を調べられないフォルダは確認しない（書き込み時のエラーで知らせる）。
    /// - Throws: `WhisperBatchError.insufficientDiskSpace`
    public static func ensure(_ required: Int64, availableIn folders: [URL]) throws {
        var checkedVolumes: [AnyHashable] = []
        for folder in folders {
            let volume = (try? folder.resourceValues(forKeys: [.volumeIdentifierKey]))?.volumeIdentifier as? AnyHashable
            if let volume {
                if checkedVolumes.contains(volume) { continue }
                checkedVolumes.append(volume)
            }
            if let available = available(at: folder), available < required {
                throw WhisperBatchError.insufficientDiskSpace(required: required, available: available)
            }
        }
    }
}
