using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using NoiseReduction.Desktop;
using NoiseReduction.Platform;

namespace NoiseReduction.WinUI
{
    internal sealed class NativeTray : IDisposable
    {
        private const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x800, MF_DEFAULT = 0x1000;
        private const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTALIGN = 8;
        private const int CommandGlobal = 1, CommandSettings = 2, CommandExit = 3, CommandMicrophone = 100;
        private readonly DesktopController controller;
        private readonly NotifyIcon tray;
        private readonly NativeWindow owner = new NativeWindow();
        private readonly Icon[] icons = new Icon[6];
        private readonly Action reveal, flyout, exit;
        private bool disposed, menuOpen;
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();

        internal NativeTray(DesktopController controller, Action reveal, Action flyout, Action exit)
        {
            this.controller = controller; this.reveal = reveal; this.flyout = flyout; this.exit = exit;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            owner.CreateHandle(new CreateParams());
            tray = new NotifyIcon { Visible = true };
            tray.MouseUp += (_, args) =>
            {
                if (args.Button == MouseButtons.Left) flyout();
                else if (args.Button == MouseButtons.Right) ShowMenu();
            };
            controller.Changed += Update;
            Update();
            Promote();
            _ = System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ => { if (!disposed) Promote(); }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Windows прячет новые значки в скрытые, показываем свой один раз, пока пользователь сам не выбрал
        private void Promote()
        {
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return;
                    foreach (string name in root.GetSubKeyNames())
                        using (var key = root.OpenSubKey(name, true))
                        {
                            if (!(key?.GetValue("ExecutablePath") is string path) || key.GetValue("IsPromoted") != null) continue;
                            path = path.Replace("{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);
                            if (!string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                            key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            tray.Visible = false; tray.Visible = true;
                        }
                }
            }
            catch (Exception) { }
        }

        internal static bool SystemLight()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) return key?.GetValue("SystemUsesLightTheme") is int value && value != 0; }
            catch (Exception) { return false; }
        }

        private static string T(string value) => UiText.Get(value).Replace("&", "");
        private static string ShortName(string value) => value.Length <= 32 ? value : value.Substring(0, 31).TrimEnd() + "…";

        private void Update()
        {
            if (disposed) return;
            DeviceInfo current = null;
            foreach (var device in controller.Devices) if (device.Id == controller.DefaultId) { current = device; break; }
            int state = !string.IsNullOrEmpty(controller.Error) ? 2 : current != null && controller.IsEnabled(current.Id) ? controller.HasProcessingError(current.Id) ? 2 : current.Attached ? 1 : 0 : 0;
            var icon = GetIcon(state, SystemLight());
            if (tray.Icon != icon) tray.Icon = icon;
            string hint = "Noise Reduction\n" + T(state == 2 ? "Ошибка обработки" : state == 1 ? "Шумоподавление включено" : "Шумоподавление выключено");
            if (current != null) hint += "\n" + current.Name;
            hint = hint.Length > 127 ? hint.Substring(0, 126) + "…" : hint;
            if (tray.Text != hint) tray.Text = hint;
        }

        private async void ShowMenu()
        {
            if (menuOpen || disposed) return;
            menuOpen = true;
            try
            {
                await controller.ScanUsageAsync();
                if (disposed) return;
                // меню следует теме панели задач, как у системных значков
                try { SetPreferredAppMode(SystemLight() ? 3 : 2); FlushMenuThemes(); } catch (Exception) { }
                IntPtr menu = CreatePopupMenu();
                var ids = new List<string>();
                try
                {
                    uint busy = controller.Busy ? MF_GRAYED : 0;
                    AppendMenu(menu, MF_STRING | busy | (controller.GlobalEnabled ? MF_CHECKED : 0), (UIntPtr)CommandGlobal, T("Шумоподавление"));
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    foreach (var device in controller.Devices)
                    {
                        var parts = new List<string>();
                        var apps = controller.Usage(device.Id);
                        if (apps != null && apps.Count > 0) parts.Add(string.Join(", ", System.Linq.Enumerable.Take(apps, 2)) + (apps.Count > 2 ? "…" : ""));
                        if (device.Id == controller.DefaultId) parts.Add(T("основной"));
                        string text = ShortName(device.Name).Replace("&", "&&") + (parts.Count > 0 ? "\t" + string.Join(" · ", parts) : "");
                        AppendMenu(menu, MF_STRING | busy | (controller.IsEnabled(device.Id) ? MF_CHECKED : 0), (UIntPtr)(uint)(CommandMicrophone + ids.Count), text);
                        ids.Add(device.Id);
                    }
                    if (ids.Count == 0) AppendMenu(menu, MF_STRING | MF_GRAYED, UIntPtr.Zero, T("Доступные микрофоны не найдены"));
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    AppendMenu(menu, MF_STRING | MF_DEFAULT, (UIntPtr)CommandSettings, T("Настройки"));
                    AppendMenu(menu, MF_STRING | busy, (UIntPtr)CommandExit, T("Выход"));
                    GetCursorPos(out var point);
                    SetForegroundWindow(owner.Handle);
                    int command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, point.X, point.Y, owner.Handle, IntPtr.Zero);
                    PostMessage(owner.Handle, 0, IntPtr.Zero, IntPtr.Zero);
                    if (command == CommandGlobal) controller.SetGlobal(!controller.GlobalEnabled);
                    else if (command == CommandSettings) reveal();
                    else if (command == CommandExit) exit();
                    else if (command >= CommandMicrophone && command - CommandMicrophone < ids.Count) controller.ToggleMicrophone(ids[command - CommandMicrophone]);
                }
                finally { DestroyMenu(menu); }
            }
            finally { menuOpen = false; }
        }

        // тонкий контур микрофона с волной внутри, цвет волны показывает состояние
        private Icon GetIcon(int state, bool light)
        {
            int index = state * 2 + (light ? 1 : 0);
            if (icons[index] != null) return icons[index];
            int size = Math.Max(16, GetSystemMetrics(49));
            using (var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.ScaleTransform(size / 16f, size / 16f);
                    var ink = light ? Color.FromArgb(26, 26, 26) : Color.White;
                    var wave = state == 1 ? (light ? Color.FromArgb(10, 140, 128) : Color.FromArgb(23, 210, 190)) : state == 2 ? (light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 110, 110)) : Color.FromArgb(140, 140, 140);
                    using (var pen = new Pen(ink, 1.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    using (var path = new GraphicsPath())
                    {
                        path.AddArc(4.5f, 0.75f, 7, 7, 180, 180); path.AddArc(4.5f, 3.75f, 7, 7, 0, 180); path.CloseFigure();
                        graphics.DrawPath(pen, path);
                        path.Reset();
                        path.AddLine(2, 6.5f, 2, 8); path.AddArc(2, 2.25f, 12, 11.25f, 180, -180); path.AddLine(14, 8, 14, 6.5f);
                        graphics.DrawPath(pen, path);
                        graphics.DrawLine(pen, 8, 13.6f, 8, 15.25f);
                        graphics.DrawLine(pen, 5.5f, 15.25f, 10.5f, 15.25f);
                    }
                    using (var pen = new Pen(wave, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        var points = new PointF[17];
                        for (int i = 0; i < points.Length; i++) { float t = i / 16f; points[i] = new PointF(5.9f + 4.2f * t, 6.2f - (float)Math.Sin(t * Math.PI * 2) * 1.9f); }
                        graphics.DrawLines(pen, points);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (var icon = Icon.FromHandle(handle)) icons[index] = (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
            return icons[index];
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; controller.Changed -= Update;
            tray.Visible = false; tray.Dispose(); owner.DestroyHandle();
            foreach (var icon in icons) if (icon != null) icon.Dispose();
        }
    }
}
