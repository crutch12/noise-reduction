// виртуальный микрофон Noise Reduction для macOS
// два устройства: видимый микрофон и скрытый выход, в который пишет приложение
// скрытый выход нельзя случайно выбрать динамиком, иначе звук системы попал бы в микрофон
#include <aspl/Driver.hpp>

#include <CoreAudio/AudioServerPlugIn.h>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <vector>

namespace {

constexpr UInt32 SampleRate = 48000;
// около 30 мс запаса, больше 100 мс не копим: иначе задержка растёт от разницы часов
constexpr UInt32 Capacity = SampleRate, TargetFill = 1440, MaxFill = 4800;

// один писатель и один читатель, каждый на своём потоке реального времени
class Ring
{
public:
    void Write(const float* data, UInt32 count)
    {
        UInt64 head = head_.load(std::memory_order_relaxed);
        for (UInt32 i = 0; i < count; ++i) {
            buffer_[(head + i) % Capacity] = data[i];
        }
        head_.store(head + count, std::memory_order_release);
    }

    void Read(float* data, UInt32 count)
    {
        const UInt64 head = head_.load(std::memory_order_acquire);
        UInt64 tail = tail_.load(std::memory_order_relaxed);
        if (head - tail > MaxFill) {
            tail = head - TargetFill;
        }
        UInt32 ready = static_cast<UInt32>(std::min<UInt64>(head - tail, count));
        for (UInt32 i = 0; i < ready; ++i) {
            data[i] = buffer_[(tail + i) % Capacity];
        }
        std::fill(data + ready, data + count, 0.0f);
        tail_.store(tail + ready, std::memory_order_relaxed);
    }

private:
    std::vector<float> buffer_ = std::vector<float>(Capacity);
    std::atomic<UInt64> head_{0}, tail_{0};
};

class FeedHandler : public aspl::IORequestHandler
{
public:
    explicit FeedHandler(Ring& ring) : ring_(ring) {}

    void OnWriteMixedOutput(const std::shared_ptr<aspl::Stream>&, Float64, Float64, const void* bytes, UInt32 size) override
    {
        ring_.Write(static_cast<const float*>(bytes), size / sizeof(float));
    }

private:
    Ring& ring_;
};

class MicrophoneHandler : public aspl::IORequestHandler
{
public:
    explicit MicrophoneHandler(Ring& ring) : ring_(ring) {}

    void OnReadClientInput(const std::shared_ptr<aspl::Client>&, const std::shared_ptr<aspl::Stream>&, Float64, Float64, void* bytes, UInt32 size) override
    {
        ring_.Read(static_cast<float*>(bytes), size / sizeof(float));
    }

private:
    Ring& ring_;
};

std::shared_ptr<aspl::Driver> CreateDriver()
{
    static Ring ring;
    auto context = std::make_shared<aspl::Context>();

    aspl::DeviceParameters micParams;
    micParams.Name = "Noise Reduction";
    micParams.Manufacturer = "Noise Reduction";
    micParams.DeviceUID = "NoiseReduction_Microphone";
    micParams.ModelUID = "NoiseReduction";
    micParams.SampleRate = SampleRate;
    micParams.ChannelCount = 1;
    auto mic = std::make_shared<aspl::Device>(context, micParams);
    mic->AddStreamAsync(aspl::Direction::Input);
    mic->SetIOHandler(std::make_shared<MicrophoneHandler>(ring));

    aspl::DeviceParameters feedParams;
    feedParams.Name = "Noise Reduction Feed";
    feedParams.Manufacturer = "Noise Reduction";
    feedParams.DeviceUID = "NoiseReduction_Feed";
    feedParams.ModelUID = "NoiseReduction";
    feedParams.SampleRate = SampleRate;
    feedParams.ChannelCount = 1;
    feedParams.CanBeDefault = false;
    feedParams.CanBeDefaultForSystemSounds = false;
    feedParams.EnableMixing = true;
    auto feed = std::make_shared<aspl::Device>(context, feedParams);
    feed->AddStreamAsync(aspl::Direction::Output);
    feed->SetIOHandler(std::make_shared<FeedHandler>(ring));
    feed->SetIsHidden(true);

    auto plugin = std::make_shared<aspl::Plugin>(context);
    plugin->AddDevice(mic);
    plugin->AddDevice(feed);
    return std::make_shared<aspl::Driver>(context, plugin);
}

} // namespace

extern "C" void* NoiseReductionEntryPoint(CFAllocatorRef, CFUUIDRef typeUUID)
{
    if (!CFEqual(typeUUID, kAudioServerPlugInTypeUUID)) {
        return nullptr;
    }
    static std::shared_ptr<aspl::Driver> driver = CreateDriver();
    return driver->GetReference();
}
