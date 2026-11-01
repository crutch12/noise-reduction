using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace NoiseReduction.Avalonia
{
    internal static class TrayGlyph
    {
        private static readonly Dictionary<(State State, bool Light), WindowIcon> cache = new();
        internal static WindowIcon Create(State state, bool light)
        {
            if (cache.TryGetValue((state, light), out var result)) return result;
            var glyph = new Glyph(state, light);
            glyph.Measure(new Size(32, 32)); glyph.Arrange(new Rect(0, 0, 32, 32));
            using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(192, 192));
            bitmap.Render(glyph);
            result = new WindowIcon(bitmap);
            cache[(state, light)] = result;
            return result;
        }

        private sealed class Glyph : Control
        {
            private readonly State state;
            private readonly bool light;
            internal Glyph(State state, bool light) { this.state = state; this.light = light; Width = Height = 32; }
            public override void Render(DrawingContext context)
            {
                base.Render(context);
                var ink = new Pen(AppTheme.Brush(light ? "#1A1A1A" : "#FFFFFF"), 1.15, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                string color = state is State.Working or State.Idle ? light ? "#0A8C80" : "#17D2BE"
                    : state is State.Error or State.Recovering or State.Unavailable or State.Fallback ? light ? "#C42B1C" : "#FF6E6E" : "#8C8C8C";
                var wave = new Pen(AppTheme.Brush(color), 1.35, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                using (context.PushTransform(Matrix.CreateScale(2, 2)))
                {
                    context.DrawGeometry(null, ink, Geometry.Parse("M 4.5,4.25 A 3.5,3.5 0 0 1 11.5,4.25 L 11.5,7.25 A 3.5,3.5 0 0 1 4.5,7.25 Z"));
                    context.DrawGeometry(null, ink, Geometry.Parse("M 2,6.5 L 2,8 A 6,5.625 0 0 0 14,8 L 14,6.5 M 8,13.6 L 8,15.25 M 5.5,15.25 L 10.5,15.25"));
                    var geometry = new StreamGeometry();
                    using (var path = geometry.Open())
                    {
                        path.BeginFigure(new Point(5.9, 6.2), false);
                        for (int index = 1; index <= 16; index++)
                        {
                            double t = index / 16d;
                            path.LineTo(new Point(5.9 + 4.2 * t, 6.2 - Math.Sin(t * Math.PI * 2) * 1.9));
                        }
                        path.EndFigure(false);
                    }
                    context.DrawGeometry(null, wave, geometry);
                }
            }
        }
    }
}
