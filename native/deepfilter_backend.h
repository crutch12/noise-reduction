#pragma once
#include "processor.h"
#include "library.h"

namespace nr {
// DeepFilterNet через обёртку nr_deepfilter, интерфейс тот же, что у RNNoise
class DeepFilterBackend {
public:
    explicit DeepFilterBackend(const PathChar* path);
    DeepFilterBackend(const DeepFilterBackend&) = delete;
    DeepFilterBackend& operator=(const DeepFilterBackend&) = delete;
    Backend api() const noexcept { return api_; }
private:
    Library module_;
    Backend api_{};
};
}
