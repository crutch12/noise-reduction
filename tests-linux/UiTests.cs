using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoiseReduction.Avalonia;
using NoiseReduction.Desktop;

namespace NoiseReduction.Linux.Tests
{
    internal static class UiTests
    {
        private static bool initialized;

        private static void Initialize()
        {
            if (initialized) return;
            AppBuilder.Configure<App>().With(AppTheme.FontOptions).UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(null);
            App.Exiting = true;
            AppTheme.RefreshPalette(false);
            initialized = true;
        }

        private static Controller ControllerFixture(string defaultSource = "test_raw")
        {
            var controller = (Controller)RuntimeHelpers.GetUninitializedObject(typeof(Controller));
            Set(controller, "<Settings>k__BackingField", new Settings { Language = "en" });
            Set(controller, "settingsGate", new object());
            Set(controller, "devices", new[] { new Microphone("test", "Test microphone", "test_raw", true) });
            Set(controller, "apps", Array.Empty<RecordingApp>());
            Set(controller, "rules", Array.Empty<ApplicationRuleInfo>());
            Set(controller, "statuses", new Dictionary<string, DeviceStatus>());
            Set(controller, "selectedId", "test");
            Set(controller, "currentDefault", defaultSource);
            Set(controller, "stopping", 1);
            return controller;
        }

        private static void Set(object value, string name, object field) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, field);

