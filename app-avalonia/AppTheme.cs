using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{
    internal static class AppTheme
    {
        internal static readonly FontFamily UiFont = new("avares://NoiseReduction/Assets/Fonts#Noto Sans");
        internal static FontManagerOptions FontOptions => new()
        {
            FontFallbacks = new[]
            {
                new FontFallback { FontFamily = new FontFamily("avares://NoiseReduction/Assets/Fonts#Noto Sans Arabic") },
                new FontFallback { FontFamily = new FontFamily("avares://NoiseReduction/Assets/Fonts#Noto Sans Devanagari") },
                new FontFallback { FontFamily = new FontFamily("avares://NoiseReduction/Assets/Fonts#Noto Sans Thai") },
                new FontFallback { FontFamily = new FontFamily("avares://NoiseReduction/Assets/Fonts#Noto Sans CJK SC") },
            },
        };
        internal static string T(string value) => UiText.Get(value);
        internal static string Local(string russian, string english) => UiText.EffectiveLanguage == "ru" ? russian : english;
        internal static string DeviceName(Controller controller, Microphone device)
        {
            if (controller.Devices.Count(item => item.Name == device.Name) < 2) return device.Name;
            string identity = device.Id.Length > 6 ? device.Id[^6..] : device.Id;
            return device.Name + " · " + identity;
        }
        internal static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
        private static readonly SolidColorBrush line = new(), surface = new(), background = new(), foreground = new();
        private static bool isDark;
        internal static IBrush Line => line;
        internal static IBrush Surface => surface;
        internal static IBrush Background => background;
        internal static IBrush Foreground => foreground;
        internal static void RefreshPalette(bool? dark = null)
        {
            isDark = dark ?? Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            line.Color = Color.Parse(IsDark ? "#383B42" : "#E1E4E8");
            surface.Color = Color.Parse(IsDark ? "#24272D" : "#FFFFFF");
            background.Color = Color.Parse(IsDark ? "#1B1D22" : "#F4F5F7");
            foreground.Color = Color.Parse(IsDark ? "#E3E7ED" : "#1F252D");
        }
        internal static bool IsDark => isDark;
        internal static ThemeVariant Variant(string value) => value == "dark" ? ThemeVariant.Dark : value == "light" ? ThemeVariant.Light : ThemeVariant.Default;
        internal static IBrush StateBrush(State state) => Brush(state switch
        {
            State.Working => IsDark ? "#50D5C1" : "#087F74",
            State.Error => IsDark ? "#FF8B8B" : "#B32828",
            State.Recovering or State.Bypass or State.Fallback => IsDark ? "#F3C36E" : "#936000",
            _ => IsDark ? "#A3A9B3" : "#626A76",
        });
        internal static string StateText(State state) => state switch
        {
            State.Working => T("Работает"),
            State.Paused => T("Пауза"),
            State.Error => T("Ошибка обработки"),
            State.Recovering => Local("Восстановление звука", "Recovering audio"),
            State.Unavailable => Local("Микрофон недоступен", "Microphone unavailable"),
            State.Bypass => Local("Звук без обработки", "Unprocessed audio"),
            State.Fallback => Local("Временная замена", "Temporary fallback"),
            _ => T("Ждёт программ"),
        };
        internal static TextBlock Text(string value = null, bool strong = false) => new()
        {
            Text = value, FontFamily = UiFont, Foreground = Foreground, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        internal static TextBlock Caption(string value = null) => new()
        {
            Text = value, FontFamily = UiFont, Foreground = Foreground, FontWeight = FontWeight.Normal, FontSize = 12, Opacity = 0.76, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        internal static ToggleSwitch Switch(string name)
        {
            var control = new ToggleSwitch { MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, OnContent = "", OffContent = "", Name = name };
            return control;
        }
        internal static ToggleButton Segment(string name) => new()
        {
            Name = name, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10, 6),
        };
        internal static void Describe(Control control, string name, string help = null)
        {
            AutomationProperties.SetName(control, name);
            if (help != null) { AutomationProperties.SetHelpText(control, help); ToolTip.SetTip(control, help); }
        }
        internal static Grid Columns(string columns, params Control[] controls)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 12 };
            for (int i = 0; i < controls.Length; i++) { Grid.SetColumn(controls[i], i); grid.Children.Add(controls[i]); }
            return grid;
        }
        internal static Grid Segments(params Control[] controls) => Columns(string.Join(",", controls.Select(_ => "*")), controls);
        internal static Border Card(Control content, double padding = 16) => new()
        {
            Padding = new Thickness(padding), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), BorderBrush = Line, Background = Surface, Child = content,
        };
        internal static Border Setting(string icon, Control label, Control value) => Card(Columns("24,*,Auto", new UiIcon(icon), label, value));
        internal static Border Divider() => new() { Height = 1, Background = Line, Margin = new Thickness(0, 5) };
        internal static StackPanel Stack(params Control[] controls)
        {
            var panel = new StackPanel { Spacing = 8 };
            foreach (var control in controls) panel.Children.Add(control);
            return panel;
        }
        internal static StackPanel Captioned(Control title, Control caption)
        {
            var panel = Stack(title, caption);
            panel.Spacing = 2;
            return panel;
        }
    }

    internal sealed class UiIcon : Control
    {
        private string kind;
        internal string Kind
        {
            get => kind;
            set { if (kind != value) { kind = value; InvalidateVisual(); } }
        }
        private TopLevel owner;
        internal UiIcon(string kind)
        {
            this.kind = kind; Width = Height = 20; VerticalAlignment = VerticalAlignment.Center;
            AttachedToVisualTree += (_, _) =>
            {
                owner = TopLevel.GetTopLevel(this);
                if (owner != null) owner.ActualThemeVariantChanged += ThemeChanged;
                InvalidateVisual();
            };
            DetachedFromVisualTree += (_, _) => { if (owner != null) owner.ActualThemeVariantChanged -= ThemeChanged; owner = null; };
        }
        private void ThemeChanged(object sender, EventArgs args) => InvalidateVisual();
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            bool dark = TopLevel.GetTopLevel(this)?.ActualThemeVariant == ThemeVariant.Dark;
            var pen = new Pen(AppTheme.Brush(dark ? "#DCE0E7" : "#414953"), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            string path = kind switch
            {
                "refresh" => "M 16,7 A 7,7 0 1 0 17,12 M 16,2 L 16,7 11,7",
                "language" => "M 3,4 L 14,4 M 8,2 L 8,4 M 5,4 Q 6,10 12,12 M 12,4 Q 11,10 3,13 M 11,18 L 15,9 19,18 M 13,15 L 17,15",
                "theme" => "M 10,2 A 8,8 0 1 0 10,18 A 2,2 0 0 0 12,16 Q 10,13 14,12 L 16,12 Q 20,12 17,6 Q 15,2 10,2 M 6,6 L 6.1,6 M 11,5 L 11.1,5 M 15,8 L 15.1,8 M 5,11 L 5.1,11",
                "tray" => "M 3,4 L 17,4 17,14 3,14 Z M 7,18 L 13,18 M 10,14 L 10,18 M 6,11 L 8,8 11,11 14,7",
                "startup" => "M 4,12 A 7,7 0 1 0 12,3 M 10,1 L 10,10 M 6,6 L 10,10 14,6",
                "new" => "M 10,3 L 10,17 M 3,10 L 17,10",
                "battery" => "M 2,5 L 16,5 16,15 2,15 Z M 18,8 L 18,12 M 9,7 L 6,11 10,11 8,14",
                "settings" => "M 3,5 L 17,5 M 3,10 L 17,10 M 3,15 L 17,15 M 7,3 L 7,7 M 13,8 L 13,12 M 8,13 L 8,17",
                "delete" => "M 3,5 L 17,5 M 7,5 L 7,2 13,2 13,5 M 5,5 L 6,18 14,18 15,5 M 8,8 L 8,15 M 12,8 L 12,15",
                "chevron-down" => "M 5,7.5 L 10,12.5 15,7.5",
                "chevron-up" => "M 5,12.5 L 10,7.5 15,12.5",
                "warning" => "M 10,2 L 19,18 1,18 Z M 10,7 L 10,12 M 10,15 L 10,15.1",
                "check" => "M 3,10 L 8,15 17,5",
                "application" => "M 2,3 L 18,3 18,17 2,17 Z M 2,7 L 18,7 M 5,5 L 5.1,5 M 8,5 L 8.1,5",
                _ => "M 10,2 A 3,3 0 0 1 13,5 L 13,10 A 3,3 0 0 1 7,10 L 7,5 A 3,3 0 0 1 10,2 M 4,9 L 4,10 A 6,6 0 0 0 16,10 L 16,9 M 10,16 L 10,19 M 7,19 L 13,19",
            };
            using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 20, Bounds.Height / 20)))
                context.DrawGeometry(null, pen, Geometry.Parse(path));
        }
    }
}
