import SwiftUI
import WhisperBatchCore

/// 設定画面（⌘,）: 一般（表示言語・自動開始）、フォント、テーマ
struct SettingsView: View {
    private enum Tab: Hashable { case general, font, theme }
    @ViewState private var tab = Tab.general

    var body: some View {
        TabView(selection: $tab) {
            GeneralSettingsTab()
                .tabItem { Label(L("settings.tab.general"), systemImage: "gearshape") }
                .tag(Tab.general)
            FontSettingsTab()
                .tabItem { Label(L("settings.tab.font"), systemImage: "textformat") }
                .tag(Tab.font)
            ThemeSettingsTab()
                .tabItem { Label(L("settings.tab.theme"), systemImage: "paintpalette") }
                .tag(Tab.theme)
        }
        .frame(width: 520)
    }
}

/// 説明文（設定項目の下に小さく表示する）
private struct SettingNote: View {
    let text: String

    var body: some View {
        Text(text)
            .appFont(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: 360, alignment: .leading)
    }
}

// MARK: - 一般

private struct GeneralSettingsTab: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Section {
                Picker(L("settings.appLanguage"), selection: $model.languagePreference) {
                    Text(L("settings.appLanguage.system", LanguagePreference.system.resolved.nativeName)).tag(LanguagePreference.system)
                    Text(AppLanguage.japanese.nativeName).tag(LanguagePreference.japanese)
                    Text(AppLanguage.english.nativeName).tag(LanguagePreference.english)
                }
                .pickerStyle(.radioGroup)
                SettingNote(text: L("settings.appLanguage.note"))
            }
            Section {
                LabeledContent(L("settings.autoStart")) {
                    VStack(alignment: .leading, spacing: 4) {
                        Toggle(L("settings.autoStart.toggle"), isOn: $model.autoStartOnAdd)
                            .toggleStyle(.checkbox)
                        SettingNote(text: L("settings.autoStart.note"))
                    }
                }
                .padding(.top, 10)
            }
        }
        .padding(20)
    }
}

// MARK: - フォント

private struct FontSettingsTab: View {
    @Environment(AppModel.self) private var model
    @Environment(\.appTypography) private var typography

    /// 書体の一覧は起動中に 1 回だけ作る（数百件あるため）
    private static let families = AppearanceSettings.availableFontFamilies()

    var body: some View {
        @Bindable var appearance = model.appearance
        Form {
            Picker(L("settings.font.family"), selection: $appearance.fontFamily) {
                Text(L("settings.font.system")).tag("")
                Divider()
                ForEach(Self.families, id: \.family) { item in
                    Text(item.displayName).tag(item.family)
                }
            }
            Picker(L("settings.font.size"), selection: $appearance.fontSize) {
                ForEach(FontSizeOption.allCases) { option in
                    Text(option.label).tag(option)
                }
            }
            .pickerStyle(.segmented)
            .frame(maxWidth: 360)

            LabeledContent(L("settings.font.preview")) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(L("settings.font.preview.title"))
                        .appFont(.headline)
                    Text(L("settings.font.preview.body"))
                        .fixedSize(horizontal: false, vertical: true)
                    Text(L("settings.font.preview.caption"))
                        .appFont(.caption)
                        .foregroundStyle(.secondary)
                }
                .padding(10)
                .frame(maxWidth: 360, alignment: .leading)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
            }
            SettingNote(text: L("settings.font.note"))

            HStack {
                Spacer()
                Button(L("settings.resetDefaults")) {
                    appearance.fontFamily = ""
                    appearance.fontSize = .standard
                }
                .disabled(typography.isStandard)
            }
        }
        .padding(20)
    }
}

// MARK: - テーマ

private struct ThemeSettingsTab: View {
    @Environment(AppModel.self) private var model
    @ViewState private var showImageImporter = false
    @ViewState private var imageError: String?

    var body: some View {
        @Bindable var appearance = model.appearance
        Form {
            Picker(L("settings.appearance"), selection: $appearance.appearanceMode) {
                ForEach(AppearanceMode.allCases) { mode in
                    Text(mode.label).tag(mode)
                }
            }
            .pickerStyle(.segmented)
            .frame(maxWidth: 320)

            Picker(L("settings.background"), selection: $appearance.backgroundStyle) {
                ForEach(BackgroundStyle.allCases) { style in
                    Text(style.label).tag(style)
                }
            }
            .pickerStyle(.segmented)
            .frame(maxWidth: 320)

            switch appearance.backgroundStyle {
            case .standard:
                EmptyView()
            case .color:
                LabeledContent(L("settings.background.colorPicker")) {
                    ColorPicker(L("settings.background.colorPicker"), selection: Binding(
                        get: { appearance.backgroundColor.color },
                        set: { appearance.backgroundColor = ThemeColor($0) }
                    ))
                    .labelsHidden()
                    .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 5 }
                }
                SettingNote(text: L("settings.background.color.note"))
            case .image:
                LabeledContent(L("settings.background.imageFile")) {
                    HStack(spacing: 8) {
                        Text(appearance.imageDisplayName ?? L("settings.background.noImage"))
                            .foregroundStyle(appearance.imageDisplayName == nil ? .secondary : .primary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .frame(maxWidth: 180, alignment: .leading)
                        Button(L("settings.background.chooseImage")) { showImageImporter = true }
                        Button(L("settings.background.removeImage")) { appearance.clearBackgroundImage() }
                            .disabled(appearance.backgroundImage == nil)
                    }
                }
                LabeledContent(L("settings.background.opacity")) {
                    HStack(spacing: 8) {
                        Slider(value: $appearance.imageOpacity, in: AppearanceSettings.imageOpacityRange)
                            .frame(maxWidth: 220)
                            .accessibilityValue(L("common.percent", Int((appearance.imageOpacity * 100).rounded())))
                        Text(L("common.percentShort", Int((appearance.imageOpacity * 100).rounded())))
                            .appFont(.body, monospacedDigit: true)
                            .frame(width: 44, alignment: .trailing)
                    }
                    .disabled(appearance.backgroundImage == nil)
                }
                SettingNote(text: L("settings.background.image.note"))
            }

            if appearance.backgroundStyle != .standard {
                LabeledContent(L("settings.background.preview")) {
                    ThemeBackground(settings: appearance)
                        .frame(width: 200, height: 112)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                        .overlay {
                            RoundedRectangle(cornerRadius: 8).strokeBorder(.separator)
                        }
                        .accessibilityHidden(true)
                        // 見出しを枠の上端にそろえる
                        .alignmentGuide(.firstTextBaseline) { $0[.top] + 12 }
                }
            }
        }
        .padding(20)
        .fileImporter(isPresented: $showImageImporter, allowedContentTypes: AppearanceSettings.imageContentTypes) { result in
            switch result {
            case .success(let url):
                do {
                    try appearance.setBackgroundImage(from: url)
                } catch {
                    imageError = L("settings.background.imageFailed", url.lastPathComponent)
                }
            case .failure(let error):
                imageError = userFacingMessage(for: error)
            }
        }
        .alert(
            L("settings.background.imageFailed.title"),
            isPresented: Binding(get: { imageError != nil }, set: { if !$0 { imageError = nil } })
        ) {
            Button(L("common.ok")) {}
        } message: {
            Text(imageError ?? "")
        }
    }
}
