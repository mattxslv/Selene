using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using Playnite.SDK.Models;

namespace Selene
{
    // Trailers without any setup: a game's Steam trailer (720p, its first minute, with sound) is downloaded into
    // ExtraMetadata\games\<id>\VideoTrailer.mp4, the file Extra Metadata Loader keeps its trailers in, so Spotlight's trailer
    // cards, Collection and that add-on all find it. Steam only streams its trailers now (DASH pieces), so the pieces are
    // fetched and joined (Mp4Join), nothing to install. Setting "Download trailers": the games you look at (and the next few
    // along the row), the whole library in the background, or off. Games not on Steam are looked up there by name.
    public static class TrailerFetch
    {
        private const string SteamPlugin = "cb91dfc9-b977-43bf-8e70-55f46e410fab";
        private const int MaxHeight = 720;
        private const double MaxSeconds = 60;   // the cards play 30 s; Collection loops it

        private static readonly LinkedList<Guid> queue = new LinkedList<Guid>();
        private static readonly HashSet<Guid> tried = new HashSet<Guid>();   // this session: asked once
        private static readonly HashSet<Guid> browsed = new HashSet<Guid>();   // looked at (their trailer is wanted)
        private static readonly object gate = new object();
        private static System.Threading.Thread worker;
        private static bool libraryQueued;
        private static readonly HashSet<Guid> libraryMedia = new HashSet<Guid>();
        private static int libraryTotal, libraryChecked;
        public static void LibraryCounts(out int done, out int total) { lock (gate) { done = libraryChecked; total = libraryTotal; } }
        public static double LibraryProgress { get { lock (gate) return libraryTotal > 0 ? (double)libraryChecked / libraryTotal : -1; } }
        public static string LibraryStatus { get { lock (gate) return libraryTotal > 0 ? "Art / trailers / pictures: " + libraryChecked + " of " + libraryTotal + " checked" : "No media queued"; } }
        public static void QueueLibraryMedia()
        {
            if (!MediaDownloads.Enabled || HomeNav.Api == null) return;
            var ids = HomeNav.Api.Database.Games.Where(g => !g.Hidden).OrderByDescending(g => g.LastActivity ?? DateTime.MinValue).Select(g => g.Id).ToList();
            lock (gate)
            {
                libraryMedia.Clear(); libraryTotal = ids.Count; libraryChecked = 0; libraryQueued = true;
                var queued = new HashSet<Guid>(queue);
                foreach (var id in ids) { libraryMedia.Add(id); tried.Remove(id); if (queued.Add(id)) queue.AddLast(id); }
            }
            Start();
        }

        private static string Mode { get { return Features.Value("trailerdownload"); } }
        private static bool WantTrailers { get { return DownloadConsent.Allowed("trailerdownload"); } }
        private static bool WantArt { get { return DownloadConsent.Allowed("artdownload"); } }
        [ThreadStatic] private static string permission;
        public static void DownloadSettingsChanged()
        {
            lock (gate) { queue.Clear(); tried.Clear(); libraryQueued = false; libraryMedia.Clear(); libraryTotal = libraryChecked = 0; }
        }

        // trailers, logos or backgrounds still coming (for the red dot on the menu and the Spotlight group)
        public static bool Busy { get { lock (gate) return worker != null; } }
        public static int Pending { get { lock (gate) return queue.Count + (worker != null ? 1 : 0); } }
        private static int done;   // games taken off the queue since the worker started (the bar under the setting)
        public static double Progress
        {
            get { lock (gate) { if (worker == null) return -1; int left = queue.Count + 1, all = done + left; return all > 0 ? (double)done / all : -1; } }
        }

        public static string PathFor(Guid id)
        {
            return System.IO.Path.Combine(HomeNav.Api.Paths.ConfigurationPath, "ExtraMetadata", "games", id.ToString(), "VideoTrailer.mp4");
        }

        // a game was selected: its trailer first, then the next ones along (they're likely next)
        public static void Want(object game, System.Collections.IList neighbours)
        {
            if (HomeNav.Api == null || (!WantTrailers && !WantArt)) return;
            if (WantTrailers && Mode == "all") QueueLibrary();
            var list = new List<Guid>();
            var g = AsGame(game);
            if (g != null) list.Add(g.Id);
            lock (gate)
            {
                // to the front in this order (the selected game first); a game the library pass already did (its art)
                // comes again the first time it's looked at, for its trailer
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var id = list[i];
                    if (browsed.Add(id)) tried.Remove(id);
                    if (tried.Contains(id)) continue;
                    queue.Remove(id);
                    queue.AddFirst(id);
                }
            }
            Start();
        }

