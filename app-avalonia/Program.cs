using System;
using System.Linq;
using Avalonia;

namespace NoiseReduction.Avalonia
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            App.StartHidden = args.Contains("--tray");
            return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
    }
}
