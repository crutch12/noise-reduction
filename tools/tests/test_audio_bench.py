import sys
import subprocess
from pathlib import Path

import numpy as np
import pytest
import soundfile as sf

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from audio_bench import RNNoise, align_delayed, estimate_delay, mix_floor


def test_alignment_preserves_duration_and_removes_known_delay():
    rng = np.random.default_rng(42)
    original = rng.normal(size=24000).astype(np.float32)
    delayed = np.concatenate((np.zeros(480), original, np.zeros(480)))
    assert estimate_delay(original, delayed, 960) == 480
    np.testing.assert_array_equal(align_delayed(delayed, 480, len(original)), original)


def test_alignment_pads_missing_tail_without_wrapping():
    np.testing.assert_array_equal(align_delayed(np.array([0, 1, 2]), 1, 4), [1, 2, 0, 0])


def test_silence_has_no_spurious_delay():
    assert estimate_delay(np.zeros(1000), np.zeros(1000), 480) == 0


def test_floor_uses_aligned_original_and_limits_attenuation():
    dry = np.ones(12, dtype=np.float32)
    wet = np.zeros(12, dtype=np.float32)
    np.testing.assert_allclose(mix_floor(dry, wet, 20), 0.1)
    np.testing.assert_allclose(mix_floor(dry, wet, 0), dry)


@pytest.mark.parametrize('limit', [-1, float('nan'), float('inf')])
def test_invalid_attenuation_is_rejected(limit):
    with pytest.raises(ValueError):
        mix_floor(np.ones(2), np.zeros(2), limit)


def test_native_model_handles_silence_and_partial_final_frame():
    output, report = RNNoise().process(np.zeros(1001, dtype=np.float32))
    assert len(output) >= 1001
    assert np.isfinite(output).all()
    assert np.max(np.abs(output)) < 1e-6
    assert report['frame_ms_p50_p95_p99_max'][0] > 0


@pytest.mark.parametrize('samples', [[], [[0, 1]], [float('nan')], [float('inf')]])
def test_invalid_audio_is_rejected_before_native_inference(samples):
    with pytest.raises(ValueError):
        RNNoise().process(samples)


def test_native_model_flushes_nonzero_partial_tail():
    signal = (0.1 * np.sin(np.arange(10001) * 0.03)).astype(np.float32)
    actual, _ = RNNoise().process(signal)
    padded, _ = RNNoise().process(np.pad(signal, (0, 4800)))
    # The dry floor must retain the last samples even if the model removes a tone
    actual = mix_floor(signal, align_delayed(actual, 960, len(signal)), 24)
    expected = mix_floor(signal, align_delayed(padded, 960, len(signal)), 24)
    np.testing.assert_array_equal(actual, expected)
    assert np.any(actual[-100:] != 0)


def test_cli_preserves_nonzero_tail_for_both_models(tmp_path):
    repo = Path(__file__).resolve().parents[2]
    binary = repo / '.local/bin/deep-filter.exe'
    if not binary.exists():
        pytest.skip('Run tools/setup-lab.ps1 for the DeepFilterNet integration test')
    source = (0.1 * np.sin(np.arange(10001) * 0.03)).astype(np.float32)
    outputs = []
    for padding in (0, 4800):
        recording = tmp_path / f'input-{padding}.wav'
        sf.write(recording, np.pad(source, (0, padding)), 48000, subtype='FLOAT')
        folder = tmp_path / f'output-{padding}'
        subprocess.run([sys.executable, str(repo / 'tools/audio_bench.py'), str(recording),
                        '--output', str(folder), '--deep-filter', str(binary)], check=True, capture_output=True)
        outputs.append(folder)
    for filename in ('rnnoise-24db.wav', 'deepfilter-24db.wav', 'deepfilter-100db.wav'):
        actual, rate = sf.read(outputs[0] / filename)
        extended, _ = sf.read(outputs[1] / filename)
        assert rate == 48000 and len(actual) == len(source)
        np.testing.assert_allclose(actual, extended[:len(source)], atol=1 / 32768, rtol=0)
        if filename.endswith('24db.wav'):
            assert np.any(actual[-100:] != 0)
