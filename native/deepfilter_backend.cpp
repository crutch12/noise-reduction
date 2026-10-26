#include "deepfilter_backend.h"
#include <stdexcept>

namespace nr {
DeepFilterBackend::DeepFilterBackend(const wchar_t* path) {
    module_ = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module_) throw std::runtime_error("Cannot load DeepFilterNet from the explicit library path");
#pragma warning(push)
#pragma warning(disable: 4191)
    api_.create = reinterpret_cast<decltype(api_.create)>(GetProcAddress(module_, "nr_df_create"));
    api_.destroy = reinterpret_cast<decltype(api_.destroy)>(GetProcAddress(module_, "nr_df_destroy"));
    api_.frame = reinterpret_cast<decltype(api_.frame)>(GetProcAddress(module_, "nr_df_process_frame"));
    auto frame_size = reinterpret_cast<int (*)()>(GetProcAddress(module_, "nr_df_get_frame_size"));
    auto latency = reinterpret_cast<int (*)(const void*)>(GetProcAddress(module_, "nr_df_get_latency"));
#pragma warning(pop)
    if (!api_.create || !api_.destroy || !api_.frame || !frame_size || !latency || frame_size() != Processor::frame_size) {
        FreeLibrary(module_);
        module_ = nullptr;
        throw std::runtime_error("Unsupported DeepFilterNet ABI or frame size");
    }
    // задержку модели узнаём на пробном состоянии, к ней добавляется кадр адаптера
    void* probe = api_.create(nullptr);
    const int model = probe ? latency(probe) : -1;
    if (probe) api_.destroy(probe);
    if (model <= 0 || static_cast<size_t>(model) + Processor::frame_size > Processor::max_latency) {
        FreeLibrary(module_);
        module_ = nullptr;
        throw std::runtime_error("DeepFilterNet model is unavailable");
    }
    api_.latency = static_cast<size_t>(model) + Processor::frame_size;
}
DeepFilterBackend::~DeepFilterBackend() { if (module_) FreeLibrary(module_); }
}
