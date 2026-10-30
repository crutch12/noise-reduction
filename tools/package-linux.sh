#!/bin/sh
# архив для Linux под архитектуру машины, на которой собирается
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
version=${1:-1.0.0}
case "$(uname -m)" in
    x86_64) arch=x64 ;;
    aarch64) arch=arm64 ;;
    *) echo "Unsupported Linux architecture $(uname -m)"; exit 1 ;;
esac
out="$root/dist/linux"
app="$out/noise-reduction"
sh "$root/tools/build-native.sh"
rm -rf "$out" && mkdir -p "$app"
dotnet publish "$root/app-avalonia" -c Release -r "linux-$arch" --self-contained -p:Version="$version" -o "$app" >/dev/null
cp "$root/build/librnnoise.so" "$root/build/libnr_bridge.so" "$root/build/libnr_deepfilter.so" "$app/"
cp "$root/LICENSE" "$root/installer/THIRD-PARTY-NOTICES.txt" "$app/"
mkdir -p "$app/licenses"
for name in RNNoise-COPYING pyrnnoise-LICENSE DeepFilterNet-LICENSE-MIT DeepFilterNet-LICENSE-APACHE miniaudio-LICENSE dotnet-LICENSE dotnet-ThirdPartyNotices Avalonia-LICENSE SkiaSharp-LICENSE HarfBuzzSharp-LICENSE MicroCom-LICENSE TmdsDbus-LICENSE; do
    cp "$root/installer/licenses/$name.txt" "$app/licenses/"
done
tar -czf "$out/NoiseReduction-$version-linux-$arch.tar.gz" -C "$out" noise-reduction
ls -la "$out"
