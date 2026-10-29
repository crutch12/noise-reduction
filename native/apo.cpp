#include "rnnoise_backend.h"
#include "deepfilter_backend.h"
#include "resampler.h"
#include "apo_id.h"
#include "control.h"
#include <audioenginebaseapo.h>
#include <audioengineextensionapo.h>
#include <audiomediatype.h>
#include <mmdeviceapi.h>
#include <ks.h>
#include <ksmedia.h>
#include <cmath>
#include <cstring>
#include <memory>
#include <string>
#include <stdexcept>
#include <vector>
#include <mutex>
#include <cwchar>
#ifdef NR_DIAGNOSTICS
#include <evntprov.h>
#endif

namespace {
std::atomic<long> objects{0};
std::atomic<long> locks{0};
HMODULE own_module = nullptr;
#ifdef NR_DIAGNOSTICS
REGHANDLE trace_provider = 0;
constexpr GUID trace_id{0x2521fcd0,0x8e8c,0x4edf,{0x8a,0x5c,0xf6,0x0f,0xb6,0x2e,0x58,0xd1}};
void trace(const wchar_t* text) {
    if (!trace_provider) EventRegister(&trace_id, nullptr, nullptr, &trace_provider);
    EventWriteString(trace_provider, 4, 0, text);
}
#else
void trace(const wchar_t*) {}
#endif
void trace_format(const wchar_t* operation, IAudioMediaType* media) {
#ifdef NR_DIAGNOSTICS
    UNCOMPRESSEDAUDIOFORMAT format{};
    if (!media || FAILED(media->GetUncompressedAudioFormat(&format))) { trace(L"Format unavailable"); return; }
    wchar_t message[256]{};
    std::swprintf(message, 256, L"%ls: channels=%lu rate=%.0f bytes=%lu bits=%lu subtype=%08lx", operation,
        format.dwSamplesPerFrame, format.fFramesPerSecond, format.dwBytesPerSampleContainer, format.dwValidBitsPerSample, format.guidFormatType.Data1);
    trace(message);
#else
    (void)operation; (void)media;
#endif
}
constexpr PROPERTYKEY endpoint_guid_property{{0x1da5d803, 0xd492, 0x4edd, {0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e}}, 4};
const APO_REG_PROPERTIES properties{CLSID_NoiseReduction,
    static_cast<APO_FLAG>(APO_FLAG_DEFAULT | APO_FLAG_INPLACE), L"Noise Reduction", L"",
    0, 1, 1, 1, 1, 1, ULONG_MAX, 1, {__uuidof(IAudioProcessingObject)}};

std::wstring module_file(const wchar_t* name) {
    std::wstring path(32768, L'\0');
    DWORD length = GetModuleFileNameW(own_module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) throw std::runtime_error("Cannot resolve module path");
    path.resize(length);
    path.resize(path.find_last_of(L"\\/") + 1);
    return path + name;
}
std::wstring model_path() { return module_file(L"rnnoise.dll"); }
constexpr uint32_t deepfilter_bit = 8u;
// Bluetooth гарнитуры в режиме звонка дают 8 и 16 кГц, многие USB микрофоны 44,1 кГц
bool supported_rate(float rate) {
    for (float allowed : { 8000.0f, 11025.0f, 16000.0f, 22050.0f, 24000.0f, 32000.0f, 44100.0f, 48000.0f }) if (rate == allowed) return true;
    return false;
}

// набор обработчиков на все каналы, у DeepFilterNet модель только в первом
struct ProcessorSet {
    std::vector<std::unique_ptr<nr::Processor>> processors;
    bool deepfilter = false;
    // звуковой поток складывает отработавшие наборы в список, освобождает их фоновый поток
    ProcessorSet* next_retired = nullptr;
};
void free_retired(std::atomic<ProcessorSet*>& retired) noexcept {
    for (ProcessorSet* set = retired.exchange(nullptr, std::memory_order_acq_rel); set;) {
        ProcessorSet* next = set->next_retired;
        delete set;
        set = next;
    }
}

class NoiseApo final : public IAudioProcessingObject, public IAudioProcessingObjectRT,
        public IAudioProcessingObjectConfiguration, public IAudioSystemEffects3 {
    class InnerUnknown final : public IUnknown {
    public:
        explicit InnerUnknown(NoiseApo* owner) : owner_(owner) {}
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** value) override { return owner_->inner_query(id, value); }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++owner_->refs_; }
        ULONG STDMETHODCALLTYPE Release() override { return owner_->inner_release(); }
    private:
        NoiseApo* owner_;
    };
