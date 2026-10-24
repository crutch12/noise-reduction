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
        private readonly Icon[] icons = new Icon[3];
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
        }

        private static string T(string value) => UiText.Get(value).Replace("&", "");
        private static string ShortName(string value) => value.Length <= 32 ? value : value.Substring(0, 31).TrimEnd() + "…";

        private void Update()
        {
            if (disposed) return;
            DeviceInfo current = null;
            foreach (var device in controller.Devices) if (device.Id == controller.DefaultId) { current = device; break; }
            int state = !string.IsNullOrEmpty(controller.Error) ? 2 : current != null && controller.IsEnabled(current.Id) ? controller.HasProcessingError(current.Id) ? 2 : current.Attached ? 1 : 0 : 0;
            var icon = GetIcon(state);
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
                // меню следует светлой или тёмной теме Windows
                bool light = true;
                try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) light = !(key?.GetValue("AppsUseLightTheme") is int value) || value != 0; } catch (Exception) { }
                try { SetPreferredAppMode(light ? 3 : 2); FlushMenuThemes(); } catch (Exception) { }
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

        private Icon GetIcon(int state)
        {
            if (icons[state] != null) return icons[state];
            int size = Math.Max(16, GetSystemMetrics(49));
            using (var source = new Icon(Path.Combine(AppContext.BaseDirectory, "NoiseReduction.ico"), size, size))
            using (var bitmap = source.ToBitmap())
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    float unit = size / 16f;
                    using (var border = new SolidBrush(Color.White)) graphics.FillEllipse(border, 8.5f * unit, 8.5f * unit, 7.5f * unit, 7.5f * unit);
                    Color color = state == 1 ? Color.FromArgb(16, 137, 62) : state == 2 ? Color.FromArgb(196, 43, 28) : Color.FromArgb(138, 138, 138);
                    using (var badge = new SolidBrush(color)) graphics.FillEllipse(badge, 9.25f * unit, 9.25f * unit, 6 * unit, 6 * unit);
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (var icon = Icon.FromHandle(handle)) icons[state] = (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
            return icons[state];
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
