using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Selene
{
    // Finding a game (hold View): Selene's keyboard (the one the profile name uses, same sounds) with the matching games
    // right above it as you type. Up goes into the results, A picks the game where the search was opened (Spotlight's
    // row, or Collection); X erases, Y types a space, Start opens the first result, B closes.
    public class SearchScreen : Screen
    {
        // one match: how its tile looks and what A on it does (games: a cover that opens the game; settings: a card)
        public sealed class Hit { public Func<Tile> Build; public Action Open; public string Key; }   // Key: the same match across searches (kept in place)

        // what is searched: the words around the field and the matches for a query (the games by default)
        public sealed class Finder
        {
            public string Title, Placeholder, EmptyNote, NoneNote;
            public Func<string, List<Hit>> Find;
            public Action Back;   // B (null: back to where the search was opened from)
            public int Max = MaxResults;   // how many matches show (one row that glides sideways to the focused one)
            public Brush Shade;   // the shade over what's behind (null: the usual one)
        }

        public override bool MenuOnStart { get { return false; } }   // (Start is its own here)
        private static readonly string[] Rows = { "1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm-." };
        private const int MaxLength = 30, MaxResults = 24;
        private readonly StackPanel letters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border field;
        private readonly TextBlock placeholder;
        private readonly List<List<Tile>> keys = new List<List<Tile>>();
        private readonly List<List<string>> values = new List<List<string>>();
        private readonly List<List<double>> centers = new List<List<double>>();
        private readonly StackPanel resultsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Height = 196 };
        private readonly TranslateTransform resultsShift = new TranslateTransform();
        private readonly Grid resultsArea;
        private double resultsX;
        private bool rowFits = true;
        private readonly List<string> resultKeys = new List<string>();
        private readonly TextBlock resultsNote;
        private readonly List<Tile> resultTiles = new List<Tile>();
        private readonly List<Hit> results = new List<Hit>();
        private readonly Finder finder;
        private readonly DispatcherTimer later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private string query = "";
        private int row = 1, col, result = -1;   // result >= 0: the focus is on the results

        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return finder.Shade; } }

        public SearchScreen() : this(Games()) { }

        public SearchScreen(Finder finder)
        {
            this.finder = finder;
            var layout = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 90, 0, 0) };
            layout.Children.Add(new TextBlock { Text = finder.Title, FontSize = 40, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 24) });
            var caret = new Rectangle { Width = 2, Height = 30, Fill = Ink.Brush("GlyphColor"), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            caret.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1100),
                KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)), new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.55)) }
            });
            var line = new Grid { Margin = new Thickness(26, 0, 26, 0) };
            var typed = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            typed.Children.Add(new TextBlock { Text = "", FontFamily = Ui.Glyphs, FontSize = 22, Foreground = Ink.Brush("SeleneInkColor", 0.6), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
            typed.Children.Add(letters);
            typed.Children.Add(caret);
            line.Children.Add(typed);
            placeholder = new TextBlock { Text = finder.Placeholder, FontSize = 24, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40, 0, 0, 0), IsHitTestVisible = false };
            line.Children.Add(placeholder);
            field = new Border
            {
                Width = 760, Height = 68, CornerRadius = new CornerRadius(34), Child = line, HorizontalAlignment = HorizontalAlignment.Center,
                Background = Ink.Brush("SeleneInkColor", 0.1), BorderBrush = Ink.Brush("SeleneFocusColor"), BorderThickness = new Thickness(2)
            };
            layout.Children.Add(field);

            // the matching games
            // (one row as wide as the screen allows: more matches than fit glide in as the focus moves along)
            resultsArea = new Grid { Height = 220, Width = Math.Max(800, Ui.ViewWidth - 160), Margin = new Thickness(0, 26, 0, 0), ClipToBounds = true };
            resultsPanel.RenderTransform = resultsShift;
            // (on a Canvas: in the Grid the row was laid out at the area's width and clipped there, so the matches past the
            // first screenful never came into view as it glided)
            var rowHost = new Canvas();
            Canvas.SetTop(resultsPanel, (220 - 196) / 2.0);
            rowHost.Children.Add(resultsPanel);
            resultsArea.Children.Add(rowHost);
            resultsNote = new TextBlock { FontSize = 19, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            resultsArea.Children.Add(resultsNote);
            layout.Children.Add(resultsArea);

            // the keyboard (as the profile name's)
            var board = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            const double key = 70, gap = 10;
            var allRows = Rows.Select(r => r.Select(c => c.ToString()).ToList()).ToList();
            allRows.Add(new List<string> { "space", "erase", "done" });
            double widest = Rows.Max(r => r.Length) * (key + gap);
            foreach (var r in allRows)
            {
                var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, gap) };
                var rowKeys = new List<Tile>();
                var rowCenters = new List<double>();
                double rowWidth = r.Sum(v => KeyWidth(v, key) + gap);
                double x = (widest - rowWidth) / 2;
                foreach (var v in r)
                {
                    double w = KeyWidth(v, key);
                    var t = new Tile(w, key - 6, 16, 4) { FocusScale = 1.1 };
                    var label = new TextBlock { Text = KeyLabel(v), FontSize = v.Length == 1 ? 24 : 19, FontFamily = v == "erase" ? Ui.Glyphs : Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    t.Face.Child = v == "done" || v == "erase" || v == "space" ? (UIElement)WithBadge(label, v == "done" ? "≡" : v == "space" ? "Y" : "X") : label;   // Y types a space, Start (≡) is Done
                    t.Root.Margin = new Thickness(gap / 2, 0, gap / 2, 0);
                    rowPanel.Children.Add(t.Root);
                    rowKeys.Add(t);
                    rowCenters.Add(x + w / 2);
                    x += w + gap;
                }
                board.Children.Add(rowPanel);
                Ui.Rise(rowPanel, 80 + (board.Children.Count - 1) * 45, 14, 260);   // the rows come up one after another (#176: it popped in all at once)
                keys.Add(rowKeys);
                values.Add(r);
                centers.Add(rowCenters);
            }
            layout.Children.Add(board);
            Root.Children.Add(layout);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Close"), Ui.Prompt("X", "Erase")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("Y", "Space"), Ui.Prompt("≡", "Open first"), Ui.Prompt("A", "Select")));
            later.Tick += (s, e) => { later.Stop(); Search(); };
            Search();
        }

        private static Grid WithBadge(UIElement label, string button)
        {
            var g = new Grid();
            g.Children.Add(label);
            var badge = new Grid { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 6, 0) };
            badge.Children.Add(new Ellipse { Fill = Ink.Brush("SeleneInkColor", 0.9) });
            badge.Children.Add(new TextBlock { Text = button, FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) });
            g.Children.Add(badge);
            return g;
        }

        private static double KeyWidth(string v, double key) { return v == "space" ? key * 4.4 : v.Length > 1 ? key * 2.1 : key; }

        private static string KeyLabel(string v)
        {
            switch (v)
            {
                case "erase": return "";
                case "space": return "Space";
                case "done": return "Open";
            }
            return v;
        }

        public override bool WantsText { get { return true; } }

        public override void Shown() { ShowFocus(); }

        private void ShowFocus()
        {
            for (int r = 0; r < keys.Count; r++)
                for (int c = 0; c < keys[r].Count; c++) keys[r][c].Focus(result < 0 && r == row && c == col);
            for (int i = 0; i < resultTiles.Count; i++) resultTiles[i].Focus(i == result);
            PlaceResults();
        }

        // the row centred while it fits; wider, it glides (ease out) so the focused match stays whole on screen, the cut-off
        // edges fading to show there's more
        private void PlaceResults()
        {
            double view = resultsArea.Width, total = 0, at = 0, width = 0;
            for (int i = 0; i < resultTiles.Count; i++)
            {
                var el = resultTiles[i].Root;
                el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double w = el.DesiredSize.Width;
                if (i == result) { at = total; width = w; }
                total += w;
            }
            double x;
            bool fits = total <= view;
            if (fits) x = (view - total) / 2;
            else
            {
                x = rowFits ? 0 : resultsX;
                if (result >= 0)
                {
                    const double room = 70;   // the next match peeks in beside the focused one
                    if (at + x < room) x = room - at;
                    if (at + width + x > view - room) x = view - room - at - width;
                }
                x = Math.Min(0, Math.Max(view - total, x));
            }
            if (fits || rowFits) { resultsShift.BeginAnimation(TranslateTransform.XProperty, null); resultsShift.X = x; }   // (a search re-centring the row: no glide)
            else if (Math.Abs(x - resultsX) > 0.5) resultsShift.BeginAnimation(TranslateTransform.XProperty, Ui.Fast(new DoubleAnimation(x, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ui.Out() }));
            resultsX = x;
            rowFits = fits;
            bool left = x < -1, right = x + total > view + 1;
            if (!left && !right) { resultsArea.OpacityMask = null; return; }
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            double edge = Math.Min(0.2, 70 / view);
            mask.GradientStops.Add(new GradientStop(left ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
            mask.GradientStops.Add(new GradientStop(right ? Colors.Transparent : Colors.Black, 1));
            mask.Freeze();
            resultsArea.OpacityMask = mask;
        }

        // ---- typing ----
        public override void Type(string text)
        {
            foreach (var ch in text)
            {
                if (query.Length >= MaxLength) { Ui.Shake(field); break; }
                if (ch == ' ' && (query.Length == 0 || query.EndsWith(" "))) continue;
                query += ch;
                var t = new TextBlock { Text = ch.ToString(), FontSize = 28, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center };
                letters.Children.Add(t);
                Ui.Rise(t, 0, 6, 160);
            }
            Ui.Key();
            Changed();
        }

        public override void Erase()
        {
            if (query.Length == 0) { Ui.Shake(field); return; }
            query = query.Substring(0, query.Length - 1);
            letters.Children.RemoveAt(letters.Children.Count - 1);
            Ui.Key();
            Changed();
        }

        private void Changed()
        {
            placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            later.Stop();
            later.Start();
        }

        // matches that still match stay where they are (no blink on every key); only new ones rise in
        private void Search()
        {
            var q = query.Trim();
            var found = q.Length > 0 ? finder.Find(q).Take(finder.Max).ToList() : new List<Hit>();
            string focusedKey = result >= 0 && result < resultKeys.Count ? resultKeys[result] : null;
            var kept = new Dictionary<string, Tile>();
            for (int i = 0; i < resultTiles.Count; i++)
                if (resultKeys[i] != null && !kept.ContainsKey(resultKeys[i])) kept[resultKeys[i]] = resultTiles[i];
            var tiles = new List<Tile>();
            var keysNow = new List<string>();
            int fresh = 0;
            foreach (var hit in found)
            {
                Tile t;
                if (hit.Key != null && kept.TryGetValue(hit.Key, out t)) kept.Remove(hit.Key);
                else
                {
                    t = hit.Build();
                    t.Root.Margin = new Thickness(8, 0, 8, 0);
                    Ui.Rise(t.Root, Math.Min(fresh, 6) * 30, 10, 220);   // (a short stagger: the last starts within 180 ms)
                    fresh++;
                }
                tiles.Add(t);
                keysNow.Add(hit.Key);
            }
            resultsPanel.Children.Clear();
            foreach (var t in tiles) resultsPanel.Children.Add(t.Root);
            results.Clear(); results.AddRange(found);
            resultTiles.Clear(); resultTiles.AddRange(tiles);
            resultKeys.Clear(); resultKeys.AddRange(keysNow);
            if (result >= 0)
            {
                int same = focusedKey != null ? resultKeys.IndexOf(focusedKey) : -1;
                result = same >= 0 ? same : resultTiles.Count == 0 ? -1 : Math.Min(result, resultTiles.Count - 1);
            }
            else resultsX = 0;   // (typing from the keyboard: the row starts from its first match)
            resultsNote.Text = q.Length == 0 ? finder.EmptyNote : results.Count == 0 ? finder.NoneNote : "";
            ShowFocus();
        }

        // ---- the matching games: the name starting with it first, then installed ones, then the rest ----
        private static Finder Games()
        {
            return new Finder
            {
                Title = "Find a game", Placeholder = "Type a game's name", EmptyNote = "Your games show here as you type.", NoneNote = "No game called that.",
                Find = q =>
                {
                    if (HomeNav.Api == null) return new List<Hit>();
                    var key = StoreData.Key(q);
                    return HomeNav.Api.Database.Games
                        .Where(g => !g.Hidden && !string.IsNullOrEmpty(g.Name) && (g.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (key.Length > 0 && StoreData.Key(g.Name).Contains(key))))
                        .OrderBy(g => g.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(g => g.IsInstalled ? 0 : 1)
                        .ThenBy(g => g.Name)
                        .Take(MaxResults)
                        .Select(g => new Hit { Build = () => GameTile(g), Open = () => OpenGame(g), Key = g.Id.ToString() })
                        .ToList();
                }
            };
        }

        private static Tile GameTile(Playnite.SDK.Models.Game g)
        {
            var t = new Tile(140, 186, 12, 4) { FocusScale = 1.06 };
            // the name until the cover is decoded (off the UI thread, then kept for the next search)
            t.Face.Child = new TextBlock { Text = g.Name, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Center };
            string path = CoverPath(g);
            if (path == null) { t.Face.Child = GameFacts.StandIn(g.Name, 12, 15); return t; }   // (no cover yet: Selene's, with the name)
            ImageSource cached;
            if (covers.TryGetValue(path, out cached)) { t.Face.Background = new ImageBrush(cached) { Stretch = Stretch.UniformToFill }; t.Face.Child = null; return t; }
            var face = t.Face;
            var ui = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                var pic = Decode(path);
                if (pic == null) return;
                ui.BeginInvoke(new Action(() =>
                {
                    Remember(path, pic);
                    var brush = new ImageBrush(pic) { Stretch = Stretch.UniformToFill, Opacity = 0 };
                    face.Background = brush;
                    face.Child = null;
                    brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = Ui.Out() });
                }));
            });
            return t;
        }

        private static void OpenGame(Playnite.SDK.Models.Game g)
        {
            LoginScene.ForgetReturnFocus();   // the pick gets the focus, not what had it before the search
            var id = g.Id;
            LoginScene.FinishThen(() => HomeNav.ShowGame(id));
        }

        // the covers decoded so far (a few dozen small ones): typing on brings the same games back without a disk read
        private static readonly Dictionary<string, ImageSource> covers = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> coverOrder = new LinkedList<string>();
        private const int CoverCache = 64;

        private static void Remember(string path, ImageSource pic)
        {
            if (covers.ContainsKey(path)) return;
            covers[path] = pic;
            coverOrder.AddFirst(path);
            while (coverOrder.Count > CoverCache) { covers.Remove(coverOrder.Last.Value); coverOrder.RemoveLast(); }
        }

        private static string CoverPath(Playnite.SDK.Models.Game g)
        {
            try
            {
                if (string.IsNullOrEmpty(g.CoverImage)) return null;
                var path = HomeNav.Api.Database.GetFullFilePath(g.CoverImage);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch { return null; }
        }

        private static ImageSource Decode(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 280;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // ---- moving ----
        private void Move(string dir)
        {
            if (result >= 0)
            {
                if (dir == "DPadDown") { result = -1; row = 0; Ui.Tick(); ShowFocus(); return; }
                if (dir == "DPadUp") return;
                int next = result + (dir == "DPadRight" ? 1 : -1);
                if (next < 0 || next >= resultTiles.Count) return;
                result = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            int r = row, c = col;
            if (dir == "DPadLeft") c = c > 0 ? c - 1 : keys[r].Count - 1;   // past an end, round to the other (faster typing)
            else if (dir == "DPadRight") c = c < keys[r].Count - 1 ? c + 1 : 0;
            else
            {
                r += dir == "DPadUp" ? -1 : 1;
                if (r < 0)
                {
                    if (resultTiles.Count == 0) return;
                    result = 0;   // up from the top row: into the results
                    Ui.Tick();
                    ShowFocus();
                    return;
                }
                if (r >= keys.Count) return;
                double x = centers[row][col];
                c = 0;
                for (int i = 1; i < centers[r].Count; i++) if (Math.Abs(centers[r][i] - x) < Math.Abs(centers[r][c] - x)) c = i;
            }
            if (c < 0 || c >= keys[r].Count) return;
            row = r; col = c;
            Ui.Tick();
            ShowFocus();
        }

        private void Open(int i)
        {
            if (i < 0 || i >= results.Count) { Ui.Shake(field); return; }
            Ui.Confirm();
            results[i].Open();
        }

        public override void Handle(string b)
        {
            if (b == "B") { Ui.Back(); if (finder.Back != null) finder.Back(); else LoginScene.Finish(); return; }
            if (b.StartsWith("DPad")) { Move(b); return; }
            if (b == "X") { Erase(); return; }
            if (b == "Y") { Type(" "); return; }   // the space bar
            if (b == "Start") { Open(0); return; }
            if (b != "A") return;
            if (result >= 0) { Open(result); return; }
            var v = values[row][col];
            Ui.Pop(keys[row][col].Root);
            if (v == "done") Open(0);
            else if (v == "erase") Erase();
            else if (v == "space") Type(" ");
            else Type(v);
        }
    }
}
