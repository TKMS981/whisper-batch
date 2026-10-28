import Foundation
import WhisperBatchCore

/// サンドボックス下のファイルアクセス。
///
/// - 追加時: 選択・ドロップされた URL へのアクセスを一時的に開始し、その間にセキュリティスコープ付き
///   ブックマークを作る（フォルダの場合は中のファイルごとに作る）。作り終えたらアクセスを解放する。
/// - 処理時: ブックマークから URL を復元し、処理している間だけアクセスを開始し、終わったら解放する。
public enum SecurityScopedAccess {
    /// 選択・ドロップされた URL を追加候補にする。フォルダは中の対応ファイル（サブフォルダを含む、名前順）に展開する。
    @concurrent
    public static func prepareCandidates(_ urls: [URL]) async -> [FileCandidate] {
        var result: [FileCandidate] = []
        for url in urls {
            let started = url.startAccessingSecurityScopedResource()
            defer { if started { url.stopAccessingSecurityScopedResource() } }

            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue {
                result += audioFiles(in: url).map { FileCandidate(url: $0, bookmark: bookmark(for: $0)) }
            } else {
                result.append(FileCandidate(url: url, bookmark: bookmark(for: url)))
            }
        }
        return result
    }

    /// ブックマーク（なければ元の URL）でアクセスを開始し、body の実行後に必ず解放する
    nonisolated(nonsending)
    public static func withAccess<T>(url: URL, bookmark: Data?, _ body: (URL) async throws -> T) async throws -> T {
        var target = url
        if let bookmark {
            var stale = false
            if let resolved = try? URL(resolvingBookmarkData: bookmark, options: [.withSecurityScope],
                                       relativeTo: nil, bookmarkDataIsStale: &stale) {
                target = resolved
            }
        }
        let started = target.startAccessingSecurityScopedResource()
        defer { if started { target.stopAccessingSecurityScopedResource() } }
        return try await body(target)
    }

    // MARK: private

    /// アクセス中の URL から読み取り専用のブックマークを作る。作れない場合は nil（非サンドボックス環境では URL だけで読める）。
    private static func bookmark(for url: URL) -> Data? {
        try? url.bookmarkData(options: [.withSecurityScope, .securityScopeAllowOnlyReadAccess],
                              includingResourceValuesForKeys: nil, relativeTo: nil)
    }

    private static func audioFiles(in folder: URL) -> [URL] {
        let enumerator = FileManager.default.enumerator(
            at: folder, includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles, .skipsPackageDescendants])
        var files: [URL] = []
        while let child = enumerator?.nextObject() as? URL {
            if (try? child.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true,
               AudioFormats.check(child) == .accepted {
                files.append(child)
            }
        }
        return files.sorted { $0.path.localizedStandardCompare($1.path) == .orderedAscending }
    }
}
