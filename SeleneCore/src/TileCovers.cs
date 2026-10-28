using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Playnite.SDK.Models;

namespace Selene
{
    // Covers decoded off the UI thread. Playnite's cover converter decoded each cover whole on the UI thread while a new
    // tile's template was built: ~15 ms a cover (measured on a real library; the stack sampler showed it inside Spotlight's
    // template), so holding a direction stuttered once per new tile. Spotlight's tiles have no binding for their cover and
    // square art: they're filled here. Collection's grid binding is let go when a tile loads. Each picture is decoded at the
    // size it's drawn on a worker (newest request first: the tiles just scrolled to), cropped there too (CoverCrop), and
    // faded in. Switch: Advanced > Load covers in the background (off: decoded on the UI thread, as before).
    public static class TileCovers
    {
        private const int CacheSize = 120;   // ~1 MB each at tile size: about 120 MB at most (Playnite is a 32-bit process)
        private static readonly Dictionary<string, BitmapSource> cache = new Dictionary<string, BitmapSource>();
        private static readonly LinkedList<string> recent = new LinkedList<string>();
        private static readonly ConditionalWeakTable<FrameworkElement, string> wanted = new ConditionalWeakTable<FrameworkElement, string>();
        private static readonly ConditionalWeakTable<FrameworkElement, object> hooked = new ConditionalWeakTable<FrameworkElement, object>();
        private static readonly Stack<Request> queue = new Stack<Request>();
        private static readonly object gate = new object();
        private static bool working, background;
        private static int decoded;

        private sealed class Request { public FrameworkElement Target; public string Path, Key; public int Width; }

        public static void Install()
        {
            background = Features.On("asynccovers");
            EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
            EventManager.RegisterClassHandler(typeof(Image), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
            HomeNav.Log("covers: decoded " + (background ? "in the background" : "on the UI thread"));
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var el = sender as FrameworkElement;
            if (el == null || (el.Name != "SeleneCoverFace" && el.Name != "SeleneSquareFace" && el.Name != "PART_ImageCover")) return;
            if (el.Name == "PART_ImageCover" && !background) return;   // (Playnite's binding as before)
            object seen;
            if (!hooked.TryGetValue(el, out seen))
            {
                hooked.Add(el, el);
                el.DataContextChanged += (s, a) => Load(el);   // a reused tile: its new game's cover
                var square = SquarePathOf(el);   // the square art's file name follows the media folder setting
                if (square != null) System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(square, (s, a) => Load(el));
            }
            Load(el);
        }

        private static void Load(FrameworkElement el)
        {
            try
            {
                var path = el.Name == "SeleneSquareFace" ? SquareFile(el) : PathOf(el.DataContext);
                if (path == null) { wanted.Remove(el); if (el.Name != "PART_ImageCover") Show(el, null); return; }   // (no picture: the square art's place stays clear over the cover)
                // the size it's drawn at, in pixels (the cover stretches to the tile's width)
                var source = PresentationSource.FromVisual(el);
                double scale = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
                double w = el.ActualWidth > 0 ? el.ActualWidth : el.Width > 0 ? el.Width : 200;
                int width = (int)Math.Min(900, Math.Ceiling(w * scale / 16) * 16);   // (rounded up: one cached picture serves nearby sizes)
                var key = path + "|" + width;
                wanted.Remove(el);
                wanted.Add(el, key);
                BitmapSource ready;
                if (cache.TryGetValue(key, out ready)) { Set(el, ready, false); Touch(key); return; }
                Release(el);
                if (!background) { var now = Decode(path, width); if (now != null) { Remember(key, now); Set(el, now, false); } return; }
                lock (gate)
                {
                    queue.Push(new Request { Target = el, Path = path, Key = key, Width = width });
                    if (working) return;
                    working = true;
                }
                ThreadPool.QueueUserWorkItem(_ => Work(el.Dispatcher));
            }
            catch (Exception ex) { HomeNav.Log("covers: " + ex.Message); }
        }

        // the tile no longer follows Playnite's binding (it would decode the cover on the UI thread again)
        private static void Release(FrameworkElement el)
        {
            var image = el as Image;
            if (image != null) { BindingOperations.ClearBinding(image, Image.SourceProperty); image.Source = null; return; }
            var brush = ((Border)el).Background as ImageBrush;
            if (brush == null) return;
            if (brush.IsFrozen) { brush = brush.Clone(); ((Border)el).Background = brush; }
            BindingOperations.ClearBinding(brush, ImageBrush.ImageSourceProperty);
            brush.ImageSource = null;
        }

        private static void Work(System.Windows.Threading.Dispatcher ui)
        {
            while (true)
            {
                Request r;
                lock (gate)
                {
                    if (queue.Count == 0) { working = false; return; }
                    r = queue.Pop();
                }
                if (!StillWanted(r)) continue;   // scrolled past before its turn
                var bmp = Decode(r.Path, r.Width);
                if (bmp == null) continue;
                if (System.Threading.Interlocked.Increment(ref decoded) % 50 == 1) HomeNav.Log("covers: " + decoded + " decoded (" + r.Width + " px wide)");
                var done = r; var picture = bmp;
                ui.BeginInvoke(new Action(() =>
                {
                    Remember(done.Key, picture);
                    if (Wants(done.Target, done.Key)) Set(done.Target, picture, true);
                }));
            }
        }

        private static BitmapSource Decode(string path, int width)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
                bi.UriSource = new Uri(path);
                bi.DecodePixelWidth = width;
                bi.EndInit();
                bi.Freeze();
                CoverCrop.Prepare(bi);   // its square window worked out here too
                return bi;
            }
            catch (Exception ex) { HomeNav.Log("covers: " + path + ": " + ex.Message); return null; }
        }

