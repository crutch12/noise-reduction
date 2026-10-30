using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using NoiseReduction.Platform;

namespace NoiseReduction.WinUI
{
    // Windows сообщает о подключении, отключении и смене микрофона по умолчанию сразу, без ожидания опроса
    public sealed class DeviceEvents : IDisposable
    {
        private const int Capture = 1;
        private static readonly Guid RawSupport = new Guid("8943b373-388c-4395-b557-bc6dbaffafdb");
        private readonly Listener listener;
        private readonly IntPtr client;
        private readonly object enumerator;
        private readonly DispatcherQueueTimer delay;

        internal DeviceEvents(DispatcherQueue dispatcher, Action changed)
        {
            // события приходят пачкой, обновляемся один раз после затишья
            delay = dispatcher.CreateTimer();
            delay.Interval = TimeSpan.FromMilliseconds(300);
            delay.IsRepeating = false;
            delay.Tick += (_, _) => changed();
            listener = new Listener(() => dispatcher.TryEnqueue(() => { delay.Stop(); delay.Start(); }));
            client = Marshal.GetComInterfaceForObject(listener, typeof(INotificationClient));
            try { enumerator = Integration.WatchEndpoints(client); }
            catch { Marshal.Release(client); throw; }
        }

        public void Dispose()
        {
            delay.Stop();
            Integration.StopWatchingEndpoints(enumerator, client);
            Marshal.Release(client);
        }

        // COM видит только публичные типы, поэтому слушатель и интерфейс открыты
        [ComVisible(true)]
        public sealed class Listener : INotificationClient
        {
            private readonly Action changed;
            public Listener(Action changed) { this.changed = changed; }
            public int OnDeviceStateChanged(string id, uint state) { changed(); return 0; }
            public int OnDeviceAdded(string id) { changed(); return 0; }
            public int OnDeviceRemoved(string id) { changed(); return 0; }
            public int OnDefaultDeviceChanged(int flow, int role, string id) { if (flow == Capture) changed(); return 0; }
            // свойства меняются постоянно, в том числе от наших же записей, их ловит обычный опрос; возврат RAW нужно заметить сразу
            public int OnPropertyValueChanged(string id, PropertyKey key) { if (key.Format == RawSupport && key.Id == 2) changed(); return 0; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey { public Guid Format; public int Id; }

        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface INotificationClient
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
        }
    }
}
