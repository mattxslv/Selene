using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace Selene
{
    // Once a day, a moment after start: is there a newer Selene on GitHub than the one installed? If so, a notification says
    // so, the menu gets an Update item, and the Update screen installs it with one press (Playnite's add-on updates don't list
    // Selene). Each version's notification comes once. Switch: "updatecheck". Selene settings > Advanced > Check for updates
    // looks at once. Developer switch: %TEMP%\selene-dev-update shows the newest release as if it were new, and the Update
    // screen does everything but hand the files to Playnite (a preview).
    public static class UpdateCheck
    {
        private const string Latest = "https://api.github.com/repos/mattxslv/Selene/releases/latest";
        private static string StateFile { get { return Path.Combine(SelenePaths.DataFolder, "update-check.txt"); } }   // "<checked at ticks> <last version told>"

        // the newer version found (null: none), its release notes, and whether it's the developer preview
        public static string Available { get; private set; }
        public static string Notes { get; private set; }
        public static bool Preview { get; private set; }

        public static void Later()
        {
            if (!Features.On("updatecheck")) return;
            var dev = Path.Combine(Path.GetTempPath(), "selene-dev-update");
            bool pretend = File.Exists(dev);
            if (pretend) try { File.Delete(dev); } catch { }
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };   // (early: its card goes before the library update's)
            t.Tick += (s, e) => { t.Stop(); Check(pretend); };
            t.Start();
        }

        // the newest release on GitHub: its version and notes (null when it can't be reached); off the UI thread
        private static void Fetch(Action<string, string> done)
        {
            var ui = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                string newest = null, notes = null;
                try
                {
                    try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
                    using (var web = new System.Net.WebClient { Encoding = System.Text.Encoding.UTF8 })
                    {
                        web.Headers[System.Net.HttpRequestHeader.UserAgent] = "Selene-Playnite";
                        web.Headers[System.Net.HttpRequestHeader.Accept] = "application/vnd.github+json";
                        var release = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(web.DownloadString(Latest)) as Dictionary<string, object>;
                        object tag, body;
                        if (release != null && release.TryGetValue("tag_name", out tag)) newest = Convert.ToString(tag).TrimStart('v');
                        if (release != null && release.TryGetValue("body", out body)) notes = Convert.ToString(body);
                    }
                }
                catch (Exception ex) { HomeNav.Log("update check: " + ex.Message); }
                ui.BeginInvoke(new Action(() => done(newest, notes)));
            });
        }

        private static void Check(bool pretend)
        {
            long checkedAt = 0; string told = "";
            try
            {
                var parts = File.Exists(StateFile) ? File.ReadAllText(StateFile).Trim().Split(' ') : new string[0];
                if (parts.Length > 0) long.TryParse(parts[0], out checkedAt);
                if (parts.Length > 1) told = parts[1];
            }
            catch { }
            if (!pretend && DateTime.UtcNow - new DateTime(checkedAt, DateTimeKind.Utc) < TimeSpan.FromHours(20)) return;
            Fetch((newest, notes) => Done(newest, notes, told, pretend));
        }

        private static void Done(string newest, string notes, string told, bool pretend)
        {
            try { if (!pretend) File.WriteAllText(StateFile, DateTime.UtcNow.Ticks + " " + (newest != null && IsNewer(newest) ? newest : told)); } catch { }
            if (newest == null) return;
            HomeNav.Log("update check: newest " + newest + ", this is " + Brand.Version + (pretend ? " (preview)" : ""));
            if (!pretend && !IsNewer(newest)) return;
            Available = newest; Notes = notes; Preview = pretend;
            if (!pretend && newest == told) return;   // (told once; the menu's Update item stays)
            ProgressToast.ShowNote("Selene " + newest, "Update available", 0xE923 /* update */, true);   // (before any task's card; the menu's Update item carries the dot)
        }

        // Selene settings > Check for updates: looks now; the Update screen when there's one, else a note
        public static void CheckNow(Func<Screen> back)
        {
            Ui.Notice("Checking for updates…", 3000);
            Fetch((newest, notes) =>
            {
                if (newest == null) { Ui.Notice("Couldn't check for updates. Try again later.", 5000); return; }
                if (!IsNewer(newest)) { Ui.Notice("Selene is up to date.", 5000); return; }
                Available = newest; Notes = notes; Preview = false;
                LoginScene.Show(new UpdateScreen(back), 1);
            });
        }

        private static bool IsNewer(string newest)
        {
            Version a, b;
            return Version.TryParse(newest.Split('-')[0], out a) && Version.TryParse((Brand.Version ?? "0").Split('-')[0], out b) && a > b;
        }
    }
}
