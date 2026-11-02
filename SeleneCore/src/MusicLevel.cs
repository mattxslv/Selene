using System;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;

namespace Selene
{
    // Music volume and Mute music (Selene settings > Sound & music) are the master for all music. Playnite's own volume only
    // reaches Playnite's background music, and UniPlaySong keeps a volume of its own, so the start-up and home music played on
    // with both at off. UniPlaySong's player has a temporary level (SetVolumeMultiplier): Selene sets it to the master level and
    // UniPlaySong's own saved volume stays as it is. Only while "Let Selene run the music" is on; off hands the level back.
    // Idea and first version: amogh-w (Selene pull request #99).
    public static class MusicLevel
    {
        private static readonly Guid UpsId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static readonly string[] Steps = { "0", "25", "50", "75", "100" };
        private static DispatcherTimer timer;
        private static double? applied;

        public static bool Installed() { return Features.HasAddon("UniPlaySong"); }

        public static void Start()
        {
            if (timer != null) return;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) => { HomeNav.SyncMoveSound(); Apply(); };
            timer.Start();
            HomeNav.SyncMoveSound();
            Apply();   // at once; if UniPlaySong's player isn't there yet, on the next tick
        }

        // Playnite closing: UniPlaySong and Spotify go back to their own levels
        public static void Release() { if (timer != null) timer.Stop(); HandBack(); }

        private static void HandBack()
        {
            if (applied.HasValue) { Set(1.0); HomeNav.Log("music level: UniPlaySong back at its own level"); }
            applied = null;
            SpotifyLevel(null);
        }

        // ---- the master level, applied to UniPlaySong ----
        private static void Apply()
        {
            try
            {
                if (!Installed()) return;
                if (!Features.On("music")) { HandBack(); return; }   // Let Selene run the music: off leaves UniPlaySong alone
                var fs = FullscreenSettings();
                if (fs == null) return;
                FirstStart(fs);
                SyncPlayniteMute(fs, Muted());
                var volume = fs.GetType().GetProperty("BackgroundVolume");
                double level = Muted() ? 0.0 : volume != null ? Math.Max(0.0, Math.Min(1.0, Convert.ToDouble(volume.GetValue(fs, null)))) : 1.0;
                SpotifyLevel(level);
                SpotifyFollow();
                // looked at every tick (the player may not exist yet at start-up), written only when it differs (Set)
                if (!Set(level)) return;
                if (applied.HasValue && Math.Abs(applied.Value - level) < 0.001) return;
                applied = level;
                HomeNav.Log("music level: UniPlaySong at " + Math.Round(level * 100) + "%" + (Muted() ? " (muted)" : ""));
            }
            catch (Exception ex) { HomeNav.Log("music level: " + ex.Message); }
        }

        // Before, Playnite's music volume didn't reach UniPlaySong, so many had it low or at 0 and still heard their music. The
        // first start with the master sets it to 100% once: UniPlaySong plays exactly as loud as it did. Then it is theirs.
        private static void FirstStart(object fs)
        {
            if (Features.Remembered("musiclevel-start") != null) return;
            Features.Remember("musiclevel-start", "1");
            var p = fs.GetType().GetProperty("BackgroundVolume");
            if (p == null || !p.CanWrite) return;
            HomeNav.Log("music level: first start, Playnite's music volume " + Math.Round(Convert.ToDouble(p.GetValue(fs, null)) * 100) + "% -> 100% (UniPlaySong stays as loud as it was)");
            p.SetValue(fs, Convert.ChangeType(1.0, p.PropertyType), null);
            SaveNow();
        }

