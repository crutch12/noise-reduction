using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using NoiseReduction.Desktop;
using static NoiseReduction.Avalonia.AppTheme;

namespace NoiseReduction.Avalonia
{
    internal sealed class MainWindow : Window
    {
        private readonly Controller controller;
        private readonly VoiceTest voice;
        private readonly Dictionary<string, MicrophoneCard> cards = new(StringComparer.Ordinal);
        private readonly ToggleSwitch master = Switch("MasterSwitch"), startup = Switch("StartupSwitch"),
            newMicrophones = Switch("NewMicrophonesSwitch"), battery = Switch("BatterySwitch");
        private readonly TextBlock globalTitle = Text(null, true), globalHelp = Caption(), microphonesHeading = Text(null, true),
            microphonesHelp = Caption(), setupTitle = Text(null, true), setupHelp = Caption(),
            empty = Caption(), appHeading = Text(null, true), languageLabel = Text(), windowThemeLabel = Text(), trayThemeLabel = Text(),
            startupLabel = Text(), trayHelp = Caption(), newLabel = Text(), newHelp = Caption(), batteryLabel = Text(), batteryHelp = Caption(),
            uninstallLabel = Text(), uninstallHelp = Caption(), appsHeading = Text(null, true), sourceLabel = Text(), routeHelp = Caption(),
            errorText = Text(), capability = Caption(), sourceHint = Caption();
        private readonly ComboBox language = new() { Name = "LanguagePicker", MinWidth = 160 },
            windowTheme = new() { Name = "WindowThemePicker", MinWidth = 160 }, trayTheme = new() { Name = "TrayThemePicker", MinWidth = 160 },
            source = new() { Name = "SourcePicker", MinWidth = 170, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly Button refresh = new() { Name = "RefreshMicrophones", Content = new UiIcon("refresh"), Padding = new Thickness(8), Background = Brushes.Transparent, BorderThickness = new Thickness(0) },
            uninstall = new() { Name = "UninstallButton", MinWidth = 96 }, makeDefault = new() { Name = "MakeDefaultMicrophone" },
            copyDiagnostics = new() { Name = "CopyDiagnostics" }, exit = new() { Name = "ExitWithoutTray" };
        private readonly HyperlinkButton tagLink = new() { Content = BuildInfo.Tag, NavigateUri = BuildInfo.Version.Contains("-dev", StringComparison.OrdinalIgnoreCase) ? null : new Uri(BuildInfo.TagUrl), FontSize = 12 },
            commitLink = new() { Content = BuildInfo.ShortCommit, NavigateUri = new Uri(BuildInfo.CommitUrl), FontSize = 12, IsVisible = BuildInfo.Commit.Length > 0 },
            sourceLink = new() { NavigateUri = new Uri(BuildInfo.Repository), FontSize = 12 };
        private readonly StackPanel microphoneList = new() { Name = "MicrophoneList", Spacing = 4 }, appList = new() { Name = "ApplicationRoutes", Spacing = 4 },
            ruleList = new() { Name = "SavedApplicationRules", Spacing = 10 };
        private readonly Expander rules = new() { Name = "SavedRules", HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly UiIcon setupIcon = new("warning");
        private readonly Border errorBox, uninstallCard, setup;
        private readonly ScrollViewer scroll;
        private bool updating, trayAvailable = true;
        private string appSignature, ruleSignature, localError;
        private uint voiceConfiguration;
        internal Action ExitRequested;
        internal Action UninstallRequested;

        internal MainWindow(Controller controller, VoiceTest voice)
        {
            this.controller = controller; this.voice = voice;
            Title = "Noise Reduction"; Width = 760; Height = 780; MinWidth = 440; MinHeight = 400;
            FontFamily = UiFont;
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NoiseReduction/Assets/NoiseReduction.ico")));
            var dismiss = new Button { Content = "×", Name = "DismissError", Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Top };
            dismiss.Click += (_, _) => { localError = null; errorBox.IsVisible = false; };
            errorBox = Card(Columns("*,Auto", errorText, dismiss), 12); errorBox.IsVisible = false;
            errorText.Foreground = StateBrush(State.Error);
            errorText.FontWeight = FontWeight.SemiBold;
            var microphonesHeader = Columns("*,Auto", Captioned(microphonesHeading, microphonesHelp), refresh);
            microphonesHeader.Margin = new Thickness(2, 16, 0, 4);
            appHeading.Margin = appsHeading.Margin = new Thickness(2, 20, 0, 4);
            uninstallCard = Setting("delete", Captioned(uninstallLabel, uninstallHelp), uninstall);
            rules.Content = ruleList;
            makeDefault.HorizontalAlignment = HorizontalAlignment.Left;
            setup = Card(Columns("24,*", setupIcon, Stack(setupTitle, setupHelp, makeDefault)));
            setup.Name = "MicrophoneSetup";
            foreach (var link in new[] { tagLink, commitLink, sourceLink }) { link.Margin = new Thickness(8, 4); link.Padding = new Thickness(4, 2); }
            var routing = Stack(Columns("*,Auto", Captioned(sourceLabel, sourceHint), source), routeHelp, capability, appList, rules, copyDiagnostics);
            var page = new StackPanel
            {
                Name = "SettingsPage", Spacing = 4, Margin = new Thickness(20, 12, 20, 20), MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    Setting("microphone", Captioned(globalTitle, globalHelp), master), errorBox, setup, microphonesHeader, microphoneList, empty, appHeading,
                    Setting("language", languageLabel, language), Setting("theme", windowThemeLabel, windowTheme), Setting("tray", trayThemeLabel, trayTheme),
                    Setting("startup", Captioned(startupLabel, trayHelp), startup), Setting("new", Captioned(newLabel, newHelp), newMicrophones),
                    Setting("battery", Captioned(batteryLabel, batteryHelp), battery), uninstallCard, appsHeading, Card(routing), exit,
                    new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0), Children = { tagLink, commitLink, sourceLink } },
                },
            };
            scroll = new ScrollViewer { Name = "SettingsScroll", Content = page, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            Content = scroll;
            foreach (var id in new[] { "system" }.Concat(Languages.Ids)) language.Items.Add(new LanguageOption(id));
            master.IsCheckedChanged += (_, _) => { if (!updating) { bool value = master.IsChecked == true; controller.Change(() => controller.Settings.Enabled = value); } };
            startup.IsCheckedChanged += (_, _) => { if (!updating) { bool value = startup.IsChecked == true; Run(() => Task.Run(() => Autostart.Set(value))); } };
            newMicrophones.IsCheckedChanged += (_, _) => { if (!updating) { bool value = newMicrophones.IsChecked == true; controller.Change(() => controller.Settings.EnableNewMicrophones = value); } };
            battery.IsCheckedChanged += (_, _) => { if (!updating) { bool value = battery.IsChecked == true; controller.Change(() => controller.Settings.FastOnBattery = value); } };
            language.SelectionChanged += (_, _) => { if (!updating && language.SelectedItem is LanguageOption item) controller.Change(() => controller.Settings.Language = item.Id); };
            windowTheme.SelectionChanged += (_, _) => { if (!updating && windowTheme.SelectedIndex >= 0) { string value = ThemeId(windowTheme.SelectedIndex); controller.Change(() => controller.Settings.WindowTheme = value); } };
            trayTheme.SelectionChanged += (_, _) => { if (!updating && trayTheme.SelectedIndex >= 0) { string value = ThemeId(trayTheme.SelectedIndex); controller.Change(() => controller.Settings.TrayTheme = value); } };
            source.SelectionChanged += (_, _) => { if (!updating && source.SelectedItem is SourceOption choice) controller.SetSource(choice.Id); };
            makeDefault.Click += (_, _) => Run(controller.MakeDefaultAsync);
            refresh.Click += (_, _) => Run(controller.RefreshAsync);
            copyDiagnostics.Click += (_, _) => Run(async () => { if (Clipboard != null) await Clipboard.SetTextAsync(controller.Diagnostics()); });
            exit.Click += (_, _) => ExitRequested?.Invoke();
            uninstall.Click += (_, _) => Run(ConfirmUninstallAsync);
            controller.Changed += Render; voice.Changed += VoiceChanged;
            ActualThemeVariantChanged += (_, _) => { RefreshPalette(ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark); Background = AppTheme.Background; foreach (var card in cards.Values) card.Update(); };
            KeyDown += (_, args) => { if (args.Key == Key.Escape) { Close(); args.Handled = true; } };
            Closing += (_, args) =>
            {
                WindowLayout.Save(this);
                if (App.Exiting || args.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
                args.Cancel = true; voice.Clear(); scroll.Offset = default;
                foreach (var card in cards.Values) card.ResetView();
                if (trayAvailable) Hide(); else ExitRequested?.Invoke();
            };
            Opened += (_, _) => WindowLayout.Restore(this);
            Render();
        }

        internal void SetTrayAvailable(bool value)
        {
            trayAvailable = value; exit.IsVisible = !value;
            if (!value && !IsVisible) { Show(); Activate(); }
            Render();
        }
        private static string ThemeId(int index) => index == 1 ? "light" : index == 2 ? "dark" : "system";
        private static int ThemeIndex(string id) => id == "light" ? 1 : id == "dark" ? 2 : 0;
        private static void SetThemeItems(ComboBox picker, string id)
        {
            var names = new[] { T("Как в системе"), T("Светлая"), T("Тёмная") };
            if (!picker.Items.Cast<string>().SequenceEqual(names)) { picker.Items.Clear(); foreach (string name in names) picker.Items.Add(name); }
            picker.SelectedIndex = ThemeIndex(id);
        }
        private void VoiceChanged() { foreach (var card in cards.Values) card.Update(); }

        internal void Render()
        {
            if (updating) return;
            updating = true;
            try
            {
                var settings = controller.Settings; UiText.Language = settings.Language;
                if (RequestedThemeVariant != Variant(settings.WindowTheme)) RequestedThemeVariant = Variant(settings.WindowTheme);
                RefreshPalette(ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark); Background = AppTheme.Background;
                FlowDirection = UiText.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                globalTitle.Text = T("Шумоподавление");
                globalHelp.Text = settings.Enabled ? T("Общий выключатель, настройки микрофонов сохраняются") : T("Шумоподавление на паузе");
                master.IsChecked = settings.Enabled; Describe(master, globalTitle.Text);
                bool selected = controller.ProcessedDefault;
                setup.IsVisible = controller.Devices.Any(device => device.Available);
                setupTitle.Text = selected ? Local("Noise Reduction выбран основным микрофоном", "Noise Reduction is the default microphone")
                    : Local("Программы могут получать звук без обработки", "Apps may be using unprocessed audio");
                setupTitle.Foreground = StateBrush(selected ? State.Working : State.Fallback);
                setupIcon.Kind = selected ? "check" : "warning";
                setupHelp.Text = selected
                    ? Local("Новые программы получат обработанный звук по умолчанию, уже открытые могут сохранить свой вход", "New apps receive processed audio by default; open apps may keep their selected input")
                    : Local("Выберите Noise Reduction в нужной программе либо сделайте его основным микрофоном Linux", "Choose Noise Reduction in your app, or set it as the default microphone in Linux");
                makeDefault.Content = selected ? Local("Выбрано", "Selected") : T("Сделать основным микрофоном");
                makeDefault.IsEnabled = !selected && !controller.Busy && setup.IsVisible;
                microphonesHeading.Text = T("Микрофоны"); refresh.IsEnabled = !controller.Busy; Describe(refresh, T("Обновить"));
                microphonesHelp.Text = Local("Настройки для каждого физического микрофона сохраняются отдельно", "Settings are saved separately for each physical microphone");
                var ids = controller.Devices.Select(device => device.Id).ToArray();
                foreach (string id in cards.Keys.Except(ids).ToArray()) { microphoneList.Children.Remove(cards[id].Container); cards.Remove(id); }
                foreach (var device in controller.Devices)
                {
                    if (!cards.TryGetValue(device.Id, out var card))
                    {
                        card = new MicrophoneCard(controller, voice, device.Id, Run);
                        cards.Add(device.Id, card); microphoneList.Children.Add(card.Container);
                    }
                    card.Update();
                }
                empty.Text = Local("Подключите микрофон и нажмите Обновить", "Connect a microphone and press Refresh"); empty.IsVisible = cards.Count == 0;
                appHeading.Text = T("Приложение"); languageLabel.Text = T("Язык");
                language.SelectedIndex = settings.Language == "system" ? 0 : Array.IndexOf(Languages.Ids, settings.Language) + 1; Describe(language, languageLabel.Text);
                windowThemeLabel.Text = T("Тема окна"); trayThemeLabel.Text = T("Тема панели трея");
                SetThemeItems(windowTheme, settings.WindowTheme); SetThemeItems(trayTheme, settings.TrayTheme);
                Describe(windowTheme, windowThemeLabel.Text); Describe(trayTheme, trayThemeLabel.Text);
                startupLabel.Text = T("Запускать при входе в систему"); startup.IsChecked = Autostart.Enabled; Describe(startup, startupLabel.Text);
                trayHelp.Text = trayAvailable ? T("Крестик оставляет программу в трее") : Local("Трей недоступен, закрытие окна завершит программу", "The tray is unavailable; closing this window quits the app");
                trayHelp.Text += "\n" + (App.ShortcutAvailable ? T("Ctrl+Alt+N включает и выключает шумоподавление")
                    : Local("Для системного сочетания клавиш используйте команду noise-reduction toggle", "For a system keyboard shortcut, use the command noise-reduction toggle"));
                newLabel.Text = T("Включать шумоподавление для новых микрофонов"); newHelp.Text = Local("Сохранённые настройки микрофонов не меняются", "Existing microphone settings are kept");
                newMicrophones.IsChecked = settings.EnableNewMicrophones; Describe(newMicrophones, newLabel.Text);
                batteryLabel.Text = T("На батарее использовать быструю модель"); batteryHelp.Text = T("Пока ноутбук работает от батареи, вместо точной модели работает быстрая");
                battery.IsChecked = settings.FastOnBattery; Describe(battery, batteryLabel.Text, batteryHelp.Text);
                uninstallLabel.Text = T("Удалить программу"); uninstallHelp.Text = Local("Настройки микрофонов сохранятся", "Microphone settings will be kept");
                uninstall.Content = T("Удалить"); uninstallCard.IsVisible = Installation.IsInstalled;
                appsHeading.Text = T("Приложения"); sourceLabel.Text = T("Исходный микрофон");
                var actualSource = controller.Devices.FirstOrDefault(device => device.Id == controller.SelectedDeviceId);
                sourceHint.Text = actualSource == null ? Local("Нет доступного источника", "No source available")
                    : Local("Сейчас", "Current") + ": " + DeviceName(controller, actualSource);
                if (settings.Source != null && settings.Source != controller.SelectedDeviceId)
                    sourceHint.Text += "\n" + Local("Выбранный микрофон недоступен, используется временная замена", "The selected microphone is unavailable; using a temporary fallback");
                var choices = new[] { new SourceOption(null, T("Как в системе")) }.Concat(controller.Devices.Select(device => new SourceOption(device.Id, DeviceName(controller, device) + (!device.Available ? " · " + StateText(State.Unavailable) : "")))).ToArray();
                if (!source.Items.Cast<SourceOption>().SequenceEqual(choices)) { source.Items.Clear(); foreach (var choice in choices) source.Items.Add(choice); }
                source.SelectedIndex = Array.FindIndex(choices, item => item.Id == settings.Source); Describe(source, sourceLabel.Text);
                routeHelp.Text = T("В Zoom, Discord и других программах выберите микрофон Noise Reduction");
                capability.Text = !controller.RoutingSupported ? Local("Маршрутизация пока недоступна: проверьте подключение к аудиосерверу", "Routing is unavailable: check the audio server connection")
                    : controller.Apps.Count == 0 ? Local("Пока нет программ, записывающих звук", "No apps are recording audio") : "";
                capability.IsVisible = !string.IsNullOrEmpty(capability.Text); UpdateApps(); UpdateRules();
                copyDiagnostics.Content = T("Скопировать диагностику"); exit.Content = T("Выход"); exit.IsVisible = !trayAvailable; sourceLink.Content = T("Исходный код");
                string message = localError ?? controller.Error; errorBox.IsVisible = !string.IsNullOrEmpty(message); errorText.Text = UiText.Message(message);
                errorText.Foreground = StateBrush(State.Error);
            }
            finally { updating = false; }
            if (voice.DeviceId != null)
            {
                uint config = controller.ConfigurationFor(voice.DeviceId);
                if (config != voiceConfiguration) { voiceConfiguration = config; Run(() => voice.ReprocessAsync(config)); }
            }
        }

        private void UpdateApps()
        {
            string signature = UiText.EffectiveLanguage + "|" + controller.RoutingSupported + "|" + string.Join("|", controller.Apps.Select(app => app.ToString())) + "|" + string.Join("|", controller.Devices.Select(device => device.Id + device.Name + device.SourceName + device.Available + controller.OutputFor(device.Id)));
            if (signature == appSignature) return;
            appSignature = signature; appList.Children.Clear();
            foreach (var app in controller.Apps)
            {
                var application = app;
                var remember = new CheckBox { Content = Local("Запомнить", "Remember"), Name = "RememberApplicationRoute" };
                var mute = new CheckBox { Content = Local("Без звука", "Mute"), IsChecked = app.Muted, Name = "MuteApplication" };
                var route = new ComboBox { Name = "ApplicationMicrophone", HorizontalAlignment = HorizontalAlignment.Stretch };
                var targets = new List<SourceOption> { new("noise_reduction", "Noise Reduction") };
                foreach (var device in controller.Devices.Where(device => device.Available))
                {
                    string output = controller.OutputFor(device.Id);
                    if (!string.IsNullOrEmpty(output)) targets.Add(new SourceOption(output, DeviceName(controller, device) + " · " + T("Обработано")));
                    targets.Add(new SourceOption(device.SourceName, DeviceName(controller, device) + " · " + T("Оригинал")));
                }
                if (!targets.Any(target => target.Id == app.SourceName)) targets.Add(new SourceOption(app.SourceName, app.SourceName));
                foreach (var target in targets) route.Items.Add(target);
                route.SelectedIndex = targets.FindIndex(target => target.Id == app.SourceName); route.IsEnabled = mute.IsEnabled = controller.RoutingSupported;
                var clear = new Button { Content = Local("Забыть правило", "Forget rule"), Name = "ForgetApplicationRule", Padding = new Thickness(8, 4) };
                route.SelectionChanged += (_, _) => { if (route.SelectedItem is SourceOption target) Run(() => controller.RouteAsync(application.StreamId, target.Id, remember.IsChecked == true)); };
                mute.IsCheckedChanged += (_, _) => Run(() => controller.MuteAsync(application.StreamId, mute.IsChecked == true, remember.IsChecked == true));
                clear.Click += (_, _) => Run(() => controller.ClearRulesAsync(application.Id));
                var state = Caption(app.Corked ? Local("Запись приостановлена", "Recording paused") : app.Muted ? Local("Без звука", "Muted") : Local("Записывает звук", "Recording audio"));
                var options = new WrapPanel { Children = { mute, remember, clear } }; mute.Margin = remember.Margin = new Thickness(0, 0, 12, 0);
                var body = Stack(Columns("20,*", AppIcons.Create(app, 20), Stack(Text(app.Name, true), state)), route, options);
                appList.Children.Add(new Border { Padding = new Thickness(0, 12), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Line, Child = body });
                Describe(route, app.Name + ": " + T("Микрофоны")); Describe(mute, app.Name + ": " + Local("Без звука", "Mute"));
            }
        }

        private void UpdateRules()
        {
            rules.Header = Local("Сохранённые правила", "Saved rules");
            rules.IsVisible = controller.Rules.Count > 0;
            string signature = UiText.EffectiveLanguage + "|" + string.Join("|", controller.Rules.Select(rule => rule.ToString())) + "|" +
                string.Join("|", controller.Devices.Select(device => device.Id + device.Name + device.SourceName + device.Available));
            if (signature == ruleSignature) return;
            ruleSignature = signature;
            ruleList.Children.Clear();
            foreach (var rule in controller.Rules)
            {
                string id = rule.Id;
                var enabled = new CheckBox { Name = "ApplicationRuleEnabled", Content = rule.Name, IsChecked = rule.Enabled };
                var remove = new Button { Name = "RemoveApplicationRule", Content = T("Удалить"), Padding = new Thickness(8, 4) };
                var route = new ComboBox { Name = "SavedRuleMicrophone", HorizontalAlignment = HorizontalAlignment.Stretch };
                var targets = new List<SourceOption>
                {
                    new(null, Local("Не менять микрофон", "Keep microphone")), new("noise_reduction", "Noise Reduction"),
                };
                foreach (var device in controller.Devices)
                {
                    string suffix = device.Available ? "" : " · " + StateText(State.Unavailable);
                    targets.Add(new SourceOption(controller.OutputFor(device.Id), DeviceName(controller, device) + " · " + T("Обработано") + suffix));
                    targets.Add(new SourceOption(device.SourceName, DeviceName(controller, device) + " · " + T("Оригинал") + suffix));
                }
                if (!targets.Any(target => target.Id == rule.SourceName)) targets.Add(new SourceOption(rule.SourceName, rule.SourceName + " · " + StateText(State.Unavailable)));
                foreach (var target in targets) route.Items.Add(target);
                route.SelectedIndex = targets.FindIndex(target => target.Id == rule.SourceName);
                var mute = new ComboBox { Name = "SavedRuleMute", HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (string option in new[] { Local("Не менять звук", "Keep mute state"), Local("Со звуком", "Unmuted"), Local("Без звука", "Muted") }) mute.Items.Add(option);
                mute.SelectedIndex = !rule.Muted.HasValue ? 0 : rule.Muted.Value ? 2 : 1;
                void SaveRule()
                {
                    if (route.SelectedItem is not SourceOption target) return;
                    bool? muted = mute.SelectedIndex == 0 ? null : mute.SelectedIndex == 2;
                    Run(() => controller.SetRuleAsync(id, target.Id, muted));
                }
                route.SelectionChanged += (_, _) => SaveRule(); mute.SelectionChanged += (_, _) => SaveRule();
                enabled.IsCheckedChanged += (_, _) => { bool value = enabled.IsChecked == true; Run(() => controller.SetRuleEnabledAsync(id, value)); };
                remove.Click += (_, _) => Run(() => controller.ClearRulesAsync(id));
                ruleList.Children.Add(Stack(Columns("*,Auto", enabled, remove), route, mute));
                Describe(enabled, rule.Name + ": " + Local("Применять правило", "Apply rule"));
                Describe(remove, rule.Name + ": " + T("Удалить"));
                Describe(route, rule.Name + ": " + T("Микрофоны")); Describe(mute, rule.Name + ": " + Local("Без звука", "Mute"));
            }
        }

        private async Task ConfirmUninstallAsync()
        {
            var dialog = new Window { Title = T("Удалить программу"), Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FlowDirection = FlowDirection, RequestedThemeVariant = RequestedThemeVariant, Background = AppTheme.Background };
            var cancel = new Button { Content = T("Отмена") }; var confirm = new Button { Content = T("Удалить") }; bool accepted = false;
            cancel.Click += (_, _) => dialog.Close(); confirm.Click += (_, _) => { accepted = true; dialog.Close(); };
            dialog.KeyDown += (_, args) => { if (args.Key == Key.Escape) { dialog.Close(); args.Handled = true; } };
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 20,
                Children = { Text(Local("Удалить Noise Reduction? Настройки микрофонов сохранятся", "Uninstall Noise Reduction? Microphone settings will be kept")),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, confirm } } },
            };
            await dialog.ShowDialog(this);
            if (accepted) UninstallRequested?.Invoke();
        }
        private async void Run(Func<Task> action)
        {
            try { await action(); } catch (Exception error) { localError = error.Message; }
            Render();
        }
        private sealed record SourceOption(string Id, string Name) { public override string ToString() => Name; }
    }
}
