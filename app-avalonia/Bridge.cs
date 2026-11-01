using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{
    internal sealed class Bridge : IDisposable
    {
        private const string Library = "nr_bridge";
        private IntPtr handle;
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int nr_bridge_list(int playback, byte[] buffer, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr nr_bridge_start_configured(byte[] input, byte[] output, byte[] models, int backend, uint config);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nr_bridge_configure(IntPtr bridge, uint config);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nr_bridge_snapshot(IntPtr bridge, out BridgeMetrics metrics);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nr_bridge_stop(IntPtr bridge);
        internal sealed record Device(string Name, bool IsDefault);
        internal static List<Device> Inputs()
        {
            int size = nr_bridge_list(0, null, 0);
            var buffer = new byte[Math.Max(size, 1)];
            nr_bridge_list(0, buffer, buffer.Length);
            var devices = new List<Device>();
            foreach (var line in Encoding.UTF8.GetString(buffer).TrimEnd('\0').Split('\n', StringSplitOptions.RemoveEmptyEntries))
                devices.Add(new Device(line.TrimStart('*'), line.StartsWith('*')));
            return devices;
        }
        internal bool Running => handle != IntPtr.Zero;
        internal bool Start(string input, string output) => Start(input, output, 1u | (12u << 8));
        internal bool Start(string input, string output, uint configuration)
        {
            Stop();
            if (String.IsNullOrWhiteSpace(input) || String.IsNullOrWhiteSpace(output))
                throw new ArgumentException("Explicit physical input and virtual output are required");
            handle = nr_bridge_start_configured(Text("id:" + input), Text("id:" + output), Text(AppContext.BaseDirectory), 2, configuration);
            return Running;
        }
        internal void Configure(uint configuration)
        {
            if (Running) nr_bridge_configure(handle, configuration);
        }
        internal void Configure(bool enabled, int attenuation, bool lowCut, bool deepFilter) =>
            Configure(Configuration(enabled, attenuation, lowCut, deepFilter));
        internal static uint Configuration(bool enabled, int attenuation, bool lowCut, bool deepFilter) =>
            (enabled ? 1u : 0) | (lowCut ? 2u : 0) | (deepFilter ? 8u : 0) | ((uint)Math.Clamp(attenuation, 0, 60) << 8);

        internal static uint Configuration(Settings settings, Profile profile, bool? onBattery)
        {
            bool batteryOverride = profile.DeepFilter && settings.FastOnBattery && onBattery == true;
            int attenuation = batteryOverride ? profile.Attenuation switch { 12 => 6, 24 => 12, _ => profile.Attenuation } : profile.Attenuation;
            return Configuration(settings.Enabled && profile.Enabled, attenuation, profile.LowCut, profile.DeepFilter && !batteryOverride);
        }
        internal BridgeMetrics Snapshot()
        {
            if (!Running) return default;
            nr_bridge_snapshot(handle, out var metrics);
            return metrics;
        }
        internal (ulong Frames, ulong ModelFrames, ulong Errors) Stats()
        {
            var metrics = Snapshot();
            return (metrics.CaptureFrames, metrics.ModelFrames, metrics.Errors);
        }
        internal void Stop()
        {
            if (!Running) return;
            var stopped = handle;
            handle = IntPtr.Zero;
            nr_bridge_stop(stopped);
        }
        public void Dispose() => Stop();
        private static byte[] Text(string value) => value == null ? null : Encoding.UTF8.GetBytes(value + "\0");
    }
}
