# dmgbuild の設定（scripts/make-dmg.sh から呼ばれる）。
# -D app=<アプリのパス> -D background=<背景画像> -D icon=<ボリュームアイコン> で値を受け取る。
import os.path

app = defines["app"]  # noqa: F821 (dmgbuild が定義する)
app_name = os.path.basename(app)

format = "ULFO"  # LZFSE 圧縮（macOS 10.11 以降で開ける）
filesystem = "APFS"
files = [app]
symlinks = {"Applications": "/Applications"}
icon = defines.get("icon") or None  # noqa: F821
background = defines.get("background") or None  # noqa: F821

window_rect = ((200, 120), (600, 380))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
icon_size = 112
text_size = 13
arrange_by = None
icon_locations = {
    app_name: (160, 170),
    "Applications": (440, 170),
}
