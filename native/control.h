#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <string>

namespace nr {
struct alignas(8) ControlBlock {
    uint32_t magic;
    uint32_t size;
    volatile LONG config;
    // модель, которая сейчас обрабатывает звук: 0 RNNoise, 1 DeepFilterNet
    volatile LONG active_model;
    volatile LONG64 audio_frames;
    volatile LONG64 model_frames;
    volatile LONG64 errors;
    volatile LONG64 initializations;
    uint64_t padding[2];
};
static_assert(sizeof(ControlBlock) == 64);
static_assert(offsetof(ControlBlock, audio_frames) == 16);

class ControlView {
public:
    ControlView() = default;
    ~ControlView();
    ControlView(const ControlView&) = delete;
    ControlView& operator=(const ControlView&) = delete;
    bool open_path(const std::wstring& path);
    bool open_endpoint(const std::wstring& guid);
    uint32_t config() const noexcept;
    void account(uint32_t frames, uint64_t model_frames) noexcept;
    void error() noexcept;
    void model(bool deepfilter) noexcept;
    using ChangeCallback = void (*)(void*) noexcept;
    void watch(ChangeCallback callback, void* context);
    void stop_watching() noexcept;
    bool available() const noexcept { return block_ != nullptr; }
private:
    void close() noexcept;
    ControlBlock* block_ = nullptr;
    std::wstring path_, notification_path_;
    HANDLE changed_ = nullptr, wait_ = nullptr;
    ChangeCallback callback_ = nullptr;
    void* context_ = nullptr;
    static void CALLBACK on_change(void* self, BOOLEAN);
};
}
