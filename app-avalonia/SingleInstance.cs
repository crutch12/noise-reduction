using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NoiseReduction.Avalonia
{
    internal sealed class SingleInstance : IDisposable
    {
        private readonly FileStream lease;
        private readonly string pipeName;
        private readonly CancellationTokenSource stopping = new();
        private NamedPipeServerStream server;
        private Task listener;

        internal SingleInstance(string identity = null)
        {
            pipeName = Name(identity);
            string runtime = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrEmpty(runtime))
            {
                runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
                Directory.CreateDirectory(runtime);
            }
            Directory.CreateDirectory(runtime);
            if (OperatingSystem.IsLinux() && !SafeDirectory(runtime, false)) throw new IOException("The state directory must belong to the current user and must not be writable by other users");
            string directory = Path.Combine(runtime, "noise-reduction-" + Environment.UserName);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (OperatingSystem.IsLinux() && !SafeDirectory(directory, true)) throw new IOException("The application lease directory must be private and must not be a symbolic link");
            string path = Path.Combine(directory, pipeName + ".lock");
            if (new FileInfo(path).LinkTarget != null) throw new IOException("The application lock is a symbolic link");
            try { lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { }
        }

        internal bool IsOwner => lease != null;

        internal static async ValueTask<string> ReadRequestAsync(StreamReader reader, CancellationToken cancellation)
        {
            var value = new StringBuilder();
            var buffer = new char[1024];
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false);
                if (count == 0) return value.Length == 0 ? null : value.ToString();
                int newline = Array.IndexOf(buffer, '\n', 0, count);
                int length = newline < 0 ? count : newline;
                if (value.Length + length > 65536) throw new InvalidDataException("The command request is too large");
                value.Append(buffer, 0, length);
                if (newline >= 0) return value.ToString().TrimEnd('\r');
            }
        }

        internal void Start(Func<string, Task<string>> handler, Action replied = null)
        {
            if (!IsOwner) throw new InvalidOperationException("Another process owns the audio session");
            if (listener != null) throw new InvalidOperationException("The command listener is already running");
            listener = ListenAsync(handler, replied);
        }

        private async Task ListenAsync(Func<string, Task<string>> handler, Action replied)
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    using var connection = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    server = connection;
                    await connection.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using var reader = new StreamReader(connection, Encoding.UTF8, false, 1024, true);
                    using var writer = new StreamWriter(connection, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                    string request = await ReadRequestAsync(reader, timeout.Token).ConfigureAwait(false);
                    if (request == null) continue;
                    string response;
                    try { response = await handler(request).WaitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { response = JsonSerializer.Serialize(new { ok = false, error = error.Message }); }
                    await writer.WriteLineAsync(response.AsMemory(), timeout.Token).ConfigureAwait(false);
                    replied?.Invoke();
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
                finally { server = null; }
            }
        }

        internal static async Task<string> SendAsync(string[] args, string identity = null, int timeoutMilliseconds = 30000, bool missingAllowed = false)
        {
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            using var client = new NamedPipeClientStream(".", Name(identity), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                using (var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
                {
                    if (missingAllowed) connectionTimeout.CancelAfter(500);
                    try { await client.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (missingAllowed && !timeout.IsCancellationRequested) { return null; }
                    catch (IOException) when (missingAllowed) { return null; }
                }
                using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, true);
                await writer.WriteLineAsync(JsonSerializer.Serialize(args).AsMemory(), timeout.Token).ConfigureAwait(false);
                return await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("The application closed the command connection");
            }
            catch (OperationCanceledException) { throw new TimeoutException("Noise Reduction is not responding"); }
        }

        private static string Name(string identity)
        {
            if (identity == null)
            {
                string endpoint = Environment.GetEnvironmentVariable("PULSE_SERVER");
                if (string.IsNullOrEmpty(endpoint)) endpoint = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "", "pulse", "native");
                if (endpoint.StartsWith("unix:", StringComparison.Ordinal)) endpoint = endpoint[5..];
                if (Path.IsPathRooted(endpoint)) endpoint = Path.GetFullPath(endpoint);
                identity = Environment.UserName + "\n" + endpoint;
            }
            return "nr-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).Substring(0, 24).ToLowerInvariant();
        }

        private static bool SafeDirectory(string path, bool privateOnly) =>
            statx(-100, path, 0x100, 0x0b, out var info) == 0 && info.Owner == geteuid() &&
            (info.Mode & 0xf000) == 0x4000 && (info.Mode & (privateOnly ? 0x3f : 0x12)) == 0;

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct FileStatus
        {
            [FieldOffset(20)] internal uint Owner;
            [FieldOffset(28)] internal ushort Mode;
        }
        [DllImport("libc.so.6", SetLastError = true)] private static extern int statx(int directory, string path, int flags, uint mask, out FileStatus status);
        [DllImport("libc.so.6")] private static extern uint geteuid();

        public void Dispose()
        {
            stopping.Cancel();
            server?.Dispose();
            lease?.Dispose();
        }
    }
}