public:
    explicit NoiseApo(IUnknown* outer = nullptr, const GUID& clsid = CLSID_NoiseReduction) : outer_(outer), inner_(this), clsid_(clsid) { ++objects; }
    ~NoiseApo() {
        controls_.stop_watching();
        // новых запросов больше нет, дожидаемся уже запущенной сборки модели
        if (work_) { WaitForThreadpoolWorkCallbacks(work_, FALSE); CloseThreadpoolWork(work_); }
        delete pending_.exchange(nullptr);
        free_retired(retired_);
        if (effects_event_) CloseHandle(effects_event_);
        --objects;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        return outer_ ? outer_->QueryInterface(id, result) : inner_query(id, result);
    }
    HRESULT inner_query(REFIID id, void** result) {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id == __uuidof(IUnknown) && outer_) { *result = static_cast<IUnknown*>(&inner_); ++refs_; return S_OK; }
        if (id == __uuidof(IUnknown) || id == __uuidof(IAudioProcessingObject)) *result = static_cast<IAudioProcessingObject*>(this);
        else if (id == __uuidof(IAudioProcessingObjectRT)) *result = static_cast<IAudioProcessingObjectRT*>(this);
        else if (id == __uuidof(IAudioProcessingObjectConfiguration)) *result = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (id == __uuidof(IAudioSystemEffects)) *result = static_cast<IAudioSystemEffects*>(this);
        else if (id == __uuidof(IAudioSystemEffects2)) *result = static_cast<IAudioSystemEffects2*>(this);
        else if (id == __uuidof(IAudioSystemEffects3)) *result = static_cast<IAudioSystemEffects3*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return outer_ ? outer_->AddRef() : ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override { return outer_ ? outer_->Release() : inner_release(); }
    ULONG inner_release() {
        ULONG remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }
    // любой сбой эффекта переводит его в пропуск звука как есть: шумодав не работает, но микрофон работает
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 size, BYTE* data) override {
        init_ready_ = false;
        __try { return initialize(size, data); }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            fail();
            if (!init_ready_) return E_FAIL;
            m_bIsInitialized = true;
            return S_OK;
        }
    }
    HRESULT initialize(UINT32 size, BYTE* data) {
        trace(L"Initialize entered");
        if (m_bIsInitialized) return APOERR_ALREADY_INITIALIZED;
        IPropertyStore* endpoint = nullptr;
        bool discovery = false;
        standalone_ = size == 0 && data == nullptr;
        if (!standalone_) {
            if (!data || size < sizeof(APOInitBaseStruct)) return E_INVALIDARG;
            auto base = reinterpret_cast<const APOInitBaseStruct*>(data);
            if (base->cbSize != size || base->clsid != clsid_) return APOERR_INVALID_APO_CLSID;
            if (size == sizeof(APOInitSystemEffects3)) {
                auto init = reinterpret_cast<const APOInitSystemEffects3*>(data);
                endpoint = init->pAPOEndpointProperties; discovery = init->InitializeForDiscoveryOnly != FALSE;
            } else if (size == sizeof(APOInitSystemEffects2)) {
                auto init = reinterpret_cast<const APOInitSystemEffects2*>(data);
                endpoint = init->pAPOEndpointProperties; discovery = init->InitializeForDiscoveryOnly != FALSE;
            } else if (size == sizeof(APOInitSystemEffects)) endpoint = reinterpret_cast<const APOInitSystemEffects*>(data)->pAPOEndpointProperties;
            else return E_INVALIDARG;
            if (!endpoint) return E_INVALIDARG;
        }
        init_ready_ = true;
        try {
            if (endpoint) {
                PROPVARIANT value{};
                HRESULT status = endpoint->GetValue(endpoint_guid_property, &value);
                if (SUCCEEDED(status) && value.vt == VT_LPWSTR && value.pwszVal) controls_.open_endpoint(value.pwszVal);
                PropVariantClear(&value);
            }
            discovery_ = discovery;
            if (!standalone_) work_ = CreateThreadpoolWork([](PTP_CALLBACK_INSTANCE, void* self, PTP_WORK) noexcept { static_cast<NoiseApo*>(self)->guarded_switch(); }, this, nullptr);
            controls_.watch([](void* context) noexcept { auto self = static_cast<NoiseApo*>(context); self->notify_effects(); self->request_switch(); }, this);
            if (!discovery) {
                try { backend_ = std::make_unique<nr::RnnoiseBackend>(model_path().c_str()); model_usable_.store(true); }
                catch (...) { controls_.error(); }
            }
        }
        catch (...) { fail(); }
        m_bIsInitialized = true;
        trace(controls_.available() ? L"Initialize: mapped control available" : L"Initialize: mapped control unavailable");
        trace(backend_ ? L"Initialize: model loaded" : L"Initialize: no model or discovery only");
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetEffectsList(GUID** ids, UINT* count, HANDLE event) override {
        trace(effective_enabled() ? L"GetEffectsList: enabled" : L"GetEffectsList: disabled");
        if (!ids || !count) return E_POINTER;
        *ids = nullptr; *count = 0;
        HRESULT status = remember_event(event);
        if (FAILED(status)) return status;
        if (!effective_enabled()) return S_OK;
        auto result = static_cast<GUID*>(CoTaskMemAlloc(sizeof(GUID)));
        if (!result) return E_OUTOFMEMORY;
        *result = AUDIO_EFFECT_TYPE_NOISE_SUPPRESSION;
        *ids = result; *count = 1;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetControllableSystemEffectsList(AUDIO_SYSTEMEFFECT** effects, UINT* count, HANDLE event) override {
        if (!effects || !count) return E_POINTER;
        *effects = nullptr; *count = 0;
        HRESULT status = remember_event(event);
        if (FAILED(status)) return status;
        auto result = static_cast<AUDIO_SYSTEMEFFECT*>(CoTaskMemAlloc(sizeof(AUDIO_SYSTEMEFFECT)));
        if (!result) return E_OUTOFMEMORY;
        result->id = AUDIO_EFFECT_TYPE_NOISE_SUPPRESSION;
        result->canSetState = TRUE;
        result->state = effective_enabled() ? AUDIO_SYSTEMEFFECT_STATE_ON : AUDIO_SYSTEMEFFECT_STATE_OFF;
        *effects = result; *count = 1;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetAudioSystemEffectState(GUID id, AUDIO_SYSTEMEFFECT_STATE state) override {
        if (id != AUDIO_EFFECT_TYPE_NOISE_SUPPRESSION) return E_INVALIDARG;
        if (state != AUDIO_SYSTEMEFFECT_STATE_ON && state != AUDIO_SYSTEMEFFECT_STATE_OFF) return E_INVALIDARG;
        system_enabled_.store(state == AUDIO_SYSTEMEFFECT_STATE_ON, std::memory_order_relaxed);
        std::lock_guard<std::mutex> lock(effects_mutex_);
        if (effects_event_) SetEvent(effects_event_);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* value) override {
        if (!value) return E_POINTER;
        *value = static_cast<HNSTIME>(latency_.load(std::memory_order_relaxed)) * 10000000LL / 48000;
        return S_OK;
    }
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 value) override { return value; }
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 value) override { return value; }
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* value) override {
        trace(L"GetInputChannelCount entered");
        if (!value) return E_POINTER;
        *value = channels_;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** value) override {
        if (!value) return E_POINTER;
        *value = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(sizeof(properties)));
        if (!*value) return E_OUTOFMEMORY;
        std::memcpy(*value, &properties, sizeof(properties));
        (*value)->clsid = clsid_;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        return negotiate(opposite, requested, supported);
    }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        return negotiate(opposite, requested, supported);
    }
    HRESULT STDMETHODCALLTYPE Reset() override {
        __try { return reset(); }
        __except (EXCEPTION_EXECUTE_HANDLER) { fail(); return S_OK; }
    }
    HRESULT reset() {
        if (!m_bIsInitialized) return APOERR_NOT_INITIALIZED;
        // Reset is called with processing stopped, while connections stay locked
        if (m_bIsLocked && !broken_.load()) {
            try {
                std::lock_guard<std::mutex> lock(switch_mutex_);
                install(build_set(wants_deepfilter(), channels_));
                highpass_.assign(channels_, nr::HighPass(static_cast<float>(rate_)));
                build_stages();
                energy_.assign(channels_, 0.0f);
                previous_settings_ = UINT32_MAX;
            }
            catch (...) { fail(); }
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 inputs, APO_CONNECTION_DESCRIPTOR** in,
            UINT32 outputs, APO_CONNECTION_DESCRIPTOR** out) override {
        lock_ready_ = false;
        __try { return lock(inputs, in, outputs, out); }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            fail();
            if (!lock_ready_) return E_FAIL;
            m_bIsLocked = true;
            return S_OK;
        }
    }
    HRESULT lock(UINT32 inputs, APO_CONNECTION_DESCRIPTOR** in, UINT32 outputs, APO_CONNECTION_DESCRIPTOR** out) {
        trace(L"LockForProcess entered");
        if (m_bIsLocked) return APOERR_APO_LOCKED;
        if (!m_bIsInitialized || discovery_) return APOERR_NOT_INITIALIZED;
        if (inputs != 1 || outputs != 1) return APOERR_NUM_CONNECTIONS_INVALID;
        if (!in || !out || !in[0] || !out[0]) return E_POINTER;
        if (in[0]->u32Signature != APO_CONNECTION_DESCRIPTOR_SIGNATURE || out[0]->u32Signature != APO_CONNECTION_DESCRIPTOR_SIGNATURE) return E_INVALIDARG;
        if (!in[0]->pFormat || !out[0]->pFormat) return E_POINTER;
        trace_format(L"Lock input", in[0]->pFormat);
        trace_format(L"Lock output", out[0]->pFormat);
        if (!in[0]->u32MaxFrameCount || out[0]->u32MaxFrameCount < in[0]->u32MaxFrameCount) return APOERR_INVALID_OUTPUT_MAXFRAMECOUNT;
        if (!valid_format(in[0]->pFormat) || !valid_format(out[0]->pFormat)) return APOERR_FORMAT_NOT_SUPPORTED;
        UNCOMPRESSEDAUDIOFORMAT input_format{}, output_format{};
        in[0]->pFormat->GetUncompressedAudioFormat(&input_format);
        out[0]->pFormat->GetUncompressedAudioFormat(&output_format);
        if (input_format.dwSamplesPerFrame != output_format.dwSamplesPerFrame) return APOERR_FORMAT_NOT_SUPPORTED;
        // формат согласован, дальше любой сбой означает только пропуск звука без обработки
        channels_ = input_format.dwSamplesPerFrame;
        max_frames_ = in[0]->u32MaxFrameCount;
        lock_ready_ = true;
        try {
#ifdef NR_FAULT_TEST
            if (fault(L"lock")) throw std::bad_alloc();
#endif
            std::lock_guard<std::mutex> lock(switch_mutex_);
            scratch_in_.resize(in[0]->u32MaxFrameCount);
            scratch_out_.resize(in[0]->u32MaxFrameCount);
            install(build_set(wants_deepfilter(), input_format.dwSamplesPerFrame));
            rate_ = static_cast<uint32_t>(input_format.fFramesPerSecond);
            highpass_.assign(input_format.dwSamplesPerFrame, nr::HighPass(input_format.fFramesPerSecond));
            energy_.assign(input_format.dwSamplesPerFrame, 0.0f);
            previous_settings_ = UINT32_MAX;
            switch_channels_ = input_format.dwSamplesPerFrame;
            build_stages();
        }
        catch (...) { fail(); }
        m_bIsLocked = true;
        trace(L"LockForProcess succeeded");
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        if (!m_bIsLocked) return APOERR_ALREADY_UNLOCKED;
        m_bIsLocked = false;
        // после падения мьютекс мог остаться захваченным, набор моделей освободит деструктор
        if (broken_.load()) { switch_channels_ = 0; max_frames_ = 0; return S_OK; }
        std::lock_guard<std::mutex> lock(switch_mutex_);
        switch_channels_ = 0;
        install(nullptr);
        max_frames_ = 0;
        return S_OK;
    }
    void STDMETHODCALLTYPE APOProcess(UINT32 inputs, APO_CONNECTION_PROPERTY** in,
            UINT32 outputs, APO_CONNECTION_PROPERTY** out) override {
        if (outputs != 1 || !out || !out[0]) return;
        if (!broken_.load(std::memory_order_relaxed) && guarded_process(inputs, in, *out[0])) return;
        bypass(inputs, in, *out[0]);
    }
