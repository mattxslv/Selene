using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Selene
{
    // Making (or editing) a profile: name -> avatar -> theme -> background. Theme and background show the real home
    // live beside the choices. B goes back a step (from the first step: back where it came from, look restored).
    public class ProfileSteps : Screen
    {
        public override bool MenuOnStart { get { return false; } }   // (Start is its own here)
        private readonly Profile editing;
        private readonly Func<Screen> back;
        private readonly Action<Profile> done;
        internal string Name, Avatar, Theme, Wallpaper, Look;
        private readonly Grid body = new Grid();
        private readonly StackPanel dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 58) };
        private readonly TextBlock nextLabel;
        private readonly List<UIElement> chrome = new List<UIElement>();   // prompts and dots, hidden while a picture is framed
        private readonly Step[] steps;
        private int step, firstStep;
        private bool saved;

        // startAt: the step to open on (2 = theme, for the menu's Theme; B there goes back where it came from)
        public ProfileSteps(Profile editing, Func<Screen> back, Action<Profile> done = null, int startAt = 0)
        {
            firstStep = startAt;
            this.editing = editing;
            this.back = back;
            this.done = done ?? (p => LoginScene.Show(new WhoScreen(p.Id), 1));
            var active = Profiles.Active;
            Name = editing != null ? editing.Name : "";
            Avatar = editing != null ? editing.Avatar : null;
            Theme = editing != null && !string.IsNullOrEmpty(editing.Theme) ? editing.Theme : ThemePalettes.DefaultId;
            Wallpaper = editing != null ? editing.Wallpaper : null;
            Look = editing != null && editing.HomeBackground == "wallpaper" ? "wallpaper" : "art";
            previewTheme = null;
            previewLook = null;
            steps = new Step[] { new NameStep(this), new AvatarStep(this), new ThemeStep(this), new BackgroundStep(this) };
            Root.Children.Add(body);
            for (int i = 0; i < steps.Length; i++)
                dots.Children.Add(new Border { Height = 8, Width = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(5, 0, 5, 0), Background = Ink.Brush("SeleneInkColor", 0.3) });
            Root.Children.Add(dots);
            chrome.Add(dots);
            if (startAt > 0) dots.Opacity = 0;   // opened from the menu on one step (Change theme): no step dots
            var backHint = Ui.Prompts(false, Ui.Prompt("B", "Back"));
            chrome.Add(backHint);
            Root.Children.Add(backHint);
            var next = Ui.Prompt("A", "Next");
            nextLabel = (TextBlock)next.Children[1];
            var nextHint = Ui.Prompts(true, next);
            chrome.Add(nextHint);
            Root.Children.Add(nextHint);
        }

        internal void ShowChrome(bool on)
        {
            foreach (var c in chrome) Ui.Fade(c, on ? 1 : 0, 160);
        }

        public override bool WantsText { get { return steps[step].WantsText; } }
        public override void Type(string text) { steps[step].Type(text); }
        public override void Erase() { steps[step].Erase(); }

        public override void Shown()
        {
            step = Math.Max(0, Math.Min(firstStep, steps.Length - 1));
            body.Children.Add(steps[step].Root);
            steps[step].Shown();
            ShowDots();
        }

        public override void Hidden()
        {
            HomeNav.ShowArtForPreview(false);
            // left without saving (B out of the first step, or the layer closed): the signed-in profile's look comes back
            if (!saved) Profiles.ApplyLook();
        }

        private void ShowDots()
        {
            for (int i = 0; i < dots.Children.Count; i++)
            {
                var d = (Border)dots.Children[i];
                d.Background = Ink.Brush("SeleneInkColor", i == step ? 1 : 0.3);
                d.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(i == step ? 24 : 8, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ui.Out() });
            }
            nextLabel.Text = steps[step].WantsText ? "Select" : step < steps.Length - 1 ? "Next" : editing != null ? "Save" : "Create profile";
        }

        public override void Handle(string b)
        {
            var s = steps[step];
            if (b == "B")
            {
                if (s.OnB()) return;
                Ui.Back();
                if (step <= firstStep) { HomeNav.ShowArtForPreview(false); if (back == null) LoginScene.Finish(); else LoginScene.Show(back(), -1); return; }
                GoTo(step - 1, -1);
                return;
            }
            if (b == "A") { if (!s.OnA()) Next(); return; }
            if (b == "Start") { Next(); return; }
            if (b.StartsWith("DPad")) { s.Move(b); return; }
            s.Extra(b);
        }

        internal void Next()
        {
            if (!steps[step].Accept()) return;
            if (step < steps.Length - 1) { Ui.Confirm(); GoTo(step + 1, 1); return; }
            Save();
        }

        // the next step is built first (its grid, pictures, preview), then both pages slide, each as one cached picture: built
        // during the slide, the work made the slide stutter (choosing a theme, on to the background)
        private void GoTo(int i, int dir)
        {
            // a slide still running: done at once, only the page being left stays
            foreach (var page in body.Children.OfType<FrameworkElement>().ToList())
            {
                if (ReferenceEquals(page, steps[step].Root)) continue;
                page.BeginAnimation(UIElement.OpacityProperty, null);
                body.Children.Remove(page);
            }
            var old = steps[step].Root;
            steps[step].Left();
            old.IsHitTestVisible = false;
            step = i;
            var now = steps[step].Root;
            now.IsHitTestVisible = true;
            now.Opacity = 0;
            if (!body.Children.Contains(now)) body.Children.Add(now);
            Ui.HoldVisuals(380);   // the slide below: pictures, the backdrop and the preview change after it
            steps[step].Shown();
            ShowDots();
            var oldStep = old;
            body.Dispatcher.BeginInvoke(new Action(() =>
            {
                // (no page-wide cache: the pages animate inside while sliding (the focused tile, the chips, the grid), and each of those
                // redrew the whole cached page every frame; the tiles and the preview are cached on their own)
                Ui.Slide(oldStep, 0, -dir * 70, 1, 0, 220, () => body.Children.Remove(oldStep));
                Ui.Slide(now, dir * 70, 0, 0, 1, 300);
                Ui.MeasureFrames("profile step slide", 340);
            }), DispatcherPriority.Background);
        }

        private void Save()
        {
            saved = true;
            Profile p;
            if (editing != null)
            {
                editing.Name = Name; editing.Avatar = Avatar; editing.Theme = Theme; editing.Wallpaper = Wallpaper; editing.HomeBackground = Look;
                Profiles.Update(editing);
                p = editing;
            }
            else
            {
                p = Profiles.Create(Name, Avatar, Theme, Wallpaper);
                p.HomeBackground = Look;
                Profiles.Update(p);
            }
            HomeNav.ShowArtForPreview(false);
            Profiles.ApplyLook();
            Ui.Chime();
            HomeNav.Log("profiles: " + (editing != null ? "saved " : "created ") + p.Name + " (theme " + p.Theme + ")");
            done(p);
        }

        // ---------------- shared bits ----------------
        internal abstract class Step
        {
            protected readonly ProfileSteps Owner;
            public readonly Grid Root = new Grid();
            protected Step(ProfileSteps owner) { Owner = owner; }
            public abstract void Move(string dir);
            public virtual bool OnA() { return false; }      // true = handled here (stay on this step)
            public virtual bool OnB() { return false; }
            public virtual bool Accept() { return true; }    // false = can't go on yet
            public virtual void Extra(string button) { }
            public virtual void Shown() { }
            public virtual void Left() { }
            public virtual bool WantsText { get { return false; } }
            public virtual void Type(string text) { }
            public virtual void Erase() { }

            protected StackPanel Header(string title, string sub)
            {
                var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 110, 0, 0) };
                head.Children.Add(Ui.Title(title));
                head.Children.Add(Ui.Sub(sub));
                Root.Children.Add(head);
                return head;
            }
        }

        // a grid of tiles taller than its window: moves (glides) so the focused row stays in view
        internal class ScrollingGrid
        {
            public readonly Grid Viewport;
            public readonly UniformGrid Panel;
            private readonly TranslateTransform shift = new TranslateTransform();
            private readonly GradientStop topClear, topSolid;
            private readonly int columns;
            private readonly double rowHeight, height;

            public ScrollingGrid(int columns, double rowHeight, double height)
            {
                this.columns = columns; this.rowHeight = rowHeight; this.height = height;
                Panel = new UniformGrid { Columns = columns, VerticalAlignment = VerticalAlignment.Top, RenderTransform = shift, Margin = new Thickness(0, 20, 0, 0),
                                          CacheMode = new System.Windows.Media.BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = false } };   // one texture: the mask and the glide cost a blit, not a redraw of every tile
                // on a canvas: a panel taller than its slot would get clipped to the slot, and the clip would scroll with it
                Viewport = new Grid { Height = height, ClipToBounds = true };
                var canvas = new Canvas();
                canvas.Children.Add(Panel);
                Viewport.Children.Add(canvas);
                // rows melt away at the edges instead of being cut off (pixel positions: the panel inside is far taller)
                var mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, height) };
                topClear = new GradientStop(Color.FromArgb(0, 0, 0, 0), 0);
                topSolid = new GradientStop(Colors.Black, 0);   // moved down (a long fade, like the bottom) once rows scroll up
                mask.GradientStops.Add(topClear);
                mask.GradientStops.Add(topSolid);
                mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - 150 / height));
                mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
                Viewport.OpacityMask = mask;
            }

            private double offset;   // how far the grid is scrolled now

            public void Reveal(int index)
            {
                int rows = (Panel.Children.Count + columns - 1) / columns;
                double total = rows * rowHeight;
                Panel.Width = Viewport.Width;
                double rowTop = (index / columns) * rowHeight;
                // scrolls only when the focused row is out of view (keeping it one row from the top slid the whole grid on
                // every move between the first rows); the fades at the top (140) and bottom (150) count as out of view
                double cur = offset;
                double viewTop = cur + (cur > 0 ? 140 : 0), viewBottom = cur + height - 150;
                if (rowTop < viewTop) cur = rowTop <= rowHeight ? 0 : rowTop - 140;
                else if (rowTop + rowHeight > viewBottom) cur = rowTop + rowHeight - (height - 150);
                cur = Math.Max(0, Math.Min(cur, total + 20 - height + 110));   // the last row can rise above the fade
                if (total <= height) cur = 0;
                offset = cur;
                double y = -cur;
                // at the top nothing fades; scrolled, the row above melts away under the title like the bottom does
                topSolid.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(y < 0 ? 140 / height : 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ui.Out() });
                var glide = new DoubleAnimation(y, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ui.Out() };
                Timeline.SetDesiredFrameRate(glide, Ui.FrameRate);
                shift.BeginAnimation(TranslateTransform.YProperty, glide);
                Ui.MeasureFrames("profile grid glide", 320);
            }
        }

        // the real home (row and top bar; no game is selected, its song would start) as a picture, so a theme or background is
        // seen before picking it. A snapshot, retaken when the choice changes: a live mirror redrew the whole home on every
        // frame (its dust never stops moving) and the steps crawled.
        // every preview box (the theme step and the background step have one each: a single shared one left the theme step's
        // box empty, since the steps are all built up front and the last one took it)
        private sealed class PreviewBox { public Border Back, Front; public double Width; public int Version = -1; }
        private static readonly List<PreviewBox> previews = new List<PreviewBox>();

        internal static FrameworkElement Preview(double width)
        {
            double height = width * 9 / 16;
            var box = new PreviewBox { Width = width };
            var host = new Grid { Width = width, Height = height, Clip = new RectangleGeometry(new Rect(0, 0, width, height), 15, 15) };
            host.Children.Add(new Border { Background = Ink.Brush("SeleneGlassColor", 0.7) });
            box.Back = new Border();
            box.Front = new Border();
            host.Children.Add(box.Back);
            host.Children.Add(box.Front);
            previews.Add(box);
            box.Front.IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) RefreshPreviewLater(); };   // a step coming up: up to date
            var frame = new Border
            {
                Child = host, CornerRadius = new CornerRadius(16), BorderBrush = Ink.Brush("SeleneInkColor", 0.25), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 40, Opacity = 0.5, Color = Colors.Black },
                // the frame and its shadow as one picture: a step sliding moved it without blurring the shadow again each frame
                CacheMode = new BitmapCache(1.0)
            };
            var col = new StackPanel();
            col.Children.Add(new TextBlock { Text = "PREVIEW", FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), Margin = new Thickness(2, 0, 0, 10) });
            col.Children.Add(frame);
            RefreshPreviewLater();
            return col;
        }

        // the preview is drawn again (soon; after a slide) when the theme, the wallpaper or the look changed
        internal static void RefreshPreviewLater()
        {
            if (previews.Count == 0) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(() => Ui.AfterHold(() => RefreshPreview())), DispatcherPriority.Background);
        }

        private static bool RefreshPreview()
        {
            try
            {
                previews.RemoveAll(b => b.Front == null);
                var shown = previews.Where(b => b.Front.IsLoaded && b.Front.IsVisible && (b.Version != HomeNav.PreviewVersion || b.Front.Child == null)).ToList();
                foreach (var box in shown)
                {
                    // drawn once into a picture the size of the box: the slides and the step's own glides move a picture,
                    // not the 1920x1080 sample with its shadows (the steps fell under 60 fps)
                    var mock = new Viewbox { Stretch = Stretch.Fill, Child = SampleHome(), CacheMode = new BitmapCache(1.0) };
                    // the old drawing stays under the new one as it fades in: a soft change, not a cut
                    var old = box.Front.Child;
                    box.Front.Child = null;
                    box.Back.Child = old;
                    box.Front.Child = mock;
                    box.Version = HomeNav.PreviewVersion;
                    box.Front.BeginAnimation(UIElement.OpacityProperty, null);
                    box.Front.Opacity = 0;
                    Ui.Fade(box.Front, 1, 160);
                }
                return true;
            }
            catch (Exception ex) { HomeNav.Log("preview: " + ex.Message); return false; }
        }

        // ---- the default preview: a sample home drawn in the current theme (not a picture of the real home, which with no games
        // was mostly empty cards). Clair Obscur: Expedition 33 is the sample game; its art, logo and cover come from Steam once. ----
        // the theme the preview is drawn in: the one picked on the theme step (not applied until A), null = Selene's own now
        private static string previewTheme, previewWallpaper;
        private static string previewLook;   // the background step's Game art / Background only: the preview only, never the home
        private static Color PC(string key, double alpha = 1)
        {
            var v = previewTheme != null ? ThemePalettes.Peek(previewTheme, key) : Application.Current.TryFindResource(key);
            var c = v is Color ? (Color)v : Colors.White;
            c.A = (byte)Math.Round(c.A * alpha);
            return c;
        }
        private static SolidColorBrush PB(string key, double alpha = 1) { var b = new SolidColorBrush(PC(key, alpha)); b.Freeze(); return b; }

        private static FrameworkElement SampleHome()
        {
            var res = Application.Current.Resources;
            var root = new Grid { Width = 1920, Height = 1080, ClipToBounds = true };
            var baseFill = new Border();
            baseFill.Background = (previewTheme != null ? ThemePalettes.Peek(previewTheme, "SeleneBaseBrush") : res["SeleneBaseBrush"]) as Brush ?? PB("SeleneScrimColor");
            root.Children.Add(baseFill);
            var wall = previewTheme != null ? Wallpapers.Decoded(previewWallpaper) : res["SeleneWallpaper"] as ImageSource;
            if (wall != null) root.Children.Add(new Image { Source = wall, Stretch = Stretch.UniformToFill });
            var artOff = previewLook != null ? previewLook == "wallpaper" : res["SeleneArtOpacity"] is double && (double)res["SeleneArtOpacity"] < 0.05;
            var art = HomeNav.SamplePicture;
            if (art != null && !artOff) root.Children.Add(new Image { Source = art, Stretch = Stretch.UniformToFill });
            // the home's shading: from the left, the top and the bottom
            var scrim = PC("SeleneScrimColor");
            Func<double, Color> sa = a => { var c = scrim; c.A = (byte)(255 * a); return c; };
            root.Children.Add(new Border { Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(sa(0.8), 0), new GradientStop(sa(0.45), 0.5), new GradientStop(sa(0.1), 1) }, new Point(0, 0), new Point(1, 0)) });
            root.Children.Add(new Border { Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(sa(0.75), 0), new GradientStop(sa(0), 0.26) }, new Point(0, 0), new Point(0, 1)) });
            root.Children.Add(new Border { Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(sa(0), 0.55), new GradientStop(sa(0.8), 1) }, new Point(0, 0), new Point(0, 1)) });

            Func<string, double, double, TextBlock> text = (t, size, alpha) => new TextBlock { Text = t, FontSize = size, FontFamily = Ui.Display, Foreground = PB("SeleneInkColor", alpha), VerticalAlignment = VerticalAlignment.Center };
            // the top bar: the tabs (the first one picked) and the clock
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 34, 0, 0) };
            tabs.Children.Add(Badge("LB"));
            var names = new[] { "All games", "Installed", "Recently played", "Favorites" };
            for (int i = 0; i < names.Length; i++)
            {
                var t = text(names[i], 21, i == 0 ? 1 : 0.75);
                if (i == 0) { t.Foreground = PB("SeleneOnFocusColor"); t.FontWeight = FontWeights.SemiBold; }
                tabs.Children.Add(new Border { Child = t, Padding = new Thickness(20, 8, 20, 10), CornerRadius = new CornerRadius(22), Margin = new Thickness(4, 0, 4, 0), Background = i == 0 ? PB("SeleneFocusColor") : Brushes.Transparent });
            }
            tabs.Children.Add(Badge("RB"));
            root.Children.Add(tabs);
            var clock = text(DateTime.Now.ToString("h:mm tt"), 32, 1);
            clock.HorizontalAlignment = HorizontalAlignment.Right; clock.VerticalAlignment = VerticalAlignment.Top; clock.Margin = new Thickness(0, 36, 70, 0);
            root.Children.Add(clock);

            // the row: three cards, then games, the sample game picked (bigger, its ring glowing)
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(80, 140, 0, 0) };
            foreach (var code in new[] { 0xE3F4 /* image */, 0xE6E1 /* show_chart */, 0xF1CC /* shopping_bag */ })
                row.Children.Add(Tile(160, null, code, false));
            row.Children.Add(Tile(220, HomeNav.SampleCover, 0, true));
            foreach (var cover in HomeNav.SampleRowCovers) row.Children.Add(Tile(160, cover, 0, false));
            root.Children.Add(row);

            // the picked game: its logo, Play / Details, a line about it
            var info = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(80, 430, 0, 0) };
            var logo = HomeNav.SampleLogo;
            if (logo != null) info.Children.Add(new Image { Source = logo, MaxWidth = 620, MaxHeight = 230, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
            else info.Children.Add(text("Clair Obscur: Expedition 33", 64, 1));
            var pills = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 34, 0, 0) };
            var play = text("Play", 24, 1); play.Foreground = PB("SeleneOnFocusColor"); play.FontWeight = FontWeights.SemiBold;
            pills.Children.Add(new Border { Child = play, Padding = new Thickness(54, 12, 54, 14), CornerRadius = new CornerRadius(30), Background = PB("SeleneFocusColor"), Margin = new Thickness(0, 0, 16, 0) });
            pills.Children.Add(new Border { Child = text("Details", 24, 1), Padding = new Thickness(44, 12, 44, 14), CornerRadius = new CornerRadius(30), Background = PB("SeleneGlassColor", 0.72), BorderBrush = PB("SeleneInkColor", 0.22), BorderThickness = new Thickness(1) });
            info.Children.Add(pills);
            var line = text("Playtime 45 h   ·   Last played Today", 22, 0.85);
            line.Margin = new Thickness(4, 26, 0, 0);
            info.Children.Add(line);
            root.Children.Add(info);

            // the hints along the bottom
            var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(80, 0, 0, 44) };
            left.Children.Add(Hint("☰", "Collection")); left.Children.Add(Hint("≡", "Menu"));
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 70, 44) };
            right.Children.Add(Hint("X", "Details")); right.Children.Add(Hint("A", "Play"));
            root.Children.Add(left); root.Children.Add(right);
            if (art == null || logo == null || HomeNav.SampleCover == null || HomeNav.SampleRowCovers.Any(c => c == null)) HomeNav.FetchSample(() => { HomeNav.PreviewChanged(); RefreshPreviewLater(); });
            return root;
        }

        private static FrameworkElement Tile(double size, ImageSource cover, int icon, bool picked)
        {
            var face = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top,
                // a tall cover in a square tile: its top (where the logo is), not its middle (the logo was cut off)
                Background = cover != null ? (Brush)new ImageBrush(cover) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top } : PB("SeleneGlassColor", 0.72),
                BorderBrush = picked ? PB("SeleneFocusColor") : PB("SeleneInkColor", 0.12), BorderThickness = new Thickness(picked ? 4 : 1)
            };
            if (picked) face.Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 30, Opacity = 0.9, Color = PC("SeleneFocusColor") };
            if (icon != 0) face.Child = new TextBlock { Text = Ui.Icon(icon), FontFamily = Ui.Icons, FontSize = 64, Foreground = PB("SeleneInkColor", 0.9), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return face;
        }

        private static FrameworkElement Badge(string label)
        {
            return new Border
            {
                Width = 44, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Background = PB("SeleneInkColor"),
                Child = new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = PB("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
        }

        private static FrameworkElement Hint(string key, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 34, 0) };
            sp.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = PB("SeleneInkColor"), Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = key, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = PB("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 24, FontFamily = Ui.Display, Foreground = PB("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        // a debounced action: moving quickly through choices only applies the one you stop on
        internal class Later
        {
            private readonly DispatcherTimer timer;
            private Action pending;
            public Later(int ms)
            {
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (s, e) => { timer.Stop(); var a = pending; pending = null; if (a != null) a(); };
            }
            public void Run(Action a) { pending = a; timer.Stop(); timer.Start(); }
        }

        // ---------------- 1. name ----------------
        internal class NameStep : Step
        {
            private static readonly string[] Rows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL'", "ZXCVBNM-." };
            private const int MaxLength = 16;
            private readonly StackPanel letters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            private readonly Border field;
            private readonly List<List<Tile>> keys = new List<List<Tile>>();
            private readonly List<List<string>> values = new List<List<string>>();
            private readonly List<List<double>> centers = new List<List<double>>();
            private readonly List<TextBlock> letterLabels = new List<TextBlock>();
            private int row = 1, col;
            private bool shift;

            public NameStep(ProfileSteps owner) : base(owner)
            {
                Header("What should we call you?", "This is the name on your profile. Type, or use the keys below.");
                var layout = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 300, 0, 0) };
                var caret = new Rectangle { Width = 2, Height = 30, Fill = Ink.Brush("GlyphColor"), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                caret.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
                {
                    RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1100),
                    KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)), new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.55)) }
                });
                var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(26, 0, 26, 0) };
                line.Children.Add(letters);
                line.Children.Add(caret);
                field = new Border
                {
                    Width = 560, Height = 68, CornerRadius = new CornerRadius(34), Child = line, HorizontalAlignment = HorizontalAlignment.Center,
                    Background = Ink.Brush("SeleneInkColor", 0.1), BorderBrush = Ink.Brush("SeleneFocusColor"), BorderThickness = new Thickness(2)
                };
                layout.Children.Add(field);
                var board = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
                const double key = 70, gap = 10;
                var allRows = Rows.Select(r => r.Select(c => c.ToString()).ToList()).ToList();
                allRows.Add(new List<string> { "shift", "space", "erase", "done" });
                double widest = Rows.Max(r => r.Length) * (key + gap);
                foreach (var r in allRows)
                {
                    var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, gap) };
                    var rowKeys = new List<Tile>();
                    var rowCenters = new List<double>();
                    double rowWidth = r.Sum(v => Width(v, key) + gap);
                    double x = (widest - rowWidth) / 2;
                    foreach (var v in r)
                    {
                        double w = Width(v, key);
                        var t = new Tile(w, key - 6, 16, 4) { FocusScale = 1.1 };
                        var label = new TextBlock { Text = Label(v), FontSize = v.Length == 1 ? 24 : 19, FontFamily = v == "shift" || v == "erase" ? Ui.Glyphs : Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                        if (v.Length == 1 && char.IsLetter(v[0])) letterLabels.Add(label);
                        // the controller shortcut for Done / erase, as a small badge on the key's top-right corner
                        t.Face.Child = v == "done" || v == "erase" || v == "space" ? (UIElement)WithBadge(label, v == "done" ? "≡" : v == "space" ? "Y" : "X") : label;   // Y types a space, Start (≡) is Done
                        t.Root.Margin = new Thickness(gap / 2, 0, gap / 2, 0);
                        rowPanel.Children.Add(t.Root);
                        rowKeys.Add(t);
                        rowCenters.Add(x + w / 2);
                        x += w + gap;
                    }
                    board.Children.Add(rowPanel);
                    keys.Add(rowKeys);
                    values.Add(r);
                    centers.Add(rowCenters);
                }
                layout.Children.Add(board);
                Root.Children.Add(layout);
                foreach (var c in owner.Name) AddLetter(c.ToString(), false);
                UpdateCase();
            }

            private static Grid WithBadge(UIElement label, string button)
            {
                var g = new Grid();
                g.Children.Add(label);
                var badge = new Grid { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 6, 0) };
                badge.Children.Add(new Ellipse { Fill = Ink.Brush("SeleneInkColor", 0.9) });
                badge.Children.Add(Ui.Letter(button, 12, FontWeights.Bold, Ink.Brush("SeleneScrimColor")));
                g.Children.Add(badge);
                return g;
            }

            private static double Width(string v, double key)
            {
                return v == "space" ? key * 4.4 : v.Length > 1 ? key * 2.1 : key;
            }

            private static string Label(string v)
            {
                switch (v)
                {
                    case "shift": return "\uE752";
                    case "erase": return "\uE750";
                    case "space": return "Space";
                    case "done": return "Done";
                }
                return v;
            }

            public override bool WantsText { get { return true; } }

            public override void Shown() { ShowFocus(); }

            private void ShowFocus()
            {
                for (int r = 0; r < keys.Count; r++)
                    for (int c = 0; c < keys[r].Count; c++) keys[r][c].Focus(r == row && c == col);
            }

            public override void Move(string dir)
            {
                int r = row, c = col;
                if (dir == "DPadLeft") c = c > 0 ? c - 1 : keys[r].Count - 1;   // past an end, round to the other (faster typing)
                else if (dir == "DPadRight") c = c < keys[r].Count - 1 ? c + 1 : 0;
                else
                {
                    r += dir == "DPadUp" ? -1 : 1;
                    if (r < 0 || r >= keys.Count) return;
                    double x = centers[row][col];
                    c = 0;
                    for (int i = 1; i < centers[r].Count; i++) if (Math.Abs(centers[r][i] - x) < Math.Abs(centers[r][c] - x)) c = i;
                }
                if (c < 0 || c >= keys[r].Count) return;
                row = r; col = c;
                Ui.Tick();
                ShowFocus();
            }

            public override bool OnA()
            {
                var v = values[row][col];
                Ui.Pop(keys[row][col].Root);
                if (v == "done") return false;
                if (v == "erase") Erase();
                else if (v == "shift") { shift = !shift; Ui.Key(); UpdateCase(); }
                else if (v == "space") Type(" ");
                else Type(v);
                return true;
            }

            // B goes back (X erases)
            public override bool OnB() { return false; }

            public override void Extra(string button)
            {
                if (button == "X") Erase();
                else if (button == "Y") Type(" ");   // the space bar (Start is done: ProfileSteps.Handle)
            }

            private bool UpperNext { get { return (Owner.Name.Length == 0 || Owner.Name.EndsWith(" ")) != shift; } }

            private void UpdateCase()
            {
                bool upper = UpperNext;
                foreach (var l in letterLabels) l.Text = upper ? l.Text.ToUpperInvariant() : l.Text.ToLowerInvariant();
            }

            public override void Type(string text)
            {
                foreach (var ch in text)
                {
                    if (Owner.Name.Length >= MaxLength) { Ui.Shake(field); return; }
                    if (ch == ' ' && (Owner.Name.Length == 0 || Owner.Name.EndsWith(" "))) continue;
                    var s = ch.ToString();
                    if (char.IsLetter(ch) && text.Length == 1 && char.IsUpper(ch)) s = UpperNext ? s : s.ToLowerInvariant();
                    Owner.Name += s;
                    AddLetter(s, true);
                    shift = false;
                }
                Ui.Key();
                UpdateCase();
            }

            private void AddLetter(string s, bool animate)
            {
                var t = new TextBlock { Text = s == " " ? " " : s, FontSize = 28, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center };
                letters.Children.Add(t);
                if (animate) Ui.Rise(t, 0, 6, 160);
            }

            public override void Erase()
            {
                if (Owner.Name.Length == 0) { Ui.Shake(field); return; }
                Owner.Name = Owner.Name.Substring(0, Owner.Name.Length - 1);
                letters.Children.RemoveAt(letters.Children.Count - 1);
                Ui.Key();
                UpdateCase();
            }

            public override bool Accept()
            {
                Owner.Name = Owner.Name.Trim();
                if (Owner.Name.Length > 0) return true;
                Ui.Shake(field);
                Ui.Back();
                return false;
            }
        }

        // ---------------- 2. avatar ----------------
        internal class AvatarStep : Step
        {
            private const int Columns = 10;
            private readonly List<string> files = new List<string>();     // null = "your own picture"
            private readonly List<Tile> tiles = new List<Tile>();
            private readonly ScrollingGrid grid;
            private int index;
            private AvatarCrop crop;   // framing your own picture, on top of the grid
            private Dialog confirm;    // removing a picture someone added
            private readonly FrameworkElement removeHint;

            public AvatarStep(ProfileSteps owner) : base(owner)
            {
                Header("Choose your avatar", "Pick one, or use your own picture. A GIF keeps moving.");
                files.Add(null);
                var steam = AnimatedAvatar.Steam ?? SelenePaths.AvatarImage;   // the animated one when the account has it
                // pictures added before, except a saved copy of the Steam avatar (it has its own tile)
                var steamPrint = !string.IsNullOrEmpty(steam) && System.IO.File.Exists(steam) ? Profiles.Fingerprint(steam) : null;
                files.AddRange(Profiles.KeptPictures().Where(f => steamPrint == null || Profiles.Fingerprint(f) != steamPrint));
                if (!string.IsNullOrEmpty(steam) && System.IO.File.Exists(steam)) files.Add(steam);
                // the shipped ones, except a picture already there (a GIF picked before as your own is the same file: it showed twice)
                var already = files.Where(f => f != null && System.IO.File.Exists(f)).ToList();
                var sizes = new HashSet<long>(already.Select(f => new System.IO.FileInfo(f).Length));   // (only same-size files are compared)
                var seen = new HashSet<string>(already.Select(f => Profiles.Fingerprint(f)));
                foreach (var f in Profiles.BuiltInAvatars())
                    if (!sizes.Contains(new System.IO.FileInfo(f).Length) || seen.Add(Profiles.Fingerprint(f))) files.Add(f);
                grid = new ScrollingGrid(Columns, 118, 560);
                grid.Viewport.Width = Columns * 118;
                grid.Viewport.HorizontalAlignment = HorizontalAlignment.Center;
                grid.Viewport.VerticalAlignment = VerticalAlignment.Top;
                grid.Viewport.Margin = new Thickness(0, 290, 0, 0);
                Root.Children.Add(grid.Viewport);   // its tiles are made the first time the step shows
                var hint = Ui.Prompts(false, Ui.Prompt("X", "Remove picture"));
                hint.Margin = new Thickness(200, 0, 0, 48);
                hint.Visibility = Visibility.Collapsed;
                removeHint = hint;
                Root.Children.Add(hint);
                // the current avatar (editing), else the first built-in one
                var current = Profiles.AvatarFile(new Profile { Avatar = owner.Avatar });
                index = current != null ? files.FindIndex(f => f != null && string.Equals(f, current, StringComparison.OrdinalIgnoreCase)) : -1;
                if (index < 0 && current != null && steamPrint != null && System.IO.File.Exists(current) && Profiles.Fingerprint(current) == steamPrint) index = files.IndexOf(steam);
                if (index < 0) index = Math.Min(1, files.Count - 1);   // the Steam avatar when there is one, else the first of ours
            }

            // the pictures come in the first time this step shows (loading all of them as soon as the profile screen opened
            // kept the other steps busy: Change theme lagged)
            private bool picturesLoading;
            private void LoadPictures()
            {
                if (picturesLoading) return;
                picturesLoading = true;
                foreach (var f in files) AddTile(f, grid.Panel.Children.Count);
                Ui.LoadInOrder(files.Select(f => f ?? "").ToList(), 192, (i, pic) =>
                {
                    var f = i < files.Count ? files[i] : null;
                    if (i >= tiles.Count) return;
                    bool gif = f != null && f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
                    tiles[i].Face.Background = gif ? AnimatedAvatar.Brush(f, 192) : new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
                    // a moving picture isn't cached (its cache was redrawn every frame and, snapped to pixels while the tile grew,
                    // the row around it vibrated)
                    if (gif) { tiles[i].Root.CacheMode = null; tiles[i].Root.SnapsToDevicePixels = false; }
                });
            }

            public override void Shown() { LoadPictures(); ShowFocus(); }

            // one avatar tile at position `at` (null = the "add your own picture" tile)
            private Tile AddTile(string f, int at)
            {
                var t = new Tile(96, 96, 48, 5) { FocusScale = 1.12 };
                if (f == null) t.Face.Child = new TextBlock { Text = "\uE710", FontFamily = Ui.Glyphs, FontSize = 30, Foreground = Ink.Brush("SeleneInkColor", 0.85), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var cell = new Grid { Width = 118, Height = 118 };
                t.Root.HorizontalAlignment = HorizontalAlignment.Center;
                t.Root.VerticalAlignment = VerticalAlignment.Center;
                cell.Children.Add(t.Root);
                grid.Panel.Children.Insert(at, cell);
                tiles.Insert(at, t);
                return t;
            }

            private void ShowFocus()
            {
                for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(i == index);
                grid.Reveal(index);
                removeHint.Visibility = Profiles.IsKeptPicture(files[index]) ? Visibility.Visible : Visibility.Collapsed;
            }

            public override void Move(string dir)
            {
                if (confirm != null) { confirm.Handle(dir); return; }
                if (crop != null) { crop.Handle(dir); return; }
                int next = Ui.GridMove(index, dir, tiles.Count, Columns);
                if (next < 0) return;
                index = next;
                Ui.Tick();
                ShowFocus();
            }

            public override bool OnB()
            {
                if (confirm != null) { confirm.Handle("B"); return true; }
                if (crop == null) return false;
                crop.Handle("B");
                return true;
            }

            public override void Extra(string button)
            {
                if (confirm != null) { if (button == "X") confirm.Answer(true); return; }
                if (crop != null) { crop.Handle(button); return; }
                if (button == "X" && Profiles.IsKeptPicture(files[index])) RemovePicture(index);
            }

            // X on a picture someone added: asks, then deletes it (not while a profile uses it)
            private void RemovePicture(int at)
            {
                var file = files[at];
                Owner.ShowChrome(false);
                confirm = new Dialog("\uE7BA", "Remove this picture?", "It goes from the pictures you added. Built-in avatars stay.", "Remove", "Keep",
                    () =>
                    {
                        confirm = null;
                        Owner.ShowChrome(true);
                        if (!Profiles.DeletePicture(file)) { Ui.Shake(tiles[at].Root); Ui.Back(); return; }
                        grid.Panel.Children.RemoveAt(at);
                        tiles.RemoveAt(at);
                        files.RemoveAt(at);
                        index = Math.Min(at, files.Count - 1);
                        ShowFocus();
                    },
                    () => { confirm = null; Owner.ShowChrome(true); });
                confirm.Open(Root);
            }

            private void CloseCrop()
            {
                var closing = crop.Root;
                crop = null;
                Owner.ShowChrome(true);
                Ui.Fade(closing, 0, 180, 0, () => Root.Children.Remove(closing));
            }

            public override bool OnA()
            {
                if (confirm != null) { confirm.Handle("A"); return true; }
                if (crop != null) { crop.Handle("A"); return true; }
                var f = files[index];
                if (f != null)
                {
                    // shipped avatars are kept relative to the theme; a Steam avatar is copied so it stays the same
                    var shipped = SelenePaths.ThemeFile("Avatars");
                    Owner.Avatar = f.StartsWith(shipped, StringComparison.OrdinalIgnoreCase) ? "Avatars/" + System.IO.Path.GetFileName(f)
                                 : Profiles.IsKeptPicture(f) ? f : Profiles.KeepPicture(f);
                    return false;
                }
                var picked = HomeNav.Api != null ? HomeNav.Api.Dialogs.SelectImagefile() : null;
                if (string.IsNullOrEmpty(picked)) return true;
                // a GIF is kept whole, so it keeps moving (the framing step would have kept one still of it); it shows cropped to
                // the circle like the Steam one
                if (picked.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    string kept;
                    try { kept = Profiles.KeepPicture(picked); }
                    catch (Exception ex) { HomeNav.Log("avatar gif: " + ex.Message); Ui.Shake(tiles[0].Root); return true; }
                    files.Insert(1, kept);
                    var gt = AddTile(kept, 1);
                    gt.Face.Background = AnimatedAvatar.Brush(kept, 192);
                    gt.Root.CacheMode = null; gt.Root.SnapsToDevicePixels = false;
                    index = 1;
                    ShowFocus();
                    Ui.Pop(gt.Root);
                    Ui.Confirm();
                    return true;
                }
                // frame it first: the circle shows exactly what the avatar will be
                // framed: back to the grid with the new picture right after "+", chosen; A on it uses it
                crop = AvatarCrop.Open(picked, saved =>
                {
                    CloseCrop();
                    files.Insert(1, saved);
                    var t = AddTile(saved, 1);
                    t.Face.Background = AnimatedAvatar.Brush(saved, 192);
                    index = 1;
                    ShowFocus();
                    Ui.Pop(t.Root);
                }, CloseCrop);
                if (crop == null) { Ui.Shake(tiles[0].Root); return true; }
                Root.Children.Add(crop.Root);
                Owner.ShowChrome(false);
                return true;
            }
        }

        // ---------------- 3. theme ----------------
        internal class ThemeStep : Step
        {
            private const int Columns = 5;
            private readonly List<ThemeInfo> themes;
            private readonly List<Tile> tiles = new List<Tile>();
            private readonly TextBlock nameLabel;
            private readonly StackPanel colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 18, 0, 0) };
            private readonly Later apply = new Later(120);   // moving quickly redraws the preview only for the one stopped on
            private int index;

            public ThemeStep(ProfileSteps owner) : base(owner)
            {
                Header("Choose your theme", "Colors for all of Selene. Move through them to see each one in the preview.");
                themes = ThemePalettes.All();
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 290, 0, 0) };
                var grid = new UniformGrid { Columns = Columns, VerticalAlignment = VerticalAlignment.Top };
                foreach (var t in themes) grid.Children.Add(Swatch(t));
                row.Children.Add(grid);
                var right = new StackPanel { Margin = new Thickness(60, 0, 0, 0) };
                right.Children.Add(Preview(780));
                right.Children.Add(colors);
                nameLabel = new TextBlock { FontSize = 26, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), Margin = new Thickness(4, 12, 0, 0) };
                right.Children.Add(nameLabel);
                row.Children.Add(right);
                Root.Children.Add(row);
                index = Math.Max(0, themes.FindIndex(t => string.Equals(t.Id, owner.Theme, StringComparison.OrdinalIgnoreCase)));
                // each tile shows its theme's own wallpaper (themes without one keep their plain color)
                var pictures = themes.Select(t => ThemePalettes.DefaultWallpaper(t.Id)).ToList();
                Ui.LoadInOrder(pictures, 280, (i, pic) =>
                {
                    if (i >= tiles.Count) return;
                    var layer = new Border { CornerRadius = new CornerRadius(13), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill }, Opacity = 0 };
                    var host = (Grid)tiles[i].Face.Child;
                    host.Children.Insert(0, layer);
                    Ui.Fade(layer, 1, 250);
                });
            }

            private FrameworkElement Swatch(ThemeInfo t)
            {
                var tile = new Tile(132, 76, 14, 5) { FocusScale = 1.08 };
                tile.Face.Background = new SolidColorBrush(Parse(t.Background, Colors.Black));
                tile.Face.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                var host = new Grid();
                // the wallpaper goes under this; the theme's background color fades in from the left so "Aa" stays readable
                var bg = Parse(t.Background, Colors.Black);
                host.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(13),
                    Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0xE6, bg.R, bg.G, bg.B), 0), new GradientStop(Color.FromArgb(0x55, bg.R, bg.G, bg.B), 0.55), new GradientStop(Color.FromArgb(0, bg.R, bg.G, bg.B), 1) }, 0)
                });
                var inner = new Grid { Margin = new Thickness(12, 10, 12, 10) };
                inner.Children.Add(new TextBlock { Text = "Aa", FontSize = 22, FontFamily = Ui.Display, Foreground = new SolidColorBrush(Parse(t.Foreground, Colors.White)), VerticalAlignment = VerticalAlignment.Center });
                inner.Children.Add(new Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(Parse(t.Accent, Colors.White)), Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                host.Children.Add(inner);
                tile.Face.Child = host;
                tiles.Add(tile);
                var cell = new StackPanel { Margin = new Thickness(9, 6, 9, 8) };
                cell.Children.Add(tile.Root);
                cell.Children.Add(new TextBlock { Text = t.Name, FontSize = 14, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.75), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 });
                return cell;
            }

            private static Color Parse(string hex, Color fallback)
            {
                try { return string.IsNullOrEmpty(hex) ? fallback : (Color)ColorConverter.ConvertFromString(hex); }
                catch (FormatException) { return fallback; }
            }

            public override void Shown() { ShowFocus(false); }

            private void ShowFocus(bool live)
            {
                for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(i == index);
                if (themes.Count == 0) return;
                var theme = themes[index];
                nameLabel.Text = theme.Name + (theme.Mode == "light" ? "  ·  light" : "");
                ShowColors(theme);
                if (live) apply.Run(() => Show(theme.Id));
                else Show(theme.Id);
            }

            // the theme's colors as a strip of chips; they rise in one after another
            private void ShowColors(ThemeInfo theme)
            {
                colors.Children.Clear();
                var list = theme.Swatches ?? new List<string> { theme.Background, theme.Foreground, theme.Accent };
                for (int i = 0; i < list.Count; i++)
                {
                    var chip = new Border
                    {
                        Width = 46, Height = 26, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 8, 0),
                        Background = new SolidColorBrush(Parse(list[i], Colors.Gray)), BorderBrush = Ink.Brush("SeleneInkColor", 0.25), BorderThickness = new Thickness(1)
                    };
                    colors.Children.Add(chip);
                    Ui.Rise(chip, i * 25, 8, 200);
                }
            }

            // only the preview shows the theme (and its wallpaper, unless one was picked); Selene itself changes on A
            private void Show(string id)
            {
                var wallpaper = Owner.Wallpaper ?? ThemePalettes.DefaultWallpaper(id);
                Wallpapers.Prepare(wallpaper, () =>
                {
                    if (!string.Equals(id, previewTheme, StringComparison.OrdinalIgnoreCase) || wallpaper != previewWallpaper) HomeNav.PreviewChanged();
                    previewTheme = id;
                    previewWallpaper = wallpaper;
                    RefreshPreviewLater();
                });
            }

            // the whole of Selene recolors, the home's wallpaper too; after the slide to the next step
            private void ApplyNow(string id)
            {
                LoginScene.CrossfadeBackdrop(() =>
                {
                    ThemePalettes.Apply(id);
                    var wallpaper = Owner.Wallpaper ?? ThemePalettes.DefaultWallpaper(id);
                    Wallpapers.Show(wallpaper);
                    LoginScene.RefreshBackdrop();
                });
                previewTheme = null;
                HomeNav.PreviewChanged();
                RefreshPreviewLater();
            }

            public override void Move(string dir)
            {
                int next = Ui.GridMove(index, dir, tiles.Count, Columns);
                if (next < 0) return;
                index = next;
                Ui.Tick();
                ShowFocus(true);
            }

            public override bool Accept()
            {
                if (themes.Count > 0)
                {
                    var id = themes[index].Id;
                    Owner.Theme = id;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => Ui.AfterHold(() => ApplyNow(id))), DispatcherPriority.Background);
                }
                return true;
            }
        }

        // ---------------- 4. background ----------------
        internal class BackgroundStep : Step
        {
            private const int Columns = 3;
            private readonly List<string> files = new List<string>();    // null = the theme's own
            private readonly List<Tile> tiles = new List<Tile>();
            private readonly ScrollingGrid grid;
            private readonly TextBlock groupLabel;
            private readonly Segmented look = new Segmented("Show", new[] { "Game art", "Background only" }, "X");
            private readonly TextBlock lookNote = new TextBlock { FontSize = 15, FontFamily = Ui.Body, TextWrapping = TextWrapping.Wrap, MaxWidth = 820, Margin = new Thickness(4, 12, 0, 0) };
            private readonly List<string> groups = new List<string>();
            private readonly Later apply = new Later(450);   // once you rest on one: applied between quick moves, the swap landed in the next glide (it stuttered)
            private int index, generation;
            private bool hasArt;   // the library has a game whose art the preview can show

            public BackgroundStep(ProfileSteps owner) : base(owner)
            {
                Header("Choose your background", "Behind the login, menus and the home when no game is selected.");
                grid = new ScrollingGrid(Columns, 142, 560);
                grid.Viewport.Width = Columns * 226;
                grid.Viewport.VerticalAlignment = VerticalAlignment.Top;
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 290, 0, 0) };
                var left = new StackPanel();
                groupLabel = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), Margin = new Thickness(10, 0, 0, 10) };
                left.Children.Add(groupLabel);
                left.Children.Add(grid.Viewport);
                row.Children.Add(left);
                var right = new StackPanel { Margin = new Thickness(50, 0, 0, 0) };
                right.Children.Add(Preview(820));
                look.Root.Margin = new Thickness(4, 20, 0, 0);
                right.Children.Add(look.Root);
                lookNote.Foreground = Ink.Brush("SeleneInkColor", 0.6);
                lookNote.Visibility = Visibility.Collapsed;   // (no note under the choices: the preview shows what each does)
                right.Children.Add(lookNote);
                row.Children.Add(right);
                Root.Children.Add(row);
            }

            // built when shown: the list starts with the theme picked a step ago; kept as it is when that theme hasn't changed
            private string filledFor;
            private void Fill()
            {
                if (filledFor == Owner.Theme && tiles.Count > 0)
                {
                    index = Owner.Wallpaper == null ? 0 : Math.Max(0, files.FindIndex(f => f != null && string.Equals(f, Owner.Wallpaper, StringComparison.OrdinalIgnoreCase)));
                    return;
                }
                filledFor = Owner.Theme;
                files.Clear(); tiles.Clear(); groups.Clear(); grid.Panel.Children.Clear();
                var theme = ThemePalettes.Find(Owner.Theme);
                var themeName = theme != null ? theme.Name : "";
                files.Add(null); groups.Add(themeName);
                var all = Wallpapers.All();
                var own = ThemePalettes.DefaultWallpaper(Owner.Theme);
                foreach (var w in all.Where(w => w.Group == themeName).Concat(all.Where(w => w.Group != themeName)))
                {
                    if (string.Equals(w.File, own, StringComparison.OrdinalIgnoreCase)) continue;   // that's "Theme's own"
                    files.Add(w.File);
                    groups.Add(w.Group);
                }
                var defaultFile = ThemePalettes.DefaultWallpaper(Owner.Theme);
                foreach (var f in files)
                {
                    var t = new Tile(204, 115, 12, 5) { FocusScale = 1.06 };
                    if (f == null)
                    {
                        // the theme's own: its default wallpaper (or Selene's gradient) with a label
                        var g = new Grid();
                        if (defaultFile == "") { var b = new Border { CornerRadius = new CornerRadius(12) }; b.SetResourceReference(Border.BackgroundProperty, "SeleneBaseBrush"); g.Children.Add(b); }
                        g.Children.Add(new Border
                        {
                            VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, 12, 12), Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Padding = new Thickness(10, 5, 10, 6),
                            Child = new TextBlock { Text = "Theme's own", FontSize = 15, FontFamily = Ui.Body, Foreground = Brushes.White }
                        });
                        t.Face.Child = g;
                    }
                    t.Root.Margin = new Thickness(11, 13, 11, 14);
                    grid.Panel.Children.Add(t.Root);
                    tiles.Add(t);
                }
                var thumbs = files.Select(f => f ?? defaultFile).ToList();
                int mine = ++generation;   // pictures still loading for an earlier list are dropped
                Ui.LoadInOrder(thumbs, 320, (i, pic) => { if (mine == generation && i < tiles.Count) tiles[i].Face.Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill }; });
                index = Owner.Wallpaper == null ? 0 : Math.Max(0, files.FindIndex(f => f != null && string.Equals(f, Owner.Wallpaper, StringComparison.OrdinalIgnoreCase)));
            }

            public override void Shown()
            {
                hasArt = false;   // (the preview draws its own sample home; nothing is put on the real one)
                Fill();
                RefreshPreviewLater();
                look.Select(previewLook == "wallpaper" ? 1 : 0, false);
                ShowNote();
                ShowFocus(false);
            }

            private void ShowFocus(bool live)
            {
                for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(i == index);
                grid.Reveal(index);
                groupLabel.Text = (index == 0 ? "THEME" : groups[index].ToUpperInvariant());
                var file = files[index] ?? ThemePalettes.DefaultWallpaper(Owner.Theme);
                // decoded off the UI thread first, so the swap itself costs nothing in the middle of a glide
                // (back on this step with the same wallpaper: nothing to change, no crossfade or new preview picture)
                if (!live && string.Equals(Wallpapers.Current ?? "", file ?? "", StringComparison.OrdinalIgnoreCase)) return;
                Action show = () => Wallpapers.Prepare(file, () => Ui.AfterHold(() => { LoginScene.CrossfadeBackdrop(() => { Wallpapers.Show(file); LoginScene.RefreshBackdrop(); }); RefreshPreviewLater(); }));
                if (live) apply.Run(show); else show();
            }

            // X: the preview with or without the game's art, to see the background both ways (the home itself isn't changed)
            private void ShowLook(bool changed)
            {
                look.Select(previewLook == "wallpaper" ? 1 : 0, changed);
                if (changed) { HomeNav.PreviewChanged(); RefreshPreviewLater(); }
                ShowNote();
            }

            public override void Left() { }   // the sample art stays until the profile screen closes

            private void ShowNote()
            {
                lookNote.Text = previewLook == "wallpaper" ? "The home always shows this background."
                              : "The home shows each game's art (an example game in the preview); this background is behind the login, the menus and an empty home.";
            }

            public override void Move(string dir)
            {
                int next = Ui.GridMove(index, dir, tiles.Count, Columns);
                if (next < 0) return;
                index = next;
                Ui.Tick();
                ShowFocus(true);
            }

            public override void Extra(string button)
            {
                if (button != "X") return;
                previewLook = previewLook == "wallpaper" ? "art" : "wallpaper";
                Ui.Key();
                ShowLook(true);
            }

            public override bool Accept()
            {
                Owner.Wallpaper = files.Count > 0 ? files[index] : null;
                return true;
            }
        }
    }
}
