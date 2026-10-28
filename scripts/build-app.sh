#!/bin/zsh
# Whisper Batch.app を組み立てる（Apple Silicon / arm64 のみ）。
#
#   ./scripts/build-app.sh            # リリースビルド → build/Whisper Batch.app
#   ./scripts/build-app.sh debug      # デバッグビルド
#
# Xcode がなくても Command Line Tools（Swift 6.2 以降・macOS 27 SDK）で動く。
# 署名はアドホック署名（ローカル実行用）。配布する場合は Developer ID で署名し公証すること。
set -euo pipefail
cd "$(dirname "$0")/.."

config="${1:-release}"
app_name="Whisper Batch"
out_dir="build"
app="$out_dir/$app_name.app"

# MP3 エンコーダー（LAME）のソースを取得・検証する（取得済みなら何もしない）
./scripts/fetch-lame.sh

echo "==> swift build ($config, arm64)"
swift build -c "$config" --arch arm64 --product WhisperBatch
bin_dir="$(swift build -c "$config" --arch arm64 --show-bin-path)"

exe="$bin_dir/WhisperBatch"
framework="$bin_dir/whisper.framework"
lame="$bin_dir/libLAME.dylib"
[[ -f "$exe" ]] || { echo "実行ファイルが見つかりません: $exe" >&2; exit 1; }
[[ -d "$framework" ]] || { echo "whisper.framework が見つかりません: $framework" >&2; exit 1; }
[[ -f "$lame" ]] || { echo "libLAME.dylib が見つかりません: $lame" >&2; exit 1; }

echo "==> $app を作成"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks" "$app/Contents/Resources"
cp "App/Info.plist" "$app/Contents/Info.plist"
cp "$exe" "$app/Contents/MacOS/WhisperBatch"
# シンボリックリンク構造を保ったままコピーする
ditto "$framework" "$app/Contents/Frameworks/whisper.framework"
# LAME は LGPL のため、差し替えできるよう動的ライブラリのまま同梱する
cp "$lame" "$app/Contents/Frameworks/libLAME.dylib"

# whisper.framework は x86_64 も含むユニバーサルバイナリなので arm64 だけにする
fw_bin="$app/Contents/Frameworks/whisper.framework/Versions/A/whisper"
if lipo -archs "$fw_bin" | grep -q x86_64; then
  lipo "$fw_bin" -thin arm64 -output "$fw_bin.arm64"
  mv "$fw_bin.arm64" "$fw_bin"
fi

# アプリ内の Frameworks を参照できるようにする
if ! otool -l "$app/Contents/MacOS/WhisperBatch" | grep -q "@executable_path/../Frameworks"; then
  install_name_tool -add_rpath "@executable_path/../Frameworks" "$app/Contents/MacOS/WhisperBatch"
fi

cp "App/ThirdPartyNotices.txt" "$app/Contents/Resources/ThirdPartyNotices.txt"
cp "ThirdParty/LAME/Sources/CLAME/COPYING" "$app/Contents/Resources/LAME-COPYING.txt"
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

echo "==> アドホック署名（App Sandbox 有効）"
codesign --force --sign - --timestamp=none "$app/Contents/Frameworks/whisper.framework"
codesign --force --sign - --timestamp=none "$app/Contents/Frameworks/libLAME.dylib"
codesign --force --sign - --timestamp=none --entitlements "App/WhisperBatch.entitlements" "$app"
codesign --verify --strict --verbose=1 "$app"

echo "==> 完了: $app"
lipo -archs "$app/Contents/MacOS/WhisperBatch"
