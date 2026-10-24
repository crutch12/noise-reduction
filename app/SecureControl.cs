using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NoiseReduction.Desktop
{
    internal sealed class SecureControl : IDisposable
    {
        private SafeFileHandle directory;
        internal FileStream Stream;
        [StructLayout(LayoutKind.Sequential)]
        private struct Information
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle handle);

        internal static SecureControl Open(string root, string guid, bool readOnly)
        {
            var result = new SecureControl();
            SafeFileHandle file = null;
            try
            {
                string expectedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string path = Path.Combine(expectedRoot, new Guid(guid).ToString("D") + ".bin");
                result.directory = CreateFile(expectedRoot, 0x80, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                Check(result.directory);
                Validate(result.directory, expectedRoot, true);
                bool created = false;
                if (!readOnly)
                {
                    file = CreateFile(path, 0xc0000000, 3, IntPtr.Zero, 1, 0x00200000, IntPtr.Zero);
                    if (file.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        file.Dispose(); file = null;
                        if (error != 80 && error != 183) throw new Win32Exception(error);
                    }
                    else created = true;
                }
                if (file == null) file = CreateFile(path, readOnly ? 0x80000000 : 0xc0000000, 3, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                Check(file);
                Validate(file, path, false);
                result.Stream = new FileStream(file, readOnly ? FileAccess.Read : FileAccess.ReadWrite);
                file = null;
                if (created)
                {
                    if (result.Stream.Length != 0) throw new IOException("Новый control-файл уже содержит данные");
                    var bytes = new byte[64];
                    Array.Copy(BitConverter.GetBytes(0x3143524eu), bytes, 4);
                    Array.Copy(BitConverter.GetBytes(64u), 0, bytes, 4, 4);
                    result.Stream.Write(bytes, 0, bytes.Length);
                    result.Stream.Flush(true);
                }
                if (result.Stream.Length != 64) throw new IOException("Некорректная длина control-файла, ожидается 64 байта");
                result.Stream.Position = 0;
                var header = new byte[8];
                if (result.Stream.Read(header, 0, 8) != 8 || BitConverter.ToUInt32(header, 0) != 0x3143524e || BitConverter.ToUInt32(header, 4) != 64)
                    throw new IOException("Некорректный заголовок control-файла");
                result.Stream.Position = 0;
                return result;
            }
            catch { if (file != null) file.Dispose(); result.Dispose(); throw; }
        }
        private static void Check(SafeFileHandle handle)
        { if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        private static void Validate(SafeFileHandle handle, string expected, bool isDirectory)
        {
            Information info;
            if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (GetFileType(handle) != 1 || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != isDirectory || (!isDirectory && info.Links != 1))
                throw new IOException("Control-путь содержит ссылку или неподдерживаемый объект");
            var path = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
            if (length == 0 || length >= path.Capacity) throw new IOException("Не удалось проверить окончательный путь control-файла");
            string actual = path.ToString();
            if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual.Substring(8);
            else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual.Substring(4);
            if (!String.Equals(actual.TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Окончательный control-путь отличается от ожидаемого");
        }
        public void Dispose()
        {
            if (Stream != null) { Stream.Dispose(); Stream = null; }
            if (directory != null) { directory.Dispose(); directory = null; }
        }
    }
}
