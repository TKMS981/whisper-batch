#!/bin/zsh
# 配布用のディスクイメージ（DMG）を作る。
#
#   ./scripts/make-dmg.sh             # アプリをビルドしてから DMG を作成
#   ./scripts/make-dmg.sh --no-build  # build/Whisper Batch.app をそのまま使う
#
# 出力: build/WhisperBatch-<バージョン>.dmg と、その SHA-256（.sha256）
# 初回は dmgbuild（PyPI）を .build/dmg-venv に取得する。
# DMG を開くとアプリと「アプリケーション」フォルダへのリンクが並び、ドラッグでインストールできる。
set -euo pipefail
cd "$(dirname "$0")/.."

app_name="Whisper Batch"
app="build/$app_name.app"

if [[ "${1:-}" != "--no-build" ]]; then
  ./scripts/build-app.sh release
fi
[[ -d "$app" ]] || { echo "アプリが見つかりません: $app" >&2; exit 1; }
codesign --verify --strict "$app"

version="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$app/Contents/Info.plist")"
dmg="build/WhisperBatch-$version.dmg"
volname="$app_name $version"

# 配布物にビルドした Mac のホームディレクトリのパスが残っていないか確認する
if python3 -I - "$app" "$HOME" <<'EOF'
import os, sys
root, home = sys.argv[1], sys.argv[2].encode()
hits = []
for d, _, files in os.walk(root):
    for f in files:
        p = os.path.join(d, f)
        if os.path.islink(p):
            continue
        with open(p, "rb") as fh:
            if home in fh.read():
                hits.append(p)
for h in hits:
    print("ローカルパスを含むファイル:", h, file=sys.stderr)
sys.exit(1 if hits else 0)
EOF
then :; else
  echo "配布物にローカルパスが含まれています。中止します。" >&2
  exit 1
fi

# DMG の作成には dmgbuild を使う（Finder を操作せずにウィンドウの配置や背景を設定できる）。
# .build/ 内の仮想環境に、バージョンを固定して入れる。
venv=".build/dmg-venv"
dmgbuild_version="1.6.5"
if ! "$venv/bin/dmgbuild" --help >/dev/null 2>&1 || \
   [[ "$("$venv/bin/python3" -c 'import importlib.metadata as m; print(m.version("dmgbuild"))')" != "$dmgbuild_version" ]]; then
  echo "==> dmgbuild $dmgbuild_version を準備"
  rm -rf "$venv"
  python3 -m venv "$venv"
  "$venv/bin/pip" install --quiet --disable-pip-version-check "dmgbuild==$dmgbuild_version"
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "==> 背景画像を生成"
swift scripts/make-dmg-background.swift "$work" 2>/dev/null
tiffutil -cathidpicheck "$work/background.png" "$work/background@2x.png" -out "$work/background.tiff" >/dev/null 2>&1

echo "==> DMG を作成: $dmg"
rm -f "$dmg"
"$venv/bin/dmgbuild" -s scripts/dmg-settings.py \
  -D app="$app" -D background="$work/background.tiff" -D icon="App/AppIcon.icns" \
  "$volname" "$dmg"
hdiutil verify -quiet "$dmg"

(cd build && shasum -a 256 "$(basename "$dmg")" > "$(basename "$dmg").sha256")
echo "==> 完了"
ls -lh "$dmg"
cat "$dmg.sha256"