private:
    bool guarded_process(UINT32 inputs, APO_CONNECTION_PROPERTY** in, APO_CONNECTION_PROPERTY& dest) noexcept {
        __try { return process(inputs, in, dest); }
        __except (EXCEPTION_EXECUTE_HANDLER) { fail(); return false; }
    }
    // звук как есть, без модели и фильтров
    void bypass(UINT32 inputs, APO_CONNECTION_PROPERTY** in, APO_CONNECTION_PROPERTY& dest) noexcept {
        dest.u32BufferFlags = BUFFER_INVALID;
        dest.u32ValidFrameCount = 0;
        if (!m_bIsLocked || inputs != 1 || !in || !in[0] || !dest.pBuffer || dest.u32Signature != APO_CONNECTION_PROPERTY_SIGNATURE) return;
        const APO_CONNECTION_PROPERTY& source = *in[0];
        if (source.u32Signature != APO_CONNECTION_PROPERTY_SIGNATURE || source.u32ValidFrameCount > max_frames_) return;
        const size_t bytes = static_cast<size_t>(source.u32ValidFrameCount) * channels_ * sizeof(float);
        if (source.u32BufferFlags == BUFFER_VALID && source.pBuffer) memmove(reinterpret_cast<void*>(dest.pBuffer), reinterpret_cast<const void*>(source.pBuffer), bytes);
        else if (source.u32BufferFlags == BUFFER_SILENT) memset(reinterpret_cast<void*>(dest.pBuffer), 0, bytes);
        else return;
        dest.u32ValidFrameCount = source.u32ValidFrameCount;
        dest.u32BufferFlags = source.u32BufferFlags;
    }
    // дальше эффект только пропускает звук, а приложение видит ошибку по счётчику
    void fail() noexcept {
        broken_.store(true);
        model_usable_.store(false);
        latency_.store(0);
        controls_.error();
    }
