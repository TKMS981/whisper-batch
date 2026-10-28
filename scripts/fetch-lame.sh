#!/bin/zsh
# LAME 4.0 の公式ソースを取得し、SHA-256 を検証して ThirdParty/LAME/Sources/CLAME に展開する。
# 展開済みなら何もしない。configure などの配布物内のスクリプトは実行しない。
set -euo pipefail
cd "$(dirname "$0")/.."

version="4.0"
url="https://downloads.sourceforge.net/project/lame/lame/${version}/lame-${version}.tar.gz"
sha256="3df5124d5ad3a98312ffd7ba6a9b36230e4f8a3e66d3ce0f425e336c32d216eb"
dest="ThirdParty/LAME/Sources/CLAME"
stamp="$dest/.lame-$version"

[[ -f "$stamp" ]] && exit 0

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
echo "==> LAME $version を取得"
curl -fsSL --retry 3 -o "$work/lame.tar.gz" "$url"
actual="$(shasum -a 256 "$work/lame.tar.gz" | awk '{print $1}')"
if [[ "$actual" != "$sha256" ]]; then
  echo "LAME のチェックサムが一致しません（期待: $sha256 / 実際: $actual）" >&2
  exit 1
fi
mkdir -p "$work/src"
tar -xzf "$work/lame.tar.gz" -C "$work/src"
src="$work/src/lame-$version"

rm -rf "$dest"
mkdir -p "$dest/include"
# libmp3lame/Makefile.am の libmp3lame_la_SOURCES と noinst_HEADERS
for f in VbrTag bitstream encoder fft gain_analysis id3tag lame newmdct presets psymodel quantize \
         quantize_pvt reservoir set_get tables takehiro util vbrquantize version mpglib_interface; do
  cp "$src/libmp3lame/$f.c" "$dest/"
done
cp "$src"/libmp3lame/*.h "$dest/"
cp "$src/include/lame.h" "$dest/include/lame.h"
# 一部のソースが参照するヘッダーだけ（デコーダーや SSE のコードはコンパイルしない）
mkdir -p "$dest/mpglib" "$dest/vector"
cp "$src"/mpglib/*.h "$dest/mpglib/"
cp "$src/libmp3lame/vector/lame_intrin.h" "$dest/vector/"
cp "ThirdParty/LAME/config.h" "$dest/config.h"
cp "$src/COPYING" "$dest/COPYING"
cp "$src/README" "$dest/README"
touch "$stamp"
echo "==> 展開しました: $dest"
