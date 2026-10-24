#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.Media.Core;
using Windows.Media.Playback;
using NoiseReduction.Desktop;
using NoiseReduction.Platform;
using Activity = NoiseReduction.Desktop.Activity;

namespace NoiseReduction.WinUI
{
    internal enum MicrophoneState { Paused, Off, Detached, Error, Active, Waiting }

    internal sealed class DesktopController : IDisposable
    {
        private sealed class Sample
        {
            internal long Errors, Models;
            internal bool Failed, Observed;
            internal readonly Activity Activity = new Activity();
            internal void Observe(ControlFile control)
            {
                long errors = control.Errors, models = control.ModelFrames;
                if (!Observed) Failed = errors > 0;
                else if (errors > Errors) Failed = true;
                else if (errors < Errors || models > Models) Failed = false;
                Errors = errors; Models = models; Observed = true;
                Activity.Update(control.Frames, models);
            }
        }

        private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoiseReduction", "settings.json");
        private readonly Dictionary<string, ControlFile> controls = new Dictionary<string, ControlFile>();
        private readonly Dictionary<string, Sample> samples = new Dictionary<string, Sample>();
        private readonly Dictionary<string, List<string>> usage = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, string> notes = new Dictionary<string, string>();
        private readonly DesktopState state = new DesktopState();
        private MediaPlayer original, processed;
        private volatile bool stopRequested;
        private readonly List<float> livePeaks = new List<float>();
        private float bucketPeak;
        private int bucketCount;
        private readonly DispatcherQueueTimer timer;
        private List<DeviceInfo> devices = new List<DeviceInfo>();
        private DateTime refreshAt;
        private readonly List<DeviceInfo> pendingNew = new List<DeviceInfo>();
        private string recordingDirectory, comparisonId;
        private bool disposed, scanning;
        internal Settings Settings { get; }
        public event Action Changed;
        public event Action<DeviceInfo> NewMicrophone;
        public IReadOnlyList<DeviceInfo> Devices => devices.AsReadOnly();
        public string DefaultId { get; private set; }
        public string RecordingId => state.CaptureEndpoint;
        public string IntegratingId { get; private set; }
        public DateTime RecordingStarted { get; private set; }
        public bool Playing { get; private set; }
        public bool ListenProcessed { get; private set; } = true;
        public float[] OriginalPeaks { get; private set; }
        public float[] ProcessedPeaks { get; private set; }
        public float[] LivePeaks { get { lock (livePeaks) return livePeaks.ToArray(); } }
        public double PlaybackProgress
        {
            get
            {
                if (!Playing || original == null) return -1;
                var session = original.PlaybackSession;
                double total = session.NaturalDuration.TotalSeconds;
                return total > 0 ? session.Position.TotalSeconds / total : 0;
            }
        }
        public bool WatchUsage { get; set; }
        public bool GlobalEnabled => Settings.Enabled;
        public bool Busy => !state.CanRefresh;
        public bool StartupEnabled { get; private set; }
        public bool EnableNewMicrophones => Settings.EnableNewMicrophones;
        public string Language => Settings.Language;
        public string Error { get; private set; }
        public string RefreshError => state.RefreshError;

