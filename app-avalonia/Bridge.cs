using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NoiseReduction.Avalonia
{
    // обёртка над libnr_bridge: реальный микрофон, обработка, виртуальное устройство
    internal sealed class Bridge : IDisposable
    {
        private const string Library = "nr_bridge";
        private IntPtr handle;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int nr_bridge_list(int playback, byte[] buffer, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr nr_bridge_start(byte[] input, byte[] output, byte[] models, int backend);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nr_bridge_configure(IntPtr bridge, uint config);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nr_bridge_stats(IntPtr bridge, out ulong frames, out ulong modelFrames, out ulong errors);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nr_bridge_stop(IntPtr bridge);

        internal sealed record Device(string Name, bool IsDefault);

        internal static List<Device> Inputs()
        {
            int size = nr_bridge_list(0, null, 0);
            var buffer = new byte[Math.Max(size, 1)];
            nr_bridge_list(0, buffer, buffer.Length);
            var result = new List<Device>();
            foreach (var line in Encoding.UTF8.GetString(buffer).TrimEnd('\0').Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                bool isDefault = line.StartsWith('*');
                string name = isDefault ? line.Substring(1) : line;
                // свой же виртуальный микрофон источником быть не может
                if (!VirtualMicrophone.IsOwn(name)) result.Add(new Device(name, isDefault));
            }
            return result;
        }

        internal bool Running => handle != IntPtr.Zero;

        internal bool Start(string input, string output)
        {
            Stop();
            string models = AppContext.BaseDirectory;
            handle = nr_bridge_start(Text(input), Text(output), Text(models), 0);
            return Running;
        }

        internal void Configure(bool enabled, int attenuation, bool lowCut, bool deepFilter)
        {
            if (!Running) return;
            uint config = (enabled ? 1u : 0) | (lowCut ? 2u : 0) | (deepFilter ? 8u : 0) | ((uint)Math.Clamp(attenuation, 0, 60) << 8);
            nr_bridge_configure(handle, config);
        }

        internal (ulong Frames, ulong ModelFrames, ulong Errors) Stats()
        {
            if (!Running) return (0, 0, 0);
            nr_bridge_stats(handle, out var frames, out var model, out var errors);
            return (frames, model, errors);
        }

        internal void Stop()
        {
            if (!Running) return;
            nr_bridge_stop(handle);
            handle = IntPtr.Zero;
        }

        public void Dispose() => Stop();

        private static byte[] Text(string value) => value == null ? null : Encoding.UTF8.GetBytes(value + "\0");
    }
}