#ifdef NR_FAULT_TEST
    static bool fault(const wchar_t* kind) noexcept {
        wchar_t value[16]{};
        return GetEnvironmentVariableW(L"NR_FAULT", value, 16) && wcscmp(value, kind) == 0;
    }
#endif
    bool process(UINT32 inputs, APO_CONNECTION_PROPERTY** in, APO_CONNECTION_PROPERTY& dest) {
        const APO_CONNECTION_PROPERTY source = inputs == 1 && in && in[0] ? *in[0] : APO_CONNECTION_PROPERTY{};
        dest.u32BufferFlags = BUFFER_INVALID;
        dest.u32ValidFrameCount = 0;
        // готовый набор с другой моделью собран в фоне, здесь только подхватываем его
        if (ProcessorSet* next = pending_.exchange(nullptr, std::memory_order_acq_rel)) {
            if (ProcessorSet* old = active_.release()) {
                old->next_retired = retired_.load(std::memory_order_acquire);
                while (!retired_.compare_exchange_weak(old->next_retired, old, std::memory_order_acq_rel, std::memory_order_acquire)) {}
            }
            active_.reset(next);
            controls_.model(active_->deepfilter);
            previous_settings_ = UINT32_MAX;
        }
        if (!m_bIsLocked || !active_ || active_->processors.empty() || inputs != 1 || !in || !in[0]) return false;
        auto& processors = active_->processors;
        if (source.u32Signature != APO_CONNECTION_PROPERTY_SIGNATURE || dest.u32Signature != APO_CONNECTION_PROPERTY_SIGNATURE) return false;
        if (source.u32BufferFlags != BUFFER_VALID && source.u32BufferFlags != BUFFER_SILENT) return false;
        if (source.u32ValidFrameCount > max_frames_ || !dest.pBuffer) return false;
        if (source.u32BufferFlags == BUFFER_VALID && !source.pBuffer) return false;
#ifdef NR_FAULT_TEST
        if (fault(L"process") && ++fault_calls_ > 5) RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
#endif
        auto output = reinterpret_cast<float*>(dest.pBuffer);
        auto input = source.u32BufferFlags == BUFFER_SILENT ? nullptr : reinterpret_cast<const float*>(source.pBuffer);
        const uint32_t settings = standalone_ ? (1u | (24u << 8)) : controls_.config();
        const bool enabled = (settings & 1u) && system_enabled_.load(std::memory_order_relaxed) && model_usable_.load(std::memory_order_relaxed);
        const bool low_cut = enabled && (settings & 2u);
        uint64_t inferred = 0;
        bool silent = true;
        if (enabled && ((settings & 4u) || active_->deepfilter) && channels_ > 1 && input) {
            // одна модель на все каналы, результат одинаково во все каналы, DeepFilterNet всегда так
            nr::mix_active(input, source.u32ValidFrameCount, channels_, energy_.data(), scratch_in_.data());
            if (low_cut) for (UINT32 i = 0; i < source.u32ValidFrameCount; ++i) scratch_in_[i] = highpass_[0].process(scratch_in_[i]);
            else if (previous_settings_ & 2u) highpass_[0].reset();
            processors[0]->set_enabled(true);
            if (settings != previous_settings_) processors[0]->set_attenuation(static_cast<float>(std::min(60u, (settings >> 8) & 255u)));
            const uint64_t before = processors[0]->processed_frames();
            run(processors, 0, scratch_in_.data(), scratch_out_.data(), source.u32ValidFrameCount);
            inferred = processors[0]->processed_frames() - before;
            for (UINT32 i = 0; i < source.u32ValidFrameCount; ++i) {
                for (UINT32 channel = 0; channel < channels_; ++channel) output[static_cast<size_t>(i) * channels_ + channel] = scratch_out_[i];
                silent = silent && scratch_out_[i] == 0;
            }
            previous_settings_ = settings;
            controls_.account(source.u32ValidFrameCount, inferred);
            dest.u32ValidFrameCount = source.u32ValidFrameCount;
            dest.u32BufferFlags = silent ? BUFFER_SILENT : BUFFER_VALID;
            return true;
        }
        for (UINT32 channel = 0; channel < channels_; ++channel) {
            if (!low_cut && ((previous_settings_ & 2u) || previous_settings_ == UINT32_MAX)) highpass_[channel].reset();
            processors[channel]->set_enabled(enabled);
            if (settings != previous_settings_) processors[channel]->set_attenuation(static_cast<float>(std::min(60u, (settings >> 8) & 255u)));
            const uint64_t before = processors[channel]->processed_frames();
            if (input) for (UINT32 i = 0; i < source.u32ValidFrameCount; ++i) {
                const float sample = input[static_cast<size_t>(i) * channels_ + channel];
                scratch_in_[i] = low_cut && std::isfinite(sample) ? highpass_[channel].process(sample) : sample;
            }
            run(processors, channel, input ? scratch_in_.data() : nullptr, scratch_out_.data(), source.u32ValidFrameCount);
            inferred += processors[channel]->processed_frames() - before;
            for (UINT32 i = 0; i < source.u32ValidFrameCount; ++i) {
                output[static_cast<size_t>(i) * channels_ + channel] = scratch_out_[i];
                silent = silent && scratch_out_[i] == 0;
            }
        }
        previous_settings_ = settings;
        controls_.account(source.u32ValidFrameCount, inferred);
        dest.u32ValidFrameCount = source.u32ValidFrameCount;
        dest.u32BufferFlags = silent ? BUFFER_SILENT : BUFFER_VALID;
        return true;
    }
