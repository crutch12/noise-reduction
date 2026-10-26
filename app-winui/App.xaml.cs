using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace NoiseReduction.WinUI
{
    public partial class App : Application
    {
        internal static App CurrentApp { get; private set; }
        internal DesktopController Controller { get; private set; }
        internal MainWindow Window { get; private set; }
        internal bool Exiting { get; private set; }
        internal DispatcherQueue Dispatcher { get; private set; }
        private NativeTray tray;
        private TrayFlyout flyout;

        public App()
        {
            CurrentApp = this;
            // до первого открытия настроек видимых окон нет, приложение всё равно живёт в трее
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Dispatcher = DispatcherQueue.GetForCurrentThread();
            Controller = new DesktopController();
            // трей первым, окно уже знает, удалось ли занять горячую клавишу
            tray = new NativeTray(Controller, ShowSettings, () => flyout?.Toggle(), ExitApplication);
            Program.WindowReady();
            flyout = new TrayFlyout(Controller, ShowSettings);
            if (!Program.StartHidden) ShowSettings();
        }

        // при запуске в трей окно не создаётся, пока его не откроют
        internal void ShowSettings()
        {
            if (Exiting) return;
            if (Window == null) Window = new MainWindow(Controller);
            Window.Reveal();
        }

        internal void ExitApplication()
        {
            Exiting = true;
            try
            {
                tray?.Dispose();
                Controller?.Dispose();
            }
            finally { Exit(); }
        }
    }
}
