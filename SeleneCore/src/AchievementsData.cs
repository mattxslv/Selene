using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Selene
{
    // Achievements from the Playnite Achievements add-on, read through its own data service (no files of its own touched).
    // Read off the UI thread; the screens get plain lists.
    public static class AchievementsData
    {
        public class Item
        {
            public string Name, Description, Icon, Tier, Trophy;
            public bool Unlocked, Hidden;
            public DateTime? When;          // local time it was unlocked
            public double? Rarity;          // % of players who have it
            public int? ProgressNum, ProgressDenom;
            public int Order;
            // bronze / silver / gold, PS5-style: the game's own grade (PlayStation), or from how rare it is
            public string Grade
            {
                get
                {
                    var t = (Trophy ?? "").Trim().ToLowerInvariant();
                    if (t == "bronze" || t == "silver" || t == "gold" || t == "platinum") return char.ToUpper(t[0]) + t.Substring(1);
                    if (Rarity.HasValue) return Rarity.Value < 5 ? "Gold" : Rarity.Value < 25 ? "Silver" : "Bronze";
                    return Tier == "Ultra rare" ? "Gold" : Tier == "Rare" || Tier == "Uncommon" ? "Silver" : "Bronze";
                }
            }
        }

        // a trophy of a grade, its metal shading down from light to its colour (as a console's trophies)
        public static System.Windows.Controls.TextBlock TrophyIcon(string grade, double size)
        {
            if (grade == "Platinum")
            {
                // platinum: white with an icy blue and a hint of lilac across it, brighter and cooler than silver (as a console's)
                var shine = new System.Windows.Media.LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1) };
                shine.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF), 0));
                shine.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD8, 0xEA, 0xFF), 0.35));
                shine.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xC4, 0xB8, 0xF4), 0.62));
                shine.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x8C, 0xA8, 0xE0), 1));
                shine.Freeze();
                return new System.Windows.Controls.TextBlock { Text = Ui.Icon(0xea23), FontFamily = Ui.Icons, FontSize = size, Foreground = shine, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            }
            var c = ((System.Windows.Media.SolidColorBrush)GradeBrush(grade)).Color;
            var light = System.Windows.Media.Color.FromRgb((byte)Math.Min(255, c.R + 55), (byte)Math.Min(255, c.G + 55), (byte)Math.Min(255, c.B + 55));
            var dark = System.Windows.Media.Color.FromRgb((byte)(c.R * 0.72), (byte)(c.G * 0.72), (byte)(c.B * 0.72));
            var metal = new System.Windows.Media.LinearGradientBrush(new System.Windows.Media.GradientStopCollection { new System.Windows.Media.GradientStop(light, 0), new System.Windows.Media.GradientStop(c, 0.5), new System.Windows.Media.GradientStop(dark, 1) }, 90);
            metal.Freeze();
            return new System.Windows.Controls.TextBlock { Text = Ui.Icon(0xea23), FontFamily = Ui.Icons, FontSize = size, Foreground = metal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        }

        // the metals' colours (a trophy's grade is the same in every theme)
        public static System.Windows.Media.Brush GradeBrush(string grade)
        {
            System.Windows.Media.Color c;
            switch (grade)
            {
                case "Platinum": c = System.Windows.Media.Color.FromRgb(0xB9, 0xC8, 0xF6); break;   // (icy blue-lilac: apart from silver)
                case "Gold": c = System.Windows.Media.Color.FromRgb(0xE8, 0xC4, 0x6A); break;
                case "Silver": c = System.Windows.Media.Color.FromRgb(0xB8, 0xBB, 0xC0); break;   // (a neutral grey)
                default: c = System.Windows.Media.Color.FromRgb(0xCD, 0x8E, 0x5E); break;
            }
            var b = new System.Windows.Media.SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public class GameSet
        {
            public Guid GameId;
            public string Name;
            public int Unlocked, Total;
            public bool Completed;
            public List<Item> Items = new List<Item>();
            public DateTime? LastUnlock { get { var w = Items.Where(i => i.Unlocked && i.When.HasValue).Select(i => i.When.Value).ToList(); return w.Count > 0 ? w.Max() : (DateTime?)null; } }
            public int Percent { get { return Total > 0 ? (int)Math.Floor(100.0 * Unlocked / Total) : 0; } }
            public bool Platinum { get { return Total > 0 && Unlocked == Total; } }   // every one earned, as a console's platinum
            public int Earned(string grade) { return Items.Count(i => i.Unlocked && i.Grade == grade); }
            public int Count(string grade) { return Items.Count(i => i.Grade == grade); }
        }

        private static object plugin;
        private static bool looked;
        private static string dataFolder;

        private static object Plugin()
        {
            if (looked) return plugin;
            looked = true;
            try
            {
                var addon = HomeNav.Api != null ? HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.GetType().FullName == "PlayniteAchievements.PlayniteAchievementsPlugin") : null;
                plugin = addon;
                if (addon != null) dataFolder = ((Playnite.SDK.Plugins.Plugin)addon).GetPluginUserDataPath();
                HomeNav.Log("achievements: " + (addon != null ? "Playnite Achievements found" : "Playnite Achievements not installed"));
            }
            catch (Exception ex) { HomeNav.Log("achievements: " + ex.Message); }
            return plugin;
        }

        public static bool Available() { return Plugin() != null; }

        private static object Service()
        {
            var p = Plugin();
            var prop = p != null ? p.GetType().GetProperty("AchievementDataService") : null;
            return prop != null ? prop.GetValue(p, null) : null;
        }

        // what was read last: the screens open on it at once (reading the add-on's database took a moment each time, a dark
        // screen before the list); a fresh read follows in the background when it's over a minute old
        private static List<GameSet> cache;
        public static bool HasCache { get { return cache != null; } }
        // one game from what was read last (null when not read yet, or nothing for it)
        public static GameSet Cached(Guid id) { return cache != null ? cache.FirstOrDefault(g => g.GameId == id) : null; }
        private static DateTime cachedAt;
        private static bool reading;

        // one game, its hidden ones as the add-on shows them; then on the UI thread
        public static void ForGame(Guid id, Action<GameSet> done)
        {
            if (cache != null)
            {
                var known = cache.FirstOrDefault(g => g.GameId == id);
                if (known != null) { done(known); return; }
            }
            var dispatcher = Dispatcher.CurrentDispatcher;
            Task.Factory.StartNew(() =>
            {
                GameSet set = null;
                try
                {
                    var svc = Service();
                    var m = svc != null ? svc.GetType().GetMethod("GetVisibleGameAchievementData", new[] { typeof(Guid) }) : null;
                    set = m != null ? ToSet(m.Invoke(svc, new object[] { id })) : null;
                }
                catch (Exception ex) { HomeNav.Log("achievements: reading a game failed: " + (ex.InnerException ?? ex).Message); }
                dispatcher.BeginInvoke(new Action(() => done(set)));
            });
        }

        // every game with achievements
        public static void All(Action<List<GameSet>> done)
        {
            if (cache != null)
            {
                done(cache);
                if ((DateTime.Now - cachedAt).TotalSeconds > 60 && !reading) Read(null);
                return;
            }
            Read(done);
        }

        // a fresh read whatever the cache's age (the add-on just checked games); nothing when one is under way
        public static void Reread(Action<List<GameSet>> done) { if (!reading) Read(done); }

        // the add-on's check under way, for the hub: games done of all (from its line "... 12/384: <game>"; its steps are
        // finer units, 10000 for the whole check), how far as a share (0-1, for a bar) and the game it's on; false when none runs
        public static bool Checking(out int done, out int total, out double share, out Guid? game)
        {
            done = total = 0; share = 0; game = null;
            try
            {
                var p = Plugin();
                var prop = p != null ? p.GetType().GetProperty("RefreshRuntime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null;
                var rt = prop != null ? prop.GetValue(p, null) : null;
                if (rt == null || !(bool)rt.GetType().GetProperty("IsRebuilding").GetValue(rt, null)) return false;
                var report = rt.GetType().GetMethod("GetLastRebuildProgress", Type.EmptyTypes).Invoke(rt, null);
                if (report != null)
                {
                    var t = report.GetType();
                    int step = (int)t.GetProperty("CurrentStep").GetValue(report, null), steps = (int)t.GetProperty("TotalSteps").GetValue(report, null);
                    share = steps > 0 ? Math.Min(1.0, (double)step / steps) : 0;
                    var m = System.Text.RegularExpressions.Regex.Match(t.GetProperty("Message").GetValue(report, null) as string ?? "", @"(\d+)\s*/\s*(\d+)");
                    if (m.Success) { done = int.Parse(m.Groups[1].Value); total = int.Parse(m.Groups[2].Value); }
                    lock (trackLock) if (tracking && trackTotal > 0 && m.Success) { done += resumeFrom; total = trackTotal; share = (double)done / total; }
                    game = t.GetProperty("CurrentGameId").GetValue(report, null) as Guid?;
                }
                return true;
            }
            catch { return false; }
        }

        private static void Read(Action<List<GameSet>> done)
        {
            reading = true;
            var dispatcher = Dispatcher.CurrentDispatcher;
            Task.Factory.StartNew(() =>
            {
                var list = new List<GameSet>();
                try
                {
                    var svc = Service();
                    var m = svc != null ? svc.GetType().GetMethod("GetAllVisibleGameAchievementDataForTheme", Type.EmptyTypes) : null;
                    var all = m != null ? m.Invoke(svc, null) as IEnumerable : null;
                    if (all != null) foreach (var g in all) { var set = ToSet(g); if (set != null && set.Total > 0) list.Add(set); }
                }
                catch (Exception ex) { HomeNav.Log("achievements: reading all failed: " + (ex.InnerException ?? ex).Message); }
                dispatcher.BeginInvoke(new Action(() =>
                {
                    cache = list;
                    cachedAt = DateTime.Now;
                    reading = false;
                    if (done != null) done(list);
                }));
            });
        }

        private static GameSet ToSet(object data)
        {
            if (data == null) return null;
            var set = new GameSet();
            var gid = Get(data, "PlayniteGameId");
            if (gid is Guid) set.GameId = (Guid)gid;
            set.Name = Get(data, "GameName") as string ?? "";
            set.Completed = Equals(Get(data, "IsCompleted"), true);
            var items = Get(data, "Achievements") as IEnumerable;
            if (items != null)
                foreach (var a in items)
                {
                    var it = new Item
                    {
                        Name = (Get(a, "DisplayName") as string) ?? (Get(a, "Name") as string) ?? "",
                        Description = Get(a, "Description") as string ?? "",
                        Unlocked = Equals(Get(a, "Unlocked"), true),
                        Hidden = Equals(Get(a, "Hidden"), true),
                        Trophy = Get(a, "TrophyType") as string,
                        Tier = TierName(Get(a, "Rarity")),
                        Order = Get(a, "DefaultOrderIndex") is int ? (int)Get(a, "DefaultOrderIndex") : 0,
                    };
                    var when = Get(a, "UnlockTimeUtc") as DateTime?;
                    if (when.HasValue) it.When = DateTime.SpecifyKind(when.Value, DateTimeKind.Utc).ToLocalTime();
                    it.ProgressNum = ToInt(Get(a, "ProgressNum"));
                    it.ProgressDenom = ToInt(Get(a, "ProgressDenom"));
                    it.Rarity = ToDouble(Get(a, "GlobalPercentUnlocked"));
                    it.Icon = IconFile(Get(a, it.Unlocked ? "UnlockedIconDisplay" : "LockedIconDisplay") as string)
                           ?? IconFile(Get(a, "IconDisplay") as string)
                           ?? IconFile(Get(a, it.Unlocked ? "UnlockedIconPath" : "LockedIconPath") as string)
                           ?? IconFile(Get(a, "UnlockedIconPath") as string);
                    set.Items.Add(it);
                }
            set.Total = set.Items.Count;
            set.Unlocked = set.Items.Count(i => i.Unlocked);
            return set;
        }

        private static string TierName(object tier)
        {
            var s = tier != null ? tier.ToString() : "";
            switch (s)
            {
                case "UltraRare": return "Ultra rare";
                case "Rare": return "Rare";
                case "Uncommon": return "Uncommon";
                case "Common": return "Common";
            }
            return "";
        }

        // an icon as a file on this PC (the add-on keeps them in its folder; paths there can be relative to it)
        private static string IconFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var full = Path.IsPathRooted(path) ? path : (dataFolder != null ? Path.Combine(dataFolder, path) : null);
                return full != null && File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        private static int? ToInt(object o) { try { return o == null ? (int?)null : System.Convert.ToInt32(o); } catch { return null; } }
        private static double? ToDouble(object o) { try { return o == null ? (double?)null : System.Convert.ToDouble(o); } catch { return null; } }

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            try { var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance); return p != null ? p.GetValue(o, null) : null; }
            catch { return null; }
        }

        // ---- words ----
        public static string RarityLine(Item i)
        {
            if (!i.Rarity.HasValue) return i.Tier;
            var pct = i.Rarity.Value < 1 ? i.Rarity.Value.ToString("0.0") : Math.Round(i.Rarity.Value).ToString("0");
            return (i.Tier.Length > 0 ? i.Tier + "  ·  " : "") + pct + "%";
        }

        // ---- the add-on set up without its desktop page ----
        // Playnite Achievements checks nothing on its own until its first-time page (desktop mode only) is gone through; from
        // the couch it never is, so only games played since showed up. Once: that page is marked done and every game it has
        // nothing for is checked in the background (the stores it's signed in to; Steam through Playnite's Steam sign-in),
        // then its own update every few hours keeps the list current. Selene's note of it: achievements-setup.txt, "done" once
        // the check ended; anything else (Playnite closed mid-check) and it's started again at the next start. After that,
        // every start checks the games played last (the add-on's "recent": its 10 latest), a few seconds, quietly
        private static string SetupFile { get { return Path.Combine(SelenePaths.DataFolder, "achievements-setup.txt"); } }

        public static void SetUpLater()
        {
            if (!Features.On("achsetup") || (!Features.On("achievements") && !Features.On("cardachievements"))) return;
            // 20 s after start, and only while Selene is in view with no game running: the add-on reads and decodes the achievements of
            // the games played last inside Playnite's own process (about 330 MB of short-lived memory and a burst of network, measured),
            // which is no help to a game that is starting or to a Playnite that was started hidden in the tray. It asks again every 2 minutes.
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            t.Tick += (s, e) =>
            {
                if (!Features.On("achsetup") || (!Features.On("achievements") && !Features.On("cardachievements"))) { t.Stop(); return; }
                if (!ScreenPresence.InView || HomeNav.GameRunning() || HomeNav.Launching) { t.Interval = TimeSpan.FromMinutes(2); return; }
                t.Stop(); SetUp();
            };
            t.Start();
        }

        // the games the long check got through, so one cut short goes on from there; in SetupFile: "started", "total <n>",
        // then a checked game's id a line
        private static readonly object trackLock = new object();
        private static readonly HashSet<Guid> checkedIds = new HashSet<Guid>();
        private static Guid? trackGame;
        private static int trackTotal, resumeFrom;   // all the games of the whole check; those done before this run
        private static bool tracking;
        private const string NL = "\r\n";

        // each of the add-on's progress reports (its threads): the game before the one it's on now is done
        private static void OnProgress(object sender, object report)
        {
            try
            {
                var t = report.GetType();
                var id = t.GetProperty("CurrentGameId").GetValue(report, null) as Guid?;
                lock (trackLock)
                {
                    if (trackTotal == 0)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(t.GetProperty("Message").GetValue(report, null) as string ?? "", @"(\d+)\s*/\s*(\d+)");
                        if (m.Success) { trackTotal = resumeFrom + int.Parse(m.Groups[2].Value); File.AppendAllText(SetupFile, "total " + trackTotal + NL); }
                    }
                    if (!id.HasValue) return;
                    if (trackGame.HasValue && trackGame.Value != id.Value && checkedIds.Add(trackGame.Value)) File.AppendAllText(SetupFile, trackGame.Value + NL);
                    trackGame = id;
                }
            }
            catch { }
        }

        // the add-on's version, from its extension.yaml ("" when not found)
        private static string AddonVersion(object plugin)
        {
            try
            {
                var yaml = Path.Combine(Path.GetDirectoryName(plugin.GetType().Assembly.Location), "extension.yaml");
                foreach (var l in File.ReadAllLines(yaml))
                    if (l.StartsWith("Version:")) return l.Substring(8).Trim();
            }
            catch { }
            return "";
        }

        // the add-on has a Steam account to read from
        private static bool SteamUserSet(object persisted)
        {
            try
            {
                var dict = persisted.GetType().GetProperty("ProviderSettings").GetValue(persisted, null) as System.Collections.IDictionary;
                var steam = dict != null && dict.Contains("Steam") ? dict["Steam"] : null;
                if (steam == null) return false;
                var prop = steam.GetType().GetProperty("SteamUserId");
                if (prop != null) return !string.IsNullOrEmpty(prop.GetValue(steam, null) as string);
                var token = steam.GetType().GetMethod("GetValue", new[] { typeof(string) }).Invoke(steam, new object[] { "SteamUserId" });
                return token != null && !string.IsNullOrEmpty(token.ToString());
            }
            catch { return false; }
        }

        // the add-on's Steam account (ProviderSettings["Steam"].SteamUserId) set to this PC's Steam user when empty; true when set
        private static bool FillSteamUser(object persisted)
        {
            try
            {
                var steamId = SelenePaths.SteamId;
                if (string.IsNullOrEmpty(steamId)) return false;
                var dict = persisted.GetType().GetProperty("ProviderSettings").GetValue(persisted, null) as System.Collections.IDictionary;
                if (dict == null || !dict.Contains("Steam")) return false;
                var steam = dict["Steam"];
                if (steam == null) return false;
                var prop = steam.GetType().GetProperty("SteamUserId");
                if (prop != null)
                {
                    if (!string.IsNullOrEmpty(prop.GetValue(steam, null) as string)) return false;
                    prop.SetValue(steam, steamId, null);
                }
                else if (steam.GetType().Name == "JObject")
                {
                    var token = steam.GetType().GetMethod("GetValue", new[] { typeof(string) }).Invoke(steam, new object[] { "SteamUserId" });
                    if (token != null && !string.IsNullOrEmpty(token.ToString())) return false;
                    var jvalue = steam.GetType().Assembly.GetType("Newtonsoft.Json.Linq.JValue");
                    steam.GetType().GetProperty("Item", new[] { typeof(string) }).SetValue(steam, Activator.CreateInstance(jvalue, (object)steamId), new object[] { "SteamUserId" });
                }
                else return false;
                HomeNav.Log("achievements: Steam account set for Playnite Achievements");
                return true;
            }
            catch (Exception ex) { HomeNav.Log("achievements: Steam account: " + (ex.InnerException ?? ex).Message); return false; }
        }

        private static void SetUp()
        {
            try
            {
                var p = Plugin();
                if (p == null) return;
                var lines = File.Exists(SetupFile) ? File.ReadAllLines(SetupFile) : new string[0];
                bool resuming = lines.Length > 0;
                // done before: only the games played last, unless the add-on was updated since (a new version can read games the
                // old one couldn't: Steam's new achievement pages came with 4.1)
                string version = AddonVersion(p);
                // the note: "done <version> steam" (steam: the full pass ran with a Steam account in place)
                string head = resuming ? lines[0].Trim() : "";
                bool recentOnly = head.StartsWith("done");
                bool steamTagged = head.EndsWith(" steam");
                string doneVersion = recentOnly ? head.Substring(4).Replace(" steam", "").Trim() : "";
                bool fullAgain = false;   // every game once more, whatever the add-on has for it (its Missing pass skips a game it looked at before)
                // (a note without a version too: checked again once)
                if (recentOnly && doneVersion != version) { HomeNav.Log("achievements: Playnite Achievements now " + version + " (was " + doneVersion + "), every game it has nothing for checked again"); recentOnly = false; lines = new string[0]; }
                lock (trackLock)
                {
                    checkedIds.Clear(); trackGame = null; trackTotal = 0;
                    if (resuming && !recentOnly)
                        foreach (var l in lines)
                        {
                            Guid id;
                            if (l.StartsWith("total ")) int.TryParse(l.Substring(6), out trackTotal);
                            else if (Guid.TryParse(l.Trim(), out id)) checkedIds.Add(id);
                        }
                    resumeFrom = checkedIds.Count;
                }
                var settings = p.GetType().GetProperty("Settings").GetValue(p, null);
                var persisted = settings.GetType().GetProperty("Persisted").GetValue(settings, null);
                // Steam: the account signed in to Steam on this PC, when the add-on has none (it reads public Steam profiles by
                // that id; left empty it never got a Steam game's achievements, which a new install never had). Then every game
                // it has nothing for is checked once more.
                if (FillSteamUser(persisted))
                {
                    typeof(Playnite.SDK.Plugins.Plugin).GetMethod("SavePluginSettings").MakeGenericMethod(settings.GetType()).Invoke(p, new[] { settings });
                    fullAgain = true;
                }
                // the account was there but the one full pass ran before it (a Missing pass after found nothing to do, every game
                // having been looked at already): the whole library once more, in the background
                else if (recentOnly && !steamTagged && SteamUserSet(persisted)) { HomeNav.Log("achievements: a Steam account is set but every game was checked before it: all of them once more"); fullAgain = true; }
                if (fullAgain) { recentOnly = false; resuming = false; lines = new string[0]; lock (trackLock) { checkedIds.Clear(); trackTotal = 0; resumeFrom = 0; } }
                var done = persisted.GetType().GetProperty("FirstTimeSetupCompleted");
                // (its theme-integration question: nothing for Selene to move over; left unanswered it held the updates too)
                var seen = persisted.GetType().GetProperty("SeenThemeMigration");
                if (!recentOnly && done != null && (!(bool)done.GetValue(persisted, null) || (seen != null && !(bool)seen.GetValue(persisted, null))))
                {
                    done.SetValue(persisted, true, null);
                    if (seen != null) seen.SetValue(persisted, true, null);
                    typeof(Playnite.SDK.Plugins.Plugin).GetMethod("SavePluginSettings").MakeGenericMethod(settings.GetType()).Invoke(p, new[] { settings });
                }

                var asm = p.GetType().Assembly;
                var request = Activator.CreateInstance(asm.GetType("PlayniteAchievements.Models.RefreshRequest"));
                request.GetType().GetProperty("Mode").SetValue(request, Enum.Parse(asm.GetType("PlayniteAchievements.Models.RefreshModeType"), fullAgain ? "Full" : recentOnly ? "Recent" : "Missing"), null);
                if (resumeFrom > 0)
                {
                    // going on from where it was cut short: the games it hadn't got to (hidden ones as the add-on's setting says)
                    var hiddenToo = persisted.GetType().GetProperty("IncludeHiddenGamesInBulkScans");
                    bool withHidden = hiddenToo == null || (bool)hiddenToo.GetValue(persisted, null);
                    var rest = HomeNav.Api.Database.Games.Where(g => (withHidden || !g.Hidden) && !checkedIds.Contains(g.Id)).Select(g => g.Id).ToList();
                    request.GetType().GetProperty("GameIds").SetValue(request, rest, null);
                }
                var policy = Activator.CreateInstance(asm.GetType("PlayniteAchievements.Services.Refresh.RefreshExecutionPolicy"));
                var pt = policy.GetType();
                pt.GetProperty("ValidateAuthentication").SetValue(policy, false, null);   // (true asks to sign in, in a desktop window)
                pt.GetProperty("UseProgressWindow").SetValue(policy, false, null);
                pt.GetProperty("SwallowExceptions").SetValue(policy, true, null);
                pt.GetProperty("ErrorLogMessage").SetValue(policy, "Selene: checking every game failed.", null);
                var dispatcher = Dispatcher.CurrentDispatcher;
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;   // (the add-on keeps these to itself)
                var rt = p.GetType().GetProperty("RefreshRuntime", any).GetValue(p, null);
                var progress = rt.GetType().GetEvent("RebuildProgress");
                Delegate tracker = null;
                Action<bool> finished = ok => dispatcher.BeginInvoke(new Action(() =>
                {
                    if (tracker != null) try { progress.RemoveEventHandler(rt, tracker); } catch { }
                    tracking = false;
                    HomeNav.Log("achievements: " + (recentOnly ? "games played last" : "every game") + " checked, " + (ok ? "done" : "with errors"));
                    cachedAt = DateTime.MinValue;   // read again on the next look
                    if (recentOnly) return;
                    try { File.WriteAllText(SetupFile, "done " + version + (SteamUserSet(persisted) ? " steam" : "")); } catch { }
                    if (ok) ProgressToast.ShowNote("Achievements", "Ready", 0xea23);
                }));
                pt.GetProperty("OnRefreshCompleted").SetValue(policy, finished, null);
                int n, all; double part; Guid? on;
                if (recentOnly && Checking(out n, out all, out part, out on)) { HomeNav.Log("achievements: the add-on is checking already"); return; }
                if (!recentOnly)
                {
                    // (written and listening before it starts: its first reports come at once)
                    if (resumeFrom == 0) File.WriteAllText(SetupFile, "started" + NL);
                    tracker = Delegate.CreateDelegate(progress.EventHandlerType, typeof(AchievementsData).GetMethod("OnProgress", BindingFlags.NonPublic | BindingFlags.Static));
                    progress.AddEventHandler(rt, tracker);
                    tracking = true;
                }
                var entry = p.GetType().GetProperty("RefreshEntryPoint", any).GetValue(p, null);
                entry.GetType().GetMethod("ExecuteAsync", any, null, new[] { request.GetType(), pt }, null).Invoke(entry, new[] { request, policy });
                if (recentOnly) { HomeNav.Log("achievements: checking the games played last"); return; }
                HomeNav.Log("achievements: " + (resumeFrom > 0 ? "going on with the check cut short, " + resumeFrom + " games done before" : fullAgain ? "checking every game in full" : "Playnite Achievements set up, checking every game"));
                if (Features.On("taskcards")) ProgressToast.ShowNote("Achievements", "Checking", 0xea23);
            }
            catch (Exception ex) { HomeNav.Log("achievements: setting up failed: " + (ex.InnerException ?? ex).Message); }
        }
    }
}
