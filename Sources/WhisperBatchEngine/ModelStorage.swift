import CryptoKit
import Foundation
import WhisperBatchCore

/// モデルファイルの保存場所と、取得済みかどうかの判定
public struct ModelStorage: Sendable {
    public let directory: URL

    public init(directory: URL) {
        self.directory = directory
    }

    /// 既定の保存先: ~/Library/Application Support/WhisperBatch/Models
    /// （サンドボックス有効時は ~/Library/Containers/<Bundle ID>/Data/Library/Application Support/WhisperBatch/Models）
    public static func defaultStorage() throws -> ModelStorage {
        let base = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        return ModelStorage(directory: base.appendingPathComponent("WhisperBatch/Models", isDirectory: true))
    }

    public func ensureDirectory() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    public func fileURL(for model: WhisperModel) -> URL {
        directory.appendingPathComponent(model.fileName)
    }

    /// 検証前のダウンロード済みファイル
    func partialURL(for model: WhisperModel) -> URL {
        directory.appendingPathComponent(model.fileName + ".download")
    }

    /// 取得済みか（ファイルがあり、サイズが一致する）。内容の検証はダウンロード直後に SHA-256 で行う。
    public func isInstalled(_ model: WhisperModel) -> Bool {
        let path = fileURL(for: model).path
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: path),
              let size = (attrs[.size] as? NSNumber)?.int64Value else { return false }
        return size == model.sizeBytes
    }

    public func delete(_ model: WhisperModel) throws {
        for url in [fileURL(for: model), partialURL(for: model)] where FileManager.default.fileExists(atPath: url.path) {
            try FileManager.default.removeItem(at: url)
        }
    }

    /// 保存先ボリュームの空き容量
    public func availableCapacity() -> Int64? {
        let probe = FileManager.default.fileExists(atPath: directory.path) ? directory : directory.deletingLastPathComponent()
        let values = try? probe.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        return values?.volumeAvailableCapacityForImportantUsage
    }

    /// SHA-256 をファイルを少しずつ読みながら計算する
    @concurrent
    static func sha256(of url: URL) async throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while true {
            try Task.checkCancellation()
            let data = try handle.read(upToCount: 8 * 1024 * 1024) ?? Data()
            if data.isEmpty { break }
            hasher.update(data: data)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}
