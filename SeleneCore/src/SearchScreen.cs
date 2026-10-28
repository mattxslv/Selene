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
        public sealed class Hit { public Func<Tile> Build; public Action Open; }

        // what is searched: the words around the field and the matches for a query (the games by default)
        public sealed class Finder
        {
            public string Title, Placeholder, EmptyNote, NoneNote;
            public Func<string, List<Hit>> Find;
            public Action Back;   // B (null: back to where the search was opened from)
            public int Max = MaxResults;   // how many matches show (they sit in one row: as many as fit the screen)
            public Brush Shade;   // the shade over what's behind (null: the usual one)
        }

        public override bool MenuOnStart { get { return false; } }   // (Start is its own here)
        private static readonly string[] Rows = { "1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm-." };
        private const int MaxLength = 30, MaxResults = 8;
        private readonly StackPanel letters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border field;
        private readonly TextBlock placeholder;
        private readonly List<List<Tile>> keys = new List<List<Tile>>();
        private readonly List<List<string>> values = new List<List<string>>();
        private readonly List<List<double>> centers = new List<List<double>>();
        private readonly StackPanel resultsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 196 };
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
            var resultsArea = new Grid { Height = 220, Margin = new Thickness(0, 26, 0, 0) };
            resultsArea.Children.Add(resultsPanel);
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

        private void Search()
        {
            results.Clear();
            resultTiles.Clear();
            resultsPanel.Children.Clear();
            if (result >= 0) result = -1;
            var q = query.Trim();
            if (q.Length > 0) results.AddRange(finder.Find(q).Take(finder.Max));
            for (int i = 0; i < results.Count; i++)
            {
                var t = results[i].Build();
                t.Root.Margin = new Thickness(8, 0, 8, 0);
                resultsPanel.Children.Add(t.Root);
                resultTiles.Add(t);
                Ui.Rise(t.Root, i * 30, 10, 220);
            }
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
                        .Select(g => new Hit { Build = () => GameTile(g), Open = () => OpenGame(g) })
                        .ToList();
                }
            };
        }

        private static Tile GameTile(Playnite.SDK.Models.Game g)
        {
            var t = new Tile(140, 186, 12, 4) { FocusScale = 1.06 };
            var cover = Cover(g);
            if (cover != null) t.Face.Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
            else t.Face.Child = new TextBlock { Text = g.Name, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Center };
            return t;
        }

        private static void OpenGame(Playnite.SDK.Models.Game g)
        {
            LoginScene.ForgetReturnFocus();   // the pick gets the focus, not what had it before the search
            var id = g.Id;
            LoginScene.FinishThen(() => HomeNav.ShowGame(id));
        }

        private static ImageSource Cover(Playnite.SDK.Models.Game g)
        {
            try
            {
                if (string.IsNullOrEmpty(g.CoverImage)) return null;
                var path = HomeNav.Api.Database.GetFullFilePath(g.CoverImage);
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
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