private:
    HRESULT remember_event(HANDLE event) {
        HANDLE copy = nullptr;
        if (event && !DuplicateHandle(GetCurrentProcess(), event, GetCurrentProcess(), &copy, 0, FALSE, DUPLICATE_SAME_ACCESS)) return HRESULT_FROM_WIN32(GetLastError());
        std::lock_guard<std::mutex> lock(effects_mutex_);
        if (effects_event_) CloseHandle(effects_event_);
        effects_event_ = copy;
        return S_OK;
    }
    bool effective_enabled() const noexcept {
        return model_usable_.load(std::memory_order_relaxed) && system_enabled_.load(std::memory_order_relaxed) && (standalone_ || (controls_.config() & 1u));
    }
    void notify_effects() noexcept {
        std::lock_guard<std::mutex> lock(effects_mutex_);
        if (effects_event_) SetEvent(effects_event_);
    }
    void build_stages() {
        std::vector<std::unique_ptr<nr::RateStage>> fresh;
        if (rate_ != 48000) for (UINT32 i = 0; i < channels_; ++i) fresh.push_back(std::make_unique<nr::RateStage>(rate_, max_frames_));
        stages_ = std::move(fresh);
    }
    // модель всегда работает на 48 кГц, на других частотах звук проходит через пересчёт туда и обратно
    void run(std::vector<std::unique_ptr<nr::Processor>>& processors, UINT32 index, const float* in, float* out, UINT32 count) noexcept {
        auto& processor = *processors[index];
        if (stages_.empty()) { processor.process(in, out, count); return; }
        stages_[index]->process(in, out, count, [&processor](const float* a, float* b, size_t n) { processor.process(a, b, n); });
    }
    bool wants_deepfilter() const noexcept { return !standalone_ && (controls_.config() & deepfilter_bit) && !deepfilter_failed_; }
    // вызывается под switch_mutex_, DeepFilterNet грузится при первом выборе
    std::unique_ptr<ProcessorSet> build_set(bool deepfilter, UINT32 channels) {
        auto set = std::make_unique<ProcessorSet>();
        nr::Backend model = backend_ ? backend_->api() : nr::Backend{};
        if (deepfilter && !deepfilter_ && !deepfilter_failed_) {
            try { deepfilter_ = std::make_unique<nr::DeepFilterBackend>(module_file(L"nr_deepfilter.dll").c_str()); }
            catch (...) { deepfilter_failed_ = true; controls_.error(); }
        }
        if (deepfilter && deepfilter_) { model = deepfilter_->api(); set->deepfilter = true; }
        const bool enabled = effective_enabled();
        for (UINT32 i = 0; i < channels; ++i) {
            // остальным каналам модель не нужна, только та же задержка исходного звука
            nr::Backend backend = set->deepfilter && i > 0 ? nr::Backend{nullptr, nullptr, nullptr, model.latency} : model;
            set->processors.push_back(std::make_unique<nr::Processor>(backend, enabled));
        }
        return set;
    }
    // звук остановлен или ещё не запущен, набор можно заменить напрямую
    void install(std::unique_ptr<ProcessorSet> set) noexcept {
        delete pending_.exchange(nullptr);
        free_retired(retired_);
        active_ = std::move(set);
        published_deepfilter_ = active_ && active_->deepfilter;
        if (active_) { update_model_status(*active_); controls_.model(active_->deepfilter); }
    }
    void update_model_status(const ProcessorSet& set) noexcept {
        bool usable = !set.processors.empty();
        if (set.deepfilter && usable) usable = set.processors[0]->has_model();
        else for (const auto& processor : set.processors) usable = usable && processor->has_model();
        if (usable) latency_.store(set.processors[0]->delay(), std::memory_order_relaxed);
        model_usable_.store(usable, std::memory_order_relaxed);
        if (!usable) controls_.error();
    }
    // каждое изменение настроек ставит сборку в очередь, деструктор её дожидается
    void request_switch() noexcept { if (work_) SubmitThreadpoolWork(work_); }
    // сборка новой модели идёт здесь, звуковой поток её только подхватывает
    void guarded_switch() noexcept {
        if (broken_.load()) return;
        __try { switch_model(); }
        __except (EXCEPTION_EXECUTE_HANDLER) { fail(); }
    }
    void switch_model() noexcept {
        try {
            std::lock_guard<std::mutex> lock(switch_mutex_);
            free_retired(retired_);
            const bool want = wants_deepfilter();
            if (!switch_channels_ || want == published_deepfilter_) return;
            auto set = build_set(want, switch_channels_);
            update_model_status(*set);
            published_deepfilter_ = set->deepfilter;
            delete pending_.exchange(set.release(), std::memory_order_acq_rel);
        }
        catch (...) { controls_.error(); }
    }
    static bool valid_format(IAudioMediaType* media) {
        UNCOMPRESSEDAUDIOFORMAT format{};
        if (!media || FAILED(media->GetUncompressedAudioFormat(&format))) return false;
        return format.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT && format.dwSamplesPerFrame >= 1 && format.dwSamplesPerFrame <= 8 &&
            format.dwBytesPerSampleContainer == 4 && format.dwValidBitsPerSample == 32 && supported_rate(format.fFramesPerSecond);
    }
    static HRESULT negotiate(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) {
        trace_format(L"Negotiate requested", requested);
        if (opposite) trace_format(L"Negotiate opposite", opposite);
        if (!requested || !supported) return E_POINTER;
        if (!valid_format(requested)) {
            UNCOMPRESSEDAUDIOFORMAT format{}, other{};
            if (FAILED(requested->GetUncompressedAudioFormat(&format)) || format.guidFormatType != KSDATAFORMAT_SUBTYPE_PCM ||
                !supported_rate(format.fFramesPerSecond) || format.dwSamplesPerFrame < 1 || format.dwSamplesPerFrame > 8 ||
                format.dwBytesPerSampleContainer < 1 || format.dwBytesPerSampleContainer > 4 ||
                format.dwValidBitsPerSample < 1 || format.dwValidBitsPerSample > format.dwBytesPerSampleContainer * 8 ||
                (opposite && (!valid_format(opposite) || FAILED(opposite->GetUncompressedAudioFormat(&other)) ||
                    other.dwSamplesPerFrame != format.dwSamplesPerFrame))) return APOERR_FORMAT_NOT_SUPPORTED;
            format.guidFormatType = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            format.dwBytesPerSampleContainer = 4;
            format.dwValidBitsPerSample = 32;
            HRESULT result = CreateAudioMediaTypeFromUncompressedAudioFormat(&format, supported);
            trace(L"Negotiate: suggest float32 for PCM hardware");
            return SUCCEEDED(result) ? S_FALSE : result;
        }
        if (opposite && !valid_format(opposite)) return APOERR_FORMAT_NOT_SUPPORTED;
        if (opposite) {
            UNCOMPRESSEDAUDIOFORMAT a{}, b{};
            requested->GetUncompressedAudioFormat(&a); opposite->GetUncompressedAudioFormat(&b);
            if (a.dwSamplesPerFrame != b.dwSamplesPerFrame) { trace(L"Negotiate: different channel counts"); return APOERR_FORMAT_NOT_SUPPORTED; }
        }
        requested->AddRef();
        *supported = requested;
        return S_OK;
    }
    bool m_bIsInitialized = false;
    bool standalone_ = false;
    bool discovery_ = false;
    bool m_bIsLocked = false;
    std::atomic<ULONG> refs_{1};
    IUnknown* outer_;
    InnerUnknown inner_;
    std::unique_ptr<nr::RnnoiseBackend> backend_;
    std::unique_ptr<nr::DeepFilterBackend> deepfilter_;
    std::atomic<bool> deepfilter_failed_{false};
    std::unique_ptr<ProcessorSet> active_;
    std::atomic<ProcessorSet*> pending_{nullptr}, retired_{nullptr};
    std::mutex switch_mutex_;
    PTP_WORK work_ = nullptr;
    std::atomic<UINT32> switch_channels_{0};
    std::atomic<bool> published_deepfilter_{false};
    std::atomic<size_t> latency_{nr::Processor::latency};
    std::vector<float> scratch_in_, scratch_out_;
    std::vector<nr::HighPass> highpass_;
    std::vector<float> energy_;
    // пересчёт частоты по каналу, пусто при 48 кГц
    std::vector<std::unique_ptr<nr::RateStage>> stages_;
    uint32_t rate_ = 48000;
    UINT32 channels_ = 1;
    GUID clsid_;
    UINT32 max_frames_ = 0;
    nr::ControlView controls_;
    uint32_t previous_settings_ = UINT32_MAX;
    std::atomic<bool> system_enabled_{true};
    std::atomic<bool> model_usable_{false};
    std::atomic<bool> broken_{false};
    bool init_ready_ = false, lock_ready_ = false;
