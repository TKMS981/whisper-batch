import SwiftUI
import WhisperBatchCore

struct ContentView: View {
    @Environment(AppModel.self) private var model
    /// 右側の幅が狭いときは設定や操作ボタンを縦に並べる
    @ViewState private var isCompact = false

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            QueueSidebar()
                .modifier(WindowDropTarget(column: .sidebar))
                .navigationSplitViewColumnWidth(min: 260, ideal: 320, max: 480)
        } detail: {
            // 結果表示を主役にし、設定（上）と保存操作（下）はそれぞれ内容の高さだけ使う
            TranscriptDetail()
                // 狭いウィンドウでも上下のパネルを押し出さないよう、中央は 0 まで縮められるようにする
                .frame(maxWidth: .infinity, minHeight: 0, maxHeight: .infinity)
                .clipped()
                .safeAreaInset(edge: .top, spacing: 0) {
                    VStack(spacing: 0) {
                        RecognitionSettingsPanel(isCompact: isCompact)
                        Divider()
                    }
                    .background(.background)
                }
                .safeAreaInset(edge: .bottom, spacing: 0) {
                    VStack(spacing: 0) {
                        Divider()
                        ExportActionBar(isCompact: isCompact)
                    }
                    .background(.background)
                }
                .onGeometryChange(for: Bool.self) { $0.size.width < 700 } action: { isCompact = $0 }
                .modifier(WindowDropTarget(column: .detail))
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    model.showFileImporter = true
                } label: {
                    Label(L("toolbar.addFiles"), systemImage: "plus")
                }
                .help(L("toolbar.addFiles.help"))

                if model.isRunning {
                    Button {
                        model.cancel()
                    } label: {
                        Label(L("action.cancel"), systemImage: "stop.fill")
                    }
                    .help(L("toolbar.cancel.help"))
                } else {
                    Button {
                        model.start()
                    } label: {
                        Label(L("action.start"), systemImage: "play.fill")
                    }
                    .disabled(!model.canStart)
                    .help(model.startBlockReason ?? L("toolbar.start.help"))
                }
            }
        }
        .fileImporter(
            isPresented: $model.showFileImporter,
            allowedContentTypes: AudioFormats.allowedContentTypes + [.folder],
            allowsMultipleSelection: true
        ) { result in
            switch result {
            case .success(let urls): model.addFiles(urls)
            case .failure(let error):
                model.alert = .init(title: L("add.failed.title"), message: userFacingMessage(for: error))
            }
        }
        .sheet(isPresented: $model.showCombineSheet) {
            CombineSheet()
        }
        .sheet(isPresented: $model.showModelManager) {
            ModelManagerSheet()
        }
        .modifier(DownloadConfirmation())
        .alert(
            model.alert?.title ?? "",
            isPresented: Binding(get: { model.alert != nil }, set: { if !$0 { model.alert = nil } }),
            presenting: model.alert
        ) { _ in
            Button(L("common.ok")) {}
        } message: { info in
            Text(info.message)
        }
    }
}

/// ウィンドウ内のどこにドロップしてもキューに追加する受け口。
///
/// macOS の NavigationSplitView は列ごとに別のビュー階層で描画されるため、
/// 分割ビュー全体ではなく各列に付ける。どちらかの列の上にあるときは両方の列を強調表示する。
private struct WindowDropTarget: ViewModifier {
    enum Column { case sidebar, detail }

    @Environment(AppModel.self) private var model
    @Environment(\.motion) private var motion
    let column: Column

    func body(content: Content) -> some View {
        content
            .dropDestination(for: URL.self) { urls, _ in
                model.addFiles(urls)
                return !urls.isEmpty
            } isTargeted: { targeted in
                model.setDropTargeted(targeted, inSidebar: column == .sidebar)
            }
            .overlay {
                // 強調表示は短くフェードで出し入れする（動きを減らす設定では即座に表示）
                ZStack {
                    if model.isDropTargeted {
                        DropHighlight(showsLabel: column == .detail)
                            .transition(.opacity)
                    }
                }
                .animation(motion.dropHighlightAnimation, value: model.isDropTargeted)
                .allowsHitTesting(false)
            }
    }
}

/// ドラッグ中に重ねる表示（クリックやスクロールは下のビューに通す）
private struct DropHighlight: View {
    let showsLabel: Bool

    var body: some View {
        ZStack {
            if showsLabel {
                // 案内は見えている範囲（サイドバーに隠れない部分）の中央に置く
                Label(L("drop.overlay"), systemImage: "tray.and.arrow.down.fill")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 18)
                    .padding(.vertical, 12)
                    .background(Color.accentColor, in: Capsule())
                    .fixedSize()
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background {
            // 枠は列全体に描く
            Rectangle()
                .strokeBorder(Color.accentColor, lineWidth: 3)
                .background(Color.accentColor.opacity(0.06))
                .ignoresSafeArea()
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

/// モデルのダウンロード前の確認（通信先・サイズ・保存先を示す）
struct DownloadConfirmation: ViewModifier {
    @Environment(AppModel.self) private var model

    func body(content: Content) -> some View {
        @Bindable var model = model
        content.alert(
            L("download.confirm.title"),
            isPresented: Binding(get: { model.downloadConfirmation != nil },
                                 set: { if !$0 { model.downloadConfirmation = nil } }),
            presenting: model.downloadConfirmation
        ) { target in
            Button(L("download.confirm.download")) { model.models.download(target) }
            Button(L("common.cancel"), role: .cancel) {}
        } message: { target in
            Text(Self.message(for: target, storagePath: model.models.storagePath))
        }
    }

    static func message(for target: WhisperModel, storagePath: String) -> String {
        L("download.confirm.message", target.displayName, target.sizeText, target.requiredSpaceText, storagePath)
    }
}
