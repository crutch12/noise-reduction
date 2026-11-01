#include "bridge.h"
#include "processor.h"
#include "rnnoise_backend.h"
#include "deepfilter_backend.h"
#include "miniaudio.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <vector>
#ifdef __APPLE__
#include <AudioToolbox/AudioToolbox.h>
#include <CoreAudio/CoreAudio.h>
#endif

namespace {
constexpr uint32_t rate = 48000;
constexpr uint32_t enabled_bit = 1, low_cut_bit = 2, deepfilter_bit = 8;
// новый путь накапливает 60 мс звука, старый порог после переполнения остаётся 30 мс
constexpr uint32_t ring_frames = rate / 2, target_fill = 1440, configured_fill = 2880, max_fill = 4800;
constexpr uint32_t configured_max_fill = 12000;
uint64_t now_ms() noexcept {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}

struct Pipeline {
    std::unique_ptr<nr::Processor> processor;
    int model = 0;
    Pipeline* next = nullptr;
};

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
    bool deepfilter_failed = false, rnnoise_failed = false, configured_start = false, playback_primed = false;
    // новый набор собирается в configure, звуковой поток только подхватывает его
    std::unique_ptr<Pipeline> active;
    std::atomic<Pipeline*> pending{nullptr}, retired{nullptr};
    std::mutex configure_mutex;
    bool built_deepfilter = false, build_attempted = false, requested_deepfilter = false;
    std::atomic<uint32_t> config{enabled_bit | (24u << 8)};
    uint32_t applied = UINT32_MAX;
    nr::HighPass highpass{static_cast<float>(rate)};
    std::vector<float> scratch = std::vector<float>(4096), processed = std::vector<float>(4096);
    std::atomic<uint64_t> frames{0}, model_frames{0}, errors{0};
    std::atomic<uint64_t> playback_frames{0}, underruns{0}, dropped_frames{0}, capture_tick{0}, playback_tick{0};
    std::atomic<float> input_peak{0}, output_peak{0};
    std::atomic<int> actual_model{0};
    std::atomic<bool> capture_alive{false}, playback_alive{false};
#ifdef __APPLE__
    // скрытый выход драйвера не виден в общем списке устройств, пишем в него напрямую по UID
    AudioComponentInstance unit = nullptr;
    bool unit_started = false;
#endif

    ~Bridge() {
        // uninit не будит Pulse main loop после отключения источника
        if (configured_start && context_ready && context.backend == ma_backend_pulseaudio) {
            if (capture_ready) ma_device_stop(&capture);
            if (playback_ready) ma_device_stop(&playback);
        }
        if (capture_ready) ma_device_uninit(&capture);
        if (playback_ready) ma_device_uninit(&playback);
#ifdef __APPLE__
        if (unit) {
            if (unit_started) AudioOutputUnitStop(unit);
            AudioUnitUninitialize(unit);
            AudioComponentInstanceDispose(unit);
        }
#endif
        if (ring_ready) ma_pcm_rb_uninit(&ring);
        if (context_ready) ma_context_uninit(&context);
        delete pending.exchange(nullptr);
        collect_retired();
    }

    void collect_retired() noexcept {
        Pipeline* node = retired.exchange(nullptr);
        while (node) { Pipeline* next = node->next; delete node; node = next; }
    }

    std::unique_ptr<Pipeline> build(bool want_deepfilter, bool enabled = true) {
        nr::Backend model{};
        if (want_deepfilter && !deepfilter && !deepfilter_failed) {
            try { deepfilter = std::make_unique<nr::DeepFilterBackend>((models + "/libnr_deepfilter" + library_suffix).c_str()); }
            catch (...) { deepfilter_failed = true; ++errors; }
        }
        if (want_deepfilter && deepfilter) model = deepfilter->api();
        else if (rnnoise) model = rnnoise->api();
        built_deepfilter = want_deepfilter && deepfilter;
        auto pipeline = std::make_unique<Pipeline>();
        pipeline->processor = std::make_unique<nr::Processor>(model, enabled);
        // без модели процессор просто пропускает звук, микрофон при этом работает
        if (!pipeline->processor->has_model()) ++errors;
        else pipeline->model = built_deepfilter ? 2 : 1;
        build_attempted = true;
        requested_deepfilter = want_deepfilter;
        return pipeline;
    }

