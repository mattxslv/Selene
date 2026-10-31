// Screenshot strip on the home: when the selected game changes, find its Steam store
// screenshots and publish them as DynamicResource SeleneShots (list of ShotItem) + SeleneShotsNone (Visibility).
// Sources, in order: Extra Metadata Loader's cached SteamAppDetails json, Steam appdetails (Steam games), Steam store
// search by name, then IGDB through Playnite's backend (games not on Steam). Images are cached in the media folder\<game id>\. Falls back to the
// game's background art. A on a tile opens a full-screen viewer (Left/Right flip, A/B close).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Selene
{
    public sealed class ShotItem
    {
        public BitmapImage Thumb { get; set; }
        public string Path { get; set; }
    }

    public static class GameMedia
    {
        private const string SteamPlugin = "cb91dfc9-b977-43bf-8e70-55f46e410fab";
        private const string EmlData = "705fdbca-e1fc-4004-b839-1d040b8b4429";
        private static string CacheRoot { get { return SelenePaths.Media("Screenshots-v2"); } }
        private const int MaxShots = 6;

        private static readonly Dictionary<Guid, List<ShotItem>> cache = new Dictionary<Guid, List<ShotItem>>();
        internal static int CachedGameCount { get { return cache.Count; } }
        internal static int CachedImageCount { get { return cache.Values.Sum(items => items.Count); } }
        internal static int PendingGameCount { get { return loading.Count; } }
        private static System.Windows.Threading.DispatcherTimer debounce;
        private static object pending;
        private static System.Collections.IList pendingNeighbours;
        private static Guid shown;
        private static int downloadGeneration;

        public static void OnSelected(object game, System.Collections.IList neighbours)
        {
            TrailerFetch.Want(game, neighbours);
            pending = game;
            pendingNeighbours = neighbours;
            if (debounce == null)
            {
                Application.Current.Resources["SeleneShotsNone"] = Visibility.Collapsed;  // no "none" text before the first load
                for (int i = 0; i < Tiles; i++) Application.Current.Resources["SeleneShot" + i + "Vis"] = Visibility.Collapsed;
                // only once the selection has settled: stepping card by card with short pauses used to load (and animate) the
                // strip after nearly every card, and that work landed right when the next move started
                debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                debounce.Tick += (s, e) => { debounce.Stop(); if (HomeNav.SigningIn) return; Load(pending); Preload(pendingNeighbours); };
            }
            var g = AsGame(game);
            if (g != null) PerformanceRecorder.Mark("selection", g.Id + " " + g.Name);
            if (g == null || g.Id != shown) FadeOut();
            if (g != null && !cache.ContainsKey(g.Id)) ShowLoading();
            else HideLoading(false);
            debounce.Stop();
            debounce.Start();
        }

        // the details page: its game's pictures at once (the wait is for Spotlight's row to settle; a page is open already)
        public static void ShowNow(object game)
        {
            OnSelected(game, null);
            if (debounce != null) debounce.Stop();
            if (!HomeNav.SigningIn) { Load(game); Preload(null); }
        }

        // the game whose pictures Current holds (Guid.Empty: none)
        public static Guid ShownId { get { return shown; } }

        private const int Tiles = 6;
        private static List<ShotItem> current = new List<ShotItem>();

        private static FrameworkElement Strip()
        {
            var strip = stripRef != null ? stripRef.Target as FrameworkElement : null;
            if (strip == null)
            {
                strip = HomeNav.FindByName(Application.Current.MainWindow, "SeleneShotStrip");
                if (strip != null) stripRef = new WeakReference(strip);
            }
            return strip;
        }

        private static void FadeOut()
        {
            var strip = Strip();
            if (strip == null || strip.Opacity < 0.01) return;
            strip.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        }

        // Row items are Playnite's GamesCollectionViewEntry (it wraps the Game in .Game), not Game itself
        private static Playnite.SDK.Models.Game AsGame(object o)
        {
            var g = o as Playnite.SDK.Models.Game;
            if (g != null || o == null) return g;
            var prop = o.GetType().GetProperty("Game");
            return prop != null ? prop.GetValue(o, null) as Playnite.SDK.Models.Game : null;
        }

        private static void Load(object o)
        {
            var g = AsGame(o);
            if (g == null) { Clear(); return; }  // Capture / Store / Library tiles: no game, no strip
            try { HltbFetch.ForPage(g); } catch (Exception ex) { HomeNav.Log("hltb: " + ex.Message); }   // its times, shown under its name, once a session
            if (g.Id == shown)
            {
                // back on the game already loaded (moved away and back before another loaded): the strip had started to
                // fade out on the way away, so it comes back
                var strip = Strip();
                if (strip != null && strip.Opacity < 0.99) strip.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
                return;
            }
            List<ShotItem> items;
            if (cache.TryGetValue(g.Id, out items)) { Apply(g.Id, items); return; }
            Clear();  // never leave the previous game's pictures up while this one loads
            ShowLoading();
            Fetch(g);
        }

        // the next games along the row are decoded ahead, so moving onto them is instant
        private static void Preload(System.Collections.IList neighbours)
        {
            if (HomeNav.Lite) return;
            if (neighbours == null) return;
            bool light = HomeNav.Light;
            var selected = AsGame(pending);
            foreach (var g in neighbours.Cast<object>().Select(AsGame)
                .Where(g => g != null && (selected == null || g.Id != selected.Id) && !cache.ContainsKey(g.Id) && !loading.Contains(g.Id) && Known(System.IO.Path.Combine(CacheRoot, g.Id.ToString())))
                .Take(Features.On("lightweight") || light ? 1 : int.MaxValue))
            {
                Fetch(g);
            }
        }

        private static void Fetch(Playnite.SDK.Models.Game g)
        {
            var id = g.Id;
            if (loading.Contains(id)) return;
            loading.Add(id);
            string name = g.Name, pluginId = g.PluginId.ToString(), gameId = g.GameId;
            string background = Background(g);
            var selected = AsGame(pending);
            bool mayDownload = selected != null && selected.Id == id;
            int generation = downloadGeneration;
            int decodeWidth = HomeNav.Light ? 480 : 720;
            System.Threading.Tasks.Task.Run(() =>
            {
                var paths = new List<string>();
                try { lock (diskLock) paths = CachedFiles(id, name, pluginId, gameId, mayDownload); }
                catch (Exception ex) { HomeNav.Log("screenshots " + name + " failed: " + ex.Message); }
                if (paths.Count == 0 && background != null) paths.Add(background);
                var list = new List<ShotItem>();
                foreach (var p in paths)
                {
                    var bi = LoadImage(p, decodeWidth);
                    if (bi != null) list.Add(new ShotItem { Thumb = bi, Path = p });
                }
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    loading.Remove(id);
                    if (generation != downloadGeneration)
                    {
                        var selectedNow = AsGame(pending);
                        if (selectedNow != null && selectedNow.Id == id) Fetch(selectedNow);
                        return;
                    }
                    Remember(id, list);
                    var still = AsGame(pending);
                    if (still != null && still.Id == id) Apply(id, list);
                }));
            });
        }

        // decoded pictures are ~1 MB each (720 px wide): keep the last 16 games in memory (about 100 MB; Playnite is a 32-bit
        // process), the rest reload from disk
        private static readonly LinkedList<Guid> recent = new LinkedList<Guid>();

        private static void Remember(Guid id, List<ShotItem> list)
        {
            cache[id] = list;
            recent.Remove(id);
            recent.AddFirst(id);
            while (recent.Count > (HomeNav.Lite || Features.On("lightweight") ? 8 : 16)) { cache.Remove(recent.Last.Value); recent.RemoveLast(); }
        }

        public static void ApplyPerformanceMode()
        {
            // Release our decoded references; no forced collection or deletion of downloaded files.
            cache.Clear(); recent.Clear();
            if (shown != Guid.Empty && current.Count > 0) Remember(shown, current);
        }

        // ---- background download of every game's screenshots (one game at a time, from Playnite start) ----
        private static readonly object diskLock = new object();
        // Called only by the serial library-media worker: cache files, never decode or retain library-sized UI images.
        internal static void DownloadPictures(Playnite.SDK.Models.Game game)
        {
            if (game == null || !DownloadConsent.Allowed("screenshotsdownload")) return;
            lock (diskLock) CachedFiles(game.Id, game.Name, game.PluginId.ToString(), game.GameId, true);
        }
        public static void DownloadSettingsChanged()
        {
            downloadGeneration++; cache.Clear(); recent.Clear(); shown = Guid.Empty;
            var selected = AsGame(pending);
            if (selected != null) Load(selected);
        }
        public static void KickDownloads() { TrailerFetch.Want(pending, null); }

        // done.txt = cached; none.txt = nothing found (look again after a week)
        private static bool Known(string dir)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "done.txt"))) return true;
            var none = System.IO.Path.Combine(dir, "none.txt");
            return System.IO.File.Exists(none) && (DateTime.Now - System.IO.File.GetLastWriteTime(none)).TotalDays < 7;
        }

        private static readonly HashSet<Guid> loading = new HashSet<Guid>();

        private static void Clear()
        {
            HideLoading(false);
            shown = Guid.Empty;
            current = new List<ShotItem>();
            FadeOut();
            Application.Current.Resources["SeleneShotsNone"] = Visibility.Collapsed;
        }

        private static void Apply(Guid id, List<ShotItem> items)
        {
            HideLoading(true);
            shown = id;
            current = items;
            var res = Application.Current.Resources;
            for (int i = 0; i < Tiles; i++)
            {
                res["SeleneShot" + i] = i < items.Count ? items[i].Thumb : null;
                res["SeleneShot" + i + "Vis"] = i < items.Count ? Visibility.Visible : Visibility.Collapsed;
            }
            res["SeleneShotsNone"] = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var strip = Strip();
            if (strip != null) { strip.BeginAnimation(UIElement.OpacityProperty, null); strip.Opacity = 1; }
            // animate once per game: the same game's pictures coming back within 2 s are shown without replaying it
            bool replay = id == lastAnimatedId && (DateTime.Now - lastAnimatedAt).TotalSeconds < 2;
            HomeNav.Log("screenshots show " + id + " (" + items.Count + ")" + (replay ? " - same game again, no animation" : ""));
            if (items.Count > 0 && !replay)
            {
                lastAnimatedId = id; lastAnimatedAt = DateTime.Now;
                AnimateIn();
            }
        }

        private static Grid skeleton;
        private static readonly List<System.Windows.Shapes.Rectangle> shine = new List<System.Windows.Shapes.Rectangle>();

        private static void ShowLoading()
        {
            if (!Features.On("screenshotskeleton")) return;
            var host = Application.Current.MainWindow != null ? HomeNav.FindByName(Application.Current.MainWindow, "HubBottomCards") as Grid : null;
            if (host == null) return;
            if (skeleton == null || skeleton.Parent != host)
            {
                HideLoading(false);
                shine.Clear();
                skeleton = new Grid { Width = 1788, Height = 252, HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true, IsHitTestVisible = false };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                for (int i = 0; i < 4; i++)
                {
                    var card = new Grid { Width = 448, Height = 252, ClipToBounds = true, Margin = new Thickness(0, 0, 22, 0) };
                    var background = new Border { CornerRadius = new CornerRadius(14), Opacity = 0.65 };
                    background.SetResourceReference(Border.BackgroundProperty, "SeleneGlassSolidBrush");
                    card.Children.Add(background);
                    var glint = new System.Windows.Shapes.Rectangle { Width = 200, Height = 252, HorizontalAlignment = HorizontalAlignment.Left,
                        Opacity = 0.12, RenderTransform = new TranslateTransform(-220, 0),
                        OpacityMask = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.White, 0.5), new GradientStop(Colors.Transparent, 1) }, 0) };
                    glint.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SeleneInkBrush");
                    card.Children.Add(glint); shine.Add(glint); row.Children.Add(card);
                }
                skeleton.Children.Add(row);
                var ownedShine = shine.ToArray();
                skeleton.Unloaded += (s, e) => { foreach (var glint in ownedShine) glint.RenderTransform.BeginAnimation(TranslateTransform.XProperty, null); };
                Panel.SetZIndex(skeleton, 2); host.Children.Add(skeleton);
            }
            if (skeleton.Visibility == Visibility.Visible && skeleton.Opacity > 0 && shine.Count > 0 && shine[0].RenderTransform.HasAnimatedProperties) return;
            skeleton.Visibility = Visibility.Visible;
            skeleton.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            for (int i = 0; i < shine.Count; i++)
                shine[i].RenderTransform.BeginAnimation(TranslateTransform.XProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(-220, 470, TimeSpan.FromMilliseconds(1300))
                    { BeginTime = TimeSpan.FromMilliseconds(i * 90), RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        }

        private static void HideLoading(bool fade)
        {
            if (skeleton == null) return;
            foreach (var glint in shine) glint.RenderTransform.BeginAnimation(TranslateTransform.XProperty, null);
            // A held zero opacity can be replaced safely if another game starts loading during this fade.
            skeleton.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(fade ? 220 : 0)));
        }

        // tiles fade in and rise one after another (50 ms apart) once the new screenshots are laid out
        private static WeakReference stripRef;
        private static Guid lastAnimatedId;
        private static DateTime lastAnimatedAt;

        // Each tile's previous animation is removed first: a new animation that waits for its turn (50 ms stagger) otherwise
        // keeps showing the old held value, so every tile was briefly fully visible, then dipped to 0 and faded in (flicker).
        private static void AnimateIn()
        {
            var strip = Strip();
            if (strip == null) return;
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(380);
            for (int i = 0; i < Math.Min(Tiles, current.Count); i++)
            {
                var c = HomeNav.FindByName(strip, "SeleneShotTile" + i) as UIElement;
                if (c == null) continue;
                var shift = c.RenderTransform as TranslateTransform;
                if (shift == null || shift.IsFrozen) { shift = new TranslateTransform(); c.RenderTransform = shift; }
                c.BeginAnimation(UIElement.OpacityProperty, null);
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                c.Opacity = 0;   // base value shown until this tile's turn
                shift.Y = 28;
                var begin = TimeSpan.FromMilliseconds(i * 50);
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = ease };
                var rise = new System.Windows.Media.Animation.DoubleAnimation(28, 0, dur) { BeginTime = begin, EasingFunction = ease };
                // at the end the base values take over, so nothing stays held by an old animation
                var tile = c; var tr = shift;
                fade.Completed += (s, e) => { tile.BeginAnimation(UIElement.OpacityProperty, null); tile.Opacity = 1; };
                rise.Completed += (s, e) => { tr.BeginAnimation(TranslateTransform.YProperty, null); tr.Y = 0; };
                c.BeginAnimation(UIElement.OpacityProperty, fade);
                shift.BeginAnimation(TranslateTransform.YProperty, rise);
            }
        }

        // ---- finding + caching the screenshot files (background thread) ----

        private static List<string> CachedFiles(Guid id, string name, string pluginId, string gameId, bool mayDownload)
        {
            string dir = System.IO.Path.Combine(CacheRoot, id.ToString());
            string done = System.IO.Path.Combine(dir, "done.txt");
            if (Known(dir))
                return System.IO.Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).ToList();

            if (!mayDownload || !DownloadConsent.Allowed("screenshotsdownload"))
                return System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).ToList() : new List<string>();

            var urls = ScreenshotUrls(id, name, pluginId, gameId);
            System.IO.Directory.CreateDirectory(dir);
            var files = new List<string>();
            for (int i = 0; i < urls.Count && files.Count < MaxShots; i++)
            {
                if (!DownloadConsent.Allowed("screenshotsdownload")) break;
                string f = System.IO.Path.Combine(dir, i.ToString("00") + ".jpg");
                try { using (var wc = Client()) wc.DownloadFile(urls[i], f); files.Add(f); PerformanceRecorder.Mark("download", "screenshots game=" + id + " bytes=" + new System.IO.FileInfo(f).Length + " trigger=cache-fill"); }
                catch { try { System.IO.File.Delete(f); } catch { } }
            }
            System.IO.File.WriteAllText(files.Count > 0 ? done : System.IO.Path.Combine(dir, "none.txt"), string.Join(Environment.NewLine, urls));
            HomeNav.Log("screenshots " + name + ": " + files.Count + " cached");
            return files;
        }

        private static List<string> ScreenshotUrls(Guid id, string name, string pluginId, string gameId)
        {
            // the game's own choice (Game options > Metadata source): the source and the id it names, no guessing by name
            var chosen = MediaSource.Override(id);
            if (chosen != null)
            {
                var found = new List<string>();
                bool igdbOnly = chosen.Source == "igdb" || (chosen.Source == "auto" && string.IsNullOrEmpty(chosen.SteamId) && !string.IsNullOrEmpty(chosen.IgdbId));
                if (!igdbOnly)
                {
                    string app = !string.IsNullOrEmpty(chosen.SteamId) ? chosen.SteamId
                               : pluginId == SteamPlugin && Regex.IsMatch(gameId ?? "", @"^\d+$") ? gameId : SearchAppId(name);
                    if (app != null) found = ParseScreenshots(Download("https://store.steampowered.com/api/appdetails?appids=" + app + "&filters=screenshots"));
                }
                if (found.Count == 0 && chosen.Source != "steam") found = !string.IsNullOrEmpty(chosen.IgdbId) ? IgdbScreenshotsById(chosen.IgdbId) : (igdbOnly ? IgdbScreenshots(name) : found);
                HomeNav.Log("screenshots " + name + ": chosen source " + chosen.Source + " (steam " + (chosen.SteamId ?? "-") + ", igdb " + (chosen.IgdbId ?? "-") + "): " + found.Count + " found");
                return found;
            }
            var eml = System.IO.Path.Combine(SelenePaths.PlayniteConfig, "ExtensionsData", EmlData, id + "_SteamAppDetails.json");
            if (System.IO.File.Exists(eml))
            {
                var json = System.IO.File.ReadAllText(eml);
                var root = Json(json);
                var candidates = new List<KeyValuePair<string, string>>();
                if (root != null) foreach (var entry in root)
                {
                    var app = entry.Value as Dictionary<string, object>;
                    object data, title;
                    var details = app != null && app.TryGetValue("data", out data) ? data as Dictionary<string, object> : null;
                    if (details != null && details.TryGetValue("name", out title)) candidates.Add(new KeyValuePair<string, string>(entry.Key, Convert.ToString(title)));
                }
                var matched = GameTitleMatch.Select(name, candidates);
                if (matched != null && (pluginId != SteamPlugin || matched == gameId))
                {
                    var fromEml = ParseScreenshots(json);
                    if (fromEml.Count > 0) return fromEml;
                }
            }
            string appId = pluginId == SteamPlugin && Regex.IsMatch(gameId ?? "", @"^\d+$") ? gameId : SearchAppId(name);
            var urls = appId == null ? new List<string>() : ParseScreenshots(Download("https://store.steampowered.com/api/appdetails?appids=" + appId + "&filters=screenshots"));
            if (urls.Count == 0) urls = IgdbScreenshots(name);
            return urls;
        }

        // IGDB via Playnite's own metadata backend (what the built-in IGDB plugin uses; no key needed)
        private const string PlayniteBackend = "https://api2.playnite.link/api/igdb/";

        private static List<string> IgdbScreenshots(string name)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(name)) return list;
            string body;
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                body = wc.UploadString(PlayniteBackend + "search", "{\"SearchTerm\":" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(name) + "}");
            }
            var found = Json(body);
            object data;
            if (found == null || !found.TryGetValue("data", out data) || !(data is object[]) || ((object[])data).Length == 0) return list;
            string want = Norm(name), igdbId = null;
            foreach (var o in (object[])data)
            {
                var d = o as Dictionary<string, object>;
                if (d != null && Norm(Convert.ToString(d["name"])) == want) { igdbId = Convert.ToString(d["id"]); break; }
            }
            if (igdbId == null) return list;  // no exact name match: better nothing than another game's pictures
            return IgdbScreenshotsById(igdbId);
        }

        private static List<string> IgdbScreenshotsById(string igdbId)
        {
            var list = new List<string>();
            var game = Json(Download(PlayniteBackend + "game/" + igdbId));
            object g, shots;
            if (game == null || !game.TryGetValue("data", out g) || !(g is Dictionary<string, object>)) return list;
            if (!((Dictionary<string, object>)g).TryGetValue("screenshots_expanded", out shots) || !(shots is object[])) return list;
            foreach (var s in (object[])shots)
            {
                var d = s as Dictionary<string, object>;
                object imageId;
                if (d != null && d.TryGetValue("image_id", out imageId)) list.Add("https://images.igdb.com/igdb/image/upload/t_1080p/" + imageId + ".jpg");
            }
            return list;
        }

        // a game's screenshots looked up again (after its metadata source changed): the saved ones go, the strip loads afresh
        public static void Refresh(Playnite.SDK.Models.Game g)
        {
            if (g == null) return;
            var id = g.Id;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { lock (diskLock) { var dir = System.IO.Path.Combine(CacheRoot, id.ToString()); if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } }
                catch (Exception ex) { HomeNav.Log("screenshots refresh: " + ex.Message); }
                Application.Current.Dispatcher.BeginInvoke(new Action(() => { cache.Remove(id); loading.Remove(id); if (shown == id) shown = Guid.Empty; Fetch(g); }));
            });
        }

        internal static string SteamAppIdByName(string name) { return SearchAppId(name); }

        private static string SearchAppId(string name)
        {
            string legacy;
            return SearchAppId(name, out legacy);
        }

        internal static string SearchAppId(string name, out string legacy)
        {
            legacy = null;
            if (string.IsNullOrWhiteSpace(name)) return null;
            var json = Download("https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(name));
            var root = Json(json);
            object items;
            if (root == null || !root.TryGetValue("items", out items)) return null;
            string want = Norm(name);
            var candidates = new List<KeyValuePair<string, string>>();
            foreach (var o in (object[])items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string n = Norm(Convert.ToString(d["name"]));
                var id = Convert.ToString(d["id"]);
                candidates.Add(new KeyValuePair<string, string>(id, Convert.ToString(d["name"])));
                if (legacy == null && (n == want || n.StartsWith(want) || want.StartsWith(n))) legacy = id;
            }
            return GameTitleMatch.Select(name, candidates);
        }

        private static string Norm(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9]", ""); }

        private static List<string> ParseScreenshots(string json)
        {
            var list = new List<string>();
            var root = Json(json);
            if (root == null) return list;
            foreach (var app in root.Values)
            {
                var a = app as Dictionary<string, object>;
                object data, shots;
                if (a == null || !a.TryGetValue("data", out data) || !(data is Dictionary<string, object>)) continue;
                if (!((Dictionary<string, object>)data).TryGetValue("screenshots", out shots) || !(shots is object[])) continue;
                foreach (var s in (object[])shots)
                {
                    var d = s as Dictionary<string, object>;
                    object full;
                    if (d != null && d.TryGetValue("path_full", out full) && full != null) list.Add(Regex.Replace(Convert.ToString(full), @"\?.*$", ""));
                }
            }
            return list;
        }

        private static Dictionary<string, object> Json(string json)
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return ser.DeserializeObject(json) as Dictionary<string, object>;
        }

        private static WebClient Client()
        {
            var wc = new WebClient { Encoding = System.Text.Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
            return wc;
        }

        private static string Download(string url) { using (var wc = Client()) return wc.DownloadString(url); }

        private static string Background(Playnite.SDK.Models.Game g)
        {
            string hero = System.IO.Path.Combine(SelenePaths.Media("Hero"), g.Id + ".jpg");
            if (System.IO.File.Exists(hero)) return hero;
            if (!string.IsNullOrEmpty(g.BackgroundImage) && HomeNav.Api != null) return HomeNav.Api.Database.GetFullFilePath(g.BackgroundImage);
            return null;
        }

        private static BitmapImage LoadImage(string path, int width)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path) || LoginScene.DevLeaveOut("shots")) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                // read from the file and closed once decoded (a MemoryStream source stayed referenced by the frozen picture,
                // so every cached screenshot also kept its whole file in memory); no shared WPF cache, a replaced file reloads
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
                bi.UriSource = new Uri(path);
                if (width > 0) bi.DecodePixelWidth = width;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // ---- full-screen viewer: A on a tile opens it, Left/Right flip, A or B close ----

        private static Grid viewer;
        private static Image viewerImage;
        private static TextBlock viewerCount;
        private static List<ShotItem> viewerItems;
        private static int viewerIndex;
        private static IInputElement returnFocus;

        // the selected game's screenshots as they are now, and the viewer from outside (the details page's band)
        public static List<ShotItem> Current { get { return current; } }
        public static bool ViewerOpen { get { return viewer != null; } }
        public static void OpenViewerAt(int index) { if (index >= 0 && index < current.Count) OpenViewer(current, index, System.Windows.Input.Keyboard.FocusedElement); }

        public static bool HandleButton(string pressed, object focused)
        {
            if (viewer != null)
            {
                if (pressed == "B" || pressed == "A") { CloseViewer(); return true; }
                if (pressed == "DPadRight" || pressed == "LeftStickRight") { Show(viewerIndex + 1); return true; }
                if (pressed == "DPadLeft" || pressed == "LeftStickLeft") { Show(viewerIndex - 1); return true; }
                return true;  // nothing else reaches the home underneath
            }
            if (pressed != "A") return false;
            var fe = focused as FrameworkElement;
            int index;
            if (fe == null || !fe.Name.StartsWith("SeleneShotTile") || !int.TryParse(Convert.ToString(fe.Tag), out index)) return false;
            if (index >= current.Count) return false;
            OpenViewer(current, index, fe);
            return true;
        }

        private static void OpenViewer(List<ShotItem> items, int index, IInputElement from)
        {
            var w = Application.Current.MainWindow;
            var host = RootPanel(w);
            if (host == null) return;
            viewerItems = items;
            returnFocus = from;
            viewerImage = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(60, 50, 60, 90) };
            RenderOptions.SetBitmapScalingMode(viewerImage, BitmapScalingMode.HighQuality);
            viewerCount = new TextBlock
            {
                Foreground = Ink.Brush("SeleneInkColor", 0.7), FontSize = 20, FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40),
            };
            viewer = new Grid { Background = Ink.Brush("SeleneScrimColor", 0.95), Focusable = false };
            if (host is Grid) { Grid.SetRowSpan(viewer, 99); Grid.SetColumnSpan(viewer, 99); }
            Panel.SetZIndex(viewer, 99999);
            viewer.Children.Add(viewerImage);
            viewer.Children.Add(viewerCount);
            host.Children.Add(viewer);
            Show(index);
        }

        private static void Show(int index)
        {
            if (viewerItems == null || viewerItems.Count == 0) return;
            viewerIndex = (index % viewerItems.Count + viewerItems.Count) % viewerItems.Count;
            var it = viewerItems[viewerIndex];
            viewerImage.Source = LoadImage(it.Path, 0) ?? (ImageSource)it.Thumb;
            viewerCount.Text = viewerItems.Count > 1 ? (viewerIndex + 1) + " / " + viewerItems.Count : "";
        }

        private static void CloseViewer()
        {
            var p = viewer != null ? viewer.Parent as Panel : null;
            if (p != null) p.Children.Remove(viewer);
            viewer = null; viewerImage = null; viewerItems = null;
            if (returnFocus != null) returnFocus.Focus();
        }

        // the outermost panel that fills the window, so the viewer covers the whole screen
        private static Panel RootPanel(Window w)
        {
            if (w == null) return null;
            DependencyObject d = w;
            for (int i = 0; i < 12 && d != null; i++)
            {
                var p = d as Panel;
                if (p != null && p.ActualWidth >= w.ActualWidth - 40 && p.ActualHeight >= w.ActualHeight - 40) return p;
                d = VisualTreeHelper.GetChildrenCount(d) > 0 ? VisualTreeHelper.GetChild(d, 0) : null;
            }
            return null;
        }
    }
}
