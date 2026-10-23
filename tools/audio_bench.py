"""Local model comparison, not a claim of perceptual quality or APO latency."""

import argparse
import ctypes
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import subprocess
import time

import numpy as np
from scipy.signal import correlate, correlation_lags
import soundfile as sf


def align_delayed(samples, delay, length):
    result = np.zeros(length, dtype=np.float32)
    available = samples[delay:delay + length]
    result[:len(available)] = available
    return result


def estimate_delay(original, processed, max_delay):
    if not np.any(original) or not np.any(processed):
        return 0
    correlation = correlate(processed, original, method="fft")
    lags = correlation_lags(len(processed), len(original))
    valid = (lags >= 0) & (lags <= max_delay)
    return int(lags[valid][np.argmax(correlation[valid])])


def mix_floor(original, processed, limit_db):
    if not math.isfinite(limit_db) or limit_db < 0:
        raise ValueError("Attenuation must be finite and nonnegative")
    dry = 10 ** (-limit_db / 20)
    return dry * original + (1 - dry) * processed


class RNNoise:
    """Float interface avoids int16 wrapping in the exploratory Python wrapper."""

    def __init__(self):
        package = Path(importlib.util.find_spec("pyrnnoise").origin).parent
        self.library_path = package / "rnnoise.dll"
        self.library = ctypes.CDLL(str(self.library_path))
        self.library.rnnoise_create.argtypes = [ctypes.c_void_p]
        self.library.rnnoise_create.restype = ctypes.c_void_p
        self.library.rnnoise_destroy.argtypes = [ctypes.c_void_p]
        self.library.rnnoise_destroy.restype = None
        pointer = ctypes.POINTER(ctypes.c_float)
        self.library.rnnoise_process_frame.argtypes = [ctypes.c_void_p, pointer, pointer]
        self.library.rnnoise_process_frame.restype = ctypes.c_float
        self.library.rnnoise_get_frame_size.restype = ctypes.c_int
        self.frame_size = self.library.rnnoise_get_frame_size()

    def process(self, samples):
        samples = np.asarray(samples, dtype=np.float32)
        if samples.ndim != 1 or not len(samples) or not np.isfinite(samples).all():
            raise ValueError("Expected nonempty finite mono audio")
        state = self.library.rnnoise_create(None)
        if not state:
            raise MemoryError("RNNoise initialization failed")
        # Extra zero frames flush the algorithmic delay and partial final frame
        count = math.ceil(len(samples) / self.frame_size) + 4
        padded = np.zeros(count * self.frame_size, dtype=np.float32)
        padded[:len(samples)] = samples * 32768
        frames = padded.reshape(-1, self.frame_size)
        times = []
        probabilities = []
        cpu_start = time.process_time()
        start = time.perf_counter()
        try:
            for frame in frames:
                pointer = frame.ctypes.data_as(ctypes.POINTER(ctypes.c_float))
                tick = time.perf_counter_ns()
                probabilities.append(self.library.rnnoise_process_frame(state, pointer, pointer))
                times.append((time.perf_counter_ns() - tick) / 1e6)
        finally:
            self.library.rnnoise_destroy(state)
        return padded / 32768, {
            "inference_loop_wall_seconds": time.perf_counter() - start,
            "inference_loop_cpu_seconds": time.process_time() - cpu_start,
            "frame_size": self.frame_size,
            "frame_ms_p50_p95_p99_max": np.percentile(times, [50, 95, 99, 100]).tolist(),
            "frames_over_10ms": sum(value > 10 for value in times),
            "vad_probabilities": probabilities,
            "library_sha256": hashlib.sha256(self.library_path.read_bytes()).hexdigest(),
        }


