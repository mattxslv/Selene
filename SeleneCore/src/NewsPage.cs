using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Selene
{
    // The full News screen, in the style of the Store: the highlighted article's picture big at the top right, its source, headline
    // and the start of its text on the left, and a row of picture tiles that glides as you move (Left / Right). A reads it in the
    // browser, Y manages the feeds, X refreshes. Opens at once from the headlines the card already holds.
    public class NewsPage : Screen
    {
        private const double TileW = 420, TileH = 236, Gap = 22;
        private readonly Func<Screen> back;
        private List<NewsArticle> articles = new List<NewsArticle>();   // what is shown (the filter applied)
        private List<NewsArticle> all = new List<NewsArticle>();
        private string filter = "";                                       // "" = every source
        private readonly StackPanel pillBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(96, 872, 0, 0) };
        private readonly List<Border> pills = new List<Border>();
        private List<string> sourceNames = new List<string>();
        private readonly List<Tile> tiles = new List<Tile>();
        private readonly List<Border> fills = new List<Border>();
        private readonly Dictionary<string, ImageSource> pics = new Dictionary<string, ImageSource>();
        private readonly StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TranslateTransform shift = new TranslateTransform();
        private readonly Image heroA = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
        private readonly Image heroB = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
        private readonly StackPanel info;
        private readonly TextBlock source, title, summary, status, count;
        private bool heroFront, closed, loading;
        private string heroUrl;
        private int index, version;

        public override bool MenuOnStart { get { return false; } }

        public NewsPage(Func<Screen> back)
        {
            this.back = back;
            // a dark base like the Store's: the articles' pictures are the picture here
            var dark = new Border();
            dark.SetResourceReference(Border.BackgroundProperty, "SeleneScrimBrush");
            Root.Children.Add(dark);
            var hero = new Grid { Width = 1500, Height = 844, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            var mask = SelenePaths.ThemeFile("Images", "TrailerMask.png");
            if (System.IO.File.Exists(mask)) hero.OpacityMask = new ImageBrush(new BitmapImage(new Uri(mask))) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(heroA, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(heroB, BitmapScalingMode.HighQuality);
            hero.Children.Add(heroA); hero.Children.Add(heroB);
            Root.Children.Add(hero);
            Root.Children.Add(new Border
            {
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0.85, 0), Ink.Stop("SeleneScrimColor", 0.35, 0.45), Ink.Stop("SeleneScrimColor", 0, 0.7) }, 0)
            });

            info = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(96, 130, 0, 0), MaxWidth = 860 };
            source = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("GlyphColor") };
            title = new TextBlock { FontSize = 50, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis, MaxHeight = 190, Margin = new Thickness(0, 8, 0, 0) };
            summary = new TextBlock { FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.82), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis, MaxHeight = 160, Margin = new Thickness(0, 16, 0, 0) };
            info.Children.Add(source); info.Children.Add(title); info.Children.Add(summary);
            Root.Children.Add(info);

            // the tiles: the picked one sits at the left edge, the rest peek in from the right
            var area = new Canvas { Width = 1900, Height = TileH + 40, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(96, 556, 0, 0) };
            strip.RenderTransform = shift;
            Canvas.SetTop(strip, 14);
            area.Children.Add(strip);
            Root.Children.Add(area);

            status = new TextBlock { FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.7), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(96, 524, 0, 0), IsHitTestVisible = false };
            count = new TextBlock { FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 530, 96, 0), IsHitTestVisible = false };
            Root.Children.Add(status); Root.Children.Add(count);
            Root.Children.Add(pillBar);

            var left = Ui.Prompts(false, Ui.Prompt("B", "Back"), Ui.Prompt("Y", "Manage feeds"), Ui.Prompt("X", "Refresh"));
            var right = Ui.Prompts(true, Ui.Prompt("A", "Read"));
            left.Margin = new Thickness(46, 0, 0, 30); right.Margin = new Thickness(0, 0, 64, 30);
            Root.Children.Add(left); Root.Children.Add(right);
        }

        public override void Shown()
        {
            closed = false;
            var cached = NewsCard.Latest;
            if (cached.Count > 0) { all = cached; Filtered(); BuildPills(); Build(); }
            if (cached.Count == 0 || NewsCard.LatestAge.TotalMinutes > 15) Refresh();
        }

        public override void Hidden() { closed = true; version++; }

        private void Refresh()
        {
            if (loading) return;
            List<NewsFeed> feeds;
            try { feeds = NewsFeeds.Load(); } catch (Exception ex) { HomeNav.Log("news: " + ex.Message); feeds = new List<NewsFeed>(); }
            if (!feeds.Any(f => f.Enabled)) { status.Text = "Nothing is followed yet. Y adds feeds."; return; }
            loading = true;
            status.Text = articles.Count == 0 ? "Loading the news…" : "Refreshing…";
            var expanded = NewsCard.Expand(feeds);   // (the library's games, read here)
            var dispatcher = Root.Dispatcher;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                List<NewsArticle> fetched = null;
                try { fetched = NewsCard.FetchAll(expanded).Take(40).ToList(); } catch (Exception ex) { HomeNav.Log("news: " + ex.Message); }
                dispatcher.BeginInvoke(new Action(() =>
                {
                    loading = false;
                    if (closed) return;
                    if (fetched != null && fetched.Count > 0) { NewsCard.SetLatest(fetched); all = fetched; filter = ""; Filtered(); BuildPills(); index = 0; status.Text = ""; Build(); }
                    else status.Text = articles.Count == 0 ? "No articles came back. Y checks your feeds." : "";
                }));
            });
        }

        private void Build()
        {
            version++;
            strip.Children.Clear(); tiles.Clear(); fills.Clear(); pics.Clear(); heroUrl = null;
            shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = 0;
            for (int i = 0; i < articles.Count; i++)
            {
                var a = articles[i];
                int at = i;
                var tile = new Tile(TileW, TileH, 14, 5) { FocusScale = 1.04 };
                tile.Root.Margin = new Thickness(0, 0, Gap, 0);
                tile.Root.MouseLeftButtonUp += (s, e) => { if (index == at) { Ui.Confirm(); Open(); } else { index = at; Ui.Tick(); Moved(); } e.Handled = true; };
                var frame = new Grid();
                frame.Children.Add(new TextBlock { Text = "", FontFamily = Ui.Glyphs, FontSize = 44, Foreground = Ink.Brush("SeleneInkColor", 0.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                var fill = new Border { CornerRadius = new CornerRadius(13), Opacity = 0 };
                frame.Children.Add(fill);
                frame.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10), Padding = new Thickness(10, 4, 12, 5), CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(Color.FromArgb(0xB4, 0, 0, 0)),
                    Child = new TextBlock { Text = a.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = TileW - 64 }
                });
                tile.Face.Child = frame;
                strip.Children.Add(tile.Root);
                tiles.Add(tile); fills.Add(fill);
                if (i < 7) Ui.Rise(tile.Root, 60 + i * 45, 12, 260);
            }
            index = Math.Max(0, Math.Min(index, tiles.Count - 1));
            Moved();
            LoadPictures();
        }

        // the sources as tabs under the row (LB / RB): All, then the ones with the most articles
        private void Filtered()
        {
            articles = filter.Length == 0 ? all.ToList() : all.Where(a => NewsCard.Name(a.Source) == filter).ToList();
        }

        private void BuildPills()
        {
            sourceNames = all.GroupBy(a => NewsCard.Name(a.Source)).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(5).ToList();
            pillBar.Children.Clear(); pills.Clear();
            if (all.Count == 0) return;
            pillBar.Children.Add(HomeNav.FilterBadge("LB"));
            var names = new List<string> { "All" }; names.AddRange(sourceNames);
            foreach (var n in names)
            {
                var pill = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 5, 20, 7), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = n, FontSize = 19, FontFamily = Ui.Body, VerticalAlignment = VerticalAlignment.Center } };
                pills.Add(pill); pillBar.Children.Add(pill);
            }
            pillBar.Children.Add(HomeNav.FilterBadge("RB"));
            ShowPills();
        }

        private void ShowPills()
        {
            for (int i = 0; i < pills.Count; i++)
            {
                bool on = (i == 0 && filter.Length == 0) || (i > 0 && sourceNames[i - 1] == filter);
                pills[i].Background = on ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
                ((TextBlock)pills[i].Child).Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.7);
            }
        }

        private void CycleFilter(int dir)
        {
            if (pills.Count == 0) return;
            int now = filter.Length == 0 ? 0 : sourceNames.IndexOf(filter) + 1;
            int next = (now + dir + pills.Count) % pills.Count;
            filter = next == 0 ? "" : sourceNames[next - 1];
            Ui.Tick();
            Ui.Pop(pills[next]);
            ShowPills();
            Filtered();
            index = 0;
            Build();
        }

        // pictures one after another off the UI thread (downloaded first when they aren't on disk), each shown as it arrives
        private void LoadPictures()
        {
            int ver = version;
            var list = articles.ToList();
            var dispatcher = Root.Dispatcher;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (closed || ver != version) return;
                    var a = list[i];
                    ImageSource src = null;
                    try { var file = NewsCard.Picture(a.Image); if (file != null) src = Wallpapers.Load(file, 960); } catch { }
                    if (src == null) continue;
                    int at = i;
                    dispatcher.BeginInvoke(new Action(() => { if (closed || ver != version) return; pics[a.Url] = src; ApplyPicture(at); }), System.Windows.Threading.DispatcherPriority.Background);
                }
            });
        }

        private void ApplyPicture(int i)
        {
            if (i >= fills.Count) return;
            fills[i].Background = new ImageBrush(pics[articles[i].Url]) { Stretch = Stretch.UniformToFill };
            Ui.Fade(fills[i], 1, 260);
            if (i == index) ShowHero();
        }

        // the picked article: its words, the picture behind, the row glides to it
        private void Moved()
        {
            if (tiles.Count == 0) { source.Text = ""; title.Text = ""; summary.Text = ""; count.Text = ""; return; }
            for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(i == index);
            var a = articles[index];
            source.Text = (NewsCard.Name(a.Source) ?? "").ToUpperInvariant() + (a.Published == DateTime.MinValue ? "" : "  ·  " + When(a.Published));
            title.Text = a.Title;
            summary.Text = a.Summary ?? "";
            count.Text = (index + 1) + " / " + articles.Count;
            Ui.Rise(info, 0, 8, 220);
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-index * (TileW + Gap), TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ShowHero();
        }

        private void ShowHero()
        {
            if (index >= articles.Count) return;
            var url = articles[index].Url;
            if (url == heroUrl) return;
            ImageSource src;
            if (!pics.TryGetValue(url, out src)) { heroUrl = null; Ui.Fade(heroA, 0, 240); Ui.Fade(heroB, 0, 240); return; }
            heroUrl = url;
            var to = heroFront ? heroB : heroA; var from = heroFront ? heroA : heroB;
            to.Source = src;
            Ui.Fade(to, 1, 320); Ui.Fade(from, 0, 320);
            heroFront = !heroFront;
        }

        private static string When(DateTime utc)
        {
            var age = DateTime.UtcNow - utc;
            if (age.TotalHours < 1) return "just now";
            if (age.TotalDays < 1) return (int)age.TotalHours + " h ago";
            if (age.TotalDays < 7) return (int)age.TotalDays + (age.TotalDays < 2 ? " day ago" : " days ago");
            return GameFacts.Date(utc.ToLocalTime(), "MMM d");
        }

        private void Open()
        {
            if (index >= articles.Count || !NewsFeeds.WebUrl(articles[index].Url)) return;
            try { Process.Start(new ProcessStartInfo(articles[index].Url) { UseShellExecute = true }); } catch (Exception ex) { status.Text = "Could not open the article: " + ex.Message; }
        }

        public override void Handle(string button)
        {
            if (button == "B") { Ui.Back(); if (back == null) LoginScene.Finish(); else LoginScene.Show(back(), -1); return; }
            if (button == "Y") { Ui.Confirm(); LoginScene.Show(new NewsFeedsScreen(() => new NewsPage(back)), 1); return; }
            if (button == "X") { Ui.Tick(); Refresh(); return; }
            if (button == "A") { Ui.Confirm(); Open(); return; }
            if (button == "LeftShoulder" || button == "RightShoulder") { CycleFilter(button == "RightShoulder" ? 1 : -1); return; }
            if (button != "DPadLeft" && button != "DPadRight") return;
            int next = Math.Max(0, Math.Min(tiles.Count - 1, index + (button == "DPadRight" ? 1 : -1)));
            if (next == index || tiles.Count == 0) return;
            index = next;
            Ui.Tick();
            Moved();
        }
    }
}
