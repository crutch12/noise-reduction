using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace NoiseReduction.Avalonia
{
    internal static class AppIcons
    {
        private static readonly Dictionary<string, Bitmap> cache = new(StringComparer.Ordinal);
        internal static Control Create(RecordingApp app, double size = 18, bool? dark = null)
        {
            Bitmap bitmap = null;
            try
            {
                string path = Resolve(app);
                if (path != null && !cache.TryGetValue(path, out bitmap)) { bitmap = Load(path); if (bitmap != null) cache[path] = bitmap; }
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { }
            Control control = bitmap != null
                ? new Image { Source = bitmap, Width = size, Height = size }
                : new UiIcon("application") { Width = size, Height = size };
            AppTheme.Describe(control, app.Name, app.Name);
            return control;
        }
        internal static void Update(StackPanel panel, IReadOnlyList<RecordingApp> applications, bool? dark = null)
        {
            string signature = (dark ?? AppTheme.IsDark) + "|" + string.Join("|", applications.Select(app => app.Id + ":" + app.Icon + ":" + app.Name));
            if ((string)panel.Tag == signature) return;
            panel.Tag = signature;
            panel.Children.Clear();
            var unique = applications.GroupBy(item => item.Id).Select(group => group.First()).ToArray();
            foreach (var app in unique.Take(5)) panel.Children.Add(Create(app, 15, dark));
            if (unique.Length > 5)
            {
                var remaining = AppTheme.Caption("+" + (unique.Length - 5));
                remaining.Foreground = AppTheme.Brush((dark ?? AppTheme.IsDark) ? "#E3E7ED" : "#1F252D");
                panel.Children.Add(remaining);
            }
        }
        private static string Resolve(RecordingApp app)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
            var roots = new[] { data }.Concat((Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share").Split(':', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            string id = app.Id.StartsWith("binary:", StringComparison.Ordinal) ? app.Id[7..] : app.Id;
            var names = new List<string> { app.Icon, id };
            foreach (string entry in new[] { app.Icon, id }.Where(SafeName).Distinct())
            {
                foreach (string root in roots)
                {
                    string desktop = Path.Combine(root, "applications", entry.EndsWith(".desktop", StringComparison.Ordinal) ? entry : entry + ".desktop");
                    if (!File.Exists(desktop)) continue;
                    try
                    {
                        bool main = false;
                        foreach (string line in File.ReadLines(desktop))
                        {
                            if (line.StartsWith('[')) main = line == "[Desktop Entry]";
                            if (main && line.StartsWith("Icon=", StringComparison.Ordinal)) { names.Insert(0, line[5..].Trim()); break; }
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    break;
                }
            }
            foreach (string icon in names.Where(name => !string.IsNullOrEmpty(name)).Distinct())
            {
                if (Path.IsPathRooted(icon)) { if (File.Exists(icon)) return icon; continue; }
                if (!SafeName(icon)) continue;
                foreach (string root in roots)
                {
                    string icons = Path.Combine(root, "icons");
                    var themes = Directory.Exists(icons) ? Directory.EnumerateDirectories(icons).OrderBy(path => Path.GetFileName(path) == "hicolor" ? 0 : 1) : Enumerable.Empty<string>();
                    foreach (string theme in themes)
                        foreach (string size in new[] { "24x24", "32x32", "48x48", "64x64", "16x16", "22x22", "128x128", "256x256", "scalable" })
                            foreach (string context in new[] { "apps", "" })
                            {
                                string found = IconFile(Path.Combine(theme, size, context), icon);
                                if (found != null) return found;
                            }
                    string pixmap = IconFile(Path.Combine(root, "pixmaps"), icon);
                    if (pixmap != null) return pixmap;
                }
            }
            return null;
        }
        private static bool SafeName(string value) => !string.IsNullOrEmpty(value) && !value.Contains('/') && !value.Contains('\\') && !value.Contains("..");
        private static string IconFile(string directory, string icon)
        {
            foreach (string extension in Path.HasExtension(icon) && new[] { ".png", ".svg", ".xpm" }.Contains(Path.GetExtension(icon)) ? new[] { "" } : new[] { ".png", ".svg", ".xpm" })
            {
                string path = Path.Combine(directory, icon + extension);
                if (File.Exists(path)) return path;
            }
            return null;
        }
        private static Bitmap Load(string path)
        {
            if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".xpm", StringComparison.OrdinalIgnoreCase)) return new Bitmap(path);
            try
            {
                IntPtr image = gdk_pixbuf_new_from_file_at_scale(path, 48, 48, 1, IntPtr.Zero);
                if (image == IntPtr.Zero) return null;
                try
                {
                    if (gdk_pixbuf_save_to_buffer(image, out IntPtr buffer, out UIntPtr length, "png", IntPtr.Zero, IntPtr.Zero) == 0) return null;
                    try
                    {
                        var bytes = new byte[checked((int)length.ToUInt64())];
                        Marshal.Copy(buffer, bytes, 0, bytes.Length);
                        using var stream = new MemoryStream(bytes);
                        return new Bitmap(stream);
                    }
                    finally { g_free(buffer); }
                }
                finally { g_object_unref(image); }
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }
        [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern IntPtr gdk_pixbuf_new_from_file_at_scale([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int width, int height, int preserve, IntPtr error);
        [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern int gdk_pixbuf_save_to_buffer(IntPtr image, out IntPtr buffer, out UIntPtr length, [MarshalAs(UnmanagedType.LPUTF8Str)] string type, IntPtr error, IntPtr options);
        [DllImport("libgobject-2.0.so.0")] private static extern void g_object_unref(IntPtr image);
        [DllImport("libglib-2.0.so.0")] private static extern void g_free(IntPtr buffer);
    }
}
