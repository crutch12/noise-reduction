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
        private const double BucketSeconds = 0.02, WindowSeconds = 4, Delay = 0.08;
        private readonly Polygon back = new Polygon(), front = new Polygon();
        private readonly Rectangle playhead = new Rectangle { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        private Func<float[]> live;
        private Func<double> progress;
        private DateTime started;
        private bool animating;
        private double drawnWidth;
        private float[] original, processed;
        internal readonly Grid View = new Grid { Height = 56, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4) };

        internal WaveformView()
        {
            View.Background = Brush("ControlFillColorSecondaryBrush");
            back.Fill = Brush("TextFillColorDisabledBrush");
            front.Fill = Brush("AccentFillColorDefaultBrush");
            playhead.Fill = Brush("TextFillColorPrimaryBrush");
            var area = new Grid();
            area.Children.Add(back); area.Children.Add(front); area.Children.Add(playhead);
            View.Children.Add(area);
            View.SizeChanged += (_, args) =>
            {
                // форма волны никогда не раздвигает карточку
                View.Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
                if (original != null && live == null) DrawResult();
            };
        }

        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
        private double Width => Math.Max(0, View.ActualWidth - View.Padding.Left - View.Padding.Right);
        private double Middle => (View.Height - View.Padding.Top - View.Padding.Bottom) / 2;

        internal void Hide()
        {
            View.Visibility = Visibility.Collapsed;
            live = null; original = processed = null;
            Animate(false);
        }

        internal void ShowLive(Func<float[]> peaks, DateTime start)
        {
            View.Visibility = Visibility.Visible;
            if (live == null) { back.Points.Clear(); front.Opacity = 1; playhead.Visibility = Visibility.Collapsed; }
            live = peaks; started = start; original = processed = null;
            Animate(true);
        }

        internal void ShowResult(float[] originalPeaks, float[] processedPeaks, bool processedAudible, Func<double> position)
        {
            View.Visibility = Visibility.Visible;
            live = null; progress = position;
            back.Opacity = processedAudible ? 0.6 : 1;
            front.Opacity = processedAudible ? 1 : 0.45;
            if (!ReferenceEquals(original, originalPeaks) || drawnWidth != Width) { original = originalPeaks; processed = processedPeaks; DrawResult(); }
            UpdatePlayhead();
            Animate(position() >= 0);
        }

        private void Animate(bool value)
        {
            if (value == animating) return;
            animating = value;
            if (value) CompositionTarget.Rendering += OnFrame; else CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object sender, object args)
        {
            if (live != null) DrawLive();
            else UpdatePlayhead();
        }

        // голос заметен и на тихом микрофоне, громкий не упирается в край
        private static double Height(float peak) => Math.Min(1, Math.Pow(Math.Max(0, peak) * 4, 0.6));

        private void DrawLive()
        {
            var peaks = live();
            double width = Width, middle = Middle;
            front.Points.Clear();
            if (width <= 0) return;
            // время показа идёт плавно по часам, данные отстают на Delay и всегда успевают прийти
            double now = Math.Min((DateTime.UtcNow - started).TotalSeconds - Delay, peaks.Length * BucketSeconds);
            double scale = width / WindowSeconds;
            int first = Math.Max(0, (int)((now - WindowSeconds) / BucketSeconds) - 1);
            int last = Math.Min(peaks.Length - 1, (int)(now / BucketSeconds));
            if (last < first) return;
            for (int i = first; i <= last; i++) front.Points.Add(new Point(width - (now - i * BucketSeconds) * scale, middle - Math.Max(0.75, Height(peaks[i]) * middle)));
            for (int i = last; i >= first; i--) front.Points.Add(new Point(width - (now - i * BucketSeconds) * scale, middle + Math.Max(0.75, Height(peaks[i]) * middle)));
        }

        private void DrawResult()
        {
            drawnWidth = Width;
            float max = 0.02f;
            foreach (float peak in original) max = Math.Max(max, peak);
            Fill(back, original, 1 / max);
            Fill(front, processed, 1 / max);
        }

        private void Fill(Polygon shape, float[] peaks, double scale)
        {
            shape.Points.Clear();
            double width = Width, middle = Middle;
            if (width <= 0 || peaks == null || peaks.Length == 0) return;
            double step = width / Math.Max(1, peaks.Length - 1);
            for (int i = 0; i < peaks.Length; i++) shape.Points.Add(new Point(i * step, middle - Math.Max(0.75, Math.Min(1, peaks[i] * scale) * middle)));
            for (int i = peaks.Length - 1; i >= 0; i--) shape.Points.Add(new Point(i * step, middle + Math.Max(0.75, Math.Min(1, peaks[i] * scale) * middle)));
        }

        private void UpdatePlayhead()
        {
            double value = progress == null ? -1 : progress();
            playhead.Visibility = value >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (value >= 0) playhead.Margin = new Thickness(Math.Clamp(value, 0, 1) * Math.Max(0, Width - 2), 0, 0, 0);
            if (value < 0) Animate(false);
        }
    }
}
