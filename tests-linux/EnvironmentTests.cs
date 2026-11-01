using System.Text;
using System.Runtime.Versioning;
using NoiseReduction.Avalonia;

namespace NoiseReduction.Linux.Tests
{
    [SupportedOSPlatform("linux")]
    internal static class EnvironmentTests
    {
        [Test]
        internal static void EnvironmentLeaseRefusesExistingSymlinkDirectory()
        {
            if (!OperatingSystem.IsLinux()) return;
            WithDirectory("XDG_STATE_HOME", directory =>
            {
                string target = Path.Combine(directory, "foreign");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "keep"), "foreign");
                string link = Path.Combine(directory, "noise-reduction-" + Environment.UserName);
                Directory.CreateSymbolicLink(link, target);
                bool refused = false;
                try { using var lease = new SingleInstance("symlink-" + Guid.NewGuid().ToString("N")); }
                catch (IOException) { refused = true; }
                Assert.True(refused, "A symlink must not become the application lease directory");
                Assert.Equal("foreign", File.ReadAllText(Path.Combine(target, "keep")));
            });
        }

        [Test]
        internal static void EnvironmentLeaseRefusesWorldWritableExistingDirectory()
        {
            if (!OperatingSystem.IsLinux()) return;
            WithDirectory("XDG_STATE_HOME", directory =>
            {
                string leaseDirectory = Path.Combine(directory, "noise-reduction-" + Environment.UserName);
                Directory.CreateDirectory(leaseDirectory);
                var permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(leaseDirectory, permissions);
                bool refused = false;
                try { using var lease = new SingleInstance("writable-" + Guid.NewGuid().ToString("N")); }
                catch (IOException) { refused = true; }
                Assert.True(refused, "Other users must not be able to replace an active lease file");
                Assert.Equal(permissions, File.GetUnixFileMode(leaseDirectory));
            });
        }

        [Test]
        internal static async Task EnvironmentIpcRejectsOversizedInputBeforeReadingItAll()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 2 * 1024 * 1024)));
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            bool refused = false;
            try { await SingleInstance.ReadRequestAsync(reader, CancellationToken.None); }
            catch (InvalidDataException) { refused = true; }
            Assert.True(refused, "IPC must reject a request longer than 65536 characters");
            Assert.True(stream.Position <= 96 * 1024, "IPC must stop reading an oversized unterminated request at its size limit");
        }

        [Test]
        internal static async Task EnvironmentIpcAcceptsARequestAtTheSizeLimit()
        {
            string request = new('x', 65536);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request + "\n"));
            using var reader = new StreamReader(stream);
            Assert.Equal(request, await SingleInstance.ReadRequestAsync(reader, CancellationToken.None));
        }

        [Test]
        internal static void EnvironmentUnreadableForeignAutostartDoesNotPreventStartup()
        {
            if (!OperatingSystem.IsLinux()) return;
            WithDirectory("XDG_CONFIG_HOME", directory =>
            {
                string path = Path.Combine(directory, "autostart", "noise-reduction.desktop");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "[Desktop Entry]\nName=Foreign\nExec=/opt/foreign\n");
                File.SetUnixFileMode(path, UnixFileMode.None);
                try
                {
                    bool readable = true;
                    try { File.ReadAllText(path); }
                    catch (UnauthorizedAccessException) { readable = false; }
                    if (readable) { Console.WriteLine("SKIP unreadable autostart fixture: this user can read mode 000 files"); return; }
                    Assert.True(!Autostart.Enabled, "Unreadable foreign startup entries must fail closed");
                    Assert.True(File.Exists(path), "Inspecting startup state must preserve the foreign entry");
                }
                finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            });
        }

        private static void WithDirectory(string variable, Action<string> action)
        {
            string previous = Environment.GetEnvironmentVariable(variable);
            string directory = Path.Combine(Path.GetTempPath(), "nr-environment-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Environment.SetEnvironmentVariable(variable, directory);
                action(directory);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
                Directory.Delete(directory, true);
            }
        }
    }
}
