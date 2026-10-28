// Games installed outside a store launcher (copied/installed into the game folders): Playnite's library update never sees them.
// Before each library refresh (HomeNav.RefreshLibrary) every folder in the roots that no game points to, and whose name
// isn't already in the library, is added as an installed game with its main exe as the Play action. The library refresh
// that follows then runs the usual new-game setup (auto-setup-new.py: trailer, hero, logo, category, square art).
// Local games without a cover/background get the square/hero that setup made, on a later refresh.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Playnite.SDK.Models;

namespace Selene
{
    public static class LocalGames
    {
        private static string[] Roots { get { return SelenePaths.GameFolders; } }
        private static readonly Regex NotTheGame = new Regex(
            @"unins|setup|install|crash|crs-handler|redist|vc_|vcredist|dxsetup|directx|dotnet|prereq|helper|report|easyanticheat|^eac|battleye|update|patch|config|launcherhelper|cefprocess|subprocess",
            RegexOptions.IgnoreCase);

        private static string Norm(string s)
        {
            var d = (s ?? "").Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var c in d)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static string Full(string p)
        {
            try { return string.IsNullOrWhiteSpace(p) ? null : Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant(); } catch { return null; }
        }

        public static void Scan()
        {
            var api = HomeNav.Api;
            if (api == null) return;
            try
            {
                var db = api.Database;
                var games = db.Games.ToList();
                var dirs = new HashSet<string>(games.Select(g => Full(g.InstallDirectory)).Where(d => d != null));
                var names = new HashSet<string>(games.Select(g => Norm(g.Name)));
                foreach (var root in Roots.Where(Directory.Exists))
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var full = Full(dir);
                        if (full == null || dirs.Contains(full)) continue;
                        var name = Path.GetFileName(dir);
                        if (names.Contains(Norm(name))) continue;  // a store copy already in the library
                        if (IsXboxAppFolder(dir)) continue;  // the Xbox library lists it already
                        if (IsInstallerFolder(dir)) continue;  // a downloaded installer, not an installed game
                        var exe = PickExe(dir);
                        if (exe == null) continue;
                        var game = new Game(name)
                        {
                            InstallDirectory = dir,
                            IsInstalled = true,
                            GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>
                            {
                                new GameAction { Name = "Play", Type = GameActionType.File, Path = exe, WorkingDir = Path.GetDirectoryName(exe), IsPlayAction = true }
                            }
                        };
                        db.Games.Add(game);
                        names.Add(Norm(name));
                        HomeNav.Log("local game added: " + name + " -> " + exe);
                    }
                }
                HideXboxDuplicates(db, games);
                MarkGoneFolders(db, games);
                SyncInstalledState(db, games);
                UseFolderTracking(db, games);
                AttachArt(db, games);
                UseSteamShortcuts(db, db.Games.ToList());
            }
            catch (Exception ex) { HomeNav.Log("local games scan failed: " + ex.Message); }
        }

        // main exe: a non-helper exe in the folder itself, else the largest one up to 4 levels down (Unreal: Binaries\Win64)
        private static string PickExe(string dir)
        {
            var found = new List<string>();
            Collect(dir, 0, found);
            var good = found.Where(f => !NotTheGame.IsMatch(Path.GetFileNameWithoutExtension(f))).ToList();
            if (good.Count == 0) return null;
            var top = good.Where(f => string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase)).ToList();
            var pool = top.Count > 0 ? top : good;
            return pool.OrderByDescending(f => new FileInfo(f).Length).First();
        }

        private static void Collect(string dir, int depth, List<string> found)
        {
            try
            {
                found.AddRange(Directory.GetFiles(dir, "*.exe"));
                if (depth < 4) foreach (var d in Directory.GetDirectories(dir)) Collect(d, depth + 1, found);
            }
            catch { }
        }

        // ---- local games that are also added to Steam as non-Steam games: launch them through Steam ----
        // (Steam Input then handles the controller, e.g. God of War Ragnarok only reads the pad through it.) Playtime keeps
        // being tracked by watching the install folder for the game's process.
        private static string SteamUserData { get { return SelenePaths.SteamUserdata; } }

        private static void UseSteamShortcuts(Playnite.SDK.IGameDatabaseAPI db, List<Game> games)
        {
            var shortcuts = ReadSteamShortcuts();
            if (shortcuts.Count == 0) return;
            foreach (var g in games.Where(x => x.PluginId == Guid.Empty && x.GameActions != null && x.GameActions.Count > 0))
            {
                var play = g.GameActions.FirstOrDefault(a => a.IsPlayAction);
                if (play == null || play.Type != GameActionType.File) continue;
                var exe = Full(Path.IsPathRooted(play.Path ?? "") ? play.Path : Path.Combine(g.InstallDirectory ?? "", play.Path ?? ""));
                uint appid;
                if (exe == null || !shortcuts.TryGetValue(exe, out appid)) continue;
                ulong gameId = ((ulong)appid << 32) | 0x02000000UL;
                play.Type = GameActionType.URL;
                play.Path = "steam://rungameid/" + gameId;
                play.WorkingDir = null;
                play.TrackingMode = TrackingMode.Directory;
                play.TrackingPath = g.InstallDirectory;
                db.Games.Update(g);
                HomeNav.Log("local game now launches through Steam: " + g.Name + " (" + play.Path + ")");
            }
        }

        // exe (full, lower case) -> shortcut appid, from every Steam user's binary shortcuts.vdf
        private static Dictionary<string, uint> ReadSteamShortcuts()
        {
            var map = new Dictionary<string, uint>();
            try
            {
                if (!Directory.Exists(SteamUserData)) return map;
                foreach (var user in Directory.GetDirectories(SteamUserData))
                {
                    var f = Path.Combine(user, "config", "shortcuts.vdf");
                    if (!File.Exists(f)) continue;
                    var b = File.ReadAllBytes(f);
                    int pos = 0;
                    uint appid = 0; string exe = null;
                    while (pos < b.Length)
                    {
                        byte type = b[pos++];
                        if (type == 0x08) { if (exe != null && appid != 0) map[exe] = appid; exe = null; appid = 0; continue; }
                        string key = ReadCString(b, ref pos);
                        if (type == 0x00) continue;  // nested map: entry or tags
                        if (type == 0x01)
                        {
                            var v = ReadCString(b, ref pos);
                            if (key.Equals("Exe", StringComparison.OrdinalIgnoreCase)) exe = Full(v.Trim('"'));
                        }
                        else if (type == 0x02)
                        {
                            if (pos + 4 > b.Length) break;
                            uint v = BitConverter.ToUInt32(b, pos); pos += 4;
                            if (key.Equals("appid", StringComparison.OrdinalIgnoreCase)) appid = v;
                        }
                        else break;  // unknown type: stop rather than misread
                    }
                }
            }
            catch (Exception ex) { HomeNav.Log("steam shortcuts read failed: " + ex.Message); }
            return map;
        }

        private static string ReadCString(byte[] b, ref int pos)
        {
            int start = pos;
            while (pos < b.Length && b[pos] != 0) pos++;
            var s = System.Text.Encoding.UTF8.GetString(b, start, pos - start);
            pos++;
            return s;
        }

        // A local game whose game file is gone (folder deleted/moved) was still "installed" and Play did nothing (Spider-Man 2):
        // it leaves the Installed list, and comes back if the file reappears. Only entries that start a file from their own
        // folder are judged; store/app shortcuts (explorer.exe shell:AppsFolder..., WindowsApps folders we can't read) and
        // Steam-shortcut launches are left alone.
        private static void SyncInstalledState(Playnite.SDK.IGameDatabaseAPI db, List<Game> games)
        {
            foreach (var g in games.Where(x => x.PluginId == Guid.Empty && !string.IsNullOrWhiteSpace(x.InstallDirectory)))
            {
                var play = g.GameActions != null ? g.GameActions.FirstOrDefault(a => a.IsPlayAction) : null;
                bool appShortcut = play != null && ((play.Arguments ?? "").IndexOf("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || g.InstallDirectory.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0);
                if (appShortcut)
                {
                    // one-time repair: an earlier version of this check marked these not installed by mistake (Forza Horizon 6)
                    if (!g.IsInstalled) { g.IsInstalled = true; db.Games.Update(g); HomeNav.Log("local game restored as installed (app shortcut): " + g.Name); }
                    continue;
                }
                if (play == null || play.Type != GameActionType.File || string.IsNullOrWhiteSpace(play.Path)) continue;
                var path = play.Path.Replace("{InstallDir}", g.InstallDirectory);
                if (!Path.IsPathRooted(path)) path = Path.Combine(g.InstallDirectory, path);
                if (!path.StartsWith(g.InstallDirectory, StringComparison.OrdinalIgnoreCase)) continue;  // starts something outside its folder
                bool there = File.Exists(path);
                if (g.IsInstalled == there) continue;
                g.IsInstalled = there;
                db.Games.Update(g);
                HomeNav.Log("local game " + (there ? "installed again: " : "game file gone, marked not installed: ") + g.Name);
            }
        }

        // a game folder can also be the Xbox app's install folder (.GamingRoot): its games carry MicrosoftGame.Config and the
        // Xbox library integration already lists them (they showed up twice more as local games)
        private static bool IsXboxAppFolder(string dir)
        {
            foreach (var d in new[] { dir, Path.Combine(dir, "Content") })
                if (File.Exists(Path.Combine(d, "MicrosoftGame.Config")) || File.Exists(Path.Combine(d, "appxmanifest.xml"))) return true;
            return false;
        }

        // a downloaded installer next to the real install (setup.exe + numbered .bin archives) is not a game
        private static readonly Regex RepackName = new Regex(@"\b(setup|installer)\b", RegexOptions.IgnoreCase);
        private static bool IsInstallerFolder(string dir)
        {
            try
            {
                if (RepackName.IsMatch(Path.GetFileName(dir) ?? "")) return true;
                var files = Directory.GetFiles(dir);
                bool archives = files.Any(f => Regex.IsMatch(Path.GetFileName(f), @"^(fg|setup|data)-?\d+\.bin$", RegexOptions.IgnoreCase));
                bool setup = files.Any(f => Path.GetFileName(f).Equals("setup.exe", StringComparison.OrdinalIgnoreCase));
                return archives || (setup && !files.Any(f => Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)) && files.Count(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) == 1);
            }
            catch { return false; }
        }

        private static readonly Regex GuidName = new Regex(@"^[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}$");

        // A game deleted by hand (folder gone) stayed "Installed": stores keep saying so (the Xbox app still listed all of Call
        // of Duty MWII's packages as installed after its folder was deleted). Folder gone = not installed, any store.
        // Also run after every library update (the store integrations set it back to installed when they import).
        public static void MarkGoneFolders()
        {
            var api = HomeNav.Api;
            if (api == null) return;
            try { MarkGoneFolders(api.Database, api.Database.Games.ToList()); }
            catch (Exception ex) { HomeNav.Log("gone-folder check failed: " + ex.Message); }
        }

        private static void MarkGoneFolders(Playnite.SDK.IGameDatabaseAPI db, List<Game> games)
        {
            foreach (var g in games.Where(x => x.IsInstalled && !x.IsRunning && !string.IsNullOrWhiteSpace(x.InstallDirectory)))
            {
                string dir = g.InstallDirectory;
                if (dir.Contains("{")) continue;  // unresolved variable: can't tell
                if (dir.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0) continue;  // locked: reads as missing (Forza)
                var root = Path.GetPathRoot(dir);
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;  // drive not there (unplugged): leave it
                if (Directory.Exists(dir)) continue;
                g.IsInstalled = false;
                db.Games.Update(g);
                HomeNav.Log("game folder gone, marked not installed: " + g.Name + " (" + dir + ")");
            }
        }

        private static void HideXboxDuplicates(Playnite.SDK.IGameDatabaseAPI db, List<Game> games)
        {
            foreach (var g in games.Where(x => x.PluginId == Guid.Empty && !x.Hidden && !string.IsNullOrWhiteSpace(x.InstallDirectory)))
            {
                bool underRoot = Roots.Any(r => g.InstallDirectory.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                if (!underRoot) continue;
                bool xbox = Directory.Exists(g.InstallDirectory) && IsXboxAppFolder(g.InstallDirectory);
                bool codeName = GuidName.IsMatch(g.Name ?? "");
                bool installer = Directory.Exists(g.InstallDirectory) && IsInstallerFolder(g.InstallDirectory);
                if (!xbox && !codeName && !installer) continue;
                g.Hidden = true;  // hidden, not deleted: playtime stays, can be shown again
                db.Games.Update(g);
                HomeNav.Log("local game hidden (" + (xbox ? "Xbox app install, the Xbox library has it" : installer ? "installer download, not the game" : "code name, not a real game name") + "): " + g.Name);
            }
        }

        // Elevation detection only: Selene never installs or runs elevated scheduled tasks.
        [System.Runtime.InteropServices.DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [System.Runtime.InteropServices.DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int size, out int returned);

        public static bool IsElevated(int pid)
        {
            var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return true;  // unknown: do not attempt process control
            try
            {
                IntPtr token;
                if (!OpenProcessToken(h, 0x0008 /* TOKEN_QUERY */, out token)) return true;  // a normal program can't read an admin one's token
                try { int v, n; return !GetTokenInformation(token, 20 /* TokenElevation */, out v, 4, out n) || v != 0; }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(h); }
        }

        // ---- automatic: wrong or generic game folder, learned from where the game actually runs ----
        // (a launcher or tool in Downloads made a game "playing" whenever anything ran from Downloads)
        private static bool IsGenericFolder(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return true;
            var d = Full(dir);
            var home = Full(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            foreach (var g in new[] { home, Full(Path.Combine(home, "Downloads")), Full(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                                      Full(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) })
                if (d == g) return true;
            var root = Path.GetPathRoot(dir);
            return !string.IsNullOrEmpty(root) && d == Full(root);
        }

        public static void LearnFolder(Guid gameId, string exePath)
        {
            try
            {
                var api = HomeNav.Api;
                if (api == null || string.IsNullOrWhiteSpace(exePath)) return;
                var g = api.Database.Games.Get(gameId);
                if (g == null) return;
                var real = Path.GetDirectoryName(exePath);
                if (IsGenericFolder(real)) return;
                // a real, existing folder is left alone (games can run helpers from elsewhere)
                if (!IsGenericFolder(g.InstallDirectory) && Directory.Exists(g.InstallDirectory)) return;
                g.InstallDirectory = real;
                var play = g.GameActions != null ? g.GameActions.FirstOrDefault(a => a.IsPlayAction) : null;
                if (play != null && play.TrackingMode == TrackingMode.Directory) play.TrackingPath = real;
                api.Database.Games.Update(g);
                HomeNav.Log("game folder learned from the running game: " + g.Name + " -> " + real);
            }
            catch (Exception ex) { HomeNav.Log("folder learn failed: " + ex.Message); }
        }

        // Folder of the game's own exe; falls back to InstallDirectory (Steam-shortcut launches, no exe path)
        private static string GameFolder(Game g)
        {
            var play = g.GameActions != null ? g.GameActions.FirstOrDefault(a => a.IsPlayAction) : null;
            if (play != null && play.Type == GameActionType.File && !string.IsNullOrWhiteSpace(play.Path) && play.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var exe = play.Path.Replace("{InstallDir}", g.InstallDirectory ?? "");
                if (!Path.IsPathRooted(exe)) exe = Path.Combine(g.InstallDirectory ?? "", exe);
                var dir = Path.GetDirectoryName(exe);
                // a helper started from somewhere else (e.g. a tool started from Downloads, which keeps running after the
                // game closes) doesn't define the game: then the install folder does
                bool inside = !string.IsNullOrWhiteSpace(g.InstallDirectory) && dir != null
                              && Full(dir).StartsWith(Full(g.InstallDirectory));
                if (!string.IsNullOrWhiteSpace(dir) && (inside || string.IsNullOrWhiteSpace(g.InstallDirectory))) return dir;
            }
            return g.InstallDirectory;
        }

        // Track local games by their folder: Star Rail's first process exits at once and hands over to an elevated one, so
        // Playnite thought it "stopped after 6 seconds" (no Now Playing, no playtime). Any process from the folder counts.
        private static void UseFolderTracking(Playnite.SDK.IGameDatabaseAPI db, List<Game> games)
        {
            foreach (var g in games.Where(x => x.PluginId == Guid.Empty && !string.IsNullOrWhiteSpace(x.InstallDirectory) && x.GameActions != null))
            {
                var play = g.GameActions.FirstOrDefault(a => a.IsPlayAction);
                if (play == null || play.Type != GameActionType.File) continue;
                if (play.TrackingMode == TrackingMode.Directory && string.Equals(play.TrackingPath, GameFolder(g), StringComparison.OrdinalIgnoreCase)) continue;
                if ((play.Arguments ?? "").IndexOf("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                play.TrackingMode = TrackingMode.Directory;
                play.TrackingPath = GameFolder(g);
                db.Games.Update(g);
                HomeNav.Log("local game tracked by folder: " + g.Name + " (" + play.TrackingPath + ")");
            }
        }

        // ---- already running when Playnite started (or not started by Playnite): show it as playing ----
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);

        private static readonly HashSet<Guid> markedRunning = new HashSet<Guid>();
        private static System.Windows.Threading.DispatcherTimer runningTimer;

        public static void StartRunningWatch()
        {
            if (runningTimer != null) return;
            if (HomeNav.Isolated) { HomeNav.Log("dev isolated: games running on this PC are not adopted"); return; }
            // first look right after start (a game left running shows as playing at once), then every 4 s
            runningTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            runningTimer.Tick += (s, e) => { runningTimer.Interval = TimeSpan.FromSeconds(4); CheckRunning(); };
            runningTimer.Start();
        }

        // path of every process, elevated ones too (limited-information access works across integrity levels)
        private static List<string> ProcessPaths()
        {
            var list = new List<string>();
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new System.Text.StringBuilder(1024); int n = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref n)) list.Add(sb.ToString().ToLowerInvariant());
                }
                finally { CloseHandle(h); }
            }
            return list;
        }

        public static string ExeOf(int pid)
        {
            var h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return null;
            try { var sb = new System.Text.StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null; }
            finally { CloseHandle(h); }
        }

        public static bool IsMarkedRunning(Guid id) { return markedRunning.Contains(id); }

        // pids of processes running from a game's folder (elevated ones too)
        public static List<int> ProcessesInGameFolder(Game g)
        {
            var list = new List<int>();
            if (g == null) return list;
            var folder = GameFolder(g);
            if (string.IsNullOrWhiteSpace(folder)) return list;
            var dir = Full(folder) + "\\";
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                var h = OpenProcess(0x1000, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new System.Text.StringBuilder(1024); int n = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref n) && sb.ToString().ToLowerInvariant().StartsWith(dir)) list.Add(p.Id);
                }
                finally { CloseHandle(h); }
            }
            return list;
        }

        private static void CheckRunning()
        {
            var api = HomeNav.Api;
            if (api == null) return;
            try
            {
                var db = api.Database;
                var candidates = db.Games.Where(x => x.PluginId == Guid.Empty && x.IsInstalled && !string.IsNullOrWhiteSpace(x.InstallDirectory)
                                                     && x.InstallDirectory.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0).ToList();
                if (candidates.Count == 0) return;
                var paths = ProcessPaths();
                foreach (var g in candidates)
                {
                    var dir = Full(GameFolder(g)) + "\\";
                    var match = paths.FirstOrDefault(pp => pp.StartsWith(dir));
                    bool running = match != null;
                    // first look after start: a "running" mark left in the database from last session is ours to manage
                    if (firstCheck && g.IsRunning && !g.IsLaunching) markedRunning.Add(g.Id);
                    if (running && !g.IsRunning && !g.IsLaunching)
                    {
                        g.IsRunning = true; db.Games.Update(g); markedRunning.Add(g.Id);
                        HomeNav.Log("running game found (not started by Playnite): " + g.Name + " <- " + match + " [install dir: " + g.InstallDirectory + ", watching " + GameFolder(g) + "]");
                    }
                    else if (!running && g.IsRunning && markedRunning.Contains(g.Id))
                    {
                        g.IsRunning = false; db.Games.Update(g); markedRunning.Remove(g.Id);
                        HomeNav.Log("running game closed: " + g.Name);
                    }
                }
            }
            catch (Exception ex) { HomeNav.Log("running check failed: " + ex.Message); }
            firstCheck = false;
        }

        private static bool firstCheck = true;

        // local games have no store art: use the square tile / hero that auto-setup made as cover / background
        private static void AttachArt(Playnite.SDK.IGameDatabaseAPI db, List<Game> games)
        {
            foreach (var g in games.Where(x => x.PluginId == Guid.Empty && x.IsInstalled))
            {
                bool changed = false;
                var square = Path.Combine(SelenePaths.Media("Square"), g.Id + ".jpg");
                var hero = Path.Combine(SelenePaths.Media("Hero"), g.Id + ".jpg");
                if (string.IsNullOrEmpty(g.CoverImage) && File.Exists(square)) { g.CoverImage = db.AddFile(square, g.Id); changed = true; }
                if (string.IsNullOrEmpty(g.BackgroundImage) && File.Exists(hero)) { g.BackgroundImage = db.AddFile(hero, g.Id); changed = true; }
                if (changed) { db.Games.Update(g); HomeNav.Log("local game art attached: " + g.Name); }
            }
        }
    }
}
