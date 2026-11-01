using System;
using System.IO;
using NoiseReduction.Avalonia;

namespace NoiseReduction.Linux.Tests
{
    internal static class AutostartTests
    {
        [Test]
        internal static void AutostartQuotesExecutableAndEscapesDesktopFieldCodes()
        {
            string entry = Autostart.Entry("/opt/voice \"test\"/100%/NoiseReduction");
            Assert.True(entry.Contains("Exec=\"/opt/voice \\\\\"test\\\\\"/100%%/NoiseReduction\" --tray"));
            Assert.True(entry.Contains("X-NoiseReduction-Owner=noise-reduction-user-install-v1"));
        }

        [Test]
        internal static void AutostartDoesNotDeleteAnotherApplicationsEntry()
        {
            WithConfiguration(directory =>
            {
                string path = Path.Combine(directory, "autostart", "noise-reduction.desktop");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                const string other = "[Desktop Entry]\nName=Other App\nExec=/opt/other\n";
                File.WriteAllText(path, other);
                Assert.True(!Autostart.Enabled);
                bool refused = false;
                try { Autostart.Set(false); }
                catch (InvalidOperationException) { refused = true; }
                Assert.True(refused && File.Exists(path));
                Assert.Equal(other, File.ReadAllText(path));
            });
        }

        [Test]
        internal static void AutostartOwnEntryCanBeEnabledAndDisabled()
        {
            WithConfiguration(directory =>
            {
                Autostart.Set(true);
                Assert.True(Autostart.Enabled);
                string path = Path.Combine(directory, "autostart", "noise-reduction.desktop");
                Assert.True(File.ReadAllText(path).Contains("--tray"));
                Autostart.Set(false);
                Assert.True(!Autostart.Enabled && !File.Exists(path));
            });
        }

        private static void WithConfiguration(Action<string> action)
        {
            string previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string directory = Path.Combine(Path.GetTempPath(), "nr-startup-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
                action(directory);
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
