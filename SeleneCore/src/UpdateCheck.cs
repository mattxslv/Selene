using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace Selene
{
    // Once a day, a moment after start: is there a newer Selene on GitHub than the one installed? If so, a notification says
    // so and where to get it (GitHub: Playnite's add-on updates don't list Selene yet). Each version is told about once. Nothing is
    // downloaded or installed here. Switch: "updatecheck". Developer switch: %TEMP%\selene-dev-update pretends 9.9.9 is out.
    public static class UpdateCheck
    {
        private const string Latest = "https://api.github.com/repos/mattxslv/Selene/releases/latest";
        private static string StateFile { get { return Path.Combine(SelenePaths.DataFolder, "update-check.txt"); } }   // "<checked at ticks> <last version told>"

        public static void Later()
        {
            if (!Features.On("updatecheck")) return;
            var dev = Path.Combine(Path.GetTempPath(), "selene-dev-update");
            bool pretend = File.Exists(dev);
            if (pretend) try { File.Delete(dev); } catch { }
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(pretend ? 6 : 30) };
            t.Tick += (s, e) => { t.Stop(); Check(pretend); };
            t.Start();
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
            var ui = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                string newest = null;
                try
                {
                    if (pretend) newest = "9.9.9";
                    else
                    {
                        try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
                        using (var web = new System.Net.WebClient())
                        {
                            web.Headers[System.Net.HttpRequestHeader.UserAgent] = "Selene-Playnite";
                            web.Headers[System.Net.HttpRequestHeader.Accept] = "application/vnd.github+json";
                            var json = web.DownloadString(Latest);
                            var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9][^\"]*)\"");
                            if (m.Success) newest = m.Groups[1].Value;
                        }
                    }
                }
                catch (Exception ex) { HomeNav.Log("update check: " + ex.Message); }
                ui.BeginInvoke(new Action(() => Done(newest, told, pretend)));
            });
        }

        private static void Done(string newest, string told, bool pretend)
        {
            try { if (!pretend) File.WriteAllText(StateFile, DateTime.UtcNow.Ticks + " " + (newest != null && IsNewer(newest) ? newest : told)); } catch { }
            if (newest == null) return;
            HomeNav.Log("update check: newest " + newest + ", this is " + Brand.Version);
            if (!IsNewer(newest) || (!pretend && newest == told)) return;
            ProgressToast.ShowNote("Selene " + newest + " is available", "Get it at github.com/mattxslv/Selene", 0xE923 /* update */);
        }

        private static bool IsNewer(string newest)
        {
            Version a, b;
            return Version.TryParse(newest.Split('-')[0], out a) && Version.TryParse((Brand.Version ?? "0").Split('-')[0], out b) && a > b;
        }
    }
}