#ifdef NR_FAULT_TEST
    int fault_calls_ = 0;
#endif
    std::mutex effects_mutex_;
    HANDLE effects_event_ = nullptr;
};

class Factory final : public IClassFactory {
public:
    explicit Factory(const GUID& clsid) : clsid_(clsid) { ++objects; }
    ~Factory() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id != __uuidof(IUnknown) && id != __uuidof(IClassFactory)) return E_NOINTERFACE;
        *result = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG n = --refs_; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (outer && id != __uuidof(IUnknown)) return CLASS_E_NOAGGREGATION;
        try {
            auto instance = new NoiseApo(outer, clsid_);
            HRESULT status = instance->inner_query(id, result);
            instance->inner_release();
            return status;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) ++locks; else --locks; return S_OK; }
private:
    std::atomic<ULONG> refs_{1};
    GUID clsid_;
};

// проверка на своём голосе, та же модель, что выбрана для микрофона
struct OfflineEngine {
    std::unique_ptr<nr::RnnoiseBackend> rnnoise;
    std::unique_ptr<nr::DeepFilterBackend> deepfilter;
    std::unique_ptr<nr::Processor> processor;
    explicit OfflineEngine(bool use_deepfilter) {
        if (use_deepfilter) { deepfilter = std::make_unique<nr::DeepFilterBackend>(module_file(L"nr_deepfilter.dll").c_str()); processor = std::make_unique<nr::Processor>(deepfilter->api()); }
        else { rnnoise = std::make_unique<nr::RnnoiseBackend>(model_path().c_str()); processor = std::make_unique<nr::Processor>(rnnoise->api()); }
        if (!processor->has_model()) throw std::bad_alloc();
        ++objects;
    }
    ~OfflineEngine() { processor.reset(); --objects; }
};
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) { own_module = module; DisableThreadLibraryCalls(module); }
#ifdef NR_DIAGNOSTICS
    if (reason == DLL_PROCESS_DETACH && trace_provider) EventUnregister(trace_provider);
