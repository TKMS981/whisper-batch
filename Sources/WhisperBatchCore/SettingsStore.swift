import Foundation
import Observation

/// 表示言語の設定（文字起こしの言語とは別）
public enum LanguagePreference: String, CaseIterable, Identifiable, Sendable {
    /// macOS の優先言語に合わせる
    case system
    case japanese = "ja"
    case english = "en"

    public var id: String { rawValue }

    /// macOS 全体の優先言語。このアプリ用の言語指定（AppleLanguages）の影響を受けないよう、全体の設定を直接読む。
    public static var systemLanguages: [String] {
        let value = CFPreferencesCopyValue("AppleLanguages" as CFString, kCFPreferencesAnyApplication,
                                           kCFPreferencesCurrentUser, kCFPreferencesAnyHost)
        return (value as? [String]) ?? Locale.preferredLanguages
    }

    public var resolved: AppLanguage {
        switch self {
        case .system: AppLanguage.preferred(from: Self.systemLanguages)
        case .japanese: .japanese
        case .english: .english
        }
    }
}

/// 利用者の設定（文字起こしの言語・モデル・Whisper の探索方式・表示言語・自動開始）を UserDefaults に保存する。
///
/// 値を変えるとすぐに保存する。値の変化に応じた画面側の処理（表示言語の切り替えなど）は持たない。
@MainActor
@Observable
public final class SettingsStore {
    /// 文字起こしの言語
    public var language: LanguageOption {
        didSet { defaults.set(language.id, forKey: Keys.language) }
    }

    public var selectedModelID: String {
        didSet {
            defaults.set(selectedModelID, forKey: Keys.model)
            // 方式を切り替えて戻ったときに、前に選んでいたモデルに戻せるようにする
            if let engine = ModelCatalog.model(id: selectedModelID)?.engine {
                defaults.set(selectedModelID, forKey: Keys.model(for: engine))
            }
        }
    }

    /// 表示言語の設定
    public var languagePreference: LanguagePreference {
        didSet { saveLanguagePreference() }
    }

    /// ファイルを追加したらすぐに文字起こしを始める（既定はオフ）
    public var autoStartOnAdd: Bool {
        didSet { defaults.set(autoStartOnAdd, forKey: Keys.autoStartOnAdd) }
    }

    /// Whisper の探索方式（速度優先／精度優先）
    public var whisperDecoding: WhisperDecoding {
        didSet { defaults.set(whisperDecoding.rawValue, forKey: Keys.whisperDecoding) }
    }

    @ObservationIgnored private let defaults: UserDefaults

    enum Keys {
        static let language = "language"
        static let model = "modelID"
        static func model(for engine: SpeechEngine) -> String { "modelID.\(engine.rawValue)" }
        static let appLanguage = "appLanguage"
        static let autoStartOnAdd = "autoStartOnAdd"
        static let whisperDecoding = "whisperDecoding"
        /// AppKit が読む、このアプリだけの言語指定（標準メニューやパネルの言語。次回起動時に反映）
        static let appleLanguages = "AppleLanguages"
    }

    /// - Parameter defaults: 保存先（テストでは専用のスイートを渡す）
    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        language = LanguageOption(storageValue: defaults.string(forKey: Keys.language))
        selectedModelID = defaults.string(forKey: Keys.model).flatMap { ModelCatalog.model(id: $0)?.id }
            ?? ModelCatalog.defaultModelID
        languagePreference = defaults.string(forKey: Keys.appLanguage).flatMap(LanguagePreference.init(rawValue:)) ?? .system
        autoStartOnAdd = defaults.bool(forKey: Keys.autoStartOnAdd)
        whisperDecoding = defaults.string(forKey: Keys.whisperDecoding).flatMap(WhisperDecoding.init(rawValue:)) ?? .default
    }

    public var selectedModel: SpeechModel {
        ModelCatalog.model(id: selectedModelID) ?? ModelCatalog.defaultModel
    }

    /// その方式で最後に選んでいたモデル（なければ nil）
    public func lastSelectedModel(for engine: SpeechEngine) -> SpeechModel? {
        defaults.string(forKey: Keys.model(for: engine))
            .flatMap(ModelCatalog.model(id:))
            .flatMap { $0.engine == engine ? $0 : nil }
    }

    private func saveLanguagePreference() {
        defaults.set(languagePreference.rawValue, forKey: Keys.appLanguage)
        if languagePreference == .system {
            defaults.removeObject(forKey: Keys.appleLanguages)
        } else {
            defaults.set([languagePreference.rawValue], forKey: Keys.appleLanguages)
        }
    }
}
