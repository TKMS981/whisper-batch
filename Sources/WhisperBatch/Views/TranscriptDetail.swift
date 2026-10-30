import AppKit
import SwiftUI
import WhisperBatchCore

/// 中央: 選択中のファイルの状態と文字起こし結果
struct TranscriptDetail: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if model.queue.items.isEmpty {
            ContentUnavailableView {
                Label(L("detail.empty.title"), systemImage: "waveform")
            } description: {
                Text(L("detail.empty.message"))
            }
        } else if let item = model.singleSelectedItem {
            ItemDetail(item: item)
                .id(item.id)
        } else if model.selection.count > 1 {
            MultipleSelectionView(items: model.selectedItems)
        } else {
            ContentUnavailableView {
                Label(L("detail.noSelection.title"), systemImage: "sidebar.left")
            } description: {
                Text(L("detail.noSelection.message"))
            }
        }
    }
}

private struct ItemDetail: View {
    @Environment(AppModel.self) private var model
    @Environment(\.motion) private var motion
    let item: QueueItem
    @Environment(\.appTypography) private var typography
    @Environment(\.hasCustomBackground) private var hasCustomBackground
    @AppStorage("showTimestamps") private var showTimestamps = false
    @ViewState private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
            Divider()
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        // 状態が変わったとき（処理中 → 完了・失敗など）に結果表示をクロスフェードで切り替える。
        // 選択を変えたときは ItemDetail ごと作り直すので、すぐに切り替わる。
        .animation(motion.resultAnimation, value: item.status.name)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .center, spacing: 10) {
                StatusIcon(item: item)
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.displayName)
                        .appFont(.headline)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .textSelection(.enabled)
                    Text(detailLine)
                        .appFont(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            if item.isVideo {
                MP3ExportRow(item: item)
            }
            if let transcript = item.transcript, item.status == .completed {
                HStack(spacing: 10) {
                    Toggle(L("detail.showTimestamps"), isOn: $showTimestamps)
                        .toggleStyle(.checkbox)
                        .fixedSize()
                    Spacer(minLength: 0)
                    Button {
                        let text = TranscriptFormatter.plainText(transcript, includeTimestamps: showTimestamps)
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(text, forType: .string)
                        copied = true
                    } label: {
                        Label(copied ? L("detail.copied") : L("detail.copy"), systemImage: copied ? "checkmark" : "doc.on.doc")
                    }
                    .fixedSize()
                    Menu {
                        ForEach(ExportFormat.allCases) { format in
                            Button(L("common.ellipsis", format.displayName)) {
                                Task {
                                    let outcome = await ExportService.exportSingle(item, format: format, includeTimestamps: showTimestamps)
                                    model.present(outcome)
                                }
                            }
                        }
                    } label: {
                        Label(L("detail.exportThis"), systemImage: "square.and.arrow.up")
                    }
                    .fixedSize()
                    .accessibilityLabel(L("detail.exportThis.accessibility"))
                }
                .controlSize(.small)
            }
        }
    }

    private var detailLine: String {
        var parts = [item.statusLabel]
        if item.isVideo { parts.append(L("detail.kind.video")) }
        if let d = item.duration { parts.append(L("detail.duration", TranscriptFormatter.clock(d))) }
        if let lang = item.transcript?.language { parts.append(L("detail.language", LanguageOption.displayName(forCode: lang))) }
        if let t = item.transcript, item.status == .completed { parts.append(L("detail.segments", t.segments.count)) }
        return parts.joined(separator: " · ")
    }

    @ViewBuilder
    private var content: some View {
        switch item.status {
        case .pending:
            ContentUnavailableView {
                Label(item.statusLabel, systemImage: "clock")
            } description: {
                Text(pendingMessage)
            }
        case .processing(let phase, let p):
            VStack(spacing: 12) {
                ProgressView(value: p) {
                    // 変換から文字起こしに移るとき、ラベルをクロスフェードで入れ替える
                    ZStack(alignment: .leading) {
                        Text(phase == .converting ? L("detail.converting") : L("detail.transcribing"))
                            .id(phase)
                            .transition(.opacity)
                    }
                    .animation(motion.phaseAnimation, value: phase)
                } currentValueLabel: {
                    Text(L("common.percentShort", Int(p * 100)))
                        .monospacedDigit()
                        .contentTransition(motion.numberChange)
                        .animation(motion.numberAnimation, value: Int(p * 100))
                }
                .frame(maxWidth: 360)
                if item.isVideo {
                    Text(L("detail.videoSteps"))
                        .appFont(.caption)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 420)
                }
                Button(L("action.cancel")) { model.cancel() }
            }
            .padding()
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        case .failed(let failure):
            ContentUnavailableView {
                Label(L("detail.failed.title"), systemImage: "exclamationmark.triangle")
            } description: {
                Text(failure.message).textSelection(.enabled)
            } actions: {
                Button(L("action.retry")) { model.retry([item.id]) }
            }
        case .cancelled:
            ContentUnavailableView {
                Label(L("detail.cancelled.title"), systemImage: "minus.circle")
            } description: {
                Text(L("detail.cancelled.message"))
            } actions: {
                Button(L("action.retry")) { model.retry([item.id]) }
            }
        case .completed:
            if let transcript = item.transcript, !transcript.isEmpty {
                TranscriptTextView(
                    text: TranscriptFormatter.plainText(transcript, includeTimestamps: showTimestamps),
                    font: typography.nsFont(size: NSFont.systemFontSize + 1),
                    showsBackground: !hasCustomBackground
                )
            } else {
                ContentUnavailableView {
                    Label(L("detail.noSpeech.title"), systemImage: "speaker.slash")
                } description: {
                    Text(L("detail.noSpeech.message"))
                } actions: {
                    Button(L("action.retry")) { model.retry([item.id]) }
                }
            }
        }
    }

    private var pendingMessage: String {
        let base = model.isRunning ? L("detail.pending.running") : L("detail.pending.idle")
        return item.isVideo ? base + "\n" + L("detail.videoSteps") : base
    }
}

