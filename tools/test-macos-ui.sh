#!/bin/sh
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
work="$root/.claude/macos-ui-tests"
[ "$(uname -s)" = Darwin ] || { echo "Run on macOS" >&2; exit 1; }
[ -f "$root/build/libnr_bridge.dylib" ] || { echo "Run tools/build-native.sh first" >&2; exit 1; }
mkdir -p "$work"
cp "$root/tools/tests/macos_picker_layout.swift" "$work/main.swift"
xcrun swiftc -target "$(uname -m)-apple-macos13" -sdk "$(xcrun --show-sdk-path --sdk macosx)" \
    -import-objc-header "$root/app-macos/Bridge-Header.h" -L "$root/build" -lnr_bridge \
    -Xlinker -rpath -Xlinker "$root/build" -o "$work/test" "$work/main.swift" \
    "$root/app-macos/Model.swift" "$root/app-macos/Controller.swift" "$root/app-macos/VoiceTest.swift" \
    "$root/app-macos/TrayGlyph.swift" "$root/app-macos/Views.swift" "$root/app-macos/MicrophoneUsers.swift"
"$work/test" "$work/pickers.png"
