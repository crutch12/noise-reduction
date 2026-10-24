using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NoiseReduction.Platform;
using Windows.ApplicationModel.DataTransfer;
using static NoiseReduction.WinUI.MicrophoneText;

namespace NoiseReduction.WinUI
{
    // Карточка микрофона в окне: заголовок с переключателем, внутри сила, проверка и подключение
    internal sealed class MicrophoneCard
    {
        private readonly DesktopController controller;
        private readonly Action<Func<System.Threading.Tasks.Task>> run;
        private readonly TextBlock name = new TextBlock { Style = Style("BodyStrongTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock details = new TextBlock { Style = Style("CaptionTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Ellipse dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        private readonly ToggleSwitch toggle = new ToggleSwitch { MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock strengthLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock strengthValue = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush") };
        private readonly Slider slider = new Slider { Minimum = 0, Maximum = 60, StepFrequency = 1, TickFrequency = 12, TickPlacement = TickPlacement.None };
        private readonly TextBlock compareLabel = new TextBlock();
        private readonly Button record = new Button(), before = new Button(), after = new Button(), stop = new Button();
        private readonly TextBlock note = new TextBlock { Style = Style("CaptionTextBlockStyle"), Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock connection = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly Button connect = new Button();
        private readonly MenuFlyoutItem reset = new MenuFlyoutItem(), diagnostics = new MenuFlyoutItem();
        private readonly Button more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6) };
        private bool updating;
        internal readonly string Id;
        internal readonly Expander View;

        internal MicrophoneCard(DesktopController controller, string id, Action<Func<System.Threading.Tasks.Task>> run)
        {
            this.controller = controller; this.run = run; Id = id;
            var header = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 8, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new FontIcon { Glyph = "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
            var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            caption.Children.Add(dot); caption.Children.Add(details);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(name); text.Children.Add(caption);
            Grid.SetColumn(text, 1); header.Children.Add(text);
            Grid.SetColumn(toggle, 2); header.Children.Add(toggle);

            var strengthHead = new Grid();
            strengthHead.Children.Add(strengthLabel); strengthHead.Children.Add(strengthValue);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            record.Style = Style("AccentButtonStyle");
            foreach (var button in new[] { record, before, after, stop }) buttons.Children.Add(button);
            var connectRow = new Grid { ColumnSpacing = 8 };
            connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            connectRow.Children.Add(connection);
            Grid.SetColumn(connect, 1); connectRow.Children.Add(connect);
            Grid.SetColumn(more, 2); connectRow.Children.Add(more);
            more.Flyout = new MenuFlyout { Items = { reset, diagnostics } };

            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(strengthHead); body.Children.Add(slider);
            body.Children.Add(Divider());
            body.Children.Add(compareLabel); body.Children.Add(buttons); body.Children.Add(note);
            body.Children.Add(Divider());
            body.Children.Add(connectRow);

            View = new Expander { Header = header, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };

            toggle.Toggled += (_, _) => { if (!updating) controller.SetProfile(Id, toggle.IsOn); };
            slider.ValueChanged += (_, args) => { if (!updating) controller.SetAttenuation(Id, (int)Math.Round(args.NewValue)); };
            record.Click += (_, _) => run(() => controller.RecordAsync(Id));
            before.Click += (_, _) => controller.Play(Id, false);
            after.Click += (_, _) => controller.Play(Id, true);
            stop.Click += (_, _) => controller.Stop();
            connect.Click += (_, _) => { var device = Device; if (device != null) run(() => controller.IntegrateAsync(Id, !device.Attached)); };
            reset.Click += (_, _) => controller.ResetProfile(Id);
            diagnostics.Click += (_, _) => { var data = new DataPackage(); data.SetText(controller.BuildDiagnostics(Id)); Clipboard.SetContent(data); };
        }

        private DeviceInfo Device { get { foreach (var device in controller.Devices) if (device.Id == Id) return device; return null; } }
        private static Style Style(string key) => (Style)Application.Current.Resources[key];
        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
        private static UIElement Divider() => new Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 4), Fill = Brush("DividerStrokeColorDefaultBrush") };

        internal void Localize()
        {
            strengthLabel.Text = T("Сила подавления");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, T("Сила подавления в децибелах"));
            compareLabel.Text = T("Сравните на своём голосе");
            record.Content = T("Записать 5 сек");
            before.Content = T("Оригинал");
            after.Content = T("С шумодавом");
            stop.Content = T("Стоп");
            reset.Text = T("Сбросить силу до 24 дБ");
            diagnostics.Text = T("Скопировать диагностику");
            ToolTipService.SetToolTip(more, T("Ещё"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, T("Ещё"));
        }

        internal void Update()
        {
            var device = Device;
            if (device == null) return;
            updating = true;
            try
            {
                var state = controller.StateOf(Id);
                bool idle = !controller.Busy;
                name.Text = device.Name;
                ToolTipService.SetToolTip(name, device.Name);
                details.Text = Details(controller, device);
                details.Foreground = Brush(state == MicrophoneState.Error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
                dot.Fill = StateBrush(state);
                dot.Opacity = state == MicrophoneState.Waiting ? 0.5 : 1;
                toggle.IsOn = controller.ProfileEnabled(Id);
                toggle.IsEnabled = idle;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, device.Name);
                int value = controller.Attenuation(Id);
                if ((int)slider.Value != value) slider.Value = value;
                slider.IsEnabled = idle;
                strengthValue.Text = value + " " + T("дБ");
                bool recording = controller.RecordingId == Id;
                record.IsEnabled = idle;
                record.Content = T(recording ? "Запись…" : "Записать 5 сек");
                before.IsEnabled = after.IsEnabled = controller.CanCompare(Id);
                stop.IsEnabled = controller.CanCompare(Id);
                string message = controller.Note(Id);
                note.Text = message == null ? T("Одна запись до и после, живой звук не воспроизводится") : UiText(message);
                connection.Text = T(device.Attached ? "Подключён к звуку Windows" : "Не подключён к звуку Windows");
                ToolTipService.SetToolTip(connect, T("Подключение и отключение ненадолго прерывают звук Windows"));
                connect.Content = T(device.Attached ? "Отключить" : "Подключить");
                connect.IsEnabled = idle;
                reset.IsEnabled = idle;
            }
            finally { updating = false; }
        }
        private static string UiText(string message) => NoiseReduction.Desktop.UiText.Message(message);
    }
}
