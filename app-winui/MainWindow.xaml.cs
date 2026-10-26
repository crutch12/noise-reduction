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
            // фокус без рамки, иначе Windows сразу подсвечивает первый переключатель
            DispatcherQueue.TryEnqueue(() => FocusSink.Focus(FocusState.Programmatic));
            controller.WatchUsage = true;
            _ = controller.ScanUsageAsync();
            Render();
        }

        private static string UninstallerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NoiseReduction", "NoiseReduction.Setup.exe");
        // своё окно удаления с подтверждением, Windows попросит права администратора
        private void UninstallClicked(object sender, RoutedEventArgs args)
        {
            try { using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UninstallerPath, "--uninstall") { UseShellExecute = true })) { } }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private void ControllerChanged() => DispatcherQueue.TryEnqueue(() => { if (AppWindow.IsVisible) Render(); });

        private void Localize()
        {
            UiText.Language = controller.Language;
            Label(MasterSwitch); Label(StartupSwitch); Label(NewMicrophonesSwitch);
            NewMicrophonesLabel.Text = T("Включать шумоподавление для новых микрофонов");
            NewMicrophonesHelp.Text = T("При подключении нового микрофона появится уведомление");
            UninstallLabel.Text = T("Удалить программу");
            UninstallHelp.Text = T("Эффекты микрофонов вернутся к настройкам Windows");
            UninstallButton.Content = T("Удалить");
            // кнопка только у установленной копии, переносной версии удалять нечего
            UninstallCard.Visibility = File.Exists(UninstallerPath) ? Visibility.Visible : Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NewMicrophonesSwitch, T("Включать шумоподавление для новых микрофонов"));
            GlobalTitle.Text = T("Шумоподавление");
            GlobalHelp.Text = T("Общий выключатель, настройки микрофонов сохраняются");
            Tip(MasterSwitch, "Для всех микрофонов, клавиша Ctrl+Alt+N");
            WindowThemeLabel.Text = T("Тема окна");
            TrayThemeLabel.Text = T("Тема панели трея");
            FillThemes(WindowThemePicker); FillThemes(TrayThemePicker);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MasterSwitch, T("Шумоподавление"));
            MicrophonesHeading.Text = T("Микрофоны");
            ToolTipService.SetToolTip(RefreshButton, T("Обновить список микрофонов"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RefreshButton, T("Обновить список микрофонов"));
            EmptyText.Text = T("Доступные микрофоны не найдены");
            AppHeading.Text = T("Приложение");
            LanguageLabel.Text = T("Язык");
            // пункты не пересоздаём, смена языка идёт прямо из этого списка
            if (LanguagePicker.Items.Count == 0)
            {
                LanguagePicker.Items.Add(new ComboBoxItem { Tag = "system" });
                for (int index = 0; index < Languages.Ids.Length; index++) LanguagePicker.Items.Add(new ComboBoxItem { Content = Languages.Names[index], Tag = Languages.Ids[index] });
            }
            ((ComboBoxItem)LanguagePicker.Items[0]).Content = T("Как в системе");
            // арабский и персидский пишутся справа налево, заголовок окна остаётся как у Windows
            WindowRoot.FlowDirection = UiText.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Reselect(LanguagePicker);
            StartupLabel.Text = T("Запускать при входе в Windows");
            TrayHelp.Text = T("Закрытое окно остаётся в трее") + (NativeTray.HotkeyRegistered ? "\n" + T("Ctrl+Alt+N включает и выключает шумоподавление") : "");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StartupSwitch, T("Запускать при входе в Windows"));
            foreach (var card in cards) card.Localize();
            renderedLanguage = controller.Language;
        }

        private static void Tip(FrameworkElement control, string text)
        {
            ToolTipService.SetToolTip(control, T(text));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(control, T(text));
        }

        // пункты создаются один раз, пересоздание во время выбора роняет WinUI
        private static readonly string[] ThemeIds = { "system", "light", "dark" };
        private static void FillThemes(ComboBox picker)
        {
            if (picker.Items.Count == 0) foreach (string id in ThemeIds) picker.Items.Add(new ComboBoxItem { Tag = id });
            string[] names = { "Как в системе", "Светлая", "Тёмная" };
            for (int index = 0; index < names.Length; index++) ((ComboBoxItem)picker.Items[index]).Content = T(names[index]);
            Reselect(picker);
        }

        // ComboBox не перерисовывает выбранный пункт после смены текста, выбираем заново
        private static void Reselect(ComboBox picker)
        {
            int index = picker.SelectedIndex;
            picker.SelectedIndex = -1;
            picker.SelectedIndex = index;
        }

        private void ApplyTheme()
        {
            if (Content is FrameworkElement root) root.RequestedTheme = controller.WindowTheme == "light" ? ElementTheme.Light : controller.WindowTheme == "dark" ? ElementTheme.Dark : ElementTheme.Default;
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
                RefreshButton.IsEnabled = !controller.Busy && !refreshing;
                string error = localError ?? controller.Error ?? controller.RefreshError;
                if (string.IsNullOrEmpty(error)) dismissed = false;
                ErrorBar.Message = string.IsNullOrEmpty(error) ? "" : UiText.Message(error);
                ErrorBar.IsOpen = !string.IsNullOrEmpty(error) && !dismissed;
                StartupSwitch.IsOn = controller.StartupEnabled;
                NewMicrophonesSwitch.IsOn = controller.EnableNewMicrophones;
                NewMicrophonesSwitch.IsEnabled = !controller.Busy;
                LanguagePicker.SelectedIndex = Array.IndexOf(Languages.Ids, controller.Language) + 1;
                WindowThemePicker.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeIds, controller.WindowTheme));
                TrayThemePicker.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeIds, controller.TrayTheme));
                ApplyTheme();
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
        private void ThemeChanged(object sender, SelectionChangedEventArgs args)
        {
            if (updating || WindowThemePicker.SelectedItem is not ComboBoxItem window || TrayThemePicker.SelectedItem is not ComboBoxItem tray) return;
            controller.SetThemes((string)window.Tag, (string)tray.Tag);
        }
        private void StartupToggled(object sender, RoutedEventArgs args) { if (!updating) controller.SetStartup(StartupSwitch.IsOn); }
        private void NewMicrophonesToggled(object sender, RoutedEventArgs args) { if (!updating) controller.SetEnableNewMicrophones(NewMicrophonesSwitch.IsOn); }
        private bool refreshing;

        // опрос быстрый, поэтому кольцо держим хотя бы полсекунды, иначе не видно, что что-то произошло
        private async void RefreshClicked(object sender, RoutedEventArgs args)
        {
            refreshing = true;
            RefreshButton.IsEnabled = false;
            RefreshIcon.Opacity = 0;
            RefreshRing.IsActive = true;
            var minimum = Task.Delay(500);
            await Task.Yield();
            controller.Refresh();
            await Task.WhenAll(controller.ScanUsageAsync(), minimum);
            RefreshRing.IsActive = false;
            RefreshIcon.Opacity = 1;
            refreshing = false;
            RefreshButton.IsEnabled = !controller.Busy;
        }
        private void ErrorClosed(InfoBar sender, InfoBarClosedEventArgs args) { if (!updating) { dismissed = true; localError = null; } }
    }
}
