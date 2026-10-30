import SwiftUI
import WhisperBatchCore

/// 下部: 一括処理の結果表示と、書き出し操作（統合保存が主操作）
struct ExportActionBar: View {
    @Environment(AppModel.self) private var model
    @Environment(\.motion) private var motion
    var isCompact = false

    var body: some View {
        Group {
            if isCompact {
                VStack(alignment: .leading, spacing: 8) {
                    animatedStatus
                    HStack {
                        Spacer(minLength: 0)
                        buttons
                    }
                }
            } else {
                HStack(spacing: 12) {
                    animatedStatus
                    Spacer(minLength: 8)
                    buttons
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    /// 表示の種類（処理中・結果・保存済み・案内）が変わったときだけ、バナーを下からフェードで入れ替える
    private var animatedStatus: some View {
        ZStack(alignment: .leading) {
            status
                .id(bannerKind)
                .transition(motion.bannerTransition)
        }
        .animation(motion.bannerAnimation, value: bannerKind)
        .clipped()
    }

    private var bannerKind: String {
        if model.isRunning { return "running" }
        if let outcome = model.lastOutcome, model.lastSaveMessage == nil { return "outcome:\(outcome.message)" }
        if let saved = model.lastSaveMessage { return "saved:\(saved)" }
        if model.hasCompletedItems { return "ready" }
        return "idle"
    }

    @ViewBuilder
    private var status: some View {
        if model.isRunning {
            let c = model.queue.counts
            let done = c.completed + c.failed
            Label(L("bar.running", c.pending + c.processing), systemImage: "waveform")
                .appFont(.callout)
                .foregroundStyle(.secondary)
                .accessibilityLabel(L("bar.running.accessibility", done, c.pending + c.processing))
        } else if let outcome = model.lastOutcome, model.lastSaveMessage == nil {
            Label(outcome.message, systemImage: icon(outcome.severity))
                .appFont(.callout)
                .foregroundStyle(color(outcome.severity))
                .lineLimit(3)
        } else if let saved = model.lastSaveMessage {
            Label(saved, systemImage: "checkmark.circle")
                .appFont(.callout)
                .foregroundStyle(.secondary)
                .lineLimit(2)
        } else if model.hasCompletedItems {
            Text(L("bar.readyToExport", model.queue.completedItems.count))
                .appFont(.callout)
                .foregroundStyle(.secondary)
        } else {
            Text(L("bar.idle"))
                .appFont(.callout)
                .foregroundStyle(.tertiary)
        }
    }

    private var buttons: some View {
        HStack(spacing: 8) {
            Menu {
                ForEach(ExportFormat.allCases) { format in
                    Button(format.displayName) { exportEach(format) }
                }
            } label: {
                Text(L("action.exportEach"))
            }
            .fixedSize()
            .disabled(!model.hasCompletedItems)
            .help(L("action.exportEach.help"))

            Button {
                model.showCombineSheet = true
            } label: {
                Label(L("action.combine"), systemImage: "doc.on.doc")
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(!model.hasCompletedItems)
            .help(L("action.combine.help"))
        }
    }

    private func exportEach(_ format: ExportFormat) {
        let selectedCompleted = model.selectedItems.filter { $0.status == .completed }
        let items = selectedCompleted.count > 1 ? selectedCompleted : model.queue.completedItems
        Task {
            let outcome = await ExportService.exportEach(items, format: format, includeTimestamps: false)
            model.present(outcome)
        }
    }

    private func icon(_ s: BatchOutcome.Severity) -> String {
        switch s {
        case .success: "checkmark.circle.fill"
        case .warning: "exclamationmark.triangle.fill"
        case .failure: "xmark.octagon.fill"
        case .neutral: "stop.circle"
        }
    }

    private func color(_ s: BatchOutcome.Severity) -> Color {
        switch s {
        case .success: .green
        case .warning: .orange
        case .failure: .red
        case .neutral: .secondary
        }
    }
}
