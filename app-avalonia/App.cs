using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{
    internal sealed partial class App : Application
    {
        internal static bool Exiting;
        internal static bool StartHidden;
        internal static Action ShowRequested;
        internal static bool ShortcutAvailable { get; private set; }
        private Controller controller;
        private VoiceTest voice;
        private GlobalShortcut shortcut;
        private MainWindow window;
        private TrayPanel panel;
        private TrayIcon tray;
        private NativeMenu menu;
        private NativeMenuItem toggle, settings, exit;
        private readonly Dictionary<string, NativeMenuItem> microphones = new(StringComparer.Ordinal);
        private DispatcherTimer hostTimer;
        private bool probing, stopping;
        private long lastClick;
        private (State State, bool Light)? iconState;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                controller = new Controller(action => Dispatcher.UIThread.Post(action));
                shortcut = new GlobalShortcut(() => Dispatcher.UIThread.Post(() => controller.Change(() => controller.Settings.Enabled = !controller.Settings.Enabled)));
                ShortcutAvailable = shortcut.Available;
                voice = new VoiceTest(action => Dispatcher.UIThread.Post(action));
                UiText.Language = controller.Settings.Language;
                RequestedThemeVariant = ThemeVariant.Default;
                AppTheme.RefreshPalette();
                window = new MainWindow(controller, voice);
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (!StartHidden) desktop.MainWindow = window;
                ShowRequested = () => Dispatcher.UIThread.Post(ShowWindow);
                Program.Attach(controller);
                Program.QuitRequested = () => Dispatcher.UIThread.Post(() => _ = ExitAsync(desktop, true));
                window.ExitRequested = () => _ = ExitAsync(desktop);
                window.UninstallRequested = () =>
                {
                    try { Installation.RequestUninstall(); _ = ExitAsync(desktop); }
                    catch (Exception error) { ShowError(error.Message); }
                };
                panel = new TrayPanel(controller, ShowWindow);
                toggle = new NativeMenuItem { ToggleType = MenuItemToggleType.CheckBox };
                toggle.Click += (_, _) => controller.Change(() => controller.Settings.Enabled = !controller.Settings.Enabled);
                settings = new NativeMenuItem(); settings.Click += (_, _) => ShowWindow();
                exit = new NativeMenuItem(); exit.Click += (_, _) => _ = ExitAsync(desktop);
                menu = new NativeMenu();
                tray = new TrayIcon { ToolTipText = "Noise Reduction", Menu = menu, IsVisible = true };
                tray.Clicked += (_, _) =>
                {
                    long now = Stopwatch.GetTimestamp();
                    double elapsed = (now - lastClick) / (double)Stopwatch.Frequency;
                    if (lastClick != 0 && elapsed < 0.5) { lastClick = 0; panel.Hide(); ShowWindow(); }
                    else { lastClick = now; panel.Toggle(window); }
                };
                TrayIcon.SetIcons(this, new TrayIcons { tray });
                controller.Changed += UpdateTray;
                ActualThemeVariantChanged += (_, _) => { window.Render(); UpdateTray(); };
                UpdateTray();
                desktop.ShutdownRequested += (_, args) =>
                {
                    if (Exiting) return;
                    args.Cancel = true;
                    _ = ExitAsync(desktop);
                };
                desktop.Exit += (_, _) => { ShowRequested = null; hostTimer?.Stop(); shortcut.Dispose(); voice.Dispose(); controller.Dispose(); };
                hostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                hostTimer.Tick += (_, _) => _ = ProbeTrayAsync();
                hostTimer.Start();
                _ = ProbeTrayAsync();
                if (!StartHidden) ShowWindow();
            }
            base.OnFrameworkInitializationCompleted();
        }

        private void ShowWindow()
        {
            panel?.Hide();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = window;
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            WindowLayout.Fit(window);
            window.Activate();
        }

        private async Task ExitAsync(IClassicDesktopStyleApplicationLifetime desktop, bool command = false)
        {
            if (stopping) return;
            stopping = true;
            panel.Hide(); voice.Clear();
            try
            {
                await controller.StopAsync();
                Exiting = true; hostTimer?.Stop(); tray.Dispose(); desktop.Shutdown();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                if (!command) await ShowErrorAsync(error.Message);
                Exiting = true; hostTimer?.Stop(); tray.Dispose(); desktop.Shutdown(1);
            }
        }

        private void ShowError(string message) => _ = ShowErrorAsync(message);

        private Task ShowErrorAsync(string message)
        {
            ShowWindow();
            var error = new Window { Title = AppTheme.T("Не получилось"), Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                RequestedThemeVariant = window.RequestedThemeVariant,
                Background = AppTheme.Background,
                FlowDirection = UiText.RightToLeft ? global::Avalonia.Media.FlowDirection.RightToLeft : global::Avalonia.Media.FlowDirection.LeftToRight };
            var close = new Button { Content = AppTheme.T("Закрыть") }; close.Click += (_, _) => error.Close();
            error.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { AppTheme.Text(message), close } };
            return error.ShowDialog(window);
        }

        private void UpdateTray()
        {
            UiText.Language = controller.Settings.Language;
            toggle.Header = AppTheme.T("Шумоподавление"); toggle.IsChecked = controller.Settings.Enabled;
            settings.Header = AppTheme.T("Настройки"); exit.Header = AppTheme.T("Выход");
            string[] ids = controller.Devices.Select(device => device.Id).ToArray();
            if (!microphones.Keys.SequenceEqual(ids))
            {
                microphones.Clear(); menu.Items.Clear(); menu.Items.Add(toggle);
                foreach (var device in controller.Devices)
                {
                    string id = device.Id;
                    var item = new NativeMenuItem { ToggleType = MenuItemToggleType.CheckBox };
                    item.Click += (_, _) => controller.ChangeProfile(id, profile => profile.Enabled = !profile.Enabled);
                    microphones.Add(id, item); menu.Items.Add(item);
                }
                menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(settings); menu.Items.Add(exit);
            }
            if (menu.Items.Count == 0) { menu.Items.Add(toggle); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(settings); menu.Items.Add(exit); }
            foreach (var device in controller.Devices)
            {
                microphones[device.Id].Header = AppTheme.DeviceName(controller, device);
                microphones[device.Id].IsChecked = controller.ProfileFor(device.Id).Enabled;
            }
            bool light = controller.Settings.TrayTheme == "light" || controller.Settings.TrayTheme == "system" && ActualThemeVariant != ThemeVariant.Dark;
            var current = (controller.State, light);
            if (iconState != current) { tray.Icon = TrayGlyph.Create(current.Item1, light); iconState = current; }
            tray.ToolTipText = "Noise Reduction · " + AppTheme.StateText(controller.State) +
                (controller.Devices.Count == 0 ? "" : "\n" + string.Join("\n", controller.Devices.Select(device => device.Name + ": " + AppTheme.StateText(controller.StatusFor(device.Id).State)))) +
                (string.IsNullOrEmpty(controller.Error) ? "" : "\n" + UiText.Message(controller.Error));
        }

        private async Task ProbeTrayAsync()
        {
            if (probing || stopping) return;
            probing = true;
            try
            {
                bool available = await Task.Run(async () =>
                {
                    if (!OperatingSystem.IsLinux()) return true;
                    try
                    {
                        using var process = new Process { StartInfo = new ProcessStartInfo("dbus-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
                        foreach (string argument in new[] { "--session", "--dest=org.freedesktop.DBus", "--type=method_call", "--print-reply", "/org/freedesktop/DBus", "org.freedesktop.DBus.NameHasOwner", "string:org.kde.StatusNotifierWatcher" }) process.StartInfo.ArgumentList.Add(argument);
                        process.Start();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try
                        {
                            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                            await process.WaitForExitAsync(timeout.Token);
                            return process.ExitCode == 0 && output.Contains("boolean true", StringComparison.Ordinal);
                        }
                        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); return false; }
                    }
                    catch (System.ComponentModel.Win32Exception) { return false; }
                });
                window.SetTrayAvailable(available);
                if (!available) panel.Hide();
            }
            finally { probing = false; }
        }
    }
}
