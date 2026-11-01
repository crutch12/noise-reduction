#!/bin/sh
# архив для Linux под архитектуру машины, на которой собирается
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
version=${1:-1.0.0}
case "$version" in ''|*[!A-Za-z0-9._+-]*|*..*) echo "Invalid package version" >&2; exit 1 ;; esac
case "$(uname -m)" in
    x86_64) arch=x64 ;;
    aarch64) arch=arm64 ;;
    *) echo "Unsupported Linux architecture $(uname -m)"; exit 1 ;;
esac
out="$root/dist/linux"
app="$out/noise-reduction"
sh "$root/tools/build-native.sh"
[ ! -L "$out" ] || { echo "Package output is a symlink: $out" >&2; exit 1; }
rm -rf "$out" && mkdir -p "$app"
dotnet publish "$root/app-avalonia" -c Release -r "linux-$arch" --self-contained -p:Version="$version" -p:SourceRevisionId="$(git -C "$root" rev-parse HEAD 2>/dev/null || true)" -o "$app" >/dev/null
cp "$root/build/librnnoise.so" "$root/build/libnr_bridge.so" "$root/build/libnr_deepfilter.so" "$app/"
cp "$root/LICENSE" "$root/installer/THIRD-PARTY-NOTICES.txt" "$app/"
mkdir -p "$app/licenses"
for name in RNNoise-COPYING pyrnnoise-LICENSE DeepFilterNet-LICENSE-MIT DeepFilterNet-LICENSE-APACHE miniaudio-LICENSE dotnet-LICENSE dotnet-ThirdPartyNotices Avalonia-LICENSE SukiUI-LICENSE Noto-LICENSE NotoCJK-LICENSE SkiaSharp-LICENSE HarfBuzzSharp-LICENSE MicroCom-LICENSE TmdsDbus-LICENSE; do
    cp "$root/installer/licenses/$name.txt" "$app/licenses/"
done
cp "$root/tools/install-linux.sh" "$app/install.sh"
cp "$root/tools/uninstall-linux.sh" "$app/uninstall.sh"
cp "$root/app/assets/Microphone.png" "$app/NoiseReduction.png"
chmod 755 "$app/NoiseReduction" "$app/install.sh" "$app/uninstall.sh"
printf '%s\n' "$version" > "$app/VERSION"
uname -m > "$app/ARCHITECTURE"
[ -z "$(find "$app" -type l -print -quit)" ] || { echo "Package symlinks are not allowed" >&2; exit 1; }
(
    cd "$app"
    find . -type f ! -name MANIFEST ! -name SHA256SUMS -printf '%P\n' | LC_ALL=C sort > MANIFEST
    { cat MANIFEST; printf '%s\n' MANIFEST; } | while IFS= read -r file; do sha256sum -- "$file"; done > SHA256SUMS
)
tar -czf "$out/NoiseReduction-$version-linux-$arch.tar.gz" -C "$out" noise-reduction
ls -la "$out"
