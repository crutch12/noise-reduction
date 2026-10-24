#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <wrl/client.h>
#include <iostream>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
static void check(HRESULT result, const char* message) {
    if (FAILED(result)) { std::cerr << message << " HRESULT=" << std::hex << result << '\n'; throw std::runtime_error(message); }
}
static void exercise(const wchar_t* id) {
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)), "Create enumerator");
    ComPtr<IMMDevice> device;
    check(enumerator->GetDevice(id, &device), "Open capture endpoint");
    ComPtr<IAudioClient> client;
    check(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.GetAddressOf())), "Activate capture");
    WAVEFORMATEX* format = nullptr;
    check(client->GetMixFormat(&format), "Get capture format");
    HRESULT init = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_NOPERSIST, 1000000, 0, format, nullptr);
    CoTaskMemFree(format);
    check(init, "Initialize shared capture");
    ComPtr<IAudioCaptureClient> capture;
    check(client->GetService(IID_PPV_ARGS(&capture)), "Get capture service");
    check(client->Start(), "Start capture");
    uint64_t total = 0;
    for (unsigned tick = 0; tick < 300; ++tick) {
        Sleep(10);
        UINT32 frames = 0;
        check(capture->GetNextPacketSize(&frames), "Get packet size");
        while (frames) {
            BYTE* data = nullptr; DWORD flags = 0;
            check(capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr), "Get capture buffer");
            total += frames;
            // Exercise the graph without inspecting or storing microphone samples
            check(capture->ReleaseBuffer(frames), "Release capture buffer");
            check(capture->GetNextPacketSize(&frames), "Get next packet size");
        }
    }
    check(client->Stop(), "Stop capture");
    if (!total) throw std::runtime_error("No capture frames delivered");
    std::cout << "PASS: shared capture graph delivered " << std::dec << total << " frames, samples discarded\n";
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(init)) return 1;
    int code = 0;
    try { exercise(argv[1]); } catch (const std::exception& error) { std::cerr << error.what() << '\n'; code = 1; }
    CoUninitialize();
    return code;
}
