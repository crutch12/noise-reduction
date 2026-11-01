using NoiseReduction.Avalonia;
using System.Diagnostics;
using System.Reflection;

namespace NoiseReduction.Linux.Tests
{
    internal static class SingleInstanceTests
    {
        [Test]
        internal static async Task InstanceReplyIsDeliveredBeforeAnExitCallbackReleasesTheOwner()
        {
            string identity = "quit-reply-" + Guid.NewGuid().ToString("N");
            using var owner = new SingleInstance(identity);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Start(_ => Task.FromResult("{\"ok\":true}"), () => { owner.Dispose(); exited.TrySetResult(); });
            Assert.Equal("{\"ok\":true}", await SingleInstance.SendAsync(new[] { "quit" }, identity, 2000));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(1));
            using var replacement = new SingleInstance(identity);
            Assert.True(replacement.IsOwner);
        }

        [Test]
        internal static void InstanceExplicitAudioEndpointSharesOwnershipAcrossRuntimeDirectories()
        {
            string previousRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            string previousServer = Environment.GetEnvironmentVariable("PULSE_SERVER");
            string directory = Path.Combine(Path.GetTempPath(), "nr-runtime-identity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "one"));
            Directory.CreateDirectory(Path.Combine(directory, "two"));
            try
            {
                Environment.SetEnvironmentVariable("PULSE_SERVER", "unix:" + Path.Combine(directory, "pulse", "native"));
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Combine(directory, "one"));
                using var first = new SingleInstance();
                Assert.True(first.IsOwner);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Combine(directory, "two"));
                using var second = new SingleInstance();
                Assert.True(!second.IsOwner, "One audio server must have one owner even when a CLI uses a different runtime directory");
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime);
                Environment.SetEnvironmentVariable("PULSE_SERVER", previousServer);
                Directory.Delete(directory, true);
            }
        }

        [Test]
        internal static void InstanceDefaultAndExplicitUnixSocketShareOwnership()
        {
            string previousServer = Environment.GetEnvironmentVariable("PULSE_SERVER");
            string previousRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            string runtime = Path.Combine(Path.GetTempPath(), "nr-instance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runtime);
            try
            {
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
                Environment.SetEnvironmentVariable("PULSE_SERVER", null);
                using var owner = new SingleInstance();
                Assert.True(owner.IsOwner);
                string socket = Path.Combine(runtime, "pulse", "native");
                Environment.SetEnvironmentVariable("PULSE_SERVER", "unix:" + socket);
                using var explicitUnix = new SingleInstance();
                Assert.True(!explicitUnix.IsOwner, "Explicit Unix endpoint must share the default server owner");
                Environment.SetEnvironmentVariable("PULSE_SERVER", socket);
                using var bareUnix = new SingleInstance();
                Assert.True(!bareUnix.IsOwner, "A bare Unix path must share the same owner");
                Environment.SetEnvironmentVariable("PULSE_SERVER", "unix:" + socket + "-other");
                using var other = new SingleInstance();
                Assert.True(other.IsOwner, "A different audio server must remain independent");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PULSE_SERVER", previousServer);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime);
                Directory.Delete(runtime, true);
            }
        }

        [Test]
        internal static async Task InstanceCrashedProcessReleasesItsLease()
        {
            string identity = "crash-test-" + Guid.NewGuid().ToString("N");
            var children = new List<Process>();
            try
            {
                Process first = StartFixture(identity); children.Add(first);
                Assert.Equal("READY", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                Process blocked = StartFixture(identity); children.Add(blocked);
                Assert.Equal("BUSY", await blocked.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                await blocked.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(2, blocked.ExitCode);
                first.Kill();
                await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Process replacement = StartFixture(identity); children.Add(replacement);
                Assert.Equal("READY", await replacement.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                foreach (var child in children)
                {
                    if (!child.HasExited) child.Kill();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    child.Dispose();
                }
            }
        }

        private static Process StartFixture(string identity)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test runner executable");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            info.ArgumentList.Add("--lease-fixture"); info.ArgumentList.Add(identity);
            return Process.Start(info) ?? throw new InvalidOperationException("Could not start ownership fixture");
        }

        [Test]
        internal static void InstanceSecondOwnerIsRejectedAndLeaseCanBeReacquired()
        {
            string identity = "lease-test-" + Guid.NewGuid().ToString("N");
            using (var first = new SingleInstance(identity))
            {
                Assert.True(first.IsOwner);
                using var second = new SingleInstance(identity);
                Assert.True(!second.IsOwner, "Second process owner must not control the same audio graph");
            }
            using var replacement = new SingleInstance(identity);
            Assert.True(replacement.IsOwner, "Released ownership must be available to a replacement");
        }

        [Test]
        internal static async Task InstanceRequestsReachTheOwnerAndReturnItsResponse()
        {
            string identity = "request-test-" + Guid.NewGuid().ToString("N");
            using var owner = new SingleInstance(identity);
            string received = null;
            owner.Start(request => { received = request; return Task.FromResult("{\"ok\":true,\"enabled\":false}"); });
            string response = await SingleInstance.SendAsync(new[] { "off" }, identity, 2000);
            Assert.True(received.Contains("off"), "Owner must receive the requested command");
            Assert.Equal("{\"ok\":true,\"enabled\":false}", response);
        }

        [Test]
        internal static async Task InstanceMissingOwnerHasABoundedConnectionTimeout()
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            bool timedOut = false;
            try { await SingleInstance.SendAsync(new[] { "status" }, "missing-" + Guid.NewGuid().ToString("N"), 150); }
            catch (TimeoutException) { timedOut = true; }
            Assert.True(timedOut, "Missing owner must return a timeout instead of hanging");
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
        }
    }
}
