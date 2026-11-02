using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Selene
{
    public class NewsScreen : Screen
    {
        private readonly bool manage;
        private readonly Func<Screen> back;
        private readonly StackPanel list = new StackPanel();
        private readonly TextBlock status = Ui.Sub("");
        private readonly ScrollViewer scroll;
        private readonly List<Border> rows = new List<Border>();
        private List<NewsFeed> feeds = new List<NewsFeed>();
        private List<NewsArticle> articles = new List<NewsArticle>();
        private int index;
        private volatile bool closed;
        private bool loading;

        public NewsScreen(bool manage, Func<Screen> back)
        {
            this.manage = manage; this.back = back;
            var layout = new Grid { Margin = new Thickness(140, 90, 140, 90) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            var header = new StackPanel();
            header.Children.Add(Ui.Title(manage ? "News feeds" : "News"));
            header.Children.Add(status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 16) };
            AddButton(actions, "Back", GoBack);
            AddButton(actions, manage ? "Add feed" : "Manage feeds", () => { if (manage) Add(); else LoginScene.Show(new NewsScreen(true, () => new NewsScreen(false, back)), 1); });
            if (!manage) AddButton(actions, "Refresh", Refresh);
            else AddButton(actions, "Remove selected", Remove);
            header.Children.Add(actions); layout.Children.Add(header);
            scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); layout.Children.Add(scroll); Root.Children.Add(layout);
            try { feeds = NewsFeeds.Load(); } catch (Exception ex) { status.Text = "Could not load feed settings: " + ex.Message; }
        }
        internal static void AddButton(Panel panel, string label, Action action)
        {
            panel.Children.Add(Ui.MouseButton(label, action));
        }
        public override void Shown() { closed = false; if (manage) Render(); else Refresh(); }
        public override void Hidden() { closed = true; }
        private void GoBack() { if (back == null) LoginScene.Finish(); else LoginScene.Show(back(), -1); }
        private void Add() { LoginScene.Show(new AddNewsFeedScreen(() => new NewsFeedsScreen(back)), 1); }
        private void Refresh()
        {
            if (loading) return;
            loading = true; status.Text = "Refreshing feeds…";
            var snapshot = NewsCard.Expand(feeds.Select(f => new NewsFeed { Url = f.Url, Enabled = f.Enabled }).ToList());   // (Steam: your games becomes each game's feed, read here on the UI thread)
            var dispatcher = Root.Dispatcher;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string errors = ""; var fetched = NewsCard.FetchAll(snapshot);
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (closed) return;
                    loading = false; if (fetched.Count > 0 || errors.Length == 0) articles = fetched; index = 0; Render();
                    if (errors.Length > 0) status.Text = errors;
                }));
            });
        }
        private void Render()
        {
            list.Children.Clear(); rows.Clear();
            int count = manage ? feeds.Count : articles.Count;
            status.Text = manage ? "Select toggles a feed. X removes it. Y adds a feed." : feeds.All(f => !f.Enabled) ? "Add and enable feeds to see their updates here." : count == 0 ? "No articles found." : "Select an article to open it in your browser.";
            for (int i = 0; i < count; i++)
            {
                FrameworkElement content;
                if (manage)
                {
                    var text = new StackPanel();
                    text.Children.Add(Ui.Text((feeds[i].Enabled ? "✓ " : "○ ") + (NewsFeeds.IsSteamLibrary(feeds[i].Url) ? "Steam: your games" : feeds[i].Url), 22));
                    content = text;
                }
                else content = ArticleRow(articles[i]);
                var row = new Border { Child = content, Padding = manage ? new Thickness(22, 14, 22, 14) : new Thickness(14), Margin = new Thickness(0, 0, 0, manage ? 10 : 14), CornerRadius = new CornerRadius(manage ? 14 : 18), BorderThickness = new Thickness(2), Background = Ink.Brush("SeleneGlassColor", 0.8) };
                if (i < 8) Ui.Rise(row, i * 45, 14, 260);
                int selected = i; row.MouseLeftButtonUp += (s, e) => { index = selected; FocusRow(); Activate(); e.Handled = true; };
                rows.Add(row); list.Children.Add(row);
            }
            index = Math.Max(0, Math.Min(index, rows.Count - 1)); FocusRow();
            FetchPictures();
        }
        // a picture on the left, the headline, its source and date, and a line or two of the article on the right
        private FrameworkElement ArticleRow(NewsArticle a)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var pic = new Border { Width = 256, Height = 144, CornerRadius = new CornerRadius(12), Background = Ink.Brush("SeleneInkColor", 0.12), Margin = new Thickness(0, 0, 22, 0) };
            pic.Child = new TextBlock { Text = "\uE8A5", FontFamily = Ui.Glyphs, FontSize = 40, Foreground = Ink.Brush("SeleneInkColor", 0.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var file = NewsCard.Cached(a.Image);
            if (file != null) ShowPicture(pic, file);
            else if (!string.IsNullOrEmpty(a.Image)) wanted.Add(new KeyValuePair<Border, string>(pic, a.Image));
            grid.Children.Add(pic);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = a.Title, FontSize = 25, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 70 });
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            meta.Children.Add(new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 3), Background = Ink.Brush("SeleneInkColor", 0.14), Child = new TextBlock { Text = a.Source.StartsWith("www.") ? a.Source.Substring(4) : a.Source, FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.9) } });
            if (a.Published != DateTime.MinValue) meta.Children.Add(new TextBlock { Text = When(a.Published), FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(meta);
            if (!string.IsNullOrEmpty(a.Summary)) text.Children.Add(new TextBlock { Text = a.Summary, FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.75), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 48, Margin = new Thickness(0, 8, 0, 0) });
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            return grid;
        }

        private static string When(DateTime utc)
        {
            var age = DateTime.UtcNow - utc;
            if (age.TotalHours < 1) return "just now";
            if (age.TotalDays < 1) return (int)age.TotalHours + " h ago";
            if (age.TotalDays < 7) return (int)age.TotalDays + (age.TotalDays < 2 ? " day ago" : " days ago");
            return GameFacts.Date(utc.ToLocalTime(), "MMM d");
        }

        private static void ShowPicture(Border pic, string file)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit(); bmp.UriSource = new Uri(file); bmp.DecodePixelWidth = 512; bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
                pic.Child = null;
                pic.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch { }
        }

        // pictures not on disk yet: fetched one after another off the UI thread, each shown as it arrives
        private readonly List<KeyValuePair<Border, string>> wanted = new List<KeyValuePair<Border, string>>();
        private void FetchPictures()
        {
            var todo = wanted.ToList(); wanted.Clear();
            if (todo.Count == 0) return;
            var dispatcher = Root.Dispatcher;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var item in todo)
                {
                    if (closed) return;
                    var file = NewsCard.Picture(item.Value);
                    if (file == null) continue;
                    var pic = item.Key;
                    dispatcher.BeginInvoke(new Action(() => { if (!closed) { ShowPicture(pic, file); Ui.Fade(pic, 1, 220, 0); } }));
                }
            });
        }

        private void FocusRow()
        {
            for (int i = 0; i < rows.Count; i++) rows[i].BorderBrush = i == index ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
            if (index < rows.Count) rows[index].BringIntoView();
        }
        private bool Save()
        {
            try { NewsFeeds.Save(feeds); return true; }
            catch (Exception ex) { status.Text = "Could not save feeds: " + ex.Message; return false; }
        }
        private void Activate()
        {
            if (index >= rows.Count) return;
            if (manage) { feeds[index].Enabled = !feeds[index].Enabled; if (Save()) Render(); else feeds[index].Enabled = !feeds[index].Enabled; }
            else if (NewsFeeds.WebUrl(articles[index].Url))
                try { Process.Start(new ProcessStartInfo(articles[index].Url) { UseShellExecute = true }); } catch (Exception ex) { status.Text = "Could not open article: " + ex.Message; }
        }
        private void Remove()
        {
            if (!manage || index >= feeds.Count) return;
            var previous = feeds[index]; feeds.RemoveAt(index);
            if (Save()) Render(); else feeds.Insert(index, previous);
        }
        public override void Handle(string button)
        {
            if (button == "B") { Ui.Back(); GoBack(); return; }
            if (button == "Y") { if (manage) Add(); else LoginScene.Show(new NewsScreen(true, () => new NewsScreen(false, back)), 1); return; }
            if (button == "X") { if (manage) Remove(); else Refresh(); return; }
            if (button == "A") { Ui.Confirm(); Activate(); return; }
            int next = index + (button == "DPadDown" ? 1 : button == "DPadUp" ? -1 : 0);
            next = Math.Max(0, Math.Min(next, rows.Count - 1));
            if (next == index) return;
            index = next; Ui.Tick(); FocusRow();
        }
    }
    public class AddNewsFeedScreen : Screen
    {
        private readonly Func<Screen> back;
        private readonly KeyPad pad;
        private readonly TextBlock value = Ui.Text("https://", 23);
        private readonly TextBlock error = Ui.Sub("Paste or type an RSS or Atom feed URL.");
        private string url = "https://";
        public override bool WantsText { get { return true; } }
        public override bool MenuOnStart { get { return false; } }
        public AddNewsFeedScreen(Func<Screen> back)
        {
            this.back = back;
            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(140, 60, 140, 60) };
            body.Children.Add(Ui.Title("Add feed")); body.Children.Add(value); body.Children.Add(error);
            pad = new KeyPad("Save", 62, 9, new[] { "1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM", ":/.-_?=&%" }, false);
            pad.Typed = Type; pad.Erased = Erase; pad.Done = Save; pad.Root.Margin = new Thickness(0, 30, 0, 0); body.Children.Add(pad.Root);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            NewsScreen.AddButton(actions, "Paste", () => { try { var pasted = Clipboard.GetText().Trim(); if (pasted.Length > 2048) { error.Text = "Feed URLs must be 2048 characters or fewer."; return; } url = pasted; Type(""); } catch (Exception ex) { error.Text = ex.Message; } });
            NewsScreen.AddButton(actions, "Save", Save); NewsScreen.AddButton(actions, "Cancel", () => LoginScene.Show(back(), -1)); body.Children.Add(actions); Root.Children.Add(body);
        }
        public override void Shown() { pad.Focus(true); }
        public override void Type(string text) { if (url.Length + text.Length <= 2048) url += text; value.Text = url; pad.After(url); }
        public override void Erase() { if (url.Length > 0) url = url.Substring(0, url.Length - 1); Type(""); }
        private void Save()
        {
            url = url.Trim();
            if (!NewsFeeds.WebUrl(url)) { error.Text = "Enter a complete http:// or https:// feed URL."; return; }
            try
            {
                var feeds = NewsFeeds.Load();
                if (feeds.Count >= 30) { error.Text = "Remove a feed before adding another (maximum 30)."; return; }
                if (feeds.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase))) { error.Text = "This feed is already added."; return; }
                feeds.Add(new NewsFeed { Url = url, Enabled = true }); NewsFeeds.Save(feeds); LoginScene.Show(back(), -1);
            }
            catch (Exception ex) { error.Text = "Could not save feed: " + ex.Message; }
        }
        public override void Handle(string button)
        {
            if (button == "B") { LoginScene.Show(back(), -1); return; }
            if (button.StartsWith("DPad")) { pad.Move(button); return; }
            pad.Press(button);
        }
    }
}
