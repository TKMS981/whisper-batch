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

    /// モデルのファイルを置くフォルダ（Whisper は保存先の直下）
    public func directory(for model: SpeechModel) -> URL {
        model.folder.map { directory.appendingPathComponent($0, isDirectory: true) } ?? directory
    }

    public func fileURL(for file: ModelFile, of model: SpeechModel) -> URL {
        directory(for: model).appendingPathComponent(file.name)
    }

    /// 検証前のダウンロード済みファイル
    func partialURL(for file: ModelFile, of model: SpeechModel) -> URL {
        directory(for: model).appendingPathComponent(file.name + ".download")
    }

    /// ファイルが揃っているか（あり、サイズが一致する）。内容の検証はダウンロード直後に SHA-256 で行う。
    public func isInstalled(_ file: ModelFile, of model: SpeechModel) -> Bool {
        let path = fileURL(for: file, of: model).path
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: path),
              let size = (attrs[.size] as? NSNumber)?.int64Value else { return false }
        return size == file.sizeBytes
    }

    /// モデルのファイルがすべて揃っているか（ダウンロードしないモデルは false）
    public func isInstalled(_ model: SpeechModel) -> Bool {
        model.isDownloadable && model.files.allSatisfy { isInstalled($0, of: model) }
    }

    public func delete(_ model: SpeechModel) throws {
        let fm = FileManager.default
        for file in model.files {
            for url in [fileURL(for: file, of: model), partialURL(for: file, of: model)] where fm.fileExists(atPath: url.path) {
                try fm.removeItem(at: url)
            }
        }
        // モデル専用のフォルダが空になったら消す
        if model.folder != nil {
            let dir = directory(for: model)
            if (try? fm.contentsOfDirectory(atPath: dir.path))?.isEmpty == true {
                try? fm.removeItem(at: dir)
            }
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
