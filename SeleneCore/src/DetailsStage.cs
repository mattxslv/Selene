using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace Selene
{
    // The game details page's lower half, filled by Selene: the game's screenshots, your captures of it, your activity
    // with it, and more games like it from your own collection. Down from the Play / Options pills goes into those bands
    // one at a time (the page glides up), Left / Right move along a band, A opens what's picked, Up / B come back up.
    public static class DetailsStage
    {
        private const double BandTop = 720, BandPitch = 230;   // (the next band starts at the foot of the screen: it peeks in, cut off and blurred)
        private static FrameworkElement details;
        private static TranslateTransform shift;
        private static readonly List<Tile>[] tiles = { new List<Tile>(), new List<Tile>(), new List<Tile>() };   // screenshots, captures, more
        private static readonly List<Action>[] opens = { new List<Action>(), new List<Action>(), new List<Action>() };
        private static readonly int[] at = new int[3];
        private static int zone;   // 0: the pills (Playnite's focus), 1..3: the bands
        private static Guid shownId;
        private static DispatcherTimer shotsWatch;
        private static int shotsSeen;

        public static void Attach(Window w)
        {
            details = HomeNav.FindByName(w, "PART_ElemGameDetails") as FrameworkElement;
            if (details == null) { HomeNav.Log("details stage: no details view"); return; }
            details.IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) Opened(); else Closed(); };
            var npc = w.DataContext as System.ComponentModel.INotifyPropertyChanged;
            if (npc != null) npc.PropertyChanged += (s, e) => { if (e.PropertyName == "SelectedGame" && details.IsVisible) details.Dispatcher.BeginInvoke(new Action(Opened), DispatcherPriority.Background); };
            if (details.IsVisible) Opened();
        }

        private static T Part<T>(string name) where T : class { return details != null ? HomeNav.FindByName(details, name) as T : null; }

        private static Game Selected()
        {
            try { return HomeNav.Api != null ? HomeNav.Api.MainView.SelectedGames.FirstOrDefault() : null; }
            catch { return null; }
        }

        private static void Opened()
        {
            var g = Selected();
            if (g == null) return;
            // the page's parts are in the template, made once the view shows
            details.Dispatcher.BeginInvoke(new Action(() =>
            {
                var page = Part<Grid>("SeleneDetailsPage");
                shift = page != null ? page.RenderTransform as TranslateTransform : null;
                if (page == null) { HomeNav.Log("details stage: no page in the template"); return; }
                // what the glide moves is cached in pieces: the top block and each band's pictures are textures, the bands' blur
                // and fade animate over them (one cache for the whole page went soft and had to be redrawn for every step)
                page.CacheMode = null;
                foreach (var name in new[] { "SeleneDetailsTop", "SeleneDetailsShots", "SeleneDetailsCaptures", "SeleneDetailsMore" })
                {
                    var el = Part<FrameworkElement>(name);
                    if (el != null && el.CacheMode == null) el.CacheMode = new System.Windows.Media.BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = true };
                }
                var activity = Part<Border>("SeleneDetailsBand2b");
                if (activity != null && activity.Child != null && activity.Child.CacheMode == null) activity.Child.CacheMode = new System.Windows.Media.BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = true };
                if (shownId != g.Id || zone == 0) { zone = 0; if (shift != null) { shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = 0; } }
                if (shownId != g.Id) GameMedia.OnSelected(g, null);   // the screenshots follow Spotlight's row; here the game may be another (Collection's pick)
                shownId = g.Id;
                Fill(g);
                Blur(false);
                HomeNav.Log("details stage: " + g.Name + " (bands " + tiles[0].Count + "/" + tiles[1].Count + "/" + tiles[2].Count + ")");
                var idle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                idle.Tick += (s2, e2) => { idle.Stop(); if (details.IsVisible) Ui.MeasureFrames("details at rest", 500); };
                idle.Start();
                HideUnder(true);
                var f = Keyboard.FocusedElement as DependencyObject;
                if (f == null || !details.IsAncestorOf(f)) FocusPills();
            }), DispatcherPriority.Loaded);
        }

        // Collection (the grid, its covers' shadows, its big art) lies under the page and was redrawn with every frame the page
        // moved, though nothing of it shows; hidden while the page is up, back the moment it goes
        private static UIElement hiddenUnder;
        private static void HideUnder(bool on)
        {
            try
            {
                if (on)
                {
                    var w = Window.GetWindow(details);
                    var lib = w != null ? HomeNav.FindByName(w, "SeleneLibrary") as UIElement : null;
                    if (lib == null || lib.Visibility != Visibility.Visible || hiddenUnder != null) return;
                    hiddenUnder = lib;
                    lib.Visibility = Visibility.Hidden;
                }
                else if (hiddenUnder != null)
                {
                    if (hiddenUnder.Visibility == Visibility.Hidden) hiddenUnder.Visibility = Visibility.Visible;
                    hiddenUnder = null;
                }
            }
            catch (Exception ex) { HomeNav.Log("details stage: hide under " + ex.Message); }
        }

        private static void Closed()
        {
            HideUnder(false);
            zone = 0;
            if (shotsWatch != null) shotsWatch.Stop();
            if (shift != null) { shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = 0; }
        }

        // ---- filling ----
        private static void Fill(Game g)
        {
            FillShots();
            FillCaptures(g);
            FillActivity(g);
            FillMore(g);
            ShowFocus();
            // the screenshots arrive a moment after the selection: looked at again for a while
            shotsSeen = GameMedia.Current.Count;
            if (shotsWatch == null) { shotsWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) }; shotsWatch.Tick += (s, e) => { if (details == null || !details.IsVisible) { shotsWatch.Stop(); return; } if (GameMedia.Current.Count != shotsSeen) { shotsSeen = GameMedia.Current.Count; FillShots(); ShowFocus(); } }; }
            shotsWatch.Stop();
            shotsWatch.Start();
        }

        private static void Band(string panelName, string labelName, int zoneIndex, bool any)
        {
            var label = Part<TextBlock>(labelName);
            if (label != null) label.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void FillShots()
        {
            var panel = Part<StackPanel>("SeleneDetailsShots");
            if (panel == null) return;
            panel.Children.Clear(); tiles[0].Clear(); opens[0].Clear();
            var shots = GameMedia.Current.Take(6).ToList();
            for (int i = 0; i < shots.Count; i++)
            {
                var t = new Tile(272, 153, 12, 5) { FocusScale = 1.05 };
                t.Root.Margin = new Thickness(0, 0, 20, 0);
                if (shots[i].Thumb != null) t.Face.Child = new Border { CornerRadius = new CornerRadius(11), Background = new ImageBrush(shots[i].Thumb) { Stretch = Stretch.UniformToFill } };
                panel.Children.Add(t.Root);
                tiles[0].Add(t);
                int idx = i;
                opens[0].Add(() => GameMedia.OpenViewerAt(idx));
                Ui.Rise(t.Root, i * 40, 10, 240);
            }
            Band("SeleneDetailsShots", "SeleneDetailsShotsLabel", 1, shots.Count > 0);
            if (at[0] >= tiles[0].Count) at[0] = 0;
        }

        private static void FillCaptures(Game g)
        {
            var panel = Part<StackPanel>("SeleneDetailsCaptures");
            var note = Part<TextBlock>("SeleneDetailsCapturesNote");
            if (panel == null) return;
            panel.Children.Clear(); tiles[1].Clear(); opens[1].Clear();
            var mine = CapturesScreen.Known.Where(c => string.Equals(c.Game, g.Name, StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
            if (note != null) note.Visibility = Visibility.Collapsed;
            if (mine.Count == 0)
            {
                // nothing of this game yet: one quiet tile says so and opens Captures (the band stays reachable, with the activity beside it)
                var empty = new Tile(272, 153, 12, 5) { FocusScale = 1.05 };
                empty.Root.Margin = new Thickness(0, 0, 20, 0);
                empty.Face.Background = Ink.Brush("SeleneGlassColor", 0.35);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                words.Children.Add(new TextBlock { Text = "", FontFamily = Ui.Glyphs, FontSize = 26, Foreground = Ink.Brush("SeleneInkColor", 0.55), HorizontalAlignment = HorizontalAlignment.Center });
                words.Children.Add(new TextBlock { Text = "No captures of this game yet", FontSize = 14, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), Margin = new Thickness(0, 8, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 });
                empty.Face.Child = words;
                panel.Children.Add(empty.Root);
                tiles[1].Add(empty);
                opens[1].Add(() => LoginScene.OpenWith(() => new CapturesScreen()));
                Ui.Rise(empty.Root, 0, 10, 240);
            }
            var files = new List<string>(); var where = new List<int>();
            for (int i = 0; i < mine.Count; i++)
            {
                var c = mine[i];
                var t = new Tile(272, 153, 12, 5) { FocusScale = 1.05 };
                t.Root.Margin = new Thickness(0, 0, 20, 0);
                var still = c.IsVideo ? Thumbnails.Cached(c.Path) : c.Path;
                if (still != null) { files.Add(still); where.Add(i); }
                else if (c.IsVideo) t.Face.Child = new TextBlock { Text = "\uE768", FontFamily = Ui.Glyphs, FontSize = 36, Foreground = Ink.Brush("SeleneInkColor", 0.85), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (c.IsVideo)
                {
                    var badge = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Padding = new Thickness(7, 3, 7, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0), Child = new TextBlock { Text = "\uE768", FontFamily = Ui.Glyphs, FontSize = 12, Foreground = Brushes.White } };
                    Panel.SetZIndex(badge, 2);
                    t.Root.Children.Insert(1, badge);
                }
                panel.Children.Add(t.Root);
                tiles[1].Add(t);
                var path = c.Path;
                opens[1].Add(() => LoginScene.OpenWith(() => new CapturesScreen(path)));
                Ui.Rise(t.Root, i * 40, 10, 240);
            }
            var list = tiles[1];
            if (files.Count > 0) Ui.LoadInOrder(files, 420, (i, pic) =>
            {
                if (where[i] >= list.Count || !ReferenceEquals(list, tiles[1])) return;
                var fill = new Border { CornerRadius = new CornerRadius(11), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill }, Opacity = 0 };
                list[where[i]].Face.Child = fill;
                Ui.Fade(fill, 1, 240);
            });
            if (at[1] >= tiles[1].Count) at[1] = 0;
        }

        private static void FillActivity(Game g)
        {
            var playtime = Part<TextBlock>("SeleneDetailsPlaytime");
            var last = Part<TextBlock>("SeleneDetailsLast");
            var week = Part<TextBlock>("SeleneDetailsWeek");
            var rank = Part<TextBlock>("SeleneDetailsRank");
            if (playtime == null) return;
            var secs = GameFacts.Seconds(g);
            var when = GameFacts.LastPlayed(g);
            playtime.Text = GameFacts.Hours(secs);
            last.Text = GameFacts.Ago(when);
            week.Text = when.HasValue && when.Value > DateTime.Now.AddDays(-7) ? "Yes" : "Not yet";
            if (secs == 0) rank.Text = "—";
            else
            {
                var order = GameFacts.Games().Where(x => GameFacts.Seconds(x) > 0).OrderByDescending(GameFacts.Seconds).ToList();
                int n = order.FindIndex(x => x.Id == g.Id) + 1;
                rank.Text = n > 0 ? "#" + n + " of " + order.Count : "—";
            }
        }

        private static void FillMore(Game g)
        {
            var panel = Part<StackPanel>("SeleneDetailsMore");
            if (panel == null) return;
            panel.Children.Clear(); tiles[2].Clear(); opens[2].Clear();
            var all = GameFacts.Games().Where(x => x.Id != g.Id).ToList();
            var devs = new HashSet<Guid>(g.DeveloperIds ?? new List<Guid>());
            var genres = new HashSet<Guid>(g.GenreIds ?? new List<Guid>());
            Func<Game, int> score = x =>
            {
                int s = 0;
                if (x.DeveloperIds != null && x.DeveloperIds.Any(devs.Contains)) s += 3;
                if (x.GenreIds != null) s += x.GenreIds.Count(genres.Contains);
                if (x.IsInstalled) s += 1;
                return s;
            };
            var like = all.Select(x => new { Game = x, Score = score(x) }).Where(x => x.Score >= 2).OrderByDescending(x => x.Score).ThenByDescending(x => GameFacts.Seconds(x.Game)).Take(8).Select(x => x.Game).ToList();
            if (like.Count < 8)
            {
                // few close matches (a game with one genre, or none): one shared genre counts, then the games you play most
                var loose = all.Where(x => !like.Contains(x)).Select(x => new { Game = x, Score = score(x) }).Where(x => x.Score >= 1).OrderByDescending(x => x.Score).ThenByDescending(x => GameFacts.Seconds(x.Game)).Select(x => x.Game);
                foreach (var x in loose) { if (like.Count >= 8) break; like.Add(x); }
                foreach (var x in all.Where(x => !like.Contains(x)).OrderByDescending(x => GameFacts.Seconds(x))) { if (like.Count >= 8) break; if (GameFacts.Seconds(x) > 0) like.Add(x); }
            }
            Band("SeleneDetailsMore", "SeleneDetailsMoreLabel", 3, like.Count > 0);
            for (int i = 0; i < like.Count; i++)
            {
                var x = like[i];
                // big covers, no names: the cover says which game it is
                var t = GameFacts.CoverTile(x, 200, 280);
                var item = new Grid { Width = 200, Margin = new Thickness(0, 0, 22, 0) };
                item.Children.Add(t.Root);
                panel.Children.Add(item);
                tiles[2].Add(t);
                var id = x.Id;
                opens[2].Add(() =>
                {
                    // that game on Spotlight (the details close first)
                    try { PlayniteMenu.Main("ToggleGameDetailsCommand"); } catch (Exception ex) { HomeNav.Log("details stage: " + ex.Message); }
                    var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    later.Tick += (s, e) => { later.Stop(); HomeNav.ShowGame(id); };
                    later.Start();
                });
                Ui.Rise(item, i * 35, 10, 240);
            }
            GameFacts.FillCovers(like, tiles[2], 400);
            if (at[2] >= tiles[2].Count) at[2] = 0;
        }

        // ---- moving ----
        private static bool Active { get { return details != null && details.IsVisible && !LoginScene.IsActive && !GameMedia.ViewerOpen; } }

        private static int NextBand(int from, int dir)
        {
            for (int z = from + dir; z >= 1 && z <= 3; z += dir) if (tiles[z - 1].Count > 0) return z;
            return -1;
        }

        private static void ShowFocus()
        {
            for (int z = 0; z < 3; z++)
                for (int i = 0; i < tiles[z].Count; i++) tiles[z][i].Focus(zone == z + 1 && i == at[z]);
        }

        private static void Glide()
        {
            if (shift == null) return;
            // the last band (tall covers) rises further, so the whole covers sit above the hints
            double to = zone <= 1 ? 0 : -(zone - 1) * BandPitch - (zone == 3 ? 100 : 0);
            var glide = new DoubleAnimation(to, TimeSpan.FromMilliseconds(320)) { EasingFunction = Ui.Out() };
            Timeline.SetDesiredFrameRate(glide, Ui.RefreshHz);   // the page is big: at WPF's 60 steps a second the move looked choppy on a fast screen
            shift.BeginAnimation(TranslateTransform.YProperty, glide);
            Blur(true);
            Ui.MeasureFrames("details glide to zone " + zone, 420);   // the next band sharpens and brightens as it rises, with the glide (the store's rows do the same)
        }

        // the bands below the one in view are blurred (they peek in at the foot of the screen, as the store's rows do)
        private static void Blur(bool animate)
        {
            int shown = Math.Max(zone, 1);
            var ms = TimeSpan.FromMilliseconds(animate ? 260 : 0);
            bool noBlur = System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-noblur"));   // dev: the peek without its blur, to compare
            foreach (var name in new[] { "SeleneDetailsBand1", "SeleneDetailsBand2", "SeleneDetailsBand2b", "SeleneDetailsBand3" })
            {
                var band = Part<FrameworkElement>(name);
                if (band == null) continue;
                int z = name.EndsWith("b") ? 2 : name[name.Length - 1] - 48;
                bool below = z > shown;
                var blur = band.Effect as System.Windows.Media.Effects.BlurEffect;
                if (blur == null) { blur = new System.Windows.Media.Effects.BlurEffect { Radius = 0, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance }; band.Effect = blur; }
                blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(below && !noBlur ? 5 : 0, ms));
                Fade(band, below, ms);
            }
        }

        // a band below the one in view fades out towards its foot (it peeks in at the foot of the screen); a band in view has no mask
        // at all, so the glide only moves finished pictures
        private static void Fade(FrameworkElement band, bool on, TimeSpan ms)
        {
            var mask = band.OpacityMask as LinearGradientBrush;
            if (!on && mask == null) return;
            if (mask == null)
            {
                mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 118), MappingMode = BrushMappingMode.Absolute };
                mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
                mask.GradientStops.Add(new GradientStop(Colors.Black, 1));
                band.OpacityMask = mask;
            }
            var top = on ? Color.FromArgb(0xE0, 0, 0, 0) : Colors.Black;
            var foot = on ? Color.FromArgb(0, 0, 0, 0) : Colors.Black;
            var a1 = new ColorAnimation(top, ms);
            var a2 = new ColorAnimation(foot, ms);
            if (!on) a2.Completed += (s, e) => { if (ReferenceEquals(band.OpacityMask, mask)) { mask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null); mask.GradientStops[1].BeginAnimation(GradientStop.ColorProperty, null); band.OpacityMask = null; } };
            mask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, a1);
            mask.GradientStops[1].BeginAnimation(GradientStop.ColorProperty, a2);
        }

        private static bool InDescription(DependencyObject d)
        {
            for (; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var fe = d as FrameworkElement;
                if (fe != null && fe.Name == "PART_ScrollHtmlDescription") return true;
                if (fe != null && fe == details) return false;
            }
            return false;
        }

        private static void FocusPills()
        {
            var pill = Part<UIElement>("PART_ButtonContext");
            if (pill != null) pill.Focus();
        }

        public static bool Handle(string b)
        {
            if (!Active) { zone = 0; return false; }
            // B from Spotlight's details: the home comes back under the page before it closes (nothing else shows in between)
            if (b == "B") HideUnder(false);   // whatever closes the page next finds Collection as it was
            if (b == "B" && HomeNav.CloseDetailsToHub()) { zone = 0; return true; }
            if (zone == 0)
            {
                if (b != "DPadDown") return false;
                int z = NextBand(0, 1);
                HomeNav.Log("details stage: Down from the top, next band " + z + " (bands " + tiles[0].Count + "/" + tiles[1].Count + "/" + tiles[2].Count + "), focus " + (Keyboard.FocusedElement is FrameworkElement ? ((FrameworkElement)Keyboard.FocusedElement).Name + "/" + Keyboard.FocusedElement.GetType().Name : "none"));
                if (z < 0) return false;
                // from anywhere on the page but the description (Down there scrolls it); opened from Collection the focus
                // sometimes sat on nothing at all, and Down did nothing
                var focused = Keyboard.FocusedElement as DependencyObject;
                if (focused != null && InDescription(focused)) return false;
                zone = z;
                Ui.Tick();
                Glide();
                ShowFocus();
                return true;
            }
            var list = tiles[zone - 1];
            if (b.StartsWith("DPad")) HomeNav.Log("details stage: " + b + " in zone " + zone + " (bands " + tiles[0].Count + "/" + tiles[1].Count + "/" + tiles[2].Count + ")");
            switch (b)
            {
                case "DPadLeft":
                case "DPadRight":
                {
                    int next = at[zone - 1] + (b == "DPadRight" ? 1 : -1);
                    if (next < 0 || next >= list.Count) { if (list.Count > 0) Ui.Shake(list[at[zone - 1]].Root); return true; }
                    at[zone - 1] = next;
                    Ui.Tick();
                    ShowFocus();
                    return true;
                }
                case "DPadDown":
                {
                    int z = NextBand(zone, 1);
                    if (z < 0) return true;
                    zone = z; Ui.Tick(); Glide(); ShowFocus();
                    return true;
                }
                case "DPadUp":
                {
                    int z = NextBand(zone, -1);
                    if (z < 0) { zone = 0; Ui.Tick(); Glide(); ShowFocus(); FocusPills(); return true; }
                    zone = z; Ui.Tick(); Glide(); ShowFocus();
                    return true;
                }
                case "B":
                    // B leaves the page from a band too (Playnite closes it); the page settles back first
                    zone = 0; Glide(); ShowFocus();
                    return false;
                case "A":
                {
                    int i = at[zone - 1];
                    if (i < opens[zone - 1].Count) { Ui.Confirm(); opens[zone - 1][i](); }
                    return true;
                }
            }
            return false;
        }
    }
}
