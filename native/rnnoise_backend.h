#pragma once
#include "processor.h"
#include "library.h"

namespace nr {
class RnnoiseBackend {
public:
    explicit RnnoiseBackend(const PathChar* path);
    RnnoiseBackend(const RnnoiseBackend&) = delete;
    RnnoiseBackend& operator=(const RnnoiseBackend&) = delete;
    Backend api() const noexcept { return api_; }
private:
    Library module_;
    Backend api_{};
};
}
