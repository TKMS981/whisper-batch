import SwiftUI
import WhisperBatchCore

/// モデルの一覧・取得・削除・切り替え
struct ModelManagerSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @ViewState private var deleteTarget: WhisperModel?
    @ViewState private var downloadTarget: WhisperModel?
    @ViewState private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("models.title"))
                .font(.title2.weight(.semibold))
            Text(L("models.message"))
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            List {
                ForEach(ModelCatalog.all) { m in
                    ModelRow(model: m, deleteTarget: $deleteTarget, downloadTarget: $downloadTarget)
                }
            }
            .frame(minHeight: 300)
            .accessibilityLabel(L("models.list.accessibility"))

            if let error = model.models.storageError ?? errorMessage {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .font(.callout)
            }

            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text(L("models.storage")).foregroundStyle(.secondary)
                    Text(model.models.storagePath)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .textSelection(.enabled)
                    Button(L("queue.menu.reveal")) { model.models.revealInFinder() }
                        .controlSize(.small)
                }
                Text(L("models.source"))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .font(.caption)

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
        .alert(L("download.confirm.title"),
               isPresented: Binding(get: { downloadTarget != nil }, set: { if !$0 { downloadTarget = nil } }),
               presenting: downloadTarget) { target in
            Button(L("download.confirm.download")) { model.models.download(target) }
            Button(L("common.cancel"), role: .cancel) {}
        } message: { target in
            Text(DownloadConfirmation.message(for: target, storagePath: model.models.storagePath))
        }
    }
}

private struct ModelRow: View {
    @Environment(AppModel.self) private var app
    let model: WhisperModel
    @Binding var deleteTarget: WhisperModel?
    @Binding var downloadTarget: WhisperModel?

    var body: some View {
        let state = app.models.state(for: model)
        let isSelected = app.selectedModelID == model.id
        HStack(alignment: .center, spacing: 10) {
            Image(systemName: isSelected ? "largecircle.fill.circle" : "circle")
                .foregroundStyle(isSelected ? Color.accentColor : .secondary)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(model.displayName).font(.body.weight(.medium))
                    if model.id == ModelCatalog.defaultModelID {
                        Text(L("models.recommended"))
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 5).padding(.vertical, 1)
                            .background(Color.accentColor.opacity(0.15), in: Capsule())
                    }
                }
                Text(model.summary).font(.caption).foregroundStyle(.secondary)
                Text(L("models.row.details", model.fileName, model.sizeText, model.requiredSpaceText))
                    .font(.caption2)
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
        case .verifying:
            Text(L("models.state.verifyingShort")).font(.caption).foregroundStyle(.secondary)
        case .failed(let message, _):
            Text(message).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
        case .installed:
            Label(L("model.state.installed"), systemImage: "checkmark.circle.fill").font(.caption).foregroundStyle(.green)
        case .notInstalled:
            Text(L("model.state.notInstalled")).font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func actions(_ state: ModelManager.State, isSelected: Bool) -> some View {
        HStack(spacing: 6) {
            switch state {
            case .notInstalled:
                Button(L("model.download")) { downloadTarget = model }
            case .failed(_, let canResume):
                Button(canResume ? L("model.resume") : L("model.retry")) { app.models.download(model) }
            case .downloading:
                Button(L("model.stop")) { app.models.cancelDownload(model) }
            case .verifying:
                EmptyView()
            case .installed:
                Button(L("models.delete")) { deleteTarget = model }
                    .disabled(app.isRunning && isSelected)
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
        case .downloading: L("models.state.downloading")
        case .verifying: L("models.state.verifyingShort")
        case .failed: L("status.failed")
        }
    }
}
