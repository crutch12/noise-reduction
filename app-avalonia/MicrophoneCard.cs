using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using NoiseReduction.Desktop;
using static NoiseReduction.Avalonia.AppTheme;

namespace NoiseReduction.Avalonia
{
    internal sealed class MicrophoneCard
    {
        private static readonly int[] FastValues = { 6, 12, 60 }, PreciseValues = { 12, 24, 60 };
        private static readonly string[] PresetNames = { "Мягко", "Обычно", "Сильно" };
        private readonly Controller controller;
        private readonly VoiceTest voice;
        private readonly Action<Func<Task>> run;
        private readonly TextBlock name = Text(null, true), details = Caption(), modelLabel = Text(null, true), modelNote = Caption(),
            strengthLabel = Text(null, true), strengthValue = Caption(), compareLabel = Text(null, true), note = Caption(),
            advancedLabel = Text(), lowCutLabel = Text(), economyNote = Caption(), sourceNote = Caption(), outputNote = Caption(), metrics = Caption(),
            inputLabel = Caption(), outputLabel = Caption(), levelsHint = Caption(), originalLabel = Text(), processedLabel = Text();
        private readonly ProgressBar inputLevel = new() { Name = "MicrophoneInputLevel", Minimum = 0, Maximum = 1, Height = 5 },
            outputLevel = new() { Name = "MicrophoneOutputLevel", Minimum = 0, Maximum = 1, Height = 5 };
        private readonly Ellipse dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel apps = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        private readonly ToggleSwitch enabled = Switch("MicrophoneEnabled"), lowCut = Switch("LowCut"), processed = Switch("ListenProcessed");
        private readonly ToggleButton fast = Segment("FastModel"), precise = Segment("PreciseModel"), custom = Segment("CustomStrength");
        private readonly ToggleButton[] presets = { Segment("GentleStrength"), Segment("NormalStrength"), Segment("StrongStrength") };
        private readonly Slider slider = new() { Name = "CustomStrengthSlider", Minimum = 0, Maximum = 60, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Button record = new() { Name = "RecordVoice", MinWidth = 120 }, listen = new() { Name = "PlayVoice", MinWidth = 88 },
            advanced = new() { Name = "AdvancedSettings", HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 5), Background = Brushes.Transparent, BorderThickness = new Thickness(0) },
            reset = new() { Name = "ResetProfile" }, diagnostics = new() { Name = "CopyMicrophoneDiagnostics" };
        private readonly WaveformView waveform = new();
        private readonly UiIcon advancedChevron = new("chevron-down") { Width = 14, Height = 14 };
        private readonly UiIcon expandChevron = new("chevron-down") { Width = 16, Height = 16 };
        private readonly Button expand = new() { Name = "ExpandMicrophone", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(20, 16, 0, 16),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        private readonly StackPanel body;
        private readonly StackPanel extra = new() { Spacing = 10, IsVisible = false };
        private readonly TextBlock warning = Caption();
        private readonly Border warningBox;
        private bool updating, customOpen;
        internal readonly string Id;
        internal readonly Border Container;

        internal MicrophoneCard(Controller controller, VoiceTest voice, string id, Action<Func<Task>> run)
        {
            this.controller = controller; this.voice = voice; this.run = run; Id = id;
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            details.TextWrapping = TextWrapping.NoWrap; details.TextTrimming = TextTrimming.CharacterEllipsis;
            var state = Columns("Auto,Auto", dot, details);
            state.ColumnSpacing = 6;
            apps.Margin = new Thickness(8, 0, 0, 0);
            var status = new WrapPanel { Orientation = Orientation.Horizontal, Children = { state, apps } };
            name.IsHitTestVisible = details.IsHitTestVisible = dot.IsHitTestVisible = false;
            var title = Columns("24,*", new UiIcon("microphone") { IsHitTestVisible = false }, Captioned(name, status));
            title.ColumnDefinitions[1].MinWidth = 0;
            title.Margin = expand.Padding;
            var heading = new Grid { Children = { expand, title } };
            var disclosure = new Button { Name = "MicrophoneDisclosure", Content = expandChevron, Padding = new Thickness(8), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            var header = Columns("*,Auto,Auto", heading, enabled, disclosure);
            header.MinWidth = 0;
            header.ColumnDefinitions[0].MinWidth = 0;
            header.ColumnSpacing = 12;
            header.Margin = new Thickness(0, 0, 12, 0);
            header.HorizontalAlignment = HorizontalAlignment.Stretch;
            var strength = Columns("*,Auto", strengthLabel, strengthValue);
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            var comparison = Columns("Auto,Auto,Auto", originalLabel, processed, processedLabel);
            comparison.ColumnSpacing = 8;
            comparison.Margin = new Thickness(0, 0, 0, 4);
            originalLabel.TextAlignment = TextAlignment.Right;
            record.Margin = new Thickness(0, 0, 8, 4); listen.Margin = new Thickness(0, 0, 8, 4);
            actions.Children.Add(record); actions.Children.Add(listen); actions.Children.Add(comparison);
            record.Classes.Add("Flat");
            var advancedContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            advancedContent.Children.Add(advancedLabel); advancedContent.Children.Add(advancedChevron);
            advanced.Content = advancedContent;
            var tools = new WrapPanel { Orientation = Orientation.Horizontal };
            reset.Margin = new Thickness(0, 0, 8, 4); diagnostics.Margin = new Thickness(0, 0, 0, 4);
            tools.Children.Add(reset); tools.Children.Add(diagnostics);
            extra.Children.Add(Columns("*,Auto", lowCutLabel, lowCut));
            extra.Children.Add(economyNote); extra.Children.Add(sourceNote); extra.Children.Add(outputNote);
            extra.Children.Add(inputLabel); extra.Children.Add(inputLevel); extra.Children.Add(outputLabel); extra.Children.Add(outputLevel);
            extra.Children.Add(levelsHint);
            extra.Children.Add(metrics); extra.Children.Add(tools);
            warningBox = Card(warning, 10);
            warning.Opacity = 1;
            warningBox.IsVisible = false;
            body = Stack(warningBox, modelLabel, Segments(fast, precise), modelNote, Divider(), strength,
                Segments(presets.Concat(new[] { custom }).Cast<Control>().ToArray()), slider,
                Divider(), compareLabel, actions, waveform, note, Divider(), advanced, extra);
            body.Margin = new Thickness(20, 16, 20, 16);
            body.IsVisible = false;
            var content = new StackPanel { Spacing = 0, Children = { header, body } };
            Container = Card(content, 0);
            Container.Name = "MicrophoneSettingsCard";
            void ToggleExpanded()
            {
                body.IsVisible = !body.IsVisible;
                expandChevron.Kind = body.IsVisible ? "chevron-up" : "chevron-down";
            }
            expand.Click += (_, _) => ToggleExpanded();
            disclosure.Click += (_, _) => ToggleExpanded();
            apps.PointerReleased += (_, args) =>
            {
                if (args.InitialPressMouseButton == MouseButton.Left) { ToggleExpanded(); args.Handled = true; }
            };
            Describe(disclosure, T("Дополнительные настройки"));
            enabled.IsCheckedChanged += (_, _) => { if (!updating) { bool value = enabled.IsChecked == true; controller.ChangeProfile(Id, profile => profile.Enabled = value); } };
            lowCut.IsCheckedChanged += (_, _) => { if (!updating) { bool value = lowCut.IsChecked == true; controller.ChangeProfile(Id, profile => profile.LowCut = value); } };
            fast.Click += (_, _) => SwitchModel(false);
            precise.Click += (_, _) => SwitchModel(true);
            for (int index = 0; index < presets.Length; index++)
            {
                int selected = index;
                presets[index].Click += (_, _) => { if (updating) return; customOpen = false; controller.ChangeProfile(Id, profile => profile.Attenuation = Values(profile.DeepFilter)[selected]); };
            }
            custom.Click += (_, _) => { customOpen = true; Update(); slider.Focus(); };
            slider.PropertyChanged += (_, args) =>
            {
                if (!updating && args.Property == RangeBase.ValueProperty)
                {
                    int value = (int)Math.Round(slider.Value);
                    controller.ChangeProfile(Id, profile => profile.Attenuation = value);
                }
            };
            record.Click += (_, _) =>
            {
                if (voice.Recording && voice.DeviceId == Id) run(voice.StopRecordingAsync);
                else
                {
                    var device = controller.Devices.FirstOrDefault(item => item.Id == Id);
                    if (device != null) run(() => voice.StartAsync(device, controller.ConfigurationFor(Id)));
                }
            };
            listen.Click += (_, _) => { if (voice.DeviceId == Id) voice.TogglePlayback(); };
            processed.IsCheckedChanged += (_, _) => { if (!updating && voice.DeviceId == Id) voice.SelectOriginal(processed.IsChecked != true); };
            advanced.Click += (_, _) => { extra.IsVisible = !extra.IsVisible; Update(); };
            reset.Click += (_, _) => { customOpen = false; controller.ResetProfile(Id); };
            diagnostics.Click += (_, _) => run(async () =>
            {
                var clipboard = TopLevel.GetTopLevel(Container)?.Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync(controller.Diagnostics(Id));
            });
            Update();
        }

        private static int[] Values(bool precise) => precise ? PreciseValues : FastValues;
        private void SwitchModel(bool precise)
        {
            if (updating) return;
            var previous = controller.ProfileFor(Id);
            int index = Array.IndexOf(Values(previous.DeepFilter), previous.Attenuation);
            if (index >= 0) customOpen = false;
            controller.ChangeProfile(Id, profile =>
            {
                profile.DeepFilter = precise;
                if (index >= 0) profile.Attenuation = Values(precise)[index];
            });
        }

        internal void ResetView() { body.IsVisible = false; expandChevron.Kind = "chevron-down"; extra.IsVisible = false; customOpen = false; }
        internal void Update()
        {
            var device = controller.Devices.FirstOrDefault(item => item.Id == Id);
            if (device == null) return;
            updating = true;
            try
            {
                var profile = controller.ProfileFor(Id);
                var status = controller.StatusFor(Id);
                name.Text = DeviceName(controller, device);
                Describe(expand, device.Name);
                ToolTip.SetTip(name, device.Name + "\n" + device.Id);
                details.Text = StateText(status.State) + (device.IsDefault ? " · " + T("основной") : "");
                dot.Fill = StateBrush(status.State);
                if (status.State == State.Error) details.Foreground = StateBrush(status.State);
                else details.Foreground = AppTheme.Foreground;
                details.Opacity = status.State == State.Error ? 1 : 0.76;
                ToolTip.SetTip(details, status.Message ?? details.Text);
                AppIcons.Update(apps, controller.ConsumersFor(Id));
                enabled.IsChecked = profile.Enabled;
                Describe(enabled, device.Name, T("Шумоподавление"));
                enabled.IsEnabled = !controller.Busy;
                modelLabel.Text = T("Модель"); fast.Content = T("Быстрая"); precise.Content = T("Точная");
                fast.IsChecked = !profile.DeepFilter; precise.IsChecked = profile.DeepFilter;
                fast.IsEnabled = precise.IsEnabled = !controller.Busy;
                Describe(fast, T("Быстрая"), T("RNNoise, почти не нагружает процессор"));
                Describe(precise, T("Точная"), T("DeepFilterNet, нагрузка примерно вдвое выше"));
                modelNote.Text = profile.DeepFilter && !status.EffectiveDeepFilter && controller.OnBattery == true && controller.Settings.FastOnBattery
                    ? T("Пока ноутбук работает от батареи, вместо точной модели работает быстрая")
                    : T("Точная лучше убирает клавиатуру и голоса на фоне, но чуть сильнее нагружает процессор");
                strengthLabel.Text = T("Сила подавления");
                var values = Values(profile.DeepFilter);
                int selected = Array.IndexOf(values, profile.Attenuation);
                if (selected < 0) customOpen = true;
                for (int index = 0; index < presets.Length; index++)
                {
                    presets[index].Content = T(PresetNames[index]);
                    presets[index].IsChecked = !customOpen && index == selected;
                    presets[index].IsEnabled = !controller.Busy;
                    Describe(presets[index], T(PresetNames[index]), values[index] >= 60 ? T("Максимум") : values[index] + " " + T("дБ"));
                }
                custom.Content = T("Своё"); custom.IsChecked = customOpen;
                slider.IsVisible = customOpen; slider.Value = profile.Attenuation;
                strengthValue.Text = customOpen ? profile.Attenuation >= 60 ? T("Максимум") : profile.Attenuation + " " + T("дБ") : "";
                Describe(slider, T("Сила в децибелах"));
                bool owns = voice.DeviceId == Id, recording = owns && voice.Recording, ready = owns && voice.HasRecording && !voice.Processing;
                compareLabel.Text = T("Проверка на своём голосе");
                record.Content = recording ? T("Остановить") + " " + voice.Duration.ToString("m\\:ss") : T("Записать");
                record.IsEnabled = recording || device.Available && !voice.Processing && !controller.Busy;
                listen.Content = T(owns && voice.Playing ? "Остановить" : "Слушать"); listen.IsEnabled = ready;
                processed.IsChecked = !owns || !voice.OriginalSelected;
                originalLabel.Text = T("Оригинал"); processedLabel.Text = T("Обработано"); processed.IsEnabled = ready;
                processed.Foreground = AppTheme.Foreground;
                Describe(processed, T("Переключение на ходу"), T("Оригинал") + " / " + T("Обработано"));
                waveform.IsVisible = owns && (voice.Recording || voice.HasRecording);
                waveform.Update(owns ? voice : null);
                note.Text = owns && !string.IsNullOrEmpty(voice.Error) ? UiText.Message(voice.Error)
                    : recording ? T("Говорите в микрофон и нажмите Остановить")
                    : owns && voice.Processing ? Local("Обработка записи…", "Processing recording…")
                    : ready && voice.ActualModel == 0 ? Local("Запись без обработки: шумоподавление выключено", "Unprocessed recording: noise reduction is off")
                    : T("Запишите фразу, затем слушайте и переключайте Оригинал и Обработано");
                advancedLabel.Text = T("Дополнительно");
                advancedChevron.Kind = extra.IsVisible ? "chevron-up" : "chevron-down";
                Describe(advanced, advancedLabel.Text);
                lowCutLabel.Text = T("Срезать низкий гул"); lowCut.IsChecked = profile.LowCut;
                Describe(lowCut, lowCutLabel.Text, T("Гул ниже 80 Гц: сеть, вентиляторы"));
                economyNote.Text = Local("Экономный режим не нужен: звук обрабатывается в моно", "Economy mode is not needed: processing uses mono audio");
                sourceNote.Text = T("Исходный микрофон") + ": " + device.SourceName;
                outputNote.Text = Local("Очищенный микрофон", "Clean microphone") + ": " + (controller.OutputFor(Id) ?? Local("Не создан", "Not created"));
                inputLabel.Text = Local("До шумоподавления", "Before noise reduction"); outputLabel.Text = Local("После шумоподавления", "After noise reduction");
                bool live = status.Metrics.Running != 0;
                inputLevel.Value = live ? Math.Clamp(status.Metrics.InputPeak, 0, 1) : 0;
                outputLevel.Value = live ? Math.Clamp(status.Metrics.OutputPeak, 0, 1) : 0;
                inputLevel.IsEnabled = outputLevel.IsEnabled = live;
                levelsHint.Text = Local("Уровни появятся, когда программа начнёт запись через Noise Reduction", "Levels appear when an app is recording with Noise Reduction");
                levelsHint.IsVisible = !live;
                Describe(inputLevel, inputLabel.Text); Describe(outputLevel, outputLabel.Text);
                string activeModel = status.Metrics.Running == 0 ? Local("Не запущена", "Not running")
                    : status.Metrics.ActualModel == 2 ? "DeepFilterNet" : status.Metrics.ActualModel == 1 ? "RNNoise" : Local("Без обработки", "Bypass");
                metrics.Text = Local("Текущая модель", "Active model") + ": " + activeModel + "\n" +
                    Local("Обработано кадров", "Processed frames") + ": " + status.Metrics.ModelFrames + " · " + Local("Потери", "Dropped") + ": " + status.Metrics.DroppedFrames;
                reset.Content = T("Сбросить настройки"); diagnostics.Content = T("Скопировать диагностику");
                warningBox.IsVisible = status.State is State.Error or State.Bypass or State.Fallback or State.Recovering or State.Unavailable;
                warning.Text = string.IsNullOrEmpty(status.Message) ? StateText(status.State) : UiText.Message(status.Message);
                warning.Foreground = StateBrush(status.State);
            }
            finally { updating = false; }
        }
    }
}
