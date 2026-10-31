using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Media.Imaging;
using Playnite.SDK.Models;

namespace Selene
{
    // Square tile art from SteamGridDB (Steam has none: its covers are tall and were cropped, cutting logos). Saved as the
    // media folder's Square\<id>.jpg, which Spotlight's tiles already show over the cover. Needs the user's own free API key:
    // pasted in Selene settings (Look > SteamGridDB key) or the add-on page, else the one the SteamGridDB metadata add-on
    // keeps. Runs with the art downloads (their permission), for games without a square picture of their own.
    // Switch: Look > Square art from SteamGridDB.
    public static class SquareFetch
    {
        private const string KeyName = "steamgriddb-key";
        private const string Api = "https://www.steamgriddb.com/api/v2/";
        private const string MetadataAddon = "f9a763e1-1ccb-4d7d-b955-d59e708f71c1";   // SteamGridDB Metadata Provider

        // the key Selene was given, else the metadata add-on's
        public static string Key
        {
            get
            {
                var own = Features.Remembered(KeyName);
                if (!string.IsNullOrWhiteSpace(own)) return own.Trim();
                return AddonKey();
            }
        }

        private static string AddonKey()
        {
            try
            {
                if (HomeNav.Api == null) return null;
                var file = Path.Combine(HomeNav.Api.Paths.ExtensionsDataPath, MetadataAddon, "config.json");
                if (!File.Exists(file)) return null;
                var root = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
                object key;
                return root != null && root.TryGetValue("ApiKey", out key) && key is string && Valid((string)key) ? ((string)key).Trim() : null;
            }
            catch { return null; }
        }

        public static bool Valid(string key) { return key != null && System.Text.RegularExpressions.Regex.IsMatch(key.Trim(), "^[0-9a-fA-F]{32}$"); }

        public static string Status()
        {
            if (!string.IsNullOrWhiteSpace(Features.Remembered(KeyName))) return "Key added";
            if (AddonKey() != null) return "Using the SteamGridDB add-on's key";
            return "No key";
        }

        public static void SetKey(string key) { Features.Remember(KeyName, key == null ? "" : key.Trim()); }

        // a key SteamGridDB accepts (asked off the UI thread)
        public static bool Works(string key)
        {
            try { Ask(Api + "games/steam/730", key.Trim()); return true; }
            catch { return false; }
        }

        // the key from the clipboard (copied from steamgriddb.com on this PC or sent from a phone: no typing with a pad),
        // checked with SteamGridDB before it's kept; done(true) once added
        public static void PasteKey(Action<bool> done)
        {
            string key = null;
            try { key = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null; } catch { }
            if (!Valid(key)) { Ui.Notice("No key copied", 4000); done(false); return; }
            System.Threading.Tasks.Task.Run(() => Works(key)).ContinueWith(t =>
            {
                if (!t.Result) { Ui.Notice("Key not accepted", 4000); done(false); return; }
                SetKey(key);
                Ui.Play("Success.wav", 0.8);
                Ui.Notice("Key added", 4000);
                done(true);
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static string SquareFile(Guid id) { return Path.Combine(SelenePaths.Media("Square"), id + ".jpg"); }

        // from the art download worker: a square picture for a game that has none
        public static void Fetch(Game g, Func<string> steamId)
        {
            if (!Features.On("squareart") || !DownloadConsent.Allowed("artdownload")) return;
            var key = Key;
            if (key == null) return;
            var target = SquareFile(g.Id);
            if (System.IO.File.Exists(target)) return;
            var none = Path.Combine(SelenePaths.Media("Trailers"), g.Id + ".nosquare");
            if (System.IO.File.Exists(none) && (DateTime.Now - System.IO.File.GetLastWriteTime(none)).TotalDays < 7) return;

            var id = steamId();
            string grids = null;
            if (!string.IsNullOrEmpty(id)) grids = Api + "grids/steam/" + id;
            else
            {
                var found = Json(Api + "search/autocomplete/" + Uri.EscapeDataString(g.Name), key);
                var first = found != null ? (found["data"] as object[] ?? new object[0]).OfType<Dictionary<string, object>>().FirstOrDefault() : null;
                if (first != null) grids = Api + "grids/game/" + first["id"];
            }
            string url = null;
            if (grids != null)
            {
                try
                {
                    var list = Json(grids + "?dimensions=1024x1024,512x512&types=static&nsfw=false&humor=false&epilepsy=false", key);
                    var pick = list != null ? (list["data"] as object[] ?? new object[0]).OfType<Dictionary<string, object>>().FirstOrDefault() : null;
                    if (pick != null) url = pick["url"] as string;
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r == null || r.StatusCode != HttpStatusCode.NotFound) throw;   // (not on SteamGridDB: none; anything else another time)
                }
            }
            if (url == null) { System.IO.File.WriteAllText(none, g.Name); HomeNav.Log("square art: " + g.Name + ": none on SteamGridDB"); return; }

            byte[] data;
            using (var web = Client(null)) data = web.DownloadData(url);
            Save(data, target);
            HomeNav.Log("square art: " + g.Name);
            var game = g.Id;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if ("".Equals(Application.Current.Resources["SeleneSquareRoot"])) SeleneShell.ApplyArtRoots();
                TileCovers.SquareArrived();
            }));
        }

        // as JPEG at most 1024 px (SteamGridDB's are PNG, often several MB)
        private static void Save(byte[] data, string target)
        {
            BitmapSource src;
            using (var mem = new MemoryStream(data))
                src = BitmapFrame.Create(mem, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (src.PixelWidth > 1024)
            {
                double f = 1024.0 / src.PixelWidth;
                src = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(f, f));
            }
            if (src.Format != System.Windows.Media.PixelFormats.Bgr24) src = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgr24, null, 0);   // (JPEG has no see-through)
            var jpg = new JpegBitmapEncoder { QualityLevel = 92 };
            jpg.Frames.Add(BitmapFrame.Create(src));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var outFile = System.IO.File.Create(target + ".part")) jpg.Save(outFile);
            if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
            System.IO.File.Move(target + ".part", target);
        }

        private static WebClient Client(string key)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var wc = new WebClient { Encoding = System.Text.Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "Selene";
            if (key != null) wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
            return wc;
        }

        private static string Ask(string url, string key) { using (var wc = Client(key)) return wc.DownloadString(url); }

        private static Dictionary<string, object> Json(string url, string key)
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(Ask(url, key)) as Dictionary<string, object>;
        }
    }
}
