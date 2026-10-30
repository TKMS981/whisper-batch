import SwiftUI
import WhisperBatchCore

/// モデルの一覧・取得・削除・切り替え。方式（Whisper / ReazonSpeech / Apple）ごとに一覧を切り替える。
struct ModelManagerSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @ViewState private var engine: SpeechEngine?
    @ViewState private var deleteTarget: SpeechModel?
    @ViewState private var downloadTarget: SpeechModel?
    @ViewState private var errorMessage: String?

    /// 表示している方式（最初は使用中のモデルの方式）
    private var shownEngine: SpeechEngine { engine ?? model.selectedEngine }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("models.title"))
                .appFont(.title2, weight: .semibold)
            Picker(L("settings.engine"), selection: Binding(get: { shownEngine }, set: { engine = $0 })) {
                ForEach(SpeechEngine.allCases) { e in
                    Text(e.displayName).tag(e)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            .accessibilityLabel(L("settings.engine.accessibility"))
            Text(L("models.message.\(shownEngine.rawValue)"))
                .appFont(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            List {
                ForEach(ModelCatalog.models(for: shownEngine)) { m in
                    ModelRow(model: m, deleteTarget: $deleteTarget, downloadTarget: $downloadTarget)
                }
            }
            .frame(minHeight: 300)
            .accessibilityLabel(L("models.list.accessibility"))

            if let error = model.models.storageError ?? errorMessage {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .appFont(.callout)
            }

            VStack(alignment: .leading, spacing: 4) {
                if shownEngine.usesDownloadedFiles {
                    HStack(spacing: 6) {
                        Text(L("models.storage")).foregroundStyle(.secondary)
                        Text(model.models.storagePath)
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .textSelection(.enabled)
                        Button(L("queue.menu.reveal")) { model.models.revealInFinder() }
                            .controlSize(.small)
                    }
                }
                Text(shownEngine.sourceDescription)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .appFont(.caption)

            HStack {
                Spacer()
                Button(L("common.close")) { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(minWidth: 560, idealWidth: 640, minHeight: 520)
        .onAppear { model.models.refresh() }
        .alert(L("models.delete.title"), isPresented: Binding(get: { deleteTarget != nil }, set: { if !$0 { deleteTarget = nil } }),
               presenting: deleteTarget) { target in
            Button(L("models.delete.confirm"), role: .destructive) {
                do { try model.models.delete(target) } catch { errorMessage = userFacingMessage(for: error) }
            }
            Button(L("common.cancel"), role: .cancel) {}
        } message: { target in
            Text(L("models.delete.message", target.displayName, target.sizeText))
        }
        // シートの上では親画面のアラートを出せないため、ダウンロードの確認もここで出す
        .alert(DownloadConfirmation.title(for: downloadTarget),
               isPresented: Binding(get: { downloadTarget != nil }, set: { if !$0 { downloadTarget = nil } }),
               presenting: downloadTarget) { target in
            Button(L("download.confirm.download")) { model.models.download(target) }
            Button(L("common.cancel"), role: .cancel) {}
        } message: { target in
            Text(DownloadConfirmation.message(for: target, model: model))
        }
    }
}

private struct ModelRow: View {
    @Environment(AppModel.self) private var app
    let model: SpeechModel
    @Binding var deleteTarget: SpeechModel?
    @Binding var downloadTarget: SpeechModel?

    var body: some View {
        let state = app.models.state(for: model)
        let isSelected = app.selectedModelID == model.id
        HStack(alignment: .center, spacing: 10) {
            Image(systemName: isSelected ? "largecircle.fill.circle" : "circle")
                .foregroundStyle(isSelected ? Color.accentColor : .secondary)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(model.displayName).appFont(.body, weight: .medium)
                    if model.isDefaultForEngine {
                        Text(L("models.recommended"))
                            .appFont(.caption2, weight: .semibold)
                            .padding(.horizontal, 5).padding(.vertical, 1)
                            .background(Color.accentColor.opacity(0.15), in: Capsule())
                    }
                }
                Text(model.summary).appFont(.caption).foregroundStyle(.secondary)
                Text(details)
                    .appFont(.caption2)
                    .foregroundStyle(.tertiary)
                stateLine(state)
            }
            Spacer(minLength: 8)
            actions(state, isSelected: isSelected)
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(isSelected ? L("models.row.accessibility.inUse", model.displayName, model.sizeText, stateText(state)) : L("models.row.accessibility", model.displayName, model.sizeText, stateText(state)))
    }

    private var details: String {
        guard model.isDownloadable else {
            return L("models.row.details.apple", model.sizeText, app.effectiveLanguage.displayName)
        }
        let files = model.files.count == 1 ? model.files[0].name : (model.folder ?? model.files[0].name)
        return L("models.row.details", files, model.sizeText, model.requiredSpaceText)
    }

    @ViewBuilder
    private func stateLine(_ state: ModelManager.State) -> some View {
        switch state {
        case .downloading(let received, let total):
            let fraction = total > 0 ? Double(received) / Double(total) : 0
            ProgressView(value: fraction) {
                EmptyView()
            } currentValueLabel: {
                Text(L("models.row.progress", Int(fraction * 100), ByteCountFormatter.fileSize.string(fromByteCount: received), ByteCountFormatter.fileSize.string(fromByteCount: total)))
                    .monospacedDigit()
            }
            .controlSize(.small)
            .frame(maxWidth: 280)
        case .installingAssets(let fraction):
            ProgressView(value: fraction) {
                EmptyView()
            } currentValueLabel: {
                Text(L("model.installingAssets"))
            }
            .controlSize(.small)
            .frame(maxWidth: 280)
        case .unsupported:
            Text(L("model.state.unsupported", app.effectiveLanguage.displayName)).appFont(.caption).foregroundStyle(.orange)
        case .verifying:
            Text(L("models.state.verifyingShort")).appFont(.caption).foregroundStyle(.secondary)
        case .failed(let message, _):
            Text(message).appFont(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
        case .installed:
            Label(L("model.state.installed"), systemImage: "checkmark.circle.fill").appFont(.caption).foregroundStyle(.green)
        case .notInstalled:
            Text(model.isDownloadable ? L("model.state.notInstalled") : L("model.state.noAssets", app.effectiveLanguage.displayName))
                .appFont(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func actions(_ state: ModelManager.State, isSelected: Bool) -> some View {
        HStack(spacing: 6) {
            switch state {
            case .notInstalled:
                Button(model.isDownloadable ? L("model.download") : L("model.downloadAssets")) { downloadTarget = model }
            case .failed(_, let canResume):
                Button(canResume ? L("model.resume") : L("model.retry")) { app.models.download(model) }
            case .downloading, .installingAssets:
                Button(L("model.stop")) { app.models.cancelDownload(model) }
            case .verifying, .unsupported:
                EmptyView()
            case .installed:
                // Apple の言語データは OS が管理するので、このアプリからは削除しない
                if model.isDownloadable {
                    Button(L("models.delete")) { deleteTarget = model }
                        .disabled(app.isRunning && isSelected)
                }
            }
            Button(isSelected ? L("models.inUse") : L("models.use")) { app.selectedModelID = model.id }
                .disabled(isSelected || app.isRunning)
        }
        .controlSize(.small)
    }

    private func stateText(_ state: ModelManager.State) -> String {
        switch state {
        case .installed: L("model.state.installed")
        case .notInstalled: L("model.state.notInstalled")
        case .downloading, .installingAssets: L("models.state.downloading")
        case .unsupported: L("model.state.unsupported", app.effectiveLanguage.displayName)
        case .verifying: L("models.state.verifyingShort")
        case .failed: L("status.failed")
        }
    }
}