        internal DesktopController()
        {
            Settings = NoiseReduction.Desktop.Settings.Load(settingsPath);
            UiText.Language = Settings.Language;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    StartupEnabled = key != null && key.GetValue("NoiseReduction") != null;
            }
            catch (Exception error) { Error = error.Message; }
            Refresh();
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null) throw new InvalidOperationException("A UI dispatcher is required");
            timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += Tick;
            timer.Start();
        }

        private void Tick(DispatcherQueueTimer sender, object args)
        {
            if (disposed) return;
            if (Busy) { if (RecordingId != null) Notify(); return; }
            if (DateTime.UtcNow >= refreshAt) Refresh();
            else
            {
                try { foreach (var pair in controls) samples[pair.Key].Observe(pair.Value); }
                catch (Exception error) { Error = error.Message; }
                Notify();
            }
        }

        public bool IsEnabled(string id) => Settings.Enabled && Settings.For(id).Enabled;
        public bool ProfileEnabled(string id) => Settings.For(id).Enabled;
        public int Attenuation(string id) => Settings.For(id).Attenuation;
        public string Note(string id) => notes.TryGetValue(id, out var note) ? note : null;
        public IReadOnlyList<string> Usage(string id) => usage.TryGetValue(id, out var names) ? names : null;
        public bool CanCompare(string id) => !disposed && !Busy && comparisonId == id && recordingDirectory != null && File.Exists(Path.Combine(recordingDirectory, "original.wav")) && File.Exists(Path.Combine(recordingDirectory, "processed.wav"));

        public bool HasProcessingError(string id)
        {
            var device = devices.Find(value => value.Id == id);
            return state.RefreshError != null || device != null && device.Attached && IsEnabled(id) && (!device.EnhancementsEnabled || samples.TryGetValue(id, out var sample) && sample.Failed);
        }
        public MicrophoneState StateOf(string id)
        {
            var device = devices.Find(value => value.Id == id);
            if (!Settings.Enabled) return MicrophoneState.Paused;
            if (!Settings.For(id).Enabled) return MicrophoneState.Off;
            if (HasProcessingError(id)) return MicrophoneState.Error;
            if (device == null || !device.Attached) return MicrophoneState.Detached;
            return samples.TryGetValue(id, out var sample) && sample.Activity.Active ? MicrophoneState.Active : MicrophoneState.Waiting;
        }

        public void SetGlobal(bool enabled) { Change(() => Settings.Enabled = enabled); }
        public void SetProfile(string id, bool enabled) { Change(() => { Settings.For(id).Enabled = enabled; if (enabled) Settings.Enabled = true; }); }
        public void SetAttenuation(string id, int attenuation) { Change(() => Settings.For(id).Attenuation = Math.Clamp(attenuation, 0, 60)); }
        public bool LowCut(string id) => Settings.For(id).LowCut;
        public void SetLowCut(string id, bool enabled) { Change(() => Settings.For(id).LowCut = enabled); }
        public bool Economy(string id) => Settings.For(id).Economy;
        public void SetEconomy(string id, bool enabled) { Change(() => Settings.For(id).Economy = enabled); }
        public void ResetProfile(string id) { SetAttenuation(id, 24); }
        public void ToggleMicrophone(string id) { if (devices.Any(device => device.Id == id)) SetProfile(id, !IsEnabled(id)); }
        // до подписки трея новые микрофоны копятся, иначе подключённые до запуска потеряются
        public void AnnounceNewMicrophones()
        {
            if (NewMicrophone == null) return;
            var ready = pendingNew.ToList(); pendingNew.Clear();
            foreach (var device in ready) NewMicrophone(device);
        }
        public void SetEnableNewMicrophones(bool enabled) { Change(() => Settings.EnableNewMicrophones = enabled); }
        public void SetLanguage(string language)
        {
            Change(() => { Settings.Language = LanguagePreference.Normalize(language); UiText.Language = Settings.Language; });
        }
        public void SetStartup(bool enabled)
        {
            if (disposed) return;
            Error = null;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (enabled) key.SetValue("NoiseReduction", "\"" + Environment.ProcessPath + "\" --tray");
                    else key.DeleteValue("NoiseReduction", false);
                }
                StartupEnabled = enabled;
            }
            catch (Exception error) { Error = error.Message; }
            Notify();
        }
        private void Change(Action change)
        {
            if (Busy || disposed) return;
            Error = null;
            try { change(); Settings.Save(settingsPath); ApplyControls(false); }
            catch (Exception error) { Error = error.Message; }
            Notify();
        }

        public void Refresh()
        {
            if (Busy || disposed) return;
            refreshAt = DateTime.UtcNow.AddSeconds(3);
            var announce = new List<DeviceInfo>();
            try
            {
                var next = Integration.EnumerateMicrophones().FindAll(device => device.Active);
                string nextDefault = Integration.GetDefaultMicrophoneId();
                foreach (var device in next)
                    if (!controls.ContainsKey(device.Id))
                    {
                        controls.Add(device.Id, new ControlFile(Integration.ControlRoot, device.Guid));
                        samples.Add(device.Id, new Sample());
                    }
                // при самом первом запуске все микрофоны новые, спрашивать про каждый не нужно
                bool known = Settings.Profiles.Count > 0;
                var fresh = next.FindAll(device => !Settings.Has(device.Id));
                if (fresh.Count > 0) { foreach (var device in fresh) Settings.For(device.Id); Settings.Save(settingsPath); }
                if (known && Settings.EnableNewMicrophones) announce.AddRange(fresh.FindAll(device => !device.Attached));
                devices = next; DefaultId = nextDefault;
                state.SetPresent(devices.Select(device => device.Id));
                ApplyControls(false);
                foreach (var pair in controls) samples[pair.Key].Observe(pair.Value);
            }
            catch (Exception error) { state.RefreshError = "Не удалось обновить микрофоны: " + error.Message; }
            pendingNew.AddRange(announce);
            AnnounceNewMicrophones();
            if (WatchUsage) _ = ScanUsageAsync();
            Notify();
        }

        // WASAPI сессии читаются только из MTA, поэтому опрос идёт в пуле потоков
        public async Task ScanUsageAsync()
        {
            if (scanning || disposed) return;
            scanning = true;
            try
            {
                var ids = devices.Select(device => device.Id).ToList();
                var result = await Task.Run(() => ids.ToDictionary(id => id, id => CaptureSessions.TryGetSnapshot(id)));
                if (disposed) return;
                usage.Clear();
                foreach (var pair in result)
                    if (pair.Value.Available)
                        usage[pair.Key] = pair.Value.Sessions.Where(session => session.ProcessId != Environment.ProcessId).Select(session => AppName(session)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Notify();
            }
            finally { scanning = false; }
        }
        private static string AppName(CaptureSessionInfo session)
        {
            if (session.IsSystemSession) return UiText.Get("Система");
            string name = session.ApplicationName;
            switch (name.ToLowerInvariant())
            {
                case "chrome": return "Chrome";
                case "msedge": return "Edge";
                case "firefox": return "Firefox";
                case "telegram": return "Telegram";
                case "discord": return "Discord";
                case "ms-teams": return "Teams";
                case "zoom": return "Zoom";
                case "soundrecorder": case "voicerecorder": return UiText.Get("Запись звука");
            }
            return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name.Substring(1) : name;
        }

        private void ApplyControls(bool bypass)
        {
            Exception failure = null;
            foreach (var pair in controls)
            {
                var profile = Settings.For(pair.Key);
                try { pair.Value.Set(state.Enabled(pair.Key, Settings.Enabled && !bypass, profile.Enabled), profile.Attenuation, profile.LowCut, profile.Economy); }
                catch (Exception error) { if (!bypass) throw; failure ??= error; }
            }
            if (failure != null) throw failure;
        }

        public async Task IntegrateAsync(string id, bool attach)
        {
            if (Busy || disposed || !devices.Any(device => device.Id == id)) return;
            bool succeeded = false;
            state.Integrating = true; IntegratingId = id; Error = null;
            notes[id] = attach ? "Windows проверяет обработку, звук не сохраняется и не воспроизводится" : "Отключение эффекта"; Notify();
            try
            {
                if (id.Contains('"')) throw new InvalidOperationException("Некорректный ID микрофона");
                string helper = Path.Combine(AppContext.BaseDirectory, "NoiseReduction.Engine.exe");
                if (!File.Exists(helper)) throw new FileNotFoundException("Engine helper is missing", helper);
                using (var probe = attach ? new ProbeConfiguration(controls[id]) : null)
                using (var process = Process.Start(new ProcessStartInfo(helper, (attach ? "--attach" : "--detach") + " \"" + id + "\"") { UseShellExecute = true, Verb = "runas" }))
                {
                    if (process == null) throw new InvalidOperationException("Cannot start engine helper");
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0) throw new InvalidOperationException("Команда не завершена, проверьте сообщение проверки подключения");
                }
                succeeded = true;
            }
            catch (Exception error) { if (!disposed) Error = error.Message; }
            finally
            {
                state.Integrating = false; IntegratingId = null;
                if (!disposed)
                {
                    notes.Remove(id);
                    Refresh();
                    if (succeeded) notes[id] = attach ? "Работа модели подтверждена, подключение сохранено" : "Эффект отключён";
                    Notify();
                }
            }
        }

        public async Task RecordAsync(string id)
        {
            var device = devices.Find(value => value.Id == id);
            if (Busy || disposed || device == null) return;
            int attenuation = Attenuation(id);
            StopPlayback(); state.CaptureEndpoint = id; comparisonId = null; Error = null;
            stopRequested = false; RecordingStarted = DateTime.UtcNow;
            lock (livePeaks) livePeaks.Clear();
            bucketPeak = 0; bucketCount = 0; OriginalPeaks = ProcessedPeaks = null;
            string directory = Path.Combine(Path.GetTempPath(), "NoiseReduction-" + Guid.NewGuid().ToString("N"));
            notes[id] = "Говорите в микрофон и нажмите Остановить"; Notify();
            try
            {
                Directory.CreateDirectory(directory); ApplyControls(false);
                var result = await Task.Run(() => Comparison.Create(id, directory, attenuation, () => stopRequested, Collect));
                if (disposed) { DeleteRecording(directory); return; }
                OriginalPeaks = Peaks(result[0], 240); ProcessedPeaks = Peaks(result[1], 240);
                DeleteRecording(recordingDirectory); recordingDirectory = directory; comparisonId = id;
                notes.Remove(id);
            }
            catch (Exception error) { if (!disposed) { Error = error.Message; notes.Remove(id); } DeleteRecording(directory); }
            finally
            {
                state.CaptureEndpoint = null;
                if (!disposed) { try { ApplyControls(false); } catch (Exception error) { Error = error.Message; } Notify(); }
            }
        }
        public void StopRecording() { stopRequested = true; }

        // пик каждых 20 мс для живой волны, вызывается из потока записи
        private void Collect(float[] packet)
        {
            foreach (float sample in packet)
            {
                bucketPeak = Math.Max(bucketPeak, Math.Abs(sample));
                if (++bucketCount < 960) continue;
                lock (livePeaks) livePeaks.Add(bucketPeak);
                bucketPeak = 0; bucketCount = 0;
            }
        }
        private static float[] Peaks(float[] samples, int count)
        {
            var peaks = new float[count];
            for (int i = 0; i < samples.Length; i++)
            {
                int bucket = (int)((long)i * count / samples.Length);
                peaks[bucket] = Math.Max(peaks[bucket], Math.Abs(samples[i]));
            }
            return peaks;
        }

        // оба варианта играют одновременно и по кругу, переключение просто меняет, какой слышно
        public void TogglePlayback(string id)
        {
            if (Playing) { StopPlayback(); Notify(); return; }
            if (!CanCompare(id)) return;
            Error = null;
            try
            {
                original = Open("original.wav"); processed = Open("processed.wav");
                ApplyListen();
                original.Play(); processed.Play();
                Playing = true;
            }
            catch (Exception error) { StopPlayback(); Error = error.Message; }
            Notify();
        }
        public void SetListenProcessed(bool value) { ListenProcessed = value; ApplyListen(); Notify(); }
        private MediaPlayer Open(string name)
        {
            var player = new MediaPlayer { IsLoopingEnabled = true, AudioCategory = MediaPlayerAudioCategory.Media };
            player.Source = MediaSource.CreateFromUri(new Uri(Path.Combine(recordingDirectory, name)));
            return player;
        }
        private void ApplyListen()
        {
            if (original != null) original.IsMuted = ListenProcessed;
            if (processed != null) processed.IsMuted = !ListenProcessed;
        }
        private void StopPlayback()
        {
            Playing = false;
            foreach (var player in new[] { original, processed }) if (player != null) { try { player.Pause(); } catch (Exception) { } player.Dispose(); }
            original = processed = null;
        }
        public void Stop() { if (!disposed) { StopPlayback(); Notify(); } }
        public string BuildDiagnostics(string id)
        {
            var device = devices.Find(value => value.Id == id);
            if (device == null) return UiText.Get("Микрофон не выбран");
            return UiText.Get("Диагностика") + "\n" + device.Name + "\n" + device.Id + "\nRNNoise\n" + UiText.Get("Формат движка: 48 kHz") + "\n" + UiText.Get("Задержка движка: 30 ms") + "\n" + StateOf(id) + (device.Attached ? ", attached" : ", detached") + (device.EnhancementsEnabled ? "" : ", enhancements off");
        }
        private static void DeleteRecording(string directory)
        {
            if (directory == null || !Directory.Exists(directory)) return;
            try
            {
                string path = Path.GetFullPath(directory);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("NoiseReduction-", StringComparison.Ordinal) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                foreach (string name in new[] { "original.wav", "processed.wav" }) File.Delete(Path.Combine(path, name));
                if (Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private void Notify() { if (!disposed) Changed?.Invoke(); }
        public void Dispose()
        {
            if (disposed) return;
            timer.Stop(); timer.Tick -= Tick;
            try { ApplyControls(true); }
            finally
            {
                disposed = true;
                StopPlayback();
                foreach (var control in controls.Values) control.Dispose();
                controls.Clear(); DeleteRecording(recordingDirectory);
            }
        }
    }
}
