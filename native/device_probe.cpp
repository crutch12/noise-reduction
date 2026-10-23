#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl/client.h>
#include <iostream>
#include <stdexcept>
using Microsoft::WRL::ComPtr;

static void require(HRESULT status) { if (FAILED(status)) throw std::runtime_error("Audio device query failed"); }
static void probe() {
    ComPtr<IMMDeviceEnumerator> enumerator;
    require(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)));
    ComPtr<IMMDeviceCollection> devices;
    require(enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &devices));
    UINT count = 0;
    require(devices->GetCount(&count));
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> device;
        require(devices->Item(i, &device));
        ComPtr<IPropertyStore> properties;
        require(device->OpenPropertyStore(STGM_READ, &properties));
        PROPVARIANT name{};
        require(properties->GetValue(PKEY_Device_FriendlyName, &name));
        if (name.vt == VT_LPWSTR) std::wcout << name.pwszVal << L'\n';
        PropVariantClear(&name);
        ComPtr<IAudioClient> client;
        HRESULT result = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.GetAddressOf()));
        if (FAILED(result)) { std::wcout << L"  Activate failed: " << std::hex << result << L'\n'; continue; }
        WAVEFORMATEX* format = nullptr;
        require(client->GetMixFormat(&format));
        std::wcout << L"  mix rate=" << format->nSamplesPerSec << L" channels=" << format->nChannels
            << L" bits=" << format->wBitsPerSample << L" format=" << format->wFormatTag << L'\n';
        CoTaskMemFree(format);
    }
}
int main() {
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(result)) return 1;
    int status = 0;
    try { probe(); }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; status = 1; }
    CoUninitialize();
    return status;
}
