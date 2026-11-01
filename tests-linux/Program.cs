using System.Reflection;

namespace NoiseReduction.Linux.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class TestAttribute : Attribute { }

    internal static class Assert
    {
        internal static void True(bool condition, string message = "Expected true")
        {
            if (!condition) throw new Exception(message);
        }

        internal static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"Expected {expected}, got {actual}");
        }
    }

    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--shell-fixture") return NoiseReduction.Avalonia.LinuxProcess.ReplaceWithShell(args[1]);
            if (args.Length == 2 && args[0] == "--lease-fixture")
            {
                using var lease = new NoiseReduction.Avalonia.SingleInstance(args[1]);
                Console.WriteLine(lease.IsOwner ? "READY" : "BUSY");
                Console.Out.Flush();
                if (lease.IsOwner) await Task.Delay(Timeout.InfiniteTimeSpan);
                return lease.IsOwner ? 0 : 2;
            }
            int passed = 0, failed = 0;
            string filter = Environment.GetEnvironmentVariable("NR_TEST_FILTER") ?? "";
            var methods = Assembly.GetExecutingAssembly().GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(method => method.IsDefined(typeof(TestAttribute), false) && (method.DeclaringType.Name + "." + method.Name).Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(method => method.Name);
            foreach (var method in methods)
            {
                try
                {
                    if (method.Invoke(null, null) is Task task) await task;
                    Console.WriteLine("PASS " + method.Name);
                    passed++;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine("FAIL " + method.Name + ": " + (error.InnerException ?? error));
                    failed++;
                }
            }
            Console.WriteLine($"{passed} passed, {failed} failed");
            return failed == 0 && passed > 0 ? 0 : 1;
        }
    }
}
