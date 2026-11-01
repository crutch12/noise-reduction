using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{
    internal sealed class VoicePlayback
    {
        private readonly long start;
        internal VoicePlayback(long position = 0) { start = position; }
        internal long FramesWritten { get; private set; }
        internal void Fill(float[] original, float[] processed, bool selected, float[] block)
        {
            float[] source = selected || processed.Length != original.Length ? original : processed;
            for (int i = 0; i < block.Length; i++) block[i] = source.Length == 0 ? 0 : source[(int)((start + FramesWritten + i) % source.Length)];
            FramesWritten += block.Length;
        }
    }

    internal sealed class VoicePcm
    {
        private readonly float[] samples;
        private readonly byte[] partial = new byte[4];
        private readonly float[] peaks = new float[200];
        private int partialCount, peakCount, peakPosition, bucketCount;
        private float bucketPeak;
        internal VoicePcm(int capacity) { samples = new float[capacity]; }
        internal int Count { get; private set; }
        internal bool Full => Count == samples.Length;
        internal bool HasPartialSample => partialCount != 0;
        internal void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
            {
                if (Full) break;
                partial[partialCount++] = value;
                if (partialCount != 4) continue;
                float sample = BinaryPrimitives.ReadSingleLittleEndian(partial);
                if (!float.IsFinite(sample)) throw new InvalidDataException(VoiceTest.Local("Неверный формат звука микрофона", "Invalid microphone audio format"));
                samples[Count++] = sample;
                partialCount = 0;
                bucketPeak = Math.Max(bucketPeak, Math.Abs(sample));
                if (++bucketCount == 960)
                {
                    peaks[peakPosition] = bucketPeak;
                    peakPosition = (peakPosition + 1) % peaks.Length;
                    peakCount = Math.Min(peaks.Length, peakCount + 1);
                    bucketCount = 0; bucketPeak = 0;
                }
            }
        }
        internal float[] ToArray() => samples.AsSpan(0, Count).ToArray();
        internal float[] LivePeaks()
        {
            int complete = Math.Min(peakCount, bucketCount > 0 ? 199 : 200);
            float[] result = new float[complete + (bucketCount > 0 ? 1 : 0)];
            for (int i = 0; i < complete; i++) result[i] = peaks[(peakPosition - complete + i + peaks.Length) % peaks.Length];
            if (bucketCount > 0) result[complete] = bucketPeak;
            return result;
        }
    }

    internal sealed class VoiceTest : IDisposable
    {
        private const int Rate = 48000, MaximumFrames = Rate * 60;
        private readonly object gate = new object();
        private readonly Action<Action> dispatch;
        private readonly SemaphoreSlim processLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource captureStop, processStop, playbackStop;
        private Task captureTask = Task.CompletedTask;
        private Process captureProcess, playbackProcess;
        private float[] original = Array.Empty<float>(), processed = Array.Empty<float>(), livePeaks = Array.Empty<float>();
        private bool recording, playing, processing, originalSelected, disposed;
        private int generation, processGeneration, playbackGeneration, actualModel;
        private long position, playbackStart, queuedFrames;
        private Stopwatch playbackClock;
        private uint effectiveConfig;
        private string deviceId, error = string.Empty;

        [DllImport("nr_bridge", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nr_bridge_process_ex(byte[] models, uint config, float[] input, float[] output, int count, out int actualModel);

        internal VoiceTest(Action<Action> dispatch = null) { this.dispatch = dispatch ?? (action => action()); }
        internal string DeviceId { get { lock (gate) return deviceId; } }
        internal bool Recording { get { lock (gate) return recording; } }
        internal bool Playing { get { lock (gate) return playing; } }
        internal bool Processing { get { lock (gate) return processing; } }
        internal bool HasRecording { get { lock (gate) return original.Length > 0; } }
        internal bool OriginalSelected { get { lock (gate) return originalSelected; } }
        internal string Error { get { lock (gate) return error; } }
        internal float[] Original { get { lock (gate) return original; } }
        internal float[] Processed { get { lock (gate) return processed; } }
        internal float[] LivePeaks { get { lock (gate) return livePeaks; } }
        internal int ActualModel { get { lock (gate) return actualModel; } }
        internal TimeSpan Duration { get { lock (gate) return TimeSpan.FromSeconds((recording ? position : original.Length) / (double)Rate); } }
        internal TimeSpan Position { get { lock (gate) return TimeSpan.FromSeconds(PositionFrames() / (double)Rate); } }
        internal event Action Changed;

        internal async Task StartAsync(Microphone device, uint config)
        {
            Clear();
            Task first;
            int id;
            lock (gate)
            {
                if (disposed) return;
                id = generation;
                if (device == null || !device.Available || string.IsNullOrWhiteSpace(device.SourceName))
                {
                    error = UiText.Get("Микрофон недоступен");
                    first = Task.CompletedTask;
                }
                else
                {
                    deviceId = device.Id;
                    effectiveConfig = config;
                    recording = true;
                    captureStop = new CancellationTokenSource();
                    var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var token = captureStop.Token;
                    captureTask = Task.Run(() => CaptureAsync(device.SourceName, id, token, ready));
                    first = ready.Task;
                }
            }
            Notify(id);
            await first.ConfigureAwait(false);
        }

        internal async Task StopRecordingAsync()
        {
            Task task;
            Process process;
            lock (gate)
            {
                captureStop?.Cancel();
                task = captureTask;
                process = captureProcess;
            }
            StopProcess(process);
            await task.ConfigureAwait(false);
        }

        private async Task CaptureAsync(string source, int id, CancellationToken token, TaskCompletionSource<bool> ready)
        {
            var pcm = new VoicePcm(MaximumFrames);
            string failure = null;
            using var process = new Process { StartInfo = Command("parec", false, source) };
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            using var firstAudio = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            firstAudio.CancelAfter(TimeSpan.FromSeconds(5));
            Task<string> stderr = Task.FromResult(string.Empty);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new IOException(Local("Не удалось начать запись", "Could not start recording"));
                stderr = ReadErrorAsync(process.StandardError);
                lock (gate)
                {
                    if (disposed || id != generation) throw new OperationCanceledException();
                    captureProcess = process;
                }
                byte[] bytes = new byte[3840];
                bool first = true;
                while (!pcm.Full)
                {
                    int count;
                    try { count = await process.StandardOutput.BaseStream.ReadAsync(bytes, first ? firstAudio.Token : limit.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (first && !limit.IsCancellationRequested)
                    {
                        throw new IOException(UiText.Get("Микрофон не передал запись, проверьте разрешение доступа и занятость устройства"));
                    }
                    if (count == 0)
                    {
                        if (pcm.HasPartialSample) throw new InvalidDataException(Local("Неполный PCM от микрофона", "Incomplete microphone PCM sample"));
                        throw new IOException(Local("Запись прервалась", "Recording was interrupted"));
                    }
                    pcm.Append(bytes.AsSpan(0, count));
                    if (pcm.Count == 0) continue;
                    first = false;
                    lock (gate)
                    {
                        if (disposed || id != generation) throw new OperationCanceledException();
                        position = pcm.Count;
                        livePeaks = pcm.LivePeaks();
                    }
                    ready.TrySetResult(true);
                    Notify(id);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { failure = exception.Message; }
            finally
            {
                Kill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                string detail = string.Empty;
                try { detail = await stderr.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
                bool reprocess;
                lock (gate)
                {
                    reprocess = !disposed && id == generation;
                    if (reprocess)
                    {
                        captureProcess = null;
                        captureStop?.Dispose(); captureStop = null;
                        recording = false;
                        original = pcm.ToArray();
                        if (failure != null && original.Length > 0) originalSelected = true;
                        position = 0;
                        error = failure == null ? (original.Length == 0 ? Local("Запись пуста", "Recording is empty") : string.Empty) : failure + (detail.Length > 0 ? ": " + detail : string.Empty);
                    }
                }
                ready.TrySetResult(false);
                if (reprocess)
                {
                    Notify(id);
                    if (failure == null && pcm.Count > 0) await ReprocessAsync(null, id).ConfigureAwait(false);
                }
            }
        }

        internal Task ReprocessAsync(uint config) => ReprocessAsync(config, null);

        private async Task ReprocessAsync(uint? config, int? expectedGeneration)
        {
            int id, revision;
            uint settings;
            float[] input;
            CancellationToken token;
            lock (gate)
            {
                if (disposed || (expectedGeneration.HasValue && expectedGeneration.Value != generation)) return;
                effectiveConfig = config ?? effectiveConfig;
                if (recording || original.Length == 0) return;
                settings = effectiveConfig;
                id = generation; revision = ++processGeneration;
                processStop?.Cancel(); processStop?.Dispose();
                processStop = new CancellationTokenSource(); token = processStop.Token;
                input = original;
                processing = true;
            }
            Notify(id);
            try
            {
                await Task.Delay(180, token).ConfigureAwait(false);
                await processLock.WaitAsync(token).ConfigureAwait(false);
                float[] output = new float[input.Length];
                int result, actual;
                try
                {
                    token.ThrowIfCancellationRequested();
                    (result, actual) = await Task.Run(() =>
                    {
                        int code = nr_bridge_process_ex(Encoding.UTF8.GetBytes(AppContext.BaseDirectory + "\0"), settings, input, output, input.Length, out int model);
                        return (code, model);
                    }).ConfigureAwait(false);
                }
                finally { processLock.Release(); }
                lock (gate)
                {
                    if (disposed || id != generation || revision != processGeneration || token.IsCancellationRequested) return;
                    processed = output;
                    actualModel = actual;
                    error = string.Empty;
                    if (result != 0)
                        error = actual == 1 && (settings & 8) != 0 ? Local("Точная модель недоступна, запись обработана быстрой", "The precise model is unavailable, the recording uses the fast model") : Local("Не удалось обработать запись выбранной моделью", "Could not process the recording with the selected model");
                    else if ((settings & 1) != 0 && actual != ((settings & 8) != 0 ? 2 : 1)) error = Local("Модель обработки не совпадает с выбранной", "The processing model does not match the selection");
                    processing = false;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception)
            {
                lock (gate)
                {
                    if (disposed || id != generation || revision != processGeneration) return;
                    processing = false;
                    error = Local("Не удалось обработать запись: ", "Could not process the recording: ") + exception.Message;
                }
            }
            Notify(id);
        }

        internal void TogglePlayback()
        {
            if (Playing) { StopPlayback(); return; }
            int id, revision;
            CancellationToken token;
            lock (gate)
            {
                if (disposed || recording || original.Length == 0 || (!originalSelected && processed.Length != original.Length)) return;
                id = generation; revision = ++playbackGeneration;
                playbackStop = new CancellationTokenSource(); token = playbackStop.Token;
                playbackStart = position;
                queuedFrames = 0;
                playbackClock = Stopwatch.StartNew();
                playing = true;
            }
            _ = Task.Run(() => PlaybackAsync(id, revision, token));
            Notify(id);
        }

        private async Task PlaybackAsync(int id, int revision, CancellationToken token)
        {
            using var process = new Process { StartInfo = Command("pacat", true, null) };
            string failure = null;
            Task<string> stderr = Task.FromResult(string.Empty);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new IOException(Local("Не удалось начать прослушивание", "Could not start playback"));
                stderr = ReadErrorAsync(process.StandardError);
                VoicePlayback cursor;
                lock (gate)
                {
                    if (disposed || id != generation || revision != playbackGeneration) throw new OperationCanceledException();
                    playbackProcess = process;
                    cursor = new VoicePlayback(playbackStart);
                    playbackClock.Restart();
                }
                float[] samples = new float[480];
                byte[] bytes = new byte[samples.Length * 4];
                var clock = Stopwatch.StartNew();
                long clockFrames = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    double ahead = (cursor.FramesWritten - clockFrames) / (double)Rate - clock.Elapsed.TotalSeconds;
                    if (ahead > .02) await Task.Delay(TimeSpan.FromSeconds(ahead - .01), token).ConfigureAwait(false);
                    if (ahead < -.1) { clock.Restart(); clockFrames = cursor.FramesWritten; }
                    lock (gate)
                    {
                        if (disposed || id != generation || revision != playbackGeneration) throw new OperationCanceledException();
                        cursor.Fill(original, processed, originalSelected, samples);
                    }
                    for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), samples[i]);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    await process.StandardInput.BaseStream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                    lock (gate) if (id == generation && revision == playbackGeneration) queuedFrames = cursor.FramesWritten;
                    if (cursor.FramesWritten % 2400 == 0) Notify(id);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { failure = exception is OperationCanceledException ? Local("Аудиовыход не отвечает", "The audio output is not responding") : Local("Ошибка прослушивания: ", "Playback error: ") + exception.Message; }
            finally
            {
                Kill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                string detail = string.Empty;
                try { detail = await stderr.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
                lock (gate)
                {
                    if (!disposed && id == generation && revision == playbackGeneration)
                    {
                        position = PositionFrames();
                        playing = false;
                        playbackProcess = null;
                        if (failure != null) error = failure + (detail.Length > 0 ? ": " + detail : string.Empty);
                    }
                }
                Notify(id);
            }
        }

        internal void SelectOriginal(bool value)
        {
            int id;
            lock (gate) { if (disposed || (!value && processed.Length != original.Length)) return; originalSelected = value; id = generation; }
            Notify(id);
        }

        internal void StopPlayback()
        {
            Process process;
            int id;
            lock (gate)
            {
                position = PositionFrames();
                playing = false;
                ++playbackGeneration;
                playbackStop?.Cancel(); playbackStop?.Dispose(); playbackStop = null;
                process = playbackProcess; playbackProcess = null;
                id = generation;
            }
            StopProcess(process);
            Notify(id);
        }

        private long PositionFrames()
        {
            if (!playing || original.Length == 0 || playbackClock == null) return position;
            long elapsed = Math.Max(0, (long)((playbackClock.Elapsed.TotalSeconds - .04) * Rate));
            return (playbackStart + Math.Min(queuedFrames, elapsed)) % original.Length;
        }

        internal void Clear()
        {
            Process capture, playback;
            int id;
            lock (gate)
            {
                id = ++generation;
                ++processGeneration; ++playbackGeneration;
                captureStop?.Cancel(); processStop?.Cancel(); playbackStop?.Cancel();
                captureStop?.Dispose(); processStop?.Dispose(); playbackStop?.Dispose();
                captureStop = processStop = playbackStop = null;
                capture = captureProcess; playback = playbackProcess;
                captureProcess = playbackProcess = null;
                original = processed = livePeaks = Array.Empty<float>();
                recording = playing = processing = originalSelected = false;
                actualModel = 0; position = 0; deviceId = null; error = string.Empty;
            }
            StopProcess(capture); StopProcess(playback);
            Notify(id);
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Clear();
        }

        private void Notify(int id)
        {
            dispatch(() =>
            {
                lock (gate) if (disposed || id != generation) return;
                Changed?.Invoke();
            });
        }

        private static ProcessStartInfo Command(string command, bool playback, string source)
        {
            var info = new ProcessStartInfo(command)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = playback, RedirectStandardOutput = !playback, RedirectStandardError = true,
            };
            foreach (string argument in new[] { "--raw", "--format=float32le", "--channels=1", "--rate=48000", "--latency-msec=30", "--process-time-msec=10", "--client-name=Noise Reduction", "--stream-name=Voice Test", "--property=application.id=org.noise-reduction.voice-test" })
                info.ArgumentList.Add(argument);
            if (playback) info.ArgumentList.Add("--playback"); else info.ArgumentList.Add("--device=" + source);
            return info;
        }

        private static async Task<string> ReadErrorAsync(StreamReader reader)
        {
            char[] buffer = new char[512];
            var result = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                if (result.Length < 2048) result.Append(buffer, 0, Math.Min(count, 2048 - result.Length));
            return result.ToString().Trim();
        }

        private static void StopProcess(Process process) { if (process != null) _ = Task.Run(() => Kill(process)); }
        private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        internal static string Local(string russian, string english) => UiText.EffectiveLanguage == "ru" ? russian : english;
    }
}
