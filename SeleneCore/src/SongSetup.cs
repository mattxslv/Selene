using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Windows;
using Playnite.SDK.Models;

namespace Selene
{
    // Game songs without any setup. UniPlaySong finds a game's song on YouTube, for which it needs two programs it doesn't
    // bring: yt-dlp (github.com/yt-dlp/yt-dlp, Unlicense) and ffmpeg (gyan.dev's essentials build). Without them it
    // finds nothing, ever: a new install had no game songs at all. Selene downloads both once (about 130 MB) into its own
    // data folder, points UniPlaySong at them, and asks it for a song for every game that has none (UniPlaySong itself
    // does the games added later). Switch: Sound & music > Get game songs.
    public static class SongSetup
    {
        private static readonly Guid UniPlaySongId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static string Tools { get { return Path.Combine(SelenePaths.DataFolder, "Tools"); } }
        private static string YtDlp { get { return Path.Combine(Tools, "yt-dlp.exe"); } }
        private static string Ffmpeg { get { return Path.Combine(Tools, "ffmpeg.exe"); } }
        private static bool started;
        private static int generation;
        private static int setupWorkers;
        private static volatile string downloadProblem;
        private static int songWorkers;
        private static volatile bool downloadFinished;
        internal static bool DisableAutomaticTriggers(object settings)
        {
            bool changed = false;
            foreach (var name in new[] { "AutoDownloadOnLibraryUpdate", "AutoDownloadOnGameInstall" })
            {
                var prop = settings != null ? settings.GetType().GetProperty(name) : null;
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool) && Equals(prop.GetValue(settings, null), true))
                { prop.SetValue(settings, false, null); changed = true; }
            }
            return changed;
        }
        public static void DisableManagedAutomaticDownloads()
        {
            try
            {
                var current = UpsSettings();
                var path = current != null ? Prop(current, "YtDlpPath") as string : null;
                if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(YtDlp), StringComparison.OrdinalIgnoreCase)) return;
                if (UpdateUps(DisableAutomaticTriggers, "Selene download consent"))
                    HomeNav.Log("songs: automatic library/install downloads disabled for Selene-managed tools; independent installations left unchanged");
            }
            catch (Exception ex) { HomeNav.Log("songs: managed download preferences: " + (ex.InnerException ?? ex).Message); }
        }

        // Songs wait until you stop: UniPlaySong switched song for every game passed while a direction was held, and each
        // switch (fade, open, decode) ran on Playnite's UI thread: up to 0.7 s frozen mid-scroll. Its own "hover settle"
        // waits until the selection rests. Turned on (1 s, its shortest) when it's off; switch: Sound & music.
        // Selene's own background default music, in UniPlaySong's default-music slot. The track ships in the theme
        // (Media\Selene OST.mp3); it is copied to Selene Core's data folder, so a theme update or a switch of theme can't
        // take it from under UniPlaySong. Only when no default file is chosen yet, and only once: a file you picked stays, and clearing
        // the setting later is respected. Without it UniPlaySong plays its own first bundled track.
        public static void ApplyDefaultMusic()
        {
            if (!Features.On("selenemusic")) return;
            try
            {
                var source = SelenePaths.ThemeFile("Media", "Selene OST.mp3");
                if (!System.IO.File.Exists(source)) return;
                var target = System.IO.Path.Combine(SelenePaths.DataFolder, "Music", "Selene OST.mp3");
                if (UpdateUps(settings => UseSeleneDefault(settings, source, target), "Selene OST")) HomeNav.Log("songs: Selene's background default music is UniPlaySong's default track");
            }
            catch (Exception ex) { HomeNav.Log("songs: background default music: " + (ex.InnerException ?? ex).Message); }
        }

        // the settings change itself (true: changed, to be saved)
        internal static bool UseSeleneDefault(object settings, string source, string target)
        {
            var type = settings != null ? settings.GetType() : null;
            var prop = type != null ? type.GetProperty("DefaultMusicPath") : null;
            if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(string)) return false;
            var current = prop.GetValue(settings, null) as string;
            bool already = string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
            // the name it had before it was called Selene OST: moved over, not left behind
            var legacy = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target), "Selene background default.mp3");
            bool mine = already || string.Equals(current, legacy, StringComparison.OrdinalIgnoreCase);
            if (!mine && !string.IsNullOrWhiteSpace(current)) return false;                      // their own file

            // UniPlaySong's "default music source": 0 a file, 1 a folder, 2 its own bundled tracks. A file path counts for nothing
            // while it is on 2 (a new install's value), so the source has to be switched too. Left alone when the user chose a
            // folder, or a bundled track other than the one it starts on.
            var sourceProp = type.GetProperty("DefaultMusicSourceOption");
            int? sourceNow = null;
            if (sourceProp != null && sourceProp.CanWrite) try { sourceNow = Convert.ToInt32(sourceProp.GetValue(settings, null)); } catch { }
            var presetProp = type.GetProperty("SelectedBundledPreset");
            var preset = presetProp != null ? presetProp.GetValue(settings, null) as string : null;
            const string StartPreset = "tunetank-dark-ambient-soundscape-music.mp3";
            if (sourceNow == 1) return false;                                                    // a folder of their own
            if (sourceNow == 9) return false;                                                    // Spotify as the ambient (its own switch)
            if (sourceNow == 2 && !string.IsNullOrEmpty(preset) && !string.Equals(preset, StartPreset, StringComparison.OrdinalIgnoreCase) && !mine) return false;   // a bundled track they picked
            var marker = Features.Remembered("selene-default-music");
            // marker "1": set by an earlier build that did not switch the source ("2": this one); "2" with the source moved off 0 is their choice
            if (already && marker == "2" && sourceNow != 0) return false;
            if (!mine && (marker == "1" || marker == "2")) return false;                          // they cleared it

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            if (!System.IO.File.Exists(target) || new System.IO.FileInfo(target).Length != new System.IO.FileInfo(source).Length) System.IO.File.Copy(source, target, true);

            Features.Remember("selene-default-music", "2");
            bool changed = false;
            if (!already) { prop.SetValue(settings, target, null); changed = true; if (mine) { try { System.IO.File.Delete(legacy); } catch { } } }
            if (sourceNow.HasValue && sourceNow.Value != 0)
            {
                sourceProp.SetValue(settings, sourceProp.PropertyType.IsEnum ? Enum.ToObject(sourceProp.PropertyType, 0) : Convert.ChangeType(0, sourceProp.PropertyType), null);
                changed = true;
            }
            return changed;
        }
        internal static bool EnableSettle(object settings)
        {
            var on = settings != null ? settings.GetType().GetProperty("HoverSettleEnabled") : null;
            if (on == null || !on.CanWrite || on.PropertyType != typeof(bool) || Equals(on.GetValue(settings, null), true)) return false;
            on.SetValue(settings, true, null);
            var secs = settings.GetType().GetProperty("HoverSettleSeconds");
            if (secs != null && secs.CanWrite && secs.PropertyType == typeof(double)) secs.SetValue(settings, 1.0, null);
            return true;
        }

        public static void ApplySongSettle()
        {
            if (!Features.On("songsettle")) return;
            try { if (UpdateUps(EnableSettle, "Selene smooth scrolling")) HomeNav.Log("songs: UniPlaySong waits until the selection rests (hover settle on)"); }
            catch (Exception ex) { HomeNav.Log("songs: hover settle: " + (ex.InnerException ?? ex).Message); }
        }

        private static object UpsPlugin() { return HomeNav.Api != null ? HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.Id == UniPlaySongId) : null; }
        private static object UpsSettings()
        {
            var plugin = UpsPlugin();
            var service = plugin != null ? Field(plugin, "_settingsService") : null;
            return service != null ? Prop(service, "Current") : null;
        }

        // a copy of UniPlaySong's settings changed by `change`, handed to its settings service and saved (false: nothing to change)
        private static bool UpdateUps(Func<object, bool> change, string reason)
        {
            var plugin = UpsPlugin();
            var service = plugin != null ? Field(plugin, "_settingsService") : null;
            var current = service != null ? Prop(service, "Current") : null;
            if (current == null) return false;
            var clone = typeof(Playnite.SDK.Data.Serialization).GetMethods().First(m => m.Name == "GetClone" && m.IsGenericMethod).MakeGenericMethod(current.GetType()).Invoke(null, new[] { current });
            if (!change(clone)) return false;
            var update = service.GetType().GetMethod("UpdateSettings");
            if (update == null) { HomeNav.Log("songs: UniPlaySong settings could not be updated (" + reason + ")"); return false; }
            update.Invoke(service, new[] { clone, reason });
            var save = plugin.GetType().GetMethods().FirstOrDefault(m => m.Name == "SavePluginSettings" && m.IsGenericMethod);
            if (save != null) save.MakeGenericMethod(clone.GetType()).Invoke(plugin, new[] { clone });
            HomeNav.ForgetUps();
            return true;
        }
        public static void DownloadSettingsChanged()
        {
            downloadProblem = null;
            System.Threading.Interlocked.Increment(ref generation);
            started = false;
            downloadFinished = false;
            if (DownloadConsent.Allowed("songsetup")) StartLater();
        }

        // how far the songs are, for the setting's row: "57 of 358 games have a song" (UniPlaySong keeps each game's song in
        // its own folder under ExtraMetadata / UniPlaySong / Games; a folder with one is a game with a song)
        private static readonly string[] Audio = { ".mp3", ".m4a", ".ogg", ".opus", ".wav", ".flac" };
        private static string SongsRoot { get { return Path.Combine(HomeNav.Api.Paths.ConfigurationPath, "ExtraMetadata", "UniPlaySong", "Games"); } }
        private static bool HasAudio(string dir) { return Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(f => Audio.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase))); }
        internal static int CountLibrarySongs(string root, IEnumerable<Guid> ids)
        { return ids.Distinct().Count(id => HasAudio(Path.Combine(root, id.ToString()))); }

        // how many games have a song, of how many. The folders are looked through off the UI thread at most every 3 s, and the
        // last count is used meanwhile: going through every game's folder on each call held up Selene settings (the stick
        // stopped for a moment at Sound & music, whose row shows the count and is refreshed every second)
        private static volatile int songsCounted = -1;
        private static DateTime songsCountedAt = DateTime.MinValue;
        private static volatile bool counting;
        private static void Count(out int have, out int total)
        {
            total = HomeNav.Api.Database.Games.Count(g => !g.Hidden);
            if (!counting && (DateTime.Now - songsCountedAt).TotalSeconds > 3)
            {
                counting = true;
                var root = SongsRoot;
                var ids = HomeNav.Api.Database.Games.Where(g => !g.Hidden).Select(g => g.Id).ToArray();
                System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    int n = 0;
                    try { n = CountLibrarySongs(root, ids); }
                    catch (Exception ex) { HomeNav.Log("songs: count: " + ex.Message); }
                    if (n != songsCounted) songsChangedAt = DateTime.Now;
                    songsCounted = n;
                    songsCountedAt = DateTime.Now;
                    counting = false;
                });
            }
            have = Math.Max(0, songsCounted);
        }
        private static bool Counted { get { return songsCounted >= 0; } }

        // Selene's downloads are running (its own tools, songs still missing)
        private static bool Fetching { get { return DownloadConsent.Allowed("songsetup") && started && (!MediaDownloads.Unified || !downloadFinished) && File.Exists(YtDlp) && File.Exists(Ffmpeg); } }
        // UniPlaySong's call can return while its last songs are still landing: done once the count has also stood still a minute
        private static DateTime songsChangedAt = DateTime.MinValue;
        private static bool Settling { get { return downloadFinished && downloadProblem == null && (DateTime.Now - songsChangedAt).TotalSeconds < 60; } }
        public static bool WorkPending { get { return setupWorkers > 0 || songWorkers > 0 || (started && !downloadFinished && downloadProblem == null) || (started && Settling); } }
        public static double CompletionProgress
        {
            get
            {
                if (downloadFinished && !WorkPending && downloadProblem == null) return 1;
                if (HomeNav.Api == null) return 0;
                int have, total; Count(out have, out total);
                double songs = Counted && total > 0 ? Math.Min(1, (double)have / total) : 0;
                // the tools are the first fifth of the songs' share (already there: counted as done)
                double tools = File.Exists(YtDlp) && File.Exists(Ffmpeg) ? 1 : toolsShare;
                double part = 0.2 * tools + 0.8 * songs;
                return WorkPending || downloadProblem != null ? Math.Min(.99, part) : part;
            }
        }

        // how far the songs are (the bar under the setting); below 0: not running
        public static double Progress
        {
            get
            {
                try { if (!Busy) return -1; int have, total; Count(out have, out total); return total > 0 ? (double)have / total : -1; }
                catch { return -1; }
            }
        }

        // songs still on their way (for the red dot on the menu and the Sound & music group)
        public static bool Busy
        {
            get
            {
                try { if (MediaDownloads.Unified) return DownloadConsent.Allowed("songsetup") && WorkPending; if (!Fetching || HomeNav.Api == null) return false; int have, total; Count(out have, out total); return total > 0 && have < total; }
                catch { return false; }
            }
        }

        // the Games row's stats strip: the count and where the songs stand, in a word or two
        public static void Stage(out string value, out string state, out bool done)
        {
            value = "–"; state = "Waiting"; done = false;
            if (HomeNav.Api == null) return;
            int have, total;
            Count(out have, out total);
            if (Counted && total > 0) value = have + " / " + total;
            if (downloadProblem != null) { state = "Check UniPlaySong"; return; }
            if (setupWorkers > 0) { state = "Preparing " + (int)(toolsShare * 100) + "%"; return; }
            if (started && (!File.Exists(YtDlp) || !File.Exists(Ffmpeg))) { state = "Setting up"; return; }
            if (WorkPending) { state = "Downloading"; return; }
            if (!Counted) return;
            done = true;
            state = have >= total ? "Done" : (total - have) + " not found";
        }

        public static string Status()
        {
            try
            {
                if (HomeNav.Api == null) return null;
                if (MediaDownloads.Unified && downloadProblem != null) return downloadProblem;
                if (MediaDownloads.Unified && setupWorkers > 0) return "Preparing song tools…";
                if (MediaDownloads.Unified && started && (!File.Exists(YtDlp) || !File.Exists(Ffmpeg))) return "Waiting for song setup";
                int have, total;
                Count(out have, out total);
                if (!Counted) return null;   // (the first count is still running)
                if (total == 0) return null;
                if (have >= total) return "Every game has a song";
                if (MediaDownloads.Unified && downloadFinished && !WorkPending) return have + " of " + total + " games have a song · " + (total - have) + " not found";
                return have + " of " + total + " games have a song" + (Fetching || WorkPending ? ", more on the way" : "");
            }
            catch { return null; }
        }

        // resting on a game that has no song yet while the songs are still coming: said once a session, short (a new user hears
        // the theme's music where a song would be and might think songs don't work)
        private static bool noted;
        public static void NoteMissing(Guid gameId)
        {
            try
            {
                if (noted || HomeNav.Api == null || !Fetching || !Features.On("songsetup")) return;
                if (HasAudio(Path.Combine(SongsRoot, gameId.ToString()))) return;
                int have, total;
                Count(out have, out total);
                if (!Counted) return;   // (the first count is still running)
                if (total == 0 || have >= total) return;
                noted = true;
                if (Features.On("taskcards")) ProgressToast.ShowNote("Game songs", have + " of " + total + " ready", 0xE405);
            }
            catch (Exception ex) { HomeNav.Log("songs: note: " + ex.Message); }
        }

        // a little after start, in the background
        public static void StartLater()
        {
            DisableManagedAutomaticDownloads();
            if (started || !DownloadConsent.Allowed("songsetup") || HomeNav.Api == null) return;
            started = true;
            int requestGeneration = generation;
            var plugin = HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.Id == UniPlaySongId);
            if (plugin == null) { downloadProblem = "Install UniPlaySong for game songs."; return; }
            var service = Field(plugin, "_settingsService");
            var current = service != null ? Prop(service, "Current") : null;
            if (current == null) { downloadProblem = "UniPlaySong settings unavailable."; HomeNav.Log("songs: UniPlaySong's settings not found (a newer version?)"); return; }
            string yt = Prop(current, "YtDlpPath") as string, ff = Prop(current, "FFmpegPath") as string;
            if (!string.IsNullOrEmpty(yt) && File.Exists(yt) && !string.IsNullOrEmpty(ff) && File.Exists(ff))
            {
                // set up already. By Selene: the songs still missing are asked for again (the list UniPlaySong works through
                // ends with Playnite; games that have a song are skipped). By hand: UniPlaySong's own business
                if (string.Equals(Path.GetFullPath(yt), Path.GetFullPath(YtDlp), StringComparison.OrdinalIgnoreCase) || (MediaDownloads.Unified && MediaDownloads.Enabled))
                {
                    var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                    later.Tick += (s, e) => { later.Stop(); if (requestGeneration == generation && DownloadConsent.Allowed("songsetup")) Fetch(plugin, false); };
                    later.Start();
                }
                return;
            }

            var dispatcher = Application.Current.Dispatcher;
            if (System.Threading.Interlocked.CompareExchange(ref setupWorkers, 1, 0) != 0) { started = false; return; }
            var worker = new System.Threading.Thread(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(20000);   // after the start-up's own work
                    if (requestGeneration != generation || !DownloadConsent.Allowed("songsetup")) return;
                    bool fresh = !File.Exists(YtDlp) || !File.Exists(Ffmpeg);
                    if (fresh && Features.On("taskcards")) dispatcher.BeginInvoke(new Action(() => ProgressToast.ShowNote("Game songs", "Setting up", 0xE405)));
                    Download(requestGeneration);
                    dispatcher.BeginInvoke(new Action(() => { if (requestGeneration == generation && DownloadConsent.Allowed("songsetup")) PointAndFetch(plugin, service); }));
                }
                catch (Exception ex) { downloadProblem = "Song setup failed. Try again."; HomeNav.Log("songs: setup failed: " + ex.Message); }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref setupWorkers, 0);
                    if (requestGeneration != generation && DownloadConsent.Allowed("songsetup")) dispatcher.BeginInvoke(new Action(StartLater));
                }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal };
            worker.Start();
        }

        // the song tools' download, by bytes (yt-dlp is the first ~15%, ffmpeg the rest): the songs' part of the bar moves
        // while they come instead of sitting at 0 for ~130 MB
        private static volatile float toolsShare;
        private static void Fetch(string url, string file, double from, double to)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Selene-Core";
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
                    if (total > 0) toolsShare = (float)(from + (to - from) * got / total);
                }
            }
            toolsShare = (float)to;
        }

        private static void Download(int requestGeneration)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(Tools);
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "Selene-Core";
                if (!File.Exists(YtDlp))
                {
                    if (requestGeneration != generation || !DownloadConsent.Allowed("songsetup")) return;
                    Fetch("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", YtDlp + ".part", 0, 0.15);
                    File.Move(YtDlp + ".part", YtDlp);
                    HomeNav.Log("songs: yt-dlp downloaded");
                }
                if (!File.Exists(Ffmpeg))
                {
                    if (requestGeneration != generation || !DownloadConsent.Allowed("songsetup")) return;
                    var zip = Path.Combine(Path.GetTempPath(), "selene-ffmpeg.zip");
                    Fetch("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", zip, 0.15, 1);
                    using (var archive = ZipFile.OpenRead(zip))
                    {
                        var exe = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                        if (exe == null) throw new FileNotFoundException("ffmpeg.exe isn't in the ffmpeg download");
                        exe.ExtractToFile(Ffmpeg + ".part", true);
                    }
                    File.Move(Ffmpeg + ".part", Ffmpeg);
                    try { File.Delete(zip); } catch { }
                    HomeNav.Log("songs: ffmpeg downloaded");
                }
            }
        }

        // UniPlaySong's settings with the two programs, swapped in through its own settings service (that's what updates its
        // downloader) and saved; then a song for every game without one, the last played first
        private static void PointAndFetch(object plugin, object service)
        {
            if (!DownloadConsent.Allowed("songsetup")) return;
            try
            {
                var current = Prop(service, "Current");
                var cloneMethod = typeof(Playnite.SDK.Data.Serialization).GetMethods().FirstOrDefault(m => m.Name == "GetClone" && m.IsGenericMethod);
                var next = cloneMethod != null ? cloneMethod.MakeGenericMethod(current.GetType()).Invoke(null, new[] { current }) : null;
                if (next == null) { HomeNav.Log("songs: couldn't copy UniPlaySong's settings"); return; }
                Set(next, "YtDlpPath", YtDlp);
                Set(next, "FFmpegPath", Ffmpeg);
                DisableAutomaticTriggers(next);
                var update = service.GetType().GetMethod("UpdateSettings");
                if (update == null) { HomeNav.Log("songs: UniPlaySong can't take new settings (a newer version?)"); return; }
                update.Invoke(service, new object[] { next, "Selene" });
                var save = plugin.GetType().GetMethods().FirstOrDefault(m => m.Name == "SavePluginSettings" && m.IsGenericMethod);
                if (save != null) save.MakeGenericMethod(next.GetType()).Invoke(plugin, new[] { next });
                HomeNav.ForgetUps();   // Selene's music control looks the new settings object up
                HomeNav.Log("songs: UniPlaySong set up with yt-dlp and ffmpeg");

                if (!Fetch(plugin, true))
                {
                    // older/newer UniPlaySong: its next library update does every game added since "never"
                    HomeNav.Log("songs: this UniPlaySong version cannot accept the requested download; use its own download controls");
                }
            }
            catch (Exception ex) { HomeNav.Log("songs: pointing UniPlaySong failed: " + (ex.InnerException ?? ex).Message); }
        }

        // a song for every game without one, the last played first, through UniPlaySong's own automatic download; false when
        // this UniPlaySong has no such method
        private static bool Fetch(object plugin, bool note)
        {
            if (!DownloadConsent.Allowed("songsetup")) return false;
            var fetch = plugin.GetType().GetMethod("AutoDownloadMusicForGamesAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fetch == null) { downloadProblem = "Use UniPlaySong to start song downloads."; return false; }
            var games = HomeNav.Api.Database.Games.Where(g => !g.Hidden).OrderByDescending(g => g.LastActivity ?? DateTime.MinValue).ToList();
            // off the UI thread, as UniPlaySong starts it (its search runs a while before it first yields); one game every
            // couple of seconds, the games that have a song skipped
            int requestGeneration = generation;
            System.Threading.Interlocked.Increment(ref songWorkers);
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (DownloadConsent.Allowed("songsetup"))
                    {
                        var task = fetch.Invoke(plugin, new object[] { games }) as System.Threading.Tasks.Task;
                        if (task != null) await task;
                        else if (requestGeneration == generation) downloadProblem = "Song progress unavailable. Check UniPlaySong.";
                    }
                }
                catch (Exception ex) { if (requestGeneration == generation) downloadProblem = "Song downloads failed. Check UniPlaySong."; HomeNav.Log("songs: " + (ex.InnerException ?? ex).Message); }
                finally
                {
                    System.Threading.Interlocked.Decrement(ref songWorkers);
                    if (requestGeneration == generation) { downloadFinished = true; songsChangedAt = DateTime.Now; }
                }
            });
            HomeNav.Log("songs: UniPlaySong is getting songs for the games without one (" + games.Count + " games)");
            if (note && Features.On("taskcards")) ProgressToast.ShowNote("Game songs", "Downloading", 0xE405);
            return true;
        }

        private static object Field(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        private static object Prop(object o, string name)
        {
            var p = o != null ? o.GetType().GetProperty(name) : null;
            return p != null ? p.GetValue(o, null) : null;
        }

        private static void Set(object o, string name, object value)
        {
            var p = o.GetType().GetProperty(name);
            if (p != null && p.CanWrite) p.SetValue(o, value, null);
        }
    }
}
