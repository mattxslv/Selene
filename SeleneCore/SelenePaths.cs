using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace Selene
{
    // Every folder Selene Core uses, from its settings (no machine-specific paths in the code)
    public static class SelenePaths
    {
        private static Plugin plugin;
        private static IPlayniteAPI api;
        private static SeleneSettings settings;

        public static void Init(Plugin owner, IPlayniteAPI playnite, SeleneSettings s) { plugin = owner; api = playnite; settings = s; themeDir = null; }

        // downloaded art, preview clips, screenshots: the user's choice, else the add-on's own data folder
        public static string Media(string sub)
        {
            var root = settings != null && !string.IsNullOrWhiteSpace(settings.MediaFolder)
                ? settings.MediaFolder
                : Path.Combine(plugin != null ? plugin.GetPluginUserDataPath() : Path.GetTempPath(), "Media");
            var dir = Path.Combine(root, sub);
            if (Path.GetExtension(sub) == "") { try { Directory.CreateDirectory(dir); } catch { } }
            return dir;
        }

        // Selene Core's own data folder (profiles, downloaded helpers)
        public static string DataFolder { get { return plugin != null ? plugin.GetPluginUserDataPath() : Path.Combine(Path.GetTempPath(), "SeleneCore"); } }

        // folders scanned for games that no store knows about (empty = off)
        public static string[] GameFolders
        {
            get { return settings != null && settings.GameFolders != null ? settings.GameFolders.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray() : new string[0]; }
        }

        // the user's own wallpapers (searched with subfolders); empty = only the ones Selene ships
        public static System.Collections.Generic.List<string> CapturesFolders { get { return settings != null && settings.CapturesFolders != null ? settings.CapturesFolders : new System.Collections.Generic.List<string>(); } }
        public static string WallpapersFolder { get { return settings != null ? settings.WallpapersFolder ?? "" : ""; } }

        // optional companion program (TV/controller helper with admin rights); empty = those features are off
        // the companion program from the settings; never Selene's own retired helper (it refuses every request)
        public static string Helper
        {
            get
            {
                var path = settings != null ? settings.HelperPath ?? "" : "";
                return LegacyElevation.IsRetiredHelper(path) ? "" : path;
            }
        }

        // optional: game-streaming host logs (a connected streaming client changes some behaviour); empty = off
        public static string StreamLogs { get { return settings != null ? settings.StreamLogsFolder ?? "" : ""; } }

        public static string StreamClientMatch { get { return settings != null ? settings.StreamClientMatch ?? "" : ""; } }
        public static string StreamCredentials { get { return settings != null ? settings.StreamCredentialsFile ?? "" : ""; } }

        public static string SteamUserdata
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    {
                        var p = k != null ? k.GetValue("SteamPath") as string : null;
                        if (!string.IsNullOrEmpty(p)) return Path.Combine(p.Replace('/', '\\'), "userdata");
                    }
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "userdata");
            }
        }

        // the signed-in Steam account (loginusers.vdf, most recent): its avatar file and display name
        private static string steamId, steamName;
        private static void ReadSteamUser()
        {
            if (steamId != null) return;
            steamId = ""; steamName = "";
            try
            {
                var steam = Path.GetDirectoryName(SteamUserdata);
                var vdf = File.ReadAllText(Path.Combine(steam, "config", "loginusers.vdf"));
                foreach (Match m in Regex.Matches(vdf, "\"(\\d{17})\"\\s*\\{(.*?)\\}", RegexOptions.Singleline))
                {
                    var body = m.Groups[2].Value;
                    var name = Regex.Match(body, "\"PersonaName\"\\s*\"([^\"]*)\"");
                    bool recent = Regex.IsMatch(body, "\"MostRecent\"\\s*\"1\"");
                    if (steamId == "" || recent) { steamId = m.Groups[1].Value; steamName = name.Success ? name.Groups[1].Value : ""; }
                    if (recent) break;
                }
            }
            catch { }
        }

        public static string SteamId { get { ReadSteamUser(); return steamId; } }

        public static string AvatarImage
        {
            get
            {
                if (settings != null && !string.IsNullOrWhiteSpace(settings.AvatarPath)) return settings.AvatarPath;
                ReadSteamUser();
                if (steamId == "") return null;
                var p = Path.Combine(Path.GetDirectoryName(SteamUserdata), "config", "avatarcache", steamId + ".png");
                return File.Exists(p) ? p : null;
            }
        }

        public static string UserName
        {
            get
            {
                if (settings != null && !string.IsNullOrWhiteSpace(settings.DisplayName)) return settings.DisplayName;
                ReadSteamUser();
                return steamName != "" ? steamName : Environment.UserName;
            }
        }

        // where Playnite keeps its settings, library and themes: %AppData%\Playnite for an installed Playnite, the program
        // folder for a portable one (Playnite says which; the AppData guess is only for code running without the API)
        public static string PlayniteConfig
        {
            get
            {
                try { if (api != null && !string.IsNullOrEmpty(api.Paths.ConfigurationPath)) return api.Paths.ConfigurationPath; } catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite");
            }
        }

        // the fullscreen theme Playnite is set to (asked of Playnite; read from its file when the API can't say)
        public static string ActiveThemeId
        {
            get
            {
                try { var t = api != null ? api.ApplicationSettings.FullscreenTheme : null; if (!string.IsNullOrEmpty(t)) return t; } catch { }
                try
                {
                    var m = Regex.Match(File.ReadAllText(Path.Combine(PlayniteConfig, "fullscreenConfig.json")), "\"Theme\"\\s*:\\s*\"([^\"]+)\"");
                    return m.Success ? m.Groups[1].Value : "";
                }
                catch { return ""; }
            }
        }

        private static string themeDir;
        public static string ThemeDir
        {
            get
            {
                if (themeDir != null) return themeDir;
                var fs = Path.Combine(PlayniteConfig, "Themes", "Fullscreen");
                var id = ActiveThemeId;
                themeDir = id != "" && Directory.Exists(Path.Combine(fs, id)) ? Path.Combine(fs, id) : fs;
                return themeDir;
            }
        }

        public static string ThemeFile(params string[] parts) { return Path.Combine(new[] { ThemeDir }.Concat(parts).ToArray()); }

        // sounds come from the theme (it decides how Selene sounds)
        public static string ThemeAudio(string file) { return ThemeFile("Audio", file); }
    }
}
