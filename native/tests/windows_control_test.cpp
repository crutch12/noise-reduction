#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <audiomediatype.h>
#include <audioengineextensionapo.h>
#include <propsys.h>
#include <propvarutil.h>
#include <wrl/client.h>
#include "apo_id.h"
#include "control.h"
#include <array>
#include <cmath>
#include <iostream>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
static void check(bool ok, const char* text) { if (!ok) throw std::runtime_error(text); }
static constexpr PROPERTYKEY endpoint_key{{0x1da5d803,0xd492,0x4edd,{0x8c,0x23,0xe0,0xc0,0xff,0xee,0x7f,0x0e}},4};

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    std::wstring file_path;
    HANDLE file = INVALID_HANDLE_VALUE, mapping = nullptr;
    nr::ControlBlock* block = nullptr;
    HMODULE module = nullptr;
    HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(com)) return 1;
    int code = 0;
    try {
        GUID fixture{}; CoCreateGuid(&fixture);
        wchar_t guid[40]{}; StringFromGUID2(fixture, guid, 40);
        std::wstring key(guid + 1, 36);
        for (auto& c : key) if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
        PWSTR root = nullptr;
        check(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &root)), "ProgramData unavailable");
        std::wstring folder(root); CoTaskMemFree(root);
        folder += L"\\NoiseReduction"; CreateDirectoryW(folder.c_str(), nullptr);
        folder += L"\\Endpoints"; CreateDirectoryW(folder.c_str(), nullptr);
        file_path = folder + L"\\" + key + L".bin";
        file = CreateFileW(file_path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        check(file != INVALID_HANDLE_VALUE, "Cannot create isolated endpoint control fixture");
        nr::ControlBlock initial{}; initial.magic = 0x3143524e; initial.size = 64; initial.config = 1;
        DWORD written = 0;
        check(WriteFile(file, &initial, 64, &written, nullptr) && written == 64, "Control fixture write failed");
        mapping = CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0, 64, nullptr);
        check(mapping != nullptr, "Cannot map fixture");
        block = static_cast<nr::ControlBlock*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 64));
        check(block != nullptr, "Cannot view fixture");
        module = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        check(module != nullptr, "Cannot load APO");
#pragma warning(push)
#pragma warning(disable: 4191)
        auto create = reinterpret_cast<HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**)>(GetProcAddress(module, "DllGetClassObject"));