    void on_capture(const float* input, uint32_t count) noexcept {
        if (Pipeline* next = pending.exchange(nullptr)) {
            Pipeline* previous = active.release();
            if (previous) {
                previous->next = retired.load();
                while (!retired.compare_exchange_weak(previous->next, previous)) { }
            }
            active.reset(next);
            applied = UINT32_MAX;
        }
        const uint32_t settings = config.load(std::memory_order_relaxed);
        const bool enabled = settings & enabled_bit;
        const bool low_cut = enabled && (settings & low_cut_bit);
        if (low_cut != bool(applied & low_cut_bit) || applied == UINT32_MAX) highpass.reset();
        if (active && settings != applied) {
            active->processor->set_enabled(enabled);
            active->processor->set_attenuation(static_cast<float>(std::min(60u, (settings >> 8) & 255u)));
        }
        actual_model.store(enabled && active ? active->model : 0, std::memory_order_relaxed);
        applied = settings;
        float peak = 0;
        for (uint32_t done = 0; done < count;) {
            const uint32_t chunk = std::min<uint32_t>(count - done, static_cast<uint32_t>(scratch.size()));
            for (uint32_t i = 0; i < chunk; ++i) {
                const float sample = input ? input[done + i] : 0.0f;
                if (std::isfinite(sample)) peak = std::max(peak, std::abs(sample));
                scratch[i] = low_cut && std::isfinite(sample) ? highpass.process(sample) : sample;
            }
            const uint64_t before = active ? active->processor->processed_frames() : 0;
            const uint64_t failures = active ? active->processor->errors() : 0;
            if (active) active->processor->process(scratch.data(), processed.data(), chunk);
            else std::memcpy(processed.data(), scratch.data(), chunk * sizeof(float));
            if (active) {
                model_frames += active->processor->processed_frames() - before;
                errors += active->processor->errors() - failures;
            }
            write(processed.data(), chunk);
            done += chunk;
        }
        frames += count;
        input_peak.store(peak, std::memory_order_relaxed);
        capture_tick.store(now_ms(), std::memory_order_relaxed);
    }

    void write(const float* data, uint32_t count) noexcept {
        while (count) {
            ma_uint32 size = count;
            void* target = nullptr;
            if (ma_pcm_rb_acquire_write(&ring, &size, &target) != MA_SUCCESS || !size) { dropped_frames += count; return; }
            std::memcpy(target, data, size * sizeof(float));
            ma_pcm_rb_commit_write(&ring, size);
            data += size; count -= size;
        }
    }

    void on_playback(float* output, uint32_t count) noexcept {
        const uint32_t reserve = configured_start ? configured_fill : target_fill;
        const uint32_t limit = configured_start ? configured_max_fill : max_fill;
        ma_uint32 available = ma_pcm_rb_available_read(&ring);
        if (available > limit) {
            ma_pcm_rb_seek_read(&ring, available - reserve);
            dropped_frames += available - reserve;
            available = reserve;
        }
        if (configured_start) {
            if (available < count) playback_primed = false;
            if (!playback_primed && available >= std::max(reserve, count)) playback_primed = true;
        }
        uint32_t done = 0;
        while (done < count && (!configured_start || playback_primed)) {
            ma_uint32 size = count - done;
            void* source = nullptr;
            if (ma_pcm_rb_acquire_read(&ring, &size, &source) != MA_SUCCESS || !size) break;
            std::memcpy(output + done, source, size * sizeof(float));
            ma_pcm_rb_commit_read(&ring, size);
            done += size;
        }
        if (done < count) {
            std::memset(output + done, 0, (count - done) * sizeof(float));
            ++underruns;
            if (configured_start) playback_primed = false;
        }
        float peak = 0;
        for (uint32_t i = 0; i < count; ++i) if (std::isfinite(output[i])) peak = std::max(peak, std::abs(output[i]));
        output_peak.store(peak, std::memory_order_relaxed);
        playback_frames += count;
        playback_tick.store(now_ms(), std::memory_order_relaxed);
    }
};

void capture_callback(ma_device* device, void*, const void* input, ma_uint32 count) {
    static_cast<Bridge*>(device->pUserData)->on_capture(static_cast<const float*>(input), count);
}
void playback_callback(ma_device* device, void* output, const void*, ma_uint32 count) {
    static_cast<Bridge*>(device->pUserData)->on_playback(static_cast<float*>(output), count);
}
void notification_callback(const ma_device_notification* notification) {
    auto bridge = static_cast<Bridge*>(notification->pDevice->pUserData);
    auto& alive = notification->pDevice == &bridge->capture ? bridge->capture_alive : bridge->playback_alive;
    if (notification->type == ma_device_notification_type_started) alive.store(true);
    else if (notification->type == ma_device_notification_type_stopped || notification->type == ma_device_notification_type_interruption_began) alive.store(false);
}

