#include "processor.h"
#include "rnnoise_backend.h"
#include <algorithm>
#include <cmath>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <vector>

static void require(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}

int wmain(int argc, wchar_t** argv) {
    try {
        require(argc == 2, "RNNoise DLL path required");
        nr::RnnoiseBackend backend(argv[1]);
        std::vector<float> input(48000), whole(input.size()), chunks(input.size());
        for (size_t i = 0; i < input.size(); ++i) input[i] = 0.1f * std::sin(float(i) * 0.03f);
        nr::Processor first(backend.api()), second(backend.api());
        first.process(input.data(), whole.data(), input.size());
        for (size_t at = 0; at < input.size();) {
            size_t count = std::min<size_t>(137, input.size() - at);
            second.process(input.data() + at, chunks.data() + at, count);
            at += count;
        }
        require(whole == chunks, "Block boundaries change audio");
        nr::Processor bypass(backend.api(), false);
        bypass.process(input.data(), whole.data(), input.size());
        for (size_t i = 0; i < input.size(); ++i) {
            float expected = i < nr::Processor::latency ? 0 : input[i - nr::Processor::latency];
            require(whole[i] == expected, "Bypass is not an exact delayed copy");
        }
        require(bypass.processed_frames() == 0, "Disabled processor runs inference");
        nr::Processor silence(backend.api());
        std::fill(input.begin(), input.end(), 0.0f);
        input[0] = std::numeric_limits<float>::quiet_NaN();
        input[1] = std::numeric_limits<float>::infinity();
        silence.process(input.data(), whole.data(), input.size());
        for (float sample : whole) require(std::isfinite(sample) && std::abs(sample) < 1e-6f, "Invalid input poisons model");
        for (size_t i = 0; i < input.size(); ++i) input[i] = 0.1f * std::sin(float(i) * 0.03f);
        nr::Processor toggled(backend.api());
        toggled.process(input.data(), whole.data(), 4800);
        toggled.set_enabled(false);
        toggled.process(input.data(), whole.data(), 4800);
        auto frames = toggled.processed_frames();
        toggled.process(input.data(), whole.data(), 4800);
        require(toggled.processed_frames() == frames, "Disabled model does not go idle after fade");
        for (size_t i = nr::Processor::latency; i < 4800; ++i)
            require(whole[i] == input[i - nr::Processor::latency], "Disabled output loses dry audio");
        toggled.set_enabled(true);
        toggled.process(input.data(), whole.data(), 4800);
        require(toggled.processed_frames() > frames, "Reenabled model stays idle");
        require(std::any_of(whole.begin(), whole.begin() + 4800, [](float x) { return std::abs(x) > 0.01f; }), "Reenable loses all audio");
        nr::Processor in_place(backend.api());
        nr::Processor separate(backend.api());
        separate.process(input.data(), whole.data(), input.size());
        in_place.process(input.data(), input.data(), input.size());
        require(input == whole, "In-place output differs from separate buffers");
        nr::Processor failed_model(nr::Backend{});
        failed_model.process(input.data(), whole.data(), input.size());
        require(!failed_model.has_model() && failed_model.processed_frames() == 0, "Missing model starts inference");
        for (size_t i = nr::Processor::latency; i < input.size(); ++i)
            require(whole[i] == input[i - nr::Processor::latency], "Missing model loses original audio");
        nr::Processor zero_strength(backend.api());
        zero_strength.set_attenuation(0);
        zero_strength.process(input.data(), whole.data(), input.size());
        for (size_t i = nr::Processor::latency + 960; i < input.size(); ++i)
            require(std::abs(whole[i] - input[i - nr::Processor::latency]) < 1e-6f, "Zero strength does not preserve original audio");
        std::cout << "PASS: block boundaries, delayed bypass, idle bypass, silence, nonfinite input, toggles, in-place\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
