#!/bin/sh
# сборка и тесты общей обработки на Linux и macOS, Windows собирается через native/build.cmd
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
out="$root/build"
mkdir -p "$out"
case "$(uname -s)" in
    Darwin) lib=dylib; shared="-dynamiclib"; arch="-arch arm64 -arch x86_64" ;;
    *) lib=so; shared="-shared"; arch="" ;;
esac

# тот же коммит RNNoise и та же модель, что внутри pyrnnoise 0.4.5 и в сборке под Windows ARM64
commit=70f1d256acd4b34a572f999a05c87bf00b67730d
model=0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37
sha() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
# скачанное лежит в .cache, хеш проверяется в любом случае
cache="$root/.cache"
mkdir -p "$cache"
fetch() {
    name=$1 hash=$2
    shift 2
    [ -f "$cache/$name" ] && [ "$(sha "$cache/$name")" = "$hash" ] && return 0
    for url in "$@"; do curl -fsSL --max-time 300 "$url" -o "$cache/$name.part" 2>/dev/null && mv "$cache/$name.part" "$cache/$name" && break || echo "$url failed"; done
}
# на Linux берём librnnoise.so из колеса pyrnnoise 0.4.5, как rnnoise.dll на Windows x64
if [ "$lib" = so ] && [ ! -f "$out/librnnoise.so" ]; then
    case "$(uname -m)" in
        x86_64) wheel=f1/8b/6498de7ef0a4670bbeae951bec9cfd2386e57ef386f53eec28e75be39790/pyrnnoise-0.4.5-py3-none-manylinux1_x86_64.whl; digest=c095114532f9fa21f621084c879d72c93aaa0c9dbe38b662a63577bada2c214e ;;
        aarch64) wheel=d7/69/96144ce909a371d9f0de8aceb18fb53188b8df14ef34fce909d345bafb42/pyrnnoise-0.4.5-py3-none-manylinux2014_aarch64.whl; digest=4b382d39b4963d38c8fe2f61639f634a5de0dd7dc731aca7ff1df48f9b1424cf ;;
        *) echo "Unsupported Linux architecture $(uname -m)"; exit 1 ;;
    esac
    name=$(basename "$wheel")
    fetch "$name" "$digest" "https://files.pythonhosted.org/packages/$wheel"
    [ "$(sha "$cache/$name")" = "$digest" ] || { echo 'pyrnnoise wheel checksum does not match PyPI'; exit 1; }
    unzip -p "$cache/$name" pyrnnoise-0.4.5.data/purelib/pyrnnoise/librnnoise.so > "$out/librnnoise.so"
fi
if [ ! -f "$out/librnnoise.$lib" ]; then
    fetch "rnnoise-$commit.tar.gz" f61ee0b3f4c4cd337303e003d333357c5eaf25ef5d75a742109ee59e9a0a3932 "https://codeload.github.com/xiph/rnnoise/tar.gz/$commit"
    fetch "rnnoise_data-$model.tar.gz" "$model" "https://media.xiph.org/rnnoise/models/rnnoise_data-$model.tar.gz" "https://modelscope.cn/models/pengzhendong/rnnoise/resolve/master/rnnoise_data-$model.tar.gz"
    [ "$(sha "$cache/rnnoise-$commit.tar.gz")" = f61ee0b3f4c4cd337303e003d333357c5eaf25ef5d75a742109ee59e9a0a3932 ] || { echo 'RNNoise source checksum does not match'; exit 1; }
    [ "$(sha "$cache/rnnoise_data-$model.tar.gz")" = "$model" ] || { echo 'RNNoise model checksum does not match'; exit 1; }
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    tar -xzf "$cache/rnnoise-$commit.tar.gz" -C "$work"
    src="$work/rnnoise-$commit"
    tar -xzf "$cache/rnnoise_data-$model.tar.gz" -C "$src"
    files=""
    for f in celt_lpc denoise kiss_fft parse_lpcnet_weights pitch rnn rnnoise_data rnnoise_tables nnet nnet_default; do files="$files $src/src/$f.c"; done
    # shellcheck disable=SC2086
    cc -O2 -fPIC $arch $shared -fvisibility=hidden -DRNNOISE_BUILD -I"$src/include" -I"$src/src" $files -lm -o "$out/librnnoise.$lib"
fi

cxx="c++ -std=c++17 -O2 -Wall -Wextra -Werror $arch -I$root/native"
# shellcheck disable=SC2086
$cxx "$root/native/tests/resampler_test.cpp" "$root/native/resampler.cpp" -o "$out/resampler_test"
# shellcheck disable=SC2086
$cxx "$root/native/tests/processor_test.cpp" "$root/native/processor.cpp" "$root/native/rnnoise_backend.cpp" -ldl -o "$out/processor_test"

# DeepFilterNet той же обёрткой, что на Windows, если есть Rust
if command -v cargo >/dev/null && [ ! -f "$out/libnr_deepfilter.$lib" ]; then
    cargo build --release --locked --manifest-path "$root/native/deepfilter/Cargo.toml" --target-dir "$out/deepfilter-target"
    cp "$out/deepfilter-target/release/libnr_deepfilter.$lib" "$out/"
fi

# miniaudio 0.11.25, один заголовок под MIT-0
fetch miniaudio-0.11.25.h ac7af4de748b7e26b777f37e01cee313a308a7296a3eb080e2906b320cc55c89 "https://raw.githubusercontent.com/mackron/miniaudio/0.11.25/miniaudio.h"
[ "$(sha "$cache/miniaudio-0.11.25.h")" = ac7af4de748b7e26b777f37e01cee313a308a7296a3eb080e2906b320cc55c89 ] || { echo 'miniaudio checksum does not match'; exit 1; }
mkdir -p "$out/include" && cp "$cache/miniaudio-0.11.25.h" "$out/include/miniaudio.h"
case "$lib" in
    dylib) system="-framework CoreFoundation -framework CoreAudio -framework AudioToolbox" ;;
    *) system="-ldl -lpthread -lm" ;;
esac
# shellcheck disable=SC2086
cc -O2 -fPIC $arch -I"$out/include" -c "$root/native/miniaudio.c" -o "$out/miniaudio.o"
# shellcheck disable=SC2086
$cxx -fPIC -fvisibility=hidden -isystem "$out/include" $shared "$root/native/bridge.cpp" "$root/native/processor.cpp" "$root/native/rnnoise_backend.cpp" "$root/native/deepfilter_backend.cpp" "$out/miniaudio.o" $system -o "$out/libnr_bridge.$lib"
# shellcheck disable=SC2086
$cxx "$root/native/tests/bridge_test.cpp" -L"$out" -lnr_bridge -Wl,-rpath,"$out" -o "$out/bridge_test"

if [ "${1:-}" = test ]; then
    "$out/resampler_test"
    "$out/processor_test" "$out/librnnoise.$lib"
    "$out/bridge_test" "$out"
fi
