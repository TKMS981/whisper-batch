import AppKit
import Foundation
import UniformTypeIdentifiers
import WhisperBatchCore

/// 結果の書き出し。保存先は必ず利用者が選ぶ。
@MainActor
enum ExportService {
    enum Outcome {
        case saved(String)
        case cancelled
        case failed(String)
    }

    // MARK: 1 件

    static func exportSingle(_ item: QueueItem, format: ExportFormat, includeTimestamps: Bool) async -> Outcome {
        guard let transcript = item.transcript else { return .failed(WhisperBatchError.nothingToExport.errorDescription!) }
        let panel = NSSavePanel()
        panel.title = L("export.single.panelTitle")
        panel.nameFieldStringValue = ExportNaming.baseName(for: item.displayName, format: format)
        panel.allowedContentTypes = [contentType(for: format)]
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        // 既存ファイルを選んだ場合の上書き確認は NSSavePanel が表示する
        guard await run(panel) == .OK, let url = panel.url else { return .cancelled }
        let text = TranscriptFormatter.format(transcript, as: format, includeTimestamps: includeTimestamps)
        do {
            try await write(text, to: url)
            return .saved(L("export.single.saved", url.lastPathComponent))
        } catch {
            return .failed(userFacingMessage(for: error))
        }
    }

    // MARK: 統合

    static func saveCombined(_ entries: [CombinedEntry], includeTimestamps: Bool) async -> Outcome {
        guard !entries.isEmpty else { return .failed(WhisperBatchError.nothingToExport.errorDescription!) }
        let panel = NSSavePanel()
        panel.title = L("combine.title")
        panel.message = L("export.combined.panelMessage", entries.count)
        panel.nameFieldStringValue = ExportNaming.combinedFileName()
        panel.allowedContentTypes = [.plainText]
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        guard await run(panel) == .OK, let url = panel.url else { return .cancelled }
        let text = TranscriptFormatter.combinedPlainText(entries, includeTimestamps: includeTimestamps)
        do {
            try await write(text, to: url)
            return .saved(L("export.combined.saved", entries.count, url.lastPathComponent))
        } catch {
            return .failed(userFacingMessage(for: error))
        }
    }

    // MARK: 個別にまとめて書き出し

    static func exportEach(_ items: [QueueItem], format: ExportFormat, includeTimestamps: Bool) async -> Outcome {
        let items = items.filter { $0.transcript != nil }
        guard !items.isEmpty else { return .failed(WhisperBatchError.nothingToExport.errorDescription!) }
        let panel = NSOpenPanel()
        panel.title = L("export.each.panelTitle")
        panel.message = L("export.each.panelMessage", items.count, format.fileExtension)
        panel.prompt = L("export.each.prompt")
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        guard await run(panel) == .OK, let folder = panel.url else { return .cancelled }

        let started = folder.startAccessingSecurityScopedResource()
        defer { if started { folder.stopAccessingSecurityScopedResource() } }

        let existing = Set((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? [])
        let names = items.map(\.displayName)
        var plan = ExportNaming.plan(audioFileNames: names, format: format, existing: existing, policy: .replace)
        if !plan.replacing.isEmpty {
            switch askConflict(plan.replacing) {
            case .replace: break
            case .keepBoth:
                plan = ExportNaming.plan(audioFileNames: names, format: format, existing: existing, policy: .keepBoth)
            case .cancel:
                return .cancelled
            }
        }

        var failures: [String] = []
        for (item, name) in zip(items, plan.fileNames) {
            let text = TranscriptFormatter.format(item.transcript!, as: format, includeTimestamps: includeTimestamps)
            do {
                try await write(text, to: folder.appendingPathComponent(name))
            } catch {
                failures.append(userFacingMessage(for: error))
            }
        }
        if failures.isEmpty {
            return .saved(L("export.each.saved", items.count, folder.lastPathComponent))
        }
        let ok = items.count - failures.count
        return .failed(L("export.each.partial", ok, failures.count) + "\n" + failures.joined(separator: "\n"))
    }

    // MARK: MP3（動画の音声）

    /// MP3 の保存先を選ぶ。キャンセルなら nil。
    static func chooseMP3Destination(for item: QueueItem) async -> URL? {
        let panel = NSSavePanel()
        panel.title = L("mp3.panelTitle")
        panel.message = L("mp3.panelMessage")
        panel.nameFieldStringValue = (item.displayName as NSString).deletingPathExtension + ".mp3"
        panel.allowedContentTypes = [.mp3]
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        guard await run(panel) == .OK else { return nil }
        return panel.url
    }

    /// 一時フォルダで作ったファイルを、利用者が選んだ場所へ移す（既存ファイルは NSSavePanel で確認済み）
    static func moveIntoPlace(_ temp: URL, to destination: URL) throws {
        let fm = FileManager.default
        do {
            if fm.fileExists(atPath: destination.path) {
                _ = try fm.replaceItemAt(destination, withItemAt: temp)
            } else {
                try fm.moveItem(at: temp, to: destination)
            }
        } catch {
            throw WhisperBatchError.exportFailed(fileName: destination.lastPathComponent, detail: (error as NSError).localizedDescription)
        }
    }

    // MARK: private

    private enum ConflictChoice { case replace, keepBoth, cancel }

    private static func askConflict(_ names: [String]) -> ConflictChoice {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = L("conflict.title", names.count)
        var list = names.prefix(5).joined(separator: "\n")
        if names.count > 5 { list += "\n" + L("conflict.more", names.count - 5) }
        alert.informativeText = L("conflict.message") + "\n\n" + list
        alert.addButton(withTitle: L("conflict.keepBoth"))
        alert.addButton(withTitle: L("conflict.replace"))
        alert.addButton(withTitle: L("common.cancel"))
        switch alert.runModal() {
        case .alertFirstButtonReturn: return .keepBoth
        case .alertSecondButtonReturn: return .replace
        default: return .cancel
        }
    }

    private static func run(_ panel: NSSavePanel) async -> NSApplication.ModalResponse {
        if let window = NSApp.keyWindow ?? NSApp.mainWindow {
            return await panel.beginSheetModal(for: window)
        }
        return await withCheckedContinuation { c in panel.begin { c.resume(returning: $0) } }
    }

    /// UTF-8（BOM なし）で書き込む。途中で失敗しても既存ファイルが壊れないよう一時ファイル経由で置き換える。
    @concurrent
    private static func write(_ text: String, to url: URL) async throws {
        do {
            try Data(text.utf8).write(to: url, options: [.atomic])
        } catch {
            let ns = error as NSError
            let detail: String
            switch ns.code {
            case NSFileWriteNoPermissionError: detail = L("export.detail.noPermission")
            case NSFileWriteOutOfSpaceError: detail = L("export.detail.outOfSpace")
            case NSFileWriteVolumeReadOnlyError: detail = L("export.detail.readOnly")
            default: detail = ns.localizedDescription
            }
            throw WhisperBatchError.exportFailed(fileName: url.lastPathComponent, detail: detail)
        }
    }

    private static func contentType(for format: ExportFormat) -> UTType {
        switch format {
        case .plainText: .plainText
        case .srt: UTType(filenameExtension: "srt", conformingTo: .plainText) ?? .plainText
        case .vtt: UTType(filenameExtension: "vtt", conformingTo: .plainText) ?? .plainText
        }
    }
}
