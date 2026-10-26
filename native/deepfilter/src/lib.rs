// Обёртка DeepFilterNet для эффекта: тот же вид функций, что у RNNoise,
// и ни одна паника модели не выходит наружу, иначе упадёт звуковая служба Windows
use df::tract::{DfParams, DfTract, RuntimeParams};
use ndarray::{ArrayView2, ArrayViewMut2};
use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr::null_mut;

const FRAME: usize = 480;
const SCALE: f32 = 32768.0;

pub struct State {
    model: DfTract,
    input: [f32; FRAME],
    output: [f32; FRAME],
    seed: u32,
}

#[no_mangle]
pub extern "C" fn nr_df_get_frame_size() -> i32 {
    FRAME as i32
}

/// Create a model state, null if the embedded model cannot start
#[no_mangle]
pub extern "C" fn nr_df_create(_model: *mut c_void) -> *mut State {
    catch_unwind(|| {
        // предел ослабления снят, силу подавления задаёт сам эффект смешиванием с исходным звуком;
        // пороги как в утилите deep-filter: с порогами библиотеки модель посреди речи то включает, то пропускает шаги и звук булькает
        let params = RuntimeParams::default_with_ch(1).with_atten_lim(100.0).with_thresholds(-15.0, 35.0, 35.0);
        let model = DfTract::new(DfParams::default(), &params).ok()?;
        if model.hop_size != FRAME || model.sr != 48000 {
            return None;
        }
        Some(Box::into_raw(Box::new(State { model, input: [0.0; FRAME], output: [0.0; FRAME], seed: 0x2545_f491 })))
    })
    .ok()
    .flatten()
    .unwrap_or(null_mut())
}

/// Samples of delay between input and output: STFT window plus model lookahead
#[no_mangle]
pub unsafe extern "C" fn nr_df_get_latency(st: *const State) -> i32 {
    match st.as_ref() {
        Some(state) => (state.model.fft_size - state.model.hop_size + state.model.lookahead * state.model.hop_size) as i32,
        None => -1,
    }
}

/// Same contract as rnnoise_process_frame: 480 samples in 16-bit range, returns voice probability
#[no_mangle]
pub unsafe extern "C" fn nr_df_process_frame(st: *mut State, out: *mut f32, input: *const f32) -> f32 {
    if st.is_null() || out.is_null() || input.is_null() {
        return 0.0;
    }
    let state = &mut *st;
    let source = std::slice::from_raw_parts(input, FRAME);
    let target = std::slice::from_raw_parts_mut(out, FRAME);
    // на полной тишине модель пропускает кадр и потом отдаёт старый звук,
    // неслышимый шум около -100 дБ держит её в работе
    for (dst, src) in state.input.iter_mut().zip(source) {
        state.seed = state.seed.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
        let dither = ((state.seed >> 9) as f32 / (1u32 << 23) as f32 - 0.5) * 2e-5;
        *dst = *src / SCALE + dither;
    }
    let result = catch_unwind(AssertUnwindSafe(|| {
        let noisy = ArrayView2::from_shape((1, FRAME), &state.input[..]).ok()?;
        let enhanced = ArrayViewMut2::from_shape((1, FRAME), &mut state.output[..]).ok()?;
        state.model.process(noisy, enhanced).ok()
    }));
    match result {
        Ok(Some(snr)) => {
            for (dst, src) in target.iter_mut().zip(state.output.iter()) {
                *dst = if src.is_finite() { *src * SCALE } else { 0.0 };
            }
            // локальное SNR в дБ переводим в грубую вероятность голоса, как у RNNoise
            ((snr + 5.0) / 20.0).clamp(0.0, 1.0)
        }
        _ => {
            target.copy_from_slice(source);
            0.0
        }
    }
}

/// Strength in dB: the model's own attenuation limit, 60 and above removes the limit and adds the post filter
#[no_mangle]
pub unsafe extern "C" fn nr_df_set_strength(st: *mut State, db: f32) {
    if st.is_null() || !db.is_finite() {
        return;
    }
    let state = &mut *st;
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if db >= 60.0 {
            state.model.set_atten_lim(100.0);
            state.model.set_pf_beta(0.02);
        } else {
            state.model.set_atten_lim(db.max(0.0));
            state.model.set_pf_beta(0.0);
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn nr_df_destroy(st: *mut State) {
    if !st.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(st))));
    }
}
