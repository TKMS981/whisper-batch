#!/bin/zsh
# テストを実行する。
# Command Line Tools だけの環境では swift-testing のマクロプラグインが自動で読み込まれないため、明示的に指定する。
set -euo pipefail
cd "$(dirname "$0")/.."

extra=()
dev_dir="$(xcode-select -p 2>/dev/null || true)"
plugin="$dev_dir/usr/lib/swift/host/plugins/testing/libTestingMacros.dylib"
if [[ "$dev_dir" == *CommandLineTools* && -f "$plugin" ]]; then
  extra=(-Xswiftc -load-plugin-library -Xswiftc "$plugin")
fi

./scripts/fetch-lame.sh
swift test --arch arm64 "${extra[@]}" "$@"
