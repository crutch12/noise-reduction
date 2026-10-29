#include "bridge.h"
#include "processor.h"
#include "rnnoise_backend.h"
#include "deepfilter_backend.h"
#include "miniaudio.h"
#include <algorithm>
#include <atomic>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace {
constexpr uint32_t rate = 48000;
constexpr uint32_t enabled_bit = 1, low_cut_bit = 2, deepfilter_bit = 8;
// задержка между захватом и выводом: держим около 30 мс, лишнее от разницы часов устройств выбрасываем
constexpr uint32_t ring_frames = rate / 2, target_fill = 1440, max_fill = 4800;

#ifdef __APPLE__
constexpr const char* library_suffix = ".dylib";
#else
constexpr const char* library_suffix = ".so";
#endif

struct Bridge {
    ma_context context{};
    ma_device capture{}, playback{};
    ma_pcm_rb ring{};
    bool context_ready = false, ring_ready = false, capture_ready = false, playback_ready = false;
    std::string models;
    std::unique_ptr<nr::RnnoiseBackend> rnnoise;
    std::unique_ptr<nr::DeepFilterBackend> deepfilter;
    bool deepfilter_failed = false;
    // новый набор собирается в configure, звуковой поток только подхватывает его
    std::unique_ptr<nr::Processor> active;
    std::atomic<nr::Processor*> pending{nullptr}, retired{nullptr};
    std::mutex configure_mutex;
    bool built_deepfilter = false;
    std::atomic<uint32_t> config{enabled_bit | (24u << 8)};
    uint32_t applied = UINT32_MAX;
    nr::HighPass highpass{static_cast<float>(rate)};
    std::vector<float> scratch = std::vector<float>(4096), processed = std::vector<float>(4096);
    std::atomic<uint64_t> frames{0}, model_frames{0}, errors{0};

    ~Bridge() {
        if (capture_ready) ma_device_uninit(&capture);
        if (playback_ready) ma_device_uninit(&playback);
        if (ring_ready) ma_pcm_rb_uninit(&ring);
        if (context_ready) ma_context_uninit(&context);
        delete pending.exchange(nullptr);
        delete retired.exchange(nullptr);
    }

    std::unique_ptr<nr::Processor> build(bool want_deepfilter) {
        nr::Backend model{};
        if (want_deepfilter && !deepfilter && !deepfilter_failed) {
            try { deepfilter = std::make_unique<nr::DeepFilterBackend>((models + "/libnr_deepfilter" + library_suffix).c_str()); }
            catch (...) { deepfilter_failed = true; ++errors; }
        }
        if (want_deepfilter && deepfilter) model = deepfilter->api();
        else if (rnnoise) model = rnnoise->api();
        built_deepfilter = want_deepfilter && deepfilter;
        auto processor = std::make_unique<nr::Processor>(model, true);
        // без модели процессор просто пропускает звук, микрофон при этом работает
        if (!processor->has_model()) ++errors;
        return processor;
    }

    void on_capture(const float* input, uint32_t count) noexcept {
        if (nr::Processor* next = pending.exchange(nullptr)) {
            delete retired.exchange(active.release());
            active.reset(next);
            applied = UINT32_MAX;
        }
        const uint32_t settings = config.load(std::memory_order_relaxed);
        const bool enabled = settings & enabled_bit;
        const bool low_cut = enabled && (settings & low_cut_bit);
        if (low_cut != bool(applied & low_cut_bit) || applied == UINT32_MAX) highpass.reset();
        if (active && settings != applied) {
            active->set_enabled(enabled);
            active->set_attenuation(static_cast<float>(std::min(60u, (settings >> 8) & 255u)));
        }
        applied = settings;
        for (uint32_t done = 0; done < count;) {
            const uint32_t chunk = std::min<uint32_t>(count - done, static_cast<uint32_t>(scratch.size()));
            for (uint32_t i = 0; i < chunk; ++i) {
                const float sample = input ? input[done + i] : 0.0f;
                scratch[i] = low_cut && std::isfinite(sample) ? highpass.process(sample) : sample;
            }
            const uint64_t before = active ? active->processed_frames() : 0;
            if (active) active->process(scratch.data(), processed.data(), chunk);
            else std::memcpy(processed.data(), scratch.data(), chunk * sizeof(float));
            if (active) model_frames += active->processed_frames() - before;
            write(processed.data(), chunk);
            done += chunk;
        }
        frames += count;
    }

    void write(const float* data, uint32_t count) noexcept {
        while (count) {
            ma_uint32 size = count;
            void* target = nullptr;
            if (ma_pcm_rb_acquire_write(&ring, &size, &target) != MA_SUCCESS || !size) return;
            std::memcpy(target, data, size * sizeof(float));
            ma_pcm_rb_commit_write(&ring, size);
            data += size; count -= size;
        }
    }

    void on_playback(float* output, uint32_t count) noexcept {
        const ma_uint32 available = ma_pcm_rb_available_read(&ring);
        if (available > max_fill) ma_pcm_rb_seek_read(&ring, available - target_fill);
        uint32_t done = 0;
        while (done < count) {
            ma_uint32 size = count - done;
            void* source = nullptr;
            if (ma_pcm_rb_acquire_read(&ring, &size, &source) != MA_SUCCESS || !size) break;
            std::memcpy(output + done, source, size * sizeof(float));
            ma_pcm_rb_commit_read(&ring, size);
            done += size;
        }
        if (done < count) std::memset(output + done, 0, (count - done) * sizeof(float));
    }
};

