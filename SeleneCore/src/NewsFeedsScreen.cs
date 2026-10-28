using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Selene
{
    // Managing the News feeds, made for News: the same dark page with the latest article's picture fading in behind, and the feeds
    // as cards (each with a picture from its latest article): a row of yours, the last card adds one, and a row of suggestions
    // under it. A turns a feed on or off (or follows a suggestion, or adds one), X removes, B goes back to News.
    public class NewsFeedsScreen : Screen
    {
        private const double TileW = 300, TileH = 176, Gap = 22;
        private readonly Func<Screen> back;
        private List<NewsFeed> feeds = new List<NewsFeed>();
        private readonly List<Tile>[] tiles = { new List<Tile>(), new List<Tile>() };
        private readonly StackPanel[] strips = { new StackPanel { Orientation = Orientation.Horizontal }, new StackPanel { Orientation = Orientation.Horizontal } };
        private readonly TranslateTransform[] shifts = { new TranslateTransform(), new TranslateTransform() };
        private readonly TextBlock subtitle, mineLabel, suggestedLabel;
        private int row, col0, col1;

        public NewsFeedsScreen(Func<Screen> back)
        {
            this.back = back;
            try { feeds = NewsFeeds.Load(); } catch (Exception ex) { HomeNav.Log("news feeds: " + ex.Message); }

            // the News page's own background: a dark base, the newest article's picture melting in at the top right, a shade for the words
            var dark = new Border();
            dark.SetResourceReference(Border.BackgroundProperty, "SeleneScrimBrush");
            Root.Children.Add(dark);
            var hero = new Grid { Width = 1500, Height = 844, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Opacity = 0.55 };
            var mask = SelenePaths.ThemeFile("Images", "TrailerMask.png");
            if (System.IO.File.Exists(mask)) hero.OpacityMask = new ImageBrush(new BitmapImage(new Uri(mask))) { Stretch = Stretch.Fill };
            var picture = FirstPicture();
            if (picture != null) hero.Children.Add(new Image { Source = picture, Stretch = Stretch.UniformToFill });
            Root.Children.Add(hero);
            Root.Children.Add(new Border
            {
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0.85, 0), Ink.Stop("SeleneScrimColor", 0.35, 0.45), Ink.Stop("SeleneScrimColor", 0, 0.7) }, 0)
            });

            var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(96, 110, 0, 0), MaxWidth = 900 };
            head.Children.Add(new TextBlock { Text = "NEWS", FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("GlyphColor") });
            head.Children.Add(new TextBlock { Text = "Your feeds", FontSize = 54, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), Margin = new Thickness(0, 6, 0, 0) });
            subtitle = new TextBlock { FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.8), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            head.Children.Add(subtitle);
            Root.Children.Add(head);

            mineLabel = Label(380);
            suggestedLabel = Label(660);
            Root.Children.Add(mineLabel); Root.Children.Add(suggestedLabel);
            AddStrip(0, 414); AddStrip(1, 694);

            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back"), Ui.Prompt("X", "Remove"), Ui.Prompt("Y", "Add a feed")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Turn on or off")));
        }

        private static TextBlock Label(double top)
        {
            return new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(100, top, 0, 0), IsHitTestVisible = false };
        }

        private void AddStrip(int i, double top)
        {
            var area = new Canvas { Width = 1900, Height = TileH + 40, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(96, top, 0, 0) };
            strips[i].RenderTransform = shifts[i];
            Canvas.SetTop(strips[i], 12);
            area.Children.Add(strips[i]);
            Root.Children.Add(area);
        }

        // the newest article's picture (already on disk from the card), for the background
        private static ImageSource FirstPicture()
        {
            try
            {
                foreach (var a in NewsCard.Latest) { var file = NewsCard.Cached(a.Image); if (file != null) return Wallpapers.Load(file, 960); }
            }
            catch { }
            return null;
        }

        public override void Shown() { Build(); ShowFocus(); }

        // ---- what each feed is called, and a picture for its card ----
        private static string NameOf(string url)
        {
            if (NewsFeeds.IsSteamLibrary(url)) return "Steam: your games";
            var known = NewsCard.Starters.FirstOrDefault(s => string.Equals(s[1], url, StringComparison.OrdinalIgnoreCase));
            if (known != null) return known[0];
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? (uri.Host.StartsWith("www.") ? uri.Host.Substring(4) : uri.Host) : url;
        }

        private static string AboutOf(string url)
        {
            if (NewsFeeds.IsSteamLibrary(url)) return "Patch notes and news of the games you play";
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host.StartsWith("www.") ? uri.Host.Substring(4) : uri.Host : url;
        }

        private static string PictureFor(string url)
        {
            try
            {
                var all = NewsCard.Latest;
                NewsArticle hit;
                if (NewsFeeds.IsSteamLibrary(url)) hit = all.FirstOrDefault(a => a.Game && !string.IsNullOrEmpty(NewsCard.Cached(a.Image)));
                else
                {
                    Uri uri;
                    var host = Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : "";
                    hit = all.FirstOrDefault(a => !a.Game && string.Equals(a.Source, host, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(NewsCard.Cached(a.Image)));
                }
                return hit == null ? null : NewsCard.Cached(hit.Image);
            }
            catch { return null; }
        }

        private bool Following(string url) { return feeds.Any(f => f.Enabled && string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)); }

        // ---- the cards ----
        private Tile Card(string name, string about, string url, string state, bool dim, bool add)
        {
            var tile = new Tile(TileW, TileH, 14, 5) { FocusScale = 1.05 };
            tile.Root.Margin = new Thickness(0, 0, Gap, 0);
            var frame = new Grid();
            string file = add ? null : PictureFor(url);
            if (file != null)
            {
                var src = Wallpapers.Load(file, 640);
                if (src != null) frame.Children.Add(new Border { CornerRadius = new CornerRadius(13), Background = new ImageBrush(src) { Stretch = Stretch.UniformToFill }, Opacity = dim ? 0.35 : 0.7 });
                frame.Children.Add(new Border { CornerRadius = new CornerRadius(13), Background = new LinearGradientBrush(Color.FromArgb(0x10, 0, 0, 0), Color.FromArgb(0xE0, 0, 0, 0), 90) });
            }
            else frame.Children.Add(new TextBlock { Text = add ? "+" : "", FontFamily = add ? Ui.Display : Ui.Glyphs, FontSize = add ? 64 : 44, Foreground = Ink.Brush("SeleneInkColor", add ? 0.8 : 0.4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, add ? 30 : 36) });
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 14) };
            words.Children.Add(new TextBlock { Text = name, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = dim ? 0.7 : 1 });
            if (!string.IsNullOrEmpty(about)) words.Children.Add(new TextBlock { Text = about, FontSize = 13, FontFamily = Ui.Body, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            if (state != null)
            {
                bool on = state == "Following" || state == "On";
                words.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 2, 11, 3), CornerRadius = new CornerRadius(9),
                    Background = on ? Ink.Brush("SeleneFocusColor") : new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
                    Child = new TextBlock { Text = (on ? "✓ " : "") + state, FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = Ui.Body, Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Brushes.White }
                });
            }
            frame.Children.Add(words);
            tile.Face.Child = frame;
            return tile;
        }

        private void Build()
        {
            int on = feeds.Count(f => f.Enabled);
            subtitle.Text = (on == 0 ? "Nothing is followed yet. " : on + (on == 1 ? " feed is on. " : " feeds are on. ")) + "What you follow shows up in News and on its card.";
            mineLabel.Text = "YOURS  ·  " + feeds.Count;
            suggestedLabel.Text = "SUGGESTED";
            foreach (var s in strips) s.Children.Clear();
            tiles[0].Clear(); tiles[1].Clear();
            for (int i = 0; i < feeds.Count; i++)
            {
                var f = feeds[i]; int at = i;
                var tile = Card(NameOf(f.Url), AboutOf(f.Url), f.Url, f.Enabled ? "On" : "Off", !f.Enabled, false);
                tile.Root.MouseLeftButtonUp += (s, e) => { row = 0; col0 = at; ShowFocus(); Activate(); e.Handled = true; };
                strips[0].Children.Add(tile.Root); tiles[0].Add(tile);
                Ui.Rise(tile.Root, 40 + i * 45, 12, 240);
            }
            var addTile = Card("Add a feed", "Paste or type an address", null, null, false, true);
            addTile.Root.MouseLeftButtonUp += (s, e) => { Ui.Confirm(); OpenAdd(); e.Handled = true; };
            strips[0].Children.Add(addTile.Root); tiles[0].Add(addTile);
            Ui.Rise(addTile.Root, 40 + feeds.Count * 45, 12, 240);
            for (int i = 0; i < NewsCard.Starters.Length; i++)
            {
                var s2 = NewsCard.Starters[i]; int at = i;
                var tile = Card(s2[0], AboutOf(s2[1]), s2[1], Following(s2[1]) ? "Following" : "Follow", false, false);
                tile.Root.MouseLeftButtonUp += (s, e) => { row = 1; col1 = at; ShowFocus(); Activate(); e.Handled = true; };
                strips[1].Children.Add(tile.Root); tiles[1].Add(tile);
                Ui.Rise(tile.Root, 200 + i * 45, 12, 240);
            }
            col0 = Math.Max(0, Math.Min(col0, tiles[0].Count - 1));
            col1 = Math.Max(0, Math.Min(col1, tiles[1].Count - 1));
        }

        private int Col { get { return row == 0 ? col0 : col1; } set { if (row == 0) col0 = value; else col1 = value; } }

        private void ShowFocus()
        {
            for (int r = 0; r < 2; r++)
            {
                int c = r == 0 ? col0 : col1;
                for (int i = 0; i < tiles[r].Count; i++) tiles[r][i].Focus(r == row && i == c);
                // the picked card inside the row's width: the row slides when it would leave
                double want = -Math.Max(0, c - 4) * (TileW + Gap);
                shifts[r].BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(want, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        private bool Save()
        {
            try { NewsFeeds.Save(feeds); return true; }
            catch (Exception ex) { HomeNav.Log("news feeds: save: " + ex.Message); Ui.Notice("Couldn't save your feeds."); return false; }
        }

        private void OpenAdd() { LoginScene.Show(new AddNewsFeedScreen(() => new NewsFeedsScreen(back)), 1); }

        private void Activate()
        {
            if (row == 0)
            {
                if (col0 >= feeds.Count) { Ui.Confirm(); OpenAdd(); return; }
                Ui.Confirm();
                feeds[col0].Enabled = !feeds[col0].Enabled;
                if (Save()) { Build(); ShowFocus(); } else feeds[col0].Enabled = !feeds[col0].Enabled;
            }
            else
            {
                var url = NewsCard.Starters[col1][1];
                var have = feeds.FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
                Ui.Confirm();
                if (have == null) feeds.Add(new NewsFeed { Url = url, Enabled = true }); else have.Enabled = !have.Enabled;
                if (Save()) { Build(); ShowFocus(); if (Following(url)) Ui.Chime(); }
            }
        }

        private void Remove()
        {
            if (row != 0 || col0 >= feeds.Count) return;
            var gone = feeds[col0];
            feeds.RemoveAt(col0);
            if (!Save()) { feeds.Insert(col0, gone); return; }
            Ui.Back();
            Build(); ShowFocus();
        }

        public override void Handle(string button)
        {
            if (button == "B") { Ui.Back(); LoginScene.Show(back != null ? back() : new NewsPage(null), -1); return; }
            if (button == "Y") { Ui.Confirm(); OpenAdd(); return; }
            if (button == "X") { Remove(); return; }
            if (button == "A") { Activate(); return; }
            if (button == "DPadLeft" || button == "DPadRight")
            {
                int next = Math.Max(0, Math.Min(tiles[row].Count - 1, Col + (button == "DPadRight" ? 1 : -1)));
                if (next == Col) return;
                Col = next; Ui.Tick(); ShowFocus();
            }
            else if (button == "DPadUp" || button == "DPadDown")
            {
                int to = row + (button == "DPadDown" ? 1 : -1);
                if (to < 0 || to > 1) return;
                row = to; Ui.Tick(); ShowFocus();
            }
        }
    }
}
