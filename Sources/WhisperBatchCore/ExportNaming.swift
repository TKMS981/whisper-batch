import Foundation

/// 個別書き出しのファイル名を決める
public enum ExportNaming {
    /// 既存ファイルとぶつかったときの扱い
    public enum ConflictPolicy: Sendable {
        case replace
        case keepBoth
    }

    public struct Plan: Equatable, Sendable {
        public var fileNames: [String]
        /// 既存ファイルを置き換えるもの
        public var replacing: [String]
    }

    /// 音声ファイル名から書き出し名を作る（"会議.m4a" → "会議.txt"）
    public static func baseName(for audioFileName: String, format: ExportFormat) -> String {
        let stem = (audioFileName as NSString).deletingPathExtension
        let safe = stem.isEmpty ? "transcript" : stem
        return "\(safe).\(format.fileExtension)"
    }

    /// 既存ファイル名と、今回書き出す名前同士の衝突を解決する。
    /// 同じ名前が今回の中で重なる場合は常に連番を付ける（別の音声の結果で上書きしないため）。
    public static func plan(audioFileNames: [String], format: ExportFormat, existing: Set<String>, policy: ConflictPolicy) -> Plan {
        var used = Set<String>()
        var names: [String] = []
        var replacing: [String] = []
        let existingLower = Set(existing.map { $0.lowercased() })
        for audio in audioFileNames {
            let base = baseName(for: audio, format: format)
            var candidate = base
            var n = 2
            func taken(_ name: String) -> Bool {
                if used.contains(name.lowercased()) { return true }
                if policy == .keepBoth && existingLower.contains(name.lowercased()) { return true }
                return false
            }
            while taken(candidate) {
                let stem = (base as NSString).deletingPathExtension
                candidate = "\(stem) (\(n)).\(format.fileExtension)"
                n += 1
            }
            used.insert(candidate.lowercased())
            names.append(candidate)
            if existingLower.contains(candidate.lowercased()) { replacing.append(candidate) }
        }
        return Plan(fileNames: names, replacing: replacing)
    }

    /// 統合ファイルの既定名
    public static func combinedFileName(date: Date = Date()) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd_HHmm"
        return L("export.combinedFileName", f.string(from: date))
    }
}
