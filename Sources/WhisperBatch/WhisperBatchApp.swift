import AppKit
import SwiftUI
import WhisperBatchCore
import WhisperBatchEngine

@main
struct WhisperBatchApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        Window("Whisper Batch", id: "main") {
            ContentView()
                .modifier(MotionProvider())
                .modifier(AppearanceProvider(settings: appDelegate.model.appearance, appliesBackground: true))
                .environment(appDelegate.model)
                // 表示言語を切り替えたら画面を作り直して、すべての文字列を新しい言語にする
                .id(appDelegate.model.appLanguage)
                .environment(\.locale, appDelegate.model.appLanguage.locale)
                .frame(minWidth: 720, minHeight: 500)
        }
        .defaultSize(width: 1080, height: 720)
        .windowResizability(.contentMinSize)
        .commands { AppCommands(model: appDelegate.model) }

        Settings {
            SettingsView()
                .modifier(AppearanceProvider(settings: appDelegate.model.appearance))
                .environment(appDelegate.model)
                .id(appDelegate.model.appLanguage)
                .environment(\.locale, appDelegate.model.appLanguage.locale)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    @MainActor let model = AppModel()

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    /// Finder でアプリのアイコンにドロップされた・「このアプリで開く」が選ばれたファイルをキューに追加する
    @MainActor
    func application(_ application: NSApplication, open urls: [URL]) {
        model.addFiles(urls)
    }

    /// 処理中・変換中・ダウンロード中なら確認し、モデルの解放と一時ファイルの削除を終えてから終了する
    @MainActor
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard model.hasActiveWork || model.models.hasActiveDownloads else { return .terminateNow }
        let alert = NSAlert()
        alert.messageText = model.hasActiveWork ? L("quit.running.title") : L("quit.download.title")
        alert.informativeText = model.hasActiveWork ? L("quit.running.message") : L("quit.download.message")
        alert.addButton(withTitle: L("quit.confirm"))
        alert.addButton(withTitle: L("common.cancel"))
        guard alert.runModal() == .alertFirstButtonReturn else { return .terminateCancel }
        Task { @MainActor in
            await model.shutdown()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    @MainActor
    func applicationWillTerminate(_ notification: Notification) {
        // 変換用の一時ファイルを残さない
        ConversionWorkspace.removeLeftovers()
    }
}

struct AppCommands: Commands {
    let model: AppModel

    var body: some Commands {
        // 表示言語が変わったらメニューの文字列も作り直す
        let _ = model.appLanguage
        CommandGroup(replacing: .newItem) {
            Button(L("menu.addFiles")) { model.showFileImporter = true }
                .keyboardShortcut("o")
        }
        CommandMenu(L("menu.transcription")) {
            Button(L("action.start")) { model.start() }
                .keyboardShortcut("r")
                .disabled(!model.canStart)
            Button(L("action.cancel")) { model.cancel() }
                .keyboardShortcut(".")
                .disabled(!model.isRunning)
            Divider()
            Button(L("action.combine")) { model.showCombineSheet = true }
                .keyboardShortcut("s", modifiers: [.command, .shift])
                .disabled(!model.hasCompletedItems)
            Divider()
            Button(L("action.manageModels")) { model.showModelManager = true }
                .keyboardShortcut("m", modifiers: [.command, .option])
        }
    }
}

