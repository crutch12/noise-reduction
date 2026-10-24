#include "control.h"
#include <shlobj.h>
#include <sddl.h>

namespace nr {
ControlView::~ControlView() { stop_watching(); close(); }
void ControlView::close() noexcept {
    if (block_) { VirtualUnlock(block_, sizeof(ControlBlock)); UnmapViewOfFile(block_); block_ = nullptr; }
}
bool ControlView::open_path(const std::wstring& path) {
    stop_watching();
    close();
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    BY_HANDLE_FILE_INFORMATION info{};
    wchar_t final_path[32768]{}, requested_path[32768]{};
    DWORD final_length = GetFinalPathNameByHandleW(file, final_path, 32768, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    DWORD requested_length = GetFullPathNameW(path.c_str(), 32768, requested_path, nullptr);
    bool safe = GetFileInformationByHandle(file, &info) && !(info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) && info.nNumberOfLinks == 1 &&
        final_length > 4 && final_length < 32768 && requested_length > 0 && requested_length < 32768 &&
        CompareStringOrdinal(final_path + 4, -1, requested_path, -1, TRUE) == CSTR_EQUAL;
    if (!safe) { CloseHandle(file); return false; }
    LARGE_INTEGER length{};
    if (!GetFileSizeEx(file, &length) || length.QuadPart != sizeof(ControlBlock)) { CloseHandle(file); return false; }
    HANDLE mapping = CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0, 0, nullptr);
    CloseHandle(file);
    if (!mapping) return false;
    auto block = static_cast<ControlBlock*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(ControlBlock)));
    CloseHandle(mapping);
    if (!block) return false;
    if (block->magic != 0x3143524e || block->size != sizeof(ControlBlock) || !VirtualLock(block, sizeof(ControlBlock))) {
        UnmapViewOfFile(block); return false;
    }
    block_ = block;
    path_ = path;
    InterlockedIncrement64(&block_->initializations);
    return true;
}
void ControlView::error() noexcept { if (block_) InterlockedIncrement64(&block_->errors); }
void ControlView::stop_watching() noexcept {
    if (wait_) { UnregisterWaitEx(wait_, INVALID_HANDLE_VALUE); wait_ = nullptr; }
    if (changed_) { CloseHandle(changed_); changed_ = nullptr; }
    if (!notification_path_.empty()) { DeleteFileW(notification_path_.c_str()); notification_path_.clear(); }
}
void CALLBACK ControlView::on_change(void* opaque, BOOLEAN) {
    auto self = static_cast<ControlView*>(opaque);
    if (self->callback_) self->callback_(self->context_);
}
void ControlView::watch(ChangeCallback callback, void* context) {
    stop_watching();
    if (!block_) return;
    GUID token{};
    if (FAILED(CoCreateGuid(&token))) return;
    wchar_t text[40]{};
    StringFromGUID2(token, text, 40);
    std::wstring key = path_.substr(path_.find_last_of(L"\\/") + 1);
    key.resize(key.size() - 4);
    std::wstring nonce(text + 1, 36);
    for (auto& c : nonce) if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
    key += L"." + nonce;
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;0x00100002;;;AU)(A;;GA;;;SY)(A;;GA;;;BA)", SDDL_REVISION_1, &descriptor, nullptr)) return;
    SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES), descriptor, FALSE};
    std::wstring name = L"Global\\NoiseReduction.Changed." + key;
    changed_ = CreateEventExW(&attributes, name.c_str(), 0, EVENT_MODIFY_STATE | SYNCHRONIZE);
    LocalFree(descriptor);
    if (!changed_) return;
    callback_ = callback; context_ = context;
    if (!RegisterWaitForSingleObject(&wait_, changed_, on_change, this, INFINITE, WT_EXECUTEDEFAULT)) { stop_watching(); return; }
    notification_path_ = path_.substr(0, path_.find_last_of(L"\\/") + 1) + key + L".notify";
    HANDLE file = CreateFileW(notification_path_.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) { stop_watching(); return; }
    CloseHandle(file);
}
bool ControlView::open_endpoint(const std::wstring& text) {
    GUID parsed{};
    if (FAILED(CLSIDFromString(text.c_str(), &parsed))) return false;
    wchar_t canonical[40]{};
    StringFromGUID2(parsed, canonical, 40);
    std::wstring guid(canonical + 1, 36);
    for (auto& c : guid) if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
    PWSTR root = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &root))) return false;
    std::wstring path(root);
    CoTaskMemFree(root);
    return open_path(path + L"\\NoiseReduction\\Endpoints\\" + guid + L".bin");
}
uint32_t ControlView::config() const noexcept {
    return block_ ? static_cast<uint32_t>(InterlockedCompareExchange(&block_->config, 0, 0)) : 0;
}
void ControlView::account(uint32_t frames, uint64_t model_frames) noexcept {
    if (block_) {
        InterlockedAdd64(&block_->audio_frames, frames);
        InterlockedAdd64(&block_->model_frames, static_cast<LONG64>(model_frames));
    }
}
}
