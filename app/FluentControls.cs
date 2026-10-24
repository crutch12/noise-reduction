using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NoiseReduction.Desktop
{
    internal sealed class SurfacePanel : Panel
    {
        internal SurfacePanel()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Padding = new Padding(14);
        }
        internal static GraphicsPath Outline(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = radius * 2;
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outline = Outline(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8 * DeviceDpi / 96f))
            using (var fill = new SolidBrush(Color.White))
            using (var border = new Pen(Theme.Border))
            { e.Graphics.FillPath(fill, outline); e.Graphics.DrawPath(border, outline); }
            base.OnPaint(e);
        }
    }
    internal sealed class FluentToggle : CheckBox
    {
        internal FluentToggle()
        { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = TextRenderer.MeasureText(Text, Font);
            float scale = DeviceDpi / 96f;
            return new Size(text.Width + (int)(58 * scale), Math.Max(text.Height, (int)(28 * scale)));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = DeviceDpi / 96f;
            int trackWidth = (int)(38 * scale), trackHeight = (int)(20 * scale);
            var track = new RectangleF(Width - trackWidth - 3 * scale, (Height - trackHeight) / 2f, trackWidth, trackHeight);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Color.FromArgb(205, 210, 216) : Checked ? Theme.Accent : Color.FromArgb(124, 132, 143);
            using (var outline = SurfacePanel.Outline(track, trackHeight / 2f))
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, outline);
            float diameter = trackHeight - 6 * scale;
            float x = Checked ? track.Right - diameter - 3 * scale : track.Left + 3 * scale;
            using (var thumb = new SolidBrush(Color.White)) e.Graphics.FillEllipse(thumb, x, track.Top + 3 * scale, diameter, diameter);
            var area = new Rectangle(0, 0, Math.Max(0, (int)track.Left - (int)(8 * scale)), Height);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, area, Enabled ? ForeColor : SystemColors.GrayText, flags);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
        }
    }
}
