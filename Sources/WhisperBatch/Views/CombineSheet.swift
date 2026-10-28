import SwiftUI
import WhisperBatchCore

/// 統合保存: 含める項目と順序を決めて 1 つのテキストファイルに保存する
struct CombineSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    struct Row: Identifiable, Equatable {
        let id: UUID
        let name: String
        var included: Bool
    }

    @ViewState private var rows: [Row] = []
    @ViewState private var selection: UUID?
    @ViewState private var includeTimestamps = false
    @ViewState private var isSaving = false
    @ViewState private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("combine.title"))
                .font(.title2.weight(.semibold))
            Text(L("combine.message"))
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack(alignment: .top, spacing: 8) {
                List(selection: $selection) {
                    ForEach($rows) { $row in
                        HStack(spacing: 8) {
                            Toggle(isOn: $row.included) { EmptyView() }
                                .toggleStyle(.checkbox)
                                .labelsHidden()
                                .accessibilityLabel(L("combine.include.accessibility", row.name))
                            Text(orderLabel(row))
                                .font(.callout.monospacedDigit())
                                .foregroundStyle(.secondary)
                                .frame(minWidth: 26, alignment: .trailing)
                            Text(row.name)
                                .lineLimit(1)
                                .truncationMode(.middle)
                                .foregroundStyle(row.included ? .primary : .secondary)
                        }
                        .tag(row.id)
                        .accessibilityElement(children: .combine)
                        .accessibilityAction(named: Text(L("combine.moveUp"))) { move(row.id, by: -1) }
                        .accessibilityAction(named: Text(L("combine.moveDown"))) { move(row.id, by: 1) }
                    }
                    .onMove { rows.move(fromOffsets: $0, toOffset: $1) }
                }
                .frame(minHeight: 220)
                .accessibilityLabel(L("combine.list.accessibility"))

                VStack(spacing: 6) {
                    Button { move(selection, by: -1) } label: { Image(systemName: "arrow.up") }
                        .help(L("combine.moveUp.help"))
                        .accessibilityLabel(L("combine.moveUp"))
                        .keyboardShortcut(.upArrow, modifiers: [.command, .option])
                        .disabled(!canMove(by: -1))
                    Button { move(selection, by: 1) } label: { Image(systemName: "arrow.down") }
                        .help(L("combine.moveDown.help"))
                        .accessibilityLabel(L("combine.moveDown"))
                        .keyboardShortcut(.downArrow, modifiers: [.command, .option])
                        .disabled(!canMove(by: 1))
                }
            }

            HStack(spacing: 12) {
                Button(L("combine.selectAll")) { setAll(true) }
                Button(L("combine.deselectAll")) { setAll(false) }
                Spacer()
                Toggle(L("combine.timestamps"), isOn: $includeTimestamps)
                    .toggleStyle(.checkbox)
            }
            .controlSize(.small)

            if let errorMessage {
                Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Text(L("combine.summary", includedCount))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Button(L("common.cancel"), role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(L("combine.save")) { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(includedCount == 0 || isSaving)
            }
        }
        .padding(20)
        .frame(minWidth: 480, idealWidth: 560, minHeight: 440)
        .onAppear(perform: load)
    }

    private var includedCount: Int { rows.filter(\.included).count }

    private func orderLabel(_ row: Row) -> String {
        guard row.included, let i = rows.filter(\.included).firstIndex(where: { $0.id == row.id }) else { return "–" }
        return "\(i + 1)."
    }

    /// 初期状態: キューの順序。キューで完了項目を 2 件以上選んでいればそれだけを含める。
    private func load() {
        let completed = model.queue.completedItems
        let selected = Set(model.selectedItems.filter { $0.status == .completed }.map(\.id))
        let useSelection = selected.count >= 2
        rows = completed.map { Row(id: $0.id, name: $0.displayName, included: !useSelection || selected.contains($0.id)) }
        selection = rows.first?.id
    }

    private func setAll(_ value: Bool) {
        for i in rows.indices { rows[i].included = value }
    }

    private func canMove(by offset: Int) -> Bool {
        guard let selection, let i = rows.firstIndex(where: { $0.id == selection }) else { return false }
        return rows.indices.contains(i + offset)
    }

    private func move(_ id: UUID?, by offset: Int) {
        guard let id, let i = rows.firstIndex(where: { $0.id == id }), rows.indices.contains(i + offset) else { return }
        rows.swapAt(i, i + offset)
    }

    private func save() {
        let entries: [CombinedEntry] = rows.filter(\.included).compactMap { row in
            guard let item = model.queue.item(row.id), let transcript = item.transcript else { return nil }
            return CombinedEntry(title: item.displayName, transcript: transcript)
        }
        isSaving = true
        errorMessage = nil
        Task {
            let outcome = await ExportService.saveCombined(entries, includeTimestamps: includeTimestamps)
            isSaving = false
            switch outcome {
            case .saved(let message):
                model.lastSaveMessage = message
                dismiss()
            case .cancelled:
                break
            case .failed(let message):
                errorMessage = message
            }
        }
    }
}
