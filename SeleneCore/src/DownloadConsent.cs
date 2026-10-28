using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Selene
{
    // Stored permission is separate from old defaults. Upgrades cannot turn inherited defaults into consent.
    public static class DownloadConsent
    {
        private const string Version = "explicit-media-v1";
        public static bool IsControlled(string key)
        { return key == "artdownload" || key == "songsetup" || key == "screenshotsdownload" || key == "trailerdownload" || key == "metadatacomplete"; }
        public static bool Initialize(SeleneSettings settings)
        {
            if (settings.Values == null) settings.Values = new Dictionary<string, string>();
            string value;
            if (settings.Values.TryGetValue("media-download-policy", out value) && value == Version) return false;
            foreach (var key in new[] { "artdownload", "songsetup", "screenshotsdownload", "metadatacomplete" }) Features.Mark(settings, key, false);
            settings.Values["trailerdownload"] = "off";
            settings.Values["media-download-policy"] = Version;
            return true;
        }
        private static string Token(string key) { return key == "trailerdownload" ? Version + ":" + Features.Value(key) : Version; }
        public static bool Allowed(string key)
        {
            if (!IsControlled(key)) return false;
            bool enabled = key == "trailerdownload" ? Features.Value(key) != "off" : Features.On(key);
            return enabled && Features.Remembered("consent-" + key) == Token(key);
        }
        public static void Grant(string key)
        {
            if (!IsControlled(key)) throw new ArgumentException("Unknown download category.");
            Features.Remember("consent-" + key, Token(key));
        }
        public static void Revoke(string key) { Features.Remember("consent-" + key, ""); }
        public static string Details(string key)
        {
            var config = HomeNav.Api != null ? HomeNav.Api.Paths.ConfigurationPath : SelenePaths.PlayniteConfig;
            if (key == "metadatacomplete")
            {
                var files = HomeNav.Api != null ? Path.GetDirectoryName(HomeNav.Api.Database.GetFullFilePath("Selene-location-check")) : Path.Combine(config, "library", "files");
                return "Fill missing details for your library using metadata add-ons and Steam. This can download covers and backgrounds. Existing details are kept.\n\nMedia is stored in your Playnite library:\n" + files;
            }
            if (key == "songsetup") return "Download missing songs for your whole library through UniPlaySong.\n\n" +
                "This also downloads yt-dlp and ffmpeg if needed (about 130 MB of tools; songs use additional space).\n\n" +
                "Songs: " + Path.Combine(config, "ExtraMetadata", "UniPlaySong", "Games") + "\nTools: " + Path.Combine(SelenePaths.DataFolder, "Tools") +
                "\n\nThis requests one library batch. Automatic library/install triggers are disabled for Selene-managed tools. UniPlaySong manages its active queue; independent add-on setups have separate controls.";
            if (key == "artdownload") return "Download missing logos and backgrounds for games you select, plus sample art when you open appearance previews. Existing files stay available while downloads are off.\n\n" +
                "Logos: " + Path.Combine(config, "ExtraMetadata", "games") + "\nBackgrounds: " + SelenePaths.Media("Hero") + "\nPreview samples: " + SelenePaths.Media("Sample");
            if (key == "screenshotsdownload") return "Download screenshots when you select a game. No whole-library background download.\n\nSaved to:\n" + SelenePaths.Media("Screenshots-v2");
            int count = HomeNav.Api != null ? HomeNav.Api.Database.Games.Count(g => !g.Hidden) : 0;
            var estimate = Features.Value("trailerdownload") == "all" && count > 0 ? "\nUp to " + count + " games: roughly " + (count * 20.0 / 1024).ToString("0.0") + " GB if every trailer is missing. Actual sizes vary." : "";
            return "Download missing trailers from Steam for " + (Features.Value("trailerdownload") == "all" ? "your whole library" : "games you select") +
                ". Each trailer can use about 20 MB." + estimate + "\n\nSaved to:\n" + Path.Combine(config, "ExtraMetadata", "games") +
                "\n\nYou can switch downloads off later. An active transfer may finish; further requests stop.";
        }
        public static void Changed(string key)
        {
            if (key == "metadatacomplete") GameInfo.CompleteLater();
            else if (key == "songsetup") SongSetup.DownloadSettingsChanged();
            else if (key == "screenshotsdownload") GameMedia.DownloadSettingsChanged();
            else { TrailerFetch.DownloadSettingsChanged(); GameMedia.KickDownloads(); }
        }
    }
}
