import Foundation
import Synchronization

/// アプリの表示言語（文字起こしの言語とは別の設定）
public enum AppLanguage: String, CaseIterable, Sendable, Identifiable {
    case japanese = "ja"
    case english = "en"

    public var id: String { rawValue }

    /// macOS の優先言語から選ぶ。日本語と英語のうち先に現れた方。どちらもなければ英語。
    public static func preferred(from languages: [String] = Locale.preferredLanguages) -> AppLanguage {
        for code in languages {
            let lower = code.lowercased()
            if lower.hasPrefix("ja") { return .japanese }
            if lower.hasPrefix("en") { return .english }
        }
        return .english
    }

    /// 言語名はその言語自身で表示する（切り替え先が読めるように）
    public var nativeName: String {
        switch self {
        case .japanese: "日本語"
        case .english: "English"
        }
    }

    public var locale: Locale { Locale(identifier: rawValue) }
}

/// 表示文字列の読み込み。
///
/// 文字列は `Localization/<言語>.lproj/Localizable.strings` にまとめ、アプリの Resources に同梱する。
/// 実行中に表示言語を切り替えられるよう、言語ごとの表を読み込んで自前で引く。
public final class Localizer: Sendable {
    public static let shared = Localizer()

    private struct State {
        var language: AppLanguage = .preferred()
        var tables: [AppLanguage: [String: String]] = [:]
        var loaded = false
    }

    private let state = Mutex(State())

    public init() {}

    public var language: AppLanguage {
        state.withLock { $0.language }
    }

    public func setLanguage(_ language: AppLanguage) {
        state.withLock { $0.language = language }
    }

    /// `<directory>/<言語>.lproj/Localizable.strings` を読み込む（テストや開発時用）
    public func loadTables(fromDirectory directory: URL) {
        var tables: [AppLanguage: [String: String]] = [:]
        for language in AppLanguage.allCases {
            let url = directory.appendingPathComponent("\(language.rawValue).lproj/Localizable.strings")
            if let dict = NSDictionary(contentsOf: url) as? [String: String] {
                tables[language] = dict
            }
        }
        state.withLock {
            $0.tables = tables
            $0.loaded = true
        }
    }

    /// 現在の表の内容（テスト用）
    public func table(for language: AppLanguage) -> [String: String] {
        ensureLoaded()
        return state.withLock { $0.tables[language] ?? [:] }
    }

    public func string(_ key: String) -> String {
        ensureLoaded()
        return state.withLock { s in
            s.tables[s.language]?[key] ?? s.tables[.english]?[key] ?? key
        }
    }

    private func ensureLoaded() {
        let loaded = state.withLock { $0.loaded }
        guard !loaded else { return }
        // アプリでは Contents/Resources、開発時は環境変数で指定したフォルダから読む
        if let dir = ProcessInfo.processInfo.environment["WHISPER_BATCH_LOCALIZATION_DIR"] {
            loadTables(fromDirectory: URL(fileURLWithPath: dir))
        } else if let resources = Bundle.main.resourceURL {
            loadTables(fromDirectory: resources)
        } else {
            state.withLock { $0.loaded = true }
        }
    }
}

/// 表示文字列を取り出す。`args` があれば `String(format:)` で埋め込む（%@, %d, %1$@ など）。
public func L(_ key: String, _ args: any CVarArg...) -> String {
    let template = Localizer.shared.string(key)
    guard !args.isEmpty else { return template }
    return String(format: template, locale: Localizer.shared.language.locale, arguments: args)
}