/// 動画の音声を MP3 として保存する操作（利用者が選んだときだけ、選んだ場所に保存する）
private struct MP3ExportRow: View {
    @Environment(AppModel.self) private var model
    let item: QueueItem

    var body: some View {
        HStack(spacing: 8) {
            if let progress = model.mp3Exports[item.id] {
                ProgressView(value: progress)
                    .frame(maxWidth: 200)
                    .accessibilityLabel(L("mp3.exporting"))
                    .accessibilityValue(L("common.percent", Int(progress * 100)))
                Text(L("mp3.exporting"))
                    .appFont(.caption)
                    .foregroundStyle(.secondary)
                Button(L("model.stop")) { model.cancelMP3Export(for: item.id) }
            } else {
                Button {
                    model.saveMP3(for: item.id)
                } label: {
                    Label(L("mp3.save"), systemImage: "waveform.badge.plus")
                }
                .help(L("mp3.save.help"))
            }
            Spacer(minLength: 0)
        }
        .controlSize(.small)
    }
}

private struct MultipleSelectionView: View {
    @Environment(AppModel.self) private var model
    let items: [QueueItem]

    var body: some View {
        let completed = items.filter { $0.status == .completed }.count
        ContentUnavailableView {
            Label(L("detail.multi.title", items.count), systemImage: "square.stack")
        } description: {
            Text(L("detail.multi.message", completed))
        } actions: {
            HStack {
                Button(L("action.combine")) { model.showCombineSheet = true }
                    .disabled(completed == 0)
                Button(L("action.retry")) { model.retry(Set(items.map(\.id))) }
                    .disabled(!items.contains { $0.status.isRetryable })
                Button(L("queue.menu.remove")) { model.removeSelected() }
                    .disabled(!items.contains { $0.status.isRemovable })
            }
        }
    }
}

/// 長い文章でも軽く表示でき、選択・コピー・検索（⌘F）ができる読み取り専用テキスト
struct TranscriptTextView: NSViewRepresentable {
    let text: String
    var font = NSFont.systemFont(ofSize: NSFont.systemFontSize + 1)
    /// false のときは背景を描かず、テーマの背景を透かす
    var showsBackground = true

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        let textView = scroll.documentView as! NSTextView
        textView.isEditable = false
        textView.isSelectable = true
        textView.isRichText = false
        textView.usesFindBar = true
        textView.isIncrementalSearchingEnabled = true
        textView.textContainerInset = NSSize(width: 14, height: 12)
        textView.backgroundColor = .textBackgroundColor
        textView.setAccessibilityLabel(L("detail.text.accessibility"))
        // 読み取り専用なのでドロップは受け付けない。ファイルのドロップをウィンドウ全体の受け口に通す。
        textView.unregisterDraggedTypes()
        applyBackground(to: scroll, textView: textView)
        apply(text, to: textView)
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let textView = scroll.documentView as? NSTextView else { return }
        applyBackground(to: scroll, textView: textView)
        guard textView.string != text || textView.font != font else { return }
        apply(text, to: textView)
    }

    private func applyBackground(to scroll: NSScrollView, textView: NSTextView) {
        scroll.drawsBackground = showsBackground
        textView.drawsBackground = showsBackground
    }

    private func apply(_ text: String, to textView: NSTextView) {
        let style = NSMutableParagraphStyle()
        style.lineSpacing = 4
        style.paragraphSpacing = 4
        let attributes: [NSAttributedString.Key: Any] = [
            .font: font,
            .foregroundColor: NSColor.textColor,
            .paragraphStyle: style,
        ]
        textView.textStorage?.setAttributedString(NSAttributedString(string: text, attributes: attributes))
    }
}
