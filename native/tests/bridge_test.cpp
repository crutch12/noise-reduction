#include "bridge.h"
#include <chrono>
#include <cstdio>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

static void check(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }

struct Stats { uint64_t frames = 0, model = 0, errors = 0; };
static Stats run(void* bridge, int ms) {
    std::this_thread::sleep_for(std::chrono::milliseconds(ms));
    Stats stats;
    nr_bridge_stats(bridge, &stats.frames, &stats.model, &stats.errors);
    return stats;
}

// на пустом бэкенде miniaudio: звук идёт, модель работает, без модели мост всё равно пропускает звук
int main(int argc, char** argv) {
    try {
        check(argc == 2, "Usage: bridge_test MODELS_DIR");
        void* bridge = nr_bridge_start(nullptr, nullptr, argv[1], 1);
        check(bridge != nullptr, "Bridge did not start");
        nr_bridge_configure(bridge, 1u | (24u << 8));
        Stats first = run(bridge, 600);
        check(first.frames > 0, "No audio captured");
        check(first.model > 0, "Model did not process audio");
        check(first.errors == 0, "Errors with a working model");
        nr_bridge_configure(bridge, 1u | 2u | (40u << 8));
        Stats second = run(bridge, 400);
        check(second.frames > first.frames && second.model > first.model, "Audio stopped after settings change");
        // точную модель проверяем, где её библиотека собрана
#ifdef __APPLE__
        const std::string deepfilter = std::string(argv[1]) + "/libnr_deepfilter.dylib";
#else
        const std::string deepfilter = std::string(argv[1]) + "/libnr_deepfilter.so";
#endif
        if (FILE* file = std::fopen(deepfilter.c_str(), "rb")) {
            std::fclose(file);
            nr_bridge_configure(bridge, 1u | 8u | (24u << 8));
            Stats precise = run(bridge, 800);
            check(precise.model > second.model && precise.errors == 0, "DeepFilterNet did not take over");
            std::cout << "DeepFilterNet checked\n";
        }
        nr_bridge_stop(bridge);

        bridge = nr_bridge_start(nullptr, nullptr, "/nonexistent", 1);
        check(bridge != nullptr, "Bridge without models must still start");
        nr_bridge_configure(bridge, 1u | 8u | (24u << 8));
        Stats broken = run(bridge, 400);
        check(broken.frames > 0, "Bridge without models dropped the audio");
        check(broken.errors > 0, "Missing models were not reported");
        nr_bridge_stop(bridge);
        // запись для проверки голосом: шум заметно тише, длина та же, без модели звук не теряется
        std::vector<float> noise(48000), cleaned(48000);
        unsigned seed = 1;
        for (float& sample : noise) { seed = seed * 1103515245u + 12345u; sample = (static_cast<float>(seed >> 16 & 0x7fff) / 16384.0f - 1.0f) * 0.1f; }
        const auto energy = [](const std::vector<float>& samples) { double sum = 0; for (size_t i = 9600; i < samples.size(); ++i) sum += samples[i] * samples[i]; return sum; };
        check(nr_bridge_process(argv[1], 1u | (60u << 8), noise.data(), cleaned.data(), 48000) == 0, "Recording was not processed");
        check(energy(cleaned) < energy(noise) * 0.5, "Processing did not reduce noise");
        check(nr_bridge_process("/nonexistent", 1u | (60u << 8), noise.data(), cleaned.data(), 48000) == 1, "Missing models were not reported");
        check(energy(cleaned) > energy(noise) * 0.9, "Recording without models lost the audio");
        std::cout << "PASS: bridge capture, model, live settings, missing models keep audio, recording\n";
        return 0;
    }
    catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}
