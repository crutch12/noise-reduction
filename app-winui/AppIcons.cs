using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo { public IntPtr Icon; public int IconIndex; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName; }
        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo { public bool IsIcon; public int HotspotX, HotspotY; public IntPtr Mask, Color; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader { public int Type, Width, Height, WidthBytes; public short Planes, BitsPixel; public IntPtr Bits; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr handle, int size, out BitmapHeader header);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader header, uint usage);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);

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
            try { image = Load(path); } catch (Exception) { image = null; }
            return cache[path] = image;
        }

        // большая иконка файла через оболочку Windows, пиксели сразу в картинку WinUI
        private static ImageSource Load(string path)
        {
            var info = new FileInfo();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<FileInfo>(), 0x100) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                if (!GetIconInfo(info.Icon, out var parts)) return null;
                try
                {
                    if (parts.Color == IntPtr.Zero || GetObject(parts.Color, Marshal.SizeOf<BitmapHeader>(), out var bitmap) == 0) return null;
                    int width = bitmap.Width, height = bitmap.Height;
                    var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
                    var pixels = new byte[width * height * 4];
                    if (GetDIBits(dc, parts.Color, 0, (uint)height, pixels, ref header, 0) == 0) return null;
                    bool alpha = false;
                    for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { alpha = true; break; }
                    // у старых иконок без альфы прозрачность задаёт маска
                    byte[] mask = null;
                    if (!alpha)
                    {
                        mask = new byte[pixels.Length];
                        var maskHeader = header;
                        if (GetDIBits(dc, parts.Mask, 0, (uint)height, mask, ref maskHeader, 0) == 0) return null;
                    }
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        int a = alpha ? pixels[i + 3] : mask[i] == 0 ? 255 : 0;
                        // WinUI ждёт цвет, уже умноженный на альфу
                        pixels[i] = (byte)(pixels[i] * a / 255); pixels[i + 1] = (byte)(pixels[i + 1] * a / 255); pixels[i + 2] = (byte)(pixels[i + 2] * a / 255); pixels[i + 3] = (byte)a;
                    }
                    var result = new WriteableBitmap(width, height);
                    using (var stream = result.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
                    result.Invalidate();
                    return result;
                }
                finally { if (parts.Color != IntPtr.Zero) DeleteObject(parts.Color); if (parts.Mask != IntPtr.Zero) DeleteObject(parts.Mask); }
            }
            finally { ReleaseDC(IntPtr.Zero, dc); DestroyIcon(info.Icon); }
        }
    }
}
