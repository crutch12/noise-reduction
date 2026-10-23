#include "processor.h"
#include <algorithm>
#include <cmath>
#include <new>

namespace nr {
Processor::Processor(Backend backend, bool enabled)
    : backend_(backend), state_(backend.create(nullptr)), enabled_(enabled),
      blend_(enabled ? 1.0f : 0.0f), previous_enabled_(enabled) {
    if (!state_) throw std::bad_alloc();
}

Processor::~Processor() { backend_.destroy(state_); }

void Processor::process(const float* input, float* output, size_t count) noexcept {
    const bool enabled = enabled_.load(std::memory_order_relaxed);
    if (enabled && !previous_enabled_) warmup_ = 4;
    previous_enabled_ = enabled;
    for (size_t i = 0; i < count; ++i) {
        float sample = input ? input[i] : 0.0f;
        sample = std::isfinite(sample) ? std::clamp(sample, -1.0f, 1.0f) : 0.0f;
        float dry = dry_[dry_position_];
        dry_[dry_position_] = sample;
        dry_position_ = (dry_position_ + 1) % latency;
        float target = enabled && warmup_ == 0 ? 1.0f : 0.0f;
        blend_ += std::clamp(target - blend_, -1.0f / 960, 1.0f / 960);
        float wet = output_[position_] / 32768.0f;
        // A fixed 24 dB floor for the first native prototype, without VAD gating
        constexpr float floor = 0.0630957344f;
        float result = dry + blend_ * (1 - floor) * (wet - dry);
        output[i] = std::isfinite(result) ? std::clamp(result, -1.0f, 1.0f) : dry;
        input_[position_] = sample * 32768.0f;
        if (++position_ == frame_size) {
            position_ = 0;
            if (enabled || blend_ > 0) {
                backend_.frame(state_, output_.data(), input_.data());
                ++processed_frames_;
                if (warmup_) --warmup_;
            } else {
                output_.fill(0);
            }
        }
    }
}
}
