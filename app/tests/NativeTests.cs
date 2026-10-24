using System;
using System.IO;
using System.Reflection;
using NoiseReduction.Desktop;

internal static class NativeTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static float[] Process(float[] samples, int attenuation = 24)
    {
        var method = typeof(Comparison).GetMethod("ProcessCaptured", BindingFlags.Static | BindingFlags.NonPublic);
        Check(method != null, "Offline comparison must support captured sample processing");
        return (float[])method.Invoke(null, new object[] { samples, attenuation });
    }
    private static int Main()
    {
        float[] silence = Process(new float[48000]);
        Check(silence.Length == 48000, "Offline comparison changes duration");
        foreach (float sample in silence) Check(!float.IsNaN(sample) && !float.IsInfinity(sample) && Math.Abs(sample) < 0.000001f, "Silence introduces noise or invalid samples");
        var signal = new float[48000];
        for (int i = 0; i < signal.Length; i++) signal[i] = (float)(Math.Sin(2 * Math.PI * 440 * i / 48000) * 0.2);
        float[] output = Process(signal);
        double energy = 0;
        foreach (float sample in output) { Check(!float.IsNaN(sample) && !float.IsInfinity(sample) && Math.Abs(sample) <= 1, "Invalid processed sample"); energy += sample * sample; }
        Check(output.Length == signal.Length && energy > 0.001, "Synthetic signal lost or truncated");
        var random = new Random(42);
        var alignment = new float[48000];
        for (int i = 4800; i < alignment.Length; i++) alignment[i] = (float)(random.NextDouble() * 0.4 - 0.2);
        float[] aligned = Process(alignment, 0);
        for (int i = 4800; i < alignment.Length; i++) Check(Math.Abs(aligned[i] - alignment[i]) < 0.00001, "Zero attenuation comparison shifts or truncates samples");
        string path = Path.Combine(Path.GetTempPath(), "nr-wave-" + Guid.NewGuid() + ".wav");
        try
        {
            Comparison.WriteWave(path, output);
            byte[] bytes = File.ReadAllBytes(path);
            Check(bytes.Length == 96044 && BitConverter.ToInt32(bytes, 24) == 48000 && BitConverter.ToInt32(bytes, 40) == 96000, "Comparison WAV has incorrect duration or format");
        }
        finally { File.Delete(path); }
        Console.WriteLine("PASS: native ABI, silence, finite synthetic signal, duration, latency compensation, PCM16 WAV");
        return 0;
    }
}
