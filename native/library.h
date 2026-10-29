#pragma once
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace nr {
#ifdef _WIN32
using PathChar = wchar_t;
#else
using PathChar = char;
#endif

// модель грузится только по явному пути, без поиска по системным папкам
class Library {
public:
    explicit Library(const PathChar* path) noexcept {
#ifdef _WIN32
        handle_ = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
#else
        handle_ = dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
    }
    ~Library() { close(); }
    Library(const Library&) = delete;
    Library& operator=(const Library&) = delete;
    bool loaded() const noexcept { return handle_ != nullptr; }
    template <typename T> T symbol(const char* name) const noexcept {
#ifdef _WIN32
#pragma warning(push)
#pragma warning(disable: 4191)
        return reinterpret_cast<T>(GetProcAddress(handle_, name));
#pragma warning(pop)
#else
        return reinterpret_cast<T>(dlsym(handle_, name));
#endif
    }
    void close() noexcept {
        if (!handle_) return;
#ifdef _WIN32
        FreeLibrary(handle_);
#else
        dlclose(handle_);
#endif
        handle_ = nullptr;
    }
private:
#ifdef _WIN32
    HMODULE handle_ = nullptr;
#else
    void* handle_ = nullptr;
#endif
};
}
