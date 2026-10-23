#include "rnnoise_backend.h"
#include "apo_id.h"
#include <audioenginebaseapo.h>
#include <ks.h>
#include <ksmedia.h>
#include <cstring>
#include <memory>
#include <string>
#include <stdexcept>
#include <vector>

namespace {
std::atomic<long> objects{0};
std::atomic<long> locks{0};
HMODULE own_module = nullptr;
const APO_REG_PROPERTIES properties{CLSID_NoiseReduction,
    static_cast<APO_FLAG>(APO_FLAG_DEFAULT | APO_FLAG_INPLACE), L"Noise Reduction Prototype", L"",
    0, 1, 1, 1, 1, 1, ULONG_MAX, 1, {__uuidof(IAudioSystemEffects)}};

std::wstring model_path() {
    std::wstring path(32768, L'\0');
    DWORD length = GetModuleFileNameW(own_module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) throw std::runtime_error("Cannot resolve module path");
    path.resize(length);
    path.resize(path.find_last_of(L"\\/") + 1);
    return path + L"rnnoise.dll";
}

class NoiseApo final : public IAudioProcessingObject, public IAudioProcessingObjectRT,
        public IAudioProcessingObjectConfiguration, public IAudioSystemEffects {
public:
    NoiseApo() { ++objects; }
    ~NoiseApo() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id == __uuidof(IUnknown) || id == __uuidof(IAudioProcessingObject)) *result = static_cast<IAudioProcessingObject*>(this);
        else if (id == __uuidof(IAudioProcessingObjectRT)) *result = static_cast<IAudioProcessingObjectRT*>(this);
        else if (id == __uuidof(IAudioProcessingObjectConfiguration)) *result = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (id == __uuidof(IAudioSystemEffects)) *result = static_cast<IAudioSystemEffects*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 size, BYTE* data) override {
        if (m_bIsInitialized) return APOERR_ALREADY_INITIALIZED;
        // Standalone prototype only, real endpoint initialization is not yet supported
        if (size || data) return E_INVALIDARG;
        try { backend_ = std::make_unique<nr::RnnoiseBackend>(model_path().c_str()); }
        catch (...) { return E_FAIL; }
        m_bIsInitialized = true;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* value) override {
        if (!value) return E_POINTER;
        *value = nr::Processor::latency * 10000000LL / 48000;
        return S_OK;
    }
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 value) override { return value; }
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 value) override { return value; }
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* value) override {
        if (!value) return E_POINTER;
        *value = channels_;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** value) override {
        if (!value) return E_POINTER;
        *value = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(sizeof(properties)));
        if (!*value) return E_OUTOFMEMORY;
        std::memcpy(*value, &properties, sizeof(properties));
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        return negotiate(opposite, requested, supported);
    }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        return negotiate(opposite, requested, supported);
    }
    HRESULT STDMETHODCALLTYPE Reset() override {
        if (!m_bIsInitialized) return APOERR_NOT_INITIALIZED;
        // Reset is called with processing stopped, while connections stay locked
        if (m_bIsLocked) {
            try {
                std::vector<std::unique_ptr<nr::Processor>> fresh;
                for (UINT32 i = 0; i < channels_; ++i) fresh.push_back(std::make_unique<nr::Processor>(backend_->api()));
                processors_ = std::move(fresh);
            }
            catch (...) { return E_OUTOFMEMORY; }
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 inputs, APO_CONNECTION_DESCRIPTOR** in,
            UINT32 outputs, APO_CONNECTION_DESCRIPTOR** out) override {
        if (m_bIsLocked) return APOERR_APO_LOCKED;
        if (!m_bIsInitialized) return APOERR_NOT_INITIALIZED;
        if (inputs != 1 || outputs != 1) return APOERR_NUM_CONNECTIONS_INVALID;
        if (!in || !out || !in[0] || !out[0]) return E_POINTER;
        if (in[0]->u32Signature != APO_CONNECTION_DESCRIPTOR_SIGNATURE || out[0]->u32Signature != APO_CONNECTION_DESCRIPTOR_SIGNATURE) return E_INVALIDARG;
        if (!in[0]->pFormat || !out[0]->pFormat) return E_POINTER;
        if (!in[0]->u32MaxFrameCount || out[0]->u32MaxFrameCount < in[0]->u32MaxFrameCount) return APOERR_INVALID_OUTPUT_MAXFRAMECOUNT;
        if (!valid_format(in[0]->pFormat) || !valid_format(out[0]->pFormat)) return APOERR_FORMAT_NOT_SUPPORTED;
        UNCOMPRESSEDAUDIOFORMAT input_format{}, output_format{};
        in[0]->pFormat->GetUncompressedAudioFormat(&input_format);
        out[0]->pFormat->GetUncompressedAudioFormat(&output_format);
        if (input_format.dwSamplesPerFrame != output_format.dwSamplesPerFrame) return APOERR_FORMAT_NOT_SUPPORTED;
        try {
            std::vector<std::unique_ptr<nr::Processor>> fresh;
            for (UINT32 i = 0; i < input_format.dwSamplesPerFrame; ++i) fresh.push_back(std::make_unique<nr::Processor>(backend_->api()));
            scratch_in_.resize(in[0]->u32MaxFrameCount);
            scratch_out_.resize(in[0]->u32MaxFrameCount);
            processors_ = std::move(fresh);
        }
        catch (...) { return E_OUTOFMEMORY; }
        channels_ = input_format.dwSamplesPerFrame;
        max_frames_ = in[0]->u32MaxFrameCount;
        m_bIsLocked = true;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        if (!m_bIsLocked) return APOERR_ALREADY_UNLOCKED;
        m_bIsLocked = false;
        processors_.clear(); max_frames_ = 0;
        return S_OK;
    }
    void STDMETHODCALLTYPE APOProcess(UINT32 inputs, APO_CONNECTION_PROPERTY** in,
            UINT32 outputs, APO_CONNECTION_PROPERTY** out) override {
        if (outputs != 1 || !out || !out[0]) return;
        const APO_CONNECTION_PROPERTY source = inputs == 1 && in && in[0] ? *in[0] : APO_CONNECTION_PROPERTY{};
        auto& dest = *out[0];
        dest.u32BufferFlags = BUFFER_INVALID;
        dest.u32ValidFrameCount = 0;
        if (!m_bIsLocked || processors_.empty() || inputs != 1 || !in || !in[0]) return;
        if (source.u32Signature != APO_CONNECTION_PROPERTY_SIGNATURE || dest.u32Signature != APO_CONNECTION_PROPERTY_SIGNATURE) return;
        if (source.u32BufferFlags != BUFFER_VALID && source.u32BufferFlags != BUFFER_SILENT) return;
        if (source.u32ValidFrameCount > max_frames_ || !dest.pBuffer) return;
        if (source.u32BufferFlags == BUFFER_VALID && !source.pBuffer) return;
        auto output = reinterpret_cast<float*>(dest.pBuffer);
        auto input = source.u32BufferFlags == BUFFER_SILENT ? nullptr : reinterpret_cast<const float*>(source.pBuffer);
        bool silent = true;
        for (UINT32 channel = 0; channel < channels_; ++channel) {
            if (input) for (UINT32 i = 0; i < source.u32ValidFrameCount; ++i) scratch_in_[i] = input[static_cast<size_t>(i) * channels_ + channel];
            processors_[channel]->process(input ? scratch_in_.data() : nullptr, scratch_out_.data(), source.u32ValidFrameCount);
            for (UINT32 i = 0; i < source.u32ValidFrameCount; ++i) {
                output[static_cast<size_t>(i) * channels_ + channel] = scratch_out_[i];
                silent = silent && scratch_out_[i] == 0;
            }
        }
        dest.u32ValidFrameCount = source.u32ValidFrameCount;
        dest.u32BufferFlags = silent ? BUFFER_SILENT : BUFFER_VALID;
    }
private:
    static bool valid_format(IAudioMediaType* media) {
        UNCOMPRESSEDAUDIOFORMAT format{};
        if (!media || FAILED(media->GetUncompressedAudioFormat(&format))) return false;
        return format.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT && format.dwSamplesPerFrame >= 1 && format.dwSamplesPerFrame <= 8 &&
            format.dwBytesPerSampleContainer == 4 && format.dwValidBitsPerSample == 32 && format.fFramesPerSecond == 48000;
    }
    static HRESULT negotiate(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) {
        if (!requested || !supported) return E_POINTER;
        if (!valid_format(requested) || (opposite && !valid_format(opposite))) return APOERR_FORMAT_NOT_SUPPORTED;
        if (opposite) {
            UNCOMPRESSEDAUDIOFORMAT a{}, b{};
            requested->GetUncompressedAudioFormat(&a); opposite->GetUncompressedAudioFormat(&b);
            if (a.dwSamplesPerFrame != b.dwSamplesPerFrame) return APOERR_FORMAT_NOT_SUPPORTED;
        }
        requested->AddRef();
        *supported = requested;
        return S_OK;
    }
    bool m_bIsInitialized = false;
    bool m_bIsLocked = false;
    std::atomic<ULONG> refs_{1};
    std::unique_ptr<nr::RnnoiseBackend> backend_;
    std::vector<std::unique_ptr<nr::Processor>> processors_;
    std::vector<float> scratch_in_, scratch_out_;
    UINT32 channels_ = 1;
    UINT32 max_frames_ = 0;
};

class Factory final : public IClassFactory {
public:
    Factory() { ++objects; }
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
        if (outer) return CLASS_E_NOAGGREGATION;
        try {
            auto instance = new NoiseApo();
            HRESULT status = instance->QueryInterface(id, result);
            instance->Release();
            return status;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) ++locks; else --locks; return S_OK; }
private:
    std::atomic<ULONG> refs_{1};
};
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) { own_module = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
STDAPI DllCanUnloadNow() { return objects == 0 && locks == 0 ? S_OK : S_FALSE; }
STDAPI DllGetClassObject(REFCLSID id, REFIID interface_id, void** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (id != CLSID_NoiseReduction) return CLASS_E_CLASSNOTAVAILABLE;
    try {
        auto factory = new Factory();
        HRESULT status = factory->QueryInterface(interface_id, result);
        factory->Release();
        return status;
    } catch (...) { return E_OUTOFMEMORY; }
}
