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
        private readonly Slider slider = new Slider { Minimum = 0, Maximum = 60, StepFrequency = 1, TickFrequency = 12, TickPlacement = TickPlacement.None, Visibility = Visibility.Collapsed };
        private readonly StrengthPresets presets = new StrengthPresets();
        private readonly ToggleSwitch lowCut = new ToggleSwitch { Visibility = Visibility.Collapsed };
        private readonly HyperlinkButton precise = new HyperlinkButton { Padding = new Thickness(0, 2, 0, 2) };
        private readonly TextBlock compareLabel = new TextBlock();
        private readonly Button record = new Button { MinWidth = 120 }, listen = new Button { MinWidth = 96 };
        private readonly ToggleSwitch listenSwitch = new ToggleSwitch { MinWidth = 0, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock note = new TextBlock { Style = Style("CaptionTextBlockStyle"), Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock connection = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly Button connect = new Button();
        private readonly Button enable = new Button { VerticalAlignment = VerticalAlignment.Center };
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
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new FontIcon { Glyph = "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
            var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            caption.Children.Add(dot); caption.Children.Add(details);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(name); text.Children.Add(caption);
            Grid.SetColumn(text, 1); header.Children.Add(text);
            enable.Style = Style("AccentButtonStyle");
            Grid.SetColumn(enable, 2); header.Children.Add(enable);
            Grid.SetColumn(toggle, 3); header.Children.Add(toggle);

            var strengthHead = new Grid();
            strengthHead.Children.Add(strengthLabel); strengthHead.Children.Add(strengthValue);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            record.Style = Style("AccentButtonStyle");
            buttons.Children.Add(record); buttons.Children.Add(listen); buttons.Children.Add(listenSwitch);
            var connectRow = new Grid { ColumnSpacing = 8 };
            connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            connectRow.Children.Add(connection);
            Grid.SetColumn(connect, 1); connectRow.Children.Add(connect);
            Grid.SetColumn(more, 2); connectRow.Children.Add(more);
            more.Flyout = new MenuFlyout { Items = { reset, diagnostics } };

            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(strengthHead); body.Children.Add(presets.View); body.Children.Add(precise); body.Children.Add(slider); body.Children.Add(lowCut);
            body.Children.Add(Divider());
            body.Children.Add(compareLabel); body.Children.Add(buttons); body.Children.Add(note);
            body.Children.Add(Divider());
            body.Children.Add(connectRow);

            View = new Expander { Header = header, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };

            toggle.Toggled += (_, _) => { if (!updating) controller.SetProfile(Id, toggle.IsOn); };
            presets.Selected += value => controller.SetAttenuation(Id, value);
            precise.Click += (_, _) => slider.Visibility = lowCut.Visibility = slider.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            lowCut.Toggled += (_, _) => { if (!updating) controller.SetLowCut(Id, lowCut.IsOn); };
            slider.ValueChanged += (_, args) => { if (!updating) controller.SetAttenuation(Id, (int)Math.Round(args.NewValue)); };
            record.Click += (_, _) => { if (controller.RecordingId == Id) controller.StopRecording(); else run(() => controller.RecordAsync(Id)); };
            listen.Click += (_, _) => controller.TogglePlayback(Id);
            listenSwitch.Toggled += (_, _) => { if (!updating) controller.SetListenProcessed(listenSwitch.IsOn); };
            connect.Click += (_, _) => { var device = Device; if (device != null) run(() => controller.IntegrateAsync(Id, !device.Attached)); };
            enable.Click += (_, _) => run(() => controller.IntegrateAsync(Id, true));
            reset.Click += (_, _) => controller.ResetProfile(Id);
            diagnostics.Click += (_, _) => { var data = new DataPackage(); data.SetText(controller.BuildDiagnostics(Id)); Clipboard.SetContent(data); };
        }

        private DeviceInfo Device { get { foreach (var device in controller.Devices) if (device.Id == Id) return device; return null; } }
        private static Style Style(string key) => (Style)Application.Current.Resources[key];
        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
        private static UIElement Divider() => new Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 4), Fill = Brush("DividerStrokeColorDefaultBrush") };

        internal void Localize()
        {
            Label(toggle);
            strengthLabel.Text = T("Сила подавления");
            presets.Localize();
            precise.Content = T("Точная настройка");
            lowCut.Header = T("Срезать низкий гул");
            Label(lowCut);
            ToolTipService.SetToolTip(lowCut, T("Убирает гудение сети, вентиляторов и стола ниже 80 Гц"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, T("Сила подавления в децибелах"));
            compareLabel.Text = T("Сравните на своём голосе");
            listenSwitch.OnContent = T("С шумодавом");
            listenSwitch.OffContent = T("Без шумодава");
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
                dot.Fill = StateBrush(state, View);
                dot.Opacity = state == MicrophoneState.Waiting ? 0.5 : 1;
                toggle.IsOn = controller.ProfileEnabled(Id);
                toggle.IsEnabled = idle;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, device.Name);
                int value = controller.Attenuation(Id);
                if ((int)slider.Value != value) slider.Value = value;
                slider.IsEnabled = idle;
                presets.Update(value, idle);
                // своё значение вне пресетов сразу показываем ползунком
                lowCut.IsOn = controller.LowCut(Id);
                lowCut.IsEnabled = idle;
                if (Array.IndexOf(StrengthPresets.Values, value) < 0 || lowCut.IsOn) slider.Visibility = lowCut.Visibility = Visibility.Visible;
                strengthValue.Text = value + " " + T("дБ");
                bool recording = controller.RecordingId == Id;
                var elapsed = DateTime.UtcNow - controller.RecordingStarted;
                record.IsEnabled = idle || recording;
                record.Content = recording ? T("Остановить") + " " + (int)elapsed.TotalMinutes + ":" + elapsed.Seconds.ToString("00") : T("Записать");
                bool ready = controller.CanCompare(Id);
                bool playing = ready && controller.Playing;
                listen.IsEnabled = ready;
                listen.Content = T(playing ? "Стоп" : "Слушать");
                listenSwitch.IsEnabled = ready;
                listenSwitch.IsOn = controller.ListenProcessed;
                string message = controller.Note(Id);
                note.Text = message == null ? T("Запишите фразу и слушайте, переключая шумодав на ходу") : UiText(message);
                // переключатель включён, но фильтр не встроен в микрофон, явно просим включить
                bool enabling = controller.IntegratingId == Id;
                enable.Visibility = state == MicrophoneState.Detached || enabling ? Visibility.Visible : Visibility.Collapsed;
                enable.Content = T(enabling ? "Включение…" : "Включить");
                enable.IsEnabled = idle;
                ToolTipService.SetToolTip(enable, T("Встроит шумодав в этот микрофон для всех программ, звук прервётся на пару секунд"));
                connection.Text = T(device.Attached ? "Встроен в звук Windows" : "Не встроен в звук Windows");
                ToolTipService.SetToolTip(connect, T("Подключение и отключение ненадолго прерывают звук Windows"));
                connect.Content = T(device.Attached ? "Отключить" : "Включить");
                connect.IsEnabled = idle;
                reset.IsEnabled = idle;
            }
            finally { updating = false; }
        }
        private static string UiText(string message) => NoiseReduction.Desktop.UiText.Message(message);
    }
}
