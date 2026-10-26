#pragma once
#include "processor.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

namespace nr {
// DeepFilterNet через обёртку nr_deepfilter.dll, интерфейс тот же, что у RNNoise
class DeepFilterBackend {
public:
    explicit DeepFilterBackend(const wchar_t* path);
    ~DeepFilterBackend();
    DeepFilterBackend(const DeepFilterBackend&) = delete;
    DeepFilterBackend& operator=(const DeepFilterBackend&) = delete;
    Backend api() const noexcept { return api_; }
private:
    HMODULE module_ = nullptr;
    Backend api_{};
};
}
