using System;
using System.Reflection;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;
using NoiseReduction.Desktop;
using NoiseReduction.Platform;

internal static class UiTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool LogicalToPhysicalPointForPerMonitorDPI(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [STAThread]
    private static int Main(string[] args)
    {
        string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoiseReduction", "settings.json");
        byte[] originalSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
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
            try { if (originalSettings != null) File.WriteAllBytes(settingsPath, originalSettings); else if (File.Exists(settingsPath)) File.Delete(settingsPath); }
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
            if (!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(window.Handle), new IntPtr(-4))) throw new Exception("Settings window is bitmap-scaled instead of PerMonitorV2 DPI aware");
            Console.WriteLine("PASS: native PerMonitorV2 window DPI awareness, dpi=" + window.DeviceDpi);
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
            TestLanguages(window, flags);
            TestAdvanced(window, flags);
            TestTrayProfiles(window, flags);
            TestKeyboard(window, flags);
            string review = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "ux", "review"));
            Directory.CreateDirectory(review);
            var captureSettings = (Settings)typeof(MainWindow).GetField("settings", flags).GetValue(window);
            string originalLanguage = captureSettings.Language;
            foreach (string locale in new[] { "en", "ru" })
            {
                captureSettings.Language = locale;
                typeof(MainWindow).GetMethod("ApplyLanguage", flags).Invoke(window, null);
                Capture(window, new Size(860, 650), args.Length == 1 ? Path.Combine(review, "desktop-" + locale + ".png") : null);
                Capture(window, new Size(760, 600), args.Length == 1 ? Path.Combine(review, "compact-" + locale + ".png") : null);
                var panel = (Control)typeof(MainWindow).GetField("advancedPanel", flags).GetValue(window);
                panel.Visible = true;
                Capture(window, new Size(860, 900), args.Length == 1 ? Path.Combine(review, "advanced-" + locale + ".png") : null);
                window.Size = new Size(760 * window.DeviceDpi / 96, 600 * window.DeviceDpi / 96);
                Application.DoEvents();
                var viewport = (Panel)panel.Parent.Parent;
                viewport.ScrollControlIntoView(panel);
                Application.DoEvents();
                if (!viewport.RectangleToScreen(viewport.ClientRectangle).Contains(panel.RectangleToScreen(panel.ClientRectangle))) throw new Exception("Expanded advanced settings cannot be reached by scrolling at minimum size");
                panel.Visible = false;
                viewport.AutoScrollPosition = Point.Empty;
            }
            captureSettings.Language = originalLanguage;
            typeof(MainWindow).GetMethod("ApplyLanguage", flags).Invoke(window, null);
            if (args.Length == 1)
            {
                File.Copy(Path.Combine(review, "desktop-ru.png"), Path.Combine(review, "desktop.png"), true);
                File.Copy(Path.Combine(review, "compact-ru.png"), Path.Combine(review, "compact.png"), true);
                CaptureTrayFixture((Theme)typeof(MainWindow).GetField("theme", flags).GetValue(window), Path.Combine(review, "tray-states.png"));
            }
            window.Close(); Application.DoEvents();
            if (window.Visible || window.IsDisposed) throw new Exception("Close did not preserve tray application");
            typeof(MainWindow).GetMethod("Reveal", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
            Application.DoEvents();
            if (!window.Visible) throw new Exception("Tray activation did not restore settings");
            var tray = (NotifyIcon)typeof(MainWindow).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            ((ToolStripMenuItem)tray.ContextMenuStrip.Items["TrayExit"]).PerformClick();
            Application.DoEvents();
            if (!window.IsDisposed) throw new Exception("Explicit tray exit did not close application");
        }
        using (var hidden = new MainWindow(true))
        {
            hidden.Show(); Application.DoEvents();
            if (hidden.Visible || hidden.IsDisposed) throw new Exception("Tray startup exposed settings");
            var tray = (NotifyIcon)typeof(MainWindow).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hidden);
            ((ToolStripMenuItem)tray.ContextMenuStrip.Items["TraySettings"]).PerformClick(); Application.DoEvents();
            if (!hidden.Visible) throw new Exception("Tray settings action did not show hidden startup");
            ((ToolStripMenuItem)tray.ContextMenuStrip.Items["TrayExit"]).PerformClick(); Application.DoEvents();
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
                if (bitmap.GetPixel(bitmap.Width * 3 / 4, bitmap.Height * 3 / 4).ToArgb() != expected[state].ToArgb()) throw new Exception("Tray badge does not render its actual state color");
        }
        var desktopState = (DesktopState)typeof(MainWindow).GetField("state", flags).GetValue(window);
        desktopState.RefreshError = "Test refresh failure";
        typeof(MainWindow).GetMethod("UpdateTray", flags).Invoke(window, null);
        var tray = (NotifyIcon)typeof(MainWindow).GetField("tray", flags).GetValue(window);
        if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(2)) || !tray.Text.StartsWith(UiText.Get("Ошибка")) && !tray.Text.StartsWith("Could not")) throw new Exception("Tray does not explain the current refresh error");
        desktopState.RefreshError = null;
        typeof(MainWindow).GetMethod("UpdateTray", flags).Invoke(window, null);
        Console.WriteLine("PASS: cached native tray icons, gray/green/red rendered badges, actionable error tooltip");
    }
    private static List<Control> Interactive(Control parent)
    {
        var result = new List<Control>();
        foreach (Control control in parent.Controls)
        {
            if (!control.Visible) continue;
            if (control is Button || control is CheckBox || control is TrackBar || control is ListBox || control is ComboBox) result.Add(control);
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
            if (!status.Text.Contains(UiText.Get("Обработка выключена")) || status.Text.Contains(UiText.Get("начните запись"))) throw new Exception("Globally disabled idle microphone invites recording instead of showing off");
            settings.Enabled = true; profile.Enabled = false; render();
            if (!status.Text.Contains(UiText.Get("Обработка выключена")) || status.Text.Contains(UiText.Get("начните запись"))) throw new Exception("Disabled idle profile invites recording instead of showing off");
            profile.Enabled = true; render();
            if (!status.Text.Contains(UiText.Get("Ожидание звука"))) throw new Exception("Enabled idle microphone lost its waiting state");
        }
        finally
        {
            settings.Enabled = globalBefore; profile.Enabled = profileBefore; activity[device.Id] = activityBefore; render();
        }
        Console.WriteLine("PASS: disabled global/profile idle status takes precedence over waiting for audio");
    }
    private static void TestLanguages(MainWindow window, BindingFlags flags)
    {
        var selector = (ComboBox)typeof(MainWindow).GetField("language", flags).GetValue(window);
        var settings = (Settings)typeof(MainWindow).GetField("settings", flags).GetValue(window);
        var global = (CheckBox)typeof(MainWindow).GetField("global", flags).GetValue(window);
        var tray = (NotifyIcon)typeof(MainWindow).GetField("tray", flags).GetValue(window);
        string path = (string)typeof(MainWindow).GetField("settingsPath", flags).GetValue(window);
        string original = settings.Language;
        try
        {
            selector.SelectedIndex = 2;
            if (global.Text != "Process all microphones" || tray.ContextMenuStrip.Items["TraySettings"].Text != "Settings" || Settings.Load(path).Language != "en") throw new Exception("English did not apply instantly to the window, tray and persisted preference");
            var bound = (Dictionary<Control, string>)typeof(MainWindow).GetField("originalTexts", flags).GetValue(window);
            foreach (var pair in bound) if (Regex.IsMatch(pair.Key.Text, @"\p{IsCyrillic}")) throw new Exception("Untranslated English interface text: " + pair.Key.Text);
            if (UiText.Message("Windows не подтвердил работу эффекта\nНовое подключение отменено") != "Windows did not confirm that noise reduction is working\nThe new connection was rolled back") throw new Exception("Integration error and rollback message were not localized");
            selector.SelectedIndex = 1;
            if (global.Text != "Обработка всех микрофонов" || tray.ContextMenuStrip.Items["TraySettings"].Text != "Настройки" || Settings.Load(path).Language != "ru") throw new Exception("Russian did not apply instantly or persist");
            selector.SelectedIndex = 0;
            if (Settings.Load(path).Language != "system" || UiText.EffectiveLanguage != LanguagePreference.Resolve("system", CultureInfo.CurrentUICulture.Name)) throw new Exception("System language did not retain the system preference");
        }
        finally { settings.Language = original; typeof(MainWindow).GetMethod("ApplyLanguage", flags).Invoke(window, null); }
        Console.WriteLine("PASS: live English/Russian/System selection, window/tray/error translations and persisted locale");
    }
    private static void TestAdvanced(MainWindow window, BindingFlags flags)
    {
        var toggle = (Button)typeof(MainWindow).GetField("advanced", flags).GetValue(window);
        var panel = (Panel)typeof(MainWindow).GetField("advancedPanel", flags).GetValue(window);
        if (panel.Visible) throw new Exception("Advanced settings should start collapsed");
        var list = (ListBox)typeof(MainWindow).GetField("devices", flags).GetValue(window);
        var device = list.SelectedItem as DeviceInfo;
        if (device == null) throw new Exception("A microphone is required for profile reset verification");
        var settings = (Settings)typeof(MainWindow).GetField("settings", flags).GetValue(window);
        var profile = settings.For(device.Id);
        int attenuation = profile.Attenuation;
        bool global = settings.Enabled, enabled = profile.Enabled;
        try
        {
            toggle.PerformClick();
            if (!panel.Visible) throw new Exception("Advanced settings did not expand");
            settings.Enabled = false; profile.Enabled = false; profile.Attenuation = 50;
            typeof(MainWindow).GetMethod("ResetProfile", flags).Invoke(window, null);
            if (profile.Attenuation != 24 || settings.Enabled || profile.Enabled) throw new Exception("Profile reset changed global/profile enabled state or failed to restore 24 dB");
            string diagnostics = (string)typeof(MainWindow).GetMethod("BuildDiagnostics", flags).Invoke(window, null);
            if (!diagnostics.Contains(device.Id) || !diagnostics.Contains("RNNoise") || !diagnostics.Contains("48 kHz") || !diagnostics.Contains("30 ms")) throw new Exception("Diagnostics omitted selected endpoint or actual engine parameters");
        }
        finally
        {
            settings.Enabled = global; profile.Enabled = enabled; profile.Attenuation = attenuation;
            typeof(MainWindow).GetMethod("Persist", flags).Invoke(window, null);
            typeof(MainWindow).GetMethod("ShowProfile", flags).Invoke(window, null);
            if (panel.Visible) toggle.PerformClick();
        }
        Console.WriteLine("PASS: collapsed advanced settings, selected-profile reset without global changes, real engine diagnostics");
    }
    private static void TestTrayProfiles(MainWindow window, BindingFlags flags)
    {
        var settings = (Settings)typeof(MainWindow).GetField("settings", flags).GetValue(window);
        var state = (DesktopState)typeof(MainWindow).GetField("state", flags).GetValue(window);
        var list = (ListBox)typeof(MainWindow).GetField("devices", flags).GetValue(window);
        var menu = (ToolStripMenuItem)typeof(MainWindow).GetField("trayDevices", flags).GetValue(window);
        var snapshots = (Dictionary<string, CaptureSessionSnapshot>)typeof(MainWindow).GetField("usageSnapshots", flags).GetValue(window);
        var defaults = typeof(MainWindow).GetField("defaultMicrophoneId", flags);
        var updating = typeof(MainWindow).GetField("updating", flags);
        var items = new List<object>(); foreach (object item in list.Items) items.Add(item);
        object selected = list.SelectedItem, defaultBefore = defaults.GetValue(window);
        bool globalBefore = settings.Enabled;
        var first = new DeviceInfo { Id = "tray-fixture-default", Name = "Default fixture" };
        var second = new DeviceInfo { Id = "tray-fixture-busy", Name = "In-use fixture" };
        var unknown = new DeviceInfo { Id = "tray-fixture-unknown", Name = "Unknown fixture" };
        Action rebuild = delegate { typeof(MainWindow).GetMethod("RebuildTrayDevices", flags).Invoke(window, null); };
        list.BeginUpdate(); updating.SetValue(window, true);
        try
        {
            list.Items.Clear(); list.Items.Add(first); list.Items.Add(second); list.Items.Add(unknown); defaults.SetValue(window, first.Id);
            snapshots.Clear();
            snapshots[first.Id] = new CaptureSessionSnapshot { Available = true, EndpointId = first.Id };
            var active = new CaptureSessionSnapshot { Available = true, EndpointId = second.Id };
            active.Sessions.Add(new CaptureSessionInfo { ApplicationName = "Browser fixture", ProcessId = 42 }); snapshots[second.Id] = active;
            snapshots[unknown.Id] = new CaptureSessionSnapshot { Available = false, EndpointId = unknown.Id };
            settings.Enabled = false; settings.For(first.Id).Enabled = true; settings.For(second.Id).Enabled = false;
            rebuild();
            if (!menu.DropDownItems[0].Text.Contains(UiText.Get("По умолчанию")) || !menu.DropDownItems[0].Text.Contains(UiText.Get("Не используется")) || !menu.DropDownItems[1].Text.Contains(UiText.Get("Используется")) || !menu.DropDownItems[2].Text.Contains(UiText.Get("Активность неизвестна"))) throw new Exception("Tray confused default microphone, actual capture use and unknown activity");
            ((ToolStripMenuItem)menu.DropDownItems[1]).PerformClick();
            if (!settings.Enabled || !settings.For(second.Id).Enabled || !settings.For(first.Id).Enabled) throw new Exception("Tray enabling a microphone did not enable global processing while preserving other profiles");
            state.Integrating = true; rebuild();
            foreach (ToolStripItem item in menu.DropDownItems) if (item.Enabled) throw new Exception("Tray profile switch remained enabled during integration");
        }
        finally
        {
            state.Integrating = false; settings.Enabled = globalBefore;
            settings.Profiles.RemoveAll(profile => profile.Id == first.Id || profile.Id == second.Id || profile.Id == unknown.Id);
            list.Items.Clear(); foreach (object item in items) list.Items.Add(item); list.SelectedItem = selected; defaults.SetValue(window, defaultBefore); snapshots.Clear();
            updating.SetValue(window, false); list.EndUpdate();
            typeof(MainWindow).GetMethod("SaveTrayProfile", flags).Invoke(window, null);
        }
        Console.WriteLine("PASS: tray per-device enable, global resume preserving profiles, actual-use/default/unknown distinction and busy guards");
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
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(2)) || !tray.Text.Contains(UiText.Get("Улучшения звука Windows выключены"))) throw new Exception("Disabled Windows enhancements did not show a red explanation");
            settings.For(current.Id).Enabled = false; update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(0)) || !tray.Text.Contains(UiText.Get("Шумоподавление выключено"))) throw new Exception("Disabled default profile did not show gray and explain why");
            settings.For(current.Id).Enabled = true; settings.Enabled = false; update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(0))) throw new Exception("Disabled global processing did not show a gray badge");
            settings.Enabled = true; current.EnhancementsEnabled = true; current.Attached = false; update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(0)) || !tray.Text.Contains(UiText.Get("Эффект не подключён"))) throw new Exception("Unattached default microphone incorrectly showed enabled");
            defaultField.SetValue(window, null); update();
            if (!Object.ReferenceEquals(tray.Icon, theme.TrayIcon(0)) || !tray.Text.Contains(UiText.Get("Основной микрофон не найден"))) throw new Exception("Missing default microphone did not show gray");
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
        var record = (Button)typeof(MainWindow).GetField("record", flags).GetValue(window);
        using (var bitmap = new Bitmap(record.Width, record.Height))
        {
            record.DrawToBitmap(bitmap, record.ClientRectangle);
            if (bitmap.GetPixel(0, 0).ToArgb() != Color.White.ToArgb()) throw new Exception("Primary action background fills its rounded corners");
        }
        Console.WriteLine("PASS: accessible native control names, keyboard tab stops and global-to-microphone focus order");
    }
    private static void Capture(MainWindow window, Size size, string path)
    {
        size = new Size(size.Width * window.DeviceDpi / 96, size.Height * window.DeviceDpi / 96);
        window.Size = size;
        window.PerformLayout(); Application.DoEvents();
        var controls = Interactive(window);
        foreach (var control in controls)
        {
            Rectangle area = window.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            if (!control.Parent.ClientRectangle.Contains(control.Bounds)) throw new Exception("Control is clipped by its parent: " + control.AccessibleName + " " + control.Bounds + " parent " + control.Parent.ClientRectangle);
            if (!window.ClientRectangle.Contains(area) || area.Width == 0 || area.Height == 0) throw new Exception("Control is clipped at " + size + ": " + control.AccessibleName + " " + area);
        }
        for (int first = 0; first < controls.Count; first++)
            for (int second = first + 1; second < controls.Count; second++)
            {
                Rectangle a = controls[first].RectangleToScreen(controls[first].ClientRectangle);
                Rectangle b = controls[second].RectangleToScreen(controls[second].ClientRectangle);
                if (a.IntersectsWith(b)) throw new Exception("Interactive controls overlap at " + size + ": " + controls[first].AccessibleName + " " + a + " parent " + controls[first].Parent.Bounds + " / " + controls[second].AccessibleName + " " + b + " parent " + controls[second].Parent.Bounds);
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
        int native = theme.TrayIcon(0).Width;
        int rowHeight = native * 4 + 24;
        using (var image = new Bitmap(760, 76 + rowHeight * 2))
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.FromArgb(241, 244, 242));
            TextRenderer.DrawText(graphics, "Tray icons: native " + native + " px and 4x", theme.Section, new Point(18, 14), Theme.Ink);
            string[] names = { "Выключено", "Включено", "Ошибка" };
            for (int state = 0; state < 3; state++) TextRenderer.DrawText(graphics, names[state], theme.Body, new Point(134 + state * 180, 44), Theme.Ink);
            Color[] backgrounds = { Color.White, Color.FromArgb(32, 42, 38) };
            for (int row = 0; row < 2; row++)
            {
                int top = 72 + row * rowHeight;
                using (var brush = new SolidBrush(backgrounds[row])) graphics.FillRectangle(brush, 12, top, 736, rowHeight - 8);
                TextRenderer.DrawText(graphics, row == 0 ? "Светлый" : "Тёмный", theme.Small, new Point(22, top + 33), row == 0 ? Theme.Secondary : Color.White);
                for (int state = 0; state < 3; state++)
                {
                    int left = 130 + state * 180;
                    graphics.DrawIcon(theme.TrayIcon(state), new Rectangle(left, top + (rowHeight - native) / 2, native, native));
                    using (var icon = theme.TrayIcon(state).ToBitmap())
                    {
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.DrawImage(icon, new Rectangle(left + native + 16, top + 8, native * 4, native * 4));
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
