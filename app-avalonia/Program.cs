using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace NoiseReduction.Avalonia
{
    internal static class Program
    {
        private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static SingleInstance instance;
        private static Controller controller;
        private static int quitting;
        internal static Action QuitRequested;

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Contains("--help") || args.Contains("-h"))
                {
                    Console.WriteLine("Noise Reduction [--tray|--headless]\nCommands: status, devices, apps, diagnostics, on, off, toggle, source <id|default>, profile <id> [--model fast|precise] [--strength 0..60] [--enabled on|off] [--low-cut on|off], default, route <stream> <source> [--remember], mute <stream> <on|off> [--remember], rules [--remove <app-id>], show, quit\nUse --json for structured output");
                    return 0;
                }
                if (args.Contains("--install")) return Installation.Install();
                if (args.Contains("--uninstall")) return Installation.Uninstall();
                bool command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal);
                if (command && args[0] is "status" or "devices" or "apps" or "diagnostics")
                {
                    string response = SingleInstance.SendAsync(args, missingAllowed: true).GetAwaiter().GetResult();
                    if (response == null) return StandaloneAsync(args).GetAwaiter().GetResult();
                    Console.WriteLine(response);
                    using var value = JsonDocument.Parse(response);
                    return value.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean() ? 0 : 1;
                }
                using var lease = new SingleInstance();
                instance = lease;
                if (!lease.IsOwner)
                {
                    string response = SingleInstance.SendAsync(command ? args : new[] { "show" }).GetAwaiter().GetResult();
                    if (command) Console.WriteLine(response);
                    using var value = JsonDocument.Parse(response);
                    return value.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean() ? 0 : 1;
                }
                if (command) return StandaloneAsync(args).GetAwaiter().GetResult();
                if (args.Contains("--headless")) return HeadlessAsync().GetAwaiter().GetResult();
                App.StartHidden = args.Contains("--tray");
                return AppBuilder.Configure<App>().With(AppTheme.FontOptions).UsePlatformDetect().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error.Message }, Json));
                return 1;
            }
        }

        internal static void Attach(Controller owner)
        {
            controller = owner;
            instance?.Start(HandleAsync, () =>
            {
                if (Interlocked.Exchange(ref quitting, 0) == 0) return;
                stopped.TrySetResult();
                QuitRequested?.Invoke();
            });
        }

        private static async Task<int> HeadlessAsync()
        {
            using var owner = new Controller();
            Attach(owner);
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stopped.TrySetResult(); });
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stopped.TrySetResult(); });
            await stopped.Task.ConfigureAwait(false);
            await owner.StopAsync().ConfigureAwait(false);
            return 0;
        }

        private static async Task<int> StandaloneAsync(string[] args)
        {
            if (args[0] == "quit")
            {
                Console.WriteLine("{\"ok\":true,\"running\":false}");
                return 0;
            }
            if (args[0] is not ("status" or "devices" or "apps" or "diagnostics"))
                throw new InvalidOperationException("Noise Reduction is not running, start the app or use --headless first");
            var snapshot = await new LinuxAudio().SnapshotAsync().ConfigureAwait(false);
            object result = args[0] switch
            {
                "devices" => new { ok = true, devices = snapshot.Devices },
                "apps" => new { ok = true, apps = snapshot.Apps },
                _ => (object)new { ok = true, running = false, defaultSource = snapshot.DefaultSource, devices = snapshot.Devices, apps = snapshot.Apps },
            };
            Console.WriteLine(JsonSerializer.Serialize(result, Json));
            return 0;
        }

        private static async Task<string> HandleAsync(string request)
        {
            var args = JsonSerializer.Deserialize<string[]>(request) ?? Array.Empty<string>();
            string command = args.Length == 0 ? "show" : args[0];
            string Argument(int index) => index < args.Length ? args[index] : throw new ArgumentException("Missing command argument");
            uint Stream() => uint.TryParse(Argument(1), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id : throw new ArgumentException("Invalid stream ID");
            bool remember = args.Contains("--remember");
            switch (command)
            {
                case "show":
                    if (App.ShowRequested == null) return JsonSerializer.Serialize(new { ok = false, headless = true, error = "The coordinator is running in headless mode" }, Json);
                    App.ShowRequested();
                    break;
                case "on": await controller.ChangeAsync(() => controller.Settings.Enabled = true).ConfigureAwait(false); break;
                case "off": await controller.ChangeAsync(() => controller.Settings.Enabled = false).ConfigureAwait(false); break;
                case "toggle": await controller.ChangeAsync(() => controller.Settings.Enabled = !controller.Settings.Enabled).ConfigureAwait(false); break;
                case "source": await controller.SetSourceAsync(Argument(1) == "default" ? null : Argument(1)).ConfigureAwait(false); break;
                case "default": await controller.MakeDefaultAsync().ConfigureAwait(false); break;
                case "profile":
                    string deviceId = Argument(1);
                    if (!controller.Devices.Any(device => device.Id == deviceId)) throw new ArgumentException("Microphone not found");
                    bool? precise = null, enabled = null, lowCut = null;
                    int? attenuation = null;
                    for (int index = 2; index < args.Length; index++)
                    {
                        if (args[index] == "--json") continue;
                        string option = args[index], value = Argument(++index);
                        switch (option)
                        {
                            case "--model": precise = value switch { "precise" => true, "fast" => false, _ => throw new ArgumentException("Model must be fast or precise") }; break;
                            case "--strength": attenuation = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int strength) && strength is >= 0 and <= 60 ? strength : throw new ArgumentException("Strength must be 0..60"); break;
                            case "--enabled": enabled = Boolean(value); break;
                            case "--low-cut": lowCut = Boolean(value); break;
                            default: throw new ArgumentException("Unknown profile option: " + option);
                        }
                    }
                    await controller.ChangeProfileAsync(deviceId, profile =>
                    {
                        if (precise.HasValue) profile.DeepFilter = precise.Value;
                        if (attenuation.HasValue) profile.Attenuation = attenuation.Value;
                        if (enabled.HasValue) profile.Enabled = enabled.Value;
                        if (lowCut.HasValue) profile.LowCut = lowCut.Value;
                    }).ConfigureAwait(false);
                    break;
                case "route": await controller.RouteAsync(Stream(), Argument(2), remember).ConfigureAwait(false); break;
                case "mute": await controller.MuteAsync(Stream(), Boolean(Argument(2)), remember).ConfigureAwait(false); break;
                case "rules":
                    if (args.Length > 2 && args[1] == "--remove") await controller.ClearRulesAsync(args[2]).ConfigureAwait(false);
                    break;
                case "quit":
                    Interlocked.Exchange(ref quitting, 1);
                    await controller.StopAsync().ConfigureAwait(false);
                    break;
                case "status": case "devices": case "apps": case "diagnostics": break;
                default: throw new ArgumentException("Unknown command: " + command);
            }
            if (command == "devices") return JsonSerializer.Serialize(new { ok = true, devices = controller.Devices }, Json);
            if (command == "apps") return JsonSerializer.Serialize(new { ok = true, apps = controller.Apps }, Json);
            if (command == "rules") return JsonSerializer.Serialize(new { ok = true, rules = controller.Rules }, Json);
            if (command == "diagnostics") return JsonSerializer.Serialize(new { ok = true, diagnostics = controller.Diagnostics() }, Json);
            return JsonSerializer.Serialize(new
            {
                ok = true, running = command != "quit", enabled = controller.Settings.Enabled,
                source = controller.Settings.Source, onBattery = controller.OnBattery, state = controller.State.ToString(), error = controller.Error,
                devices = controller.Devices.Select(device => new { device.Id, device.Name, device.SourceName, device.IsDefault, device.Available, output = controller.OutputFor(device.Id), state = controller.StatusFor(device.Id).State.ToString(), profile = controller.ProfileFor(device.Id) }),
                apps = controller.Apps,
            }, Json);
        }

        private static bool Boolean(string value) => value switch
        {
            "on" or "true" => true,
            "off" or "false" => false,
            _ => throw new ArgumentException("Value must be on or off"),
        };
    }

    internal static class Installation
    {
        internal static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, ".installed"));

        internal static int Install()
        {
            using var process = Process.Start(new ProcessStartInfo("/bin/sh") { UseShellExecute = false, ArgumentList = { Path.Combine(AppContext.BaseDirectory, "install.sh") } });
            process.WaitForExit();
            return process.ExitCode;
        }

        internal static void RequestUninstall()
        {
            string script = Path.Combine(AppContext.BaseDirectory, "uninstall.sh");
            if (!IsInstalled || !File.Exists(script)) throw new InvalidOperationException("This is a portable copy, delete its folder to remove it");
            Process.Start(new ProcessStartInfo("/bin/sh") { UseShellExecute = false, ArgumentList = { script, "--wait-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) } });
        }

        internal static int Uninstall()
        {
            string script = Path.Combine(AppContext.BaseDirectory, "uninstall.sh");
            if (!IsInstalled || !File.Exists(script)) throw new InvalidOperationException("This is a portable copy, delete its folder to remove it");
            return LinuxProcess.ReplaceWithShell(script);
        }
    }
}
