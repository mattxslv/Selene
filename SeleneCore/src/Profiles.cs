using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace Selene
{
    public class Profile
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Avatar { get; set; }
        public string Theme { get; set; }
        // null = the theme's default wallpaper, so switching themes changes it until the user picks one themselves
        public string Wallpaper { get; set; }
        // what's behind the home: "art" (the game's art, the default) or "wallpaper" (the wallpaper only)
        public string HomeBackground { get; set; }
        public DateTime Created { get; set; }
        public List<Guid> Favorites { get; set; }
        public Dictionary<Guid, ulong> Playtime { get; set; }
        public Dictionary<Guid, DateTime> LastPlayed { get; set; }

        public Profile()
        {
            Favorites = new List<Guid>();
            Playtime = new Dictionary<Guid, ulong>();
            LastPlayed = new Dictionary<Guid, DateTime>();
        }
    }

    public class ProfileStore
    {
        public List<Profile> Profiles { get; set; }
        public Guid ActiveId { get; set; }

        public ProfileStore() { Profiles = new List<Profile>(); }
    }

    // Selene's own profiles (Playnite has a single user): look, favorites, playtime and last played per person.
    // The game library and store accounts stay shared. Saved in Selene Core's data folder as profiles.json.
    public static class Profiles
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static string file;
        private static ProfileStore store = new ProfileStore();
        // set when profiles.json exists but couldn't be loaded: nothing is saved, so the file on disk is never overwritten
        private static string loadError;

        public static void Init(string dataFolder)
        {
            // developer switch: %TEMP%\selene-dev-profiles keeps profiles in a scratch folder (to try the first launch)
            if (File.Exists(Path.Combine(Path.GetTempPath(), "selene-dev-profiles"))) dataFolder = Path.Combine(Path.GetTempPath(), "selene-dev-profiles-data");
            Directory.CreateDirectory(dataFolder);
            file = Path.Combine(dataFolder, "profiles.json");
            if (!File.Exists(file)) return;
            string json;
            try { json = File.ReadAllText(file); }
            catch (IOException ex) { Fail(ex, "profiles.json is locked or unreadable right now"); return; }
            catch (UnauthorizedAccessException ex) { Fail(ex, "no permission to read profiles.json"); return; }
            try
            {
                store = Serialization.FromJson<ProfileStore>(json) ?? new ProfileStore();
                if (store.Profiles == null) store.Profiles = new List<Profile>();
            }
            catch (Exception ex)
            {
                // damaged file: set it aside for recovery and start fresh
                var kept = file + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(file, kept); }
                catch (Exception moveEx) { Fail(moveEx, "profiles.json is damaged and couldn't be set aside"); return; }
                logger.Error(ex, "Selene Core: profiles.json was damaged, moved to " + kept + " and starting fresh");
                store = new ProfileStore();
            }
        }

        private static void Fail(Exception ex, string why)
        {
            loadError = why;
            store = new ProfileStore();
            logger.Error(ex, "Selene Core: " + why + "; profiles are off until the next start and nothing will be saved");
        }

        // why profiles couldn't be loaded this session (null when they loaded fine), for the screens to show
        public static string LoadError { get { return loadError; } }

        public static IReadOnlyList<Profile> All { get { return store.Profiles; } }

        public static bool FirstRun { get { return store.Profiles.Count == 0; } }

        public static Profile Active { get { return store.Profiles.FirstOrDefault(p => p.Id == store.ActiveId); } }

        // the avatars shipped with the theme (theme\Avatars)
        public static string[] BuiltInAvatars()
        {
            var folder = SelenePaths.ThemeFile("Avatars");
            // pictures and GIFs (a GIF default moves, like one of your own); the GIFs first, where they're seen (in the
            // alphabet they were lost among the pictures)
            return Directory.Exists(folder) ? Directory.GetFiles(folder).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(f => f).ToArray() : new string[0];
        }

        public static Profile Create(string name, string avatar, string theme, string wallpaper)
        {
            var profile = new Profile { Id = Guid.NewGuid(), Name = name, Avatar = avatar, Theme = theme, Wallpaper = wallpaper, Created = DateTime.Now };
            store.Profiles.Add(profile);
            store.ActiveId = profile.Id;
            Save();
            return profile;
        }

        public static void SignIn(Guid id)
        {
            if (store.Profiles.All(p => p.Id != id)) throw new ArgumentException("no Selene profile with id " + id);
            store.ActiveId = id;
            Save();
            ApplyLook();
        }

        // saves changes made to a profile's fields (the profile steps in edit mode)
        public static void Update(Profile profile)
        {
            Save();
            if (Active == profile) ApplyLook();
        }

        public static void Remove(Guid id)
        {
            store.Profiles.RemoveAll(p => p.Id == id);
            if (store.ActiveId == id) store.ActiveId = Guid.Empty;
            Save();
        }

        // built-in avatars are kept as "Avatars/<file>" (the theme folder can move); pictures people add are full paths
        public static string AvatarFile(Profile profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.Avatar)) return null;
            if (Path.IsPathRooted(profile.Avatar)) return profile.Avatar;
            return SelenePaths.ThemeFile(profile.Avatar.Split('/'));
        }

        // a picture someone picked (or their Steam avatar): copied into Selene Core's data so it survives the original moving
        public static string KeepPicture(string source)
        {
            // the same picture kept before (e.g. the Steam avatar picked again): that copy
            var folder = Path.Combine(Path.GetDirectoryName(file), "avatars");
            if (Directory.Exists(folder))
            {
                var print = Fingerprint(source);
                var same = Directory.GetFiles(folder).FirstOrDefault(f => Fingerprint(f) == print);
                if (same != null) return same;
            }
            var target = NewPicturePath(Path.GetExtension(source));
            File.Copy(source, target);
            return target;
        }

        // pictures people added before (framed or copied), newest first, offered again in the avatar picker
        public static List<string> KeptPictures()
        {
            var folder = Path.Combine(Path.GetDirectoryName(file), "avatars");
            if (!Directory.Exists(folder)) return new List<string>();
            var seen = new HashSet<string>();
            return new DirectoryInfo(folder).GetFiles().Where(f => f.Extension == ".png" || f.Extension == ".jpg" || f.Extension == ".jpeg" || f.Extension == ".gif")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Where(f => seen.Add(Fingerprint(f.FullName)))   // identical copies show once
                .Select(f => f.FullName).ToList();
        }

        public static string Fingerprint(string path)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (var stream = File.OpenRead(path))
                    return BitConverter.ToString(md5.ComputeHash(stream));
            }
            catch (IOException) { return path; }
        }

        // removes a picture someone added; false when a profile still uses it
        public static bool DeletePicture(string path)
        {
            if (!IsKeptPicture(path) || store.Profiles.Any(p => string.Equals(AvatarFile(p), path, StringComparison.OrdinalIgnoreCase))) return false;
            var print = Fingerprint(path);
            // identical copies (shown as one) go too
            foreach (var other in Directory.GetFiles(Path.GetDirectoryName(path)))
                if (Fingerprint(other) == print && !store.Profiles.Any(p => string.Equals(AvatarFile(p), other, StringComparison.OrdinalIgnoreCase)))
                    File.Delete(other);
            return true;
        }

        public static bool IsKeptPicture(string path)
        {
            var folder = Path.Combine(Path.GetDirectoryName(file), "avatars");
            return path != null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }

        // a new file for a profile picture, next to profiles.json
        public static string NewPicturePath(string extension)
        {
            var folder = Path.Combine(Path.GetDirectoryName(file), "avatars");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
        }

        public static void SetTheme(string theme)
        {
            var p = Active;
            if (p == null) return;
            p.Theme = theme;
            Save();
            ApplyLook();
        }

        // null puts the theme's default wallpaper back
        public static void SetWallpaper(string wallpaper)
        {
            var p = Active;
            if (p == null) return;
            p.Wallpaper = wallpaper;
            Save();
            ApplyLook();
        }

        public static void SetHomeBackground(string look)
        {
            var p = Active;
            if (p == null) return;
            p.HomeBackground = look;
            Save();
            ApplyLook();
        }

        public static bool ToggleFavorite(Guid gameId)
        {
            var p = Active;
            if (p == null) return false;
            bool now = !p.Favorites.Remove(gameId);
            if (now) p.Favorites.Add(gameId);
            Save();
            HomeNav.QueueRowSync("favorite changed");   // the Favorites tab shows it without a filter switch (#39)
            return now;
        }

        // the signed-in profile's favorite / last played; Playnite's own value when nobody is signed in
        public static bool IsFavorite(Guid gameId, bool playniteFavorite)
        {
            var p = Active;
            return p != null ? p.Favorites.Contains(gameId) : playniteFavorite;
        }

        public static DateTime? LastPlayed(Guid gameId, DateTime? playniteLastActivity)
        {
            var p = Active;
            if (p == null) return playniteLastActivity;
            DateTime at;
            return p.LastPlayed.TryGetValue(gameId, out at) ? at : (DateTime?)null;
        }

        public static void RecordSession(Guid gameId, ulong seconds)
        {
            var p = Active;
            if (p == null) return;
            ulong total;
            p.Playtime.TryGetValue(gameId, out total);
            p.Playtime[gameId] = total + seconds;
            p.LastPlayed[gameId] = DateTime.Now;
            Save();
        }

        // colors and wallpaper of the signed-in profile (Selene's own look before anyone signs in)
        public static void ApplyLook()
        {
            var p = Active;
            var theme = p != null && !string.IsNullOrEmpty(p.Theme) ? p.Theme : ThemePalettes.DefaultId;
            // developer switch: %TEMP%\selene-dev-theme holding a theme id overrides it (to check every screen in each theme)
            var dev = Path.Combine(Path.GetTempPath(), "selene-dev-theme");
            if (File.Exists(dev) && File.ReadAllText(dev).Trim().Length > 0) theme = File.ReadAllText(dev).Trim();
            ThemePalettes.Apply(theme);
            var wallpaper = p != null && !string.IsNullOrEmpty(p.Wallpaper) ? p.Wallpaper : ThemePalettes.DefaultWallpaper(theme);
            Wallpapers.Show(wallpaper);
            LoginScene.RefreshBackdrop();
            // the Spotlight background setting: the picked game's art, or always the wallpaper
            ApplyHomeBackground(Features.Value("homebackground") == "wallpaper" ? "wallpaper" : "art");
            TopProfile.Refresh();
        }

        public static readonly string[] HomeBackgrounds = { "art", "wallpaper" };

        // anything but "wallpaper" (including the old "blend") is the game's art: theme\Constants.xaml's value
        public static void ApplyHomeBackground(string look)
        {
            HomeNav.PreviewChanged();
            var res = Application.Current.Resources;
            HomeNav.FadeArt(look == "wallpaper" ? 0 : 1);   // a smooth change instead of a jump
            res.Remove("SeleneArtOpacity");
            if (look == "wallpaper") res["SeleneArtOpacity"] = 0.0;
            LoginScene.RefreshBackdrop();   // the home's glow shows behind games too when their art is off
        }

        private static void Save()
        {
            if (loadError != null)
            {
                logger.Error("Selene Core: not saving profiles, they weren't loaded this session (" + loadError + ")");
                return;
            }
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, Serialization.ToJson(store, true));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }
    }
}
