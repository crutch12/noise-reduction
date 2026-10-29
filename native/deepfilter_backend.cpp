#include "deepfilter_backend.h"
#include <stdexcept>

namespace nr {
DeepFilterBackend::DeepFilterBackend(const PathChar* path) : module_(path) {
    if (!module_.loaded()) throw std::runtime_error("Cannot load DeepFilterNet from the explicit library path");
    api_.create = module_.symbol<decltype(api_.create)>("nr_df_create");
    api_.destroy = module_.symbol<decltype(api_.destroy)>("nr_df_destroy");
    api_.frame = module_.symbol<decltype(api_.frame)>("nr_df_process_frame");
    api_.strength = module_.symbol<decltype(api_.strength)>("nr_df_set_strength");
    auto frame_size = module_.symbol<int (*)()>("nr_df_get_frame_size");
    auto latency = module_.symbol<int (*)(const void*)>("nr_df_get_latency");
    if (!api_.create || !api_.destroy || !api_.frame || !api_.strength || !frame_size || !latency || frame_size() != Processor::frame_size) {
        module_.close();
        throw std::runtime_error("Unsupported DeepFilterNet ABI or frame size");
    }
    // задержку модели узнаём на пробном состоянии, к ней добавляется кадр адаптера
    void* probe = api_.create(nullptr);
    const int model = probe ? latency(probe) : -1;
    if (probe) api_.destroy(probe);
    if (model <= 0 || static_cast<size_t>(model) + Processor::frame_size > Processor::max_latency) {
        module_.close();
        throw std::runtime_error("DeepFilterNet model is unavailable");
    }
    api_.latency = static_cast<size_t>(model) + Processor::frame_size;
}
}
