// Install all, in one go: Playnite's own install link asks once per add-on (a question, a download, "Restart now?" each
// time). This does what Playnite does after each Yes, for every add-on at once: asks Playnite's add-on database for the
// newest package that fits this Playnite, downloads it, checks it with Playnite's own check, and puts it in Playnite's own
// install queue (ExtensionInstaller.QueuePackageInstall). Playnite installs the whole queue on its next start, so one
// restart finishes everything.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;

namespace Selene
{
    public static class AddonBatch
    {
        private const string Api = "https://api.playnite.link/api/addons/installer?addonId=";

        // downloads and queues each add-on on a background thread; progress(i, count, id) before each one, done(id, ok) after
        // it, finished(queued, failed) at the end (all on the UI thread)
        public static void Run(List<AddonInfo> addons, Action<int, int, string> progress, Action<string, bool> done, Action<int, int> finished) { Run(addons, progress, done, finished, null); }

        // share(0..1): how far the whole batch is, by the bytes of the add-on being downloaded (some are large and took a while)
        public static void Run(List<AddonInfo> addons, Action<int, int, string> progress, Action<string, bool> done, Action<int, int> finished, Action<double> share)
        {
            var ui = Application.Current.Dispatcher;
            var sdk = typeof(Playnite.SDK.IPlayniteAPI).Assembly.GetName().Version;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                var mirror = Mirror();
                int queued = 0, failed = 0;
                for (int i = 0; i < addons.Count; i++)
                {
                    var a = addons[i];
                    int at = i;
                    ui.Invoke(new Action(() => progress(at, addons.Count, a.Id)));
                    bool ok = false;
                    try
                    {
                        DateTime told = DateTime.MinValue;
                        var file = Download(a.Id, sdk, mirror, part =>
                        {
                            if (share == null || (DateTime.Now - told).TotalMilliseconds < 100) return;   // (a few updates a second)
                            told = DateTime.Now;
                            double whole = (at + part) / addons.Count;
                            ui.BeginInvoke(new Action(() => share(whole)));
                        });
                        ok = (bool)ui.Invoke(new Func<bool>(() => Queue(file)));
                        if (ok) HomeNav.Log("install all: " + a.Id + " queued (" + file + ")");
                    }
                    catch (Exception ex) { HomeNav.Log("install all: " + a.Id + " failed: " + ex.Message); }
                    if (ok) queued++; else failed++;
                    bool result = ok;
                    ui.Invoke(new Action(() => { done(a.Id, result); if (share != null) share((double)(at + 1) / addons.Count); }));
                }
                ui.Invoke(new Action(() => finished(queued, failed)));
            });
        }

        // the newest package for this Playnite: same SDK major version, not newer than it (Playnite's own rule)
        // Playnite's own add-ons (the store libraries) come from playnite.link at ~14 KB/s: Steam library took minutes. Their
        // author publishes the same packages on GitHub, a few versions behind, at full speed. Name -> address, asked once a batch.
        private static Dictionary<string, string> Mirror()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "Selene";
                    web.Encoding = System.Text.Encoding.UTF8;
                    var release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(web.DownloadString("https://api.github.com/repos/JosefNemec/PlayniteExtensions/releases/latest"));
                    var assets = release.ContainsKey("assets") ? release["assets"] as System.Collections.ArrayList : null;
                    if (assets != null)
                        foreach (Dictionary<string, object> a in assets)
                            names[Convert.ToString(a["name"])] = Convert.ToString(a["browser_download_url"]);
                }
            }
            catch (Exception ex) { HomeNav.Log("install all: GitHub mirror: " + ex.Message); }
            return names;
        }

        private static string Download(string addonId, Version sdk, Dictionary<string, string> mirror, Action<double> part)
        {
            string json;
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "Selene";
                web.Encoding = System.Text.Encoding.UTF8;
                json = web.DownloadString(Api + Uri.EscapeDataString(addonId));
            }
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var data = root.ContainsKey("data") ? root["data"] as Dictionary<string, object> : root;
            var packages = data != null && data.ContainsKey("packages") ? data["packages"] as System.Collections.ArrayList : null;
            if (packages == null || packages.Count == 0) throw new Exception("no packages in the add-on database");
            string url = null, fast = null;
            Version best = null, bestFast = null;
            foreach (Dictionary<string, object> p in packages)
            {
                Version v, req;
                if (!Version.TryParse(Convert.ToString(p["version"]), out v)) continue;
                if (!p.ContainsKey("requiredApiVersion") || !Version.TryParse(Convert.ToString(p["requiredApiVersion"]), out req)) continue;
                if (req.Major != sdk.Major || req > sdk) continue;
                var address = Convert.ToString(p["packageUrl"]);
                if (best == null || v > best) { best = v; url = address; }
                string copy;
                if (mirror.TryGetValue(Path.GetFileName(address), out copy) && (bestFast == null || v > bestFast)) { bestFast = v; fast = copy; }
            }
            if (url == null) throw new Exception("no package fits this Playnite (SDK " + sdk + ")");
            // the slow server only: the GitHub copy instead (an older one is updated by Playnite later)
            if (fast != null && url.IndexOf("playnite.link", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HomeNav.Log("install all: " + addonId + " " + bestFast + " from GitHub" + (bestFast < best ? " (" + best + " is on playnite.link only)" : ""));
                url = fast;
            }
            var folder = Path.Combine(Path.GetTempPath(), "Playnite");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, addonId + ".pext");
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Selene";
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = File.Create(file))
            {
                long total = response.ContentLength, got = 0;
                var buffer = new byte[81920];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, n);
                    got += n;
                    if (total > 0) part((double)got / total);
                }
            }
            return file;
        }

        // a package (add-on .pext or theme .pthm) to Playnite's install queue, as opening it would (Selene's own update)
        public static bool QueuePackage(string file)
        {
            try { return Queue(file); }
            catch (Exception ex) { HomeNav.Log("update: queue failed: " + (ex.InnerException ?? ex).Message); return false; }
        }

        // Playnite's own check and queue (Playnite.Plugins.ExtensionInstaller); the queue file itself if they can't be reached
        private static bool Queue(string file)
        {
            var installer = PlayniteType("Playnite.Plugins.ExtensionInstaller");
            if (installer != null)
            {
                var verify = installer.GetMethod(file.EndsWith(".pthm", StringComparison.OrdinalIgnoreCase) ? "VerifyThemePackage" : "VerifyExtensionPackage", BindingFlags.Public | BindingFlags.Static);
                var queue = installer.GetMethod("QueuePackageInstall", BindingFlags.Public | BindingFlags.Static);
                if (queue != null)
                {
                    try { if (verify != null) verify.Invoke(null, new object[] { file }); }
                    catch (TargetInvocationException ex) { HomeNav.Log("install all: package check failed: " + (ex.InnerException ?? ex).Message); return false; }
                    queue.Invoke(null, new object[] { file });
                    return true;
                }
            }
            // fallback: add to Playnite's queue file (the same list Playnite writes after each Yes)
            var path = Path.Combine(SelenePaths.PlayniteConfig, "extinstalls.json");
            var js = new JavaScriptSerializer();
            var list = File.Exists(path) ? js.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(path)) ?? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>>();
            if (!list.Any(i => string.Equals(Convert.ToString(i["Path"]), file, StringComparison.OrdinalIgnoreCase)))
                list.Add(new Dictionary<string, object> { { "InstallType", 0 }, { "Path", file } });
            File.WriteAllText(path, js.Serialize(list));
            return true;
        }

        private static Type PlayniteType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "Playnite") return asm.GetType(name);
            return null;
        }

        // restarts Playnite (its own restart, as after an install); false when it can't be reached
        public static bool RestartPlaynite()
        {
            try
            {
                var appType = PlayniteType("Playnite.PlayniteApplication");
                var current = appType != null ? appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static) : null;
                var app = current != null ? current.GetValue(null, null) : null;
                var restart = app != null ? app.GetType().GetMethod("Restart", new[] { typeof(bool) }) : null;
                if (restart == null) return false;
                HomeNav.Log("install all: restarting Playnite");
                restart.Invoke(app, new object[] { true });
                return true;
            }
            catch (Exception ex) { HomeNav.Log("install all: restart failed: " + ex.Message); return false; }
        }
    }
}
