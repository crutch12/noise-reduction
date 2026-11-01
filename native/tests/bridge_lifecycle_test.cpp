#include "../bridge.cpp"
#include <condition_variable>
#include <iostream>
#include <stdexcept>
#include <thread>

static void check(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }

struct IdleLoop {
    std::mutex mutex;
    std::condition_variable changed;
    bool entered = false, released = false, stopped = false;
    unsigned wakeups = 0;
};

static ma_result idle_loop(ma_device* device) {
    auto& loop = *static_cast<IdleLoop*>(device->pUserData);
    std::unique_lock<std::mutex> lock(loop.mutex);
    loop.entered = true;
    loop.changed.notify_all();
    loop.changed.wait(lock, [&loop] { return loop.released; });
    return MA_SUCCESS;
}

static ma_result wake_idle_loop(ma_device* device) {
    auto& loop = *static_cast<IdleLoop*>(device->pUserData);
    std::lock_guard<std::mutex> lock(loop.mutex);
    ++loop.wakeups;
    loop.released = true;
    loop.changed.notify_all();
    return MA_SUCCESS;
}

// The real miniaudio worker blocks as Pulse does after its source disappears
static void stop_idle_device(ma_device_type type) {
    IdleLoop loop;
    auto bridge = std::make_unique<Bridge>();
    bridge->configured_start = true;
    check(init_context(bridge->context, 1), "Lifecycle context initialization failed");
    bridge->context_ready = true;
    ma_device_config config = ma_device_config_init(type);
    config.sampleRate = rate;
    config.capture.format = config.playback.format = ma_format_f32;
    config.capture.channels = config.playback.channels = 1;
    config.pUserData = &loop;
    ma_device* device = type == ma_device_type_capture ? &bridge->capture : &bridge->playback;
    check(ma_device_init(&bridge->context, &config, device) == MA_SUCCESS, "Lifecycle device initialization failed");
    if (type == ma_device_type_capture) bridge->capture_ready = true;
    else bridge->playback_ready = true;
    bridge->context.callbacks.onDeviceStart = [](ma_device*) { return MA_SUCCESS; };
    bridge->context.callbacks.onDeviceStop = [](ma_device*) { return MA_ERROR; };
    bridge->context.callbacks.onDeviceDataLoop = idle_loop;
    bridge->context.callbacks.onDeviceDataLoopWakeup = wake_idle_loop;
    bridge->context.callbacks.onContextUninit = [](ma_context*) { return MA_SUCCESS; };
    bridge->context.backend = ma_backend_pulseaudio;
    check(ma_device_start(device) == MA_SUCCESS, "Lifecycle device did not start");
    {
        std::unique_lock<std::mutex> lock(loop.mutex);
        check(loop.changed.wait_for(lock, std::chrono::seconds(1), [&loop] { return loop.entered; }), "Backend loop did not become idle");
    }
    Bridge* handle = bridge.release();
    std::thread stop([&loop, handle] {
        delete handle;
        std::lock_guard<std::mutex> lock(loop.mutex);
        loop.stopped = true;
        loop.changed.notify_all();
    });
    bool completed;
    {
        std::unique_lock<std::mutex> lock(loop.mutex);
        completed = loop.changed.wait_for(lock, std::chrono::seconds(1), [&loop] { return loop.stopped; });
        // Release the broken implementation too, so RED exits without a stuck test process
        loop.released = true;
        loop.changed.notify_all();
    }
    stop.join();
    check(completed, "Configured Pulse stop failed to wake its idle backend loop");
    check(loop.wakeups == 1, "Configured Pulse stop did not use the backend wakeup");
}

int main() {
    try {
        stop_idle_device(ma_device_type_capture);
        stop_idle_device(ma_device_type_playback);
        std::cout << "PASS: configured Pulse capture and playback stop wake idle workers after backend failure\n";
        return 0;
    }
    catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}
