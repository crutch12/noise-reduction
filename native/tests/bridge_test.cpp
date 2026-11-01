#include "bridge.h"
#include <chrono>
#include <cmath>
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
        static_assert(sizeof(nr_bridge_metrics) == 64, "Metrics ABI changed");
        nr_bridge_metrics metrics{};
        nr_bridge_snapshot(nullptr, &metrics);
        check(metrics.running == 0 && metrics.capture_frames == 0, "Null snapshot is not empty");
        void* configured = nr_bridge_start_configured(nullptr, nullptr, argv[1], 1, 0);
        check(configured != nullptr, "Configured bridge did not start");
        std::this_thread::sleep_for(std::chrono::milliseconds(150));
        nr_bridge_snapshot(configured, &metrics);
        check(metrics.running == 1 && metrics.capture_frames > 0 && metrics.playback_frames > 0, "Transport counters missing");
        check(metrics.model_frames == 0 && metrics.actual_model == 0, "Initial bypass was not applied before start");
        nr_bridge_configure(configured, 1u | (24u << 8));
        std::this_thread::sleep_for(std::chrono::milliseconds(150));
        nr_bridge_snapshot(configured, &metrics);
        check(metrics.actual_model == 1 && metrics.model_frames > 0, "Actual RNNoise model missing");
        nr_bridge_stop(configured);
        configured = nr_bridge_start_configured(nullptr, nullptr, "/nonexistent", 1, 8u);
        check(configured != nullptr, "Configured bypass without models did not start");
        nr_bridge_snapshot(configured, &metrics);
        check(metrics.actual_model == 0 && metrics.errors == 0, "Configured bypass loaded unused models");
        nr_bridge_configure(configured, 1u | 8u);
        nr_bridge_snapshot(configured, &metrics);
        const auto failures = metrics.errors;
        check(failures > 0, "Enabling missing models did not report failure");
        nr_bridge_configure(configured, 1u | 8u | (12u << 8));
        nr_bridge_snapshot(configured, &metrics);
        check(metrics.errors == failures, "Strength update rebuilt a missing model");
        nr_bridge_stop(configured);
        std::vector<float> marker(4921), aligned(marker.size());
        marker[0] = 0.75f; marker[479] = -0.5f; marker[4800] = 0.25f;
        int actual = -1;
        check(nr_bridge_process_ex(argv[1], 0, marker.data(), aligned.data(), static_cast<int>(marker.size()), &actual) == 0 && actual == 0, "Offline bypass failed");
        check(marker == aligned, "Offline bypass changed marker alignment");
        check(nr_bridge_process_ex(argv[1], 1u, marker.data(), aligned.data(), static_cast<int>(marker.size()), &actual) == 0 && actual == 1, "RNNoise marker was not processed");
        check(marker == aligned, "RNNoise zero-strength marker is not aligned");
        std::vector<float> hum(48000, 0.25f), filtered(hum.size());
        check(nr_bridge_process_ex(argv[1], 1u | 2u, hum.data(), filtered.data(), static_cast<int>(hum.size()), &actual) == 0, "Low-cut preview failed");
        check(std::abs(filtered.back()) < 0.001f, "Low-cut config did not remove DC");
        check(nr_bridge_process_ex(argv[1], 2u, hum.data(), filtered.data(), static_cast<int>(hum.size()), &actual) == 0 && hum == filtered, "Disabled preview still applies low-cut");
        check(nr_bridge_process_ex("/nonexistent", 1u | 8u, marker.data(), aligned.data(), static_cast<int>(marker.size()), &actual) != 0 && actual == 0, "Missing model identity is not truthful");
        check(marker == aligned, "Missing model lost PCM alignment");
        check(nr_bridge_process_ex(argv[1], 1u, marker.data(), aligned.data(), -1, &actual) != 0, "Negative length accepted");
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
            configured = nr_bridge_start_configured(nullptr, nullptr, argv[1], 1, 1u | 8u | (24u << 8));
            check(configured != nullptr, "Configured DeepFilterNet did not start");
            nr_bridge_snapshot(configured, &metrics);
            check(metrics.actual_model == 2, "Requested DeepFilterNet was not selected before start");
            nr_bridge_stop(configured);
            check(nr_bridge_process_ex(argv[1], 1u | 8u, marker.data(), aligned.data(), static_cast<int>(marker.size()), &actual) == 0 && actual == 2, "DeepFilterNet marker was not processed");
            check(marker == aligned, "DeepFilterNet zero-strength marker is not aligned");
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
