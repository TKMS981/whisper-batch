import Foundation

/// 認識結果の 1 区間。時刻は音声ファイル先頭からの秒数。
public struct TranscriptSegment: Equatable, Sendable, Codable {
    public var start: TimeInterval
    public var end: TimeInterval
    public var text: String

    public init(start: TimeInterval, end: TimeInterval, text: String) {
        self.start = start
        self.end = end
        self.text = text
    }
}

public struct Transcript: Equatable, Sendable, Codable {
    public var segments: [TranscriptSegment]
    /// 認識に使った言語コード（例: "ja"）。自動判定時は判定結果。
    public var language: String?

    public init(segments: [TranscriptSegment], language: String?) {
        self.segments = segments
        self.language = language
    }

    public var isEmpty: Bool {
        segments.allSatisfy { $0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    }
}

/// 文字起こしの言語設定（アプリの表示言語 `AppLanguage` とは別）
public enum LanguageOption: Hashable, Sendable, CaseIterable, Identifiable {
    case auto
    case japanese
    case english
    case chinese
    case korean
    case spanish
    case french
    case german

    public var id: String { code ?? "auto" }

    /// whisper.cpp に渡す言語コード。nil は自動判定。
    public var code: String? {
        switch self {
        case .auto: nil
        case .japanese: "ja"
        case .english: "en"
        case .chinese: "zh"
        case .korean: "ko"
        case .spanish: "es"
        case .french: "fr"
        case .german: "de"
        }
    }

    /// 表示名（表示言語に従う）
    public var displayName: String {
        L("whisperLanguage.\(id)")
    }

    public init(storageValue: String?) {
        self = Self.allCases.first { $0.id == storageValue } ?? .japanese
    }

    /// 判定された言語コードの表示名（一覧にない言語はコードをそのまま表示）
    public static func displayName(forCode code: String) -> String {
        allCases.first { $0.code == code }?.displayName ?? code
    }
}
