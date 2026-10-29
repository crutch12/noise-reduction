using System;
using System.IO;

namespace NoiseReduction.Avalonia
{
    // запуск при входе: LaunchAgent на macOS, .desktop в autostart на Linux
    internal static class Autostart
    {
        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static string File_ => OperatingSystem.IsMacOS()
            ? Path.Combine(Home, "Library", "LaunchAgents", "com.github.crutch12.noisereduction.plist")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Home, ".config"), "autostart", "noise-reduction.desktop");

        internal static bool Enabled => File.Exists(File_);

        internal static void Set(bool enabled)
        {
            if (!enabled) { File.Delete(File_); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(File_));
            string executable = Environment.ProcessPath;
            File.WriteAllText(File_, OperatingSystem.IsMacOS()
                ? $"""
                  <?xml version="1.0" encoding="UTF-8"?>
                  <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                  <plist version="1.0">
                  <dict>
                    <key>Label</key><string>com.github.crutch12.noisereduction</string>
                    <key>ProgramArguments</key><array><string>{Escape(executable)}</string><string>--tray</string></array>
                    <key>RunAtLoad</key><true/>
                    <key>ProcessType</key><string>Interactive</string>
                  </dict>
                  </plist>
                  """
                : $"""
                  [Desktop Entry]
                  Type=Application
                  Name=Noise Reduction
                  Exec="{executable}" --tray
                  X-GNOME-Autostart-enabled=true
                  """);
        }

        private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
