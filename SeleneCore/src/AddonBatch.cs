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
        public static void Run(List<AddonInfo> addons, Action<int, int, string> progress, Action<string, bool> done, Action<int, int> finished)
        {
            var ui = Application.Current.Dispatcher;
            var sdk = typeof(Playnite.SDK.IPlayniteAPI).Assembly.GetName().Version;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                int queued = 0, failed = 0;
                for (int i = 0; i < addons.Count; i++)
                {
                    var a = addons[i];
                    int at = i;
                    ui.Invoke(new Action(() => progress(at, addons.Count, a.Id)));
                    bool ok = false;
                    try
                    {
                        var file = Download(a.Id, sdk);
                        ok = (bool)ui.Invoke(new Func<bool>(() => Queue(file)));
                        if (ok) HomeNav.Log("install all: " + a.Id + " queued (" + file + ")");
                    }
                    catch (Exception ex) { HomeNav.Log("install all: " + a.Id + " failed: " + ex.Message); }
                    if (ok) queued++; else failed++;
                    bool result = ok;
                    ui.Invoke(new Action(() => done(a.Id, result)));
                }
                ui.Invoke(new Action(() => finished(queued, failed)));
            });
        }

        // the newest package for this Playnite: same SDK major version, not newer than it (Playnite's own rule)
        private static string Download(string addonId, Version sdk)
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
            string url = null;
            Version best = null;
            foreach (Dictionary<string, object> p in packages)
            {
                Version v, req;
                if (!Version.TryParse(Convert.ToString(p["version"]), out v)) continue;
                if (!p.ContainsKey("requiredApiVersion") || !Version.TryParse(Convert.ToString(p["requiredApiVersion"]), out req)) continue;
                if (req.Major != sdk.Major || req > sdk) continue;
                if (best == null || v > best) { best = v; url = Convert.ToString(p["packageUrl"]); }
            }
            if (url == null) throw new Exception("no package fits this Playnite (SDK " + sdk + ")");
            var folder = Path.Combine(Path.GetTempPath(), "Playnite");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, addonId + ".pext");
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "Selene";
                web.DownloadFile(url, file);
            }
            return file;
        }

        // Playnite's own check and queue (Playnite.Plugins.ExtensionInstaller); the queue file itself if they can't be reached
        private static bool Queue(string file)
        {
            var installer = PlayniteType("Playnite.Plugins.ExtensionInstaller");
            if (installer != null)
            {
                var verify = installer.GetMethod("VerifyExtensionPackage", BindingFlags.Public | BindingFlags.Static);
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