def describe(samples):
    return {
        "samples": len(samples),
        "finite": bool(np.isfinite(samples).all()),
        "peak": float(np.max(np.abs(samples))) if len(samples) else 0,
        "rms": float(np.sqrt(np.mean(np.square(samples, dtype=np.float64)))) if len(samples) else 0,
        "samples_at_or_above_full_scale": int(np.sum(np.abs(samples) >= 1)),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", type=Path, default=Path(".local/comparison"))
    parser.add_argument("--deep-filter", type=Path, default=Path(".local/bin/deep-filter.exe"))
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    decoded_path = args.output / "decoded-float.wav"
    subprocess.run(["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-i", str(args.input),
                    "-ac", "1", "-ar", "48000", "-c:a", "pcm_f32le", str(decoded_path)], check=True)
    source, sample_rate = sf.read(decoded_path, dtype="float32")
    if not len(source) or not np.isfinite(source).all():
        raise ValueError("Input must contain finite audio samples")
    # One shared gain for all comparisons, retaining at least 6 dB of headroom
    gain = min(10 ** (-6 / 20), 0.5 / max(float(np.max(np.abs(source))), 1e-12))
    dry = source * gain
    original_path = args.output / "original.wav"
    # Use the same quantized input for both models and the listening reference
    sf.write(original_path, dry, sample_rate, subtype="PCM_16")
    dry, _ = sf.read(original_path, dtype="float32")
    report = {
        "input_sha256": hashlib.sha256(args.input.read_bytes()).hexdigest(),
        "sample_rate": sample_rate,
        "duration_seconds": len(dry) / sample_rate,
        "shared_input_gain_db": 20 * math.log10(gain),
        "source": describe(source),
        "notes": ["All listening files use the same input gain; no individual normalization",
                  "No clean speech reference: no objective speech-quality or noise-reduction score",
                  "Offline throughput does not establish real-time deadlines or system-wide latency",
                  "RNNoise alignment is estimated by correlation; DeepFilterNet uses its own delay compensation"],
        "outputs": {"original.wav": describe(dry)},
    }
    engine = RNNoise()
    wet, timing = engine.process(dry)
    delay = estimate_delay(dry, wet, 4800)
    wet = align_delayed(wet, delay, len(dry))
    report["rnnoise"] = timing
    report["rnnoise"]["estimated_delay_samples"] = delay
    report["rnnoise"]["real_time_factor"] = timing["inference_loop_wall_seconds"] / report["duration_seconds"]
    for name, data in (("rnnoise-full.wav", wet), ("rnnoise-24db.wav", mix_floor(dry, wet, 24))):
        if not np.isfinite(data).all() or np.max(np.abs(data)) >= 1:
            raise ValueError(f"Invalid or overloaded output: {name}")
        sf.write(args.output / name, data, sample_rate, subtype="PCM_16")
        report["outputs"][name] = describe(data)
    report["deepfilter"] = {}
    binary = args.deep_filter.resolve()
    # Upstream -D discards initial delay but does not flush the model tail
    # Pad by 100 ms, round to a full frame and trim only after compensation
    cli_input = args.output / "deepfilter-input.wav"
    padded_length = math.ceil(len(dry) / 480) * 480 + 4800
    sf.write(cli_input, np.pad(dry, (0, padded_length - len(dry))), sample_rate, subtype="FLOAT")
    for limit in (100,):
        folder = args.output / f"df-{limit}"
        start = time.perf_counter()
        result = subprocess.run([str(binary), "-D", "-a", str(limit), "-o", str(folder), str(cli_input)],
                                capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(f"DeepFilterNet failed ({result.returncode}): {result.stdout}{result.stderr}")
        elapsed = time.perf_counter() - start
        data, rate = sf.read(folder / cli_input.name, dtype="float32")
        compensated_samples = padded_length - len(data)
        if rate != sample_rate or len(data) < len(dry) or not 0 <= compensated_samples <= 4800:
            raise ValueError("DeepFilterNet changed output format or length")
        data = data[:len(dry)]
        if not np.isfinite(data).all() or np.max(np.abs(data)) >= 1:
            raise ValueError("DeepFilterNet output is invalid or overloaded")
        name = f"deepfilter-{limit}db.wav"
        sf.write(args.output / name, data, rate, subtype="PCM_16")
        report["outputs"][name] = describe(data)
        report["deepfilter"][str(limit)] = {
            "cli_wall_seconds_including_init_and_io": elapsed,
            "cli_real_time_factor": elapsed / report["duration_seconds"],
            "compensated_delay_samples": compensated_samples,
            "log": result.stdout + result.stderr,
        }
        soft = mix_floor(dry, data, 24)
        sf.write(args.output / "deepfilter-24db.wav", soft, rate, subtype="PCM_16")
        report["outputs"]["deepfilter-24db.wav"] = describe(soft)
        report["deepfilter"]["soft_mode"] = "24 dB dry floor applied after full inference; upstream attenuation limiter is not used"
    report["deepfilter"]["binary_sha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
    (args.output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(args.output), "rnnoise_rtf": report["rnnoise"]["real_time_factor"],
                      "delay_samples": delay, "files": list(report["outputs"])}, indent=2))


if __name__ == "__main__":
    main()
