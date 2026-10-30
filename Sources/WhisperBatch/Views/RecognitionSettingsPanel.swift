import SwiftUI
import WhisperBatchCore

/// 右上: 方式・モデル・言語の設定、開始・キャンセル
struct RecognitionSettingsPanel: View {
    @Environment(AppModel.self) private var model
    var isCompact = false

    var body: some View {
        @Bindable var model = model
        VStack(alignment: .leading, spacing: 10) {
            if isCompact {
                VStack(alignment: .leading, spacing: 8) {
                    enginePicker
                    modelPicker
                    HStack {
                        languagePicker.frame(maxWidth: 220)
                        Spacer(minLength: 0)
                        runButton
                    }
                }
            } else {
                HStack(alignment: .center, spacing: 16) {
                    enginePicker
                    Spacer(minLength: 0)
                    runButton
                }
                HStack(alignment: .center, spacing: 16) {
                    modelPicker.frame(maxWidth: 400)
                    languagePicker.frame(maxWidth: 200)
                    Spacer(minLength: 0)
                }
            }
            ModelStatusLine()
            if let reason = model.startBlockReason, !model.isRunning, !model.queue.items.isEmpty {
                Label(reason, systemImage: "info.circle")
                    .appFont(.caption)
                    .foregroundStyle(.secondary)
            }
            Label(L("settings.privacy"), systemImage: "lock.shield")
                .appFont(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(2)
        }
        .padding(14)
    }

    /// Whisper / ReazonSpeech / Apple の切り替え
    private var enginePicker: some View {
        HStack(spacing: 8) {
            Picker(L("settings.engine"), selection: Binding(get: { model.selectedEngine }, set: { model.selectEngine($0) })) {
                ForEach(SpeechEngine.allCases) { engine in
                    Text(engine.displayName).tag(engine)
                }
            }
            .pickerStyle(.segmented)
            .fixedSize()
            .disabled(model.isRunning)
            .help(model.selectedEngine.summary)
            .accessibilityLabel(L("settings.engine.accessibility"))
        }
    }

    /// 選んでいる方式のモデル（サイズ）
    private var modelPicker: some View {
        @Bindable var model = model
        return LabeledContent(L("settings.model")) {
            Picker(L("settings.model"), selection: $model.selectedModelID) {
                ForEach(ModelCatalog.models(for: model.selectedEngine)) { m in
                    Text(pickerLabel(m)).tag(m.id)
                }
            }
            .labelsHidden()
            .disabled(model.isRunning)
            .accessibilityLabel(L("settings.model.accessibility"))
        }
    }

    private var languagePicker: some View {
        @Bindable var model = model
        let engine = model.selectedEngine
        // ReazonSpeech は日本語専用なので、日本語に固定して表示する
        let fixed = engine.languageOptions.count == 1
        return LabeledContent(L("settings.language")) {
            Picker(L("settings.language"), selection: fixed ? .constant(model.effectiveLanguage) : $model.language) {
                ForEach(engine.languageOptions) { option in
                    Text(option.displayName).tag(option)
                }
                // Apple で「自動判定」が保存されている場合も、選択肢が空にならないようにする
                if !engine.supports(model.language), !fixed {
                    Text(model.language.displayName).tag(model.language)
                }
            }
            .labelsHidden()
            .disabled(model.isRunning || fixed)
            .help(fixed ? L("settings.language.japaneseOnly") : "")
            .accessibilityLabel(L("settings.language.accessibility"))
        }
    }

    @ViewBuilder
    private var runButton: some View {
        if model.isRunning {
            Button(role: .cancel) {
                model.cancel()
            } label: {
                Label(L("action.cancel"), systemImage: "stop.fill")
            }
            .controlSize(.large)
        } else {
            Button {
                model.start()
            } label: {
                Label(L("action.start"), systemImage: "play.fill")
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(!model.canStart)
        }
    }

    private func pickerLabel(_ m: SpeechModel) -> String {
        let mark = model.models.isInstalled(m) ? "✓ " : ""
        let name = m.isDefaultForEngine ? L("model.recommendedName", m.displayName) : m.displayName
        return m.isDownloadable ? "\(mark)\(name) — \(m.sizeText)" : "\(mark)\(name)"
    }
}

/// 選択中のモデルの取得状況
private struct ModelStatusLine: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let m = model.selectedModel
        HStack(spacing: 8) {
            switch model.models.state(for: m) {
            case .installed:
                if model.isLoadingModel {
                    ProgressView().controlSize(.small)
                    Text(L("model.loading")).appFont(.callout)
                } else {
                    Label(L("model.state.installed"), systemImage: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                        .appFont(.callout)
                    Text(m.summary).appFont(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            case .notInstalled:
                Label(m.isDownloadable ? L("model.state.notInstalled") : L("model.state.noAssets", model.effectiveLanguage.displayName),
                      systemImage: "arrow.down.circle")
                    .foregroundStyle(.orange)
                    .appFont(.callout)
                if m.isDownloadable {
                    Text(L("model.sizeAndSpace", m.sizeText, m.requiredSpaceText))
                        .appFont(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                Button(m.isDownloadable ? L("model.download") : L("model.downloadAssets")) { model.downloadConfirmation = m }
                    .controlSize(.small)
            case .installingAssets(let fraction):
                ProgressView(value: fraction)
                    .frame(maxWidth: 220)
                    .accessibilityLabel(L("model.installingAssets"))
                    .accessibilityValue(L("common.percent", Int(fraction * 100)))
                Text(L("model.installingAssets"))
                    .appFont(.caption).foregroundStyle(.secondary).lineLimit(1)
                Button(L("model.stop")) { model.models.cancelDownload(m) }
                    .controlSize(.small)
            case .unsupported:
                Label(L("model.state.unsupported", model.effectiveLanguage.displayName), systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.orange)
                    .appFont(.callout)
                    .lineLimit(2)
            case .downloading(let received, let total):
                let fraction = total > 0 ? Double(received) / Double(total) : 0
                ProgressView(value: fraction)
                    .frame(maxWidth: 220)
                    .accessibilityLabel(L("model.downloading.accessibility"))
                    .accessibilityValue(L("common.percent", Int(fraction * 100)))
                Text(progressText(received, total))
                    .appFont(.caption, monospacedDigit: true).foregroundStyle(.secondary).lineLimit(1)
                Button(L("model.stop")) { model.models.cancelDownload(m) }
                    .controlSize(.small)
            case .verifying:
                ProgressView().controlSize(.small)
                Text(L("model.verifying")).appFont(.callout)
            case .failed(let message, let canResume):
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .appFont(.callout)
                    .lineLimit(2)
                Button(canResume ? L("model.resume") : L("model.retry")) { model.models.download(m) }
                    .controlSize(.small)
            }
            Spacer(minLength: 0)
            Button(L("action.manageModels")) { model.showModelManager = true }
                .controlSize(.small)
        }
        .frame(minHeight: 22)
    }

    private func progressText(_ received: Int64, _ total: Int64) -> String {
        let f = ByteCountFormatter.fileSize
        return "\(f.string(fromByteCount: received)) / \(f.string(fromByteCount: total))"
    }
}
