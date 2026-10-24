using System;
using System.Reflection;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using NoiseReduction.Desktop;
using NoiseReduction.Platform;

internal static class UiTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool LogicalToPhysicalPointForPerMonitorDPI(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [STAThread]
    private static int Main(string[] args)
    {
        var original = new Dictionary<string, uint>();
        if (Directory.Exists(Integration.ControlRoot))
            foreach (string file in Directory.GetFiles(Integration.ControlRoot, "*.bin"))
            {
                Guid guid;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out guid)) continue;
                using (var control = new ControlFile(Integration.ControlRoot, guid.ToString("D"), true)) original.Add(guid.ToString("D"), control.Configuration);
            }
        try { return Run(args); }
        finally
        {
            var failures = new List<Exception>();
            foreach (var item in original)
                try { using (var control = new ControlFile(Integration.ControlRoot, item.Key)) control.SetConfiguration(item.Value); }
                catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("Could not restore original endpoint configurations", failures);
            Console.WriteLine("PASS: original endpoint configuration words restored, audio counters preserved");
        }
    }
    private static int Run(string[] args)
    {
        if (args.Length < 1 || args.Length > 2) throw new ArgumentException("Path to the app executable is required");
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
            TestTrayIcons(window, flags);
            TestDefaultMicrophoneTray(window, flags);
            TestDisabledIdleStatus(window, flags);
            TestKeyboard(window, flags);
            string review = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "ux", "review"));
            Directory.CreateDirectory(review);
            Capture(window, new Size(860, 650), args.Length == 1 ? Path.Combine(review, "desktop.png") : null);
            Capture(window, new Size(760, 600), args.Length == 1 ? Path.Combine(review, "compact.png") : null);
            if (args.Length == 1) CaptureTrayFixture((Theme)typeof(MainWindow).GetField("theme", flags).GetValue(window), Path.Combine(review, "tray-states.png"));
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

    private static void TestTrayIcons(MainWindow window, BindingFlags flags)
    {
        var health = new TrayHealth();
        health.Observe("fixture", 1, 100);
        if (!health.Failed("fixture")) throw new Exception("Observed model error was not shown");
        health.Observe("fixture", 1, 101);
        if (health.Failed("fixture")) throw new Exception("Confirmed model recovery did not clear the error");
        health.Observe("fixture", 1, 101);
        if (health.Failed("fixture")) throw new Exception("Recovered historical error returned when capture became idle");
        health.Observe("fixture", 2, 102);
        if (!health.Failed("fixture")) throw new Exception("A new model error did not restore error state");
        health.Observe("fixture", 0, 0);
        if (health.Failed("fixture")) throw new Exception("Reset counters retained a stale error");
        var theme = (Theme)typeof(MainWindow).GetField("theme", flags).GetValue(window);
        Color[] expected = { Color.FromArgb(137, 146, 142), Color.FromArgb(46, 146, 91), Color.FromArgb(197, 68, 68) };
        for (int state = 0; state < 3; state++)
        {
            Icon icon = theme.TrayIcon(state);
            if (!Object.ReferenceEquals(icon, theme.TrayIcon(state))) throw new Exception("Tray icon was reallocated for an unchanged state");
            using (var bitmap = icon.ToBitmap())
                if (bitmap.GetPixel(12, 12).ToArgb() != expected[state].ToArgb()) throw new Exception("Tray badge does not render its actual state color");
        }
        var desktopState = (DesktopState)typeof(MainWindow).GetField("state", flags).GetValue(window);
        desktopState.RefreshError = "Test refresh failure";
        typeof(MainWindow).GetMethod("UpdateTray", flags).Invoke(window, null);
        var tray = (NotifyIcon)typeof(MainWindow).GetField("tray", flags).GetValue(window);
        if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(2)) || !tray.Text.StartsWith("Ошибка")) throw new Exception("Tray does not explain the current refresh error");
        desktopState.RefreshError = null;
        typeof(MainWindow).GetMethod("UpdateTray", flags).Invoke(window, null);
        Console.WriteLine("PASS: cached native tray icons, gray/green/red rendered badges, actionable error tooltip");
    }
    private static List<Control> Interactive(Control parent)
    {
        var result = new List<Control>();
        foreach (Control control in parent.Controls)
        {
            if (control is Button || control is CheckBox || control is TrackBar || control is ListBox) result.Add(control);
            result.AddRange(Interactive(control));
        }
        return result;
    }
    private static void TestDisabledIdleStatus(MainWindow window, BindingFlags flags)
    {
        var list = (ListBox)typeof(MainWindow).GetField("devices", flags).GetValue(window);
        var device = list.SelectedItem as DeviceInfo;
        if (device == null) throw new Exception("A selected microphone is required for the idle status check");
        var settings = (Settings)typeof(MainWindow).GetField("settings", flags).GetValue(window);
        var activity = (Dictionary<string, Activity>)typeof(MainWindow).GetField("activities", flags).GetValue(window);
        var status = (Label)typeof(MainWindow).GetField("status", flags).GetValue(window);
        var profile = settings.For(device.Id);
        bool globalBefore = settings.Enabled, profileBefore = profile.Enabled;
        Activity activityBefore = activity[device.Id];
        Action render = delegate { typeof(MainWindow).GetMethod("RenderStatus", flags).Invoke(window, null); };
        try
        {
            activity[device.Id] = new Activity();
            settings.Enabled = false; profile.Enabled = true; render();
            if (!status.Text.Contains("Обработка выключена") || status.Text.Contains("начните запись")) throw new Exception("Globally disabled idle microphone invites recording instead of showing off");
            settings.Enabled = true; profile.Enabled = false; render();
            if (!status.Text.Contains("Обработка выключена") || status.Text.Contains("начните запись")) throw new Exception("Disabled idle profile invites recording instead of showing off");
            profile.Enabled = true; render();
            if (!status.Text.Contains("Ожидание звука")) throw new Exception("Enabled idle microphone lost its waiting state");
        }
        finally
        {
            settings.Enabled = globalBefore; profile.Enabled = profileBefore; activity[device.Id] = activityBefore; render();
        }
        Console.WriteLine("PASS: disabled global/profile idle status takes precedence over waiting for audio");
    }
    private static void TestDefaultMicrophoneTray(MainWindow window, BindingFlags flags)
    {
        var list = (ListBox)typeof(MainWindow).GetField("devices", flags).GetValue(window);
        var settings = (Settings)typeof(MainWindow).GetField("settings", flags).GetValue(window);
        var tray = (NotifyIcon)typeof(MainWindow).GetField("tray", flags).GetValue(window);
        var theme = (Theme)typeof(MainWindow).GetField("theme", flags).GetValue(window);
        var defaultField = typeof(MainWindow).GetField("defaultMicrophoneId", flags);
        var updating = typeof(MainWindow).GetField("updating", flags);
        var originals = new List<object>();
        foreach (object item in list.Items) originals.Add(item);
        object selected = list.SelectedItem;
        object defaultId = defaultField.GetValue(window);
        bool globallyEnabled = settings.Enabled;
        var current = new DeviceInfo { Id = "test-default", Name = "Default microphone fixture", Active = true, Attached = true, EnhancementsEnabled = true };
        var other = new DeviceInfo { Id = "test-selected", Name = "Other microphone fixture", Active = true, Attached = true };
        Action update = delegate { typeof(MainWindow).GetMethod("UpdateTray", flags).Invoke(window, null); };
        list.BeginUpdate(); updating.SetValue(window, true);
        try
        {
            list.Items.Clear(); list.Items.Add(current); list.Items.Add(other);
            defaultField.SetValue(window, current.Id);
            settings.Enabled = true;
            settings.For(current.Id).Enabled = true;
            update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(1)) || !tray.Text.Contains(current.Name)) throw new Exception("Enabled attached Windows default microphone did not show a green badge");
            list.SelectedItem = other;
            settings.For(other.Id).Enabled = false;
            update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(1)) || !tray.Text.Contains(current.Name)) throw new Exception("Selected microphone changed the Windows default microphone badge");
            current.EnhancementsEnabled = false; update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(2)) || !tray.Text.Contains("Улучшения звука Windows выключены")) throw new Exception("Disabled Windows enhancements did not show a red explanation");
            settings.For(current.Id).Enabled = false; update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(0)) || !tray.Text.Contains("выключено")) throw new Exception("Disabled default profile did not show gray and explain why");
            settings.For(current.Id).Enabled = true; settings.Enabled = false; update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(0))) throw new Exception("Disabled global processing did not show a gray badge");
            settings.Enabled = true; current.EnhancementsEnabled = true; current.Attached = false; update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(0)) || !tray.Text.Contains("не подключён")) throw new Exception("Unattached default microphone incorrectly showed enabled");
            defaultField.SetValue(window, null); update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(0)) || !tray.Text.Contains("не найден")) throw new Exception("Missing default microphone did not show gray");
        }
        finally
        {
            settings.Enabled = globallyEnabled;
            settings.Profiles.RemoveAll(profile => profile.Id == current.Id || profile.Id == other.Id);
            list.Items.Clear(); foreach (object item in originals) list.Items.Add(item);
            list.SelectedItem = selected;
            defaultField.SetValue(window, defaultId);
            updating.SetValue(window, false);
            list.EndUpdate();
            typeof(MainWindow).GetMethod("ShowProfile", flags).Invoke(window, null);
            update();
        }
        Console.WriteLine("PASS: tray follows Windows default microphone, independent of selection, global/profile off, unattached and missing states");
    }
    private static void TestKeyboard(MainWindow window, BindingFlags flags)
    {
        var controls = Interactive(window);
        foreach (var control in controls)
            if (String.IsNullOrWhiteSpace(control.AccessibleName) || !control.TabStop) throw new Exception("Control lacks a keyboard stop or accessible name: " + control.Text);
        var global = (CheckBox)typeof(MainWindow).GetField("global", flags).GetValue(window);
        var devices = (ListBox)typeof(MainWindow).GetField("devices", flags).GetValue(window);
        global.Focus();
        window.SelectNextControl(global, true, true, true, false);
        if (!devices.Focused) throw new Exception("Tab from the global switch did not reach the microphone list");
        Console.WriteLine("PASS: accessible native control names, keyboard tab stops and global-to-microphone focus order");
    }
    private static void Capture(MainWindow window, Size size, string path)
    {
        window.Size = size;
        window.PerformLayout(); Application.DoEvents();
        var controls = Interactive(window);
        foreach (var control in controls)
        {
            Rectangle area = window.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            if (!window.ClientRectangle.Contains(area) || area.Width == 0 || area.Height == 0) throw new Exception("Control is clipped at " + size + ": " + control.AccessibleName + " " + area);
        }
        for (int first = 0; first < controls.Count; first++)
            for (int second = first + 1; second < controls.Count; second++)
            {
                Rectangle a = controls[first].RectangleToScreen(controls[first].ClientRectangle);
                Rectangle b = controls[second].RectangleToScreen(controls[second].ClientRectangle);
                if (a.IntersectsWith(b)) throw new Exception("Interactive controls overlap at " + size + ": " + controls[first].AccessibleName + " / " + controls[second].AccessibleName);
            }
        if (path != null)
        {
            window.BringToFront(); window.Activate(); Application.DoEvents();
            Thread.Sleep(100); Application.DoEvents();
            Point origin = window.PointToScreen(Point.Empty);
            Point end = new Point(origin.X + window.ClientSize.Width, origin.Y + window.ClientSize.Height);
            bool transformed = LogicalToPhysicalPointForPerMonitorDPI(window.Handle, ref origin) && LogicalToPhysicalPointForPerMonitorDPI(window.Handle, ref end);
            Size physical = new Size(end.X - origin.X, end.Y - origin.Y);
            bool screenCapture = transformed && GetForegroundWindow() == window.Handle && physical.Width > 0 && physical.Height > 0;
            Point[] corners = { origin, new Point(end.X - 1, origin.Y), new Point(origin.X, end.Y - 1), new Point(end.X - 1, end.Y - 1), new Point((origin.X + end.X) / 2, (origin.Y + end.Y) / 2) };
            foreach (Point point in corners)
            {
                IntPtr at = WindowFromPhysicalPoint(point);
                if (at != window.Handle && !IsChild(window.Handle, at)) screenCapture = false;
            }
            Size imageSize = screenCapture ? physical : window.ClientSize;
            using (var image = new Bitmap(imageSize.Width, imageSize.Height))
            {
                if (screenCapture) using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(origin, Point.Empty, physical, CopyPixelOperation.SourceCopy);
                else window.Controls[0].DrawToBitmap(image, new Rectangle(Point.Empty, window.ClientSize));
                image.Save(path);
            }
            File.WriteAllText(path + ".txt", "Window " + size.Width + "x" + size.Height + ", logical client " + window.ClientSize.Width + "x" + window.ClientSize.Height + ", image " + imageSize.Width + "x" + imageSize.Height + ", " + (screenCapture ? "CopyFromScreen of foreground app client only, DPI-converted and physical window ownership checked" : "client-only DrawToBitmap fallback, safe foreground capture unavailable"));
        }
        Console.WriteLine("PASS: native layout without overlapping controls at " + size.Width + "x" + size.Height);
    }
    private static void CaptureTrayFixture(Theme theme, string path)
    {
        using (var image = new Bitmap(700, 280))
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.FromArgb(241, 244, 242));
            TextRenderer.DrawText(graphics, "Значки трея: 16 px и увеличение 4x", theme.Section, new Point(18, 14), Theme.Ink);
            string[] names = { "Выключено", "Включено", "Ошибка" };
            for (int state = 0; state < 3; state++) TextRenderer.DrawText(graphics, names[state], theme.Body, new Point(134 + state * 180, 44), Theme.Ink);
            Color[] backgrounds = { Color.White, Color.FromArgb(32, 42, 38) };
            for (int row = 0; row < 2; row++)
            {
                int top = 72 + row * 94;
                using (var brush = new SolidBrush(backgrounds[row])) graphics.FillRectangle(brush, 12, top, 676, 86);
                TextRenderer.DrawText(graphics, row == 0 ? "Светлый" : "Тёмный", theme.Small, new Point(22, top + 33), row == 0 ? Theme.Secondary : Color.White);
                for (int state = 0; state < 3; state++)
                {
                    int left = 130 + state * 180;
                    graphics.DrawIcon(theme.TrayIcon(state), new Rectangle(left, top + 34, 16, 16));
                    using (var icon = theme.TrayIcon(state).ToBitmap())
                    {
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.DrawImage(icon, new Rectangle(left + 36, top + 10, 64, 64));
                    }
                }
            }
            image.Save(path);
        }
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
