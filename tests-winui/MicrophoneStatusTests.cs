using System.Reflection;
using System.Runtime.Loader;

internal static class MicrophoneStatusTests
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        string binary = Path.GetFullPath(args[0]), directory = Path.GetDirectoryName(binary);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(binary);
        var sampleType = assembly.GetType("NoiseReduction.WinUI.DesktopController+Sample", true);
        var controlType = assembly.GetType("NoiseReduction.Desktop.ControlFile", true);
        string root = Path.GetFullPath(Path.Combine(".claude", "windows-status-tests", Guid.NewGuid().ToString("N")));
        string id = Guid.NewGuid().ToString("D"), path = Path.Combine(root, id + ".bin");
        Directory.CreateDirectory(root);
        var control = (IDisposable)Activator.CreateInstance(controlType, new object[] { root, id });
        var observe = sampleType.GetMethod("Observe", BindingFlags.Instance | BindingFlags.NonPublic);
        var failed = sampleType.GetField("Failed", BindingFlags.Instance | BindingFlags.NonPublic);
        object sample = null;
        int passed = 0, errors = 0;
        void Read(long frames, long models, long failures, long errorModels = 0)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                file.Position = 16;
                using var writer = new BinaryWriter(file);
                writer.Write(frames); writer.Write(models); writer.Write(failures);
                file.Position = 48;
                writer.Write(errorModels);
            }
            observe.Invoke(sample, new object[] { control });
        }
        void Expect(bool value)
        {
            if ((bool)failed.GetValue(sample) != value) throw new Exception("Unexpected processing error state");
        }
        void Test(string name, Action body)
        {
            sample = Activator.CreateInstance(sampleType, true);
            try { body(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { errors++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        try
        {
            Test("Historical errors do not fail an idle microphone after restart", () => { Read(10000, 20, 5); Expect(false); Read(10000, 20, 5); Expect(false); });
            Test("A new error is reported without model progress", () => { Read(10000, 20, 0); Read(10480, 20, 1, 20); Expect(true); });
            Test("Successful processing clears a previous error", () => { Read(10000, 20, 0); Read(10480, 20, 1, 20); Expect(true); Read(10960, 21, 1, 20); Expect(false); });
            Test("Counter reset clears a previous error", () => { Read(10000, 20, 0); Read(10480, 20, 1, 20); Expect(true); Read(0, 0, 0); Expect(false); });
            Test("Failure before UI startup remains visible", () => { Read(10000, 20, 5, 20); Expect(true); Read(58000, 20, 5, 20); Expect(true); });
            Test("Recovery before UI startup does not show an old error", () => { Read(10000, 21, 5, 20); Expect(false); });
            Test("Failure and recovery between polls do not latch an idle error", () => { Read(10000, 20, 0); Read(10960, 21, 1, 20); Expect(false); Read(10960, 21, 1, 20); Expect(false); });
        }
        finally
        {
            control.Dispose();
            File.Delete(path);
            Directory.Delete(root);
        }
        Console.WriteLine($"{passed} passed, {errors} failed");
        return errors == 0 ? 0 : 1;
    }
}
