using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NoiseReduction.Desktop;
using Windows.Graphics;
using static NoiseReduction.WinUI.MicrophoneText;

namespace NoiseReduction.WinUI
{
    // Быстрая панель над треем: общий выключатель и переключатель каждого микрофона
    public sealed partial class TrayFlyout : Window
    {
        private sealed class Row
        {
            internal string Id;
            internal TextBlock Name, Details;
            internal Ellipse Dot;
            internal StackPanel Apps;
            internal ToggleSwitch Toggle;
        }

        private const int WidthDip = 360;
        private readonly DesktopController controller;
        private readonly Action openSettings;
        private readonly List<Row> rows = new List<Row>();
        private DateTime hiddenAt;
        private int extraWidth, extraHeight;
        private bool calibrated;
        private bool updating;
        private string renderedLanguage;

        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        private IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

        internal TrayFlyout(DesktopController controller, Action openSettings)
        {
            this.controller = controller; this.openSettings = openSettings;
            InitializeComponent();
            FlyoutRoot.RequestedTheme = NativeTray.TrayLight(controller) ? ElementTheme.Light : ElementTheme.Dark;
            FlyoutIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Microphone.png")));
            SystemBackdrop = new ThinAcrylic(true);
            ExtendsContentIntoTitleBar = true;
            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = true;
            AppWindow.SetPresenter(presenter);
            AppWindow.IsShownInSwitchers = false;
            AppWindow.Closing += (_, args) => { if (!App.CurrentApp.Exiting) { args.Cancel = true; Dismiss(); } };
            Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) Dismiss(); };
            controller.Changed += () => DispatcherQueue.TryEnqueue(() => { if (AppWindow.IsVisible) Render(); });
        }

        internal void Toggle()
        {
            if (AppWindow.IsVisible) { Dismiss(); return; }
            // клик по значку сначала снимает фокус с панели, повторно её не открываем
            if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds < 350) return;
            // как системные панели трея, следуем теме панели задач
            FlyoutRoot.RequestedTheme = NativeTray.TrayLight(controller) ? ElementTheme.Light : ElementTheme.Dark;
            Render();
            Place();
            AppWindow.Show();
            Activate();
            SetForegroundWindow(Handle);
            FocusSink.Focus(FocusState.Programmatic);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Fit);
            controller.FlyoutOpen = true;
            _ = controller.ScanUsageAsync();
        }

        private void Dismiss()
        {
            if (!AppWindow.IsVisible) return;
            hiddenAt = DateTime.UtcNow;
            AppWindow.Hide();
            controller.FlyoutOpen = false;
        }

        private void Place()
        {
            GetCursorPos(out var cursor);
            IntPtr monitor = MonitorFromPoint(cursor, 2);
            // берём DPI монитора с треем, панель может быть ещё на другом экране
            double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
            FlyoutRoot.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
            double heightDip = FlyoutRoot.DesiredSize.Height;
            if (heightDip < 40) heightDip = 52 + Math.Max(1, rows.Count) * 96;
            int width = (int)Math.Ceiling(WidthDip * scale) + extraWidth, height = (int)Math.Ceiling(heightDip * scale) + extraHeight;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(monitor, ref info);
            var work = info.Work; var screen = info.Monitor;
            int gap = (int)(12 * scale);
            int x = Math.Clamp(cursor.X - width / 2, work.Left + gap, work.Right - width - gap);
            int y = work.Bottom - height - gap;
            if (work.Top > screen.Top) y = work.Top + gap;
            else if (work.Left > screen.Left) { x = work.Left + gap; y = Math.Clamp(cursor.Y - height / 2, work.Top + gap, work.Bottom - height - gap); }
            else if (work.Right < screen.Right) { x = work.Right - width - gap; y = Math.Clamp(cursor.Y - height / 2, work.Top + gap, work.Bottom - height - gap); }
            // до первой подгонки рамки окно показывается за экраном, чтобы не было видно скачка
            if (!calibrated) { x = -32000; y = -32000; }
            AppWindow.MoveAndResize(new RectInt32(x, calibrated ? Math.Max(work.Top, y) : y, width, Math.Min(height, work.Bottom - work.Top - gap * 2)));
        }

        // окно без заголовка всё равно резервирует служебную область, подгоняем по фактическому месту под XAML
        private void Fit()
        {
            var root = FlyoutRoot.XamlRoot;
            if (root == null || !AppWindow.IsVisible) return;
            FlyoutRoot.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
            double scale = root.RasterizationScale;
            int dw = (int)Math.Round((WidthDip - root.Size.Width) * scale), dh = (int)Math.Round((FlyoutRoot.DesiredSize.Height - root.Size.Height) * scale);
            bool first = !calibrated;
            calibrated = true;
            if (Math.Abs(dw) < 2 && Math.Abs(dh) < 2) { if (first) Place(); return; }
            // поправку на рамку запоминаем, следующие открытия сразу нужного размера
            extraWidth += dw; extraHeight += dh;
            Place();
        }

        private void Render()
        {
            updating = true;
            try
            {
                UiText.Language = controller.Language;
                var ids = controller.Devices.Select(device => device.Id).ToList();
                if (!ids.SequenceEqual(rows.Select(row => row.Id)) || MicrophoneRows.Children.Count == 0)
                {
                    rows.Clear(); MicrophoneRows.Children.Clear();
                    foreach (string id in ids) { var row = CreateRow(id); rows.Add(row); }
                    if (rows.Count == 0) MicrophoneRows.Children.Add(new TextBlock { Text = T("Доступные микрофоны не найдены"), Style = (Style)FlyoutRoot.Resources["FlyoutValueStyle"] });
                }
                else if (rows.Count == 0 && renderedLanguage != controller.Language && MicrophoneRows.Children.FirstOrDefault() is TextBlock empty) empty.Text = T("Доступные микрофоны не найдены");
                if (renderedLanguage != controller.Language)
                {
                    FlyoutRoot.FlowDirection = UiText.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                    GlobalLabel.Text = T("Шумоподавление");
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GlobalSwitch, T("Шумоподавление"));
                    SettingsLabel.Text = T("Настройки");
                    ToolTipService.SetToolTip(GlobalSwitch, T("Для всех микрофонов, клавиша Ctrl+Alt+N"));
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SettingsButton, T("Настройки"));
                    renderedLanguage = controller.Language;
                }
                bool idle = !controller.Busy;
                GlobalSwitch.IsOn = controller.GlobalEnabled;
                GlobalSwitch.IsEnabled = idle;
                foreach (var row in rows)
                {
                    var device = controller.Devices.First(value => value.Id == row.Id);
                    var state = controller.StateOf(row.Id);
                    row.Name.Text = device.Name;
                    ToolTipService.SetToolTip(row.Name, device.Name);
                    ShowDetails(row.Details, controller, device);
                    AppIcons.Show(row.Apps, controller.Usage(row.Id), row.Details.Foreground);
                    ToolTipService.SetToolTip(row.Details, state == MicrophoneState.Detached ? T("Включите переключатель, Windows попросит подтверждение") : null);
                    row.Dot.Fill = StateBrush(state, FlyoutRoot);
                    row.Dot.Opacity = state == MicrophoneState.Waiting ? 0.5 : 1;
                    // переключатель показывает, работает ли шумоподавление на деле
                    row.Toggle.IsOn = controller.ProfileEnabled(row.Id) && device.Attached;
                    row.Toggle.IsEnabled = idle;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Toggle, device.Name);
                }
            }
            finally { updating = false; }
        }

        private Row CreateRow(string id)
        {
            var row = new Row
            {
                Id = id,
                Name = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                Details = new TextBlock { Style = (Style)FlyoutRoot.Resources["FlyoutDetailsStyle"], TextTrimming = TextTrimming.CharacterEllipsis },
                Apps = AppIcons.Create(),
                Dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center },
                Toggle = new ToggleSwitch { MinWidth = 0, OnContent = "", OffContent = "", Margin = new Thickness(0, -4, -12, 0), FocusVisualMargin = new Thickness(-3, -3, 9, -3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
            };
            // одна сетка на микрофон: слева имя и состояние, справа переключатель
            var block = new Grid { ColumnSpacing = 12, RowSpacing = 0 };
            block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            block.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int index = 0; index < 2; index++) block.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            details.Children.Add(row.Dot); details.Children.Add(row.Details); details.Children.Add(row.Apps);
            block.Children.Add(row.Name);
            Grid.SetRow(details, 1); block.Children.Add(details);
            Grid.SetColumn(row.Toggle, 1); Grid.SetRowSpan(row.Toggle, 2); block.Children.Add(row.Toggle);
            MicrophoneRows.Children.Add(block);
            row.Toggle.Toggled += (_, _) =>
            {
                if (updating) return;
                controller.SetProfile(id, row.Toggle.IsOn);
                var device = controller.Devices.FirstOrDefault(value => value.Id == id);
                if (row.Toggle.IsOn && device != null && !device.Attached) { Dismiss(); _ = controller.IntegrateAsync(id, true); }
            };
            return row;
        }

        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
        private void GlobalToggled(object sender, RoutedEventArgs args) { if (!updating) controller.SetGlobal(GlobalSwitch.IsOn); }
        private void SettingsClicked(object sender, RoutedEventArgs args) { Dismiss(); openSettings(); }
        private void FlyoutKeyDown(object sender, KeyRoutedEventArgs args) { if (args.Key == Windows.System.VirtualKey.Escape) Dismiss(); }
    }
}