#ifdef __APPLE__
OSStatus unit_render(void* ref, AudioUnitRenderActionFlags*, const AudioTimeStamp*, UInt32, UInt32 count, AudioBufferList* data) {
    static_cast<Bridge*>(ref)->on_playback(static_cast<float*>(data->mBuffers[0].mData), count);
    return noErr;
}

bool start_unit(Bridge& bridge, const char* uid) {
    CFStringRef text = CFStringCreateWithCString(nullptr, uid, kCFStringEncodingUTF8);
    if (!text) return false;
    AudioObjectPropertyAddress address{kAudioHardwarePropertyTranslateUIDToDevice, kAudioObjectPropertyScopeGlobal, 0};
    AudioDeviceID device = kAudioObjectUnknown;
    UInt32 size = sizeof(device);
    const OSStatus found = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, sizeof(text), &text, &size, &device);
    CFRelease(text);
    if (found != noErr || device == kAudioObjectUnknown) return false;
    AudioComponentDescription description{kAudioUnitType_Output, kAudioUnitSubType_HALOutput, kAudioUnitManufacturer_Apple, 0, 0};
    AudioComponent component = AudioComponentFindNext(nullptr, &description);
    if (!component || AudioComponentInstanceNew(component, &bridge.unit) != noErr) return false;
    AudioStreamBasicDescription format{rate, kAudioFormatLinearPCM, kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked, 4, 1, 4, 1, 32, 0};
    AURenderCallbackStruct callback{unit_render, &bridge};
    if (AudioUnitSetProperty(bridge.unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &device, sizeof(device)) != noErr ||
        AudioUnitSetProperty(bridge.unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0, &format, sizeof(format)) != noErr ||
        AudioUnitSetProperty(bridge.unit, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, 0, &callback, sizeof(callback)) != noErr ||
        AudioUnitInitialize(bridge.unit) != noErr || AudioOutputUnitStart(bridge.unit) != noErr) return false;
    bridge.unit_started = true;
    return true;
}
#endif

bool find(ma_context& context, bool playback, const char* name, ma_device_id& id, bool stable_ids = false) {
    ma_device_info *playbacks = nullptr, *captures = nullptr;
    ma_uint32 playback_count = 0, capture_count = 0;
    if (ma_context_get_devices(&context, &playbacks, &playback_count, &captures, &capture_count) != MA_SUCCESS) return false;
    ma_device_info* list = playback ? playbacks : captures;
    const ma_uint32 count = playback ? playback_count : capture_count;
    const bool pulse_id = stable_ids && context.backend == ma_backend_pulseaudio && std::strncmp(name, "id:", 3) == 0;
    for (ma_uint32 i = 0; i < count; ++i)
        if (std::strcmp(pulse_id ? list[i].id.pulse : list[i].name, pulse_id ? name + 3 : name) == 0) { id = list[i].id; return true; }
    return false;
}

