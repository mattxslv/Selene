using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // The library's background: the selected game's art, and once you rest on a game its trailer takes over the whole
    // background (muted, looping) with the game's song. R3 swaps the song for the trailer's own sound. Moving on fades the
    // trailer away and the default music comes back.
    public static class LibraryStage
    {
        private static ListBox list;
        private static FrameworkElement library;
        private static Grid host;
        private static TextBlock rsHint;
        private static UIElement art, wallpaper, baseLayer;   // faded away while the trailer plays (black behind it)
        private static MediaElement video;
        private static bool videoHasSound, soundOn;
        private static DispatcherTimer arm;

        public static bool Playing { get { return video != null; } }

        public static void Attach(Window w)
        {
            if (list != null || w == null) return;
            list = HomeNav.FindByName(w, "PART_ListGameItems") as ListBox;
            library = HomeNav.FindByName(w, "SeleneLibrary");
            host = HomeNav.FindByName(w, "SeleneLibraryTrailerHost") as Grid;
            rsHint = HomeNav.FindByName(w, "SeleneLibraryRsHint") as TextBlock;
            // Collection's own (game details has a PART_ImageBackground of its own, which was the one found before)
            art = HomeNav.FindByName(library, "PART_ImageBackground") as UIElement;
            // Playnite doesn't keep that name on its image: it's the FadeImage sitting right in Collection's own grid
            var grid = library as Panel;
            if (art == null && grid != null)
                foreach (UIElement child in grid.Children)
                    if (child.GetType().Name == "FadeImage") { art = child; break; }
            wallpaper = HomeNav.FindByName(library, "SeleneLibraryWallpaper") as UIElement;
            baseLayer = HomeNav.FindByName(library, "SeleneLibraryBase") as UIElement;
            HomeNav.Log("library stage: art " + (art != null) + ", wallpaper " + (wallpaper != null));
            if (list == null || library == null || host == null) { HomeNav.Log("library stage: parts not found"); list = null; return; }
            arm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4000) };   // like Aniki: the art first, the trailer about 4 s in
            arm.Tick += (s, e) => { arm.Stop(); Play(); };
            HookEndOfRow();
            list.SelectionChanged += (s, e) => Rearm(true);
            library.IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) Rearm(false); else Stop(); };
            HookArtCrossfade(grid);
        }

        // ---- the row's ends ----
        // The row scrolls by its own size estimate, which stops short of the last tile (the last game ended 47 px past the
        // screen's edge at the furthest scroll), and the selected tile grows (zoom and ring) past whatever edge it is at. So
        // the tile is kept inside the row by what is really on screen: the whole row slides back by the missing distance,
        // smoothly, at the start and the end, on any screen size.
        private static bool endHooked;
        private static DateTime endSettleUntil, endLastFrame = DateTime.MinValue;

        private static void HookEndOfRow()
        {
            list.SelectionChanged += (s, e) =>
            {
                endSettleUntil = DateTime.Now.AddMilliseconds(900);   // the row's own scroll runs a moment after the move
                if (endHooked) return;
                endHooked = true;
                CompositionTarget.Rendering += KeepTileInside;
            };
        }

        private static void KeepTileInside(object sender, EventArgs e)
        {
            try
            {
                var presenter = FindPresenter(list);
                if (presenter == null) { Unhook(); return; }
                var shift = presenter.RenderTransform as TranslateTransform;
                if (shift == null || shift.IsFrozen) { shift = new TranslateTransform(); presenter.RenderTransform = shift; }
                var item = list.SelectedIndex >= 0 ? list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as FrameworkElement : null;
                double target = 0;
                // only the two ends need it (everywhere else the row's own scroll leaves the tile well inside), and nothing
                // reacts to the tiles passing the edge mid-scroll: that made the row rubber-band
                bool atEnd = list.SelectedIndex == 0 || list.SelectedIndex == list.Items.Count - 1;
                if (atEnd && item != null && item.IsVisible && item.ActualWidth > 0)
                {
                    double edge = Math.Max(18, list.ActualWidth * 0.008);   // what the zoom and the ring need beside the tile
                    double left = item.TranslatePoint(new Point(0, 0), list).X - shift.X + 14;   // the tile itself (the item has a 14 px margin on its left)
                    double right = left - 14 + item.ActualWidth - 0;
                    if (right > list.ActualWidth - edge) target = (list.ActualWidth - edge) - right;
                    else if (left < edge) target = edge - left;
                }
                // eased by time, not per frame: the same speed on a 60 Hz and a 360 Hz screen
                var nowAt = DateTime.Now;
                double dt = endLastFrame == DateTime.MinValue ? 0.016 : Math.Min(0.05, (nowAt - endLastFrame).TotalSeconds);
                endLastFrame = nowAt;
                double diff = target - shift.X;
                shift.X = Math.Abs(diff) < 0.4 ? target : shift.X + diff * (1 - Math.Exp(-dt / 0.09));
                if (DateTime.Now > endSettleUntil && Math.Abs(diff) < 0.4) Unhook();
            }
            catch { Unhook(); }
        }

        private static void Unhook()
        {
            CompositionTarget.Rendering -= KeepTileInside;
            endHooked = false;
            endLastFrame = DateTime.MinValue;
        }

        private static ItemsPresenter FindPresenter(DependencyObject root)
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = child as ItemsPresenter ?? FindPresenter(child);
                if (found != null) return found;
            }
            return null;
        }

        // ---- the art from game to game: a crossfade of Selene's own ----
        // Playnite's image swaps its picture with a blink of a fade. Here the picture as it was stays over it while the next
        // one comes in, then melts away over a good half second.
        private static Image artSnap;
        private static DispatcherTimer snapFallback;
        private static bool snapCovering;
        private static readonly HashSet<Image> watched = new HashSet<Image>();

        private static void HookArtCrossfade(Panel grid)
        {
            var fe = art as FrameworkElement;
            if (fe == null || grid == null) return;
            artSnap = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false, Opacity = 0 };
            RenderOptions.SetBitmapScalingMode(artSnap, BitmapScalingMode.Fant);
            Grid.SetRowSpan(artSnap, Grid.GetRowSpan(fe));
            Panel.SetZIndex(artSnap, Panel.GetZIndex(fe));
            int at = grid.Children.IndexOf(fe);
            grid.Children.Insert(at + 1, artSnap);   // right over the art
            snapFallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            snapFallback.Tick += (s, e) => { snapFallback.Stop(); MeltSnapshot(); };   // no new picture came (the same art): the still goes anyway
            list.SelectionChanged += (s, e) => TakeSnapshot();
            fe.Loaded += (s, e) => WatchInnerImages(fe);
            WatchInnerImages(fe);
        }

        // Playnite's image holds its pictures in Image elements inside: one of them getting a new picture means the next art is in
        private static void WatchInnerImages(FrameworkElement fe)
        {
            foreach (var img in Inner(fe).ToList())
            {
                if (!watched.Add(img)) continue;
                DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(img, (s, e) =>
                {
                    if (snapCovering && img.Source != null) fe.Dispatcher.BeginInvoke(new Action(MeltSnapshot), DispatcherPriority.Loaded);
                });
            }
        }

        private static IEnumerable<Image> Inner(DependencyObject d)
        {
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                var img = c as Image;
                if (img != null) yield return img;
                foreach (var deeper in Inner(c)) yield return deeper;
            }
        }

        // the picture showing now, kept over the art until the next one is in
        private static void TakeSnapshot()
        {
            var fe = art as FrameworkElement;
            if (fe == null || artSnap == null || video != null || !fe.IsVisible) return;
            if (snapCovering) { snapFallback.Stop(); snapFallback.Start(); return; }   // still covering from the last move: that still stays
            if (watched.Count == 0) WatchInnerImages(fe);
            var showing = Inner(fe).FirstOrDefault(i => i.Source != null && i.Opacity > 0.5 && i.IsVisible);
            if (showing == null) return;
            artSnap.Source = showing.Source;
            artSnap.Stretch = showing.Stretch;
            artSnap.RenderTransform = fe.RenderTransform;             // the same crop as the art's (the letterbox trim)
            artSnap.RenderTransformOrigin = fe.RenderTransformOrigin;
            artSnap.BeginAnimation(UIElement.OpacityProperty, null);
            artSnap.Opacity = fe.Opacity;
            snapCovering = true;
            snapFallback.Stop();
            snapFallback.Start();
        }

        private static void MeltSnapshot()
        {
            if (!snapCovering || artSnap == null) return;
            snapCovering = false;
            snapFallback.Stop();
            artSnap.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private static void Rearm(bool moved)
        {
            Stop(moved);
            if (arm == null || !library.IsVisible) return;
            arm.Stop();
            // the same wait as Spotlight's trailer cards (the Trailer cards after setting; never = none here either)
            double after = Features.Number("trailerdelay");
            if (after <= 0) return;
            arm.Interval = TimeSpan.FromSeconds(after);
            arm.Start();
        }

        private static void Play()
        {
            try
            {
                if (!library.IsVisible || !Features.On("librarytrailer")) return;
                var w = Window.GetWindow(library);
                var details = w != null ? HomeNav.FindByName(w, "PART_ElemGameDetails") : null;
                if (details != null && details.IsVisible) return;
                var game = list.SelectedItem;
                string path; bool hasSound;
                if (game == null || !HomeNav.TrailerFor(game, out path, out hasSound)) return;
                var player = new MediaElement
                {
                    Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
                    Stretch = Stretch.UniformToFill, IsMuted = true, IsHitTestVisible = false   // the game's song plays instead (R3: its own sound)
                };
                player.MediaEnded += (s, e) => { player.Position = TimeSpan.Zero; player.Play(); };
                player.MediaOpened += (s, e) =>
                {
                    if (video != player) return;
                    // black cinema bars are measured while it's still hidden, so any crop is in place before it shows
                    CropBars(player, () =>
                    {
                        if (video != player) return;
                        Fade(true);
                        HomeNav.SetMusic("trailer");   // the game's song comes with its trailer
                        if (rsHint != null && videoHasSound) rsHint.Text = "Trailer sound";
                    });
                };
                player.MediaFailed += (s, e) => { HomeNav.Log("library trailer failed: " + e.ErrorException.Message); Stop(); };
                video = player;
                videoHasSound = hasSound;
                soundOn = false;
                host.Children.Add(player);
                player.Play();
            }
            catch (Exception ex) { HomeNav.Log("library trailer: " + ex.Message); }
        }

        // R3 while a trailer plays: its own sound instead of the game's song (and back)
        public static bool ToggleSound()
        {
            if (video == null || !videoHasSound) return false;
            soundOn = !soundOn;
            video.IsMuted = !soundOn;
            HomeNav.TrailerSound(soundOn);
            if (rsHint != null) rsHint.Text = soundOn ? "Trailer sound off" : "Trailer sound";
            Ui.Key();
            HomeNav.Log("library trailer sound " + (soundOn ? "on" : "off"));
            return true;
        }

        // Many trailers carry black cinema bars inside their 16:9 picture. Measured twice while the trailer is still hidden
        // (0.6 s and 0.9 s in); only when both agree is it zoomed, at once, just enough to push the bars off screen, so the
        // picture reaches the top edge. Then `show`. A trailer starting on black frames just isn't cropped (no zoom later).
        private static void CropBars(MediaElement player, Action show)
        {
            player.RenderTransformOrigin = new Point(0.5, 0.5);
            double first = 0;
            var look = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            look.Tick += (s, e) =>
            {
                if (video != player) { look.Stop(); return; }
                double scale = BarZoom(player);
                if (first == 0) { first = scale; look.Interval = TimeSpan.FromMilliseconds(300); return; }
                look.Stop();
                if (scale > 1.02 && Math.Abs(scale - first) < 0.03)
                {
                    player.RenderTransform = new ScaleTransform(scale, scale);
                    HomeNav.Log("library trailer: black bars, zoomed to " + scale.ToString("0.00"));
                }
                show();
            };
            look.Start();
        }

        // how much zoom hides the bars (1 = none): rows that are black right across, counted from the top and the bottom
        private static double BarZoom(MediaElement player)
        {
            try
            {
                const int w = 160, h = 90;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(player) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var px = new byte[w * h * 4];
                rtb.CopyPixels(px, w * 4, 0);
                Func<int, bool> dark = y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        if (px[i] > 28 || px[i + 1] > 28 || px[i + 2] > 28) return false;
                    }
                    return true;
                };
                int top = 0; while (top < h && dark(top)) top++;
                if (top >= h) return 1;   // a black frame (fading in): nothing to measure yet
                int bottom = 0; while (bottom < h && dark(h - 1 - bottom)) bottom++;
                int band = Math.Min(top, bottom);
                if (band < h * 0.05 || Math.Abs(top - bottom) > h * 0.05) return 1;   // no bars, or not even ones
                // a real bar ends on a sharp edge (a dark scene fades out instead): the first rows past it hold picture
                Func<int, double> mean = y => { double sum = 0; for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; sum += px[i] + px[i + 1] + px[i + 2]; } return sum / (w * 3); };
                if (mean(Math.Min(h - 1, top + 1)) < 40 || mean(Math.Max(0, h - 2 - bottom)) < 40) return 1;
                return Math.Min(1.45, h / (double)(h - 2 * band));
            }
            catch { return 1; }
        }

        // entering Collection: its art fades in on its own, a moment after the screen (Playnite's image pops in without
        // a fade here, mid-way through the screen's fade-in)
        public static void FadeArtIn()
        {
            if (art == null) return;
            var keys = new DoubleAnimationUsingKeyFrames();
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            keys.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            keys.Completed += (s, e) => { if (video == null) art.BeginAnimation(UIElement.OpacityProperty, null); };
            art.BeginAnimation(UIElement.OpacityProperty, keys);
        }

        public static void Stop() { Stop(false); }

        // `moved`: another game was picked, so the art waits for its picture before it comes back (it showed the last
        // game's art for a moment)
        private static void Stop(bool moved)
        {
            if (arm != null) arm.Stop();
            var old = video;
            video = null;
            if (rsHint != null) rsHint.Text = "Filter";
            if (soundOn) { soundOn = false; HomeNav.TrailerSound(false); }
            if (old != null)
            {
                Fade(false, moved);
                var gone = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };   // after the fade, whatever came next
                gone.Tick += (s, e) => { gone.Stop(); old.Stop(); old.Source = null; host.Children.Remove(old); };
                gone.Start();
                if (library != null && library.IsVisible) HomeNav.SetMusic("home");
            }
        }

        // the art has a picture (it covers the wallpaper completely then)
        private static bool ArtShowing()
        {
            if (art == null) return false;
            var source = art.GetType().GetProperty("Source");
            return source != null && source.GetValue(art, null) != null;
        }

        private static void Fade(bool on, bool moved = false)
        {
            var fade = new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 700 : 250)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            // the game's art and the wallpaper step aside while the trailer plays (dark on the left), and come back after.
            // Under a picture the wallpaper goes and comes back unseen (fading it with the art let it show through)
            bool covered = ArtShowing();
            int artDelay = !on && moved ? 260 : 0;   // the next game's picture loads first (SourceUpdateDelay 120 + decoding)
            foreach (var el in new[] { art, wallpaper, baseLayer })
            {
                if (el == null) continue;
                bool under = el != art;
                if (on)
                {
                    var ms = under && covered ? 0 : 700;
                    el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
                else
                {
                    // fades back to its own opacity (1 for the art, the theme's for the wallpaper), then lets go; what's
                    // under the art comes back once the art is up
                    var target = el == wallpaper ? 0.6 : 1.0;
                    var back = new DoubleAnimation(target, TimeSpan.FromMilliseconds(under && covered ? 0 : 300))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(artDelay + (under && covered ? 300 : 0)),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    var who = el;
                    back.Completed += (s, e) => who.BeginAnimation(UIElement.OpacityProperty, null);
                    el.BeginAnimation(UIElement.OpacityProperty, back);
                }
            }
            host.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