        // the player's temporary level (0 silent, 1 as UniPlaySong has it)
        private static bool Set(double level)
        {
            try
            {
                var svc = Service();
                if (svc == null) return false;
                // only when it differs: setting it writes the player's volume at once, which cuts a fade-out short, and UniPlaySong
                // only changes songs when that fade ends (written every tick, songs got stuck on one track)
                var held = svc.GetType().GetField("_volumeMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);
                if (held != null && Math.Abs(Convert.ToDouble(held.GetValue(svc)) - level) < 0.001) return true;
                var set = svc.GetType().GetMethod("SetVolumeMultiplier", new[] { typeof(double) });
                if (set == null) { HomeNav.Log("music level: UniPlaySong has no SetVolumeMultiplier(double)"); return false; }
                set.Invoke(svc, new object[] { level });
                return true;
            }
            catch (Exception ex) { HomeNav.Log("music level: " + (ex.InnerException ?? ex).Message); return false; }
        }

        // ---- the settings rows: Playnite's music volume, edited there ----
        public static string Volume()
        {
            var fs = FullscreenSettings();
            var p = fs != null ? fs.GetType().GetProperty("BackgroundVolume") : null;
            if (p == null) return "50";
            double v = Convert.ToDouble(p.GetValue(fs, null)) * 100;
            return Steps.OrderBy(s => Math.Abs(double.Parse(s) - v)).First();   // 45% (an older default) reads as 50%
        }

        public static void SetVolume(string value)
        {
            var fs = FullscreenSettings();
            var p = fs != null ? fs.GetType().GetProperty("BackgroundVolume") : null;
            if (p == null) return;
            p.SetValue(fs, Convert.ChangeType(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture) / 100.0, p.PropertyType), null);
            SaveNow();
            Apply();
        }

        // Playnite writes its settings when it closes: a change is written at once, or a crash brings the old level back
        private static void SaveNow()
        {
            try
            {
                var settings = AppSettings();
                var save = settings != null ? settings.GetType().GetMethod("SaveSettings", Type.EmptyTypes) : null;
                if (save == null) { HomeNav.Log("music level: Playnite has no SaveSettings()"); return; }
                save.Invoke(settings, null);
            }
            catch (Exception ex) { HomeNav.Log("music level: save: " + (ex.InnerException ?? ex).Message); }
        }

        // Mute music is Selene's own switch: Playnite doesn't save IsMusicMuted, so it came back at every start. Selene keeps it
        // and hands it to Playnite.
        public static bool Muted() { return Features.On("musicmuted"); }

        private static void SyncPlayniteMute(object fs, bool muted)
        {
            var p = fs.GetType().GetProperty("IsMusicMuted");
            if (p != null && p.CanWrite && !Equals(p.GetValue(fs, null), muted)) p.SetValue(fs, muted, null);
        }

        // ---- UniPlaySong's own switches that choose what plays ----
        public static bool UpsSwitch(string name)
        {
            var settings = UpsSettings();
            var p = settings != null ? settings.GetType().GetProperty(name) : null;
            return p != null && Equals(p.GetValue(settings, null), true);
        }

        public static void SetUpsSwitch(string name, bool on)
        {
            try
            {
                var plugin = Plugin();
                var settings = UpsSettings();
                var prop = settings != null ? settings.GetType().GetProperty(name) : null;
                if (plugin == null || prop == null || !prop.CanWrite) { HomeNav.Log("music: UniPlaySong setting " + name + " not found"); return; }
                prop.SetValue(settings, on, null);
                SaveUps(plugin, settings);
                HomeNav.Log("music: UniPlaySong " + name + " " + (on ? "on" : "off"));
            }
            catch (Exception ex) { HomeNav.Log("music: changing " + name + " failed: " + (ex.InnerException ?? ex).Message); }
        }

        // saved the way the add-on's own settings page saves
        private static void SaveUps(object plugin, object settings)
        {
            var save = typeof(Playnite.SDK.Plugins.Plugin).GetMethod("SavePluginSettings").MakeGenericMethod(settings.GetType());
            save.Invoke(plugin, new[] { settings });
        }

        // ---- Spotify as the ambient music: UniPlaySong's default music source set to Spotify (it drives the Spotify app wherever
        // a game has no song of its own). Off puts back the source chosen before. ----
        private const int SpotifySource = 9, BundledSource = 2;   // UniPlaySong's DefaultMusicSource: Spotify, BundledPreset

        public static bool SpotifyAmbient()
        {
            var settings = UpsSettings();
            var p = settings != null ? settings.GetType().GetProperty("DefaultMusicSourceOption") : null;
            return p != null && Convert.ToInt32(p.GetValue(settings, null)) == SpotifySource;
        }

