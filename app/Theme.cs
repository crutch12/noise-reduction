using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Collections.Generic;

namespace NoiseReduction.Desktop
{
    internal sealed class TrayHealth
    {
        private sealed class Sample { internal long Errors, Models; internal bool Failed; }
        private readonly Dictionary<string, Sample> samples = new Dictionary<string, Sample>();
        internal void Observe(string id, long errors, long models)
        {
            Sample sample;
            if (!samples.TryGetValue(id, out sample))
            { sample = new Sample { Failed = errors > 0 }; samples.Add(id, sample); }
            else if (errors > sample.Errors) sample.Failed = true;
            else if (errors < sample.Errors || models > sample.Models) sample.Failed = false;
            sample.Errors = errors; sample.Models = models;
        }
        internal bool Failed(string id)
        { Sample sample; return samples.TryGetValue(id, out sample) && sample.Failed; }
    }
    internal sealed class Theme : IDisposable
    {
        internal static readonly Color Ink = Color.FromArgb(32, 58, 53);
        internal static readonly Color Secondary = Color.FromArgb(88, 102, 95);
        internal static readonly Color Sidebar = Color.FromArgb(241, 244, 242);
        internal static readonly Color Selection = Color.FromArgb(227, 238, 231);
        internal static readonly Color Accent = Color.FromArgb(47, 107, 89);
        internal static readonly Color Border = Color.FromArgb(222, 229, 223);
        internal readonly Font Body = new Font("Segoe UI", 9);
        internal readonly Font Title = new Font("Segoe UI", 18, FontStyle.Bold);
        internal readonly Font Device = new Font("Segoe UI", 13, FontStyle.Bold);
        internal readonly Font Section = new Font("Segoe UI", 10, FontStyle.Bold);
        internal readonly Font Small = new Font("Segoe UI", 8.5f);
        internal readonly Icon AppIcon;
        private readonly Icon plainTray;
        private readonly Icon[] stateIcons = new Icon[3];
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

        internal Theme()
        {
            AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            IntPtr large, small;
            ExtractIconEx(Application.ExecutablePath, 0, out large, out small, 1);
            try { plainTray = small == IntPtr.Zero ? (Icon)AppIcon.Clone() : CloneIcon(small); }
            finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); }
        }
        private static Icon CloneIcon(IntPtr handle)
        { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
        internal Icon TrayIcon(int state)
        {
            if (stateIcons[state] == null)
            {
                using (var bitmap = new Bitmap(16, 16))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.DrawIcon(plainTray, new Rectangle(0, 0, 16, 16));
                        using (var white = new SolidBrush(Color.White)) graphics.FillEllipse(white, 8, 8, 8, 8);
                        Color color = state == 1 ? Color.FromArgb(46, 146, 91) : state == 2 ? Color.FromArgb(197, 68, 68) : Color.FromArgb(137, 146, 142);
                        using (var badge = new SolidBrush(color)) graphics.FillEllipse(badge, 9, 9, 6, 6);
                    }
                    IntPtr handle = bitmap.GetHicon();
                    try { stateIcons[state] = CloneIcon(handle); } finally { DestroyIcon(handle); }
                }
            }
            return stateIcons[state];
        }
        internal static void Button(Button button, string accessible, bool primary)
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(70, 32);
            button.Margin = new Padding(0, 0, 8, 0);
            button.Padding = new Padding(8, 2, 8, 2);
            button.AccessibleName = accessible;
            if (primary)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
                button.BackColor = Accent;
                button.ForeColor = Color.White;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(39, 91, 75);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 73, 60);
            }
            else button.UseVisualStyleBackColor = true;
        }
        public void Dispose()
        {
            foreach (var icon in stateIcons) if (icon != null) icon.Dispose();
            plainTray.Dispose(); AppIcon.Dispose();
            Body.Dispose(); Title.Dispose(); Device.Dispose(); Section.Dispose(); Small.Dispose();
        }
    }
}
