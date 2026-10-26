#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mmreg.h>
#include <audiomediatype.h>
#include <audioenginebaseapo.h>
#include <audioengineextensionapo.h>
#include <propsys.h>
#include <wrl/client.h>
#include "apo_id.h"
#include <array>
#include <cmath>
#include <iostream>
#include <fstream>
#include <vector>
#include <chrono>
#include <stdexcept>
using Microsoft::WRL::ComPtr;

static void check(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
struct Outer final : IUnknown {
    ULONG references = 1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (id != __uuidof(IUnknown)) return E_NOINTERFACE;
        *value = static_cast<IUnknown*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { return --references; }
};

int wmain(int argc, wchar_t** argv) {
    try {
        check(argc == 2 || argc == 4, "Usage: apo_test DLL [input-float32.raw output-float32.raw]");
        check(SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)), "COM initialization failed");
        HMODULE module = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        check(module != nullptr, "APO DLL load failed");
#pragma warning(push)
#pragma warning(disable: 4191)
        auto factory_fn = reinterpret_cast<HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**)>(GetProcAddress(module, "DllGetClassObject"));
        auto can_unload = reinterpret_cast<HRESULT (STDAPICALLTYPE*)()>(GetProcAddress(module, "DllCanUnloadNow"));
#pragma warning(pop)
        check(factory_fn && can_unload, "Missing COM exports");
        {
            ComPtr<IClassFactory> factory;
            check(SUCCEEDED(factory_fn(CLSID_NoiseReduction, IID_PPV_ARGS(&factory))), "Factory creation failed");
            {
                Outer outer;
                ComPtr<IUnknown> inner;
                check(SUCCEEDED(factory->CreateInstance(&outer, IID_PPV_ARGS(&inner))), "Composite APO aggregation is not supported");
                ComPtr<IAudioProcessingObject> aggregated;
                check(SUCCEEDED(inner.As(&aggregated)), "Aggregated APO interface missing");
                ComPtr<IUnknown> identity;
                check(SUCCEEDED(aggregated.As(&identity)) && identity.Get() == static_cast<IUnknown*>(&outer), "Aggregated IUnknown identity is broken");
                identity.Reset(); aggregated.Reset(); inner.Reset();
                check(outer.references == 1, "Aggregated outer references leaked");
            }
            ComPtr<IAudioProcessingObject> apo;
            check(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&apo))), "APO creation failed");
            check(can_unload() == S_FALSE, "DLL can unload while objects live");
            ComPtr<IAudioProcessingObjectRT> rt;
            ComPtr<IAudioProcessingObjectConfiguration> config;
            check(SUCCEEDED(apo.As(&rt)) && SUCCEEDED(apo.As(&config)), "Missing required interfaces");
            ComPtr<IAudioSystemEffects3> effects;
            check(SUCCEEDED(apo.As(&effects)), "Missing Windows 11 controllable effects");
            AUDIO_SYSTEMEFFECT* effect_list = nullptr;
            UINT effect_count = 0;
            check(SUCCEEDED(effects->GetControllableSystemEffectsList(&effect_list, &effect_count, nullptr)) && effect_count == 1, "Missing controllable noise suppression");
            GUID effect_id = effect_list[0].id;
            CoTaskMemFree(effect_list);
            check(SUCCEEDED(effects->SetAudioSystemEffectState(effect_id, AUDIO_SYSTEMEFFECT_STATE_OFF)), "Cannot disable system effect");
            GUID* active = nullptr;
            UINT active_count = 99;
            check(SUCCEEDED(effects->GetEffectsList(&active, &active_count, nullptr)) && active_count == 0 && active == nullptr, "Disabled effect reported as active");
            check(SUCCEEDED(effects->SetAudioSystemEffectState(effect_id, AUDIO_SYSTEMEFFECT_STATE_ON)), "Cannot reenable system effect");
            {
                ComPtr<IAudioProcessingObject> windows_apo;
                check(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&windows_apo))), "Cannot create Windows APO");
                ComPtr<IPropertyStore> store;
                check(SUCCEEDED(PSCreateMemoryPropertyStore(IID_PPV_ARGS(&store))), "Cannot create endpoint property store");
                APOInitSystemEffects3 init{};
                init.APOInit.cbSize = sizeof(init); init.APOInit.clsid = CLSID_NoiseReduction;
                init.pAPOEndpointProperties = store.Get(); init.InitializeForDiscoveryOnly = TRUE;
                check(SUCCEEDED(windows_apo->Initialize(sizeof(init), reinterpret_cast<BYTE*>(&init))), "Windows discovery initialization failed");
            }
            check(SUCCEEDED(apo->Initialize(0, nullptr)), "Initialize failed");
            check(apo->Initialize(0, nullptr) == APOERR_ALREADY_INITIALIZED, "Repeated initialize accepted");
            HNSTIME latency = 0;
            check(SUCCEEDED(apo->GetLatency(&latency)) && latency == 300000, "Wrong fixed latency");
            WAVEFORMATEX wave{WAVE_FORMAT_IEEE_FLOAT, 1, 48000, 192000, 4, 32, 0};
            ComPtr<IAudioMediaType> media;
            check(SUCCEEDED(CreateAudioMediaType(&wave, sizeof(wave), &media)), "Media type creation failed");
            ComPtr<IAudioMediaType> supported;
            check(apo->IsInputFormatSupported(nullptr, media.Get(), &supported) == S_OK, "Mono float48k rejected");
            {
                WAVEFORMATEX pcm{WAVE_FORMAT_PCM, 4, 48000, 384000, 8, 16, 0};
                WAVEFORMATEX float_wave{WAVE_FORMAT_IEEE_FLOAT, 4, 48000, 768000, 16, 32, 0};
                ComPtr<IAudioMediaType> hardware, float_media;
                check(SUCCEEDED(CreateAudioMediaType(&pcm, sizeof(pcm), &hardware)) &&
                    SUCCEEDED(CreateAudioMediaType(&float_wave, sizeof(float_wave), &float_media)), "Hardware format fixture failed");
                ComPtr<IAudioMediaType> alternative;
                check(apo->IsInputFormatSupported(float_media.Get(), hardware.Get(), &alternative) == S_FALSE && alternative,
                    "PCM16 hardware format must suggest float32 instead of failing graph negotiation");
                UNCOMPRESSEDAUDIOFORMAT format{};
                check(SUCCEEDED(alternative->GetUncompressedAudioFormat(&format)) && format.dwSamplesPerFrame == 4 &&
                    format.dwBytesPerSampleContainer == 4 && format.dwValidBitsPerSample == 32 && format.fFramesPerSecond == 48000,
                    "Suggested hardware format is not four-channel float48k");
                ComPtr<IAudioMediaType> accepted;
                check(apo->IsInputFormatSupported(float_media.Get(), alternative.Get(), &accepted) == S_OK,
                    "Suggested format cannot complete negotiation");
                alternative.Reset();
                check(apo->IsOutputFormatSupported(float_media.Get(), hardware.Get(), &alternative) == S_FALSE && alternative,
                    "Output negotiation must also suggest float32");
                alternative.Reset();
                check(apo->IsInputFormatSupported(nullptr, hardware.Get(), &alternative) == S_FALSE && alternative,
                    "PCM format without opposite must suggest float32");
            }
            wave.nSamplesPerSec = 96000; wave.nAvgBytesPerSec = 384000;
            ComPtr<IAudioMediaType> unsupported;
            check(SUCCEEDED(CreateAudioMediaType(&wave, sizeof(wave), &unsupported)), "Negative media creation failed");
            ComPtr<IAudioMediaType> suggested;
            check(apo->IsInputFormatSupported(nullptr, unsupported.Get(), &suggested) != S_OK, "96000 accepted");
            wave.nSamplesPerSec = 48000; wave.nAvgBytesPerSec = 192000;
            std::array<float, 480> input{}, output{};
            APO_CONNECTION_DESCRIPTOR in{APO_CONNECTION_BUFFER_TYPE_EXTERNAL, reinterpret_cast<UINT_PTR>(input.data()), 480, media.Get(), APO_CONNECTION_DESCRIPTOR_SIGNATURE};
            APO_CONNECTION_DESCRIPTOR out{APO_CONNECTION_BUFFER_TYPE_EXTERNAL, reinterpret_cast<UINT_PTR>(output.data()), 480, media.Get(), APO_CONNECTION_DESCRIPTOR_SIGNATURE};
            auto pin = &in; auto pout = &out;
            HRESULT lock_result = config->LockForProcess(1, &pin, 1, &pout);
            if (FAILED(lock_result)) std::cerr << "Lock HRESULT: " << std::hex << lock_result << '\n';
            check(SUCCEEDED(lock_result), "Lock failed");
            check(config->LockForProcess(1, &pin, 1, &pout) == APOERR_APO_LOCKED, "Repeated lock accepted");
            APO_CONNECTION_PROPERTY ip{reinterpret_cast<UINT_PTR>(input.data()), 480, BUFFER_SILENT, APO_CONNECTION_PROPERTY_SIGNATURE};
            APO_CONNECTION_PROPERTY op{reinterpret_cast<UINT_PTR>(output.data()), 0, BUFFER_INVALID, APO_CONNECTION_PROPERTY_SIGNATURE};
            auto pip = &ip; auto pop = &op;
            // A silent buffer may contain nonzero garbage, it must not be consumed
            input.fill(0.9f);
            for (int frame = 0; frame < 10; ++frame) {
                rt->APOProcess(1, &pip, 1, &pop);
                check(op.u32ValidFrameCount == 480 && op.u32BufferFlags == BUFFER_SILENT, "Silence contract violated");
                for (float value : output) check(value == 0, "SILENT input bytes were read");
            }
            ip.u32BufferFlags = BUFFER_VALID;
            for (int frame = 0; frame < 10; ++frame) rt->APOProcess(1, &pip, 1, &pop);
            ip.u32BufferFlags = BUFFER_SILENT;
            rt->APOProcess(1, &pip, 1, &pop);
            check(op.u32BufferFlags == BUFFER_VALID, "Queued tail lost on silent input");
            check(SUCCEEDED(apo->Reset()), "Reset while connected failed");
            for (int frame = 0; frame < 10; ++frame) {
                rt->APOProcess(1, &pip, 1, &pop);
                check(op.u32BufferFlags == BUFFER_SILENT && op.u32ValidFrameCount == 480, "Reset leaked old tail or lost connections");
            }
            output.fill(0.7f);
            ip.u32ValidFrameCount = 481;
            rt->APOProcess(1, &pip, 1, &pop);
            check(op.u32BufferFlags == BUFFER_INVALID && op.u32ValidFrameCount == 0, "Oversized buffer accepted");
            for (float value : output) check(value == 0.7f, "Oversized buffer modified memory");
            check(SUCCEEDED(config->UnlockForProcess()), "Unlock failed");
            check(config->UnlockForProcess() == APOERR_ALREADY_UNLOCKED, "Repeated unlock accepted");
            if (argc == 4) {
                std::ifstream file(argv[2], std::ios::binary | std::ios::ate);
                check(file.good(), "Cannot open raw input");
                auto bytes = file.tellg();
                check(bytes > 0 && bytes % sizeof(float) == 0, "Invalid raw float input");
                std::vector<float> samples(static_cast<size_t>(bytes) / sizeof(float));
                file.seekg(0);
                file.read(reinterpret_cast<char*>(samples.data()), bytes);
                check(file.good(), "Cannot read raw input");
                size_t original_size = samples.size();
                samples.resize(((original_size + 1440 + 479) / 480) * 480, 0.0f);
                std::vector<float> rendered(samples.size());
                check(SUCCEEDED(config->LockForProcess(1, &pin, 1, &pout)), "Render relock failed");
                ip.u32BufferFlags = BUFFER_VALID;
                ip.u32ValidFrameCount = 480;
                auto started = std::chrono::steady_clock::now();
                for (size_t at = 0; at < samples.size(); at += 480) {
                    std::copy_n(samples.data() + at, 480, input.data());
                    rt->APOProcess(1, &pip, 1, &pop);
                    check(op.u32ValidFrameCount == 480 && op.u32BufferFlags != BUFFER_INVALID, "Render failed");
                    std::copy_n(output.data(), 480, rendered.data() + at);
                }
                double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
                check(SUCCEEDED(config->UnlockForProcess()), "Render unlock failed");
                std::ofstream saved(argv[3], std::ios::binary);
                saved.write(reinterpret_cast<char*>(rendered.data() + 1440), static_cast<std::streamsize>(original_size * sizeof(float)));
                check(saved.good(), "Cannot write rendered audio");
                std::cout << "APO rendered " << original_size << " samples in " << elapsed << " seconds\n";
            }
            for (WORD channels : std::array<WORD, 3>{2, 4, 8}) {
                wave.nChannels = channels;
                wave.nSamplesPerSec = 48000;
                wave.nBlockAlign = channels * 4;
                wave.nAvgBytesPerSec = wave.nSamplesPerSec * wave.nBlockAlign;
                ComPtr<IAudioMediaType> multichannel;
                check(SUCCEEDED(CreateAudioMediaType(&wave, sizeof(wave), &multichannel)), "Multichannel media failed");
                std::vector<float> multi_in(480 * channels), multi_out(480 * channels);
                for (size_t i = 0; i < 480; ++i) multi_in[i * channels] = 0.1f * std::sin(float(i) * 0.03f);
                in.pBuffer = reinterpret_cast<UINT_PTR>(multi_in.data());
                out.pBuffer = reinterpret_cast<UINT_PTR>(multi_out.data());
                in.pFormat = out.pFormat = multichannel.Get();
                check(SUCCEEDED(config->LockForProcess(1, &pin, 1, &pout)), "Multichannel lock failed");
                ip.pBuffer = in.pBuffer; op.pBuffer = out.pBuffer;
                ip.u32BufferFlags = BUFFER_VALID; ip.u32ValidFrameCount = 480;
                for (int frame = 0; frame < 12; ++frame) rt->APOProcess(1, &pip, 1, &pop);
                check(op.u32ValidFrameCount == 480 && op.u32BufferFlags == BUFFER_VALID, "Multichannel output lost");
                bool sound = false;
                for (size_t i = 0; i < 480; ++i) {
                    sound = sound || std::abs(multi_out[i * channels]) > 0.001f;
                    for (WORD channel = 1; channel < channels; ++channel)
                        check(multi_out[i * channels + channel] == 0, "Audio leaks between channels");
                }
                check(sound, "First channel was erased");
                check(SUCCEEDED(config->UnlockForProcess()), "Multichannel unlock failed");
            }
            // Bluetooth гарнитура: 16 кГц моно; при выключенном эффекте звук проходит пересчёт частоты без потерь
            {
                ComPtr<IAudioSystemEffects3> system_effects;
                check(SUCCEEDED(apo.As(&system_effects)), "No system effects interface");
                check(SUCCEEDED(system_effects->SetAudioSystemEffectState(AUDIO_EFFECT_TYPE_NOISE_SUPPRESSION, AUDIO_SYSTEMEFFECT_STATE_OFF)), "Effect state failed");
                WAVEFORMATEX narrow{WAVE_FORMAT_IEEE_FLOAT, 1, 16000, 64000, 4, 32, 0};
                ComPtr<IAudioMediaType> narrow_media;
                check(SUCCEEDED(CreateAudioMediaType(&narrow, sizeof(narrow), &narrow_media)), "16 kHz media failed");
                ComPtr<IAudioMediaType> narrow_supported;
                check(apo->IsInputFormatSupported(nullptr, narrow_media.Get(), &narrow_supported) == S_OK, "16 kHz rejected");
                std::vector<float> narrow_in(160), narrow_out(160);
                in.pBuffer = reinterpret_cast<UINT_PTR>(narrow_in.data()); out.pBuffer = reinterpret_cast<UINT_PTR>(narrow_out.data());
                in.pFormat = out.pFormat = narrow_media.Get(); in.u32MaxFrameCount = out.u32MaxFrameCount = 160;
                check(SUCCEEDED(config->LockForProcess(1, &pin, 1, &pout)), "16 kHz lock failed");
                ip.pBuffer = in.pBuffer; op.pBuffer = out.pBuffer; ip.u32BufferFlags = BUFFER_VALID; ip.u32ValidFrameCount = 160;
                double in_power = 0, out_power = 0;
                for (int frame = 0; frame < 100; ++frame) {
                    for (size_t i = 0; i < 160; ++i) narrow_in[i] = 0.2f * std::sin(2 * 3.14159265f * 300.0f * float(frame * 160 + i) / 16000.0f);
                    rt->APOProcess(1, &pip, 1, &pop);
                    check(op.u32ValidFrameCount == 160 && op.u32BufferFlags != BUFFER_INVALID, "16 kHz output lost");
                    if (frame >= 50) for (size_t i = 0; i < 160; ++i) { in_power += narrow_in[i] * narrow_in[i]; out_power += narrow_out[i] * narrow_out[i]; }
                }
                check(std::abs(10 * std::log10(out_power / in_power)) < 0.5, "16 kHz audio level changed");
                check(SUCCEEDED(config->UnlockForProcess()), "16 kHz unlock failed");
            }
        }
        check(can_unload() == S_OK, "COM references leaked");
        FreeLibrary(module);
        CoUninitialize();
        std::cout << "PASS: COM lifecycle, format negotiation, latency, lock, silence, tail, oversize, unload, 16 kHz\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