#endif
    return TRUE;
}
STDAPI DllCanUnloadNow() { return objects == 0 && locks == 0 ? S_OK : S_FALSE; }
STDAPI DllGetClassObject(REFCLSID id, REFIID interface_id, void** result) {
    trace(L"DllGetClassObject entered");
    if (!result) return E_POINTER;
    *result = nullptr;
    if (id != CLSID_NoiseReduction && id != CLSID_NoiseReductionComponent) return CLASS_E_CLASSNOTAVAILABLE;
    try {
        auto factory = new Factory(id);
        HRESULT status = factory->QueryInterface(interface_id, result);
        factory->Release();
        return status;
    } catch (...) { return E_OUTOFMEMORY; }
}
extern "C" __declspec(dllexport) void* __cdecl nr_create() noexcept {
    try { return new OfflineEngine(false); } catch (...) { return nullptr; }
}
extern "C" __declspec(dllexport) void* __cdecl nr_create_model(int deepfilter) noexcept {
    try { return new OfflineEngine(deepfilter != 0); } catch (...) { return nullptr; }
}
extern "C" __declspec(dllexport) uint32_t __cdecl nr_get_engine_latency(void* handle) noexcept {
    return handle ? static_cast<uint32_t>(static_cast<OfflineEngine*>(handle)->processor->delay()) : 0;
}
extern "C" __declspec(dllexport) void __cdecl nr_destroy(void* handle) noexcept { delete static_cast<OfflineEngine*>(handle); }
extern "C" __declspec(dllexport) void __cdecl nr_process(void* handle, const float* input, float* output, uint32_t count) noexcept {
    if (handle && output) static_cast<OfflineEngine*>(handle)->processor->process(input, output, count);
}
extern "C" __declspec(dllexport) void __cdecl nr_set_enabled(void* handle, int enabled) noexcept {
    if (handle) static_cast<OfflineEngine*>(handle)->processor->set_enabled(enabled != 0);
}
extern "C" __declspec(dllexport) void __cdecl nr_set_attenuation(void* handle, float db) noexcept {
    if (handle) static_cast<OfflineEngine*>(handle)->processor->set_attenuation(db);
}
extern "C" __declspec(dllexport) uint32_t __cdecl nr_get_latency() noexcept { return nr::Processor::latency; }
