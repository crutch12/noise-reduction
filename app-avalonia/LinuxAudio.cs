using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{

    internal sealed record PulseSource(uint Index, string Name, string Description, string StableId, bool Physical, uint? OwnerModule, Dictionary<string, string> Properties);
    internal sealed record PulseModule(uint Index, string Name, string Arguments);
    internal sealed record CommandResult(int ExitCode, string Output, string Error);

    internal sealed class LinuxAudio
    {
        internal const string OwnerProperty = "noise-reduction.owner";
        internal const string WorkerId = "org.noise-reduction.bridge";
        private bool? jsonSupported;
        internal string WatcherError { get; private set; }

        internal async Task<(IReadOnlyList<Microphone> Devices, IReadOnlyList<RecordingApp> Apps, string DefaultSource)> SnapshotAsync(CancellationToken cancellation = default)
        {
            var sourceQuery = ListAsync("sources", cancellation);
            var streamQuery = ListAsync("source-outputs", cancellation);
            var defaultQuery = DefaultAsync(cancellation);
            await Task.WhenAll(sourceQuery, streamQuery, defaultQuery);
            ISet<string> splitSerials = null;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoiseReduction", "linux.json");
                splitSerials = LinuxPreferences.Load(path).SplitSerials;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException) { }
            var sources = ParseSources(await sourceQuery, splitSerials);
            string selected = await defaultQuery;
            var microphones = sources.Where(source => source.Physical).Select(source =>
                new Microphone(source.StableId, source.Description, source.Name, source.Name == selected)).ToArray();
            return (microphones, ParseStreams(await streamQuery, sources), selected);
        }

        internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

        internal static List<PulseSource> ParseSources(string text, ISet<string> splitSerials = null)
        {
            var result = new List<PulseSource>();
            foreach (var item in Objects(text))
            {
                string name = Get(item, "name"), description = Get(item, "description");
                var props = Properties(item);
                string serial = Value(props, "device.serial");
                string identity = Value(props, "device.bus_path", "api.alsa.path", "bluez.path");
                string port = Value(props, "device.bus_path", "sysfs.path", "object.path");
                string profile = Value(props, "device.profile.name", "alsa.card_name");
                if (String.IsNullOrEmpty(profile) && name.Contains('.')) profile = name[(name.LastIndexOf('.') + 1)..];
                if (serial.Length > 0) identity = serial;
                else if (String.IsNullOrEmpty(identity)) identity = name;
                else identity += "|" + port;
                string monitor = Get(item, "monitor_of_sink");
                if (monitor.Length == 0) monitor = Get(item, "monitor_source");
                bool monitored = monitor.Length > 0 && monitor != "null" && monitor != "4294967295" && monitor != "n/a";
                bool owned = props.ContainsKey(OwnerProperty) || name == "noise_reduction" || name == "noise_reduction_feed.monitor";
                string deviceClass = Value(props, "device.class");
                string virtualDevice = Value(props, "device.virtual", "node.virtual");
                bool physical = !owned && !monitored && deviceClass != "monitor" && deviceClass != "filter" && deviceClass != "abstract" && !Boolean(virtualDevice);
                uint? ownerModule = UInt(Get(item, "owner_module"));
                result.Add(new PulseSource(UInt(Get(item, "index")) ?? 0, name, description.Length == 0 ? name : description,
                    "pulse:" + Hash(identity + "|" + profile), physical, ownerModule, props));
            }
            foreach (var group in result.Where(source => source.Physical && Value(source.Properties, "device.serial").Length > 0)
                .GroupBy(source => source.StableId).ToList())
            {
                bool duplicate = group.Count() > 1;
                if (duplicate) splitSerials?.Add(group.Key);
                if (!duplicate && splitSerials?.Contains(group.Key) != true) continue;
                foreach (var source in group)
                {
                    string port = Value(source.Properties, "device.bus_path", "sysfs.path", "object.path");
                    if (port.Length == 0) port = source.Name;
                    result[result.IndexOf(source)] = source with { StableId = "pulse:" + Hash(group.Key + "|" + port) };
                }
            }
            return result;
        }

        internal static List<RecordingApp> ParseStreams(string text, IReadOnlyList<PulseSource> sources)
        {
            var result = new List<RecordingApp>();
            var ownModules = sources.Where(source => source.OwnerModule.HasValue &&
                (source.Properties.ContainsKey(OwnerProperty) || (source.Name == VirtualMicrophone.Alias &&
                    Value(source.Properties, "device.master_device") == VirtualMicrophone.Feed + ".monitor")))
                .Select(source => source.OwnerModule.Value).ToHashSet();
            foreach (var item in Objects(text))
            {
                uint? ownerModule = UInt(Get(item, "owner_module"));
                if (ownerModule.HasValue && ownModules.Contains(ownerModule.Value)) continue;
                var props = Properties(item);
                string appId = Value(props, "application.id", "application.desktop", "application.desktop_entry", "flatpak.app-id");
                if (props.ContainsKey(OwnerProperty) || appId == WorkerId || appId == "org.noise-reduction.voice-test" ||
                    Value(props, "application.process.id") == Environment.ProcessId.ToString(CultureInfo.InvariantCulture)) continue;
                uint client = UInt(Get(item, "client")) ?? 0;
                if (appId.Length == 0)
                {
                    string binary = Value(props, "application.process.binary");
                    appId = binary.Length == 0 ? "unknown:" + client : "binary:" + binary;
                }
                string name = Value(props, "application.name", "media.name", "application.process.binary");
                if (name.Length == 0) name = "Unknown application (#" + client + ")";
                uint sourceIndex = UInt(Get(item, "source")) ?? UInt32.MaxValue;
                var source = sources.FirstOrDefault(source => source.Index == sourceIndex);
                result.Add(new RecordingApp(UInt(Get(item, "index")) ?? 0, appId, name, Value(props, "application.icon_name", "application.desktop"),
                    source?.Name ?? Get(item, "source"), Boolean(Get(item, "corked")), Boolean(Get(item, "mute")), client));
            }
            return result;
        }

        internal static List<PulseModule> ParseModules(string text)
        {
            return Objects(text).Select(item => new PulseModule(UInt(Get(item, "index"))
                ?? throw new InvalidDataException("Module identifiers are missing; use the text module list"), Get(item, "name"), Get(item, "argument"))).ToList();
        }

        internal static HashSet<string> Demand(IEnumerable<Microphone> devices, IEnumerable<RecordingApp> apps, string selected)
        {
            var active = apps.Where(app => !app.Corked).Select(app => app.SourceName).ToHashSet(StringComparer.Ordinal);
            return devices.Where(device => device.Available && (active.Contains(VirtualMicrophone.SourceFor(device.Id)) ||
                (device.Id == selected && active.Contains(VirtualMicrophone.Alias)))).Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        }

        internal async Task<string> ListAsync(string kind, CancellationToken cancellation)
        {
            // PulseAudio 16 JSON omits module IDs, which are required for ownership checks
            if (kind == "modules") return await RequireAsync(cancellation, "list", "modules");
            if (jsonSupported != false)
            {
                var json = await RunAsync(cancellation, "--format=json", "list", kind);
                if (json.ExitCode == 0)
                {
                    try { using var document = JsonDocument.Parse(json.Output); jsonSupported = true; return json.Output; }
                    catch (JsonException) { }
                }
                else if (json.Error.Contains("format", StringComparison.OrdinalIgnoreCase) || json.Error.Contains("option", StringComparison.OrdinalIgnoreCase)) jsonSupported = false;
                else throw new IOException("pactl " + kind + ": " + json.Error);
            }
            return await RequireAsync(cancellation, "list", kind);
        }

        internal async Task<string> DefaultAsync(CancellationToken cancellation)
        {
            string info = await RequireAsync(cancellation, "info");
            foreach (var line in info.Split('\n'))
                if (line.StartsWith("Default Source:", StringComparison.Ordinal)) return line[15..].Trim();
            throw new IOException("PulseAudio did not report a default source");
        }

        internal async Task<string> RequireAsync(CancellationToken cancellation, params string[] arguments)
        {
            var result = await RunAsync(cancellation, arguments);
            if (result.ExitCode != 0) throw new IOException("pactl " + String.Join(' ', arguments.Take(2)) + ": " + result.Error);
            return result.Output;
        }

        internal static async Task<CommandResult> RunAsync(CancellationToken cancellation, params string[] arguments)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var start = StartInfo("pactl", arguments);
            using var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new IOException("Could not start pactl");
                Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return new CommandResult(process.ExitCode, (await output).Trim(), (await error).Trim());
            }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 2)
            {
                throw MissingPactl(error);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException("pactl timed out after 4 seconds");
            }
            finally { if (cancellation.IsCancellationRequested) TryKill(process); }
        }

        internal async Task WatchAsync(Action signal, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using var watcher = new Process { StartInfo = StartInfo("pactl", new[] { "subscribe" }) };
                    watcher.Start();
                    WatcherError = null;
                    Task<string> errors = watcher.StandardError.ReadToEndAsync(cancellation);
                    try
                    {
                        string line;
                        while ((line = await watcher.StandardOutput.ReadLineAsync(cancellation)) != null)
                            if (line.Contains("source", StringComparison.Ordinal) || line.Contains("server", StringComparison.Ordinal) || line.Contains("module", StringComparison.Ordinal) || line.Contains("client", StringComparison.Ordinal)) signal();
                        await watcher.WaitForExitAsync(cancellation);
                        WatcherError = "Event connection ended: " + (await errors).Trim();
                    }
                    finally { TryKill(watcher); }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    WatcherError = error is System.ComponentModel.Win32Exception { NativeErrorCode: 2 } missing ? MissingPactl(missing).Message : error.Message;
                }
                signal();
                try { await Task.Delay(1500, cancellation); } catch (OperationCanceledException) { break; }
            }
        }

        private static ProcessStartInfo StartInfo(string program, IEnumerable<string> arguments)
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.Environment["LC_ALL"] = "C";
            start.Environment["LANG"] = "C";
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            return start;
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }

        private static List<Dictionary<string, object>> Objects(string text)
        {
            var result = new List<Dictionary<string, object>>();
            if (text.TrimStart().StartsWith('['))
            {
                using var document = JsonDocument.Parse(text);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var values = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var property in item.EnumerateObject())
                    {
                        if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
                        {
                            var props = new Dictionary<string, string>(StringComparer.Ordinal);
                            foreach (var value in property.Value.EnumerateObject()) props[value.Name] = value.Value.ToString();
                            values["properties"] = props;
                        }
                        else values[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? "null" : property.Value.ToString();
                    }
                    result.Add(values);
                }
                return result;
            }
            Dictionary<string, object> current = null;
            bool properties = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (raw.Length > 0 && !Char.IsWhiteSpace(raw[0]) && line.Contains('#'))
                {
                    current = new Dictionary<string, object>(StringComparer.Ordinal) { ["index"] = line[(line.LastIndexOf('#') + 1)..], ["properties"] = new Dictionary<string, string>(StringComparer.Ordinal) };
                    result.Add(current);
                    properties = false;
                    continue;
                }
                if (current == null) continue;
                if (line == "Properties:") { properties = true; continue; }
                int equals = line.IndexOf(" = ", StringComparison.Ordinal);
                if (properties && equals > 0)
                {
                    string value = line[(equals + 3)..].Trim();
                    if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
                    ((Dictionary<string, string>)current["properties"])[line[..equals]] = value;
                    continue;
                }
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                properties = false;
                string key = line[..colon].ToLowerInvariant().Replace(' ', '_');
                if (key == "monitor_of_sink" && line[(colon + 1)..].Trim() == "n/a") current[key] = "null";
                else current[key] = line[(colon + 1)..].Trim();
            }
            return result;
        }

        private static string Get(Dictionary<string, object> values, string key) => values.TryGetValue(key, out var value) ? value.ToString() : "";
        private static Dictionary<string, string> Properties(Dictionary<string, object> values) => values.TryGetValue("properties", out var value) ? (Dictionary<string, string>)value : new(StringComparer.Ordinal);
        private static uint? UInt(string text) => UInt32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value) && value != UInt32.MaxValue ? value : null;
        private static bool Boolean(string text) => String.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "yes" || text == "1";
        internal static string Value(Dictionary<string, string> properties, params string[] keys)
        {
            foreach (string key in keys) if (properties.TryGetValue(key, out string value) && !String.IsNullOrWhiteSpace(value)) return value;
            return "";
        }

        internal static bool ConnectionFailure(Exception error) => error is TimeoutException ||
            error.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) || error is OperationCanceledException ||
            error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 };

        private static IOException MissingPactl(System.ComponentModel.Win32Exception error)
        {
            string message = UiText.EffectiveLanguage == "ru"
                ? "Не найден pactl, установите пакет pulseaudio-utils\nDebian/Ubuntu: sudo apt install pulseaudio-utils"
                : "pactl is missing, install the pulseaudio-utils package\nDebian/Ubuntu: sudo apt install pulseaudio-utils";
            return new IOException(message, error);
        }
    }
}
