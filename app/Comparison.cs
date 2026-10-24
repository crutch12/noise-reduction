using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace NoiseReduction.Desktop
{
    internal static class Comparison
    {
        [DllImport("NoiseReduction.Apo.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr nr_create();
        [DllImport("NoiseReduction.Apo.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void nr_destroy(IntPtr state);
        [DllImport("NoiseReduction.Apo.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void nr_process(IntPtr state, float[] input, float[] output, uint samples);
        [DllImport("NoiseReduction.Apo.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void nr_set_attenuation(IntPtr state, float db);
        [DllImport("NoiseReduction.Apo.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void nr_set_enabled(IntPtr state, int enabled);
        [DllImport("NoiseReduction.Apo.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint nr_get_latency();

        internal static void Create(string endpoint, string directory, int attenuation)
        {
            string engine = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NoiseReduction.Apo.dll");
            if (!File.Exists(engine) || !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rnnoise.dll")))
                throw new FileNotFoundException("Для сравнения нужны NoiseReduction.Apo.dll и rnnoise.dll рядом с программой");
            float[] original = Capture(endpoint);
            float[] processed = ProcessCaptured(original, attenuation);
            WriteWave(Path.Combine(directory, "original.wav"), original);
            WriteWave(Path.Combine(directory, "processed.wav"), processed);
        }

        internal static float[] ProcessCaptured(float[] original, int attenuation)
        {
            IntPtr state = nr_create();
            if (state == IntPtr.Zero) throw new InvalidOperationException("Не удалось запустить RNNoise");
            try
            {
                nr_set_attenuation(state, attenuation); nr_set_enabled(state, 1);
                int latency = checked((int)nr_get_latency());
                if (latency < 0 || latency > 48000) throw new InvalidOperationException("Некорректная задержка движка");
                float[] padded = new float[original.Length + latency];
                float[] output = new float[padded.Length];
                Array.Copy(original, padded, original.Length);
                nr_process(state, padded, output, (uint)padded.Length);
                float[] processed = new float[original.Length];
                Array.Copy(output, latency, processed, 0, processed.Length);
                return processed;
            }
            finally { nr_destroy(state); }
        }

        internal static void WriteWave(string path, float[] samples)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 2);
                foreach (float sample in samples) writer.Write((short)Math.Round(Math.Max(-1, Math.Min(1, sample)) * 32767));
            }
        }

        internal static void VerifyCapture(string endpoint, Func<bool> modelAdvanced)
        { Capture(endpoint, true, modelAdvanced); }

        private static float[] Capture(string endpoint)
        { return Capture(endpoint, false, null); }

        private static float[] Capture(string endpoint, bool discard, Func<bool> modelAdvanced)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            bool started = false;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                Check(enumerator.GetDevice(endpoint, out device));
                Guid clientId = typeof(IAudioClient).GUID; object instance;
                Check(device.Activate(ref clientId, 23, IntPtr.Zero, out instance)); client = (IAudioClient)instance;
                var format = new WaveFormat { FormatTag = 3, Channels = 1, SamplesPerSec = 48000, AvgBytesPerSec = 192000, BlockAlign = 4, BitsPerSample = 32 };
                Check(client.Initialize(0, unchecked((int)0x88000000), 1000000, 0, ref format, IntPtr.Zero));
                Guid captureId = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref captureId, out instance)); capture = (IAudioCaptureClient)instance;
                // Let the bypass flag reach an already running audio graph
                Thread.Sleep(150);
                Check(client.Start()); started = true;
                int target = (discard ? 3 : 5) * 48000;
                var samples = discard ? null : new List<float>(target);
                int captured = 0;
                DateTime deadline = DateTime.UtcNow.AddSeconds(discard ? 6 : 12);
                while (captured < target)
                {
                    if (discard && modelAdvanced()) break;
                    if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Микрофон не передал запись, проверьте разрешение доступа и занятость устройства");
                    uint available; Check(capture.GetNextPacketSize(out available));
                    if (available == 0) { Thread.Sleep(10); continue; }
                    IntPtr data; uint count; uint flags; ulong position; ulong timestamp;
                    Check(capture.GetBuffer(out data, out count, out flags, out position, out timestamp));
                    try
                    {
                        int length = Math.Min(checked((int)count), target - captured);
                        if (!discard)
                        {
                            var packet = new float[length];
                            if ((flags & 2) == 0) Marshal.Copy(data, packet, 0, length);
                            samples.AddRange(packet);
                        }
                        captured += length;
                    }
                    finally { Check(capture.ReleaseBuffer(count)); }
                }
                return discard ? null : samples.ToArray();
            }
            catch (COMException error)
            { throw new InvalidOperationException("Не удалось записать выбранный микрофон: " + error.Message, error); }
            finally
            {
                if (started && client != null) client.Stop();
                if (capture != null) Marshal.ReleaseComObject(capture);
                if (client != null) Marshal.ReleaseComObject(client);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }
        private static void Check(int result) { Marshal.ThrowExceptionForHR(result); }
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort FormatTag, Channels;
            public uint SamplesPerSec, AvgBytesPerSec;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }
        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int mode, int flags, long duration, long periodicity, ref WaveFormat format, IntPtr session);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }
        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
