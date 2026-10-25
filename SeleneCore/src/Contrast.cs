using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Selene
{
    // Text stays readable over any picture: the home's shades (left, bottom, top bar) deepen as much as the picture behind
    // them needs. A picture is measured where the text sits; light text needs a darker shade over a bright picture, dark
    // text (light themes) a lighter one over a dark picture. Dark wallpapers keep the light shade they always had.
    public static class Contrast
    {
        private const double Base = 0.42, Max = 0.88;   // (a firmer least: dark wallpapers get the console gradient too)
        private static double wallpaperNeed = 0;
        private static ImageSource wallpaperImg;
        private static double shadeStrength = Base;

        // how much the home's shading darkens the spot at (x, y), both 0..1 across the screen (the three shades' masks)
        public static double ShadeAt(double x, double y)
        {
            Func<double, double, double, double, double, double> ramp = (v, a, av, b, bv) => v <= a ? av : v >= b ? bv : av + (bv - av) * (v - a) / (b - a);
            double left = x < 0.5 ? ramp(x, 0, 0.88, 0.5, 0.5) : ramp(x, 0.5, 0.5, 1, 0.15);
            double bottom = ramp(y, 0.4, 0, 1, 0.9);
            double top = y < 0.1 ? ramp(y, 0, 0.9, 0.1, 0.44) : ramp(y, 0.1, 0.44, 0.26, 0);
            double s = shadeStrength;
            return 1 - (1 - left * s) * (1 - bottom * s) * (1 - top * s);
        }
        // the background changed (another wallpaper, game art came or went): text that adapts to it measures again
        public static event Action Changed;
        public static ImageSource WallpaperImage { get { return wallpaperImg; } }

        // the picture behind the home right now: the game art when it shows, else the wallpaper
        public static ImageSource Behind()
        {
            var artOpacity = Application.Current.Resources["SeleneArtOpacity"];
            bool artShown = art.Count > 0 && !(artOpacity is double && (double)artOpacity < 0.05);
            return artShown ? art.Values.Last() : wallpaperImg;
        }
        private static readonly Dictionary<object, ImageSource> art = new Dictionary<object, ImageSource>();
        private static readonly Dictionary<ImageSource, double> measured = new Dictionary<ImageSource, double>();

        // 0 = the text reads fine over it, 1 = it needs the deepest shade
        public static double Need(ImageSource img)
        {
            var src = img as BitmapSource;
            if (src == null || src.PixelWidth < 2 || src.PixelHeight < 2) return 0;
            double cached;
            if (measured.TryGetValue(img, out cached)) return cached;
            double need = 0;
            try
            {
                // big enough that fine bright detail (a dotted map, stars) isn't averaged away into grey
                var small = new TransformedBitmap(src, new ScaleTransform(192.0 / src.PixelWidth, 108.0 / src.PixelHeight));
                var px32 = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = px32.PixelWidth, h = px32.PixelHeight;
                var px = new byte[w * h * 4];
                px32.CopyPixels(px, w * 4, 0);
                double sum = 0, weight = 0, bright = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        // where the words are: the left side, the bottom (hints) and the top bar count; the right middle less
                        double wt = (x < w * 0.6 ? 1.0 : 0.35) + (y > h * 0.7 || y < h * 0.12 ? 0.6 : 0);
                        int i = (y * w + x) * 4;
                        double lum = (0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) / 255.0;
                        sum += lum * wt; weight += wt;
                        if (lum > 0.55) bright += wt;
                    }
                double light = weight > 0 ? sum / weight : 0;
                // bright detail on a dark picture still fights light text: its share counts as well as the average
                double share = weight > 0 ? bright / weight : 0;
                light = Math.Max(light, Math.Min(0.9, 0.3 + share * 1.6));
                var ink = Ink.Of("SeleneInkColor");
                bool lightText = (0.2126 * ink.R + 0.7152 * ink.G + 0.0722 * ink.B) / 255.0 > 0.5;
                need = lightText ? light : 1 - light;
            }
            catch (Exception ex) { HomeNav.Log("contrast: " + ex.Message); }
            if (measured.Count > 64) measured.Clear();
            measured[img] = need;
            return need;
        }

        public static void Wallpaper(ImageSource img)
        {
            wallpaperImg = img;
            wallpaperNeed = Need(img);
            Apply();
        }

        // a picture of the home's art layer came (or went: null)
        public static void Art(object layer, ImageSource img)
        {
            if (img == null) art.Remove(layer); else art[layer] = img;
            Apply();
        }

        public static void Apply()
        {
            var w = Application.Current != null ? Application.Current.MainWindow : null;
            if (w == null) return;
            var artOpacity = Application.Current.Resources["SeleneArtOpacity"];
            bool artShown = art.Count > 0 && !(artOpacity is double && (double)artOpacity < 0.05);   // a profile that keeps the wallpaper
            double need = artShown ? art.Values.Max(i => Need(i)) : wallpaperNeed;
            // the Shade over the art setting scales it (Lighter / As needed / Deeper)
            double scale = Features.Number("shade");
            if (scale <= 0) scale = 1;
            double strength = Math.Max(Base * scale, Math.Min(Max, (Base + (need - 0.35) * 1.5) * scale));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (var name in new[] { "SeleneHubShadeLeft", "SeleneHubShadeBottom", "SeleneHubShadeTop" })
            {
                var shade = HomeNav.FindByName(w, name) as UIElement;
                if (shade == null) continue;
                // like the PS5's home: a gradient from the left fading out to the right, plus soft shading at the top (the bar, the
                // clock) and the bottom (the hints), all as deep as the picture needs
                double to = strength;
                shade.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
            }
            shadeStrength = strength;
            var changed = Changed;
            if (changed != null) changed();
        }
    }
}