void capture_callback(ma_device* device, void*, const void* input, ma_uint32 count) {
    static_cast<Bridge*>(device->pUserData)->on_capture(static_cast<const float*>(input), count);
}
void playback_callback(ma_device* device, void* output, const void*, ma_uint32 count) {
    static_cast<Bridge*>(device->pUserData)->on_playback(static_cast<float*>(output), count);
}

bool find(ma_context& context, bool playback, const char* name, ma_device_id& id) {
    ma_device_info *playbacks = nullptr, *captures = nullptr;
    ma_uint32 playback_count = 0, capture_count = 0;
    if (ma_context_get_devices(&context, &playbacks, &playback_count, &captures, &capture_count) != MA_SUCCESS) return false;
    ma_device_info* list = playback ? playbacks : captures;
    const ma_uint32 count = playback ? playback_count : capture_count;
    for (ma_uint32 i = 0; i < count; ++i) if (std::strcmp(list[i].name, name) == 0) { id = list[i].id; return true; }
    return false;
}

bool init_context(ma_context& context, int backend) {
    const ma_backend null_backend[] = { ma_backend_null };
    return ma_context_init(backend == 1 ? null_backend : nullptr, backend == 1 ? 1 : 0, nullptr, &context) == MA_SUCCESS;
}
}

NR_BRIDGE_API int nr_bridge_list(int playback, char* buffer, int size) {
    ma_context context;
    if (!init_context(context, 0)) return 0;
    ma_device_info *playbacks = nullptr, *captures = nullptr;
    ma_uint32 playback_count = 0, capture_count = 0;
    std::string text;
    if (ma_context_get_devices(&context, &playbacks, &playback_count, &captures, &capture_count) == MA_SUCCESS) {
        ma_device_info* list = playback ? playbacks : captures;
        const ma_uint32 count = playback ? playback_count : capture_count;
        for (ma_uint32 i = 0; i < count; ++i) text += std::string(list[i].isDefault ? "*" : "") + list[i].name + "\n";
    }
    ma_context_uninit(&context);
    const int needed = static_cast<int>(text.size()) + 1;
    if (buffer && size >= needed) std::memcpy(buffer, text.c_str(), needed);
    return needed;
}

NR_BRIDGE_API void* nr_bridge_start(const char* input, const char* output, const char* models, int backend) {
    auto bridge = std::make_unique<Bridge>();
    try {
        bridge->models = models ? models : "";
        try { bridge->rnnoise = std::make_unique<nr::RnnoiseBackend>((bridge->models + "/librnnoise" + library_suffix).c_str()); }
        catch (...) { ++bridge->errors; }
        bridge->active = bridge->build(false);
        if (!init_context(bridge->context, backend)) return nullptr;
        bridge->context_ready = true;
        if (ma_pcm_rb_init(ma_format_f32, 1, ring_frames, nullptr, nullptr, &bridge->ring) != MA_SUCCESS) return nullptr;
        bridge->ring_ready = true;

        ma_device_id input_id{}, output_id{};
        ma_device_config capture = ma_device_config_init(ma_device_type_capture);
        capture.capture.format = ma_format_f32;
        capture.capture.channels = 1;
        capture.sampleRate = rate;
        capture.periodSizeInMilliseconds = 10;
        capture.dataCallback = capture_callback;
        capture.pUserData = bridge.get();
        if (input && *input) { if (!find(bridge->context, false, input, input_id)) return nullptr; capture.capture.pDeviceID = &input_id; }
        ma_device_config playback = ma_device_config_init(ma_device_type_playback);
        playback.playback.format = ma_format_f32;
        playback.playback.channels = 1;
        playback.sampleRate = rate;
        playback.periodSizeInMilliseconds = 10;
        playback.dataCallback = playback_callback;
        playback.pUserData = bridge.get();
        if (output && *output) { if (!find(bridge->context, true, output, output_id)) return nullptr; playback.playback.pDeviceID = &output_id; }

        if (ma_device_init(&bridge->context, &capture, &bridge->capture) != MA_SUCCESS) return nullptr;
        bridge->capture_ready = true;
        if (ma_device_init(&bridge->context, &playback, &bridge->playback) != MA_SUCCESS) return nullptr;
        bridge->playback_ready = true;
        if (ma_device_start(&bridge->playback) != MA_SUCCESS || ma_device_start(&bridge->capture) != MA_SUCCESS) return nullptr;
        return bridge.release();
    }
    catch (...) { return nullptr; }
}

NR_BRIDGE_API void nr_bridge_configure(void* handle, uint32_t config) {
    auto bridge = static_cast<Bridge*>(handle);
    if (!bridge) return;
    std::lock_guard<std::mutex> lock(bridge->configure_mutex);
    delete bridge->retired.exchange(nullptr);
    const bool want = config & deepfilter_bit;
    if (want != bridge->built_deepfilter) {
        try { delete bridge->pending.exchange(bridge->build(want).release()); }
        catch (...) { ++bridge->errors; }
    }
    bridge->config.store(config);
}

NR_BRIDGE_API void nr_bridge_stats(void* handle, uint64_t* frames, uint64_t* model_frames, uint64_t* errors) {
    auto bridge = static_cast<Bridge*>(handle);
    if (frames) *frames = bridge ? bridge->frames.load() : 0;
    if (model_frames) *model_frames = bridge ? bridge->model_frames.load() : 0;
    if (errors) *errors = bridge ? bridge->errors.load() : 0;
}

NR_BRIDGE_API void nr_bridge_stop(void* handle) { delete static_cast<Bridge*>(handle); }
