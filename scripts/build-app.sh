#!/bin/zsh
# Whisper Batch.app を組み立てる（Apple Silicon / arm64 のみ）。
#
#   ./scripts/build-app.sh            # リリースビルド → build/Whisper Batch.app
#   ./scripts/build-app.sh debug      # デバッグビルド
#
# Xcode がなくても Command Line Tools（Swift 6.2 以降・macOS 27 SDK）で動く。
#
# 署名:
#   既定はアドホック署名（ローカル実行用、Hardened Runtime なし）。
#   環境変数 WB_SIGN_IDENTITY に Developer ID の証明書名を指定すると、アプリと同梱ライブラリを
#   その証明書で署名し、Hardened Runtime（--options runtime）と署名時刻（--timestamp）を付ける。
#     WB_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" ./scripts/build-app.sh
#   WB_DISABLE_LIBRARY_VALIDATION=1 を併せて指定すると、別の署名のライブラリも読み込めるようにする
#   （利用者が libLAME.dylib を差し替えられるようにする場合。README の「Hardened Runtime での動作」を参照）。
#   公証は scripts/make-dmg.sh で行う。
set -euo pipefail
cd "$(dirname "$0")/.."

config="${1:-release}"
app_name="Whisper Batch"
out_dir="build"
app="$out_dir/$app_name.app"
identity="${WB_SIGN_IDENTITY:-}"

# 証明書はビルドを始める前に確かめる（見つからないなら時間をかけてビルドしない）
if [[ -n "$identity" ]] && ! security find-identity -v -p codesigning | grep -qF "$identity"; then
  echo "署名用の証明書がキーチェーンに見つかりません: $identity" >&2
  echo "（security find-identity -v -p codesigning で表示される名前をそのまま指定してください）" >&2
  exit 1
fi

# MP3 エンコーダー（LAME）のソースを取得・検証する（取得済みなら何もしない）
./scripts/fetch-lame.sh

echo "==> swift build ($config, arm64)"
swift build -c "$config" --arch arm64 --product WhisperBatch
bin_dir="$(swift build -c "$config" --arch arm64 --show-bin-path)"

exe="$bin_dir/WhisperBatch"
framework="$bin_dir/whisper.framework"
lame="$bin_dir/libLAME.dylib"
sherpa="$bin_dir/libsherpa-onnx-c-api.dylib"
[[ -f "$exe" ]] || { echo "実行ファイルが見つかりません: $exe" >&2; exit 1; }
[[ -d "$framework" ]] || { echo "whisper.framework が見つかりません: $framework" >&2; exit 1; }
[[ -f "$lame" ]] || { echo "libLAME.dylib が見つかりません: $lame" >&2; exit 1; }
[[ -f "$sherpa" ]] || { echo "libsherpa-onnx-c-api.dylib が見つかりません: $sherpa" >&2; exit 1; }

echo "==> $app を作成"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks" "$app/Contents/Resources"
cp "App/Info.plist" "$app/Contents/Info.plist"
cp "$exe" "$app/Contents/MacOS/WhisperBatch"
# シンボリックリンク構造を保ったままコピーする
ditto "$framework" "$app/Contents/Frameworks/whisper.framework"
# LAME は LGPL のため、差し替えできるよう動的ライブラリのまま同梱する
cp "$lame" "$app/Contents/Frameworks/libLAME.dylib"
# sherpa-onnx（ReazonSpeech の実行用、onnxruntime を静的に含む）
cp "$sherpa" "$app/Contents/Frameworks/libsherpa-onnx-c-api.dylib"

# whisper.framework と sherpa-onnx は x86_64 も含むユニバーサルバイナリなので arm64 だけにする
for fw_bin in "$app/Contents/Frameworks/whisper.framework/Versions/A/whisper" "$app/Contents/Frameworks/libsherpa-onnx-c-api.dylib"; do
  if lipo -archs "$fw_bin" | grep -q x86_64; then
    lipo "$fw_bin" -thin arm64 -output "$fw_bin.arm64"
    mv "$fw_bin.arm64" "$fw_bin"
  fi
done

# アプリ内の Frameworks を参照できるようにする
if ! otool -l "$app/Contents/MacOS/WhisperBatch" | grep -q "@executable_path/../Frameworks"; then
  install_name_tool -add_rpath "@executable_path/../Frameworks" "$app/Contents/MacOS/WhisperBatch"
fi

cp "App/ThirdPartyNotices.txt" "$app/Contents/Resources/ThirdPartyNotices.txt"
cp "ThirdParty/LAME/Sources/CLAME/COPYING" "$app/Contents/Resources/LAME-COPYING.txt"
cp App/Licenses/*.txt "$app/Contents/Resources/"
# 表示文字列（日本語・英語）
for lproj in Localization/*.lproj; do
  ditto "$lproj" "$app/Contents/Resources/$(basename "$lproj")"
done
if [[ -f "App/AppIcon.icns" ]]; then
  cp "App/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
fi

# デバッグ用の参照情報（ソースやオブジェクトファイルの絶対パスを含む）を取り除く。
# 残すとビルドした Mac のユーザー名やフォルダ構成が配布物に含まれてしまう。
strip -S -x "$app/Contents/MacOS/WhisperBatch"
strip -S -x "$app/Contents/Frameworks/libLAME.dylib"

# 同梱ライブラリを先に、アプリ本体を最後に署名する
nested=(
  "$app/Contents/Frameworks/whisper.framework"
  "$app/Contents/Frameworks/libLAME.dylib"
  "$app/Contents/Frameworks/libsherpa-onnx-c-api.dylib"
)
entitlements="App/WhisperBatch.entitlements"

if [[ -n "$identity" ]]; then
  echo "==> Developer ID 署名（Hardened Runtime・App Sandbox 有効）: $identity"
  if [[ "${WB_DISABLE_LIBRARY_VALIDATION:-0}" == "1" ]]; then
    echo "    ライブラリ検証を無効にします（com.apple.security.cs.disable-library-validation）"
    sign_tmp="$(mktemp -d)"
    trap 'rm -rf "$sign_tmp"' EXIT
    cp "$entitlements" "$sign_tmp/WhisperBatch.entitlements"
    /usr/libexec/PlistBuddy -c "Add :com.apple.security.cs.disable-library-validation bool true" "$sign_tmp/WhisperBatch.entitlements"
    entitlements="$sign_tmp/WhisperBatch.entitlements"
  fi
  sign=(codesign --force --sign "$identity" --options runtime --timestamp)
else
  echo "==> アドホック署名（App Sandbox 有効）"
  sign=(codesign --force --sign - --timestamp=none)
fi
for item in "${nested[@]}"; do
  "${sign[@]}" "$item"
done
"${sign[@]}" --entitlements "$entitlements" "$app"
codesign --verify --strict --deep --verbose=1 "$app"
if [[ -n "$identity" ]]; then
  # Hardened Runtime が付いているか確認する（公証の必須条件）
  codesign --display --verbose=2 "$app" 2>&1 | grep -q "flags=.*runtime" || {
    echo "Hardened Runtime が有効になっていません" >&2
    exit 1
  }
fi

echo "==> 完了: $app"
lipo -archs "$app/Contents/MacOS/WhisperBatch"