        public static void SetSpotifyAmbient(bool on)
        {
            try
            {
                var plugin = Plugin();
                var settings = UpsSettings();
                var prop = settings != null ? settings.GetType().GetProperty("DefaultMusicSourceOption") : null;
                if (plugin == null || prop == null || !prop.CanWrite) { HomeNav.Log("music: UniPlaySong's default music source not found"); return; }
                int now = Convert.ToInt32(prop.GetValue(settings, null)), to;
                if (on)
                {
                    if (now == SpotifySource) return;
                    Features.Remember("spotifyprevsource", now.ToString());   // what to go back to
                    to = SpotifySource;
                }
                else if (!int.TryParse(Features.Remembered("spotifyprevsource"), out to) || to == SpotifySource) to = BundledSource;
                prop.SetValue(settings, Enum.ToObject(prop.PropertyType, to), null);
                SaveUps(plugin, settings);
                HomeNav.Log("music: UniPlaySong default music source " + now + " -> " + to);
                if (!on) SpotifyLevel(null);
            }
            catch (Exception ex) { HomeNav.Log("music: Spotify switch failed: " + (ex.InnerException ?? ex).Message); }
        }

        // Spotify plays from its own app, past UniPlaySong's player: the master level goes to the Spotify app's volume in the
        // Windows mixer. Windows keeps that volume, so Spotify's own level is put back when it stops being the ambient and when
        // Playnite closes. Looked at when the level changes, and every few seconds (Spotify opened later).
        private static float spotifyOwn = -1;
        private static double? spotifySet;
        private static DateTime spotifyAt;

        private static void SpotifyLevel(double? level)
        {
            if (level.HasValue && !SpotifyAmbient()) level = null;
            if (!level.HasValue)
            {
                if (spotifyOwn < 0) return;
                CloseToTray.SetAppVolume("Spotify", spotifyOwn);
                HomeNav.Log("music level: Spotify back at " + Math.Round(spotifyOwn * 100) + "%");
                spotifyOwn = -1; spotifySet = null;
                return;
            }
            bool same = spotifySet.HasValue && Math.Abs(spotifySet.Value - level.Value) < 0.001;
            if (same && (DateTime.Now - spotifyAt).TotalSeconds < 5) return;
            spotifyAt = DateTime.Now;
            var before = CloseToTray.SetAppVolume("Spotify", (float)level.Value);
            if (before < 0) return;   // not running
            if (spotifyOwn < 0) spotifyOwn = before;
            if (!same) HomeNav.Log("music level: Spotify at " + Math.Round(level.Value * 100) + "%");
            spotifySet = level;
        }

        // UniPlaySong's Spotify conductor
        private static object SpotifyControl()
        {
            var plugin = Plugin();
            var field = plugin != null ? plugin.GetType().GetField("_spotifyControlService", BindingFlags.Instance | BindingFlags.NonPublic) : null;
            return field != null ? field.GetValue(plugin) : null;
        }

        // UniPlaySong pauses Spotify for a game's song, a trailer's sound or a launch, but only looks every 2 s (Spotify played
        // over the start of a game's song): asked to look again each tick (it only sends Spotify a command when it must)
        private static void SpotifyFollow()
        {
            try
            {
                if (!SpotifyAmbient()) return;
                var spotify = SpotifyControl();
                var recompute = spotify != null ? spotify.GetType().GetMethod("Recompute", Type.EmptyTypes) : null;
                if (recompute != null) recompute.Invoke(spotify, null);
            }
            catch (Exception ex) { HomeNav.Log("music: Spotify follow: " + (ex.InnerException ?? ex).Message); }
        }

        private static bool SpotifyPlaying()
        {
            var settings = UpsSettings();
            var p = settings != null ? settings.GetType().GetProperty("SpotifyActive") : null;
            return p != null && Equals(p.GetValue(settings, null), true);
        }

        // ---- what is playing ----
        // "Now playing: title - artist" under the volume row (nothing while nothing plays)
        public static string NowPlaying()
        {
            string title, artist;
            Song(out title, out artist);
            if (string.IsNullOrEmpty(title)) return null;
            return "Now playing: " + title + (string.IsNullOrEmpty(artist) ? "" : " - " + artist);
        }

        // the song and its artist (the default music's title is its file name after "[Default]")
        public static void Song(out string title, out string artist)
        {
            title = artist = null;
            var settings = UpsSettings();
            if (settings == null) return;
            var t = settings.GetType().GetProperty("NowPlayingTitle");
            var a = settings.GetType().GetProperty("NowPlayingArtist");
            title = t != null ? t.GetValue(settings, null) as string : null;
            artist = a != null ? a.GetValue(settings, null) as string : null;
            if (title != null && title.StartsWith("[Default]")) title = title.Substring(9).Trim();
            // UniPlaySong's title follows the game under the cursor while Selene keeps the home music going: what really plays is
            // the file its player has open, so where the two differ the file's name is shown
            var file = PlayingFile();
            if (!string.IsNullOrEmpty(file) && (string.IsNullOrEmpty(title) || Plain(title).IndexOf(Plain(file), StringComparison.Ordinal) < 0 && Plain(file).IndexOf(Plain(title), StringComparison.Ordinal) < 0))
            { title = Readable(file); artist = null; }
        }

