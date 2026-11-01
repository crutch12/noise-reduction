using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NoiseReduction.Avalonia
{
    internal sealed class WaveformView : Control
    {
        private VoiceTest voice;
        private float[] original, processed;
        private StreamGeometry originalShape, processedShape;
        private double drawnWidth, drawnHeight;

        internal WaveformView()
        {
            Height = 64;
            ClipToBounds = true;
            IsHitTestVisible = false;
        }

        internal void Update(VoiceTest value)
        {
            voice = value;
            if (value == null || value.Recording || !value.HasRecording)
            {
                original = processed = null;
                originalShape = processedShape = null;
            }
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            IBrush accent = AppTheme.Brush(AppTheme.IsDark ? "#50D5C1" : "#087F74");
            if (voice == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            if (voice.Recording)
            {
                var peaks = voice.LivePeaks;
                if (peaks.Length != 0) context.DrawGeometry(accent, null, Shape(peaks, 1, true));
                return;
            }
            if (!voice.HasRecording) return;
            var nextOriginal = voice.Original;
            var nextProcessed = voice.Processed;
            if (!ReferenceEquals(original, nextOriginal) || !ReferenceEquals(processed, nextProcessed) || drawnWidth != Bounds.Width || drawnHeight != Bounds.Height)
            {
                original = nextOriginal; processed = nextProcessed;
                drawnWidth = Bounds.Width; drawnHeight = Bounds.Height;
                float reference = 0.02f;
                foreach (float sample in original) if (float.IsFinite(sample)) reference = Math.Max(reference, Math.Abs(sample));
                originalShape = Shape(original, reference, false);
                processedShape = Shape(processed, reference, false);
            }
            using (context.PushOpacity(voice.OriginalSelected ? .8 : .45))
                if (originalShape != null) context.DrawGeometry(Brushes.Gray, null, originalShape);
            using (context.PushOpacity(voice.OriginalSelected ? .4 : .9))
                if (processedShape != null) context.DrawGeometry(accent, null, processedShape);
            if (voice.Playing && voice.Duration.TotalSeconds > 0)
            {
                double x = Bounds.Width * Math.Clamp(voice.Position.TotalSeconds / voice.Duration.TotalSeconds, 0, 1);
                context.DrawLine(new Pen(accent, 1.5), new Point(x, 0), new Point(x, Bounds.Height));
            }
        }

        private StreamGeometry Shape(float[] samples, float reference, bool live)
        {
            if (samples == null || samples.Length == 0) return null;
            int buckets = Math.Max(1, Math.Min(samples.Length, (int)Math.Ceiling(Bounds.Width)));
            float[] peaks = new float[buckets];
            for (int i = 0; i < samples.Length; i++)
                if (float.IsFinite(samples[i])) peaks[(int)((long)i * buckets / samples.Length)] = Math.Max(peaks[(int)((long)i * buckets / samples.Length)], Math.Abs(samples[i]));
            double middle = Bounds.Height / 2;
            double width = live ? Bounds.Width * samples.Length / 200.0 : Bounds.Width;
            double offset = live ? Bounds.Width - width : 0;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new Point(offset, middle), true);
                for (int i = 0; i < buckets; i++)
                    path.LineTo(new Point(offset + (i + .5) * width / buckets, middle - Amplitude(peaks[i]) * (middle - 2)));
                path.LineTo(new Point(offset + width, middle));
                for (int i = buckets - 1; i >= 0; i--)
                    path.LineTo(new Point(offset + (i + .5) * width / buckets, middle + Amplitude(peaks[i]) * (middle - 2)));
                path.EndFigure(true);
            }
            return geometry;

            double Amplitude(float peak) => live ? Math.Min(1, Math.Pow(peak * 4, .6)) : Math.Min(1, peak / reference);
        }
    }
}
