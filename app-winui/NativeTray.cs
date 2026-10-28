using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        private const uint WM_HOTKEY = 0x0312, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_SETTINGCHANGE = 0x001A, WM_POWERBROADCAST = 0x0218, PBT_APMPOWERSTATUSCHANGE = 0x000A, WM_TRAY = 0x8000 + 1;
        private const uint NIN_BALLOONHIDE = 0x403, NIN_BALLOONTIMEOUT = 0x404, NIN_BALLOONUSERCLICK = 0x405;
        private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public int Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public uint TimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
            public uint InfoFlags; public Guid Item; public IntPtr BalloonIcon;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public int Size; public uint Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background; public string Menu, ClassName; public IntPtr SmallIcon;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        // окно только для сообщений трея, горячей клавиши и меню, на экране его нет
        private readonly WindowProcedure procedure;
        private readonly IntPtr window;
        private readonly uint taskbarCreated;
        private readonly IntPtr[] icons = new IntPtr[6];
        private IntPtr currentIcon;
        private string currentTip = "";
        private readonly Action reveal, flyout, exit;
        private bool disposed, menuOpen, afterDoubleClick;
        private string balloonId;
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
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
            procedure = Procedure;
            var windowClass = new WindowClass { Size = Marshal.SizeOf<WindowClass>(), Procedure = procedure, Instance = GetModuleHandle(null), ClassName = "NoiseReductionTray" };
            RegisterClassEx(ref windowClass);
            window = CreateWindowEx(0, "NoiseReductionTray", "Noise Reduction", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
            // после перезапуска Проводника значок нужно добавить заново
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            // Ctrl+Alt+N ставит шумодав на паузу и снимает с неё из любой программы
            HotkeyRegistered = RegisterHotKey(window, 1, 0x0002 | 0x0001 | 0x4000, 0x4E);
            AddIcon();
            controller.Changed += Update;
            controller.NewMicrophone += device =>
            {
                if (disposed) return;
                balloonId = device.Id;
                Balloon(T("Новый микрофон"), ShortName(device.Name) + "\n" + T("Нажмите, чтобы включить шумоподавление"));
            };
            controller.AnnounceNewMicrophones();
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
                            RemoveIcon(); AddIcon();
                        }
                }
            }
            catch (Exception) { }
        }

        internal static bool HotkeyRegistered { get; private set; }

        // тема трея из настроек, по умолчанию как у панели задач
        internal static bool TrayLight(DesktopController controller) => controller.TrayTheme == "light" || controller.TrayTheme != "dark" && SystemLight();

        internal static bool SystemLight()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) return key?.GetValue("SystemUsesLightTheme") is int value && value != 0; }
            catch (Exception) { return false; }
        }

        private IntPtr Procedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
        {
            // ошибка, вылетевшая отсюда в Windows, молча закрыла бы всю программу
            try
            {
                if (message == WM_HOTKEY) Hotkey();
                else if (message == WM_TRAY)
                {
                    uint mouse = (uint)lParam.ToInt64() & 0xFFFF;
                    // после двойного щелчка Windows шлёт ещё одно отпускание, панель на него не открываем
                    if (mouse == WM_LBUTTONUP) { if (afterDoubleClick) afterDoubleClick = false; else flyout(); }
                    else if (mouse == WM_RBUTTONUP) ShowMenu();
                    // окно настроек забирает фокус, панель от первого клика при этом закрывается сама
                    else if (mouse == WM_LBUTTONDBLCLK) { afterDoubleClick = true; reveal(); }
                    else if (mouse == NIN_BALLOONUSERCLICK) { if (balloonId != null) _ = controller.IntegrateAsync(balloonId, true); balloonId = null; }
                    else if (mouse == NIN_BALLOONHIDE || mouse == NIN_BALLOONTIMEOUT) balloonId = null;
                }
                // тема панели задач поменялась, значок перерисовываем под неё
                else if (message == WM_SETTINGCHANGE) Update();
                // переключились между сетью и батареей
                else if (message == WM_POWERBROADCAST && wParam.ToInt64() == PBT_APMPOWERSTATUSCHANGE) controller.PowerChanged();
                else if (message == taskbarCreated && taskbarCreated != 0 && !disposed) AddIcon();
            }
            catch (Exception) { }
            return DefWindowProc(handle, message, wParam, lParam);
        }

        private void Hotkey()
        {
            if (disposed || controller.Busy) return;
            controller.SetGlobal(!controller.GlobalEnabled);
            Balloon("Noise Reduction", T(controller.GlobalEnabled ? "Шумоподавление включено" : "Шумоподавление на паузе"));
        }

        private NotifyIconData Data(uint flags) => new NotifyIconData { Size = Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1, Flags = flags, CallbackMessage = WM_TRAY, Icon = currentIcon, Tip = currentTip, Info = "", InfoTitle = "" };

        private void AddIcon()
        {
            // версию не меняем: так Enter на значке с клавиатуры приходит как щелчок, а уведомления работают и без неё
            var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            Shell_NotifyIcon(NIM_ADD, ref data);
        }

        private void RemoveIcon() { var data = Data(0); Shell_NotifyIcon(NIM_DELETE, ref data); }

        private void Balloon(string title, string text)
        {
            var data = Data(NIF_INFO);
            data.InfoTitle = title.Length > 63 ? title.Substring(0, 63) : title;
            data.Info = text.Length > 255 ? text.Substring(0, 255) : text;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
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
            string hint = "Noise Reduction\n" + T(state == 2 ? "Ошибка обработки" : state == 1 ? "Шумоподавление включено" : "Шумоподавление выключено");
            if (current != null) hint += "\n" + current.Name;
            hint = hint.Length > 127 ? hint.Substring(0, 126) + "…" : hint;
            if (icon == currentIcon && hint == currentTip) return;
            currentIcon = icon; currentTip = hint;
            var data = Data(NIF_ICON | NIF_TIP);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
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
                try { SetPreferredAppMode(TrayLight(controller) ? 3 : 2); FlushMenuThemes(); } catch (Exception) { }
                IntPtr menu = CreatePopupMenu();
                var ids = new List<string>();
                try
                {
                    uint busy = controller.Busy ? MF_GRAYED : 0;
                    AppendMenu(menu, MF_STRING | busy | (controller.GlobalEnabled ? MF_CHECKED : 0), (UIntPtr)CommandGlobal, T("Шумоподавление"));
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    foreach (var device in controller.Devices)
                    {
                        string text = ShortName(device.Name).Replace("&", "&&") + (device.Id == controller.DefaultId ? "\t" + T("основной") : "");
                        AppendMenu(menu, MF_STRING | busy | (controller.IsEnabled(device.Id) ? MF_CHECKED : 0), (UIntPtr)(uint)(CommandMicrophone + ids.Count), text);
                        ids.Add(device.Id);
                    }
                    if (ids.Count == 0) AppendMenu(menu, MF_STRING | MF_GRAYED, UIntPtr.Zero, T("Доступные микрофоны не найдены"));
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    AppendMenu(menu, MF_STRING | MF_DEFAULT, (UIntPtr)CommandSettings, T("Настройки"));
                    AppendMenu(menu, MF_STRING | busy, (UIntPtr)CommandExit, T("Выход"));
                    GetCursorPos(out var point);
                    SetForegroundWindow(window);
                    int command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, point.X, point.Y, window, IntPtr.Zero);
                    PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);
                    if (command == CommandGlobal) controller.SetGlobal(!controller.GlobalEnabled);
                    else if (command == CommandSettings) reveal();
                    else if (command == CommandExit) exit();
                    else if (command >= CommandMicrophone && command - CommandMicrophone < ids.Count) {
                        string id = ids[command - CommandMicrophone];
                        controller.ToggleMicrophone(id);
                        var device = controller.Devices.FirstOrDefault(value => value.Id == id);
                        if (controller.IsEnabled(id) && device != null && !device.Attached) _ = controller.IntegrateAsync(id, true);
                    }
                }
                finally { DestroyMenu(menu); }
            }
            finally { menuOpen = false; }
        }

        private IntPtr GetIcon(int state, bool light)
        {
            int index = state * 2 + (light ? 1 : 0);
            if (icons[index] == IntPtr.Zero) icons[index] = TrayGlyph.Create(Math.Max(16, GetSystemMetrics(49)), state, light);
            return icons[index];
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; controller.Changed -= Update;
            UnregisterHotKey(window, 1);
            RemoveIcon(); DestroyWindow(window);
            foreach (var icon in icons) if (icon != IntPtr.Zero) DestroyIcon(icon);
        }
    }
}
