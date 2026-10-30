using System;
using System.IO;

namespace NoiseReduction.Avalonia
{
    // запуск при входе: .desktop в папке autostart
    internal static class Autostart
    {
        private static string File_ => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "autostart", "noise-reduction.desktop");

        internal static bool Enabled => File.Exists(File_);

        internal static void Set(bool enabled)
        {
            if (!enabled) { File.Delete(File_); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(File_));
            File.WriteAllText(File_, $"""
                [Desktop Entry]
                Type=Application
                Name=Noise Reduction
                Exec="{Environment.ProcessPath}" --tray
                X-GNOME-Autostart-enabled=true
                """);
        }
    }
}
