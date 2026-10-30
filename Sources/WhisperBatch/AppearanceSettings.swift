import AppKit
import Observation
import SwiftUI
import UniformTypeIdentifiers
import WhisperBatchCore

/// 文字の大きさ（アプリ全体に掛ける倍率）
enum FontSizeOption: String, CaseIterable, Identifiable {
    case small, standard, large, extraLarge

    var id: String { rawValue }

    var scale: CGFloat {
        switch self {
        case .small: 0.9
        case .standard: 1
        case .large: 1.15
        case .extraLarge: 1.3
        }
    }

    var label: String {
        switch self {
        case .small: L("settings.fontSize.small")
        case .standard: L("settings.fontSize.standard")
        case .large: L("settings.fontSize.large")
        case .extraLarge: L("settings.fontSize.extraLarge")
        }
    }
}

/// ライト・ダークの切り替え
enum AppearanceMode: String, CaseIterable, Identifiable {
    case system, light, dark

    var id: String { rawValue }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: nil
        case .light: .light
        case .dark: .dark
        }
    }

    var label: String {
        switch self {
        case .system: L("settings.appearance.system")
        case .light: L("settings.appearance.light")
        case .dark: L("settings.appearance.dark")
        }
    }
}

/// ウィンドウの背景
enum BackgroundStyle: String, CaseIterable, Identifiable {
    /// macOS 標準の背景
    case standard
    case color
    case image

    var id: String { rawValue }

    var label: String {
        switch self {
        case .standard: L("settings.background.standard")
        case .color: L("settings.background.color")
        case .image: L("settings.background.image")
        }
    }
}

/// 背景色（sRGB の成分で保存する）
struct ThemeColor: Equatable {
    var red: Double
    var green: Double
    var blue: Double
    var alpha: Double

    static let defaultColor = ThemeColor(red: 0.20, green: 0.32, blue: 0.48, alpha: 1)

    init(red: Double, green: Double, blue: Double, alpha: Double) {
        self.red = red
        self.green = green
        self.blue = blue
        self.alpha = alpha
    }

    init(_ color: Color) {
        let ns = NSColor(color).usingColorSpace(.sRGB) ?? .gray
        self.init(red: ns.redComponent, green: ns.greenComponent, blue: ns.blueComponent, alpha: ns.alphaComponent)
    }

    init?(storage: [Double]?) {
        guard let c = storage, c.count == 4 else { return nil }
        self.init(red: c[0], green: c[1], blue: c[2], alpha: c[3])
    }

    var storage: [Double] { [red, green, blue, alpha] }

    /// 文字を明るくすべき暗い色か（相対輝度で判定。ほぼ透明な色は判定しない）
    var prefersDarkAppearance: Bool? {
        guard alpha >= 0.5 else { return nil }
        func linear(_ c: Double) -> Double { c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4) }
        let luminance = 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue)
        return luminance < 0.18
    }
    var color: Color { Color(.sRGB, red: red, green: green, blue: blue, opacity: alpha) }
}

/// 画面の文字の書体と大きさ。ビューは `.appFont(.callout)` のように文字の種類を指定して使う。
struct AppTypography: Equatable {
    /// 書体のファミリー名（nil ならシステムフォント）
    var family: String?
    var scale: CGFloat

    static let standard = AppTypography(family: nil, scale: 1)

    var isStandard: Bool { family == nil && scale == 1 }

    /// 文字の種類（本文・見出し・注記など）に合わせた大きさで、選んだ書体を返す
    func font(_ style: Font.TextStyle, weight: Font.Weight? = nil) -> Font {
        if isStandard {
            let base = Font.system(style)
            return weight.map { base.weight($0) } ?? base
        }
        let size = (Self.pointSize(for: style) * scale).rounded()
        let resolvedWeight = weight ?? (style == .headline ? .bold : nil)
        guard let family, let ns = Self.nsFont(family: family, weight: resolvedWeight ?? .regular, size: size) else {
            let base = Font.system(size: size)
            return resolvedWeight.map { base.weight($0) } ?? base
        }
        return Font(ns)
    }

    /// AppKit の部品（文字起こし結果の表示）で使う書体
    func nsFont(size: CGFloat) -> NSFont {
        let scaled = (size * scale).rounded()
        if let family, let font = Self.nsFont(family: family, weight: .regular, size: scaled) { return font }
        return NSFont.systemFont(ofSize: scaled)
    }

    /// macOS 標準の文字の種類ごとの大きさ
    static func pointSize(for style: Font.TextStyle) -> CGFloat {
        let ns: NSFont.TextStyle = switch style {
        case .largeTitle: .largeTitle
        case .title: .title1
        case .title2: .title2
        case .title3: .title3
        case .headline: .headline
        case .subheadline: .subheadline
        case .callout: .callout
        case .footnote: .footnote
        case .caption: .caption1
        case .caption2: .caption2
        default: .body
        }
        return NSFont.preferredFont(forTextStyle: ns).pointSize
    }

