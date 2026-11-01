#include "../bridge.cpp"
#include <array>
#include <iostream>
#include <stdexcept>

static void check(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }

static void init(Bridge& bridge, bool configured) {
    bridge.configured_start = configured;
    check(ma_pcm_rb_init(ma_format_f32, 1, ring_frames, nullptr, nullptr, &bridge.ring) == MA_SUCCESS, "Ring initialization failed");
    bridge.ring_ready = true;
}

static void write(Bridge& bridge, uint32_t count, float sample) {
    std::vector<float> input(count, sample);
    bridge.write(input.data(), count);
}

static std::array<float, 480> read(Bridge& bridge) {
    std::array<float, 480> output;
    output.fill(-1);
    bridge.on_playback(output.data(), static_cast<uint32_t>(output.size()));
    return output;
}

static bool filled(const std::array<float, 480>& output, float sample) {
    return std::all_of(output.begin(), output.end(), [sample](float value) { return value == sample; });
}

int main() {
    try {
        Bridge configured;
        init(configured, true);
        write(configured, 480, .5f);
        check(filled(read(configured), 0), "Configured playback drained audio before the reserve was primed");
        check(ma_pcm_rb_available_read(&configured.ring) == 480, "Priming discarded captured audio");
        check(configured.underruns == 1 && configured.playback_frames == 480, "Priming silence was hidden from transport metrics");
        write(configured, 960, .25f);
        check(filled(read(configured), 0), "Configured playback started before the 60ms reserve was primed");
        check(ma_pcm_rb_available_read(&configured.ring) == 1440, "Sixty millisecond priming discarded captured audio");
        write(configured, 1440, .125f);
        check(filled(read(configured), .5f), "Priming did not preserve the first captured block");
        const uint64_t priming = configured.underruns;
        check(filled(read(configured), .25f), "First delayed capture callback caused a gap");
        check(filled(read(configured), .25f), "Second delayed capture callback caused a gap");
        check(filled(read(configured), .125f), "Third delayed capture callback caused a gap");
        check(filled(read(configured), .125f), "Fourth delayed capture callback caused a gap");
        check(configured.underruns == priming, "Forty milliseconds of jitter exhausted the primed reserve");
        check(filled(read(configured), .125f), "Final buffered block was lost");
        write(configured, 240, .75f);
        check(filled(read(configured), 0), "A partial starved block was emitted before reprime");
        check(ma_pcm_rb_available_read(&configured.ring) == 240, "A starved partial block was lost");
        write(configured, 480, .125f);
        check(filled(read(configured), 0), "Playback resumed before rebuilding its reserve");
        write(configured, 2160, .125f);
        const auto resumed = read(configured);
        check(std::all_of(resumed.begin(), resumed.begin() + 240, [](float value) { return value == .75f; }) &&
            std::all_of(resumed.begin() + 240, resumed.end(), [](float value) { return value == .125f; }), "Reprime changed PCM order");
        check(configured.underruns == priming + 2 && configured.dropped_frames == 0, "Reprime loss metrics are not truthful");

        Bridge burst;
        init(burst, true);
        std::vector<float> tagged(6720);
        for (size_t i = 0; i < tagged.size(); ++i) tagged[i] = static_cast<float>(i + 1) / 8192;
        burst.write(tagged.data(), 2880);
        const auto prefix = read(burst);
        check(std::equal(prefix.begin(), prefix.end(), tagged.begin()), "Burst fixture did not prime the original PCM");
        burst.write(tagged.data() + 2880, 3840);
        for (size_t block = 0; block < 8; ++block) {
            const auto output = read(burst);
            check(std::equal(output.begin(), output.end(), tagged.begin() + 480 + block * 480), "Balanced capture burst dropped or reordered PCM");
        }
        check(ma_pcm_rb_available_read(&burst.ring) == 2400 && burst.dropped_frames == 0 && burst.underruns == 0,
            "Balanced capture burst did not return to its original reserve without loss");
        for (size_t block = 0; block < 5; ++block) {
            const auto output = read(burst);
            check(std::equal(output.begin(), output.end(), tagged.begin() + 4320 + block * 480), "Buffered burst tail did not preserve every PCM sample");
        }
        check(burst.dropped_frames == 0 && burst.underruns == 0, "Balanced burst introduced transport loss");

        Bridge overflow;
        init(overflow, true);
        write(overflow, 12480, .5f);
        check(filled(read(overflow), .5f), "Overflow correction failed to start a primed buffer");
        check(overflow.dropped_frames == 9600 && ma_pcm_rb_available_read(&overflow.ring) == 2400, "Overflow did not retain the sixty millisecond reserve");

        Bridge legacy;
        init(legacy, false);
        write(legacy, 480, .5f);
        check(filled(read(legacy), .5f), "Legacy start gained configured-only priming latency");
        check(ma_pcm_rb_available_read(&legacy.ring) == 0 && legacy.underruns == 0, "Legacy ring semantics changed");
        write(legacy, 6000, .5f);
        check(filled(read(legacy), .5f) && legacy.dropped_frames == 4560 && ma_pcm_rb_available_read(&legacy.ring) == 960, "Legacy overflow target changed");
        std::cout << "PASS: configured priming, jitter reserve, burst PCM order, bounded overflow, truthful counters, legacy playback\n";
        return 0;
    }
    catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}
