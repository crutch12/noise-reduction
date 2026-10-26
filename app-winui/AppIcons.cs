using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NoiseReduction.WinUI
{
    // Иконки программ, которые сейчас слушают микрофон, название видно при наведении
    internal static class AppIcons
    {
        private static readonly Brush Transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        private static readonly Dictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);

        internal static StackPanel Create() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Margin = new Thickness(-2, -2, 0, -2), VerticalAlignment = VerticalAlignment.Center };

        internal static void Show(StackPanel panel, IReadOnlyList<AppUse> apps, Brush foreground)
        {
            // перерисовываем только при смене списка, иначе подсказка мигает на каждом обновлении
            string signature = apps == null ? "" : string.Join("|", apps.Select(app => app.Name + "*" + app.Path));
            if (panel.Tag as string == signature) return;
            panel.Tag = signature;
            panel.Children.Clear();
            if (apps == null) return;
            foreach (var app in apps)
            {
                var image = Icon(app.Path);
                // без иконки у файла рисуем стандартный значок приложения
                FrameworkElement item = image == null
                    ? new FontIcon { Glyph = "", FontSize = 14, Foreground = foreground }
                    : new Image { Source = image, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
                // подложка при наведении, как у мелких кнопок Fluent
                var chip = new Border { Child = item, Padding = new Thickness(2), CornerRadius = new CornerRadius(4), Background = Transparent };
                // цвет SubtleFillColorSecondary по теме самого элемента, у трея она своя
                chip.PointerEntered += (_, _) => chip.Background = new SolidColorBrush(chip.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x0A, 0, 0, 0));
                chip.PointerExited += (_, _) => chip.Background = Transparent;
                ToolTipService.SetToolTip(chip, app.Name);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, app.Name);
                panel.Children.Add(chip);
            }
        }

        private static ImageSource Icon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (cache.TryGetValue(path, out var image)) return image;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                using var bitmap = icon.ToBitmap();
                // поток не закрываем, картинка читает его уже после возврата
                var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;
                var source = new BitmapImage();
                source.SetSource(stream.AsRandomAccessStream());
                image = source;
            }
            catch (Exception) { image = null; }
            return cache[path] = image;
        }
    }
}
