using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace NoiseReduction.WinUI
{
    internal static class Program
    {
        internal static bool StartHidden;
        private static EventWaitHandle activate;
        private static RegisteredWaitHandle listener;
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] != "--tray")
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "NoiseReduction.Engine.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string arg in args) start.ArgumentList.Add(arg);
                using (var process = Process.Start(start)) { process.WaitForExit(); return process.ExitCode; }
            }
            StartHidden = args.Length == 1;
            bool created;
            using (var mutex = new Mutex(true, "Local\\NoiseReduction.Desktop.1", out created))
            using (activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\NoiseReduction.ShowSettings.2"))
            {
                if (!created) { activate.Set(); return 0; }
                try
                {
                    WinRT.ComWrappersSupport.InitializeComWrappers();
                    Microsoft.UI.Xaml.Application.Start(delegate
                    {
                        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                        new App();
                    });
                }
                finally { if (listener != null) listener.Unregister(null); mutex.ReleaseMutex(); }
            }
            return 0;
        }
        internal static void WindowReady()
        {
            listener = ThreadPool.RegisterWaitForSingleObject(activate, delegate
            {
                var app = App.CurrentApp;
                if (app != null && app.Window != null) app.Window.DispatcherQueue.TryEnqueue(delegate { app.Window.Reveal(); });
            }, null, Timeout.Infinite, false);
        }
    }
}
