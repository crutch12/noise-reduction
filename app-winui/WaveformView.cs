using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace NoiseReduction.WinUI
{
    // Волна записи: исходный звук серым, после шумодава цветом поверх, бегунок при прослушивании
    internal sealed class WaveformView
    {
        private readonly Polygon back = new Polygon(), front = new Polygon();
        private readonly Rectangle playhead = new Rectangle { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        internal readonly Grid View = new Grid { Height = 56, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4) };

        internal WaveformView()
        {
            View.Background = Brush("ControlFillColorSecondaryBrush");
            back.Fill = Brush("TextFillColorDisabledBrush");
            front.Fill = Brush("AccentFillColorDefaultBrush");
            playhead.Fill = Brush("TextFillColorPrimaryBrush");
            var canvas = new Grid();
            canvas.Children.Add(back); canvas.Children.Add(front); canvas.Children.Add(playhead);
            View.Children.Add(canvas);
        }

        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

        internal void Live(float[] peaks)
        {
            // последние 3 секунды бегут справа налево
            const int window = 150;
            var shown = new float[window];
            int start = Math.Max(0, peaks.Length - window);
            Array.Copy(peaks, start, shown, window - (peaks.Length - start), peaks.Length - start);
            Draw(front, shown, Scale(shown));
            back.Points.Clear();
            front.Opacity = 1;
            playhead.Visibility = Visibility.Collapsed;
        }

        internal void Result(float[] original, float[] processed, bool processedAudible, double progress)
        {
            double scale = Scale(original);
            Draw(back, original, scale);
            Draw(front, processed, scale);
            back.Opacity = processedAudible ? 0.6 : 1;
            front.Opacity = processedAudible ? 1 : 0.45;
            double width = Width;
            playhead.Visibility = progress >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (progress >= 0) playhead.Margin = new Thickness(Math.Clamp(progress, 0, 1) * Math.Max(0, width - 2), 0, 0, 0);
        }

        private double Width => Math.Max(0, View.ActualWidth - View.Padding.Left - View.Padding.Right);

        private static double Scale(float[] peaks)
        {
            float max = 0.02f;
            foreach (float peak in peaks) max = Math.Max(max, peak);
            return 1 / max;
        }

        private void Draw(Polygon shape, float[] peaks, double scale)
        {
            shape.Points.Clear();
            double width = Width, height = View.Height - View.Padding.Top - View.Padding.Bottom, middle = height / 2;
            if (width <= 0 || peaks.Length == 0) return;
            double step = width / Math.Max(1, peaks.Length - 1);
            for (int i = 0; i < peaks.Length; i++) shape.Points.Add(new Point(i * step, middle - Math.Max(0.75, Math.Min(1, peaks[i] * scale) * middle)));
            for (int i = peaks.Length - 1; i >= 0; i--) shape.Points.Add(new Point(i * step, middle + Math.Max(0.75, Math.Min(1, peaks[i] * scale) * middle)));
        }
    }
}
