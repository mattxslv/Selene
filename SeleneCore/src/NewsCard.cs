using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace Selene
{
    // The News card on Spotlight: the latest headlines from the feeds you follow as small cards (Down goes into them, A opens
    // the article in the browser, A on the card itself opens the whole list). With no feed followed yet it offers a few to
    // follow with one press. Feeds are managed in Selene settings > Spotlight > News feeds.
    public static class NewsCard
    {
        private static DateTime fetchedAt = DateTime.MinValue;
        // the headlines last read (the full News screen opens from them at once)
        private static volatile List<NewsArticle> latest = new List<NewsArticle>();
        private static DateTime latestAt = DateTime.MinValue;
        public static List<NewsArticle> Latest { get { return latest.ToList(); } }
        public static TimeSpan LatestAge { get { return DateTime.Now - latestAt; } }
        public static void SetLatest(List<NewsArticle> all) { latest = all; latestAt = DateTime.Now; }
        private static bool busy;

        public static readonly string[][] Starters =
        {
            new[] { "Steam: your games", NewsFeeds.SteamLibrary },
            new[] { "PC Gamer", "https://www.pcgamer.com/rss/" },
            new[] { "Rock Paper Shotgun", "https://www.rockpapershotgun.com/feed" },
            new[] { "PCGamesN", "https://www.pcgamesn.com/mainrss.xml" },
        };

        // force: the feeds changed (added, removed, switched); otherwise the headlines are kept for 15 minutes
        public static void Refresh(bool force)
        {
            var app = Application.Current;
            if (app == null || !Features.On("cardnews")) return;
            List<NewsFeed> feeds;
            try { feeds = NewsFeeds.Load(); } catch (Exception ex) { HomeNav.Log("news card: " + ex.Message); feeds = new List<NewsFeed>(); }
            if (!feeds.Any(f => f.Enabled)) { app.Dispatcher.BeginInvoke(new Action(ShowStarters)); return; }
            if (busy || (!force && (DateTime.Now - fetchedAt).TotalMinutes < 15)) return;
            busy = true;
            var expandedFeeds = Expand(feeds);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                List<NewsArticle> articles = null;
                var pictures = new Dictionary<string, string>();
                int followed = feeds.Count(f => f.Enabled);
                var expanded = expandedFeeds;
                try
                {
                    var all = FetchAll(expanded);
                    SetLatest(all.Take(40).ToList());
                    articles = Pick(all);
                    foreach (var a in articles) { var file = Picture(a.Image); if (file != null) pictures[a.Url] = file; }   // (off the UI thread)
                }
                catch (Exception ex) { HomeNav.Log("news card: " + ex.Message); }
                app.Dispatcher.BeginInvoke(new Action(() => { busy = false; fetchedAt = DateTime.Now; ShowArticles(articles, pictures, followed); }));
            });
        }

        private static void ShowArticles(List<NewsArticle> articles, Dictionary<string, string> pictures, int followed)
        {
            var entries = new List<CardShowcase.Entry>();
            if (articles != null)
                foreach (var a in articles)
                {
                    var article = a;
                    entries.Add(new CardShowcase.Entry
                    {
                        Image = pictures != null && pictures.ContainsKey(article.Url) ? pictures[article.Url] : null,
                        Title = Short(article.Title, 52), Sub = article.Game ? "" : Name(article.Source), Glyph = "\uE774",
                        Open = () => { if (NewsFeeds.WebUrl(article.Url)) try { Process.Start(new ProcessStartInfo(article.Url) { UseShellExecute = true }); } catch (Exception ex) { HomeNav.Log("news: " + ex.Message); } }
                    });
                }
            int sources = articles == null ? 0 : articles.Select(a => a.Source).Distinct().Count();
            string about = entries.Count == 0 ? "No headlines came back from your feeds. A opens the list to check them."
                         : "Latest from " + (sources == 1 ? "1 source" : sources + " sources") + ". Down for the headlines.";
            Stats();
            CardShowcase.Set("nav:news", "News", entries.Count == 0 ? "No news right now" : "What's new", about, entries);
        }

        // the numbers under the headline: how much is new, over the headlines last read
        private static void Stats()
        {
            var res = Application.Current.Resources;
            var all = Latest;
            var now = DateTime.UtcNow;
            int today = all.Count(a => a.Published != DateTime.MinValue && (now - a.Published).TotalHours < 24);
            int week = all.Count(a => a.Published != DateTime.MinValue && (now - a.Published).TotalDays < 7);
            int sources = all.Select(a => a.Source).Distinct().Count();
            var newest = all.Where(a => a.Published != DateTime.MinValue).OrderByDescending(a => a.Published).FirstOrDefault();
            res["SeleneNewsStatToday"] = today == 0 ? "Nothing yet" : today == 1 ? "1 article" : today + " articles";
            res["SeleneNewsStatWeek"] = week == 0 ? "Nothing yet" : week == 1 ? "1 article" : week + " articles";
            res["SeleneNewsStatSources"] = sources == 0 ? "None" : sources.ToString();
            res["SeleneNewsStatLatest"] = newest == null ? "\u2014" : Age(now - newest.Published);
        }

        private static string Age(TimeSpan span)
        {
            if (span.TotalMinutes < 60) return "Just now";
            if (span.TotalHours < 24) return (int)span.TotalHours + " h ago";
            return (int)span.TotalDays + (span.TotalDays < 2 ? " day ago" : " days ago");
        }

        private static void ShowStarters()
        {
            var res = Application.Current.Resources;
            res["SeleneNewsStatToday"] = "\u2014"; res["SeleneNewsStatWeek"] = "\u2014"; res["SeleneNewsStatSources"] = "\u2014"; res["SeleneNewsStatLatest"] = "\u2014";
            var entries = new List<CardShowcase.Entry>();
            foreach (var s in Starters)
            {
                var name = s[0]; var url = s[1];
                entries.Add(new CardShowcase.Entry
                {
                    Title = name, Sub = "Follow", Glyph = "",
                    Open = () =>
                    {
                        try
                        {
                            var feeds = NewsFeeds.Load();
                            if (!feeds.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase))) feeds.Add(new NewsFeed { Url = url, Enabled = true });
                            else foreach (var f in feeds) if (string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)) f.Enabled = true;
                            NewsFeeds.Save(feeds);   // (refreshes the card)
                            Ui.Chime();
                            Ui.Notice("Now following " + name + ".");
                        }
                        catch (Exception ex) { HomeNav.Log("news card: " + ex.Message); }
                    }
                });
            }
            CardShowcase.Set("nav:news", "News", "News from the games you love", "Pick a feed to follow. Add your own in Selene settings: Spotlight, News feeds. Down for a few to start with.", entries);
        }

        // four headlines, newest first; with several sources no one of them fills more than half
        private static List<NewsArticle> Pick(List<NewsArticle> all)
        {
            int sources = all.Select(a => a.Source).Distinct().Count();
            var perSource = new Dictionary<string, int>();
            var chosen = new List<NewsArticle>();
            foreach (var a in all)
            {
                int n; perSource.TryGetValue(a.Source, out n);
                if (sources > 1 && n >= 2) continue;
                perSource[a.Source] = n + 1;
                chosen.Add(a);
                if (chosen.Count == 4) break;   // four across the card, as the Store card has: no more fit, and the strip doesn't scroll
            }
            return chosen;
        }

        // Steam: your games becomes each Steam game's own news (the ones played last), with the game's art for an article with no picture
        public static List<NewsFeed> Expand(List<NewsFeed> feeds)
        {
            var result = new List<NewsFeed>();
            foreach (var f in feeds.Where(x => x.Enabled))
            {
                if (!NewsFeeds.IsSteamLibrary(f.Url)) { result.Add(f); continue; }
                try
                {
                    var steam = new Guid("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
                    var games = HomeNav.Api.Database.Games.Where(g => g.PluginId == steam && !g.Hidden).OrderByDescending(g => g.LastActivity ?? DateTime.MinValue).ThenByDescending(g => g.Playtime).Take(10).ToList();
                    foreach (var g in games)
                    {
                        int id;
                        if (!int.TryParse(g.GameId, out id)) continue;
                        result.Add(new NewsFeed { Url = "https://store.steampowered.com/feeds/news/app/" + id + "/", Enabled = true, Label = g.Name, Cover = GameFacts.BackgroundPath(g) ?? GameFacts.CoverPath(g) });
                    }
                }
                catch (Exception ex) { HomeNav.Log("news card: " + ex.Message); }
            }
            return result;
        }

        // every feed read at the same time (a few at once), then one list: newest first, no article twice
        public static List<NewsArticle> FetchAll(List<NewsFeed> feeds)
        {
            var all = new List<NewsArticle>();
            System.Threading.Tasks.Parallel.ForEach(feeds, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 5 }, f =>
            {
                string errors;
                var part = NewsFeeds.Fetch(new[] { f }, out errors);
                lock (all) all.AddRange(part);
            });
            return all.GroupBy(a => a.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderByDescending(a => a.Published).ToList();
        }

        public static string Name(string host)
        {
            if (host.IndexOf("steampowered", StringComparison.OrdinalIgnoreCase) >= 0) return "Steam";
            return host.StartsWith("www.") ? host.Substring(4) : host;
        }

        // an article's picture kept on disk (one file per address), so the card doesn't fetch it again; null when it can't be had
        // the file for a picture already kept (or a game's own art), else null
        public static string Cached(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (System.IO.Path.IsPathRooted(url)) return System.IO.File.Exists(url) ? url : null;
            var file = FileFor(url);
            return file != null && System.IO.File.Exists(file) ? file : null;
        }

        private static string FileFor(string url)
        {
            if (!NewsFeeds.WebUrl(url)) return null;
            string name;
            using (var sha = System.Security.Cryptography.SHA1.Create()) name = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant();
            return System.IO.Path.Combine(SelenePaths.DataFolder, "news-pictures", name + ".img");
        }

        public static string Picture(string url)
        {
            var kept = Cached(url);
            if (kept != null) return kept;
            if (string.IsNullOrEmpty(url) || !NewsFeeds.WebUrl(url)) return null;
            try
            {
                var folder = System.IO.Path.Combine(SelenePaths.DataFolder, "news-pictures");
                System.IO.Directory.CreateDirectory(folder);
                string name;
                using (var sha = System.Security.Cryptography.SHA1.Create()) name = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant();
                var file = System.IO.Path.Combine(folder, name + ".img");
                if (System.IO.File.Exists(file)) return file;
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                request.Timeout = 8000; request.ReadWriteTimeout = 8000; request.UserAgent = "Selene RSS reader";
                using (var response = request.GetResponse())
                using (var input = response.GetResponseStream())
                using (var memory = new System.IO.MemoryStream())
                {
                    var buffer = new byte[8192]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { memory.Write(buffer, 0, count); if (memory.Length > 4 * 1024 * 1024) return null; }
                    var b = memory.ToArray();
                    bool known = b.Length > 8 && ((b[0] == 0xFF && b[1] == 0xD8) || (b[0] == 0x89 && b[1] == 0x50) || (b[0] == 0x47 && b[1] == 0x49) || (b[0] == 0x42 && b[1] == 0x4D));   // jpeg, png, gif, bmp (not webp: Windows can't draw it)
                    if (!known) return null;
                    System.IO.File.WriteAllBytes(file, b);
                    return file;
                }
            }
            catch (Exception ex) { HomeNav.Log("news picture: " + url + ": " + ex.Message); return null; }
        }

        private static string Short(string text, int max) { return text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…"; }
    }
}
