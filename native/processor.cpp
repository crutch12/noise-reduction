#include "processor.h"
#include <algorithm>
#include <cmath>

namespace nr {
Processor::Processor(Backend backend, bool enabled)
    : backend_(backend), state_(backend.create ? backend.create(nullptr) : nullptr), enabled_(enabled),
      delay_(backend.latency ? std::min(backend.latency, max_latency) : latency),
      blend_(enabled ? 1.0f : 0.0f), previous_enabled_(enabled) {
}

Processor::~Processor() { if (state_) backend_.destroy(state_); }

void Processor::set_attenuation(float db) noexcept {
    if (!std::isfinite(db)) return;
    floor_.store(std::pow(10.0f, -std::clamp(db, 0.0f, 60.0f) / 20.0f), std::memory_order_relaxed);
}

void Processor::process(const float* input, float* output, size_t count) noexcept {
    const bool enabled = state_ && enabled_.load(std::memory_order_relaxed);
    const float floor = floor_.load(std::memory_order_relaxed);
    if (enabled && !previous_enabled_) warmup_ = 4;
    previous_enabled_ = enabled;
    for (size_t i = 0; i < count; ++i) {
        float sample = input ? input[i] : 0.0f;
        sample = std::isfinite(sample) ? std::clamp(sample, -1.0f, 1.0f) : 0.0f;
        float dry = dry_[dry_position_];
        dry_[dry_position_] = sample;
        dry_position_ = (dry_position_ + 1) % delay_;
        float target = enabled && warmup_ == 0 ? 1.0f : 0.0f;
        blend_ += std::clamp(target - blend_, -1.0f / 960, 1.0f / 960);
        float wet = output_[position_] / 32768.0f;
        current_floor_ += std::clamp(floor - current_floor_, -1.0f / 960, 1.0f / 960);
        float result = dry + blend_ * (1 - current_floor_) * (wet - dry);
        output[i] = std::isfinite(result) ? std::clamp(result, -1.0f, 1.0f) : dry;
        input_[position_] = sample * 32768.0f;
        if (++position_ == frame_size) {
            position_ = 0;
            if (state_ && (enabled || blend_ > 0)) {
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
