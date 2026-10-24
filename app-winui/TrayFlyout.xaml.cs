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
    // Быстрая панель над треем: у каждого микрофона переключатель и сила
    public sealed partial class TrayFlyout : Window
    {
        private sealed class Row
        {
            internal string Id;
            internal TextBlock Name, Details, Value;
            internal Ellipse Dot;
            internal ToggleSwitch Toggle;
            internal Slider Slider;
            internal HyperlinkButton Enable;
        }

        private const int WidthDip = 360;
        private readonly DesktopController controller;
        private readonly Action openSettings;
        private readonly List<Row> rows = new List<Row>();
        private DateTime hiddenAt;
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
            FlyoutRoot.RequestedTheme = NativeTray.SystemLight() ? ElementTheme.Light : ElementTheme.Dark;
            SystemBackdrop = new ThinAcrylic();
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
            FlyoutRoot.RequestedTheme = NativeTray.SystemLight() ? ElementTheme.Light : ElementTheme.Dark;
            Render();
            Place();
            AppWindow.Show();
            Activate();
            SetForegroundWindow(Handle);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Fit);
            _ = controller.ScanUsageAsync();
        }

        private void Dismiss()
        {
            if (!AppWindow.IsVisible) return;
            hiddenAt = DateTime.UtcNow;
            AppWindow.Hide();
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
            int width = (int)Math.Ceiling(WidthDip * scale), height = (int)Math.Ceiling(heightDip * scale);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(monitor, ref info);
            var work = info.Work; var screen = info.Monitor;
            int gap = (int)(12 * scale);
            int x = Math.Clamp(cursor.X - width / 2, work.Left + gap, work.Right - width - gap);
            int y = work.Bottom - height - gap;
            if (work.Top > screen.Top) y = work.Top + gap;
            else if (work.Left > screen.Left) { x = work.Left + gap; y = Math.Clamp(cursor.Y - height / 2, work.Top + gap, work.Bottom - height - gap); }
            else if (work.Right < screen.Right) { x = work.Right - width - gap; y = Math.Clamp(cursor.Y - height / 2, work.Top + gap, work.Bottom - height - gap); }
            AppWindow.MoveAndResize(new RectInt32(x, Math.Max(work.Top, y), width, Math.Min(height, work.Bottom - work.Top - gap * 2)));
        }

        // окно без заголовка всё равно резервирует служебную область, подгоняем по фактическому месту под XAML
        private void Fit()
        {
            var root = FlyoutRoot.XamlRoot;
            if (root == null || !AppWindow.IsVisible) return;
            FlyoutRoot.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
            double scale = root.RasterizationScale;
            int dw = (int)Math.Round((WidthDip - root.Size.Width) * scale), dh = (int)Math.Round((FlyoutRoot.DesiredSize.Height - root.Size.Height) * scale);
            if (Math.Abs(dw) < 2 && Math.Abs(dh) < 2) return;
            var position = AppWindow.Position; var size = AppWindow.Size;
            bool bottom = position.Y + size.Height / 2 > root.Size.Height * scale;
            AppWindow.MoveAndResize(new RectInt32(position.X - dw / 2, bottom ? position.Y - dh : position.Y, size.Width + dw, size.Height + dh));
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
                    GlobalLabel.Text = T("Шумоподавление");
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GlobalSwitch, T("Шумоподавление"));
                    ToolTipService.SetToolTip(SettingsButton, T("Настройки"));
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
                    row.Details.Text = Details(controller, device);
                    bool enabling = controller.IntegratingId == row.Id;
                    row.Enable.Visibility = state == MicrophoneState.Detached || enabling ? Visibility.Visible : Visibility.Collapsed;
                    row.Enable.Content = T(enabling ? "Включение…" : "Включить");
                    row.Enable.IsEnabled = idle;
                    row.Dot.Fill = StateBrush(state, FlyoutRoot);
                    row.Dot.Opacity = state == MicrophoneState.Waiting ? 0.5 : 1;
                    row.Toggle.IsOn = controller.ProfileEnabled(row.Id);
                    row.Toggle.IsEnabled = idle;
                    int value = controller.Attenuation(row.Id);
                    if ((int)row.Slider.Value != value) row.Slider.Value = value;
                    row.Slider.IsEnabled = idle && controller.IsEnabled(row.Id);
                    row.Value.Text = value.ToString();
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Toggle, device.Name);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Slider, T("Сила подавления в децибелах"));
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
                Value = new TextBlock { Style = (Style)FlyoutRoot.Resources["FlyoutValueStyle"], TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center },
                Dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center },
                Toggle = new ToggleSwitch { MinWidth = 0, OnContent = "", OffContent = "", Margin = new Thickness(0, 0, -12, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center },
                Slider = new Slider { Minimum = 0, Maximum = 60, StepFrequency = 1, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center }
            };
            // одна сетка на микрофон: слева имя и ползунок, справа переключатель и значение на одном крае
            var block = new Grid { ColumnSpacing = 12, RowSpacing = 0 };
            block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            block.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int index = 0; index < 3; index++) block.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Enable = new HyperlinkButton { Padding = new Thickness(4, 0, 4, 0), MinHeight = 0, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(row.Dot); details.Children.Add(row.Details); details.Children.Add(row.Enable);
            row.Enable.Click += (_, _) => { Dismiss(); _ = controller.IntegrateAsync(id, true); };
            block.Children.Add(row.Name);
            Grid.SetRow(details, 1); block.Children.Add(details);
            Grid.SetColumn(row.Toggle, 1); Grid.SetRowSpan(row.Toggle, 2); block.Children.Add(row.Toggle);
            Grid.SetRow(row.Slider, 2); block.Children.Add(row.Slider);
            Grid.SetRow(row.Value, 2); Grid.SetColumn(row.Value, 1); block.Children.Add(row.Value);
            MicrophoneRows.Children.Add(block);
            row.Toggle.Toggled += (_, _) => { if (!updating) controller.SetProfile(id, row.Toggle.IsOn); };
            row.Slider.ValueChanged += (_, args) => { if (!updating) { row.Value.Text = ((int)args.NewValue).ToString(); controller.SetAttenuation(id, (int)Math.Round(args.NewValue)); } };
            return row;
        }

        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
        private void GlobalToggled(object sender, RoutedEventArgs args) { if (!updating) controller.SetGlobal(GlobalSwitch.IsOn); }
        private void SettingsClicked(object sender, RoutedEventArgs args) { Dismiss(); openSettings(); }
        private void FlyoutKeyDown(object sender, KeyRoutedEventArgs args) { if (args.Key == Windows.System.VirtualKey.Escape) Dismiss(); }
    }
}
