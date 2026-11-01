using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NoiseReduction.Avalonia
{
    internal sealed class GlobalShortcut : IDisposable
    {
        private IntPtr connection;
        private readonly CancellationTokenSource stopping = new();
        private readonly Task listener;
        internal bool Available => connection != IntPtr.Zero;

        internal GlobalShortcut(Action toggle)
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland") return;
            try
            {
                connection = xcb_connect(null, out int screen);
                if (connection == IntPtr.Zero || xcb_connection_has_error(connection) != 0) { Release(); return; }
                IntPtr display = XOpenDisplay(null);
                if (display == IntPtr.Zero) { Release(); return; }
                byte key;
                try { key = XKeysymToKeycode(display, 0x006e); }
                finally { XCloseDisplay(display); }
                if (key == 0) { Release(); return; }
                var screens = xcb_setup_roots_iterator(xcb_get_setup(connection));
                for (int index = 0; index < screen && screens.Remaining > 0; index++) xcb_screen_next(ref screens);
                if (screens.Remaining == 0) { Release(); return; }
                uint root = unchecked((uint)Marshal.ReadInt32(screens.Data));
                foreach (ushort locks in new ushort[] { 0, 2, 16, 18 })
                {
                    uint cookie = xcb_grab_key_checked(connection, 0, root, (ushort)(12 | locks), key, 1, 1);
                    IntPtr error = xcb_request_check(connection, cookie);
                    if (error == IntPtr.Zero) continue;
                    free(error); Release(); return;
                }
                xcb_flush(connection);
                listener = Task.Run(() => ListenAsync(key, toggle));
            }
            catch (DllNotFoundException) { Release(); }
            catch (EntryPointNotFoundException) { Release(); }
        }

        private async Task ListenAsync(byte key, Action toggle)
        {
            bool down = false;
            uint releasedAt = 0;
            try
            {
                while (!stopping.IsCancellationRequested && xcb_connection_has_error(connection) == 0)
                {
                    IntPtr next;
                    while ((next = xcb_poll_for_event(connection)) != IntPtr.Zero)
                    {
                        try
                        {
                            int type = Marshal.ReadByte(next) & 0x7f;
                            if (Marshal.ReadByte(next, 1) != key) continue;
                            uint time = unchecked((uint)Marshal.ReadInt32(next, 4));
                            if (type == 3) { down = false; releasedAt = time; }
                            if (type == 2)
                            {
                                bool repeat = down || time == releasedAt;
                                down = true;
                                if (!repeat) toggle();
                            }
                        }
                        finally { free(next); }
                    }
                    await Task.Delay(20, stopping.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }

        private void Release()
        {
            if (connection == IntPtr.Zero) return;
            xcb_disconnect(connection); connection = IntPtr.Zero;
        }

        public void Dispose()
        {
            stopping.Cancel();
            listener?.GetAwaiter().GetResult();
            Release();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenIterator { internal IntPtr Data; internal int Remaining, Index; }
        [DllImport("libxcb.so.1")] private static extern IntPtr xcb_connect(string display, out int screen);
        [DllImport("libxcb.so.1")] private static extern int xcb_connection_has_error(IntPtr connection);
        [DllImport("libxcb.so.1")] private static extern IntPtr xcb_get_setup(IntPtr connection);
        [DllImport("libxcb.so.1")] private static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);
        [DllImport("libxcb.so.1")] private static extern void xcb_screen_next(ref ScreenIterator iterator);
        [DllImport("libxcb.so.1")] private static extern uint xcb_grab_key_checked(IntPtr connection, byte ownerEvents, uint window, ushort modifiers, byte key, byte pointerMode, byte keyboardMode);
        [DllImport("libxcb.so.1")] private static extern IntPtr xcb_request_check(IntPtr connection, uint cookie);
        [DllImport("libxcb.so.1")] private static extern int xcb_flush(IntPtr connection);
        [DllImport("libxcb.so.1")] private static extern IntPtr xcb_poll_for_event(IntPtr connection);
        [DllImport("libxcb.so.1")] private static extern void xcb_disconnect(IntPtr connection);
        [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string display);
        [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, nuint symbol);
        [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
        [DllImport("libc.so.6")] private static extern void free(IntPtr memory);
    }
}
