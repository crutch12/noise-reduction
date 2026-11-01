using System.Diagnostics;
using System.Reflection;

namespace NoiseReduction.Linux.Tests
{
    internal static class ProcessTests
    {
        [Test]
        internal static void UninstallShellReplacesManagedProcessAndPreservesExitStatus()
        {
            if (!OperatingSystem.IsLinux()) return;
            string directory = Path.Combine(Path.GetTempPath(), "nr uninstall " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string script = Path.Combine(directory, "uninstall.sh");
                string pid = Path.Combine(directory, "pid");
                File.WriteAllText(script, "printf '%s' \"$$\" > \"$(dirname \"$0\")/pid\"\nexit 23\n");
                var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--shell-fixture"); start.ArgumentList.Add(script);
                using var process = Process.Start(start);
                Assert.True(process.WaitForExit(10000), "Uninstall must terminate instead of waiting for the managed process");
                Assert.Equal(23, process.ExitCode);
                Assert.Equal(process.Id.ToString(), File.ReadAllText(pid));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
