using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NoiseReduction.Avalonia
{
    internal static class LinuxProcess
    {
        internal static int ReplaceWithShell(string script)
        {
            execv("/bin/sh", new[] { "sh", script, null });
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int execv(string path, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] arguments);
    }
}
