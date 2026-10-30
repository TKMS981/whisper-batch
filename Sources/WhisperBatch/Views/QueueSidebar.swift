import SwiftUI
import WhisperBatchCore

/// 左側: ファイル追加の案内と処理キュー（ドロップの受け口はウィンドウ全体）
struct QueueSidebar: View {
    @Environment(AppModel.self) private var model
    @Environment(\.motion) private var motion
    @Environment(\.hasCustomBackground) private var hasCustomBackground

    var body: some View {
        @Bindable var model = model
        VStack(spacing: 0) {
            if model.queue.items.isEmpty {
                EmptyQueueView(isTargeted: model.isDropTargeted)
            } else {
                CompactDropStrip(isTargeted: model.isDropTargeted)
                if let notice = model.addNotice {
                    NoticeBanner(text: notice) { model.addNotice = nil }
                }
                List(selection: $model.selection) {
                    ForEach(model.queue.items) { item in
                        QueueRow(item: item)
                            .tag(item.id)
                            .transition(motion.rowTransition)
                    }
                }
                // ファイルの追加・削除で行が短くフェード（とスライド）して出入りする
                .animation(motion.listChange, value: model.queue.items.map(\.id))
                .listStyle(.sidebar)
                .scrollContentBackground(hasCustomBackground ? .hidden : .automatic)
                .frame(minHeight: 0, maxHeight: .infinity)
                .accessibilityLabel(L("queue.accessibility"))
                .onDeleteCommand { model.removeSelected() }
                .contextMenu(forSelectionType: UUID.self) { ids in
                    QueueContextMenu(ids: ids)
                }
                Divider()
                QueueFooter()
            }
        }
        // 空の案内とキューの一覧の切り替えも同じ動きにそろえる
        .animation(motion.listChange, value: model.queue.items.isEmpty)
    }
}

private struct EmptyQueueView: View {
    @Environment(AppModel.self) private var model
    let isTargeted: Bool

    var body: some View {
        VStack(spacing: 14) {
            Spacer(minLength: 0)
            Image(systemName: "waveform")
                .font(.system(size: 40, weight: .light))
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text(L("queue.empty.title"))
                .appFont(.title3, weight: .semibold)
            Text(L("queue.empty.message"))
                .appFont(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Button(L("action.addFilesEllipsis")) { model.showFileImporter = true }
                .controlSize(.large)
            if model.isAddingFiles { ProgressView().controlSize(.small) }
            Text(L("queue.empty.formats", AudioFormats.summary))
                .appFont(.caption)
                .foregroundStyle(.tertiary)
                .multilineTextAlignment(.center)
            if let notice = model.addNotice {
                Text(notice)
                    .appFont(.caption)
                    .foregroundStyle(.orange)
                    .multilineTextAlignment(.center)
            }
            Spacer(minLength: 0)
        }
        .padding(20)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(isTargeted ? Color.accentColor : Color.secondary.opacity(0.35),
                              style: StrokeStyle(lineWidth: isTargeted ? 2 : 1, dash: [6, 4]))
                .padding(12)
        )
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("queue.dropArea.accessibility"))
    }
}

private struct CompactDropStrip: View {
    @Environment(AppModel.self) private var model
    let isTargeted: Bool

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "tray.and.arrow.down")
                .foregroundStyle(isTargeted ? Color.accentColor : .secondary)
                .accessibilityHidden(true)
            Text(isTargeted ? L("queue.dropStrip.targeted") : L("queue.dropStrip"))
                .appFont(.callout)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            Spacer(minLength: 4)
            if model.isAddingFiles { ProgressView().controlSize(.small) }
            Button(L("action.addShort")) { model.showFileImporter = true }
                .controlSize(.small)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .strokeBorder(isTargeted ? Color.accentColor : Color.secondary.opacity(0.3),
                              style: StrokeStyle(lineWidth: isTargeted ? 2 : 1, dash: [5, 3]))
        )
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("queue.dropArea.accessibility"))
    }
}

struct NoticeBanner: View {
    let text: String
    let onClose: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "info.circle.fill")
                .foregroundStyle(.orange)
                .accessibilityHidden(true)
            Text(text)
                .appFont(.caption)
                .lineLimit(6)
                .textSelection(.enabled)
            Spacer(minLength: 0)
            Button {
                onClose()
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
            .accessibilityLabel(L("notice.close"))
        }
        .padding(8)
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 6))
        .padding(.horizontal, 10)
        .padding(.bottom, 6)
    }
}

struct QueueRow: View {
    @Environment(\.motion) private var motion
    let item: QueueItem

