using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace Selene
{
    // Left alone for a while (the Idle screen setting), Selene dims and drifts through your games: each one's art, slowly
    // zooming, crossfading to the next every few seconds, with its logo (or name) and a line about it at the bottom left,
    // the clock and date at the bottom right; now and then one of your own screenshots instead. Anywhere in Selene (the
    // home, Collection, menus, settings...), never over a running or starting game, a playing clip, or when Playnite isn't
    // in front. Any button wakes it, and that press does nothing else.
    public static class IdleScreen
    {
        private class Slide { public string Picture, Logo, Title, Status; public List<string[]> Figures = new List<string[]>(); }   // Figures: label, value

        private static DateTime lastTouch = DateTime.Now;
        private static DispatcherTimer watch, turn, tick;
        private static Grid layer;
        private static Image front, back;
        private static Grid captionA, captionB;
        private static TextBlock clock, date;
        private static List<Slide> slides = new List<Slide>();
        private static int at;
        private static readonly Random random = new Random();
        public static bool Showing { get; private set; }
        private static bool devPretend;

        public static void Install()
        {
            if (watch != null) return;
            watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            watch.Tick += (s, e) => Check();
            watch.Start();
        }

        // a button went down: not idle (and the idle screen, if up, goes away)
        public static void Touched()
        {
            lastTouch = DateTime.Now;
            if (Showing) Hide();
        }

        public static void Restart() { lastTouch = DateTime.Now; if (Showing) Hide(); }

        private static void Check()
        {
            try
            {
                double minutes = Features.Number("idle");
                var dev = Path.Combine(Path.GetTempPath(), "selene-dev-idle");   // developer switch: after 10 s, once
                bool pretend = File.Exists(dev);
                if (pretend) minutes = 10 / 60.0;
                if (Showing || minutes <= 0 || (DateTime.Now - lastTouch).TotalMinutes < minutes) return;
                if (HomeNav.Launching || HomeNav.GameRunning() || HomeNav.ClipIsPlaying || ProgressToast.Busy) return;
                var main = Application.Current.MainWindow;
                if (main == null || !main.IsActive || main.WindowState == WindowState.Minimized) return;
                var host = HomeNav.FindByName(main, "PART_MainHost") as Grid;
                if (host == null) return;
                if (pretend) try { File.Delete(dev); } catch { }
                devPretend = pretend;
                Show(host);
            }
            catch (Exception ex) { HomeNav.Log("idle: " + ex.Message); }
        }

        // what the idle screen goes through: the games with art (shuffled), a recent screenshot of your own every fourth turn
        private static List<Slide> Gather()
        {
            var api = HomeNav.Api;
            var list = new List<Slide>();
            if (api != null)
            {
                foreach (var g in api.Database.Games.Where(g => GameFacts.BackgroundPath(g) != null).OrderBy(g => random.Next()).Take(40))
                {
                    var logo = Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", g.Id.ToString(), "logo.png");
                    var secs = GameFacts.Seconds(g);
                    var last = GameFacts.LastPlayed(g);
                    var slide = new Slide { Picture = GameFacts.BackgroundPath(g), Logo = File.Exists(logo) ? logo : null, Title = g.Name, Status = g.IsInstalled ? "Installed" : "Not installed" };
                    if (secs > 0) slide.Figures.Add(new[] { "PLAYTIME", GameFacts.Hours(secs) });
                    if (last.HasValue) slide.Figures.Add(new[] { "LAST PLAYED", GameFacts.Ago(last) });
                    if (slide.Figures.Count == 0) slide.Figures.Add(new[] { "PLAYTIME", "Not played yet" });
                    list.Add(slide);
                }
            }
            // your own screenshots: with the game's logo too, when it's one of yours (a capture only knows the game's name)
            var shots = CapturesScreen.Known.Where(c => !c.IsVideo && File.Exists(c.Path)).OrderByDescending(c => c.When).Take(8).ToList();
            for (int i = 0, k = 3; i < shots.Count && k <= list.Count; i++, k += 4)
            {
                string shotLogo = null;
                var game = api != null ? api.Database.Games.FirstOrDefault(g => string.Equals(g.Name, shots[i].Game, StringComparison.OrdinalIgnoreCase)) : null;
                if (game != null) { var lp = Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", game.Id.ToString(), "logo.png"); if (File.Exists(lp)) shotLogo = lp; }
                list.Insert(k, new Slide { Picture = shots[i].Path, Logo = shotLogo, Title = shots[i].Game, Status = "Your screenshot", Figures = { new[] { "TAKEN", shots[i].When.ToString("MMMM d, yyyy") } } });
            }
            // (the developer switch: a screenshot first, to see its words without waiting for the fourth turn)
            if (devPretend) { int first = list.FindIndex(x => x.Status == "Your screenshot"); if (first > 0) { var sh = list[first]; list.RemoveAt(first); list.Insert(0, sh); } }
            if (list.Count == 0)
            {
                var w = Wallpapers.Current;
                if (!string.IsNullOrEmpty(w)) list.Add(new Slide { Picture = w, Title = "" });
            }
            return list;
        }

        private static void Show(Grid host)
        {
            slides = Gather();
            if (slides.Count == 0) return;
            Showing = true;
            HomeNav.Log("idle: screen up (" + slides.Count + " pictures)");
            layer = new Grid { Background = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
            Grid.SetRowSpan(layer, 99);
            Grid.SetColumnSpan(layer, 99);
            Panel.SetZIndex(layer, 9000);
            back = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
            front = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
            foreach (var img in new[] { back, front }) { RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality); img.CacheMode = new BitmapCache(1.0); layer.Children.Add(img); }
            // a dim over all of it, deeper along the bottom where the words are (everything over the pictures sits above their
            // z-order: the pictures are swapped between 1 and 2, and the words at 0 slipped under them)
            var dim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x5C, 0, 0, 0)) };
            Panel.SetZIndex(dim, 10);
            layer.Children.Add(dim);
            var bottomShade = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 420, Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0xB8, 0, 0, 0), 1) }, 90) };
            Panel.SetZIndex(bottomShade, 11);
            layer.Children.Add(bottomShade);
            captionA = new Grid { Opacity = 0 }; captionB = new Grid { Opacity = 0 };
            Panel.SetZIndex(captionA, 12); Panel.SetZIndex(captionB, 12);
            layer.Children.Add(captionA); layer.Children.Add(captionB);
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 80, 64) };
            clock = new TextBlock { FontSize = 64, FontFamily = Ui.Display, FontWeight = FontWeights.Light, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Right };
            date = new TextBlock { FontSize = 20, FontFamily = Ui.Body, Foreground = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 2, 0) };
            right.Children.Add(clock); right.Children.Add(date);
            Panel.SetZIndex(right, 13);
            layer.Children.Add(right);
            host.Children.Add(layer);
            at = -1;
            Tick();
            Next();
            layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1600)) { EasingFunction = Ui.Out() });
            turn = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            turn.Tick += (s, e) => Next();
            turn.Start();
            tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            tick.Tick += (s, e) => Tick();
            tick.Start();
        }

        private static void Tick()
        {
            if (clock == null) return;
            clock.Text = DateTime.Now.ToString("h:mm tt");
            date.Text = DateTime.Now.ToString("dddd, MMMM d");
        }

        // the next picture fades in over the last, drifting a little larger as it shows; its words come with it
        private static void Next()
        {
            if (!Showing || slides.Count == 0) return;
            at = (at + 1) % slides.Count;
            var slide = slides[at];
            var pic = Wallpapers.Load(slide.Picture, 1920);
            if (pic == null) { if (slides.Count > 1) { slides.RemoveAt(at); at--; Next(); } return; }
            var old = front; front = back; back = old;
            Panel.SetZIndex(front, 2); Panel.SetZIndex(back, 1);
            front.Source = pic;
            var zoom = new ScaleTransform(1.0, 1.0);
            front.RenderTransform = zoom;
            var slow = TimeSpan.FromSeconds(14);
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.08, slow));
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.08, slow));
            front.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1800)));
            back.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1800)));
            // the words: logo or name, a line under it, rising in as the old ones fade
            var oldCap = captionA; captionA = captionB; captionB = oldCap;
            captionA.Children.Clear();
            var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(80, 0, 0, 64), RenderTransform = new TranslateTransform(0, 14) };
            ImageSource logo = slide.Logo != null ? Wallpapers.Load(slide.Logo, 600) : null;
            if (logo != null) words.Children.Add(new Image { Source = logo, MaxWidth = 520, MaxHeight = 170, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 16, Opacity = 0.6, Color = Colors.Black } });
            else if (!string.IsNullOrEmpty(slide.Title)) words.Children.Add(new TextBlock { Text = slide.Title, FontSize = 46, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, MaxWidth = 900, TextTrimming = TextTrimming.CharacterEllipsis });
            // under it: a small pill with the state, then the figures as small caps labels over their values (the session card's look)
            var facts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 18, 0, 0) };
            if (!string.IsNullOrEmpty(slide.Status))
                facts.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 3, 12, 4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 26, 0),
                    Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = slide.Status, FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) }
                });
            for (int i = 0; i < slide.Figures.Count; i++)
            {
                if (i > 0) facts.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 4, 22, 4) });
                var fig = new StackPanel { Margin = new Thickness(0, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
                fig.Children.Add(new TextBlock { Text = slide.Figures[i][0], FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) });
                fig.Children.Add(new TextBlock { Text = slide.Figures[i][1], FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Brushes.White, Margin = new Thickness(0, 1, 0, 0) });
                facts.Children.Add(fig);
            }
            if (facts.Children.Count > 0) words.Children.Add(facts);
            captionA.Children.Add(words);
            captionA.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(500), EasingFunction = Ui.Out() });
            ((TranslateTransform)words.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(1100)) { BeginTime = TimeSpan.FromMilliseconds(500), EasingFunction = Ui.Out() });
            captionB.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));
        }

        private static void Hide()
        {
            Showing = false;
            if (turn != null) { turn.Stop(); turn = null; }
            if (tick != null) { tick.Stop(); tick = null; }
            var l = layer;
            layer = null;
            if (l == null) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)) { EasingFunction = Ui.Out() };
            fade.Completed += (s, e) => { var p = l.Parent as Panel; if (p != null) p.Children.Remove(l); };
            l.BeginAnimation(UIElement.OpacityProperty, fade);
            HomeNav.Log("idle: woke");
        }
    }
}
