#include "rnnoise_backend.h"
#include <stdexcept>

namespace nr {
RnnoiseBackend::RnnoiseBackend(const PathChar* path) : module_(path) {
    if (!module_.loaded()) throw std::runtime_error("Cannot load RNNoise from the explicit library path");
    api_.create = module_.symbol<decltype(api_.create)>("rnnoise_create");
    api_.destroy = module_.symbol<decltype(api_.destroy)>("rnnoise_destroy");
    api_.frame = module_.symbol<decltype(api_.frame)>("rnnoise_process_frame");
    auto frame_size = module_.symbol<int (*)()>("rnnoise_get_frame_size");
    if (!api_.create || !api_.destroy || !api_.frame || !frame_size || frame_size() != Processor::frame_size) {
        module_.close();
        throw std::runtime_error("Unsupported RNNoise ABI or frame size");
    }
}
}
