using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{
    internal sealed class App : Application
    {
        internal static bool Exiting;
        internal static bool StartHidden;
        private Controller controller;
        private MainWindow window;
        private TrayIcon tray;
        private NativeMenuItem toggle, settings, exit;

        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                controller = new Controller();
                window = new MainWindow(controller);
                toggle = new NativeMenuItem { ToggleType = NativeMenuItemToggleType.CheckBox };
                toggle.Click += (_, _) => controller.Change(() => controller.Settings.Enabled = !controller.Settings.Enabled);
                settings = new NativeMenuItem();
                settings.Click += (_, _) => ShowWindow();
                exit = new NativeMenuItem();
                exit.Click += (_, _) => { Exiting = true; controller.Dispose(); desktop.Shutdown(); };
                tray = new TrayIcon
                {
                    Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NoiseReduction/Assets/NoiseReduction.ico"))),
                    Menu = new NativeMenu { Items = { toggle, settings, new NativeMenuItemSeparator(), exit } },
                };
                tray.Clicked += (_, _) => ShowWindow();
                TrayIcon.SetIcons(this, new TrayIcons { tray });
                controller.Changed += UpdateTray;
                UpdateTray();
                desktop.Exit += (_, _) => { if (!Exiting) controller.Dispose(); };
                if (!StartHidden || VirtualMicrophone.NeedsInstall) ShowWindow();
            }
            base.OnFrameworkInitializationCompleted();
        }

        private void ShowWindow()
        {
            window.Show();
            window.Activate();
        }

        private void UpdateTray()
        {
            toggle.Header = UiText.Get("Шумоподавление");
            toggle.IsChecked = controller.Settings.Enabled;
            settings.Header = UiText.Get("Настройки");
            exit.Header = UiText.Get("Выход");
            tray.ToolTipText = "Noise Reduction";
        }
    }
}