bool init_context(ma_context& context, int backend, bool configured = false) {
    const ma_backend selected[] = { backend == 1 ? ma_backend_null : ma_backend_pulseaudio };
    ma_context_config config = ma_context_config_init();
    if (configured) config.pulse.pApplicationName = "Noise Reduction";
    const bool explicit_backend = backend == 1 || (configured && backend == 2);
    return ma_context_init(explicit_backend ? selected : nullptr, explicit_backend ? 1 : 0, &config, &context) == MA_SUCCESS;
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

static void* start_bridge(const char* input, const char* output, const char* models, int backend, uint32_t config, bool configured) {
    auto bridge = std::make_unique<Bridge>();
    try {
        bridge->models = models ? models : "";
        bridge->configured_start = configured;
        if (!configured || (config & enabled_bit)) {
            try { bridge->rnnoise = std::make_unique<nr::RnnoiseBackend>((bridge->models + "/librnnoise" + library_suffix).c_str()); }
            catch (...) { bridge->rnnoise_failed = true; ++bridge->errors; }
        }
        bridge->config.store(config);
        if (configured && !(config & enabled_bit)) {
            bridge->active = std::make_unique<Pipeline>();
            bridge->active->processor = std::make_unique<nr::Processor>(nr::Backend{}, false);
        } else bridge->active = bridge->build(configured && (config & deepfilter_bit));
        bridge->active->processor->set_attenuation(static_cast<float>(std::min(60u, (config >> 8) & 255u)));
        bridge->actual_model.store(config & enabled_bit ? bridge->active->model : 0);
        if (!init_context(bridge->context, backend, configured)) return nullptr;
        bridge->context_ready = true;
        bridge->capture_tick.store(now_ms());
        bridge->playback_tick.store(now_ms());
        if (ma_pcm_rb_init(ma_format_f32, 1, ring_frames, nullptr, nullptr, &bridge->ring) != MA_SUCCESS) return nullptr;
        bridge->ring_ready = true;

        ma_device_id input_id{}, output_id{};
        ma_device_config capture = ma_device_config_init(ma_device_type_capture);
        capture.capture.format = ma_format_f32;
        capture.capture.channels = 1;
        capture.sampleRate = rate;
        capture.periodSizeInMilliseconds = 10;
        capture.dataCallback = capture_callback;
        capture.notificationCallback = notification_callback;
        if (configured) capture.pulse.pStreamNameCapture = "Noise Reduction capture";
        capture.pUserData = bridge.get();
        if (input && *input) { if (!find(bridge->context, false, input, input_id, configured)) return nullptr; capture.capture.pDeviceID = &input_id; }
        ma_device_config playback = ma_device_config_init(ma_device_type_playback);
        playback.playback.format = ma_format_f32;
        playback.playback.channels = 1;
        playback.sampleRate = rate;
        playback.periodSizeInMilliseconds = 10;
        playback.dataCallback = playback_callback;
        playback.notificationCallback = notification_callback;
        if (configured) playback.pulse.pStreamNamePlayback = "Noise Reduction output";
        playback.pUserData = bridge.get();
        // uid: открывается отдельно, в общем списке устройств скрытого выхода нет
        const bool by_uid = output && std::strncmp(output, "uid:", 4) == 0;
        if (output && *output && !by_uid) { if (!find(bridge->context, true, output, output_id, configured)) return nullptr; playback.playback.pDeviceID = &output_id; }

        if (ma_device_init(&bridge->context, &capture, &bridge->capture) != MA_SUCCESS) return nullptr;
        bridge->capture_ready = true;
#ifdef __APPLE__
        if (output && std::strncmp(output, "uid:", 4) == 0) {
            if (!start_unit(*bridge, output + 4)) return nullptr;
            bridge->playback_alive.store(true);
            if (ma_device_start(&bridge->capture) != MA_SUCCESS) return nullptr;
            return bridge.release();
        }
#endif
        if (ma_device_init(&bridge->context, &playback, &bridge->playback) != MA_SUCCESS) return nullptr;
        bridge->playback_ready = true;
        if (ma_device_start(&bridge->playback) != MA_SUCCESS || ma_device_start(&bridge->capture) != MA_SUCCESS) return nullptr;
        return bridge.release();
    }
    catch (...) { return nullptr; }
}

NR_BRIDGE_API void* nr_bridge_start(const char* input, const char* output, const char* models, int backend) {
    return start_bridge(input, output, models, backend, enabled_bit | (24u << 8), false);
}

NR_BRIDGE_API void* nr_bridge_start_configured(const char* input, const char* output, const char* models, int backend, uint32_t config) {
    return start_bridge(input, output, models, backend, config, true);
}

NR_BRIDGE_API void nr_bridge_configure(void* handle, uint32_t config) {
    auto bridge = static_cast<Bridge*>(handle);
    if (!bridge) return;
    std::lock_guard<std::mutex> lock(bridge->configure_mutex);
    bridge->collect_retired();
    const bool want = config & deepfilter_bit;
    if (bridge->configured_start && (config & enabled_bit) && !bridge->rnnoise && !bridge->rnnoise_failed) {
        try { bridge->rnnoise = std::make_unique<nr::RnnoiseBackend>((bridge->models + "/librnnoise" + library_suffix).c_str()); }
        catch (...) { bridge->rnnoise_failed = true; ++bridge->errors; }
    }
    const bool rebuild = bridge->configured_start
        ? (config & enabled_bit) && (!bridge->build_attempted || want != bridge->requested_deepfilter)
        : want != bridge->built_deepfilter;
    if (rebuild) {
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

NR_BRIDGE_API void nr_bridge_snapshot(void* handle, nr_bridge_metrics* metrics) {
    if (!metrics) return;
    *metrics = {};
    auto bridge = static_cast<Bridge*>(handle);
    if (!bridge) return;
    metrics->capture_frames = bridge->frames.load();
    metrics->model_frames = bridge->model_frames.load();
    metrics->playback_frames = bridge->playback_frames.load();
    metrics->errors = bridge->errors.load();
    metrics->underruns = bridge->underruns.load();
    metrics->dropped_frames = bridge->dropped_frames.load();
    metrics->input_peak = bridge->input_peak.load();
    metrics->output_peak = bridge->output_peak.load();
    metrics->actual_model = bridge->actual_model.load();
    const uint64_t now = now_ms(), capture = bridge->capture_tick.load(), playback = bridge->playback_tick.load();
    metrics->running = bridge->capture_alive.load() && bridge->playback_alive.load() &&
        (capture == 0 || now - capture < 2000) && (playback == 0 || now - playback < 2000);
}

NR_BRIDGE_API void nr_bridge_stop(void* handle) { delete static_cast<Bridge*>(handle); }

NR_BRIDGE_API int nr_bridge_process(const char* models, uint32_t config, const float* input, float* output, int count) {
    if (!input || !output || count < 0) return 1;
    try {
        Bridge bridge;
        bridge.models = models ? models : "";
        try { bridge.rnnoise = std::make_unique<nr::RnnoiseBackend>((bridge.models + "/librnnoise" + library_suffix).c_str()); }
        catch (...) { ++bridge.errors; }
        auto pipeline = bridge.build(config & deepfilter_bit);
        auto& processor = pipeline->processor;
        const bool enabled = config & enabled_bit;
        processor->set_enabled(enabled);
        processor->set_attenuation(static_cast<float>(std::min(60u, (config >> 8) & 255u)));
        const size_t delay = processor->delay();
        std::vector<float> padded(static_cast<size_t>(count) + delay), result(padded.size());
        nr::HighPass highpass{static_cast<float>(rate)};
        for (int i = 0; i < count; ++i) {
            const bool cut = enabled && (config & low_cut_bit) && std::isfinite(input[i]);
            padded[i] = cut ? highpass.process(input[i]) : input[i];
        }
        processor->process(padded.data(), result.data(), padded.size());
        std::memcpy(output, result.data() + delay, static_cast<size_t>(count) * sizeof(float));
        return processor->has_model() ? 0 : 1;
    }
    catch (...) {
        std::memcpy(output, input, static_cast<size_t>(count) * sizeof(float));
        return 1;
    }
}

NR_BRIDGE_API int nr_bridge_process_ex(const char* models, uint32_t config, const float* input, float* output, int count, int* actual_model) {
    if (actual_model) *actual_model = 0;
    if (!input || !output || count < 0) return 1;
    if (!(config & enabled_bit)) {
        std::memmove(output, input, static_cast<size_t>(count) * sizeof(float));
        return 0;
    }
    try {
        Bridge bridge;
        bridge.models = models ? models : "";
        try { bridge.rnnoise = std::make_unique<nr::RnnoiseBackend>((bridge.models + "/librnnoise" + library_suffix).c_str()); }
        catch (...) { ++bridge.errors; }
        auto pipeline = bridge.build(config & deepfilter_bit);
        auto& processor = pipeline->processor;
        processor->set_attenuation(static_cast<float>(std::min(60u, (config >> 8) & 255u)));
        const size_t delay = processor->delay();
        std::vector<float> padded(static_cast<size_t>(count) + delay), result(padded.size());
        nr::HighPass highpass{static_cast<float>(rate)};
        for (int i = 0; i < count; ++i) padded[i] = (config & low_cut_bit) && std::isfinite(input[i]) ? highpass.process(input[i]) : input[i];
        processor->process(padded.data(), result.data(), padded.size());
        std::memcpy(output, result.data() + delay, static_cast<size_t>(count) * sizeof(float));
        if (actual_model) *actual_model = pipeline->model;
        const int requested = config & deepfilter_bit ? 2 : 1;
        return pipeline->model == requested && processor->errors() == 0 ? 0 : 1;
    }
    catch (...) {
        std::memmove(output, input, static_cast<size_t>(count) * sizeof(float));
        return 1;
    }
}

NR_BRIDGE_API int nr_bridge_in_use(const char* uid) {
#ifdef __APPLE__
    CFStringRef text = uid ? CFStringCreateWithCString(nullptr, uid, kCFStringEncodingUTF8) : nullptr;
    if (!text) return -1;
    AudioObjectPropertyAddress address{kAudioHardwarePropertyTranslateUIDToDevice, kAudioObjectPropertyScopeGlobal, 0};
    AudioDeviceID device = kAudioObjectUnknown;
    UInt32 size = sizeof(device);
    const OSStatus found = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, sizeof(text), &text, &size, &device);
    CFRelease(text);
    if (found != noErr || device == kAudioObjectUnknown) return -1;
    address = {kAudioDevicePropertyDeviceIsRunningSomewhere, kAudioObjectPropertyScopeGlobal, 0};
    UInt32 running = 0;
    size = sizeof(running);
    return AudioObjectGetPropertyData(device, &address, 0, nullptr, &size, &running) == noErr ? (running ? 1 : 0) : -1;
#else
    (void)uid;
    return -1;
#endif
}