    /// ファミリーの中から指定の太さに近い書体を探す（なければ標準の太さ）
    static func nsFont(family: String, weight: Font.Weight, size: CGFloat) -> NSFont? {
        let manager = NSFontManager.shared
        return manager.font(withFamily: family, traits: [], weight: managerWeight(weight), size: size)
            ?? manager.font(withFamily: family, traits: [], weight: 5, size: size)
            ?? NSFont(name: family, size: size)
    }

    /// NSFontManager の太さ（0〜15、5 が標準、9 が太字）
    private static func managerWeight(_ weight: Font.Weight) -> Int {
        switch weight {
        case .ultraLight: 2
        case .thin: 3
        case .light: 4
        case .medium: 6
        case .semibold: 8
        case .bold: 9
        case .heavy: 10
        case .black: 11
        default: 5
        }
    }
}

/// 外観の設定（書体・テーマ）。UserDefaults に保存し、背景画像はアプリ専用のフォルダに複製して持つ。
@MainActor
@Observable
final class AppearanceSettings {
    /// 書体のファミリー名（空文字はシステムフォント）
    var fontFamily: String {
        didSet { UserDefaults.standard.set(fontFamily, forKey: Keys.fontFamily) }
    }

    var fontSize: FontSizeOption {
        didSet { UserDefaults.standard.set(fontSize.rawValue, forKey: Keys.fontSize) }
    }

    var appearanceMode: AppearanceMode {
        didSet { UserDefaults.standard.set(appearanceMode.rawValue, forKey: Keys.appearanceMode) }
    }

    var backgroundStyle: BackgroundStyle {
        didSet { UserDefaults.standard.set(backgroundStyle.rawValue, forKey: Keys.backgroundStyle) }
    }

    var backgroundColor: ThemeColor {
        didSet { UserDefaults.standard.set(backgroundColor.storage, forKey: Keys.backgroundColor) }
    }

    /// 背景画像の不透明度（0.05〜1）
    var imageOpacity: Double {
        didSet { UserDefaults.standard.set(imageOpacity, forKey: Keys.imageOpacity) }
    }

    /// 読み込み済みの背景画像
    private(set) var backgroundImage: NSImage?
    /// 背景画像の元のファイル名（設定画面での表示用）
    private(set) var imageDisplayName: String?

    static let imageOpacityRange: ClosedRange<Double> = 0.05...1
    static let defaultImageOpacity = 0.3

    private enum Keys {
        static let fontFamily = "fontFamily"
        static let fontSize = "fontSize"
        static let appearanceMode = "appearanceMode"
        static let backgroundStyle = "backgroundStyle"
        static let backgroundColor = "backgroundColor"
        static let imageOpacity = "backgroundImageOpacity"
        static let imageFile = "backgroundImageFile"
        static let imageDisplayName = "backgroundImageDisplayName"
    }

    init() {
        let defaults = UserDefaults.standard
        let family = defaults.string(forKey: Keys.fontFamily) ?? ""
        // 保存した書体が削除されていたらシステムフォントに戻す
        fontFamily = family.isEmpty || NSFontManager.shared.availableMembers(ofFontFamily: family) != nil ? family : ""
        fontSize = defaults.string(forKey: Keys.fontSize).flatMap(FontSizeOption.init(rawValue:)) ?? .standard
        appearanceMode = defaults.string(forKey: Keys.appearanceMode).flatMap(AppearanceMode.init(rawValue:)) ?? .system
        backgroundStyle = defaults.string(forKey: Keys.backgroundStyle).flatMap(BackgroundStyle.init(rawValue:)) ?? .standard
        backgroundColor = ThemeColor(storage: defaults.array(forKey: Keys.backgroundColor) as? [Double]) ?? .defaultColor
        let opacity = defaults.object(forKey: Keys.imageOpacity) as? Double ?? Self.defaultImageOpacity
        imageOpacity = min(max(opacity, Self.imageOpacityRange.lowerBound), Self.imageOpacityRange.upperBound)
        if let name = defaults.string(forKey: Keys.imageFile), let dir = try? Self.imageDirectory() {
            backgroundImage = NSImage(contentsOf: dir.appendingPathComponent(name))
            imageDisplayName = backgroundImage == nil ? nil : defaults.string(forKey: Keys.imageDisplayName)
        }
    }

    // MARK: 参照

    var typography: AppTypography {
        AppTypography(family: fontFamily.isEmpty ? nil : fontFamily, scale: fontSize.scale)
    }

    /// 画面に使うライト・ダーク。「システムに合わせる」で背景色を使っているときは、色の明るさに合わせて読みやすい方にする。
    var effectiveColorScheme: ColorScheme? {
        if let scheme = appearanceMode.colorScheme { return scheme }
        guard backgroundStyle == .color, let dark = backgroundColor.prefersDarkAppearance else { return nil }
        return dark ? .dark : .light
    }

    /// 標準以外の背景が表示されているか（パネルや本文の背景を透かすかどうかに使う）
    var hasCustomBackground: Bool {
        switch backgroundStyle {
        case .standard: false
        case .color: true
        case .image: backgroundImage != nil
        }
    }