        // a file name as words ("dark-ambient-soundscape" reads "dark ambient soundscape")
        private static string Readable(string file)
        {
            var s = file.Replace('_', ' ');
            if (s.IndexOf(' ') < 0) s = s.Replace('-', ' ');
            return s.Trim();
        }

        private static string Plain(string s) { return new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(); }

        private static string PlayingFile()
        {
            try
            {
                var svc = Service();
                var p = svc != null ? svc.GetType().GetProperty("CurrentSongPath") : null;
                var path = p != null ? p.GetValue(svc, null) as string : null;
                return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileNameWithoutExtension(path);
            }
            catch { return null; }
        }

        // ---- the Music card: the player's own controls ("TogglePlayPauseInternal", "SkipToNextSong", "RestartCurrentSong") ----
        public static void Control(string method)
        {
            try
            {
                if (SpotifyPlaying())
                {
                    // through UniPlaySong's Spotify conductor: a pause made there holds (it would play Spotify again at once
                    // otherwise). Previous restarts the track when it is a few seconds in.
                    var spotify = SpotifyControl();
                    var name = method == "TogglePlayPauseInternal" ? "ToggleManualPlayPause" : method == "SkipToNextSong" ? "SkipNext" : "SkipPrevious";
                    var call = spotify != null ? spotify.GetType().GetMethod(name, Type.EmptyTypes) : null;
                    if (call == null) { HomeNav.Log("music: UniPlaySong's Spotify control has no " + name + "()"); return; }
                    call.Invoke(spotify, null);
                    HomeNav.Log("music: Spotify " + name);
                    return;
                }
                var svc = Service();
                var m = svc != null ? svc.GetType().GetMethod(method, Type.EmptyTypes) : null;
                if (m == null) { HomeNav.Log("music: UniPlaySong has no " + method + "()"); return; }
                m.Invoke(svc, null);
                HomeNav.Log("music: " + method);
            }
            catch (Exception ex) { HomeNav.Log("music: " + method + ": " + (ex.InnerException ?? ex).Message); }
        }

        public static bool Paused()
        {
            try
            {
                var svc = Service();
                var p = svc != null ? svc.GetType().GetProperty("IsPaused") : null;
                return p != null && (bool)p.GetValue(svc, null);
            }
            catch { return false; }
        }

        // the state in one line, for the log (the music? dev key)
        public static string Describe()
        {
            var svc = Service();
            var held = svc != null ? svc.GetType().GetField("_volumeMultiplier", BindingFlags.Instance | BindingFlags.NonPublic) : null;
            return "installed=" + Installed() + " player=" + (svc != null) + " volume=" + Volume() + " muted=" + Muted()
                + " multiplier=" + (held != null ? Convert.ToDouble(held.GetValue(svc)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "?")
                + " paused=" + Paused() + " movesound=" + HomeNav.MoveSoundState() + " spotify=" + SpotifyAmbient() + " playing='" + NowPlaying() + "'";
        }

        // ---- finding things ----
        private static object UpsSettings() { return HomeNav.LiveUps(); }

        private static Playnite.SDK.Plugins.Plugin Plugin()
        {
            return HomeNav.Api != null ? HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.Id == UpsId) : null;
        }

        // UniPlaySong's player (a private field of its plugin)
        private static object Service()
        {
            var plugin = Plugin();
            var field = plugin != null ? plugin.GetType().GetField("_playbackService", BindingFlags.Instance | BindingFlags.NonPublic) : null;
            return field != null ? field.GetValue(plugin) : null;
        }

        private static object AppSettings()
        {
            var appType = Type.GetType("Playnite.PlayniteApplication, Playnite");
            var cur = appType != null ? appType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
            var current = cur != null ? cur.GetValue(null, null) : null;
            var prop = current != null ? appType.GetProperty("AppSettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
            return prop != null ? prop.GetValue(current, null) : null;
        }

        private static object FullscreenSettings()
        {
            var settings = AppSettings();
            var fs = settings != null ? settings.GetType().GetProperty("Fullscreen") : null;
            return fs != null ? fs.GetValue(settings, null) : null;
        }
    }
}
