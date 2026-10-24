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
        internal static readonly Color Ink = Color.FromArgb(32, 32, 32);
        internal static readonly Color Secondary = Color.FromArgb(93, 93, 93);
        internal static readonly Color Sidebar = Color.FromArgb(243, 243, 243);
        internal static readonly Color Selection = Color.FromArgb(232, 240, 250);
        internal static readonly Color Accent = Color.FromArgb(0, 103, 192);
        internal static readonly Color Border = Color.FromArgb(224, 224, 224);
        internal readonly Font Body = new Font("Segoe UI", 10);
        internal readonly Font Title = new Font("Segoe UI Semibold", 18);
        internal readonly Font Device = new Font("Segoe UI Semibold", 13);
        internal readonly Font Section = new Font("Segoe UI Semibold", 10);
        internal readonly Font Small = new Font("Segoe UI", 9);
        internal readonly Icon AppIcon;
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        private readonly Icon[] stateIcons = new Icon[3];
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

        internal Theme()
        {
            AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        private static Icon CloneIcon(IntPtr handle)
        { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
        internal Icon TrayIcon(int state)
        {
            if (stateIcons[state] == null)
            {
                int size = Math.Max(16, GetSystemMetrics(49));
                using (var bitmap = new Bitmap(size, size))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.ScaleTransform(size / 16f, size / 16f);
                        using (var tile = Rounded(new RectangleF(0, 0, 16, 16), 3.5f))
                        using (var ink = new SolidBrush(Accent)) graphics.FillPath(ink, tile);
                        using (var capsule = Rounded(new RectangleF(5, 2, 5, 8), 2.5f))
                        using (var white = new SolidBrush(Color.White)) graphics.FillPath(white, capsule);
                        using (var pen = new Pen(Color.White, 1.3f))
                        {
                            pen.StartCap = pen.EndCap = LineCap.Round;
                            graphics.DrawArc(pen, 3, 4.5f, 9, 8, 0, 180);
                            graphics.DrawLine(pen, 3, 8.5f, 3, 7);
                            graphics.DrawLine(pen, 12, 8.5f, 12, 7);
                            graphics.DrawLine(pen, 7.5f, 12.5f, 7.5f, 14);
                        }
                        using (var white = new SolidBrush(Color.White)) graphics.FillEllipse(white, 8.5f, 8.5f, 7.5f, 7.5f);
                        Color color = state == 1 ? Color.FromArgb(46, 146, 91) : state == 2 ? Color.FromArgb(197, 68, 68) : Color.FromArgb(137, 146, 142);
                        using (var badge = new SolidBrush(color)) graphics.FillEllipse(badge, 9.25f, 9.25f, 6, 6);
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
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
            button.BackColor = primary ? Accent : Color.White;
            button.ForeColor = primary ? Color.White : Ink;
            bool hovered = false, pressed = false;
            button.MouseEnter += delegate { hovered = true; button.Invalidate(); };
            button.MouseLeave += delegate { hovered = pressed = false; button.Invalidate(); };
            button.MouseDown += delegate { pressed = true; button.Invalidate(); };
            button.MouseUp += delegate { pressed = false; button.Invalidate(); };
            button.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (primary) e.Graphics.Clear(Color.White);
                float scale = e.Graphics.DpiX / 96f;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color background = !button.Enabled ? Color.FromArgb(246, 246, 246) : primary ? (pressed ? Color.FromArgb(0, 80, 160) : hovered ? Color.FromArgb(0, 94, 176) : Accent) : (pressed ? Color.FromArgb(239, 239, 239) : hovered ? Color.FromArgb(249, 249, 249) : Color.White);
                using (var shape = Rounded(new RectangleF(.5f, .5f, button.Width - 1, button.Height - 1), 5 * scale))
                using (var brush = new SolidBrush(background))
                using (var border = new Pen(primary && button.Enabled ? background : Border))
                { e.Graphics.FillPath(brush, shape); e.Graphics.DrawPath(border, shape); }
                TextRenderer.DrawText(e.Graphics, button.Text, button.Font, button.ClientRectangle, button.Enabled ? button.ForeColor : Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (button.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(button.ClientRectangle, -(int)(4 * scale), -(int)(4 * scale)), button.ForeColor, background);
            };
        }
        private static GraphicsPath Rounded(RectangleF box, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
            path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
            path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
            path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        public void Dispose()
        {
            foreach (var icon in stateIcons) if (icon != null) icon.Dispose();
            AppIcon.Dispose();
            Body.Dispose(); Title.Dispose(); Device.Dispose(); Section.Dispose(); Small.Dispose();
        }
    }
}
