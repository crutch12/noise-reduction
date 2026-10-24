using System;
using System.Reflection;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using NoiseReduction.Desktop;
using NoiseReduction.Platform;

internal static class UiTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Path to the app executable is required");
        TestSingleInstance(args[0]);
        Application.EnableVisualStyles();
        using (var window = new MainWindow(false))
        {
            window.Show(); Application.DoEvents();
            if (!window.Visible || window.Text != "Noise Reduction") throw new Exception("Settings window did not open");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            TestStartup(window, flags);
            var state = (DesktopState)typeof(MainWindow).GetField("state", flags).GetValue(window);
            var refresh = (Button)typeof(MainWindow).GetField("refresh", flags).GetValue(window);
            var list = (ListBox)typeof(MainWindow).GetField("devices", flags).GetValue(window);
            state.Integrating = true;
            typeof(MainWindow).GetMethod("ShowProfile", flags).Invoke(window, null);
            if (refresh.Enabled || list.Enabled || ((CheckBox)typeof(MainWindow).GetField("global", flags).GetValue(window)).Enabled)
                throw new Exception("Elevated operation did not disable conflicting UI actions");
            typeof(MainWindow).GetMethod("RefreshDevices", flags).Invoke(window, null);
            if (!state.Integrating) throw new Exception("Refresh disturbed elevated operation");
            state.Integrating = false;
            typeof(MainWindow).GetMethod("ShowProfile", flags).Invoke(window, null);
            if (!refresh.Enabled || !list.Enabled) throw new Exception("UI did not recover after elevated operation");
            if (list.SelectedItem != null)
            {
                var device = (DeviceInfo)list.SelectedItem;
                state.CaptureEndpoint = device.Id;
                typeof(MainWindow).GetMethod("ShowProfile", flags).Invoke(window, null);
                if (refresh.Enabled) throw new Exception("Refresh remained enabled during comparison");
                typeof(MainWindow).GetMethod("RefreshDevices", flags).Invoke(window, null);
                if (state.CaptureEndpoint != device.Id) throw new Exception("Refresh disturbed active comparison");
                state.CaptureEndpoint = null;
                typeof(MainWindow).GetMethod("ShowProfile", flags).Invoke(window, null);
                if (!refresh.Enabled) throw new Exception("Refresh not restored after comparison");
            }
            state.RefreshError = "Enumeration test failure";
            typeof(MainWindow).GetMethod("Poll", flags).Invoke(window, null);
            var status = (Label)typeof(MainWindow).GetField("status", flags).GetValue(window);
            if (status.Text != "Enumeration test failure") throw new Exception("Activity polling overwrote enumeration failure");
            typeof(MainWindow).GetMethod("RefreshDevices", flags).Invoke(window, null);
            if (state.RefreshError != null) throw new Exception("Successful refresh did not clear enumeration failure");
            using (var image = new Bitmap(window.Width, window.Height))
            { window.DrawToBitmap(image, window.ClientRectangle); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UiTests.png")); }
            window.Close(); Application.DoEvents();
            if (window.Visible || window.IsDisposed) throw new Exception("Close did not preserve tray application");
            typeof(MainWindow).GetMethod("Reveal", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
            Application.DoEvents();
            if (!window.Visible) throw new Exception("Tray activation did not restore settings");
            var tray = (NotifyIcon)typeof(MainWindow).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            ((ToolStripMenuItem)tray.ContextMenuStrip.Items[1]).PerformClick();
            Application.DoEvents();
            if (!window.IsDisposed) throw new Exception("Explicit tray exit did not close application");
        }
        using (var hidden = new MainWindow(true))
        {
            hidden.Show(); Application.DoEvents();
            if (hidden.Visible || hidden.IsDisposed) throw new Exception("Tray startup exposed settings");
            var tray = (NotifyIcon)typeof(MainWindow).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hidden);
            ((ToolStripMenuItem)tray.ContextMenuStrip.Items[0]).PerformClick(); Application.DoEvents();
            if (!hidden.Visible) throw new Exception("Tray settings action did not show hidden startup");
            ((ToolStripMenuItem)tray.ContextMenuStrip.Items[1]).PerformClick(); Application.DoEvents();
        }
        Console.WriteLine("PASS: settings window, close-to-tray, activation, explicit Exit");
        return 0;
    }

    private static void TestStartup(MainWindow window, BindingFlags flags)
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string name = "NoiseReduction";
        using (var key = Registry.CurrentUser.CreateSubKey(path))
        {
            bool existed = Array.IndexOf(key.GetValueNames(), name) >= 0;
            object original = existed ? key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
            RegistryValueKind kind = existed ? key.GetValueKind(name) : RegistryValueKind.String;
            var checkbox = (CheckBox)typeof(MainWindow).GetField("startup", flags).GetValue(window);
            try
            {
                checkbox.Checked = false;
                if (key.GetValue(name) != null) throw new Exception("Startup disable did not remove the Run value");
                checkbox.Checked = true;
                string expected = "\"" + Application.ExecutablePath + "\" --tray";
                if ((string)key.GetValue(name) != expected || key.GetValueKind(name) != RegistryValueKind.String)
                    throw new Exception("Startup did not write the quoted executable and tray argument");
                checkbox.Checked = false;
                if (key.GetValue(name) != null) throw new Exception("Startup could not be disabled after enabling");
            }
            finally
            {
                if (existed) key.SetValue(name, original, kind); else key.DeleteValue(name, false);
            }
        }
        Console.WriteLine("PASS: startup disable, exact quoted Run value, original registry value and kind restored");
    }

    private static void TestSingleInstance(string app)
    {
        Exception failure = null;
        Process secondary = null;
        MainWindow primary = null;
        bool activated = false;
        var elapsed = Stopwatch.StartNew();
        using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
        {
            timer.Tick += delegate
            {
                try
                {
                    if (elapsed.ElapsedMilliseconds > 12000) throw new Exception("Single-instance activation timed out");
                    int count = 0;
                    foreach (Form form in Application.OpenForms) if (form is MainWindow) { count++; primary = (MainWindow)form; }
                    if (primary == null) return;
                    if (count != 1) throw new Exception("More than one settings window was created");
                    if (secondary == null)
                    {
                        if (primary.Visible) throw new Exception("Primary --tray launch exposed settings");
                        primary.Location = new Point(-10000, -10000);
                        secondary = Process.Start(new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true });
                        return;
                    }
                    if (!secondary.HasExited || !primary.Visible) return;
                    if (secondary.ExitCode != 0) throw new Exception("Secondary instance did not exit successfully");
                    activated = true;
                    timer.Stop();
                    typeof(MainWindow).GetField("exiting", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(primary, true);
                    primary.Close();
                }
                catch (Exception error)
                {
                    failure = error;
                    timer.Stop();
                    if (primary != null)
                    {
                        typeof(MainWindow).GetField("exiting", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(primary, true);
                        primary.Close();
                    }
                    else Application.ExitThread();
                }
            };
            EventHandler startTimer = null;
            startTimer = delegate { Application.Idle -= startTimer; timer.Start(); };
            Application.Idle += startTimer;
            try
            {
                int result = (int)typeof(Program).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new[] { "--tray" } });
                if (result != 0) throw new Exception("Primary instance returned an error");
            }
            finally
            {
                Application.Idle -= startTimer;
                if (secondary != null) { if (!secondary.HasExited) secondary.Kill(); secondary.Dispose(); }
            }
        }
        if (failure != null) throw failure;
        if (!activated) throw new Exception("Primary message loop exited without activation");
        bool created;
        using (var mutex = new Mutex(false, "Local\\NoiseReduction.Desktop.1", out created))
            if (!created) throw new Exception("Single-instance mutex remained after primary exit");
        Console.WriteLine("PASS: Program --tray launch, second-process mutex/broadcast activation, one window, message loop and mutex cleanup");
    }
}
