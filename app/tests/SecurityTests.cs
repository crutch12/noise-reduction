using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using NoiseReduction.Desktop;

internal static class SecurityTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLink(string link, string target, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateSymbolicLink(string link, string target, uint flags);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Rejected(Action action, string message)
    {
        try { action(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (Win32Exception) { return; }
        throw new Exception(message);
    }
    private static void Open(string root, string guid, bool readOnly)
    { using (var control = new ControlFile(root, guid, readOnly)) { } }
    private static bool Same(byte[] first, byte[] second)
    { if (first.Length != second.Length) return false; for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false; return true; }
    private static bool Link(string link, string target, bool directory)
    {
        if (CreateSymbolicLink(link, target, (directory ? 1u : 0u) | 2u)) return true;
        if (CreateSymbolicLink(link, target, directory ? 1u : 0u)) return true;
        int error = Marshal.GetLastWin32Error();
        if (error == 1314 || error == 5) { Console.WriteLine("SKIP: symbolic link privilege unavailable, error " + error); return false; }
        throw new Win32Exception(error);
    }
    internal static void Run(string fixture)
    {
        string root = Path.Combine(fixture, "secure-controls");
        Directory.CreateDirectory(root);
        string guid = "c2345678-1234-1234-1234-123456789abc";
        string path = Path.Combine(root, guid + ".bin");
        var original = new byte[] { 17, 18, 19, 20, 21 };
        File.WriteAllBytes(path, original);
        Rejected(delegate { Open(root, guid, true); }, "Readonly accepted malformed length");
        Rejected(delegate { Open(root, guid, false); }, "Writable open accepted malformed length");
        Check(Same(original, File.ReadAllBytes(path)), "Malformed control was truncated or rewritten");
        File.Delete(path);
        Rejected(delegate { Open(root, guid, true); }, "Readonly created missing control");
        Check(!File.Exists(path), "Readonly created a file");
        var malformedHeader = new byte[64];
        File.WriteAllBytes(path, malformedHeader);
        Rejected(delegate { Open(root, guid, true); }, "Readonly accepted malformed magic");
        Rejected(delegate { Open(root, guid, false); }, "Writable open repaired existing malformed magic");
        Check(Same(malformedHeader, File.ReadAllBytes(path)), "Existing header was rewritten");
        File.Delete(path);
        using (var control = new ControlFile(root, guid))
        {
            Rejected(delegate { Directory.Move(root, root + "-moved"); }, "Control parent can be renamed while mapped");
            Rejected(delegate { File.Delete(path); }, "Mapped control can be unlinked");
        }
        byte[] valid = File.ReadAllBytes(path);
        File.Delete(path);
        string victim = Path.Combine(fixture, "unrelated-victim.bin");
        File.WriteAllBytes(victim, valid);
        if (!CreateHardLink(path, victim, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Rejected(delegate { Open(root, guid, false); }, "Writable open followed hard link");
        Rejected(delegate { Open(root, guid, true); }, "Readonly open accepted hard link");
        Check(Same(valid, File.ReadAllBytes(victim)), "Hardlink victim was modified");
        File.Delete(path);
        if (Link(path, victim, false))
        {
            Rejected(delegate { Open(root, guid, false); }, "Writable open followed file symlink");
            Rejected(delegate { Open(root, guid, true); }, "Readonly open followed file symlink");
            Check(Same(valid, File.ReadAllBytes(victim)), "Symlink victim was modified");
            File.Delete(path);
        }
        File.WriteAllBytes(path, valid);
        string alias = Path.Combine(fixture, "root-alias");
        if (Link(alias, root, true))
        {
            try
            {
                Rejected(delegate { Open(alias, guid, false); }, "Control root symlink accepted");
                Rejected(delegate { Open(alias, guid, true); }, "Readonly control root symlink accepted");
                string child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
                File.WriteAllBytes(Path.Combine(child, guid + ".bin"), valid);
                Rejected(delegate { Open(Path.Combine(alias, "child"), guid, false); }, "Ancestor symlink final path mismatch accepted");
                Check(Same(valid, File.ReadAllBytes(path)), "Directory alias changed target data");
            }
            finally { Directory.Delete(alias); }
        }
        Console.WriteLine("PASS: secure control malformed-file preservation, readonly no-create, real hardlink/file symlink/directory symlink rejection, handle locks");
    }
}
