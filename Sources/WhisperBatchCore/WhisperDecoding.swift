/// Whisper の探索方式（デコード方式）
public enum WhisperDecoding: String, CaseIterable, Identifiable, Sendable {
    /// 速度優先: 各時点で最も確からしい語だけを選ぶ（Greedy）
    case greedy
    /// 精度優先: 複数の候補（ビーム幅 5）を並行して比べる（Beam Search）。従来からの既定。
    case beamSearch

    public static let `default`: WhisperDecoding = .beamSearch

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .greedy: L("decoding.greedy")
        case .beamSearch: L("decoding.beamSearch")
        }
    }
}
