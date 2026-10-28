import Foundation

/// 書き出し形式
public enum ExportFormat: String, CaseIterable, Identifiable, Sendable {
    case plainText
    case srt
    case vtt

    public var id: String { rawValue }

    public var fileExtension: String {
        switch self {
        case .plainText: "txt"
        case .srt: "srt"
        case .vtt: "vtt"
        }
    }

    public var displayName: String {
        L("exportFormat.\(rawValue)")
    }
}

/// 統合ファイルに入れる 1 件分
public struct CombinedEntry: Sendable {
    public var title: String
    public var transcript: Transcript
    public init(title: String, transcript: Transcript) {
        self.title = title
        self.transcript = transcript
    }
}

/// 文字起こし結果を各形式の文字列にする。出力には認識結果とファイル名だけを含める。
public enum TranscriptFormatter {
    /// 発話がなかったときの本文（表示言語に従う）
    public static var noSpeechText: String { L("export.noSpeech") }

    // MARK: 単体

    public static func format(_ transcript: Transcript, as format: ExportFormat, includeTimestamps: Bool = false) -> String {
        switch format {
        case .plainText: plainText(transcript, includeTimestamps: includeTimestamps)
        case .srt: srt(transcript)
        case .vtt: vtt(transcript)
        }
    }

    /// 1 区間 1 行のテキスト。末尾は改行で終える。
    public static func plainText(_ transcript: Transcript, includeTimestamps: Bool = false) -> String {
        let lines = cleanedSegments(transcript).map { seg in
            includeTimestamps ? "[\(clock(seg.start))] \(seg.text)" : seg.text
        }
        if lines.isEmpty { return noSpeechText + "\n" }
        return lines.joined(separator: "\n") + "\n"
    }

    public static func srt(_ transcript: Transcript) -> String {
        var out = ""
        for (i, seg) in cleanedSegments(transcript).enumerated() {
            let (start, end) = cueTimes(seg)
            out += "\(i + 1)\n"
            out += "\(timestamp(start, separator: ",")) --> \(timestamp(end, separator: ","))\n"
            out += seg.text + "\n\n"
        }
        return out
    }

    public static func vtt(_ transcript: Transcript) -> String {
        var out = "WEBVTT\n\n"
        for seg in cleanedSegments(transcript) {
            let (start, end) = cueTimes(seg)
            out += "\(timestamp(start, separator: ".")) --> \(timestamp(end, separator: "."))\n"
            // "-->" はキューの区切りと誤認されるため置き換える
            out += seg.text.replacingOccurrences(of: "-->", with: "→") + "\n\n"
        }
        return out
    }

    // MARK: 統合

    /// 複数の結果を 1 つのテキストにまとめる。順序は entries の順。
    public static func combinedPlainText(_ entries: [CombinedEntry], includeTimestamps: Bool = false) -> String {
        let total = entries.count
        let rule = String(repeating: "=", count: 40)
        var blocks: [String] = []
        for (i, entry) in entries.enumerated() {
            var block = "\(rule)\n[\(i + 1)/\(total)] \(entry.title)\n\(rule)\n\n"
            block += plainText(entry.transcript, includeTimestamps: includeTimestamps)
            blocks.append(block)
        }
        return blocks.joined(separator: "\n")
    }

    // MARK: 時刻表記

    /// 00:01:02,345 / 00:01:02.345
    public static func timestamp(_ seconds: TimeInterval, separator: String) -> String {
        let totalMs = Int((max(seconds, 0) * 1000).rounded())
        let h = totalMs / 3_600_000
        let m = (totalMs / 60_000) % 60
        let s = (totalMs / 1000) % 60
        let ms = totalMs % 1000
        return String(format: "%02d:%02d:%02d%@%03d", h, m, s, separator, ms)
    }

    /// 1:02:03 / 02:03（画面・テキスト用）
    public static func clock(_ seconds: TimeInterval) -> String {
        let t = Int(max(seconds, 0).rounded(.down))
        let h = t / 3600, m = (t / 60) % 60, s = t % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%02d:%02d", m, s)
    }

    // MARK: private

    /// 前後の空白を除き、空の区間と改行を整理する
    static func cleanedSegments(_ transcript: Transcript) -> [TranscriptSegment] {
        transcript.segments.compactMap { seg in
            let text = seg.text
                .components(separatedBy: .newlines)
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
                .joined(separator: " ")
            guard !text.isEmpty else { return nil }
            return TranscriptSegment(start: seg.start, end: seg.end, text: text)
        }
    }

    /// 字幕の表示時間が 0 以下にならないようにする
    private static func cueTimes(_ seg: TranscriptSegment) -> (TimeInterval, TimeInterval) {
        let start = max(seg.start, 0)
        let end = max(seg.end, start + 0.5)
        return (start, end)
    }
}
