#!/bin/sh
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
work="$root/.claude/macos-consumer-tests"
[ "$(uname -s)" = Darwin ] || { echo "Run on macOS" >&2; exit 1; }
mkdir -p "$work"
cp "$root/tools/tests/macos_microphone_users.swift" "$work/main.swift"
xcrun swiftc -target "$(uname -m)-apple-macos13" -sdk "$(xcrun --show-sdk-path --sdk macosx)" \
    -o "$work/test" "$work/main.swift" "$root/app-macos/MicrophoneUsers.swift"
"$work/test" "$@"
