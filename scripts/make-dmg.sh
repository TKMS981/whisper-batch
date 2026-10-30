#!/bin/zsh
# 配布用のディスクイメージ（DMG）を作る。
#
#   ./scripts/make-dmg.sh             # アプリをビルドしてから DMG を作成
#   ./scripts/make-dmg.sh --no-build  # build/Whisper Batch.app をそのまま使う
#
# 出力: build/WhisperBatch-<バージョン>.dmg と、その SHA-256（.sha256）
# 初回は dmgbuild（PyPI）を .build/dmg-venv に取得する。
# DMG を開くとアプリと「アプリケーション」フォルダへのリンクが並び、ドラッグでインストールできる。
#
# Developer ID 署名と公証（どちらも任意。未指定ならアドホック署名の DMG を作る）:
#   WB_SIGN_IDENTITY  Developer ID の証明書名。アプリ（build-app.sh に渡る）と DMG をこの証明書で署名する。
#   WB_NOTARY_PROFILE notarytool のキーチェーンプロファイル名。指定するとアプリと DMG を公証し、
#                     公証の結果（チケット）を両方に添付（staple）する。WB_SIGN_IDENTITY が必要。
#                     プロファイルは事前に作っておく:
#                       xcrun notarytool store-credentials <名前> --apple-id <Apple ID> --team-id <Team ID>
#   例: WB_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" WB_NOTARY_PROFILE=whisper-batch ./scripts/make-dmg.sh
set -euo pipefail
cd "$(dirname "$0")/.."

identity="${WB_SIGN_IDENTITY:-}"
notary_profile="${WB_NOTARY_PROFILE:-}"
if [[ -n "$notary_profile" && -z "$identity" ]]; then
  echo "公証には Developer ID 署名が必要です。WB_SIGN_IDENTITY も指定してください。" >&2
  exit 1
fi
if [[ -n "$notary_profile" ]] && ! xcrun notarytool history --keychain-profile "$notary_profile" >/dev/null 2>&1; then
  echo "notarytool のキーチェーンプロファイルを使えません: $notary_profile" >&2
  echo "（xcrun notarytool store-credentials $notary_profile ... で作成してください）" >&2
  exit 1
fi

# 公証を申請して完了を待ち、受け付けられなければログを表示して中止する
notarize() {
  local file="$1"
  echo "==> 公証を申請: $file（数分かかることがあります）"
  local result sub_status submission_id
  result="$(mktemp)"
  xcrun notarytool submit "$file" --keychain-profile "$notary_profile" --wait --output-format plist > "$result"
  sub_status="$(/usr/libexec/PlistBuddy -c 'Print :status' "$result" 2>/dev/null || echo unknown)"
  submission_id="$(/usr/libexec/PlistBuddy -c 'Print :id' "$result" 2>/dev/null || echo "")"
  rm -f "$result"
  if [[ "$sub_status" != "Accepted" ]]; then
    echo "公証が受け付けられませんでした（状態: $sub_status）" >&2
    [[ -n "$submission_id" ]] && xcrun notarytool log "$submission_id" --keychain-profile "$notary_profile" >&2 || true
    exit 1
  fi
  echo "    受け付けられました（ID: $submission_id）"
}

app_name="Whisper Batch"
app="build/$app_name.app"

if [[ "${1:-}" != "--no-build" ]]; then
  ./scripts/build-app.sh release
fi
[[ -d "$app" ]] || { echo "アプリが見つかりません: $app" >&2; exit 1; }
codesign --verify --strict --deep "$app"
if [[ -n "$identity" ]]; then
  # --no-build のときに、別の証明書やアドホック署名のアプリを使っていないか確認する
  codesign --display --verbose=2 "$app" 2>&1 | grep -qF "Authority=$identity" || {
    echo "アプリが $identity で署名されていません。--no-build を外して作り直してください。" >&2
    exit 1
  }
fi

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

if [[ -n "$notary_profile" ]]; then
  # アプリにもチケットを添付しておくと、DMG からコピーした後にオフラインでも Gatekeeper の確認を通る
  ditto -c -k --keepParent "$app" "$work/app.zip"
  notarize "$work/app.zip"
  xcrun stapler staple "$app"
  xcrun stapler validate "$app"
fi

echo "==> 背景画像を生成"
swift scripts/make-dmg-background.swift "$work" 2>/dev/null
tiffutil -cathidpicheck "$work/background.png" "$work/background@2x.png" -out "$work/background.tiff" >/dev/null 2>&1

echo "==> DMG を作成: $dmg"
rm -f "$dmg"
"$venv/bin/dmgbuild" -s scripts/dmg-settings.py \
  -D app="$app" -D background="$work/background.tiff" -D icon="App/AppIcon.icns" \
  "$volname" "$dmg"
hdiutil verify -quiet "$dmg"

if [[ -n "$identity" ]]; then
  echo "==> DMG に署名"
  codesign --force --sign "$identity" --timestamp "$dmg"
  codesign --verify --strict "$dmg"
fi
if [[ -n "$notary_profile" ]]; then
  notarize "$dmg"
  xcrun stapler staple "$dmg"
  xcrun stapler validate "$dmg"
  echo "==> Gatekeeper の評価"
  spctl --assess --type open --context context:primary-signature --verbose=2 "$dmg"
fi

(cd build && shasum -a 256 "$(basename "$dmg")" > "$(basename "$dmg").sha256")
echo "==> 完了"
ls -lh "$dmg"
cat "$dmg.sha256"