        private static void Layout(Window window)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        private static void Click(Window window, Control control)
        {
            Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window).Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Layout(window);
        }

        [Test]
        internal static void UiMicrophoneToggleDoesNotExpandOrCollapseItsCard()
        {
            Initialize();
            var controller = ControllerFixture();
            using var voice = new VoiceTest();
            var card = new MicrophoneCard(controller, voice, "test", _ => { });
            var window = new Window { Width = 720, Height = 800, Content = card.Container, VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Top };
            window.Show(); Layout(window);
            try
            {
                var toggle = card.Container.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "MicrophoneEnabled");
                double before = card.Container.Bounds.Height;
                bool enabled = toggle.IsChecked == true;
                Click(window, toggle);
                Assert.True(toggle.IsChecked != enabled, "Clicking the microphone switch must toggle it");
                Assert.True(Math.Abs(before - card.Container.Bounds.Height) < 1, "The microphone switch must not expand its card");
                var header = card.Container.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ExpandMicrophone");
                Rect bounds = header.Bounds;
                Click(window, header);
                double expanded = card.Container.Bounds.Height;
                Assert.True(expanded > before + 100, "Clicking the microphone title must reveal its controls");
                Assert.Equal(bounds, header.Bounds);
                enabled = toggle.IsChecked == true;
                Click(window, toggle);
                Assert.True(toggle.IsChecked != enabled, "The switch must remain usable in an expanded card");
                Assert.True(Math.Abs(expanded - card.Container.Bounds.Height) < 1, "The microphone switch must not collapse its card");
                Click(window, header);
                Assert.True(Math.Abs(before - card.Container.Bounds.Height) < 1, "Closing the card must restore its original height");
            }
            finally { window.Close(); }
        }

        [Test]
        internal static void UiApplicationIconUsesDesktopMetadataAndLegacyThemeDirectory()
        {
            Initialize();
            string oldData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string folder = Path.Combine(Path.GetTempPath(), "nr-icon-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("XDG_DATA_HOME", folder);
                Directory.CreateDirectory(Path.Combine(folder, "applications"));
                string icons = Path.Combine(folder, "icons/hicolor/32x32");
                Directory.CreateDirectory(icons);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(16, 16), new Vector(96, 96))) bitmap.Save(Path.Combine(icons, "editor-logo.png"));
                File.WriteAllText(Path.Combine(folder, "applications/org.example.Editor.desktop"), "[Desktop Entry]\nName=Editor\nIcon=editor-logo\n");
                var app = new RecordingApp(1, "org.example.Editor", "Editor", "", "test_raw", false, false);
                Assert.True(AppIcons.Create(app) is Image, "The installed desktop entry and flat hicolor directory must resolve the real icon");
            }
            finally { Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldData); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }

        [Test]
        internal static void UiApplicationIconLoadsSvgAndDoesNotInventAnInitial()
        {
            Initialize();
            string icon = Path.Combine(Path.GetTempPath(), "nr-icon-" + Guid.NewGuid().ToString("N") + ".svg");
            try
            {
                File.WriteAllText(icon, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><rect width=\"16\" height=\"16\" fill=\"red\"/></svg>");
                Assert.True(AppIcons.Create(new RecordingApp(1, "svg-test", "Editor", icon, "test_raw", false, false)) is Image, "SVG-only application icons must render");
                var fallback = AppIcons.Create(new RecordingApp(2, "unknown:missing", "Unknown", "", "test_raw", false, false));
                Assert.True(fallback is UiIcon, "An unknown application needs an application glyph rather than its first letter");
            }
            finally { File.Delete(icon); }
        }

        [Test]
        internal static void UiConsumerOverflowCountsApplicationsInsteadOfStreams()
        {
            Initialize();
            var applications = Enumerable.Range(1, 6).Select(index => new RecordingApp((uint)index, "unknown:test" + index, "App " + index, "", "test_raw", false, false)).ToArray();
            var panel = new StackPanel();
            AppIcons.Update(panel, applications.Concat(applications).ToArray());
            Assert.Equal(6, panel.Children.Count);
            Assert.Equal("+1", ((TextBlock)panel.Children[5]).Text);
        }

        [Test]
        internal static void UiConsumerIconStaysBesideStatusAndHasItsOwnTooltip()
        {
            string icon = Path.Combine(Path.GetTempPath(), "nr-consumer-" + Guid.NewGuid().ToString("N") + ".png");
            Initialize();
            using (var bitmap = new RenderTargetBitmap(new PixelSize(16, 16), new Vector(96, 96))) bitmap.Save(icon);
            try
            {
                WithMainWindow((window, controller) =>
                {
                    Set(controller, "apps", new[] { new RecordingApp(1, "binary:audacity", "Audacity", icon, "test_raw", false, false) });
                    Set(controller, "statuses", new Dictionary<string, DeviceStatus> { ["test"] = new(State.Working, "", false, default) });
                    window.Render(); Layout(window);
                    var card = window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "MicrophoneSettingsCard");
                    var image = card.GetVisualDescendants().OfType<Image>().Single();
                    var status = card.GetVisualDescendants().OfType<TextBlock>().Single(control => control.IsEffectivelyVisible && (control.Text ?? "").StartsWith("Working", StringComparison.Ordinal));
                    var measured = new TextBlock { Text = status.Text, FontFamily = status.FontFamily, FontSize = status.FontSize, FontWeight = status.FontWeight };
                    measured.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Point left = image.TranslatePoint(default, window).Value;
                    Point right = status.TranslatePoint(new Point(measured.DesiredSize.Width, 0), window).Value;
                    var hit = window.InputHitTest(image.TranslatePoint(new Point(7, 7), window).Value) as Visual;
                    string target = hit?.GetType().Name;
                    object tip = null;
                    while (hit != null && tip == null) { if (hit is Control control) tip = ToolTip.GetTip(control); hit = hit.GetVisualParent(); }
                    Assert.True(left.X - right.X <= 12 && tip as string == "Audacity", $"Consumer gap={left.X - right.X}; tooltip={tip}; hit={target}");
                    double before = card.Bounds.Height;
                    Click(window, image);
                    Assert.True(card.Bounds.Height > before + 40, "Clicking a consumer icon must still expand its microphone card");
                });
            }
            finally { File.Delete(icon); }
        }

        [Test]
        internal static void UiVoiceComparisonFitsBesideRecordAndPlay()
        {
            WithMainWindow((window, _) =>
            {
                window.Width = 550;
                Layout(window);
                var header = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ExpandMicrophone");
                Click(window, header);
                var record = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "RecordVoice");
                var comparison = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "ListenProcessed").Parent as Control;
                double firstRow = record.TranslatePoint(default, window).Value.Y;
                Assert.True(Math.Abs(comparison.TranslatePoint(default, window).Value.Y - firstRow) < 1, "Comparison must share a row with Record and Play when their content fits");
            });
        }

        [Test]
        internal static void UiVoiceComparisonStaysInsideTheMinimumWindow()
        {
            WithMainWindow((window, _) =>
            {
                window.Width = 440;
                Layout(window);
                Click(window, window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ExpandMicrophone"));
                var card = window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "MicrophoneSettingsCard");
                var comparison = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "ListenProcessed").Parent as Control;
                Point position = comparison.TranslatePoint(default, card).Value;
                Assert.True(position.X >= 0 && position.X + comparison.Bounds.Width <= card.Bounds.Width, "The whole comparison control must fit inside a narrow card");
            });
        }

        [Test]
        internal static void UiAudioMetersExplainTheirIdleState()
        {
            WithMainWindow((window, controller) =>
            {
                Click(window, window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ExpandMicrophone"));
                Click(window, window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "AdvancedSettings"));
                var text = window.GetVisualDescendants().OfType<TextBlock>().Select(control => control.Text ?? "").ToArray();
                Assert.True(text.Contains("Before noise reduction") && text.Contains("After noise reduction"), "Audio meters must describe the processing stages");
                Assert.True(text.Any(value => value.Contains("when an app is recording", StringComparison.OrdinalIgnoreCase)), "Idle meters must explain when they receive real audio levels");
                var input = window.GetVisualDescendants().OfType<ProgressBar>().Single(control => control.Name == "MicrophoneInputLevel");
                var output = window.GetVisualDescendants().OfType<ProgressBar>().Single(control => control.Name == "MicrophoneOutputLevel");
                Assert.True(!input.IsEnabled && !output.IsEnabled);
                Set(controller, "statuses", new Dictionary<string, DeviceStatus> { ["test"] = new(State.Working, "", false, new BridgeMetrics { Running = 1, InputPeak = .5f, OutputPeak = .25f }) });
                window.Render(); Layout(window);
                Assert.True(input.IsEnabled && output.IsEnabled);
                Assert.Equal(.5, input.Value); Assert.Equal(.25, output.Value);
            });
        }

        [Test]
        internal static void UiLanguageNamesHaveGlyphsForEverySupportedScript()
        {
            Initialize();
            foreach (string name in Languages.Names)
                foreach (var rune in name.EnumerateRunes())
                {
                    if (System.Text.Rune.IsWhiteSpace(rune)) continue;
                    bool found = FontManager.Current.TryMatchCharacter(rune.Value, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, AppTheme.UiFont, CultureInfo.InvariantCulture, out _);
                    Assert.True(found, $"No glyph for U+{rune.Value:X4} in {name}");
                }
        }

        [Test]
        internal static void UiFooterLinksHaveSeparateClickTargets()
        {
            WithMainWindow((window, _) =>
            {
                var links = window.GetVisualDescendants().OfType<HyperlinkButton>().ToArray();
                Assert.Equal(3, links.Length);
                for (int index = 1; index < links.Length; index++)
                {
                    Point previous = links[index - 1].TranslatePoint(new Point(links[index - 1].Bounds.Width, 0), window).Value;
                    Point next = links[index].TranslatePoint(default, window).Value;
                    Assert.True(Math.Abs(previous.Y - next.Y) > 1 || next.X - previous.X >= 12, "Version, commit and source links need at least 12 px between targets");
                }
            });
        }

        [Test]
        internal static void UiDefaultMicrophoneActionIsAtTheTopAndShowsItsResult()
        {
            WithMainWindow((window, controller) =>
            {
                var setup = window.GetVisualDescendants().OfType<Control>().SingleOrDefault(control => control.Name == "MicrophoneSetup");
                Assert.True(setup != null, "The microphone setup action must be visible above the microphone list");
                var list = window.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "MicrophoneList");
                Assert.True(setup.TranslatePoint(default, window).Value.Y < list.TranslatePoint(default, window).Value.Y);
                var button = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "MakeDefaultMicrophone");
                Assert.True(button.IsEnabled);
                Set(controller, "currentDefault", "noise_reduction");
                window.Render(); Layout(window);
                Assert.True(!button.IsEnabled, "Selecting the clean default must disable the already-completed action");
                Assert.True(button.Content.ToString().Contains("selected", StringComparison.OrdinalIgnoreCase), "The action must confirm its selected state");
            });
        }

        [Test]
        internal static void UiTrayStateChangesOnlyTheCentralWave()
        {
            Initialize();
            byte[] reference = Glyph(State.Idle);
            foreach (State state in Enum.GetValues<State>())
            {
                byte[] pixels = Glyph(state);
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        if (x >= 20 && x <= 44 && y >= 12 && y <= 36) continue;
                        for (int channel = 0; channel < 4; channel++)
                            Assert.Equal(reference[(y * 64 + x) * 4 + channel], pixels[(y * 64 + x) * 4 + channel]);
                    }
            }
        }

        [Test]
        internal static void UiTrayColorsMatchWindowsAndMac()
        {
            Initialize();
            foreach (var pair in new[] { (State.Idle, 10, 140, 128), (State.Working, 10, 140, 128),
                (State.Paused, 140, 140, 140), (State.Bypass, 140, 140, 140), (State.Error, 196, 43, 28) })
            {
                byte[] pixels = Glyph(pair.Item1);
                bool found = false;
                for (int offset = 0; offset < pixels.Length; offset += 4)
                    if (pixels[offset + 3] == 255 && pixels[offset + 1] == pair.Item3 &&
                        (pixels[offset] == pair.Item2 && pixels[offset + 2] == pair.Item4 || pixels[offset] == pair.Item4 && pixels[offset + 2] == pair.Item2)) found = true;
                Assert.True(found, "The central wave must use the shared tray palette for " + pair.Item1);
            }
        }

        private static byte[] Glyph(State state)
        {
            Type type = typeof(TrayGlyph).GetNestedType("Glyph", BindingFlags.NonPublic);
            var glyph = (Control)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { state, true }, null);
            glyph.Measure(new Size(32, 32)); glyph.Arrange(new Rect(0, 0, 32, 32));
            using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(192, 192));
            bitmap.Render(glyph);
            byte[] pixels = new byte[64 * 64 * 4];
            IntPtr memory = Marshal.AllocHGlobal(pixels.Length);
            try { bitmap.CopyPixels(new PixelRect(0, 0, 64, 64), memory, pixels.Length, 64 * 4); Marshal.Copy(memory, pixels, 0, pixels.Length); }
            finally { Marshal.FreeHGlobal(memory); }
            return pixels;
        }

        private static void WithMainWindow(Action<MainWindow, Controller> action)
        {
            Initialize();
            string previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string directory = Path.Combine(Path.GetTempPath(), "nr-ui-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            var controller = ControllerFixture();
            using var voice = new VoiceTest();
            var window = new MainWindow(controller, voice);
            window.Show(); Layout(window);
            try { action(window, controller); }
            finally { window.Close(); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
