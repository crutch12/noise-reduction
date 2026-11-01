using System.Diagnostics;
using NoiseReduction.Avalonia;

namespace NoiseReduction.Linux.Tests
{
    internal static class ShortcutTests
    {
        [Test]
        internal static async Task ShortcutReceivesX11KeyAndReleasesGrab()
        {
            var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var shortcut = new GlobalShortcut(() => pressed.TrySetResult()))
            {
                Assert.True(shortcut.Available, "Ctrl+Alt+N must be registered on X11");
                using var conflict = new GlobalShortcut(() => { });
                Assert.True(!conflict.Available, "A conflicting grab must not claim availability");
                using var key = Process.Start(new ProcessStartInfo("xdotool") { ArgumentList = { "key", "--clearmodifiers", "ctrl+alt+n" }, UseShellExecute = false });
                await key.WaitForExitAsync();
                await pressed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            using var replacement = new GlobalShortcut(() => { });
            Assert.True(replacement.Available, "Disposing the shortcut must release its grab");
        }

        [Test]
        internal static void ShortcutDoesNotClaimXWaylandGrabAsGlobal()
        {
            string previous = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            try
            {
                Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
                using var shortcut = new GlobalShortcut(() => { });
                Assert.True(!shortcut.Available);
            }
            finally { Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", previous); }
        }
    }
}
