using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NoiseReduction.WinUI
{
    // Значок трея без GDI+: линии со скруглёнными концами по расстоянию до отрезков, края сглажены
    internal static class TrayGlyph
    {
        private readonly struct Stroke
        {
            internal readonly (float X, float Y)[] Points;
            internal readonly float Width;
            internal readonly uint Color;
            internal Stroke((float X, float Y)[] points, float width, uint color) { Points = points; Width = width; Color = color; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo { public bool IsIcon; public int HotspotX, HotspotY; public IntPtr Mask, Color; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant; }
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo info);

        private static (float X, float Y)[] Arc(float cx, float cy, float rx, float ry, float from, float to)
        {
            var points = new (float, float)[25];
            for (int i = 0; i < points.Length; i++)
            {
                double angle = (from + (to - from) * i / (points.Length - 1)) * Math.PI / 180;
                points[i] = (cx + rx * (float)Math.Cos(angle), cy + ry * (float)Math.Sin(angle));
            }
            return points;
        }

        private static (float X, float Y)[] Join(params (float X, float Y)[][] parts)
        {
            var all = new List<(float, float)>();
            foreach (var part in parts) all.AddRange(part);
            return all.ToArray();
        }

        // тонкий контур микрофона с волной внутри, цвет волны показывает состояние; рисунок задан в сетке 16x16
        internal static IntPtr Create(int size, int state, bool light)
        {
            uint ink = light ? 0xFF1A1A1Au : 0xFFFFFFFFu;
            uint wave = state == 1 ? (light ? 0xFF0A8C80u : 0xFF17D2BEu) : state == 2 ? (light ? 0xFFC42B1Cu : 0xFFFF6E6Eu) : 0xFF8C8C8Cu;
            var wavePoints = new (float, float)[17];
            for (int i = 0; i < wavePoints.Length; i++) { float t = i / 16f; wavePoints[i] = (5.9f + 4.2f * t, 6.2f - (float)Math.Sin(t * Math.PI * 2) * 1.9f); }
            var strokes = new[]
            {
                // капсула: верхняя дуга, правая сторона, нижняя дуга, левая сторона
                new Stroke(Join(Arc(8, 4.25f, 3.5f, 3.5f, 180, 360), Arc(8, 7.25f, 3.5f, 3.5f, 0, 180), new[] { (4.5f, 4.25f) }), 1.15f, ink),
                // подставка
                new Stroke(Join(new[] { (2f, 6.5f), (2f, 8f) }, Arc(8, 7.875f, 6, 5.625f, 180, 0), new[] { (14f, 8f), (14f, 6.5f) }), 1.15f, ink),
                new Stroke(new[] { (8f, 13.6f), (8f, 15.25f) }, 1.15f, ink),
                new Stroke(new[] { (5.5f, 15.25f), (10.5f, 15.25f) }, 1.15f, ink),
                new Stroke(wavePoints, 1.35f, wave),
            };
            var pixels = new uint[size * size];
            float scale = size / 16f;
            foreach (var stroke in strokes) Draw(pixels, size, scale, stroke);
            return ToIcon(pixels, size);
        }

        private static void Draw(uint[] pixels, int size, float scale, Stroke stroke)
        {
            float half = stroke.Width * scale / 2;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f, best = float.MaxValue;
                    for (int i = 1; i < stroke.Points.Length; i++)
                    {
                        float ax = stroke.Points[i - 1].X * scale, ay = stroke.Points[i - 1].Y * scale, bx = stroke.Points[i].X * scale, by = stroke.Points[i].Y * scale;
                        float dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy;
                        float t = length > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / length, 0, 1) : 0;
                        float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
                        best = Math.Min(best, ex * ex + ey * ey);
                    }
                    float coverage = Math.Clamp(half + 0.5f - (float)Math.Sqrt(best), 0, 1);
                    if (coverage > 0) Blend(ref pixels[y * size + x], stroke.Color, coverage);
                }
        }

        // смешивание поверх уже нарисованного, цвет хранится с умноженной альфой
        private static void Blend(ref uint target, uint color, float coverage)
        {
            float a = coverage, inverse = 1 - a;
            uint r = (color >> 16) & 255, g = (color >> 8) & 255, b = color & 255;
            uint ta = target >> 24, tr = (target >> 16) & 255, tg = (target >> 8) & 255, tb = target & 255;
            target = ((uint)(a * 255 + ta * inverse) << 24) | ((uint)(r * a + tr * inverse) << 16) | ((uint)(g * a + tg * inverse) << 8) | (uint)(b * a + tb * inverse);
        }

        private static IntPtr ToIcon(uint[] pixels, int size)
        {
            var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = size, Height = -size, Planes = 1, BitCount = 32 };
            IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
            try
            {
                // значкам Windows нужна обычная, не умноженная альфа
                var straight = new int[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint value = pixels[i], a = value >> 24;
                    if (a == 0) continue;
                    uint r = Math.Min(255u, ((value >> 16) & 255) * 255 / a), g = Math.Min(255u, ((value >> 8) & 255) * 255 / a), b = Math.Min(255u, (value & 255) * 255 / a);
                    straight[i] = unchecked((int)((a << 24) | (r << 16) | (g << 8) | b));
                }
                Marshal.Copy(straight, 0, bits, straight.Length);
                var info = new IconInfo { IsIcon = true, Mask = mask, Color = color };
                return CreateIconIndirect(ref info);
            }
            finally { DeleteObject(color); DeleteObject(mask); }
        }
    }
}
