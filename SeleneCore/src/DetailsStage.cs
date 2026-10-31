using System;
using System.Text.RegularExpressions;
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
        private static Border sink;
        private static System.Windows.Input.ICommand playniteOptions;   // Playnite's own Game Options, kept for when Selene's is off   // the keyboard focus while a band has Selene's focus (on a pill, that pill lit up too)

        public static bool InBand { get { return details != null && details.IsVisible && zone > 0; } }

        public static void FocusBand() { if (sink != null) sink.Focus(); }
        private static Guid shownId;
        private static DispatcherTimer shotsWatch;
        private static int shotsSeen;
        private static Guid shotsFor;   // the game whose pictures the band shows

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

        // the right stick scrolls the description, smoothly and faster the further it's pushed (Playnite gives the stick's
        // presses only, not its tilt: read from the pad itself while the page is up)
        private static StickScroll stickScroll;
        private static void WatchStick(bool on)
        {
            if (stickScroll == null) stickScroll = new StickScroll(() => Part<ScrollViewer>("PART_ScrollHtmlDescription"), () => Active) { TopSpeed = 240 };   // (from anywhere on the page)
            if (on) stickScroll.Start(); else stickScroll.Stop();
        }

        private static void Opened()
        {
            HomeNav.QuietDetailsTransition();
            var g = Selected();
            if (g == null) return;
            WatchStick(true);
            // the page's parts are in the template, made once the view shows
            details.Dispatcher.BeginInvoke(new Action(() =>
            {
                var page = Part<Grid>("SeleneDetailsPage");
                shift = page != null ? page.RenderTransform as TranslateTransform : null;
                if (page == null) { HomeNav.Log("details stage: no page in the template"); return; }
                HltbFetch.ForPage(g);
                // Game Options: Selene's screen (Playnite's own menu when that's switched off)
                var options = Part<System.Windows.Controls.Primitives.ButtonBase>("PART_ButtonOptions");
                if (options != null)
                {
                    if (playniteOptions == null && !(options.Command is SeleneShell.Relay)) playniteOptions = options.Command;
                    var gid = g.Id;
                    options.Command = new SeleneShell.Relay(() => { if (!GameOptionsScreen.Open(gid) && playniteOptions != null && playniteOptions.CanExecute(null)) playniteOptions.Execute(null); });
                }
                SeleneShell.Switch(Part<UIElement>("SeleneAchievementsStat"), "achievements");
                SeleneShell.Switch(Part<UIElement>("SeleneAchievementsHint"), "achievements");
                // the description never takes the focus (the right stick scrolls it): inside it Playnite's text view moved a hidden
                // caret line by line and jumped the view to it, and the focus went back and forth between it and Play
                foreach (var name in new[] { "PART_ScrollHtmlDescription", "PART_HtmlDescription" })
                {
                    var el = Part<FrameworkElement>(name);
                    if (el != null) { el.Focusable = false; KeyboardNavigation.SetIsTabStop(el, false); }
                }
                if (sink == null || !ReferenceEquals(sink.Parent, page))
                {
                    sink = new Border { Width = 0, Height = 0, Focusable = true, FocusVisualStyle = null, IsHitTestVisible = false };
                    page.Children.Add(sink);
                }
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
                // the screenshots follow Spotlight's row; here the game may be another (Collection's pick), or the row moved on since
                // this page was last open: its own game's, loaded now
                if (shownId != g.Id || GameMedia.ShownId != g.Id) GameMedia.ShowNow(g);
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
            WatchStick(false);
            HideUnder(false);
            zone = 0;
            if (shotsWatch != null) shotsWatch.Stop();
            if (shift != null) { shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = 0; }
        }

        // ---- filling ----
        // the description as words only: store pages put banners, GIFs and videos in it, which in this small glass box were
        // cut pieces of pictures (and loaded from the web as it scrolled, so it jumped). Playnite's own text is replaced.
        private static void FillDescription(Game g)
        {
            var view = Part<FrameworkElement>("PART_HtmlDescription");
            if (view == null) return;
            var prop = System.ComponentModel.DependencyPropertyDescriptor.FromName("HtmlText", view.GetType(), view.GetType());
            if (prop == null) return;
            var words = string.IsNullOrEmpty(g.Description) ? "" : WordsOnly(g.Description);   // (none: empty, not the last game's)
            if (Equals(view.GetValue(prop.DependencyProperty), words)) return;
            view.SetValue(prop.DependencyProperty, words);
            var scroller = Part<ScrollViewer>("PART_ScrollHtmlDescription");
            if (scroller != null) scroller.ScrollToTop();   // a new game's words from their start
        }

        internal static string WordsOnly(string html)
        {
            var s = Regex.Replace(html, @"<(video|iframe|object|picture|svg)\b.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<(img|source|embed|video|iframe)\b[^>]*>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<a\b[^>]*>\s*</a>", "", RegexOptions.IgnoreCase);   // links that only held a picture
            s = Regex.Replace(s, @"<span\b[^>]*>\s*</span>", "", RegexOptions.IgnoreCase);   // the boxes pictures sat in
            s = Regex.Replace(s, @"(\s*<br\s*/?>\s*){3,}", "<br><br>", RegexOptions.IgnoreCase);   // the gaps the pictures left
            s = Regex.Replace(s, @"<p\b[^>]*>\s*(<br\s*/?>\s*)*</p>", "", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"^(\s*<br\s*/?>)+", "", RegexOptions.IgnoreCase).Trim();
        }

        // who made it, without the company's legal ending ("IO Interactive A/S", "Valve Corporation", "Ubisoft Inc.")
        private static void FillDevelopers(Game g)
        {
            var line = Part<TextBlock>("SeleneDevelopers");
            if (line == null) return;
            var names = (g.Developers ?? new List<Company>()).Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name)).Select(c => PlainCompany(c.Name)).Distinct().ToList();
            line.Text = string.Join(", ", names);
        }

        internal static string PlainCompany(string name)
        {
            var s = Regex.Replace(name.Trim(), @"[,\s]+(A/S|AS|AB|ApS|Inc\.?|Incorporated|Ltd\.?|Limited|LLC|L\.L\.C\.|GmbH|AG|S\.?A\.?S?|S\.r\.l\.|SRL|Co\.,?\s*Ltd\.?|Co\.|Corp\.?|Corporation|Pty\.?\s*Ltd\.?|K\.K\.|B\.V\.|BV|Oy|Sp\.\s*z\s*o\.o\.|PLC|plc)$", "", RegexOptions.IgnoreCase).Trim().TrimEnd(',');
            return s.Length > 0 ? s : name.Trim();
        }

        private static void Fill(Game g)
        {
            FillDescription(g);
            FillDevelopers(g);
            FillShots();
            FillCaptures(g);
            FillActivity(g);
            FillMore(g);
            ShowFocus();
            // the screenshots arrive a moment after the selection: looked at again for a while
            shotsSeen = GameMedia.Current.Count; shotsFor = GameMedia.ShownId;
            // (by the game they belong to as well as their count: another game's cached list of the same length never changed the count)
            if (shotsWatch == null) { shotsWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) }; shotsWatch.Tick += (s, e) => { if (details == null || !details.IsVisible) { shotsWatch.Stop(); return; } if (GameMedia.Current.Count != shotsSeen || GameMedia.ShownId != shotsFor) { shotsSeen = GameMedia.Current.Count; shotsFor = GameMedia.ShownId; FillShots(); ShowFocus(); } }; }
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
            // only this game's pictures (the list still holds the game before's until its own are loaded)
            var shots = GameMedia.Current.Take(GameMedia.ShownId == shownId ? 6 : 0).ToList();
            for (int i = 0; i < shots.Count; i++)
            {
                var t = new Tile(272, 153, 12, 5) { FocusScale = 1.05 };
                t.Root.Margin = new Thickness(0, 0, 20, 0);
                if (shots[i].Thumb != null) t.Face.Child = new Border { CornerRadius = new CornerRadius(11), Background = new ImageBrush(shots[i].Thumb) { Stretch = Stretch.UniformToFill } };
                panel.Children.Add(t.Root);
                tiles[0].Add(t);
                BindPointer(t, 0, tiles[0].Count - 1);
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
            var key = CapturesScreen.Fold(g.Name);   // (as a filename carries the name: "Onimusha_ Way of the Sword" is this game's)
            var mine = CapturesScreen.Known.Where(c => CapturesScreen.Fold(c.Game) == key).Take(4).ToList();
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
                BindPointer(empty, 1, 0);
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
                if (still == null && c.IsVideo)
                {
                    // not made yet (only Captures made them before): asked for, and put in when it comes
                    var tile = t; var owner = tiles[1];
                    Thumbnails.Request(c.Path, made =>
                    {
                        if (made == null || !ReferenceEquals(owner, tiles[1])) return;
                        try
                        {
                            var pic = new System.Windows.Media.Imaging.BitmapImage();
                            pic.BeginInit(); pic.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; pic.DecodePixelWidth = 560; pic.UriSource = new Uri(made); pic.EndInit(); pic.Freeze();
                            var fill = new Border { CornerRadius = new CornerRadius(11), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill }, Opacity = 0 };
                            tile.Face.Child = fill;
                            Ui.Fade(fill, 1, 240);
                        }
                        catch (Exception ex) { HomeNav.Log("details: capture still: " + ex.Message); }
                    });
                }
                if (c.IsVideo)
                {
                    var badge = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Padding = new Thickness(7, 3, 7, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0), Child = new TextBlock { Text = "\uE768", FontFamily = Ui.Glyphs, FontSize = 12, Foreground = Brushes.White } };
                    Panel.SetZIndex(badge, 2);
                    t.Root.Children.Insert(1, badge);
                }
                panel.Children.Add(t.Root);
                tiles[1].Add(t);
                BindPointer(t, 1, tiles[1].Count - 1);
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
                BindPointer(t, 2, tiles[2].Count - 1);
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
        internal static bool Active { get { return details != null && details.IsVisible && !LoginScene.IsActive && !GameMedia.ViewerOpen; } }

        private static int NextBand(int from, int dir)
        {
            for (int z = from + dir; z >= 1 && z <= 3; z += dir) if (tiles[z - 1].Count > 0) return z;
            return -1;
        }

        private static void BindPointer(Tile tile, int band, int index)
        {
            tile.Root.MouseLeftButtonUp += (s, e) =>
            {
                if (!Active || LoginScene.IsActive) return;
                zone = band + 1; at[band] = index; ShowFocus(); Handle("A"); e.Handled = true;
            };
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
            Timeline.SetDesiredFrameRate(glide, Ui.FrameRate);   // the page is big: at WPF's 60 steps a second the move looked choppy on a fast screen
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

        internal static void ScrollPage(bool down)
        {
            var scroller = Part<ScrollViewer>("PART_ScrollHtmlDescription");
            if (scroller != null) { if (down) scroller.PageDown(); else scroller.PageUp(); }
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
            // the right stick only scrolls the description here (StickScroll reads it directly): its directions reaching
            // Playnite moved the focus (Up put it on Play)
            if (Active && b.StartsWith("RightStick") && b != "RightStick") return true;
            if (b == "Start" && Active) { SeleneShell.OpenMenu(); return true; }   // the menu over the page, as everywhere
            if (!Active) { zone = 0; return false; }
            // Y: the game's achievements (the hint shows when Playnite Achievements has them)
            if (b == "Y" && Features.On("achievements") && AchievementsData.Available())
            {
                var hint = Part<UIElement>("SeleneAchievementsHint");
                var g = Selected();
                if (g != null && hint != null && hint.IsVisible) { Ui.Confirm(); var id = g.Id; LoginScene.OpenWith(() => new AchievementsScreen(id, null)); return true; }
            }
            // B from Spotlight's details: the home comes back under the page before it closes (nothing else shows in between)
            if (b == "B") HideUnder(false);   // whatever closes the page next finds Collection as it was
            if (b == "B" && HomeNav.CloseDetailsToHub()) { zone = 0; return true; }
            if (zone == 0)
            {
                // the focus in the description after all (it can't take it any more, but Playnite's first focus): back on the buttons
                var inText = Keyboard.FocusedElement as DependencyObject;
                if (b.StartsWith("DPad") && inText != null && InDescription(inText)) { FocusPills(); return true; }
                if (b != "DPadDown") return false;
                int z = NextBand(0, 1);
                HomeNav.Log("details stage: Down from the top, next band " + z + " (bands " + tiles[0].Count + "/" + tiles[1].Count + "/" + tiles[2].Count + "), focus " + (Keyboard.FocusedElement is FrameworkElement ? ((FrameworkElement)Keyboard.FocusedElement).Name + "/" + Keyboard.FocusedElement.GetType().Name : "none"));
                if (z < 0) return false;
                // from anywhere on the page but the description (Down there scrolls it); opened from Collection the focus
                // sometimes sat on nothing at all, and Down did nothing
                zone = z;
                Ui.Tick();
                Glide();
                ShowFocus();
                FocusBand();   // the keyboard focus leaves the buttons too (Play stayed lit with the screenshots chosen)
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
