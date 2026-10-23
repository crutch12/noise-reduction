#pragma once
#include "processor.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

namespace nr {
class RnnoiseBackend {
public:
    explicit RnnoiseBackend(const wchar_t* path);
    ~RnnoiseBackend();
    RnnoiseBackend(const RnnoiseBackend&) = delete;
    RnnoiseBackend& operator=(const RnnoiseBackend&) = delete;
    Backend api() const noexcept { return api_; }
private:
    HMODULE module_ = nullptr;
    Backend api_{};
};
}
