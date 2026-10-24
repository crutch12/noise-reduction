using System;
using System.Runtime.InteropServices;

namespace NoiseReduction.Desktop
{
    internal static class WindowAppearance
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static void Apply(IntPtr window)
        {
            int material = 2, rounded = 2;
            DwmSetWindowAttribute(window, 38, ref material, sizeof(int));
            DwmSetWindowAttribute(window, 33, ref rounded, sizeof(int));
        }
    }
}
