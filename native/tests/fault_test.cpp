#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mmreg.h>
#include <audiomediatype.h>
#include <audioenginebaseapo.h>
#include <wrl/client.h>
#include "apo_id.h"
#include <array>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>
using Microsoft::WRL::ComPtr;

static void check(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }

// эффект собран с NR_FAULT_TEST: сбой при запуске потока или падение посреди обработки
// микрофон при этом должен работать, звук идёт как есть
int wmain(int argc, wchar_t** argv) {
    try {
        check(argc == 3, "Usage: fault_test DLL lock|process");
        const std::wstring mode = argv[2];
        check(mode == L"lock" || mode == L"process", "Unknown fault");
        SetEnvironmentVariableW(L"NR_FAULT", mode.c_str());
        check(SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)), "COM initialization failed");
        HMODULE module = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        check(module != nullptr, "APO DLL load failed");
#pragma warning(push)
#pragma warning(disable: 4191)
        auto factory_fn = reinterpret_cast<HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**)>(GetProcAddress(module, "DllGetClassObject"));
#pragma warning(pop)
        check(factory_fn != nullptr, "Missing COM exports");
        {
            ComPtr<IClassFactory> factory;
            check(SUCCEEDED(factory_fn(CLSID_NoiseReduction, IID_PPV_ARGS(&factory))), "Factory creation failed");
            ComPtr<IAudioProcessingObject> apo;
            check(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&apo))), "APO creation failed");
            ComPtr<IAudioProcessingObjectRT> rt;
            ComPtr<IAudioProcessingObjectConfiguration> config;
            check(SUCCEEDED(apo.As(&rt)) && SUCCEEDED(apo.As(&config)), "Missing required interfaces");
            check(SUCCEEDED(apo->Initialize(0, nullptr)), "Initialize failed");
            WAVEFORMATEX wave{WAVE_FORMAT_IEEE_FLOAT, 1, 48000, 192000, 4, 32, 0};
            ComPtr<IAudioMediaType> media;
            check(SUCCEEDED(CreateAudioMediaType(&wave, sizeof(wave), &media)), "Media type creation failed");
            std::array<float, 480> input{}, output{};
            APO_CONNECTION_DESCRIPTOR in{APO_CONNECTION_BUFFER_TYPE_EXTERNAL, reinterpret_cast<UINT_PTR>(input.data()), 480, media.Get(), APO_CONNECTION_DESCRIPTOR_SIGNATURE};
            APO_CONNECTION_DESCRIPTOR out{APO_CONNECTION_BUFFER_TYPE_EXTERNAL, reinterpret_cast<UINT_PTR>(output.data()), 480, media.Get(), APO_CONNECTION_DESCRIPTOR_SIGNATURE};
            auto pin = &in; auto pout = &out;
            check(SUCCEEDED(config->LockForProcess(1, &pin, 1, &pout)), "Failed effect must not break the stream");
            APO_CONNECTION_PROPERTY ip{reinterpret_cast<UINT_PTR>(input.data()), 480, BUFFER_VALID, APO_CONNECTION_PROPERTY_SIGNATURE};
            APO_CONNECTION_PROPERTY op{reinterpret_cast<UINT_PTR>(output.data()), 0, BUFFER_INVALID, APO_CONNECTION_PROPERTY_SIGNATURE};
            auto pip = &ip; auto pop = &op;
            for (int frame = 0; frame < 30; ++frame) {
                for (size_t i = 0; i < input.size(); ++i) input[i] = 0.3f * std::sin(0.05f * static_cast<float>(frame * 480 + i));
                rt->APOProcess(1, &pip, 1, &pop);
                // в режиме process первые кадры ещё обрабатываются моделью
                if (mode == L"process" && frame < 6) continue;
                check(op.u32BufferFlags == BUFFER_VALID && op.u32ValidFrameCount == 480, "Broken effect dropped the audio");
                check(output == input, "Broken effect must pass audio unchanged");
            }
            HNSTIME latency = 1;
            check(SUCCEEDED(apo->GetLatency(&latency)) && latency == 0, "Bypass must not report model latency");
            output.fill(0.5f);
            ip.u32BufferFlags = BUFFER_SILENT;
            rt->APOProcess(1, &pip, 1, &pop);
            check(op.u32BufferFlags == BUFFER_SILENT && op.u32ValidFrameCount == 480, "Silence lost in bypass");
            for (float value : output) check(value == 0, "Silent bypass left garbage");
            check(SUCCEEDED(apo->Reset()), "Reset failed in bypass");
            check(SUCCEEDED(config->UnlockForProcess()), "Unlock failed in bypass");
        }
        std::wcout << L"PASS: " << mode << L" fault keeps the microphone working\n";
        return 0;
    }
    catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}
