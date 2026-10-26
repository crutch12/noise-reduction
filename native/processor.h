#pragma once
#include <array>
#include <cmath>
#include <atomic>
#include <cstddef>
#include <cstdint>

namespace nr {
struct Backend {
    void* (*create)(void*);
    void (*destroy)(void*);
    float (*frame)(void*, float*, const float*);
    // задержка модели вместе с адаптером кадров, 0 значит как у RNNoise
    size_t latency = 0;
    // своя сила подавления у модели; без неё сила задаётся подмешиванием исходного звука
    void (*strength)(void*, float) = nullptr;
};

// Срез гула ниже 80 Гц, Butterworth второго порядка на частоте устройства
class HighPass {
public:
    explicit HighPass(float rate = 48000.0f) noexcept {
        const double w = 2 * 3.14159265358979323846 * 80.0 / rate, c = std::cos(w), alpha = std::sin(w) / (2 * 0.70710678118654752);
        const double a0 = 1 + alpha;
        b0 = static_cast<float>((1 + c) / 2 / a0); b1 = static_cast<float>(-(1 + c) / a0);
        a1 = static_cast<float>(-2 * c / a0); a2 = static_cast<float>((1 - alpha) / a0);
    }
    float process(float x) noexcept {
        const float y = b0 * x + z1_;
        z1_ = b1 * x - a1 * y + z2_;
        z2_ = b0 * x - a2 * y;
        return y;
    }
    void reset() noexcept { z1_ = z2_ = 0; }
private:
    float b0, b1, a1, a2;
    float z1_ = 0, z2_ = 0;
};

// Экономный режим: среднее только по каналам с сигналом, молчащие каналы не снижают уровень голоса
inline void mix_active(const float* interleaved, size_t frames, size_t channels, float* energy, float* mono) noexcept {
    float loudest = 0;
    for (size_t c = 0; c < channels; ++c) {
        double sum = 0;
        for (size_t i = 0; i < frames; ++i) { const float x = interleaved[i * channels + c]; sum += x == x ? double(x) * x : 0; }
        energy[c] = 0.98f * energy[c] + 0.02f * float(frames ? sum / frames : 0);
        if (energy[c] > loudest) loudest = energy[c];
    }
    size_t active = 0;
    for (size_t c = 0; c < channels; ++c) if (energy[c] >= loudest * 0.01f) ++active;
    for (size_t i = 0; i < frames; ++i) {
        float sum = 0;
        for (size_t c = 0; c < channels; ++c) if (energy[c] >= loudest * 0.01f) sum += interleaved[i * channels + c];
        mono[i] = active ? sum / float(active) : 0;
    }
}

class Processor {
public:
    static constexpr size_t frame_size = 480;
    // Verified for the pinned RNNoise build: 960 model + 480 adapter samples
    static constexpr size_t latency = 1440;
    static constexpr size_t max_latency = 2880;
    explicit Processor(Backend backend, bool enabled = true);
    ~Processor();
    Processor(const Processor&) = delete;
    Processor& operator=(const Processor&) = delete;
    void set_enabled(bool value) noexcept { enabled_.store(value, std::memory_order_relaxed); }
    void set_attenuation(float db) noexcept;
    void process(const float* input, float* output, size_t count) noexcept;
    uint64_t processed_frames() const noexcept { return processed_frames_; }
    bool has_model() const noexcept { return state_ != nullptr; }
    size_t delay() const noexcept { return delay_; }

private:
    Backend backend_;
    void* state_;
    std::atomic<bool> enabled_;
    std::atomic<float> floor_{0.0630957344f};
    std::atomic<float> strength_{24.0f};
    float applied_strength_ = -1.0f;
    float current_floor_ = 0.0630957344f;
    std::array<float, frame_size> input_{};
    std::array<float, frame_size> output_{};
    std::array<float, max_latency> dry_{};
    size_t delay_;
    size_t position_ = 0;
    size_t dry_position_ = 0;
    float blend_;
    bool previous_enabled_;
    unsigned warmup_ = 0;
    uint64_t processed_frames_ = 0;
};
}
