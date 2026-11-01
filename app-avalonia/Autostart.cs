using System;
using System.IO;
using System.Linq;

namespace NoiseReduction.Avalonia
{
    internal static class Autostart
    {
        private const string Owner = "X-NoiseReduction-Owner=noise-reduction-user-install-v1";
        internal static string Entry(string executable)
        {
            if (executable.IndexOfAny(new[] { '\n', '\r', '=' }) >= 0) throw new ArgumentException("The executable path cannot be used in a desktop entry");
            string quoted = executable.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
            quoted = quoted.Replace("\\", "\\\\").Replace("%", "%%");
            return $"[Desktop Entry]\nType=Application\nName=Noise Reduction\nExec=\"{quoted}\" --tray\nX-GNOME-Autostart-enabled=true\n{Owner}\n";
        }
        private static string File_ => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "autostart", "noise-reduction.desktop");

        private static string Executable
        {
            get
            {
                string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
                string launcher = Path.Combine(bin, "noise-reduction");
                return File.Exists(Path.Combine(AppContext.BaseDirectory, ".installed")) && File.Exists(launcher) ? launcher : Environment.ProcessPath;
            }
        }

        private static bool Owned(string path)
        {
            try
            {
                if (new FileInfo(path).LinkTarget != null) return false;
                string text = File.ReadAllText(path);
                return text.Split('\n').Contains(Owner) || text.Trim() == $"[Desktop Entry]\nType=Application\nName=Noise Reduction\nExec=\"{Environment.ProcessPath}\" --tray\nX-GNOME-Autostart-enabled=true";
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static bool Enabled => File.Exists(File_) && Owned(File_);

        internal static void Set(bool enabled)
        {
            if (new FileInfo(File_).LinkTarget != null || File.Exists(File_) && !Owned(File_)) throw new InvalidOperationException("Another application owns this autostart entry");
            if (!enabled) { File.Delete(File_); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(File_));
            string temporary = File_ + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(Entry(Executable));
                    stream.Write(bytes); stream.Flush(true);
                }
                File.Move(temporary, File_, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
