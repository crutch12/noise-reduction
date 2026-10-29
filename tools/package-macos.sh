#!/bin/sh
# Noise Reduction.app с драйвером внутри, отдельно под arm64 и x64: .NET не собирает универсальный бинарник
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
version=${1:-1.0.0}
out="$root/dist/macos"
sh "$root/tools/build-native.sh"
cmake -S "$root/native/macos/driver" -B "$root/build/driver" -DCMAKE_BUILD_TYPE=Release -DDRIVER_VERSION="$version" >/dev/null
cmake --build "$root/build/driver" --config Release >/dev/null
rm -rf "$out" && mkdir -p "$out"
for arch in arm64 x64; do
    app="$out/$arch/Noise Reduction.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    dotnet publish "$root/app-avalonia" -c Release -r "osx-$arch" --self-contained -p:Version="$version" -o "$app/Contents/MacOS" >/dev/null
    # библиотеки собраны под оба процессора, в пакет кладём только нужную половину
    case $arch in arm64) slice=arm64 ;; *) slice=x86_64 ;; esac
    for name in librnnoise libnr_bridge libnr_deepfilter; do
        lipo "$root/build/$name.dylib" -thin $slice -output "$app/Contents/MacOS/$name.dylib"
    done
    cp -R "$root/build/driver/NoiseReduction.driver" "$app/Contents/Resources/"
    cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Noise Reduction</string>
  <key>CFBundleDisplayName</key><string>Noise Reduction</string>
  <key>CFBundleIdentifier</key><string>com.github.crutch12.noisereduction</string>
  <key>CFBundleExecutable</key><string>NoiseReduction</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Noise Reduction cleans the sound of your microphone and passes it to the Noise Reduction virtual microphone</string>
</dict>
</plist>
EOF
    # без сертификата Apple подпись ad-hoc, иначе на Apple Silicon приложение не запустится вовсе
    codesign --force --deep --sign - "$app"
    (cd "$out/$arch" && ditto -c -k --keepParent "Noise Reduction.app" "$out/NoiseReduction-$version-macos-$arch.zip")
done
ls -la "$out"