#pragma warning(pop)
        check(create != nullptr, "No class factory");
        {
            ComPtr<IClassFactory> factory;
            check(SUCCEEDED(create(CLSID_NoiseReduction, IID_PPV_ARGS(&factory))), "Factory failed");
            ComPtr<IAudioProcessingObject> apo;
            check(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&apo))), "Create APO failed");
            ComPtr<IPropertyStore> store;
            check(SUCCEEDED(PSCreateMemoryPropertyStore(IID_PPV_ARGS(&store))), "Property store failed");
            PROPVARIANT endpoint{};
            check(SUCCEEDED(InitPropVariantFromString(guid, &endpoint)), "Property value failed");
            check(SUCCEEDED(store->SetValue(endpoint_key, endpoint)), "Endpoint GUID failed");
            PropVariantClear(&endpoint);
            APOInitSystemEffects2 init{};
            init.APOInit.cbSize = sizeof(init); init.APOInit.clsid = CLSID_NoiseReduction; init.pAPOEndpointProperties = store.Get();
            check(SUCCEEDED(apo->Initialize(sizeof(init), reinterpret_cast<BYTE*>(&init))), "Real Windows initialization failed");
            check(block->initializations == 1, "Endpoint control was not associated");
            ComPtr<IAudioSystemEffects3> effects; ComPtr<IAudioProcessingObjectConfiguration> configuration; ComPtr<IAudioProcessingObjectRT> rt;
            check(SUCCEEDED(apo.As(&effects)) && SUCCEEDED(apo.As(&configuration)) && SUCCEEDED(apo.As(&rt)), "Interfaces missing");
            HANDLE changed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            check(changed != nullptr, "Effects notification fixture failed");
            GUID* list = nullptr; UINT count = 0;
            check(SUCCEEDED(effects->GetEffectsList(&list, &count, changed)) && count == 1, "Enabled effect missing"); CoTaskMemFree(list);
            WIN32_FIND_DATAW found{};
            HANDLE search = FindFirstFileW((folder + L"\\" + key + L".*.notify").c_str(), &found);
            check(search != INVALID_HANDLE_VALUE, "No Windows effect notification descriptor"); FindClose(search);
            std::wstring marker(found.cFileName); marker.resize(marker.size() - 7);
            HANDLE signal = OpenEventW(EVENT_MODIFY_STATE, FALSE, (L"Global\\NoiseReduction.Changed." + marker).c_str());
            check(signal != nullptr, "Cannot open effect notification");
            InterlockedExchange(&block->config, 0); SetEvent(signal);
            check(WaitForSingleObject(changed, 3000) == WAIT_OBJECT_0, "Windows was not notified of disabled processing");
            check(SUCCEEDED(effects->GetEffectsList(&list, &count, changed)) && count == 0 && list == nullptr, "Disabled effect still advertised");
            InterlockedExchange(&block->config, 1); SetEvent(signal);
            check(WaitForSingleObject(changed, 3000) == WAIT_OBJECT_0, "Windows was not notified of enabled processing");
            WAVEFORMATEX wave{WAVE_FORMAT_IEEE_FLOAT,1,48000,192000,4,32,0};
            ComPtr<IAudioMediaType> media; check(SUCCEEDED(CreateAudioMediaType(&wave, sizeof(wave), &media)), "Format failed");
            std::array<float,480> input{}, output{};
            for (size_t i = 0; i < 480; ++i) input[i] = std::sin(static_cast<float>(i) * 0.03f) * 0.1f;
            APO_CONNECTION_DESCRIPTOR in{APO_CONNECTION_BUFFER_TYPE_EXTERNAL,reinterpret_cast<UINT_PTR>(input.data()),480,media.Get(),APO_CONNECTION_DESCRIPTOR_SIGNATURE};
            APO_CONNECTION_DESCRIPTOR out{APO_CONNECTION_BUFFER_TYPE_EXTERNAL,reinterpret_cast<UINT_PTR>(output.data()),480,media.Get(),APO_CONNECTION_DESCRIPTOR_SIGNATURE};
            auto pin = &in, pout = &out;
            APO_CONNECTION_PROPERTY ip{in.pBuffer,480,BUFFER_VALID,APO_CONNECTION_PROPERTY_SIGNATURE};
            APO_CONNECTION_PROPERTY op{out.pBuffer,0,BUFFER_INVALID,APO_CONNECTION_PROPERTY_SIGNATURE};
            auto pip = &ip, pop = &op;
            for (int cycle = 0; cycle < 3; ++cycle) {
                if (cycle != 1) check(SUCCEEDED(configuration->LockForProcess(1,&pin,1,&pout)), "Lock failed");
                else check(SUCCEEDED(apo->Reset()), "Reset failed");
                for (int frame = 0; frame < 12; ++frame) rt->APOProcess(1,&pip,1,&pop);
                for (size_t i = 0; i < 480; ++i) check(std::abs(output[i] - input[i]) < 1e-6f, "Configured zero attenuation lost after Reset/relock");
                if (cycle != 0) check(SUCCEEDED(configuration->UnlockForProcess()), "Unlock failed");
            }
            check(block->audio_frames > 0 && block->model_frames > 0, "Actual processing telemetry missing");
            InterlockedExchange(&block->config, 0);
            const LONG64 before_bypass = block->model_frames;
            check(SUCCEEDED(configuration->LockForProcess(1,&pin,1,&pout)), "Disabled graph lock failed");
            for (int frame = 0; frame < 6; ++frame) rt->APOProcess(1,&pip,1,&pop);
            check(block->model_frames == before_bypass, "New disabled graph performed model inference during startup fade");
            check(SUCCEEDED(apo->Reset()), "Disabled graph reset failed");
            for (int frame = 0; frame < 6; ++frame) rt->APOProcess(1,&pip,1,&pop);
            check(block->model_frames == before_bypass, "Reset disabled graph performed model inference");
            check(SUCCEEDED(configuration->UnlockForProcess()), "Disabled graph unlock failed");
            // модель меняется на ходу: звук идёт, DeepFilterNet собирается в фоне и подхватывается
            InterlockedExchange(&block->config, 1 | 8 | (24 << 8));
            check(SUCCEEDED(configuration->LockForProcess(1,&pin,1,&pout)), "Model switch lock failed");
            check(block->active_model == 1, "Locked graph ignored the selected DeepFilterNet model");
            InterlockedExchange(&block->config, 1 | (24 << 8)); SetEvent(signal);
            bool switched = false;
            for (int wait = 0; wait < 300 && !switched; ++wait) { Sleep(10); rt->APOProcess(1,&pip,1,&pop); switched = block->active_model == 0; }
            check(switched, "RNNoise was not picked up while processing");
            InterlockedExchange(&block->config, 1 | 8 | (24 << 8)); SetEvent(signal);
            switched = false;
            for (int wait = 0; wait < 300 && !switched; ++wait) { Sleep(10); rt->APOProcess(1,&pip,1,&pop); switched = block->active_model == 1; }
            check(switched, "DeepFilterNet was not picked up while processing");
            const LONG64 before_deepfilter = block->model_frames;
            for (int frame = 0; frame < 12; ++frame) rt->APOProcess(1,&pip,1,&pop);
            check(block->model_frames > before_deepfilter, "DeepFilterNet did not process audio");
            for (size_t i = 0; i < 480; ++i) check(std::isfinite(output[i]) && std::abs(output[i]) <= 1.0f, "DeepFilterNet output is not finite");
            check(SUCCEEDED(configuration->UnlockForProcess()), "Model switch unlock failed");
            CloseHandle(signal); CloseHandle(changed);
        }
        std::cout << "PASS: Windows endpoint initialization, control association, notifications, telemetry, strength after Reset/relock, live model switch\n";
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; code = 1; }
    if (module) FreeLibrary(module);
    if (block) UnmapViewOfFile(block);
    if (mapping) CloseHandle(mapping);
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (!file_path.empty()) DeleteFileW(file_path.c_str());
    CoUninitialize();
    return code;
}
