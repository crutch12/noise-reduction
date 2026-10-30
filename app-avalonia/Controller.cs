using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{
    internal enum State { Paused, Idle, Working, Error }

    // как и на Windows, микрофон занимаем только пока им пользуется какая-то программа
    internal sealed class Controller : IDisposable
    {
        private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoiseReduction", "settings.json");
        private readonly Bridge bridge = new();
        private readonly DispatcherTimer timer;
        private Process watcher;
        private bool inUse, prepared;
        private DateTime idleSince = DateTime.MaxValue, retryAt;
        private ulong lastModel, lastErrors;
        private List<Bridge.Device> devices = new();

        internal Settings Settings { get; }
        internal State State { get; private set; } = State.Idle;
        internal string Error { get; private set; }
        internal IReadOnlyList<Bridge.Device> Devices => devices;
        internal event Action Changed;

        internal Controller()
        {
            Settings = Settings.Load(settingsPath);
            UiText.Language = Settings.Language;
            Refresh();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => Tick();
            timer.Start();
            WatchLinux();
            Tick();
        }

        private string Key => "source:" + (Settings.Source ?? "default");
        internal Profile Profile => Settings.For(Key);

        internal void Refresh()
        {
            try { devices = Bridge.Inputs(); }
            catch (Exception error) { Error = error.Message; }
            Changed?.Invoke();
        }

        internal void Change(Action change)
        {
            change();
            Settings.Save(settingsPath);
            UiText.Language = Settings.Language;
            Apply();
            Changed?.Invoke();
        }

        internal void SetSource(string source)
        {
            Change(() => Settings.Source = source);
            // другой микрофон: мост перезапустится на следующем такте
            bridge.Stop();
        }

        private void Apply()
        {
            var profile = Profile;
            bridge.Configure(Settings.Enabled && profile.Enabled, profile.Attenuation, profile.LowCut, profile.DeepFilter);
        }

        private void Tick()
        {
            if (!prepared)
            {
                prepared = VirtualMicrophone.Prepare(out var error);
                if (!prepared)
                {
                    State = State.Error;
                    Error = error;
                    Changed?.Invoke();
                    return;
                }
            }
            // после того как программа отпустила микрофон, ещё немного держим мост, чтобы не дёргать устройство
            if (inUse) idleSince = DateTime.MaxValue;
            else if (idleSince == DateTime.MaxValue) idleSince = DateTime.UtcNow;
            bool want = inUse || DateTime.UtcNow - idleSince < TimeSpan.FromSeconds(3);
            if (want && !bridge.Running && DateTime.UtcNow >= retryAt)
            {
                string source = devices.Any(device => device.Name == Settings.Source) ? Settings.Source : null;
                if (bridge.Start(source, VirtualMicrophone.Feed)) { Apply(); Error = null; lastModel = 0; lastErrors = 0; }
                else { Error = UiText.Get("Не удалось открыть микрофон"); retryAt = DateTime.UtcNow.AddSeconds(5); }
            }
            else if (!want && bridge.Running) bridge.Stop();
            var stats = bridge.Stats();
            var next = !Settings.Enabled || !Profile.Enabled ? State.Paused
                : Error != null || stats.Errors > lastErrors ? State.Error
                : bridge.Running && stats.ModelFrames > lastModel ? State.Working
                : State.Idle;
            lastModel = stats.ModelFrames;
            if (stats.Errors > lastErrors && Error == null) Error = UiText.Get("Ошибка обработки");
            lastErrors = stats.Errors;
            if (next != State) { State = next; Changed?.Invoke(); }
        }

        // pactl subscribe сообщает о новых и закрытых потоках записи, сам список проверяем отдельно
        private void WatchLinux()
        {
            try
            {
                watcher = Process.Start(new ProcessStartInfo("pactl", "subscribe") { RedirectStandardOutput = true, UseShellExecute = false });
                watcher.OutputDataReceived += (_, line) =>
                {
                    if (line.Data != null && line.Data.Contains("source-output")) Dispatcher.UIThread.Post(CheckLinux);
                };
                watcher.BeginOutputReadLine();
            }
            catch (Exception) { watcher = null; }
            CheckLinux();
        }

        private void CheckLinux()
        {
            string source = Pactl("list", "short", "sources").Split('\n').Select(line => line.Split('\t')).FirstOrDefault(parts => parts.Length > 1 && parts[1] == "noise_reduction")?[0];
            inUse = source != null && Pactl("list", "short", "source-outputs").Split('\n').Any(line => line.Split('\t') is { Length: > 1 } parts && parts[1] == source);
            Tick();
        }

        private static string Pactl(params string[] arguments)
        {
            try
            {
                var start = new ProcessStartInfo("pactl") { RedirectStandardOutput = true, UseShellExecute = false };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output;
            }
            catch (Exception) { return ""; }
        }

        public void Dispose()
        {
            timer.Stop();
            try { watcher?.Kill(); } catch (Exception) { }
            bridge.Dispose();
            VirtualMicrophone.Release();
        }
    }
}
