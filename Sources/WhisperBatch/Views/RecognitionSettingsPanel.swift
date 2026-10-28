import SwiftUI
import WhisperBatchCore

/// 右上: モデルと言語の設定、開始・キャンセル
struct RecognitionSettingsPanel: View {
    @Environment(AppModel.self) private var model
    var isCompact = false

    var body: some View {
        @Bindable var model = model
        VStack(alignment: .leading, spacing: 10) {
            if isCompact {
                VStack(alignment: .leading, spacing: 8) {
                    modelPicker
                    HStack {
                        languagePicker.frame(maxWidth: 220)
                        Spacer(minLength: 0)
                        runButton
                    }
                }
            } else {
                HStack(alignment: .center, spacing: 16) {
                    modelPicker.frame(maxWidth: 380)
                    languagePicker.frame(maxWidth: 200)
                    Spacer(minLength: 0)
                    runButton
                }
            }
            ModelStatusLine()
            if let reason = model.startBlockReason, !model.isRunning, !model.queue.items.isEmpty {
                Label(reason, systemImage: "info.circle")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Label(L("settings.privacy"), systemImage: "lock.shield")
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(2)
        }
        .padding(14)
    }

    private var modelPicker: some View {
        @Bindable var model = model
        return LabeledContent(L("settings.model")) {
            Picker(L("settings.model"), selection: $model.selectedModelID) {
                ForEach(ModelCatalog.all) { m in
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
        return LabeledContent(L("settings.language")) {
            Picker(L("settings.language"), selection: $model.language) {
                ForEach(LanguageOption.allCases) { option in
                    Text(option.displayName).tag(option)
                }
            }
            .labelsHidden()
            .disabled(model.isRunning)
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

    private func pickerLabel(_ m: WhisperModel) -> String {
        let mark = model.models.isInstalled(m) ? "✓ " : ""
        let name = m.id == ModelCatalog.defaultModelID ? L("model.recommendedName", m.displayName) : m.displayName
        return "\(mark)\(name) — \(m.sizeText)"
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
                    Text(L("model.loading")).font(.callout)
                } else {
                    Label(L("model.state.installed"), systemImage: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                        .font(.callout)
                    Text(m.summary).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            case .notInstalled:
                Label(L("model.state.notInstalled"), systemImage: "arrow.down.circle")
                    .foregroundStyle(.orange)
                    .font(.callout)
                Text(L("model.sizeAndSpace", m.sizeText, m.requiredSpaceText))
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Button(L("model.download")) { model.downloadConfirmation = m }
                    .controlSize(.small)
            case .downloading(let received, let total):
                let fraction = total > 0 ? Double(received) / Double(total) : 0
                ProgressView(value: fraction)
                    .frame(maxWidth: 220)
                    .accessibilityLabel(L("model.downloading.accessibility"))
                    .accessibilityValue(L("common.percent", Int(fraction * 100)))
                Text(progressText(received, total))
                    .font(.caption.monospacedDigit()).foregroundStyle(.secondary).lineLimit(1)
                Button(L("model.stop")) { model.models.cancelDownload(m) }
                    .controlSize(.small)
            case .verifying:
                ProgressView().controlSize(.small)
                Text(L("model.verifying")).font(.callout)
            case .failed(let message, let canResume):
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .font(.callout)
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
