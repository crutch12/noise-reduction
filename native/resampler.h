#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

namespace nr {
// Пересчёт частоты в отношении L/M многофазным фильтром с оконным sinc; таблица считается заранее,
// в звуковом потоке только умножения без выделения памяти
class Resampler {
public:
    // отводов на фазу при повышении частоты; при понижении их больше во столько раз, во сколько понижаем
    static constexpr size_t base_taps = 32;
    Resampler(uint32_t from, uint32_t to);
    // n входных отсчётов, возвращает сколько записано в out; out должен вмещать max_output(n)
    size_t process(const float* in, size_t n, float* out) noexcept;
    size_t max_output(size_t n) const noexcept { return (n * up_ + down_ - 1) / down_ + 1; }
    // задержка во входных отсчётах
    size_t delay() const noexcept { return taps_ / 2; }
    void reset() noexcept;
private:
    uint32_t up_, down_, phase_ = 0;
    size_t taps_;
    std::vector<float> table_, history_;
    size_t head_ = 0;
};

// Модель работает на 48 кГц: вход приводится к 48 кГц, обрабатывается и возвращается в частоту устройства.
// Выход отстаёт на фиксированный запас, чтобы на каждый вызов отдавать ровно столько, сколько пришло
class RateStage {
public:
    RateStage(uint32_t rate, size_t max_frames);
    template <class Process> void process(const float* in, float* out, size_t n, Process&& model) noexcept {
        for (size_t i = 0; i < n; ++i) input_[i] = in ? in[i] : 0.0f;
        size_t wide = up_.process(input_.data(), n, wide_in_.data());
        model(wide_in_.data(), wide_out_.data(), wide);
        size_t narrow = down_.process(wide_out_.data(), wide, narrow_.data());
        for (size_t i = 0; i < narrow; ++i) push(narrow_[i]);
        for (size_t i = 0; i < n; ++i) out[i] = pop();
    }
    // задержка в отсчётах устройства без учёта модели
    size_t delay() const noexcept { return delay_; }
    uint32_t rate() const noexcept { return rate_; }
private:
    void push(float value) noexcept { fifo_[tail_] = value; tail_ = (tail_ + 1) % fifo_.size(); ++count_; }
    float pop() noexcept { if (!count_) return 0.0f; float value = fifo_[head_]; head_ = (head_ + 1) % fifo_.size(); --count_; return value; }
    uint32_t rate_;
    Resampler up_, down_;
    std::vector<float> input_, wide_in_, wide_out_, narrow_, fifo_;
    size_t head_ = 0, tail_ = 0, count_ = 0, delay_ = 0;
};
}
