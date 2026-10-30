using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{
    internal sealed class MainWindow : Window
    {
        // как на Windows: у быстрой модели сила это доля исходного звука, у точной предел самой модели
        private static readonly int[] FastValues = { 6, 12, 60 }, PreciseValues = { 12, 24, 60 };
        private static readonly string[] PresetNames = { "Мягко", "Обычно", "Сильно" };

        private readonly Controller controller;
        private readonly ToggleSwitch master = new(), lowCut = new(), startup = new();
        private readonly TextBlock title = Heading(), status = new(), hint = Caption(), sourceLabel = Label(), modelLabel = Label(), modelNote = Caption(),
            strengthLabel = Label(), strengthValue = new() { HorizontalAlignment = HorizontalAlignment.Right }, languageLabel = Label();
        private readonly Ellipse dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox source = new() { HorizontalAlignment = HorizontalAlignment.Stretch }, language = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly HyperlinkButton tagLink = new() { Content = BuildInfo.Tag, NavigateUri = new Uri(BuildInfo.TagUrl) },
            commitLink = new() { Content = BuildInfo.ShortCommit, NavigateUri = new Uri(BuildInfo.CommitUrl), IsVisible = BuildInfo.Commit.Length > 0 },
            sourceLink = new() { NavigateUri = new Uri(BuildInfo.Repository) };
        private readonly ToggleButton fast = Segment(), precise = Segment(), custom = Segment();
        private readonly ToggleButton[] presets = { Segment(), Segment(), Segment() };
        private readonly Slider slider = new() { Minimum = 0, Maximum = 60, TickFrequency = 1, IsSnapToTickEnabled = true, IsVisible = false };
        private bool updating, customOpen;

        internal MainWindow(Controller controller)
        {
            this.controller = controller;
            Width = 460; Height = 640; MinWidth = 380;
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NoiseReduction/Assets/NoiseReduction.ico")));

            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { dot, status } };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { title } };
            Grid.SetColumn(master, 1); header.Children.Add(master);

            var models = Row(fast, precise);
            var strength = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { strengthLabel } };
            Grid.SetColumn(strengthValue, 1); strength.Children.Add(strengthValue);
            var presetRow = Row(presets.Append(custom).ToArray());

            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(16), Spacing = 12,
                    Children =
                    {
                        Card(header, statusRow, hint),
                        Card(sourceLabel, source),
                        Card(modelLabel, models, modelNote, strength, presetRow, slider, lowCut),
                        Card(languageLabel, language, startup),
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
                            Children = { tagLink, commitLink, sourceLink },
                        },
                    }
                }
            };

            ToolTip.SetTip(tagLink, BuildInfo.TagUrl);
            ToolTip.SetTip(commitLink, BuildInfo.CommitUrl);
            ToolTip.SetTip(sourceLink, BuildInfo.Repository);
            master.IsCheckedChanged += (_, _) => { if (!updating) controller.Change(() => controller.Settings.Enabled = master.IsChecked == true); };
            lowCut.IsCheckedChanged += (_, _) => { if (!updating) controller.Change(() => controller.Profile.LowCut = lowCut.IsChecked == true); };
            startup.IsCheckedChanged += (_, _) => { if (!updating) Autostart.Set(startup.IsChecked == true); };
            fast.Click += (_, _) => SwitchModel(false);
            precise.Click += (_, _) => SwitchModel(true);
            for (int i = 0; i < presets.Length; i++)
            {
                int index = i;
                presets[i].Click += (_, _) => { customOpen = false; controller.Change(() => controller.Profile.Attenuation = Values[index]); };
            }
            custom.Click += (_, _) => { customOpen = true; Render(); };
            slider.PropertyChanged += (_, args) =>
            {
                if (args.Property == RangeBase.ValueProperty && !updating) controller.Change(() => controller.Profile.Attenuation = (int)Math.Round(slider.Value));
            };
            source.SelectionChanged += (_, _) =>
            {
                if (updating || source.SelectedIndex < 0) return;
                controller.SetSource(source.SelectedIndex == 0 ? null : controller.Devices[source.SelectedIndex - 1].Name);
            };
            language.SelectionChanged += (_, _) =>
            {
                if (updating || language.SelectedIndex < 0) return;
                controller.Change(() => controller.Settings.Language = language.SelectedIndex == 0 ? "system" : Languages.Ids[language.SelectedIndex - 1]);
            };
            controller.Changed += Render;
            // закрытие только прячет окно, приложение остаётся в трее; выключению системы мешать нельзя
            Closing += (_, args) =>
            {
                if (App.Exiting || args.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
                args.Cancel = true;
                Hide();
                // при открытии окно выглядит как новое: начало страницы, своя сила не раскрыта
                customOpen = false;
                ((ScrollViewer)Content).Offset = default;
                Render();
            };
            Render();
        }

        private int[] Values => controller.Profile.DeepFilter ? PreciseValues : FastValues;

        private void SwitchModel(bool deepFilter)
        {
            var profile = controller.Profile;
            if (profile.DeepFilter == deepFilter) { Render(); return; }
            // пресет сохраняется при смене модели: обычно остаётся обычно
            int index = Array.IndexOf(Values, profile.Attenuation);
            controller.Change(() =>
            {
                profile.DeepFilter = deepFilter;
                if (index >= 0) profile.Attenuation = Values[index];
            });
        }

        internal void Render()
        {
            updating = true;
            var settings = controller.Settings;
            var profile = controller.Profile;
            Title = "Noise Reduction";
            title.Text = T("Шумоподавление");
            master.IsChecked = settings.Enabled;
            (status.Text, dot.Fill) = controller.State switch
            {
                State.Working => (T("Работает"), Brush("#0F7B0F")),
                State.Paused => (T("Пауза"), Brush("#8A8A8A")),
                State.Error => (T("Ошибка обработки") + (controller.Error != null ? ": " + controller.Error : ""), Brush("#C42B1C")),
                _ => (T("Ждёт программ"), Brush("#0F7B0F")),
            };
            hint.Text = T("В Zoom, Discord и других программах выберите микрофон Noise Reduction");

            sourceLabel.Text = T("Исходный микрофон");
            var names = new[] { T("Как в системе") }.Concat(controller.Devices.Select(device => device.Name)).ToList();
            if (!source.Items.Cast<string>().SequenceEqual(names)) { source.Items.Clear(); foreach (var name in names) source.Items.Add(name); }
            int selected = controller.Devices.ToList().FindIndex(device => device.Name == settings.Source);
            source.SelectedIndex = selected < 0 ? 0 : selected + 1;

            modelLabel.Text = T("Модель");
            fast.Content = T("Быстрая"); precise.Content = T("Точная");
            fast.IsChecked = !profile.DeepFilter; precise.IsChecked = profile.DeepFilter;
            modelNote.Text = T("Точная лучше убирает клавиатуру и голоса на фоне, но чуть сильнее нагружает процессор");
            strengthLabel.Text = T("Сила подавления");
            strengthValue.Text = profile.Attenuation >= 60 ? T("Максимум") : profile.Attenuation + " " + T("дБ");
            int preset = Array.IndexOf(Values, profile.Attenuation);
            if (preset < 0) customOpen = true;
            for (int i = 0; i < presets.Length; i++) { presets[i].Content = T(PresetNames[i]); presets[i].IsChecked = !customOpen && i == preset; }
            custom.Content = T("Своё"); custom.IsChecked = customOpen;
            slider.IsVisible = customOpen;
            slider.Value = profile.Attenuation;
            lowCut.Content = T("Срезать низкий гул");
            lowCut.IsChecked = profile.LowCut;

            sourceLink.Content = T("Исходный код");
            languageLabel.Text = T("Язык");
            var languages = new[] { T("Как в системе") }.Concat(Languages.Names).ToList();
            if (!language.Items.Cast<string>().SequenceEqual(languages)) { language.Items.Clear(); foreach (var name in languages) language.Items.Add(name); }
            language.SelectedIndex = settings.Language == "system" ? 0 : Array.IndexOf(Languages.Ids, settings.Language) + 1;
            startup.Content = T("Запускать при входе в систему");
            startup.IsChecked = Autostart.Enabled;
            updating = false;
        }

        private static string T(string text) => UiText.Get(text);
        private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
        private static TextBlock Heading() => new() { FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private static TextBlock Label() => new() { FontWeight = FontWeight.SemiBold };
        private static TextBlock Caption() => new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private static ToggleButton Segment() => new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };

        private static Grid Row(params Control[] items)
        {
            var row = new Grid { ColumnSpacing = 6 };
            for (int i = 0; i < items.Length; i++) { row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); Grid.SetColumn(items[i], i); row.Children.Add(items[i]); }
            return row;
        }

        private static Border Card(params Control[] items)
        {
            var panel = new StackPanel { Spacing = 8 };
            foreach (var item in items) panel.Children.Add(item);
            return new Border
            {
                Padding = new Thickness(16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                // полупрозрачные серые одинаково смотрятся в светлой и тёмной теме
                Background = new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
                Child = panel,
            };
        }

    }
}