        // the square art's file for this tile (the theme's hidden SquarePath next to it), when there is one
        private static TextBlock SquarePathOf(FrameworkElement el)
        {
            if (el.Name != "SeleneSquareFace") return null;
            var grid = el.Parent as Panel;
            return grid != null ? grid.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == "SquarePath") : null;
        }

        private static string SquareFile(FrameworkElement el)
        {
            var t = SquarePathOf(el);
            var file = t != null ? t.Text : null;
            return !string.IsNullOrEmpty(file) && System.IO.File.Exists(file) ? file : null;
        }

        private static void Show(FrameworkElement el, BitmapSource picture)
        {
            var brush = ((Border)el).Background as ImageBrush;
            if (brush == null) return;
            if (brush.IsFrozen) { brush = brush.Clone(); ((Border)el).Background = brush; }
            brush.ImageSource = picture;
        }

        private static bool StillWanted(Request r)
        {
            // read off the UI thread: a stale answer only means one extra decode
            string key;
            return wanted.TryGetValue(r.Target, out key) && key == r.Key;
        }

        private static bool Wants(FrameworkElement el, string key) { string k; return wanted.TryGetValue(el, out k) && k == key; }

        private static void Set(FrameworkElement el, BitmapSource picture, bool fade)
        {
            var image = el as Image;
            if (image != null) { BindingOperations.ClearBinding(image, Image.SourceProperty); image.Source = picture; }
            else
            {
                var border = (Border)el;
                var brush = border.Background as ImageBrush;
                if (brush == null) return;
                if (brush.IsFrozen) { brush = brush.Clone(); border.Background = brush; }
                BindingOperations.ClearBinding(brush, ImageBrush.ImageSourceProperty);
                brush.ImageSource = picture;
                // the cover's square window (worked out on the worker): applied here, since the brush may be a fresh copy that
                // CoverCrop never hooked (a template brush without a binding is frozen and shared, so it's copied above)
                if (border.Name == "SeleneCoverFace") CoverCrop.Apply(brush);
            }
            if (!fade) { el.BeginAnimation(UIElement.OpacityProperty, null); return; }
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private static void Remember(string key, BitmapSource picture)
        {
            cache[key] = picture;
            Touch(key);
            while (recent.Count > CacheSize) { cache.Remove(recent.Last.Value); recent.RemoveLast(); }
        }

        private static void Touch(string key) { recent.Remove(key); recent.AddFirst(key); }

        private static string PathOf(object item)
        {
            var game = item as Game;
            if (game == null && item != null)
            {
                var prop = item.GetType().GetProperty("Game");
                game = prop != null ? prop.GetValue(item, null) as Game : null;
            }
            return GameFacts.CoverPath(game);
        }
    }
}
