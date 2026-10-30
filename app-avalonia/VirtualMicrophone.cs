using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace NoiseReduction.Avalonia
{
    // микрофон Noise Reduction, который видят другие программы, и служебный выход, в который пишет мост
    internal static class VirtualMicrophone
    {
        internal const string Name = "Noise Reduction";
        private const string LinuxFeed = "noise_reduction_feed";
        private const string LinuxFeedName = "Noise Reduction Feed";
        private static readonly List<string> modules = new();

        // куда мост пишет обработанный звук
        internal const string Feed = LinuxFeedName;

        internal static bool IsOwn(string device) =>
            device.StartsWith(Name, StringComparison.Ordinal) || device.StartsWith("Monitor of ", StringComparison.Ordinal);

        // устройство живёт, пока работает звуковой сервер, поэтому создаём его при каждом запуске
        internal static bool Prepare(out string error)
        {
            error = null;
            if (!Run("pactl", out var sinks, "list", "short", "sinks")) { error = sinks; return false; }
            if (!sinks.Contains(LinuxFeed) &&
                !Load(out error, "module-null-sink", $"sink_name={LinuxFeed}", "channels=1", "rate=48000", $"sink_properties=device.description='{LinuxFeedName}'")) return false;
            Run("pactl", out var sources, "list", "short", "sources");
            if (!sources.Split('\n').Any(line => line.Contains("\tnoise_reduction\t")) &&
                !Load(out error, "module-remap-source", $"master={LinuxFeed}.monitor", "source_name=noise_reduction", "channels=1", $"source_properties=device.description='{Name}'")) return false;
            return true;
        }

        internal static void Release()
        {
            foreach (var module in modules.AsEnumerable().Reverse()) Run("pactl", out _, "unload-module", module);
            modules.Clear();
        }

        private static bool Load(out string error, params string[] arguments)
        {
            if (!Run("pactl", out var output, new[] { "load-module" }.Concat(arguments).ToArray())) { error = output; return false; }
            modules.Add(output.Trim());
            error = null;
            return true;
        }

        private static bool Run(string file, out string output, params string[] arguments)
        {
            try
            {
                var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start);
                output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0 && error.Length > 0) output = error.Trim();
                return process.ExitCode == 0;
            }
            catch (Exception exception) { output = exception.Message; return false; }
        }
    }
}
