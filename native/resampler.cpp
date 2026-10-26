#include "resampler.h"
#include <algorithm>
#include <cmath>
#include <numeric>

namespace nr {
namespace {
double bessel0(double x) {
    double sum = 1, term = 1;
    for (int k = 1; k < 32; ++k) { term *= (x / (2 * k)) * (x / (2 * k)); sum += term; }
    return sum;
}
}

Resampler::Resampler(uint32_t from, uint32_t to) {
    uint32_t common = std::gcd(from, to);
    up_ = to / common; down_ = from / common;
    taps_ = base_taps * ((down_ + up_ - 1) / up_);
    history_.assign(taps_, 0.0f);
    // прототип на частоте from*up: срез чуть ниже меньшей из половин частот, окно Кайзера
    const size_t length = taps_ * up_;
    const size_t taps = taps_;
    const double cutoff = 0.5 / std::max(up_, down_) * 0.92;
    // центр ровно на отсчёте, иначе задержка дробная и звук сдвигается на полотсчёта
    const double beta = 8.0, center = static_cast<double>(length / 2);
    table_.assign(length, 0.0f);
    for (uint32_t phase = 0; phase < up_; ++phase)
        for (size_t k = 0; k < taps; ++k) {
            const size_t index = phase + k * up_;
            const double t = index - center;
            const double sinc = t == 0 ? 2 * cutoff : std::sin(2 * 3.14159265358979323846 * cutoff * t) / (3.14159265358979323846 * t);
            const double ratio = (index - center) / center;
            const double window = bessel0(beta * std::sqrt(std::max(0.0, 1 - ratio * ratio))) / bessel0(beta);
            table_[phase * taps_ + k] = static_cast<float>(sinc * window * up_);
        }
}

void Resampler::reset() noexcept {
    std::fill(history_.begin(), history_.end(), 0.0f);
    head_ = 0; phase_ = 0;
}

size_t Resampler::process(const float* in, size_t n, float* out) noexcept {
    size_t produced = 0;
    for (size_t i = 0; i < n; ++i) {
        head_ = (head_ + taps_ - 1) % taps_;
        history_[head_] = in[i];
        // выходы, которые приходятся на этот входной отсчёт
        for (; phase_ < up_; phase_ += down_) {
            const float* coefficients = &table_[phase_ * taps_];
            float sum = 0;
            for (size_t k = 0; k < taps_; ++k) sum += coefficients[k] * history_[(head_ + k) % taps_];
            out[produced++] = sum;
        }
        phase_ -= up_;
    }
    return produced;
}

RateStage::RateStage(uint32_t rate, size_t max_frames)
    : rate_(rate), up_(rate, 48000), down_(48000, rate) {
    input_.assign(max_frames, 0.0f);
    wide_in_.assign(up_.max_output(max_frames), 0.0f);
    wide_out_.assign(wide_in_.size(), 0.0f);
    narrow_.assign(down_.max_output(wide_in_.size()), 0.0f);
    fifo_.assign(narrow_.size() + max_frames + 64, 0.0f);
    // запас на неравномерность: отношение частот не целое, и за вызов выходит то на отсчёт больше, то меньше
    const size_t reserve = 8;
    for (size_t i = 0; i < reserve; ++i) push(0.0f);
    delay_ = reserve + up_.delay() + (down_.delay() * rate + 24000) / 48000;
}
}
