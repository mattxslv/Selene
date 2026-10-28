// Readable text over any picture, automatically. Every piece of text on the home and in Collection is measured against the
// picture right behind it (the wallpaper or the game art, cropped as the screen shows it): where it reads fine nothing is
// added; where the picture is bright, a soft dark glow is painted behind its lines (in the text's own background, so the
// words stay sharp; stronger over a bright spot) and quiet grey text comes up toward white. Measured again whenever the background changes.
// Part of the "Text shadow" switch; text elsewhere (menus, login) keeps the plain light halo.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Selene
{
    public static class AdaptiveText
    {
        private static readonly List<WeakReference> texts = new List<WeakReference>();
        // the dark halo behind bright-backed text (on: the darkness taken off was the screen-edge shading)
        private const bool HaloOn = true;
        private static bool hooked;
        private static DispatcherTimer batch;

        // text on the home or in Collection (over the art): true when it is taken care of here
        public static bool Track(TextBlock t)
        {
            if (t == null || !OverTheArt(t)) return false;
            Hook();
            if (!texts.Any(r => ReferenceEquals(r.Target, t)))
            {
                texts.Add(new WeakReference(t));
                t.IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) Soon(); };
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(t, (s, e) => Soon());
            }
            Soon();
            return true;
        }

        // a block of text (the empty-tab message...): every piece of text in it
        public static void Watch(FrameworkElement block, Func<ImageSource> unused)
        {
            if (block == null) return;
            Hook();
            Action all = () => { foreach (var t in Texts(block)) Track(t); };
            block.IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) block.Dispatcher.BeginInvoke(all, DispatcherPriority.Loaded); };
            block.Dispatcher.BeginInvoke(all, DispatcherPriority.Loaded);
        }

        public static void Refresh(FrameworkElement block) { Soon(); }
        public static void RefreshAll() { Soon(); }

        private static void Hook()
        {
            if (hooked) return;
            hooked = true;
            Contrast.Changed += Soon;
            batch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
            batch.Tick += (s, e) => { batch.Stop(); MeasureAll(); };
        }

        // measured in one go a moment later (a background change and many texts showing at once cost one pass)
        private static void Soon()
        {
            if (batch == null) return;
            batch.Stop();
            batch.Start();
        }

        private static void MeasureAll()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { MeasureAllNow(); }
            finally { if (sw.ElapsedMilliseconds > 8) HomeNav.Log("readable: measured in " + sw.ElapsedMilliseconds + " ms"); }
        }

        private static void MeasureAllNow()
        {
            texts.RemoveAll(r => !(r.Target is TextBlock));
            foreach (var r in texts.ToList())
            {
                var t = r.Target as TextBlock;
                if (t != null && t.IsVisible) Measure(t);
            }
        }

        private static void Measure(TextBlock t)
        {
            try
            {
                if (t.Effect is DropShadowEffect) t.Effect = null;   // (the earlier halo made the text itself soft)
                var fg = t.Foreground as SolidColorBrush;
                if (fg != null && (0.2126 * fg.Color.R + 0.7152 * fg.Color.G + 0.0722 * fg.Color.B) / 255.0 < 0.5) { Plain(t); return; }   // dark text: no dark halo
                if (!Features.On("textshadow")) { Plain(t); return; }
                double bright = BrightnessBehind(t, PictureBehind(t)) * (1 - ShadingBehind(t));   // as it shows, under the home's shading
                // 0 = reads fine .. 1 = bright behind it
                double k = Math.Max(0, Math.Min(1, (bright - 0.32) / 0.40));
                var q = quiet.GetValue(t, x => { var b = x.Foreground as SolidColorBrush; return new Quiet { Opacity = b != null && b.Opacity < 0.8 ? b.Opacity : -1 }; });
                double was = q.Opacity;
                if (was >= 0) t.Foreground = Ink.Brush("SeleneInkColor", was + (0.95 - was) * k);
                if (k < 0.08 || !HaloOn) { Plain(t); return; }   // readable as it is (or the halo off): nothing added
                Halo(t, 8 + 18 * k, 0.35 + 0.4 * k, 1 + (int)Math.Round(k * k));   // a light touch, a little more over brighter spots
            }
            catch (Exception ex) { HomeNav.Log("readable: " + ex.Message); }
        }

        // the text's layout before any room was made for its glow
        private sealed class Layout { public Thickness Padding, Margin; public double Width, MaxWidth, Height, MaxHeight; public bool Roomy; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, Layout> layouts = new System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, Layout>();

        private static Layout LayoutOf(TextBlock t)
        {
            return layouts.GetValue(t, x => new Layout { Padding = x.Padding, Margin = x.Margin, Width = x.Width, MaxWidth = x.MaxWidth, Height = x.Height, MaxHeight = x.MaxHeight });
        }

        // no glow: the text as it was
        private static void Plain(TextBlock t)
        {
            Layout o;
            if (!layouts.TryGetValue(t, out o) || !o.Roomy) { if (t.Background is DrawingBrush) t.Background = null; return; }
            t.Background = null;
            t.Padding = o.Padding; t.Margin = o.Margin; t.Width = o.Width; t.MaxWidth = o.MaxWidth; t.Height = o.Height; t.MaxHeight = o.MaxHeight;
            o.Roomy = false;
        }

        // the soft dark halo around the letters (a blurred dark copy of the text), drawn once into a picture that is the text's
        // own background: the words themselves get no effect, so they stay sharp (an effect on the text made it look blurry).
        // The text gets room around it for the halo (padding), given back by a margin of the same size, so nothing moves.
        private static void Halo(TextBlock t, double blur, double opacity, int layers)
        {
            var o = LayoutOf(t);
            double pad = Math.Ceiling(blur + 4);
            if (!o.Roomy || Math.Abs(t.Padding.Left - o.Padding.Left - pad) > 0.5)
            {
                if (o.Roomy) Plain(t);
                t.Padding = new Thickness(o.Padding.Left + pad, o.Padding.Top + pad, o.Padding.Right + pad, o.Padding.Bottom + pad);
                t.Margin = new Thickness(o.Margin.Left - pad, o.Margin.Top - pad, o.Margin.Right - pad, o.Margin.Bottom - pad);
                if (!double.IsNaN(o.Width)) t.Width = o.Width + 2 * pad;
                if (!double.IsInfinity(o.MaxWidth)) t.MaxWidth = o.MaxWidth + 2 * pad;
                if (!double.IsNaN(o.Height)) t.Height = o.Height + 2 * pad;
                if (!double.IsInfinity(o.MaxHeight)) t.MaxHeight = o.MaxHeight + 2 * pad;
                o.Roomy = true;
                t.UpdateLayout();
            }
            double W = t.ActualWidth, H = t.ActualHeight;
            if ((t.Text ?? "").Trim().Length == 0 || W < 2 || H < 2) { t.Background = null; return; }
            // the copy: same words, same font and wrapping, in the shade color, blurred
            var copy = new TextBlock
            {
                Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontWeight = t.FontWeight, FontStyle = t.FontStyle, FontStretch = t.FontStretch,
                TextAlignment = t.TextAlignment, TextWrapping = t.TextWrapping, TextTrimming = t.TextTrimming, LineHeight = t.LineHeight,
                LineStackingStrategy = t.LineStackingStrategy, FlowDirection = t.FlowDirection,
                Foreground = new SolidColorBrush(Ink.Of("SeleneScrimColor")), Opacity = opacity,
                Margin = t.Padding, Width = Math.Max(1, W - t.Padding.Left - t.Padding.Right),
                Effect = new BlurEffect { Radius = blur, RenderingBias = RenderingBias.Quality, KernelType = KernelType.Gaussian }
            };
            var host = new Grid { Width = W, Height = H };
            host.Children.Add(copy);
            // over a very bright spot a single blurred copy of thin letters is too faint: up to three, stacked
            for (int i = 1; i < layers; i++)
                host.Children.Add(new TextBlock
                {
                    Text = copy.Text, FontFamily = copy.FontFamily, FontSize = copy.FontSize, FontWeight = copy.FontWeight, FontStyle = copy.FontStyle, FontStretch = copy.FontStretch,
                    TextAlignment = copy.TextAlignment, TextWrapping = copy.TextWrapping, TextTrimming = copy.TextTrimming, LineHeight = copy.LineHeight,
                    LineStackingStrategy = copy.LineStackingStrategy, FlowDirection = copy.FlowDirection, Foreground = copy.Foreground, Opacity = opacity,
                    Margin = copy.Margin, Width = copy.Width, Effect = new BlurEffect { Radius = blur * (1 + 0.35 * i), RenderingBias = RenderingBias.Quality, KernelType = KernelType.Gaussian }
                });
            host.Measure(new Size(W, H));
            host.Arrange(new Rect(0, 0, W, H));
            host.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(t);
            var pic = new RenderTargetBitmap((int)Math.Ceiling(W * dpi.DpiScaleX), (int)Math.Ceiling(H * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            pic.Render(host);
            pic.Freeze();
            var brush = new ImageBrush(pic) { Stretch = Stretch.Fill };
            brush.Freeze();
            t.Background = brush;
        }

        // each text's own quietness (a grey hint's opacity; -1 for full-strength text), remembered before it is brightened
        private sealed class Quiet { public double Opacity; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, Quiet> quiet = new System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, Quiet>();

        // how much the home's own shading (the left gradient, the top and bottom) already darkens the spot behind a text on the home
        private static double ShadingBehind(TextBlock t)
        {
            for (DependencyObject d = t; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var fe = d as FrameworkElement;
                if (fe == null) continue;
                if (fe.Name == "SeleneLibrary") return 0;
                if (fe.Name != "WelcomeHubOverlay") continue;
                var main = Application.Current.MainWindow;
                var root = main != null ? main.Content as FrameworkElement : null;
                if (root == null || root.ActualWidth < 1 || !t.IsDescendantOf(root)) return 0;
                var r = t.TransformToVisual(root).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                return Contrast.ShadeAt((r.Left + r.Width / 2) / root.ActualWidth, (r.Top + r.Height / 2) / root.ActualHeight);
            }
            return 0;
        }

        // the picture behind a text: Collection's own (the wallpaper while it has no games), else the home's
        private static ImageSource PictureBehind(TextBlock t)
        {
            for (DependencyObject d = t; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var fe = d as FrameworkElement;
                if (fe == null || fe.Name != "SeleneLibrary") continue;
                if (HomeNav.CollectionEmpty()) return Contrast.WallpaperImage;
                var art = HomeNav.FindByName(fe, "PART_ImageBackground");
                var src = art != null ? art.GetType().GetProperty("Source") : null;
                var img = src != null ? src.GetValue(art, null) as ImageSource : null;
                return img ?? Contrast.WallpaperImage;
            }
            return Contrast.Behind();
        }

        // on the home (Spotlight, its top bar) or in Collection
        private static bool OverTheArt(DependencyObject t)
        {
            for (DependencyObject d = t; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var fe = d as FrameworkElement;
                if (fe == null) continue;
                if (fe.Name == "WelcomeHubOverlay" || fe.Name == "SeleneLibrary") return true;
                if (fe.Name == "AcceuilSettings" || fe is Window) return false;
            }
            return false;
        }

        private static IEnumerable<TextBlock> Texts(DependencyObject root)
        {
            var t = root as TextBlock;
            if (t != null) { yield return t; yield break; }
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                foreach (var c in Texts(VisualTreeHelper.GetChild(root, i))) yield return c;
        }

        // how bright (0..1, the brighter parts weighing most) the picture is right behind `el`, the picture filling the screen
        // the way the theme shows it (UniformToFill: centred, the overflow cut off)
        public static double BrightnessBehind(FrameworkElement el, ImageSource img)
        {
            var src = img as BitmapSource;
            var main = Application.Current.MainWindow;
            var root = main != null ? main.Content as FrameworkElement : null;
            if (src == null || root == null || root.ActualWidth < 1 || el.ActualWidth < 1) return 0;
            if (!el.IsDescendantOf(root)) return 0;
            var r = el.TransformToVisual(root).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            double W = root.ActualWidth, H = root.ActualHeight;
            double ia = (double)src.PixelWidth / src.PixelHeight, sa = W / H;
            double fx = 1, fy = 1, ox = 0, oy = 0;
            if (ia > sa) { fx = sa / ia; ox = (1 - fx) / 2; } else { fy = ia / sa; oy = (1 - fy) / 2; }
            double u0 = ox + Math.Max(0, r.Left / W) * fx, u1 = ox + Math.Min(1, r.Right / W) * fx;
            double v0 = oy + Math.Max(0, r.Top / H) * fy, v1 = oy + Math.Min(1, r.Bottom / H) * fy;
            if (u1 <= u0 || v1 <= v0) return 0;
            int x = (int)(u0 * src.PixelWidth), y = (int)(v0 * src.PixelHeight);
            int w = Math.Max(1, (int)((u1 - u0) * src.PixelWidth)), h = Math.Max(1, (int)((v1 - v0) * src.PixelHeight));
            if (x + w > src.PixelWidth) w = src.PixelWidth - x;
            if (y + h > src.PixelHeight) h = src.PixelHeight - y;
            if (w < 1 || h < 1) return 0;
            BitmapSource part = new CroppedBitmap(src, new Int32Rect(x, y, w, h));
            double scale = Math.Min(1, 96.0 / Math.Max(w, h));   // fine bright detail stays visible
            if (scale < 1) part = new TransformedBitmap(part, new ScaleTransform(scale, scale));
            part = new FormatConvertedBitmap(part, PixelFormats.Bgra32, null, 0);
            int pw = part.PixelWidth, ph = part.PixelHeight;
            var px = new byte[pw * ph * 4];
            part.CopyPixels(px, pw * 4, 0);
            var lums = new List<double>(pw * ph);
            for (int i = 0; i < px.Length; i += 4) lums.Add((0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) / 255.0);
            lums.Sort();
            return lums[Math.Min(lums.Count - 1, (int)(lums.Count * 0.92))];   // the brightest bits behind the words decide
        }
    }
}
