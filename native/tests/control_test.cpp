#include "control.h"
#include <iostream>
#include <stdexcept>

static void check(bool ok, const char* text) { if (!ok) throw std::runtime_error(text); }
int main() {
    wchar_t folder[MAX_PATH], path[MAX_PATH];
    GetTempPathW(MAX_PATH, folder);
    GetTempFileNameW(folder, L"nrc", 0, path);
    try {
        HANDLE file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        check(file != INVALID_HANDLE_VALUE, "Cannot create control fixture");
        nr::ControlBlock initial{};
        initial.magic = 0x3143524e; initial.size = 64; initial.config = 1 | (24 << 8);
        DWORD written = 0;
        check(WriteFile(file, &initial, sizeof(initial), &written, nullptr) && written == 64, "Cannot initialize fixture");
        {
            nr::ControlView view;
            check(view.open_path(path), "Cannot open control mapping");
            check(view.config() == (1u | (24u << 8)), "Configuration changed");
            view.account(480, 1);
            HANDLE notified = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            check(notified != nullptr, "Cannot create callback fixture");
            view.watch([](void* context) noexcept { SetEvent(static_cast<HANDLE>(context)); }, notified);
            std::wstring pattern(path);
            pattern.resize(pattern.size() - 4);
            pattern += L".*.notify";
            WIN32_FIND_DATAW found{};
            HANDLE search = FindFirstFileW(pattern.c_str(), &found);
            check(search != INVALID_HANDLE_VALUE, "Notification descriptor is missing");
            FindClose(search);
            std::wstring key(found.cFileName);
            key.resize(key.size() - 7);
            std::wstring event_name = L"Global\\NoiseReduction.Changed." + key;
            HANDLE change = OpenEventW(EVENT_MODIFY_STATE, FALSE, event_name.c_str());
            check(change != nullptr, "Cannot open shared notification event");
            SetEvent(change);
            check(WaitForSingleObject(notified, 3000) == WAIT_OBJECT_0, "Notification callback was not delivered");
            CloseHandle(change);
            view.stop_watching();
            CloseHandle(notified);
            initial.config = 0;
            SetFilePointer(file, 8, nullptr, FILE_BEGIN);
            check(WriteFile(file, const_cast<const LONG*>(&initial.config), 4, &written, nullptr), "Cannot update config");
            check(view.config() == 0, "Mapped config does not update live");
        }
        SetFilePointer(file, 0, nullptr, FILE_BEGIN);
        DWORD read = 0;
        check(ReadFile(file, &initial, 64, &read, nullptr) && read == 64, "Cannot read counters");
        check(initial.audio_frames == 480 && initial.model_frames == 1 && initial.initializations == 1, "Counters were not preserved");
        initial.magic = 0;
        SetFilePointer(file, 0, nullptr, FILE_BEGIN);
        check(WriteFile(file, &initial, 64, &written, nullptr), "Cannot corrupt fixture");
        nr::ControlView bad;
        check(!bad.open_path(path) && bad.config() == 0, "Corrupt control was enabled");
        initial.magic = 0x3143524e;
        SetFilePointer(file, 0, nullptr, FILE_BEGIN);
        check(WriteFile(file, &initial, 64, &written, nullptr), "Cannot restore fixture");
        CloseHandle(file);
        std::wstring hard = std::wstring(path) + L".hard";
        if (CreateHardLinkW(hard.c_str(), path, nullptr)) {
            nr::ControlView linked;
            check(!linked.open_path(hard), "Hard-linked control was accepted");
            check(DeleteFileW(hard.c_str()) != FALSE, "Cannot remove hard-link fixture");
        }
        std::wstring symbolic = std::wstring(path) + L".symlink";
        if (CreateSymbolicLinkW(symbolic.c_str(), path, 2) || CreateSymbolicLinkW(symbolic.c_str(), path, 0)) {
            nr::ControlView redirected;
            check(!redirected.open_path(symbolic), "Symbolic control was accepted");
            check(DeleteFileW(symbolic.c_str()) != FALSE, "Cannot remove symlink fixture");
        }
        DeleteFileW(path);
        std::cout << "PASS: mapped control, live config, counters, invalid mapping bypass\n";
        return 0;
    } catch (const std::exception& error) { DeleteFileW(path); std::cerr << error.what() << '\n'; return 1; }
}
