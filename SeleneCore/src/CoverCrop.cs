using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Selene
{
    // The home row's tiles are squares; a cover that isn't one lost its bottom (tall) or its sides (wide), always from the same
    // place. This picks the square window of the cover with the most going on in it (edges and detail: faces, logos, art) rather
    // than the plain bands, leaning a little to the top (tall) or the middle (wide) when it can't tell. The theme names the
    // cover's face "SeleneCoverFace"; the brush's own picture decides, so a recycled tile is redone when its game changes.
    public static class CoverCrop
    {
        private static readonly ConditionalWeakTable<ImageBrush, object> hooked = new ConditionalWeakTable<ImageBrush, object>();
        private static readonly ConditionalWeakTable<BitmapSource, object> windows = new ConditionalWeakTable<BitmapSource, object>();   // boxed Rect per picture

        public static void Install()
        {
            EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var face = sender as Border;
            if (face == null || face.Name != "SeleneCoverFace") return;
            var brush = face.Background as ImageBrush;
            if (brush == null) return;
            try
            {
                object seen;
                if (!hooked.TryGetValue(brush, out seen))
                {
                    hooked.Add(brush, brush);
                    System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ImageBrush.ImageSourceProperty, typeof(ImageBrush)).AddValueChanged(brush, (s, a) => Apply(brush));
                }
                Apply(brush);
            }
            catch (Exception ex) { HomeNav.Log("cover crop: " + ex.Message); }
        }

        internal static void Apply(ImageBrush brush)
        {
            var src = brush.ImageSource as BitmapSource;
            if (src == null) return;
            if (!Features.On("smartcrop")) { brush.Viewbox = new Rect(0, 0, 1, 1); brush.AlignmentY = AlignmentY.Top; return; }
            if (src.IsDownloading) { src.DownloadCompleted += (s, a) => Apply(brush); return; }
            object known;
            Rect window;
            if (windows.TryGetValue(src, out known)) window = (Rect)known;
            else
            {
                window = Choose(src);
                try { windows.Add(src, window); } catch { }
            }
            brush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
            brush.Viewbox = window;
            brush.Stretch = Stretch.Fill;   // the window is already square
        }

        // worked out ahead, off the UI thread (TileCovers): Apply then only looks it up
        internal static void Prepare(BitmapSource src)
        {
            object known;
            if (src == null || windows.TryGetValue(src, out known)) return;
            try { windows.Add(src, Choose(src)); } catch { }
        }

        // the square window (relative to the picture) with the most detail
        private static Rect Choose(BitmapSource src)
        {
            double w = src.PixelWidth, h = src.PixelHeight;
            if (w < 2 || h < 2) return new Rect(0, 0, 1, 1);
            double ratio = h / w;
            if (ratio > 0.92 && ratio < 1.08) return new Rect(0, 0, 1, 1);   // already (nearly) square
            bool tall = ratio > 1;
            // a small grey copy: the long side is scanned, the short side is the window's width
            double scale = 64.0 / Math.Min(w, h);
            var small = new TransformedBitmap(src, new ScaleTransform(scale, scale));
            var grey = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);
            int sw = grey.PixelWidth, sh = grey.PixelHeight;
            var px = new byte[sw * sh];
            grey.CopyPixels(px, sw, 0);
            int length = tall ? sh : sw, win = tall ? sw : sh;   // window = a square of 'win' along the long side
            if (length <= win) return new Rect(0, 0, 1, 1);
            // detail per line along the long side (how much each pixel differs from its neighbours)
            var energy = new double[length];
            for (int y = 0; y < sh - 1; y++)
                for (int x = 0; x < sw - 1; x++)
                {
                    int i = y * sw + x;
                    double d = Math.Abs(px[i] - px[i + 1]) + Math.Abs(px[i] - px[i + sw]);
                    energy[tall ? y : x] += d;
                }
            int starts = length - win;
            double sum = 0;
            for (int i = 0; i < win; i++) sum += energy[i];
            int best = 0; double bestScore = double.MinValue;
            for (int s = 0; s <= starts; s++)
            {
                if (s > 0) sum += energy[s + win - 1] - energy[s - 1];
                double at = (double)s / starts;                                   // 0 = start .. 1 = end
                double bias = tall ? 1 - 0.20 * at : 1 - 0.30 * Math.Abs(at - 0.5) * 2;   // tall: lean to the top; wide: lean to the middle
                double score = sum * bias;
                if (score > bestScore) { bestScore = score; best = s; }
            }
            double from = (double)best / length, size = (double)win / length;
            return tall ? new Rect(0, from, 1, size) : new Rect(from, 0, size, 1);
        }
    }
}
