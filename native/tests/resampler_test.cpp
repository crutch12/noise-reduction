#include "resampler.h"
#include <algorithm>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <vector>

static void check(bool ok, const char* text) { if (!ok) throw std::runtime_error(text); }
static const double pi = 3.14159265358979323846;

// тон через пересчёт туда и обратно блоками разного размера; синусоида подбирается с любой фазой,
// поэтому дробная задержка при 44,1 кГц не мешает, а щелчки, пропуски и искажения остаются в остатке
int main() {
    try {
        for (uint32_t rate : { 8000u, 16000u, 24000u, 32000u, 44100u, 48000u }) {
            for (double tone : { 100.0, 1000.0, 3000.0 }) {
                if (tone > rate * 0.4) continue;
                const size_t total = rate * 2;
                std::vector<float> input(total), output(total);
                for (size_t i = 0; i < total; ++i) input[i] = 0.5f * static_cast<float>(std::sin(2 * pi * tone * i / rate));
                nr::RateStage stage(rate, 1024);
                size_t wide_total = 0, offset = 0, step = 0;
                const size_t sizes[] = { 160, 441, 480, 1, 1024, 333, 10 };
                while (offset < total) {
                    size_t n = std::min(sizes[step++ % 7], total - offset);
                    stage.process(input.data() + offset, output.data() + offset, n, [&](const float* in, float* out, size_t count) {
                        for (size_t i = 0; i < count; ++i) out[i] = in[i];
                        wide_total += count;
                    });
                    offset += n;
                }
                check(std::abs(wide_total - static_cast<double>(total) * 48000 / rate) < 4, "Model saw a wrong number of 48 kHz samples");
                double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0;
                const double w = 2 * pi * tone / rate;
                for (size_t i = rate / 2; i < total; ++i) { double s = std::sin(w * i), c = std::cos(w * i); ss += s * s; sc += s * c; cc += c * c; ys += output[i] * s; yc += output[i] * c; }
                const double det = std::max(ss * cc - sc * sc, 1e-12), a = (ys * cc - yc * sc) / det, b = (yc * ss - ys * sc) / det;
                double error = 0, power = 0;
                for (size_t i = rate / 2; i < total; ++i) { double fit = a * std::sin(w * i) + b * std::cos(w * i); error += (output[i] - fit) * (output[i] - fit); power += fit * fit; }
                const double amplitude = std::sqrt(a * a + b * b), db = 10 * std::log10(error / power + 1e-20);
                // фаза тона говорит о задержке; сравниваем с заявленной на низком тоне, где она однозначна
                const double delay = std::fmod(std::atan2(-b, a) / w + 10 * rate / tone, rate / tone);
                std::cout << rate << " Hz, tone " << tone << ": amplitude " << amplitude << ", distortion " << db << " dB, delay " << delay << " (declared " << stage.delay() << ")\n";
                check(std::abs(amplitude - 0.5) < 0.03, "Level changed");
                check(db < -60, "Resampled audio is distorted");
                if (tone == 100.0) check(std::abs(delay - static_cast<double>(stage.delay())) < 1.5, "Declared delay is wrong");
            }
        }
        std::cout << "PASS: rate conversion round trip for 8..48 kHz, uneven blocks, level, distortion, declared delay\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
