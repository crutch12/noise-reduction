using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace NoiseReduction.Avalonia
{
    internal sealed record WindowGeometry(int X, int Y, double Width, double Height, bool Maximized);

    internal static class WindowLayout
    {
        private static string FileName => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } value ? value : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "noise-reduction", "window.json");

        internal static void Restore(Window window)
        {
            WindowGeometry saved = null;
            try { if (File.Exists(FileName)) saved = JsonSerializer.Deserialize(File.ReadAllText(FileName), WindowLayoutJsonContext.Default.WindowGeometry); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
            if (saved != null && double.IsFinite(saved.Width) && double.IsFinite(saved.Height))
            {
                window.Width = Math.Clamp(saved.Width, 440, 1600);
                window.Height = Math.Clamp(saved.Height, 400, 1600);
                window.Position = new PixelPoint(saved.X, saved.Y);
            }
            Fit(window, saved == null);
            if (saved?.Maximized == true) window.WindowState = WindowState.Maximized;
        }

        internal static void Save(Window window)
        {
            if (window.WindowState == WindowState.Minimized) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FileName));
                if (window.WindowState == WindowState.Maximized && File.Exists(FileName))
                {
                    var saved = JsonSerializer.Deserialize(File.ReadAllText(FileName), WindowLayoutJsonContext.Default.WindowGeometry);
                    if (saved != null) { File.WriteAllText(FileName, JsonSerializer.Serialize(saved with { Maximized = true }, WindowLayoutJsonContext.Default.WindowGeometry)); return; }
                }
                var geometry = new WindowGeometry(window.Position.X, window.Position.Y, window.Width, window.Height, window.WindowState == WindowState.Maximized);
                File.WriteAllText(FileName + ".tmp", JsonSerializer.Serialize(geometry, WindowLayoutJsonContext.Default.WindowGeometry));
                File.Move(FileName + ".tmp", FileName, true);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }

        internal static void Fit(Window window, bool center = false)
        {
            var screen = window.Screens.ScreenFromPoint(window.Position) ?? window.Screens.Primary;
            if (screen == null) return;
            double scale = screen.Scaling;
            var area = screen.WorkingArea;
            double frameWidth = Math.Max(0, (window.FrameSize?.Width ?? window.ClientSize.Width) - window.ClientSize.Width);
            double frameHeight = Math.Max(0, (window.FrameSize?.Height ?? window.ClientSize.Height) - window.ClientSize.Height);
            double availableWidth = Math.Max(0, area.Width / scale - frameWidth), availableHeight = Math.Max(0, area.Height / scale - frameHeight);
            window.MinWidth = Math.Min(440, availableWidth);
            window.MinHeight = Math.Min(400, availableHeight);
            window.Width = Math.Min(window.Width, availableWidth);
            window.Height = Math.Min(window.Height, availableHeight);
            int width = (int)Math.Ceiling((window.Width + frameWidth) * scale), height = (int)Math.Ceiling((window.Height + frameHeight) * scale);
            int x = center ? area.X + (area.Width - width) / 2 : window.Position.X;
            int y = center ? area.Y + (area.Height - height) / 2 : window.Position.Y;
            window.Position = new PixelPoint(Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width)), Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height)));
        }

        internal static void PlaceTray(Window panel, Window owner)
        {
            var screen = owner.Screens.ScreenFromWindow(owner) ?? panel.Screens.Primary;
            if (screen == null) return;
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            panel.Width = Math.Min(360, area.Width / scale - 16);
            panel.MaxHeight = Math.Max(100, area.Height / scale - 16);
            panel.Height = Math.Min(panel.Height, panel.MaxHeight);
            bool top = area.Y > screen.Bounds.Y;
            panel.Position = new PixelPoint(area.Right - (int)((panel.Width + 8) * scale), top ? area.Y + (int)(8 * scale) : area.Bottom - (int)((panel.Height + 8) * scale));
        }
    }
}