    /// 進捗（%）。処理中でなければ nil。
    private var progressPercent: Int? {
        if case .processing(_, let p) = item.status { return Int((p * 100).rounded(.down)) }
        return nil
    }

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            StatusIcon(item: item)
                .frame(width: 18)
                .padding(.top, 1)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 4) {
                    Text(item.displayName)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    if item.isVideo {
                        Image(systemName: "film")
                            .appFont(.caption)
                            .foregroundStyle(.secondary)
                            .help(L("queue.row.video.help"))
                            .accessibilityHidden(true)
                    }
                }
                Text(subtitle)
                    .appFont(.caption)
                    .foregroundStyle(subtitleColor)
                    .lineLimit(2)
                    .contentTransition(motion.numberChange)
                    .animation(motion.numberAnimation, value: progressPercent)
                if case .processing(_, let p) = item.status {
                    ProgressView(value: p)
                        .controlSize(.small)
                        .accessibilityHidden(true)
                }
            }
        }
        .padding(.vertical, 2)
        .help(helpText)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(item.isVideo ? L("queue.row.videoLabel", item.displayName) : item.displayName)
        .accessibilityValue(subtitle)
    }

    private var durationText: String? {
        item.duration.map { TranscriptFormatter.clock($0) }
    }

    var subtitle: String {
        let separator = " · "
        switch item.status {
        case .pending:
            return [item.statusLabel, durationText].compactMap { $0 }.joined(separator: separator)
        case .processing:
            return item.statusLabel
        case .completed:
            var parts = [item.statusLabel]
            if let d = durationText { parts.append(d) }
            if let lang = item.transcript?.language { parts.append(LanguageOption.displayName(forCode: lang)) }
            if item.transcript?.isEmpty == true { parts.append(L("queue.row.noSpeech")) }
            return parts.joined(separator: separator)
        case .failed(let failure):
            return L("queue.row.failed", failure.message)
        case .cancelled:
            return item.statusLabel
        }
    }

    private var subtitleColor: Color {
        if case .failed = item.status { return .red }
        return .secondary
    }

    private var helpText: String {
        "\(item.url.lastPathComponent)\n\(subtitle)"
    }
}

struct StatusIcon: View {
    @Environment(\.motion) private var motion
    let item: QueueItem
    /// 完了・失敗になった回数。増えたときだけアイコンを 1 回弾ませる（表示し直しただけでは弾まない）。
    @ViewState private var bounceCount = 0

    var body: some View {
        ZStack {
            // アイコンは常に置いておき、記号の入れ替えと弾む動きを付ける（処理中は隠してスピナーを重ねる）
            Image(systemName: symbolName)
                .foregroundStyle(symbolColor)
                .contentTransition(motion.symbolChange)
                .symbolEffect(.bounce, value: bounceCount)
                .opacity(item.status.isProcessing ? 0 : 1)
            if item.status.isProcessing {
                ProgressView().controlSize(.mini)
            }
        }
        .animation(motion.symbolChangeAnimation, value: symbolName)
        .onChange(of: finishedState) { _, newValue in
            if newValue != nil, motion.bouncesStatusIcon { bounceCount += 1 }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(item.statusLabel)
    }

    private var symbolName: String {
        switch item.status {
        case .pending, .processing: "clock"
        case .completed: "checkmark.circle.fill"
        case .failed: "exclamationmark.triangle.fill"
        case .cancelled: "minus.circle"
        }
    }

    private var symbolColor: Color {
        switch item.status {
        case .completed: .green
        case .failed: .red
        default: .secondary
        }
    }

    /// 完了・失敗のときだけ値を持つ
    private var finishedState: String? {
        switch item.status {
        case .completed: "completed"
        case .failed: "failed"
        default: nil
        }
    }
}

private struct QueueContextMenu: View {
    @Environment(AppModel.self) private var model
    let ids: Set<UUID>

    var body: some View {
        let items = model.queue.items.filter { ids.contains($0.id) }
        Button(L("queue.menu.retry")) { model.retry(ids) }
            .disabled(!items.contains { $0.status.isRetryable } || items.isEmpty)
        Button(L("queue.menu.reveal")) { model.revealInFinder(ids) }
            .disabled(items.isEmpty)
        if items.count == 1, let item = items.first, item.isVideo {
            Button(L("mp3.save")) { model.saveMP3(for: item.id) }
                .disabled(model.mp3Exports[item.id] != nil)
        }
        Divider()
        Button(L("queue.menu.remove")) {
            model.selection = ids
            model.removeSelected()
        }
        .disabled(!items.contains { $0.status.isRemovable })
    }
}

private struct QueueFooter: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let counts = model.queue.counts
        HStack(spacing: 6) {
            Button {
                model.removeSelected()
            } label: {
                Image(systemName: "minus")
            }
            .disabled(!model.selectedItems.contains { $0.status.isRemovable })
            .help(L("queue.footer.remove.help"))
            .accessibilityLabel(L("queue.footer.remove"))

            Menu {
                Button(L("queue.footer.removePending")) { model.removeAllPending() }
                    .disabled(counts.pending == 0)
                Button(L("queue.footer.removeAll")) { model.removeAll() }
                    .disabled(counts.total == counts.processing)
                Divider()
                Button(L("queue.footer.retryFailed")) {
                    model.retry(Set(model.queue.items.filter {
                        $0.status.isFailed || $0.status == .cancelled
                    }.map(\.id)))
                }
                .disabled(counts.failed + counts.cancelled == 0)
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .accessibilityLabel(L("queue.footer.menu"))

            Spacer(minLength: 4)

            Text(summary(counts))
                .appFont(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.tail)
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
    }

    private func summary(_ c: StatusCounts) -> String {
        var parts = [L("queue.footer.total", c.total)]
        if c.completed > 0 { parts.append(L("queue.footer.completed", c.completed)) }
        if c.failed > 0 { parts.append(L("queue.footer.failed", c.failed)) }
        if c.pending > 0 { parts.append(L("queue.footer.pending", c.pending)) }
        return parts.joined(separator: " · ")
    }
}