    /// 選べる書体（ファミリー名と、表示言語での名前）
    static func availableFontFamilies() -> [(family: String, displayName: String)] {
        let manager = NSFontManager.shared
        return manager.availableFontFamilies
            .filter { !$0.hasPrefix(".") }
            .map { ($0, manager.localizedName(forFamily: $0, face: nil)) }
            .sorted { $0.1.localizedStandardCompare($1.1) == .orderedAscending }
    }

    // MARK: 背景画像

    /// 画像をアプリ専用のフォルダに複製して背景に設定する。元のファイルを移動・削除しても背景は残る。
    func setBackgroundImage(from source: URL) throws {
        let accessing = source.startAccessingSecurityScopedResource()
        defer { if accessing { source.stopAccessingSecurityScopedResource() } }
        guard let image = NSImage(contentsOf: source), image.isValid else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let dir = try Self.imageDirectory()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let ext = source.pathExtension.isEmpty ? "img" : source.pathExtension.lowercased()
        let fileName = "background-\(UUID().uuidString).\(ext)"
        try FileManager.default.copyItem(at: source, to: dir.appendingPathComponent(fileName))
        removeStoredImage()
        UserDefaults.standard.set(fileName, forKey: Keys.imageFile)
        UserDefaults.standard.set(source.lastPathComponent, forKey: Keys.imageDisplayName)
        backgroundImage = image
        imageDisplayName = source.lastPathComponent
        backgroundStyle = .image
    }

    func clearBackgroundImage() {
        removeStoredImage()
        backgroundImage = nil
        imageDisplayName = nil
    }

    private func removeStoredImage() {
        let defaults = UserDefaults.standard
        if let name = defaults.string(forKey: Keys.imageFile), let dir = try? Self.imageDirectory() {
            try? FileManager.default.removeItem(at: dir.appendingPathComponent(name))
        }
        defaults.removeObject(forKey: Keys.imageFile)
        defaults.removeObject(forKey: Keys.imageDisplayName)
    }

    /// ~/Library/Application Support/WhisperBatch/Background（サンドボックス有効時はコンテナ内）
    private static func imageDirectory() throws -> URL {
        let base = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        return base.appendingPathComponent("WhisperBatch/Background", isDirectory: true)
    }

    /// 設定画面で選べる画像の種類
    static let imageContentTypes: [UTType] = [.image]
}

// MARK: - 画面への反映

private struct AppTypographyKey: EnvironmentKey {
    static let defaultValue = AppTypography.standard
}

private struct CustomBackgroundKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var appTypography: AppTypography {
        get { self[AppTypographyKey.self] }
        set { self[AppTypographyKey.self] = newValue }
    }

    /// テーマで背景を変えているか（true ならパネルや本文の背景を不透明にしない）
    var hasCustomBackground: Bool {
        get { self[CustomBackgroundKey.self] }
        set { self[CustomBackgroundKey.self] = newValue }
    }
}

private struct AppFontModifier: ViewModifier {
    @Environment(\.appTypography) private var typography
    let style: Font.TextStyle
    let weight: Font.Weight?
    let monospacedDigit: Bool

    func body(content: Content) -> some View {
        let font = typography.font(style, weight: weight)
        content.font(monospacedDigit ? font.monospacedDigit() : font)
    }
}

extension View {
    /// 設定で選んだ書体と大きさで文字を表示する（`.font(.callout)` の代わりに使う）
    func appFont(_ style: Font.TextStyle, weight: Font.Weight? = nil, monospacedDigit: Bool = false) -> some View {
        modifier(AppFontModifier(style: style, weight: weight, monospacedDigit: monospacedDigit))
    }
}

/// 書体・ライト／ダークの設定を画面全体に渡す（メインウィンドウと設定画面の両方で使う）
struct AppearanceProvider: ViewModifier {
    let settings: AppearanceSettings
    var appliesBackground = false

    func body(content: Content) -> some View {
        let typography = settings.typography
        content
            .environment(\.appTypography, typography)
            .environment(\.hasCustomBackground, appliesBackground && settings.hasCustomBackground)
            // 文字の種類を指定していない文字にも、選んだ書体を使う
            .font(typography.isStandard ? nil : typography.font(.body))
            .preferredColorScheme(appliesBackground ? settings.effectiveColorScheme : settings.appearanceMode.colorScheme)
    }
}

/// テーマの背景（色、または画像を指定した不透明度で重ねる）
struct ThemeBackground: View {
    let settings: AppearanceSettings

    var body: some View {
        switch settings.backgroundStyle {
        case .standard:
            Color.clear
        case .color:
            settings.backgroundColor.color
        case .image:
            if let image = settings.backgroundImage {
                Color(nsColor: .windowBackgroundColor)
                    .overlay {
                        Image(nsImage: image)
                            .resizable()
                            .interpolation(.high)
                            .scaledToFill()
                            .opacity(settings.imageOpacity)
                    }
                    .clipped()
            } else {
                Color.clear
            }
        }
    }
}
