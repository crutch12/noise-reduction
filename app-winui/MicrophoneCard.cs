using System;
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
    // Карточка микрофона: сверху сила и проверка, редкое спрятано в Дополнительно
    internal sealed class MicrophoneCard
    {
        private readonly DesktopController controller;
        private readonly TextBlock name = new TextBlock { Style = Style("BodyStrongTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly StackPanel apps = AppIcons.Create();
        private readonly TextBlock details = new TextBlock { Style = Style("CaptionTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Ellipse dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        private readonly ToggleSwitch toggle = new ToggleSwitch { MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        private readonly InfoBar hookBar = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
        private readonly Button hookButton = new Button();
        private readonly TextBlock modelLabel = new TextBlock { Style = Style("BodyStrongTextBlockStyle") };
        private readonly ToggleButton fastModel = new ToggleButton { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 5) };
        private readonly ToggleButton preciseModel = new ToggleButton { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 5) };
        private readonly TextBlock modelNote = new TextBlock { Style = Style("CaptionTextBlockStyle"), Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock strengthLabel = new TextBlock { Style = Style("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock strengthValue = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush") };
        private readonly StrengthPresets presets = new StrengthPresets(true);
        private readonly Slider slider = new Slider { Minimum = 0, Maximum = 60, StepFrequency = 1, Visibility = Visibility.Collapsed };
        private readonly TextBlock compareLabel = new TextBlock { Style = Style("BodyStrongTextBlockStyle") };
        private readonly Button record = new Button { MinWidth = 150 }, listen = new Button { MinWidth = 96 };
        private readonly ToggleSwitch listenSwitch = new ToggleSwitch { MinWidth = 0, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly WaveformView waveform = new WaveformView();
        private readonly TextBlock note = new TextBlock { Style = Style("CaptionTextBlockStyle"), Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        private readonly Button advanced = new Button { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 8, 4) };
        private readonly TextBlock advancedLabel = new TextBlock();
        private readonly FontIcon advancedChevron = new FontIcon { Glyph = "", FontSize = 12 };
        private readonly StackPanel extra = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        private readonly ToggleSwitch lowCut = new ToggleSwitch(), economy = new ToggleSwitch { Visibility = Visibility.Collapsed };
        private readonly Button reset = new Button(), diagnostics = new Button();
        private bool updating;
        internal readonly string Id;
        internal readonly Expander View;

        internal MicrophoneCard(DesktopController controller, string id, Action<Func<System.Threading.Tasks.Task>> run)
        {
            this.controller = controller; Id = id;
            var header = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 8, 0, 8) };
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) header.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            header.Children.Add(new FontIcon { Glyph = "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
            var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            caption.Children.Add(dot); caption.Children.Add(details); caption.Children.Add(apps);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(name); text.Children.Add(caption);
            Grid.SetColumn(text, 1); header.Children.Add(text);
            Grid.SetColumn(toggle, 2); header.Children.Add(toggle);

            var strengthHead = new Grid();
            strengthHead.Children.Add(strengthLabel); strengthHead.Children.Add(strengthValue);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            record.Style = Style("AccentButtonStyle");
            buttons.Children.Add(record); buttons.Children.Add(listen); buttons.Children.Add(listenSwitch);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            tools.Children.Add(reset); tools.Children.Add(diagnostics);
            extra.Children.Add(lowCut); extra.Children.Add(economy); extra.Children.Add(tools);
            var advancedContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            advancedContent.Children.Add(advancedLabel); advancedContent.Children.Add(advancedChevron);
            advanced.Content = advancedContent;
            advanced.Click += (_, _) =>
            {
                bool open = extra.Visibility != Visibility.Visible;
                extra.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                advancedChevron.Glyph = open ? "" : "";
            };

            var models = new Grid { ColumnSpacing = 4 };
            models.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            models.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(preciseModel, 1);
            models.Children.Add(fastModel); models.Children.Add(preciseModel);

            var body = new StackPanel { Spacing = 8 };
            hookBar.ActionButton = hookButton;
            body.Children.Add(hookBar);
            body.Children.Add(modelLabel); body.Children.Add(models); body.Children.Add(modelNote);
            body.Children.Add(Divider());
            body.Children.Add(strengthHead); body.Children.Add(presets.View); body.Children.Add(slider);
            body.Children.Add(Divider());
            body.Children.Add(compareLabel); body.Children.Add(buttons); body.Children.Add(waveform.View); body.Children.Add(note);
            body.Children.Add(Divider());
            body.Children.Add(advanced); body.Children.Add(extra);

            View = new Expander { Header = header, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };

            toggle.Toggled += (_, _) =>
            {
                if (updating) return;
                controller.SetProfile(Id, toggle.IsOn);
                // включение микрофона сразу встраивает шумоподавление, если его ещё нет
                var device = Device;
                if (toggle.IsOn && device != null && !device.Attached) run(() => controller.IntegrateAsync(Id, true));
            };
            presets.Selected += value => { presets.CustomOpen = false; controller.SetAttenuation(Id, value); };
            presets.CustomClicked += () => { presets.CustomOpen = true; Update(); };
            slider.ValueChanged += (_, args) => { if (!updating) controller.SetAttenuation(Id, (int)Math.Round(args.NewValue)); };
            lowCut.Toggled += (_, _) => { if (!updating) controller.SetLowCut(Id, lowCut.IsOn); };
            economy.Toggled += (_, _) => { if (!updating) controller.SetEconomy(Id, economy.IsOn); };
            fastModel.Click += (_, _) => { if (!updating) controller.SetDeepFilter(Id, false); Update(); };
            preciseModel.Click += (_, _) => { if (!updating) controller.SetDeepFilter(Id, true); Update(); };
            record.Click += (_, _) => { if (controller.RecordingId == Id) controller.StopRecording(); else run(() => controller.RecordAsync(Id)); };
            listen.Click += (_, _) => controller.TogglePlayback(Id);
            listenSwitch.Toggled += (_, _) => { if (!updating) controller.SetListenProcessed(listenSwitch.IsOn); };
            hookButton.Click += (_, _) => run(() => controller.IntegrateAsync(Id, true));
            reset.Click += (_, _) => { presets.CustomOpen = false; controller.ResetProfile(Id); };
            diagnostics.Click += (_, _) => { var data = new DataPackage(); data.SetText(controller.BuildDiagnostics(Id)); Clipboard.SetContent(data); };
        }

        private DeviceInfo Device { get { foreach (var device in controller.Devices) if (device.Id == Id) return device; return null; } }
        private static Style Style(string key) => (Style)Application.Current.Resources[key];
        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
        private static UIElement Divider() => new Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 4), Fill = Brush("DividerStrokeColorDefaultBrush") };

        internal void Localize()
        {
            Label(toggle); Label(lowCut); Label(economy);
            modelLabel.Text = T("Модель");
            fastModel.Content = T("Быстрая");
            preciseModel.Content = T("Точная");
            modelNote.Text = T("Точная лучше убирает клавиатуру и голоса на фоне, но чуть сильнее нагружает процессор");
            strengthLabel.Text = T("Сила подавления");
            presets.Localize();
            compareLabel.Text = T("Проверка на своём голосе");
            listenSwitch.OnContent = T("Обработано");
            listenSwitch.OffContent = T("Оригинал");
            advancedLabel.Text = T("Дополнительно");
            lowCut.Header = T("Срезать низкий гул");
            economy.Header = T("Экономный режим");
            reset.Content = T("Сбросить настройки");
            diagnostics.Content = T("Скопировать диагностику");
            Tip(fastModel, "RNNoise, почти не нагружает процессор");
            Tip(preciseModel, "DeepFilterNet, нагрузка чуть выше");
            Tip(slider, "Сила в децибелах");
            Tip(listenSwitch, "Переключение на ходу");
            Tip(lowCut, "Гул ниже 80 Гц: сеть, вентиляторы");
            Tip(economy, "Каналы обрабатываются вместе, нагрузка ниже");
            Tip(reset, "Точная модель и обычная сила, остальное выключено");
        }

        private static void Tip(FrameworkElement control, string text)
        {
            ToolTipService.SetToolTip(control, T(text));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(control, T(text));
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
                ShowDetails(details, controller, device);
                AppIcons.Show(apps, controller.Usage(Id), details.Foreground);
                ToolTipService.SetToolTip(details, Details(controller, device));
                details.Foreground = Brush(state == MicrophoneState.Error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
                dot.Fill = StateBrush(state, View);
                dot.Opacity = state == MicrophoneState.Waiting ? 0.5 : 1;
                // переключатель показывает, работает ли шумоподавление на деле
                toggle.IsOn = controller.ProfileEnabled(Id) && device.Attached;
                toggle.IsEnabled = idle;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, device.Name);
                // включён в программе, но не встроен в Windows: объясняем и даём одну кнопку
                bool enabling = controller.IntegratingId == Id;
                hookBar.IsOpen = controller.ProfileEnabled(Id) && !device.Attached || enabling;
                // у микрофонов с пакетом эффектов Windows выбор делается в Параметрах, кнопка открывает нужную страницу
                bool pack = device.EffectPack && device.EnhancementsEnabled;
                hookBar.Title = T(enabling ? "Включение…" : pack ? "Выберите Noise Reduction в Параметрах Windows" : device.EnhancementsEnabled ? "Шумоподавление не встроено в Windows" : "В Windows выключены улучшения звука");
                hookBar.Message = T(enabling ? "Подтвердите запрос Windows, звук прервётся на пару секунд" : pack ? "Windows разрешает выбрать эффект только вам: в пункте Улучшения звука выберите Noise Reduction" : "Программы получают звук без обработки. Нажмите Включить, Windows попросит подтверждение");
                hookButton.Content = T(pack ? "Открыть Параметры" : "Включить");
                hookButton.IsEnabled = idle;

                bool precise = controller.DeepFilter(Id);
                fastModel.IsChecked = !precise; preciseModel.IsChecked = precise;
                fastModel.IsEnabled = preciseModel.IsEnabled = idle;

                int value = controller.Attenuation(Id);
                // значение вне пресетов сразу открывает ползунок
                if (Array.IndexOf(StrengthPresets.Values, value) < 0) presets.CustomOpen = true;
                presets.Update(value, idle);
                if ((int)slider.Value != value) slider.Value = value;
                slider.IsEnabled = idle;
                slider.Visibility = presets.CustomOpen ? Visibility.Visible : Visibility.Collapsed;
                strengthValue.Text = presets.CustomOpen ? value + " " + T("дБ") : "";

                bool recording = controller.RecordingId == Id;
                var elapsed = DateTime.UtcNow - controller.RecordingStarted;
                record.IsEnabled = idle || recording;
                record.Content = recording ? T("Остановить") + " " + (int)elapsed.TotalMinutes + ":" + elapsed.Seconds.ToString("00") : T("Записать");
                bool ready = controller.CanCompare(Id);
                listen.IsEnabled = ready;
                listen.Content = T(ready && controller.Playing ? "Стоп" : "Слушать");
                listenSwitch.IsEnabled = ready;
                listenSwitch.IsOn = controller.ListenProcessed;
                UpdateWaveform();
                string message = controller.Note(Id);
                note.Text = message == null ? T("Запишите фразу, затем слушайте и переключайте Оригинал и Обработано") : NoiseReduction.Desktop.UiText.Message(message);

                lowCut.IsOn = controller.LowCut(Id);
                lowCut.IsEnabled = idle;
                // точная модель и так обрабатывает каналы вместе, экономному режиму там делать нечего
                economy.Visibility = device.Channels > 1 && !precise ? Visibility.Visible : Visibility.Collapsed;
                economy.IsOn = controller.Economy(Id);
                economy.IsEnabled = idle;
                reset.IsEnabled = idle;
            }
            finally { updating = false; }
        }

        private void UpdateWaveform()
        {
            bool recording = controller.RecordingId == Id;
            bool ready = controller.CanCompare(Id) && controller.OriginalPeaks != null;
            if (recording) waveform.ShowLive(() => controller.LivePeaks, controller.RecordingStarted);
            else if (ready) waveform.ShowResult(controller.OriginalPeaks, controller.ProcessedPeaks, controller.ListenProcessed, () => controller.PlaybackProgress);
            else waveform.Hide();
        }
    }
}
