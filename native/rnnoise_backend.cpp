#include "rnnoise_backend.h"
#include <stdexcept>

namespace nr {
RnnoiseBackend::RnnoiseBackend(const wchar_t* path) {
    module_ = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module_) throw std::runtime_error("Cannot load RNNoise from the explicit library path");
#pragma warning(push)
#pragma warning(disable: 4191)
    api_.create = reinterpret_cast<decltype(api_.create)>(GetProcAddress(module_, "rnnoise_create"));
    api_.destroy = reinterpret_cast<decltype(api_.destroy)>(GetProcAddress(module_, "rnnoise_destroy"));
    api_.frame = reinterpret_cast<decltype(api_.frame)>(GetProcAddress(module_, "rnnoise_process_frame"));
    auto frame_size = reinterpret_cast<int (*)()>(GetProcAddress(module_, "rnnoise_get_frame_size"));
#pragma warning(pop)
    if (!api_.create || !api_.destroy || !api_.frame || !frame_size || frame_size() != Processor::frame_size) {
        FreeLibrary(module_);
        module_ = nullptr;
        throw std::runtime_error("Unsupported RNNoise ABI or frame size");
    }
}
RnnoiseBackend::~RnnoiseBackend() { if (module_) FreeLibrary(module_); }
}