        // "Whole library": every game, the last played first, behind the ones being looked at
        private static void QueueLibrary()
        {
            if (libraryQueued || !WantTrailers || Mode != "all") return;
            libraryQueued = true;
            var ids = HomeNav.Api.Database.Games.Where(x => !x.Hidden).OrderByDescending(x => x.LastActivity ?? DateTime.MinValue).Select(x => x.Id).ToList();
            lock (gate) { var queued = new HashSet<Guid>(queue); foreach (var id in ids) if (queued.Add(id)) queue.AddLast(id); }
            Start();
        }

        private static void Start()
        {
            lock (gate)
            {
                if (worker != null || queue.Count == 0) return;
                worker = new System.Threading.Thread(Work) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal };
                worker.Start();
            }
        }

        private static void Work()
        {
            int made = 0;
            while (true)
            {
                Guid id;
                lock (gate)
                {
                    if (queue.Count == 0 || (!WantTrailers && !WantArt)) { queue.Clear(); worker = null; done = 0; break; }
                    id = queue.First.Value;
                    queue.RemoveFirst();
                    done++;
                    if (!tried.Add(id)) continue;
                }
                // A library batch should not compete with a running game between transfers.
                while (MediaDownloads.Unified && MediaDownloads.Enabled && (WantTrailers || WantArt) && HomeNav.GameRunning()) System.Threading.Thread.Sleep(1000);
                bool looked;
                lock (gate) looked = browsed.Contains(id);
                PerformanceRecorder.Mark("media_queue", "game=" + id + " browsed=" + looked + " trailerScope=" + Mode + " pending=" + Pending);
                asked = false;
                bool retry = false;
                try { if (Fetch(id)) made++; }
                catch (Exception ex)
                {
                    HomeNav.Log("trailer download: " + id + " failed: " + ex.Message);
                    // Steam saying "too many requests": that game goes to the back, and a minute's rest
                    var web = ex as WebException;
                    var status = web != null && web.Response is HttpWebResponse ? (int)((HttpWebResponse)web.Response).StatusCode : 0;
                    if (status == 429) { retry = true; lock (gate) { tried.Remove(id); queue.AddLast(id); } System.Threading.Thread.Sleep(60000); }
                }
                finally { lock (gate) if (!retry && libraryMedia.Remove(id)) libraryChecked++; }
                // the library pass goes gently (Steam limits how often it's asked); the games being looked at don't wait
                if (!looked && asked) System.Threading.Thread.Sleep(1500);
            }
            if (made > 0) HomeNav.Log("trailer download: " + made + " new");
        }

        // true when a trailer was made
        private static bool Fetch(Guid id)
        {
            var g = HomeNav.Api.Database.Games.Get(id);
            if (g == null) return false;
            string appId = null;
            bool appIdLooked = false;
            Func<string> steamId = () =>
            {
                if (!appIdLooked)
                {
                    appIdLooked = true;
                    var chosen = MediaSource.Override(g.Id);   // (Game options > Metadata source)
                    if (chosen != null && !string.IsNullOrEmpty(chosen.SteamId)) appId = chosen.SteamId;
                    else if (chosen != null && chosen.Source == "igdb") appId = null;
                    else if (g.PluginId.ToString() == SteamPlugin) appId = g.GameId;
                    else { asked = true; appId = GameMedia.SteamAppIdByName(g.Name); }   // (a search on Steam's store)
                }
                return appId;
            };
            bool selected, bulk; lock (gate) { selected = browsed.Contains(id); bulk = libraryMedia.Contains(id) && MediaDownloads.Enabled; }
            permission = "artdownload";
            if (WantArt && (selected || bulk)) { try { FetchArt(g, steamId); } catch (Exception ex) { HomeNav.Log("art download: " + g.Name + " failed: " + ex.Message); } }
            if (WantArt && (selected || bulk)) { try { SquareFetch.Fetch(g, steamId); } catch (Exception ex) { HomeNav.Log("square art: " + g.Name + " failed: " + ex.Message); } }
            if (bulk) { try { GameMedia.DownloadPictures(g); } catch (Exception ex) { HomeNav.Log("library pictures: " + g.Name + ": " + ex.Message); } }
            permission = "trailerdownload";

            // the trailer: a game looked at, or every game with "Whole library"
            bool wanted;
            lock (gate) wanted = WantTrailers && (Mode == "all" || (Mode == "browse" && browsed.Contains(id)));
            if (!wanted) return false;
            var file = PathFor(id);
            if (System.IO.File.Exists(file)) return false;
            // nothing on Steam: asked again after a week
            var none = System.IO.Path.Combine(SelenePaths.Media("Trailers"), id + ".none");
            if (System.IO.File.Exists(none) && (DateTime.Now - System.IO.File.GetLastWriteTime(none)).TotalDays < 7) return false;

            var mpd = string.IsNullOrEmpty(steamId()) ? null : ManifestUrl(steamId());
            if (mpd == null) { System.IO.File.WriteAllText(none, g.Name); return false; }

            var started = DateTime.Now;
            var dir = System.IO.Path.GetDirectoryName(file);
            System.IO.Directory.CreateDirectory(dir);
            var part = file + ".part";
            try
            {
                Download(mpd, part);
                if (System.IO.File.Exists(file)) { System.IO.File.Delete(part); return false; }   // Extra Metadata Loader got there first
                System.IO.File.Move(part, file);
                PerformanceRecorder.Mark("download", "trailer game=" + id + " bytes=" + new System.IO.FileInfo(file).Length + " scope=" + Mode);
            }
            finally { try { if (System.IO.File.Exists(part)) System.IO.File.Delete(part); } catch { } }
            HomeNav.Log("trailer download: " + g.Name + " (" + (new System.IO.FileInfo(file).Length / 1048576.0).ToString("0.0") + " MB, " + (DateTime.Now - started).TotalSeconds.ToString("0.0") + " s)");
            Application.Current.Dispatcher.BeginInvoke(new Action(() => { HomeNav.TrailerReady(id); LibraryStage.TrailerReady(id); }));
            return true;
        }

        // ---- art: the logo (ExtraMetadata\games\<id>\logo.png, where Extra Metadata Loader keeps it) and the wide background
        // (the media folder's Hero\<id>.jpg) from Steam, for the games that have none: Spotlight, Collection and a game's page
        // show them over the plain store background. Asked again after a week when Steam had nothing.
        private const string SteamAssets = "https://shared.akamai.steamstatic.com/store_item_assets/";

        private static void FetchArt(Game g, Func<string> steamId)
        {
            var logo = System.IO.Path.Combine(HomeNav.Api.Paths.ConfigurationPath, "ExtraMetadata", "games", g.Id.ToString(), "logo.png");
            var hero = System.IO.Path.Combine(SelenePaths.Media("Hero"), g.Id + ".jpg");
            RepairLegacyArt(g, logo, hero);
            bool needLogo = !System.IO.File.Exists(logo), needHero = !System.IO.File.Exists(hero);
            if (!needLogo) TrimLogoOnce(g.Id, logo);   // (one saved on a wide canvas before the trim came in)
            if (!needHero && IsBanner(hero)) { try { System.IO.File.Delete(hero); needHero = true; } catch { } }   // a banner saved before the crop came in: fetched again, sharper
            // a background copied from Steam's cache is its small banner (1102 x 620 once cut, blurry on a 4K screen): the store's
            // double-size one asked for once
            var sharpTried = System.IO.Path.Combine(SelenePaths.Media("Trailers"), g.Id + ".herohd");
            bool sharpen = !needHero && !System.IO.File.Exists(sharpTried) && SmallSteamHero(hero);
            if (!needLogo && !needHero && !sharpen) return;
            var none = System.IO.Path.Combine(SelenePaths.Media("Trailers"), g.Id + ".noart");
            if (System.IO.File.Exists(none) && (DateTime.Now - System.IO.File.GetLastWriteTime(none)).TotalDays < 7) return;
            var id = steamId();
            if (string.IsNullOrEmpty(id)) { System.IO.File.WriteAllText(none, g.Name); return; }

            // Steam's own library cache on this PC first (it has the logos the store page doesn't: newer games keep theirs
            // in hashed folders only Steam knows)
            if (needLogo && CopyFromSteam(id, "logo.png", logo)) { needLogo = false; TrimLogo(logo); HomeNav.Log("art download: " + g.Name + " logo (Steam's cache)"); }
            // (its background only when the store has none: the cache keeps just the small one)

            // the file names (newer games have them in hashed folders) from Steam's store service
            string heroName = "library_hero.jpg";
            Dictionary<string, object> assets = null;
            try { assets = StoreAssets(id, address => new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(Text(address))); }
            catch (Exception ex) { HomeNav.Log("art download: " + g.Name + ": store service: " + ex.Message); }
            if (assets != null)
            {
                if (assets.ContainsKey("library_hero_2x")) heroName = (string)assets["library_hero_2x"];   // (sharper once cropped)
                else if (assets.ContainsKey("library_hero")) heroName = (string)assets["library_hero"];
            }
            Func<string, string> url = name => AssetUrl(id, assets, name);

            bool got = false;
            if (needLogo)
                foreach (var name in new[] { "logo_2x.png", "logo.png" })
                    if (Save(url(name), logo, true)) { got = true; needLogo = false; TrimLogo(logo); HomeNav.Log("art download: " + g.Name + " logo"); break; }
            // newer games keep their logo in a hashed folder that only Steam's app info names (the store page doesn't):
            // read through steamcmd.net, a public mirror of it
            if (needLogo)
                foreach (var name in LogoNamesFromAppInfo(id))
                    if (Save(url(name), logo, true)) { got = true; needLogo = false; TrimLogo(logo); HomeNav.Log("art download: " + g.Name + " logo (app info)"); break; }
            bool heroSaved = false;
            if (needHero)
            {
                try { heroSaved = Save(url(heroName), hero, false); }
                catch (Exception ex) { HomeNav.Log("art download: " + g.Name + " background: " + ex.Message); }
            }
            if (heroSaved)
            {
                got = true;
                Crop16x9(hero);
                HeroArrived();
                HomeNav.Log("art download: " + g.Name + " background");
            }
            else if (needHero && CopyFromSteam(id, "library_hero.jpg", hero)) { got = true; Crop16x9(hero); HeroArrived(); HomeNav.Log("art download: " + g.Name + " background (Steam's cache)"); }
            if (sharpen)
            {
                var fresh = hero + ".hd";
                if (Save(url(heroName), fresh, false))
                {
                    Crop16x9(fresh);
                    if (PixelWidth(fresh) > PixelWidth(hero)) { System.IO.File.Delete(hero); System.IO.File.Move(fresh, hero); HeroArrived(); HomeNav.Log("art download: " + g.Name + " sharper background"); }
                }
                try { System.IO.File.Delete(fresh); } catch { }
                System.IO.File.WriteAllText(sharpTried, g.Name);
            }
            if (!got && (needLogo && !System.IO.File.Exists(logo)) && (needHero && !System.IO.File.Exists(hero))) System.IO.File.WriteAllText(none, g.Name);
        }

        // Old downloads have no provenance record. Only replace art whose decoded pixels match the old,
        // rejected Steam result. Unrecognized/custom files stay intact. Backups remain in our data folder.
        private static void RepairLegacyArt(Game g, string logo, string hero)
        {
            if (g.PluginId.ToString() == SteamPlugin || MediaSource.Override(g.Id) != null) return;
            var marker = System.IO.Path.Combine(SelenePaths.Media("ArtMatching-v3"), g.Id + ".txt");
            if (System.IO.File.Exists(marker)) return;
            if (!System.IO.File.Exists(logo) && !System.IO.File.Exists(hero)) return;
            asked = true;
            string legacy;
            var current = GameMedia.SearchAppId(g.Name, out legacy);
            if (legacy != null && legacy != current)
            {
                var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Selene-art-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(scratch);
                try
                {
                    var baseUrl = SteamAssets + "steam/apps/" + legacy + "/";
                    if (System.IO.File.Exists(logo))
                    {
                        foreach (var name in new[] { "logo_2x.png", "logo.png" }.Concat(LogoNamesFromAppInfo(legacy)))
                        {
                            var probe = System.IO.Path.Combine(scratch, "logo.png");
                            if (System.IO.File.Exists(probe)) System.IO.File.Delete(probe);
                            if (!Save(baseUrl + name, probe, true)) continue;
                            bool same = SamePixels(logo, probe);
                            TrimLogo(probe);
                            if (same || SamePixels(logo, probe)) { BackUpMismatchedArt(g.Id, logo); break; }
                        }
                    }
                    if (System.IO.File.Exists(hero))
                    {
                        foreach (var heroUrl in LegacyHeroUrls(legacy))
                        {
                            var probe = System.IO.Path.Combine(scratch, "hero.jpg");
                            if (System.IO.File.Exists(probe)) System.IO.File.Delete(probe);
                            if (!Save(heroUrl, probe, false)) continue;
                            bool same = SamePixels(hero, probe);
                            Crop16x9(probe);
                            if (same || SamePixels(hero, probe)) { BackUpMismatchedArt(g.Id, hero); break; }
                        }
                    }
                    HomeNav.Log("art matching: " + g.Name + ": rejected legacy Steam " + legacy + ", new match " + (current ?? "none"));
                }
                finally
                {
                    // This unique scratch directory contains only the two files written above.
                    foreach (var file in System.IO.Directory.GetFiles(scratch)) System.IO.File.Delete(file);
                    System.IO.Directory.Delete(scratch);
                }
            }
            System.IO.File.WriteAllText(marker, g.Name + "\n" + (current ?? "none"));
        }

        private static List<string> LegacyHeroUrls(string id)
        {
            var urls = new List<string>();
            var query = "{\"ids\":[{\"appid\":" + id + "}],\"context\":{\"language\":\"english\",\"country_code\":\"US\"},\"data_request\":{\"include_assets\":true}}";
            var root = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(Text("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(query))) as Dictionary<string, object>;
            var response = root != null && root.ContainsKey("response") ? root["response"] as Dictionary<string, object> : null;
            var items = response != null && response.ContainsKey("store_items") ? response["store_items"] as object[] : null;
            var item = items != null && items.Length > 0 ? items[0] as Dictionary<string, object> : null;
            var assets = item != null && item.ContainsKey("assets") ? item["assets"] as Dictionary<string, object> : null;
            if (assets != null && assets.ContainsKey("asset_url_format"))
                foreach (var key in new[] { "library_hero_2x", "library_hero" })
                    if (assets.ContainsKey(key)) urls.Add(SteamAssets + ((string)assets["asset_url_format"]).Replace("${FILENAME}", (string)assets[key]));
            urls.Add(SteamAssets + "steam/apps/" + id + "/library_hero_2x.jpg");
            urls.Add(SteamAssets + "steam/apps/" + id + "/library_hero.jpg");
            return urls.Distinct().ToList();
        }

        private static void BackUpMismatchedArt(Guid id, string file)
        {
            var backup = System.IO.Path.Combine(SelenePaths.DataFolder, "ArtMatchingBackups", id.ToString());
            System.IO.Directory.CreateDirectory(backup);
            System.IO.File.Move(file, System.IO.Path.Combine(backup, Guid.NewGuid().ToString("N") + "-" + System.IO.Path.GetFileName(file)));
            HomeNav.Log("art matching: backed up mismatched " + file);
        }

        private static bool SamePixels(string a, string b)
        {
            Func<string, System.Windows.Media.Imaging.BitmapSource> read = file =>
            {
                using (var stream = System.IO.File.OpenRead(file))
                    return new System.Windows.Media.Imaging.FormatConvertedBitmap(System.Windows.Media.Imaging.BitmapFrame.Create(stream,
                        System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad),
                        System.Windows.Media.PixelFormats.Bgra32, null, 0);
            };
            var left = read(a); var right = read(b);
            if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight) return false;
            int stride = left.PixelWidth * 4;
            var x = new byte[stride * left.PixelHeight]; var y = new byte[x.Length];
            left.CopyPixels(x, stride, 0); right.CopyPixels(y, stride, 0);
            return x.SequenceEqual(y);
        }

        // library_logo from Steam's app info (2x first, English), as asset file names ("<hash>/logo_2x.png"); none when unknown
        private static List<string> LogoNamesFromAppInfo(string appId)
        {
            var names = new List<string>();
            try
            {
                var root = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(Text("https://api.steamcmd.net/v1/info/" + appId)) as Dictionary<string, object>;
                Func<object, string, object> at = (o, k) => { var d = o as Dictionary<string, object>; object v; return d != null && d.TryGetValue(k, out v) ? v : null; };
                var logo = at(at(at(at(at(root, "data"), appId), "common"), "library_assets_full"), "library_logo");
                foreach (var size in new[] { "image2x", "image" })
                {
                    var name = at(at(logo, size), "english") as string;
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            catch (Exception ex) { HomeNav.Log("art download: app info " + appId + ": " + ex.Message); }
            return names;
        }

        // the theme only looks in the media folder once something is there (SeleneShell.ApplyArtRoots)
        private static void HeroArrived()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if ("".Equals(Application.Current.Resources["SeleneHeroRoot"])) SeleneShell.ApplyArtRoots();
                SeleneCrossfade.FileChanged();   // (a sharper copy over one already shown)
            }));
        }

        // a picture from Steam's library cache (librarycache\<appid>\ or a hashed folder under it); false when it has none
        private static bool CopyFromSteam(string appId, string name, string to)
        {
            try
            {
                var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (string.IsNullOrEmpty(steam)) return false;
                var dir = System.IO.Path.Combine(steam.Replace('/', '\\'), "appcache", "librarycache", appId);
                if (!System.IO.Directory.Exists(dir)) return false;
                var found = System.IO.Directory.GetFiles(dir, name, System.IO.SearchOption.AllDirectories)
                    .OrderByDescending(f => new System.IO.FileInfo(f).Length).FirstOrDefault();
                if (found == null) return false;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                System.IO.File.Copy(found, to + ".part", true);
                if (System.IO.File.Exists(to)) { System.IO.File.Delete(to + ".part"); return true; }
                System.IO.File.Move(to + ".part", to);
                return true;
            }
            catch (Exception ex) { HomeNav.Log("art: Steam's cache: " + ex.Message); return false; }
        }

        // Steam's backgrounds are banners (about 3:1); Selene's are 16:9 like the screen (a game's page stretches the art over
        // its whole tall page: a banner was blown up to a slice of its middle). The middle, cut to 16:9; nothing for one that
        // is already about that shape.
        private static bool IsBanner(string path)
        {
            try
            {
                using (var file = System.IO.File.OpenRead(path))
                {
                    var frame = System.Windows.Media.Imaging.BitmapFrame.Create(file, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                    return frame.PixelHeight > 0 && (double)frame.PixelWidth / frame.PixelHeight >= 1.9;
                }
            }
            catch { return false; }
        }

        // a game's store pictures by name (library_capsule_2x, library_hero...): newer games keep them in hashed folders
        // that only Steam's store service names; null when it can't be asked
        // (json: the caller's own download, under the permission its work runs with)
        internal static Dictionary<string, object> StoreAssets(string appId, Func<string, object> json)
        {
            var q = "{\"ids\":[{\"appid\":" + appId + "}],\"context\":{\"language\":\"english\",\"country_code\":\"US\"},\"data_request\":{\"include_assets\":true}}";
            var root = json("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(q)) as Dictionary<string, object>;
            var items = root != null && root.ContainsKey("response") ? ((Dictionary<string, object>)root["response"])["store_items"] as object[] : null;
            return items != null && items.Length > 0 && ((Dictionary<string, object>)items[0]).ContainsKey("assets") ? ((Dictionary<string, object>)items[0])["assets"] as Dictionary<string, object> : null;
        }

        // a file's address: the hashed name the store service gave for that asset key, or the plain old one
        internal static string AssetUrl(string appId, Dictionary<string, object> assets, string name)
        {
            string format = assets != null && assets.ContainsKey("asset_url_format") ? (string)assets["asset_url_format"] : "steam/apps/" + appId + "/${FILENAME}";
            return SteamAssets + format.Replace("${FILENAME}", name);
        }

        private static int PixelWidth(string path)
        {
            try
            {
                using (var file = System.IO.File.OpenRead(path))
                    return System.Windows.Media.Imaging.BitmapFrame.Create(file, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).PixelWidth;
            }
            catch { return 0; }
        }

        // Steam's small 1920 x 620 banner cut to 16:9 (what its cache keeps); other sizes are someone's choice and stay
        internal static bool SmallSteamHero(string path)
        {
            try
            {
                using (var file = System.IO.File.OpenRead(path))
                {
                    var frame = System.Windows.Media.Imaging.BitmapFrame.Create(file, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                    return frame.PixelHeight == 620 && Math.Abs(frame.PixelWidth - 1102) <= 1;
                }
            }
            catch { return false; }
        }

        private static void Crop16x9(string path)
        {
            try
            {
                System.Windows.Media.Imaging.BitmapSource src;
                using (var file = System.IO.File.OpenRead(path))
                    src = System.Windows.Media.Imaging.BitmapFrame.Create(file, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                int w = src.PixelWidth, h = src.PixelHeight;
                if (h == 0 || (double)w / h < 1.9) return;
                int cw = (int)Math.Round(h * 16.0 / 9);
                var crop = new System.Windows.Media.Imaging.CroppedBitmap(src, new System.Windows.Int32Rect((w - cw) / 2, 0, cw, h));
                var jpg = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 92 };
                jpg.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
                using (var outFile = System.IO.File.Create(path + ".part")) jpg.Save(outFile);
                System.IO.File.Delete(path);
                System.IO.File.Move(path + ".part", path);
            }
            catch (Exception ex) { HomeNav.Log("art: crop " + System.IO.Path.GetFileName(path) + ": " + ex.Message); }
        }

        // (ink = clearly visible pixels: the soft shadow many logos carry across their whole canvas isn't, or nothing would ever be cut)
        // Steam's client cache keeps a logo on a whole 1280 x 720 canvas, the logo somewhere inside it with empty space around:
        // fitted to the logo's box that canvas fits and the letters land wherever they were. Cut to the ink (a little room kept),
        // as the store's logos come. Nothing for a logo already tight.
        private static void TrimLogo(string path)
        {
            try
            {
                System.Windows.Media.Imaging.BitmapSource src;
                using (var file = System.IO.File.OpenRead(path))
                    src = System.Windows.Media.Imaging.BitmapFrame.Create(file, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                if (src.Format != System.Windows.Media.PixelFormats.Bgra32) src = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
                if (w < 8 || h < 8) return;
                var px = new byte[stride * h];
                src.CopyPixels(px, stride, 0);
                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                {
                    int rowAt = y * stride;
                    for (int x = 0; x < w; x++)
                        if (px[rowAt + x * 4 + 3] > 40) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                }
                if (maxX < 0) return;
                int bw = maxX - minX + 1, bh = maxY - minY + 1;
                if (bw > w * 0.9 && bh > h * 0.9) return;   // tight already
                int pad = Math.Max(2, (int)(Math.Max(bw, bh) * 0.03));
                int cx = Math.Max(0, minX - pad), cy = Math.Max(0, minY - pad);
                int cw = Math.Min(w - cx, bw + 2 * pad), ch = Math.Min(h - cy, bh + 2 * pad);
                var crop = new System.Windows.Media.Imaging.CroppedBitmap(src, new System.Windows.Int32Rect(cx, cy, cw, ch));
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
                using (var outFile = System.IO.File.Create(path + ".part")) png.Save(outFile);
                System.IO.File.Delete(path);
                System.IO.File.Move(path + ".part", path);
                HomeNav.Log("art: logo trimmed " + w + "x" + h + " -> " + cw + "x" + ch + " (" + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) + ")");
            }
            catch (Exception ex) { HomeNav.Log("art: trim logo: " + ex.Message); }
        }

        // the logos saved in the last days (Selene's, before the trim), looked at once a session
        private static readonly HashSet<Guid> trimLooked = new HashSet<Guid>();
        // Every game's logo, trimmed once: Extra Metadata and others keep a logo on a 640 x 360 canvas with the lettering in a strip of
        // it, and fitted to the logo box that left the name small (Monster Hunter Wilds). Looked at in the background after start-up;
        // a file already seen (same size and time) is skipped next time.
        public static void TrimAllLogos()
        {
            if (!Features.On("trimlogos") || HomeNav.Api == null) return;
            var folder = System.IO.Path.Combine(HomeNav.Api.Paths.ConfigurationPath, "ExtraMetadata", "games");
            if (!System.IO.Directory.Exists(folder)) return;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try
                {
                    var state = System.IO.Path.Combine(SelenePaths.DataFolder, "logos-checked-2.txt");
                    var seen = new HashSet<string>();
                    if (System.IO.File.Exists(state)) foreach (var line in System.IO.File.ReadAllLines(state)) seen.Add(line);
                    var added = new List<string>();
                    foreach (var dir in System.IO.Directory.GetDirectories(folder))
                    {
                        var logo = System.IO.Path.Combine(dir, "logo.png");
                        if (!System.IO.File.Exists(logo)) continue;
                        var name = System.IO.Path.GetFileName(dir);
                        var info = new System.IO.FileInfo(logo);
                        if (seen.Contains(name + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks)) continue;
                        TrimLogo(logo);
                        info = new System.IO.FileInfo(logo);
                        added.Add(name + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
                        System.Threading.Thread.Sleep(5);   // (a long library isn't read in one go)
                    }
                    if (added.Count > 0) { System.IO.Directory.CreateDirectory(SelenePaths.DataFolder); System.IO.File.AppendAllLines(state, added); }
                    HomeNav.Log("logos: " + added.Count + " looked at");
                }
                catch (Exception ex) { HomeNav.Log("logos: " + ex.Message); }
            });
        }

        private static void TrimLogoOnce(Guid id, string logo)
        {
            lock (gate) { if (!trimLooked.Add(id)) return; }
            try { if ((DateTime.Now - System.IO.File.GetLastWriteTime(logo)).TotalDays < 3) TrimLogo(logo); } catch { }
        }

        // a picture to a file (through .part, so a cut download never shows); false when there is none (or it isn't one)
        private static bool Save(string from, string to, bool png)
        {
            byte[] data;
            try { using (var web = Client()) data = web.DownloadData(from); }
            catch (WebException ex)
            {
                // only "not there" means there is none; anything else (too many requests, no connection) is tried another time
                var r = ex.Response as HttpWebResponse;
                if (r != null && (r.StatusCode == HttpStatusCode.NotFound || r.StatusCode == HttpStatusCode.Forbidden)) return false;
                throw;
            }
            bool ok = png ? data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 : data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;
            if (!ok) return false;
            PerformanceRecorder.Mark("download", "art bytes=" + data.Length + " file=" + System.IO.Path.GetFileName(to));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
            var part = to + ".part";
            System.IO.File.WriteAllBytes(part, data);
            if (System.IO.File.Exists(to)) { System.IO.File.Delete(part); return true; }
            System.IO.File.Move(part, to);
            return true;
        }

        // the store's highlighted trailer (else its first), as H.264 DASH
        private static string ManifestUrl(string appId)
        {
            var json = Text("https://store.steampowered.com/api/appdetails?filters=movies&appids=" + appId);
            var root = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            object app, data, movies;
            if (root == null || !root.TryGetValue(appId, out app) || !(app is Dictionary<string, object>)) return null;
            if (!((Dictionary<string, object>)app).TryGetValue("data", out data) || !(data is Dictionary<string, object>)) return null;
            if (!((Dictionary<string, object>)data).TryGetValue("movies", out movies) || !(movies is object[])) return null;
            var list = ((object[])movies).OfType<Dictionary<string, object>>().ToList();
            var movie = list.FirstOrDefault(m => m.ContainsKey("highlight") && true.Equals(m["highlight"])) ?? list.FirstOrDefault();
            object url;
            return movie != null && movie.TryGetValue("dash_h264", out url) ? url as string : null;
        }

        // the video (720p, or the nearest below) and the audio, their pieces for the first minute, joined into one file
        private static void Download(string mpdUrl, string target)
        {
            var mpd = XDocument.Parse(Text(mpdUrl));
            XNamespace ns = mpd.Root.Name.Namespace;
            double length = Seconds((string)mpd.Root.Attribute("mediaPresentationDuration"));
            var sets = mpd.Descendants(ns + "AdaptationSet").ToList();
            var videoSet = sets.FirstOrDefault(s => (string)s.Attribute("contentType") == "video");
            var audioSet = sets.FirstOrDefault(s => (string)s.Attribute("contentType") == "audio");
            if (videoSet == null) throw new Exception("no video in the trailer");
            var videos = videoSet.Elements(ns + "Representation").OrderByDescending(r => (int?)r.Attribute("height") ?? 0).ToList();
            var video = videos.FirstOrDefault(r => ((int?)r.Attribute("height") ?? 0) <= MaxHeight) ?? videos.Last();
            var audio = audioSet != null ? audioSet.Elements(ns + "Representation").FirstOrDefault() : null;

            var baseUrl = mpdUrl.Substring(0, mpdUrl.LastIndexOf('/') + 1);
            byte[] vInit; List<byte[]> vChunks;
            Pieces(baseUrl, ns, video, length, out vInit, out vChunks);
            byte[] aInit = null; List<byte[]> aChunks = null;
            if (audio != null) Pieces(baseUrl, ns, audio, length, out aInit, out aChunks);
            if (vChunks.Count == 0) throw new Exception("no video pieces");
            Mp4Join.Write(target, vInit, vChunks, aInit, aChunks);
        }

        private static void Pieces(string baseUrl, XNamespace ns, XElement rep, double length, out byte[] init, out List<byte[]> chunks)
        {
            var tpl = rep.Element(ns + "SegmentTemplate") ?? rep.Parent.Element(ns + "SegmentTemplate");
            if (tpl == null) throw new Exception("no segment template");
            var repId = (string)rep.Attribute("id");
            double scale = (double?)tpl.Attribute("timescale") ?? 1, each = ((double?)tpl.Attribute("duration") ?? 0) / scale;
            int first = (int?)tpl.Attribute("startNumber") ?? 1;
            int count = each > 0 ? (int)Math.Ceiling(Math.Min(length > 0 ? length : MaxSeconds, MaxSeconds) / each) : 0;
            using (var web = Client())
            {
                init = web.DownloadData(baseUrl + Fill((string)tpl.Attribute("initialization"), repId, 0));
                chunks = new List<byte[]>();
                for (int n = first; n < first + count; n++)
                {
                    try { chunks.Add(web.DownloadData(baseUrl + Fill((string)tpl.Attribute("media"), repId, n))); }
                    catch (WebException) { break; }   // past the end
                }
            }
        }

        // $RepresentationID$, $Number$ / $Number%05d$
        private static string Fill(string tpl, string repId, int number)
        {
            var s = tpl.Replace("$RepresentationID$", repId);
            return Regex.Replace(s, @"\$Number(%0(\d+)d)?\$", m => m.Groups[2].Success ? number.ToString().PadLeft(int.Parse(m.Groups[2].Value), '0') : number.ToString());
        }

        // ISO 8601 "PT1M32.6S"
        private static double Seconds(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return 0;
            try { return System.Xml.XmlConvert.ToTimeSpan(iso).TotalSeconds; } catch { return 0; }
        }

        private static bool asked;   // the last game asked Steam something (the library pass rests only after those)

        private static WebClient Client()
        {
            if (!DownloadConsent.Allowed(permission)) throw new OperationCanceledException("Media downloads are off or were not confirmed.");
            asked = true;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }   // (as Selene's other downloads)
            var wc = new WebClient { Encoding = System.Text.Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
            return wc;
        }

        private static string Text(string url) { using (var wc = Client()) return wc.DownloadString(url); }

        private static Game AsGame(object o)
        {
            var g = o as Game;
            if (g != null || o == null) return g;
            var prop = o.GetType().GetProperty("Game");
            return prop != null ? prop.GetValue(o, null) as Game : null;
        }
    }
}
