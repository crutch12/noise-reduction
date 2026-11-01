using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using NoiseReduction.Desktop;
using static NoiseReduction.Avalonia.AppTheme;

namespace NoiseReduction.Avalonia
{
    internal sealed class TrayPanel : Window
    {
        private sealed class DeviceRow
        {
            internal readonly TextBlock Name = Text(null, true), State = Caption();
            internal readonly Ellipse Dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            internal readonly StackPanel Apps = new() { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            internal readonly ToggleSwitch Toggle = Switch("TrayMicrophoneEnabled");
            internal Grid View;
        }
        private readonly Controller controller;
        private readonly ToggleSwitch master = Switch("TrayMasterSwitch");
        private readonly TextBlock global = Text(null, true), settingsText = Text(), empty = Caption();
        private readonly StackPanel rows = new() { Name = "TrayMicrophones", Spacing = 14, Margin = new Thickness(16, 14) };
        private readonly Dictionary<string, DeviceRow> devices = new(StringComparer.Ordinal);
        private readonly Button settings = new() { Name = "TraySettings", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6) };
        private readonly SolidColorBrush surface = new(), line = new(), background = new(), foreground = new();
        private bool updating;

        internal TrayPanel(Controller controller, Action showSettings)
        {
            this.controller = controller;
            Title = "Noise Reduction"; Name = "NoiseReductionTrayPanel"; Width = 360; Height = 200; CanResize = false;
            WindowDecorations = global::Avalonia.Controls.WindowDecorations.None; ShowInTaskbar = false; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var microphoneScroll = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var header = new Border
            {
                Padding = new Thickness(16, 10), BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1),
                Child = Columns("20,*,Auto", new UiIcon("microphone"), global, master),
            };
            settings.Content = Columns("16,*", new UiIcon("settings") { Width = 16, Height = 16 }, settingsText);
            var footer = new Border { Padding = new Thickness(8, 4), Background = background, BorderBrush = line, BorderThickness = new Thickness(0, 1, 0, 0), Child = settings };
            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            grid.Children.Add(header); Grid.SetRow(microphoneScroll, 1); grid.Children.Add(microphoneScroll); Grid.SetRow(footer, 2); grid.Children.Add(footer);
            Content = new Border { Background = surface, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = grid };
            master.IsCheckedChanged += (_, _) => { if (!updating) { bool value = master.IsChecked == true; controller.Change(() => controller.Settings.Enabled = value); } };
            settings.Click += (_, _) => { Hide(); showSettings(); };
            Deactivated += (_, _) => Hide();
            KeyDown += (_, args) => { if (args.Key == Key.Escape) { Hide(); args.Handled = true; } };
            controller.Changed += () => { if (IsVisible) Update(); };
            ActualThemeVariantChanged += (_, _) => { if (!updating && IsVisible) Update(); };
            SizeChanged += (_, _) => { foreach (var row in devices.Values) row.View.Width = Math.Max(0, ClientSize.Width - 34); };
        }
        internal void Toggle(Window owner)
        {
            if (IsVisible) { Hide(); return; }
            Update(); Height = 114 + Math.Max(1, controller.Devices.Count) * 60;
            WindowLayout.PlaceTray(this, owner); Show(); Activate();
        }
        private void Update()
        {
            updating = true;
            try
            {
                RequestedThemeVariant = Variant(controller.Settings.TrayTheme);
                bool dark = ActualThemeVariant == ThemeVariant.Dark;
                surface.Color = Color.Parse(dark ? "#24272D" : "#FFFFFF"); line.Color = Color.Parse(dark ? "#383B42" : "#E1E4E8"); background.Color = Color.Parse(dark ? "#1B1D22" : "#F4F5F7");
                foreground.Color = Color.Parse(dark ? "#E3E7ED" : "#1F252D");
                global.Foreground = settingsText.Foreground = empty.Foreground = foreground;
                FlowDirection = UiText.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                global.Text = T("Шумоподавление"); master.IsChecked = controller.Settings.Enabled; Describe(master, global.Text);
                settingsText.Text = T("Настройки");
                string[] ids = controller.Devices.Select(device => device.Id).ToArray();
                if (!devices.Keys.SequenceEqual(ids))
                {
                    devices.Clear(); rows.Children.Clear();
                    foreach (var device in controller.Devices)
                    {
                        string id = device.Id; var row = new DeviceRow();
                        row.Name.TextWrapping = row.State.TextWrapping = TextWrapping.NoWrap;
                        row.Name.TextTrimming = row.State.TextTrimming = TextTrimming.CharacterEllipsis;
                        row.View = Columns("*,Auto", Captioned(row.Name, Columns("Auto,*,Auto", row.Dot, row.State, row.Apps)), row.Toggle);
                        row.View.Width = Math.Max(0, Width - 34);
                        row.Toggle.IsCheckedChanged += (_, _) => { if (!updating) { bool value = row.Toggle.IsChecked == true; controller.ChangeProfile(id, profile => profile.Enabled = value); } };
                        devices.Add(id, row); rows.Children.Add(row.View);
                    }
                    if (devices.Count == 0) rows.Children.Add(empty);
                }
                foreach (var device in controller.Devices)
                {
                    var row = devices[device.Id]; var status = controller.StatusFor(device.Id);
                    row.Name.Text = DeviceName(controller, device); ToolTip.SetTip(row.Name, device.Name + "\n" + device.Id);
                    row.Name.Foreground = row.State.Foreground = foreground;
                    row.State.Text = StateText(status.State); ToolTip.SetTip(row.State, status.Message ?? row.State.Text);
                    row.Dot.Fill = Brush(status.State == State.Working ? dark ? "#50D5C1" : "#087F74" : status.State == State.Error ? dark ? "#FF8B8B" : "#B32828" : dark ? "#A3A9B3" : "#626A76");
                    row.Toggle.IsChecked = controller.ProfileFor(device.Id).Enabled; row.Toggle.IsEnabled = !controller.Busy;
                    Describe(row.Toggle, device.Name, T("Шумоподавление")); AppIcons.Update(row.Apps, controller.ConsumersFor(device.Id), dark);
                }
                empty.Text = Local("Микрофоны не найдены", "No microphones found");
                if (devices.Count == 0 && rows.Children.Count == 0) rows.Children.Add(empty);
            }
            finally { updating = false; }
        }
    }
}
