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
if [ ! -f "$out/librnnoise.$lib" ]; then
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    curl -fsSL "https://codeload.github.com/xiph/rnnoise/tar.gz/$commit" -o "$work/source.tar.gz"
    [ "$(sha "$work/source.tar.gz")" = f61ee0b3f4c4cd337303e003d333357c5eaf25ef5d75a742109ee59e9a0a3932 ] || { echo 'RNNoise source checksum does not match'; exit 1; }
    for url in "https://media.xiph.org/rnnoise/models/rnnoise_data-$model.tar.gz" "https://modelscope.cn/models/pengzhendong/rnnoise/resolve/master/rnnoise_data-$model.tar.gz"; do
        curl -fsSL --max-time 300 "$url" -o "$work/model.tar.gz" && break || echo "$url failed"
    done
    [ "$(sha "$work/model.tar.gz")" = "$model" ] || { echo 'RNNoise model checksum does not match'; exit 1; }
    tar -xzf "$work/source.tar.gz" -C "$work"
    src="$work/rnnoise-$commit"
    tar -xzf "$work/model.tar.gz" -C "$src"
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

if [ "${1:-}" = test ]; then
    "$out/resampler_test"
    "$out/processor_test" "$out/librnnoise.$lib"
fi
