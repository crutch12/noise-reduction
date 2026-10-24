using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NoiseReduction.Desktop;
using Windows.Graphics;
using static NoiseReduction.WinUI.MicrophoneText;

namespace NoiseReduction.WinUI
{
    public sealed partial class MainWindow : Window
    {
        private readonly DesktopController controller;
        private readonly List<MicrophoneCard> cards = new List<MicrophoneCard>();
        private bool updating, dismissed;
        private string renderedLanguage;
        private string localError;
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        internal IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

        internal MainWindow(DesktopController controller)
        {
            this.controller = controller;
            updating = true;
            InitializeComponent();
            SystemBackdrop = new ThinAcrylic();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBar);
            AppImage.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Microphone.png")));
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "NoiseReduction.ico"));
            int dpi = (int)GetDpiForWindow(Handle);
            AppWindow.Resize(new SizeInt32(560 * dpi / 96, 680 * dpi / 96));
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 440 * dpi / 96;
                presenter.PreferredMinimumHeight = 420 * dpi / 96;
            }
            AppWindow.Closing += (_, args) =>
            {
                if (App.CurrentApp.Exiting) return;
                args.Cancel = true; AppWindow.Hide(); controller.WatchUsage = false;
            };
            controller.Changed += ControllerChanged;
            Render();
        }

        internal void Reveal()
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
            AppWindow.Show();
            Activate();
            SetForegroundWindow(Handle);
            controller.WatchUsage = true;
            _ = controller.ScanUsageAsync();
            Render();
        }

        private void ControllerChanged() => DispatcherQueue.TryEnqueue(() => { if (AppWindow.IsVisible) Render(); });

        private void Localize()
        {
            UiText.Language = controller.Language;
            Label(MasterSwitch); Label(StartupSwitch);
            GlobalTitle.Text = T("Шумоподавление");
            GlobalHelp.Text = T("Пауза сохраняет настройки каждого микрофона");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MasterSwitch, T("Шумоподавление"));
            MicrophonesHeading.Text = T("Микрофоны");
            ToolTipService.SetToolTip(RefreshButton, T("Обновить список микрофонов"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RefreshButton, T("Обновить список микрофонов"));
            EmptyText.Text = T("Доступные микрофоны не найдены");
            AppHeading.Text = T("Приложение");
            LanguageLabel.Text = T("Язык");
            LanguagePicker.Items.Clear();
            LanguagePicker.Items.Add(new ComboBoxItem { Content = T("Как в системе"), Tag = "system" });
            LanguagePicker.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
            LanguagePicker.Items.Add(new ComboBoxItem { Content = "Русский", Tag = "ru" });
            StartupLabel.Text = T("Запускать при входе в Windows");
            TrayHelp.Text = T("Крестик оставляет программу в трее");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StartupSwitch, T("Запускать при входе в Windows"));
            EngineText.Text = T("RNNoise · формат движка 48 кГц · задержка движка 30 мс");
            foreach (var card in cards) card.Localize();
            renderedLanguage = controller.Language;
        }

        private void Render()
        {
            updating = true;
            try
            {
                var ids = controller.Devices.Select(device => device.Id).ToList();
                if (!ids.SequenceEqual(cards.Select(card => card.Id)))
                {
                    var previous = cards.ToDictionary(card => card.Id);
                    cards.Clear(); MicrophoneList.Children.Clear();
                    foreach (string id in ids)
                    {
                        if (!previous.TryGetValue(id, out var card)) { card = new MicrophoneCard(controller, id, Run); card.Localize(); }
                        cards.Add(card); MicrophoneList.Children.Add(card.View);
                    }
                    var expand = cards.FirstOrDefault(card => card.Id == controller.DefaultId) ?? cards.FirstOrDefault();
                    if (expand != null && previous.Count == 0) expand.View.IsExpanded = true;
                }
                if (renderedLanguage != controller.Language) Localize();
                EmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var card in cards) card.Update();
                MasterSwitch.IsOn = controller.GlobalEnabled;
                MasterSwitch.IsEnabled = !controller.Busy;
                RefreshButton.IsEnabled = !controller.Busy;
                string error = localError ?? controller.Error ?? controller.RefreshError;
                if (string.IsNullOrEmpty(error)) dismissed = false;
                ErrorBar.Message = string.IsNullOrEmpty(error) ? "" : UiText.Message(error);
                ErrorBar.IsOpen = !string.IsNullOrEmpty(error) && !dismissed;
                StartupSwitch.IsOn = controller.StartupEnabled;
                LanguagePicker.SelectedIndex = controller.Language == "en" ? 1 : controller.Language == "ru" ? 2 : 0;
            }
            finally { updating = false; }
        }

        private async void Run(Func<Task> action)
        {
            localError = null;
            try { await action(); }
            catch (Exception error) { localError = error.Message; }
            Render();
        }
        private void MasterToggled(object sender, RoutedEventArgs args) { if (!updating) controller.SetGlobal(MasterSwitch.IsOn); }
        private void LanguageChanged(object sender, SelectionChangedEventArgs args) { if (!updating && LanguagePicker.SelectedItem is ComboBoxItem item) controller.SetLanguage((string)item.Tag); }
        private void StartupToggled(object sender, RoutedEventArgs args) { if (!updating) controller.SetStartup(StartupSwitch.IsOn); }
        private void RefreshClicked(object sender, RoutedEventArgs args) { controller.Refresh(); _ = controller.ScanUsageAsync(); }
        private void ErrorClosed(InfoBar sender, InfoBarClosedEventArgs args) { if (!updating) { dismissed = true; localError = null; } }
    }
}
