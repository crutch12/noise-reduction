#!/bin/sh
# Noise Reduction.app с драйвером внутри, отдельно под arm64 и x64: модели большие, половина чужого процессора не нужна
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
version=${1:-1.0.0}
out="$root/dist/macos"
sh "$root/tools/build-native.sh"
cmake -S "$root/native/macos/driver" -B "$root/build/driver" -DCMAKE_BUILD_TYPE=Release -DDRIVER_VERSION="$version" >/dev/null
cmake --build "$root/build/driver" --config Release >/dev/null
rm -rf "$out" && mkdir -p "$out"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
python3 "$root/tools/generate-macos-strings.py" "$work/strings.json"
sips -s format png "$root/app/assets/NoiseReduction.ico" --out "$work/icon.png" >/dev/null
mkdir "$work/AppIcon.iconset"
for size in 16 32 128 256 512; do
    sips -z $size $size "$work/icon.png" --out "$work/AppIcon.iconset/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$work/icon.png" --out "$work/AppIcon.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$work/AppIcon.iconset" -o "$work/AppIcon.icns"

for arch in arm64 x64; do
    app="$out/$arch/Noise Reduction.app"
    macos="$app/Contents/MacOS"
    mkdir -p "$macos" "$app/Contents/Resources"
    case $arch in arm64) slice=arm64 ;; *) slice=x86_64 ;; esac
    # библиотеки собраны под оба процессора, в пакет кладём только нужную половину
    for name in librnnoise libnr_bridge libnr_deepfilter; do
        lipo "$root/build/$name.dylib" -thin $slice -output "$macos/$name.dylib"
    done
    install_name_tool -id @rpath/libnr_bridge.dylib "$macos/libnr_bridge.dylib"
    xcrun swiftc -O -target "$slice-apple-macos13" -sdk "$(xcrun --show-sdk-path --sdk macosx)" \
        -import-objc-header "$root/app-macos/Bridge-Header.h" \
        -L "$macos" -lnr_bridge -Xlinker -rpath -Xlinker @executable_path \
        -o "$macos/NoiseReduction" "$root"/app-macos/*.swift
    cp -R "$root/build/driver/NoiseReduction.driver" "$app/Contents/Resources/"
    cp "$work/strings.json" "$work/AppIcon.icns" "$app/Contents/Resources/"
    mkdir -p "$app/Contents/Resources/licenses"
    cp "$root/LICENSE" "$root/installer/THIRD-PARTY-NOTICES.txt" "$app/Contents/Resources/"
    for name in RNNoise-COPYING DeepFilterNet-LICENSE-MIT DeepFilterNet-LICENSE-APACHE miniaudio-LICENSE libASPL-LICENSE libASPL-LICENSE.apple2012 libASPL-LICENSE.apple2020; do
        cp "$root/installer/licenses/$name.txt" "$app/Contents/Resources/licenses/"
    done
    cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Noise Reduction</string>
  <key>CFBundleDisplayName</key><string>Noise Reduction</string>
  <key>CFBundleIdentifier</key><string>com.github.crutch12.noisereduction</string>
  <key>CFBundleExecutable</key><string>NoiseReduction</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Noise Reduction cleans the sound of your microphone and passes it to the Noise Reduction virtual microphone</string>
</dict>
</plist>
EOF
    # без сертификата Apple подпись ad-hoc, иначе на Apple Silicon приложение не запустится вовсе
    # каждую часть подписываем отдельно; защищённый режим не даёт подменить библиотеки через переменные DYLD
    # проверка библиотек выключена: без Team ID система не загрузила бы и наши собственные
    for name in librnnoise libnr_bridge libnr_deepfilter; do codesign --force --options runtime --sign - "$macos/$name.dylib" 2>/dev/null; done
    # подпись от сборки драйвера не привязана к Info.plist, с такой coreaudiod его не загружает
    codesign --force --options runtime --sign - "$app/Contents/Resources/NoiseReduction.driver"
    codesign --force --options runtime --entitlements "$root/app-macos/NoiseReduction.entitlements" --sign - "$app"
    codesign --verify --deep --strict "$app"
    (cd "$out/$arch" && ditto -c -k --keepParent "Noise Reduction.app" "$out/NoiseReduction-$version-macos-$arch.zip")
done
ls -la "$out"
