#pragma once
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>

namespace nr {
struct Backend {
    void* (*create)(void*);
    void (*destroy)(void*);
    float (*frame)(void*, float*, const float*);
};

// Срез гула ниже 80 Гц, Butterworth второго порядка для 48 кГц
class HighPass {
public:
    float process(float x) noexcept {
        const float y = b0 * x + z1_;
        z1_ = b1 * x - a1 * y + z2_;
        z2_ = b0 * x - a2 * y;
        return y;
    }
    void reset() noexcept { z1_ = z2_ = 0; }
private:
    static constexpr float b0 = 0.99262254f, b1 = -1.98524509f, a1 = -1.98519066f, a2 = 0.98529951f;
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
    explicit Processor(Backend backend, bool enabled = true);
    ~Processor();
    Processor(const Processor&) = delete;
    Processor& operator=(const Processor&) = delete;
    void set_enabled(bool value) noexcept { enabled_.store(value, std::memory_order_relaxed); }
    void set_attenuation(float db) noexcept;
    void process(const float* input, float* output, size_t count) noexcept;
    uint64_t processed_frames() const noexcept { return processed_frames_; }
    bool has_model() const noexcept { return state_ != nullptr; }

private:
    Backend backend_;
    void* state_;
    std::atomic<bool> enabled_;
    std::atomic<float> floor_{0.0630957344f};
    float current_floor_ = 0.0630957344f;
    std::array<float, frame_size> input_{};
    std::array<float, frame_size> output_{};
    std::array<float, latency> dry_{};
    size_t position_ = 0;
    size_t dry_position_ = 0;
    float blend_;
    bool previous_enabled_;
    unsigned warmup_ = 0;
    uint64_t processed_frames_ = 0;
};
}
