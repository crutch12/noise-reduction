using Microsoft.UI.Xaml;

namespace NoiseReduction.WinUI
{
    public partial class App : Application
    {
        internal static App CurrentApp { get; private set; }
        internal DesktopController Controller { get; private set; }
        internal MainWindow Window { get; private set; }
        internal bool Exiting { get; private set; }
        private NativeTray tray;
        private TrayFlyout flyout;

        public App()
        {
            CurrentApp = this;
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Controller = new DesktopController();
            // трей первым, окно уже знает, удалось ли занять горячую клавишу
            tray = new NativeTray(Controller, () => Window?.Reveal(), () => flyout?.Toggle(), ExitApplication);
            Window = new MainWindow(Controller);
            Program.WindowReady();
            flyout = new TrayFlyout(Controller, Window.Reveal);
            if (!Program.StartHidden) Window.Reveal();
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
