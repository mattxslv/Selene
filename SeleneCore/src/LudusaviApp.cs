using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Plugins;

namespace Selene
{
    // The Ludusavi add-on needs the Ludusavi program itself (github.com/mtkennerly/ludusavi, MIT).
    // Selene Core downloads it into its own data folder and points the add-on at it, so save backups just work.
    public static class LudusaviApp
    {
        public static readonly Guid PluginId = Guid.Parse("72e2de43-d859-44d8-914e-4277741c8208");
        private static readonly ILogger logger = LogManager.GetLogger();

        private class Release
        {
            public string tag_name { get; set; }
            public List<Asset> assets { get; set; }
        }

        private class Asset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
        }

        public static string Exe { get { return Path.Combine(SelenePaths.DataFolder, "Ludusavi", "ludusavi.exe"); } }

        // the program the add-on will actually run, or null when that setting points at nothing usable
        private static string OnPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "ludusavi.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        // the add-on's ExecutablePath setting: live when it's running, else its settings file, else its default
        private static string ConfiguredPath(IPlayniteAPI api)
        {
            var addon = api.Addons.Plugins.FirstOrDefault(p => p.Id == PluginId);
            if (addon != null)
            {
                var settings = addon.GetSettings(false);
                var property = settings != null ? settings.GetType().GetProperty("ExecutablePath") : null;
                if (property != null) return property.GetValue(settings, null) as string;
            }
            var file = Path.Combine(api.Paths.ExtensionsDataPath, PluginId.ToString(), "config.json");
            if (File.Exists(file))
            {
                var config = Serialization.FromJsonFile<Dictionary<string, object>>(file);
                object value;
                if (config != null && config.TryGetValue("ExecutablePath", out value) && value is string) return (string)value;
            }
            return "ludusavi";
        }

        // true when the add-on's setting leads to a ludusavi.exe that exists (a bare name is looked up on PATH)
        public static bool Ready(IPlayniteAPI api)
        {
            var configured = ConfiguredPath(api);
            if (string.IsNullOrWhiteSpace(configured)) return false;
            if (configured.IndexOf('\\') < 0 && configured.IndexOf('/') < 0) return OnPath() != null;
            return File.Exists(configured);
        }

        // makes the add-on's setting lead to a working program: an existing copy on PATH, else Selene's own download.
        // Throws when downloading or pointing the add-on fails.
        public static void Setup(IPlayniteAPI api)
        {
            if (Ready(api)) return;
            var program = OnPath();
            if (program == null)
            {
                if (!File.Exists(Exe)) Download();
                program = Exe;
            }
            PointAddon(api, program);
            if (!Ready(api)) throw new InvalidOperationException("the Ludusavi add-on still doesn't point at " + program);
        }

        // the slow part of Setup (the download), safe to run off the UI thread; Setup then only points the add-on
        public static void DownloadIfNeeded(IPlayniteAPI api)
        {
            if (!Ready(api) && OnPath() == null && !File.Exists(Exe)) Download();
        }

        private static void Download()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "Selene-Core";
                var release = Serialization.FromJson<Release>(web.DownloadString("https://api.github.com/repos/mtkennerly/ludusavi/releases/latest"));
                var zip = release.assets.FirstOrDefault(a => a.name.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase));
                if (zip == null) throw new InvalidOperationException("Ludusavi " + release.tag_name + " has no Windows 64-bit download");
                if (!zip.browser_download_url.StartsWith("https://github.com/mtkennerly/ludusavi/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("unexpected Ludusavi download location " + zip.browser_download_url);

                var folder = Path.GetDirectoryName(Exe);
                var temp = Path.Combine(Path.GetTempPath(), zip.name);
                web.DownloadFile(zip.browser_download_url, temp);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                ZipFile.ExtractToDirectory(temp, folder);
                File.Delete(temp);
                if (!File.Exists(Exe)) throw new FileNotFoundException("ludusavi.exe not found in " + zip.name, Exe);
                logger.Info("Selene Core: installed Ludusavi " + release.tag_name + " to " + folder);
            }
        }

        // backups without the question: the add-on asks "Back up save data?" after every game unless told not to. Selene
        // sets it to just do it (its live settings when it's running, its file otherwise). The Automatic save backups switch.
        public static void Quiet(IPlayniteAPI api)
        {
            var quiet = new[] { "AskBackupOnGameStopped", "AskPlatformBackupOnNonPcGameStopped" };
            var addon = api.Addons.Plugins.FirstOrDefault(p => p.Id == PluginId);
            if (addon != null)
            {
                var settings = addon.GetSettings(false);
                if (settings == null) return;
                bool change = false;
                foreach (var name in quiet)
                {
                    var prop = settings.GetType().GetProperty(name);
                    if (prop != null && prop.PropertyType == typeof(bool) && Equals(prop.GetValue(settings, null), true)) change = true;
                }
                if (!change) return;
                settings.BeginEdit();
                foreach (var name in quiet)
                {
                    var prop = settings.GetType().GetProperty(name);
                    if (prop != null && prop.PropertyType == typeof(bool)) prop.SetValue(settings, false, null);
                }
                settings.EndEdit();
                logger.Info("Selene Core: Ludusavi backs up without asking now");
                return;
            }
            var file = Path.Combine(api.Paths.ExtensionsDataPath, PluginId.ToString(), "config.json");
            if (!File.Exists(file)) return;
            var config = Serialization.FromJsonFile<Dictionary<string, object>>(file);
            if (config == null) return;
            bool changed = false;
            foreach (var name in quiet) { object v; if (config.TryGetValue(name, out v) && v is bool && (bool)v) { config[name] = false; changed = true; } }
            if (changed) File.WriteAllText(file, Serialization.ToJson(config, true));
        }

        private static void PointAddon(IPlayniteAPI api, string program)
        {
            var addon = api.Addons.Plugins.FirstOrDefault(p => p.Id == PluginId);
            if (addon != null)
            {
                // running add-on: change its live settings so it saves our path instead of overwriting it later
                var settings = addon.GetSettings(false);
                var property = settings != null ? settings.GetType().GetProperty("ExecutablePath") : null;
                if (property == null) throw new InvalidOperationException("the Ludusavi add-on no longer has an ExecutablePath setting");
                settings.BeginEdit();
                property.SetValue(settings, program, null);
                settings.EndEdit();
                return;
            }
            // just installed, loads after the next restart: leave the path in its settings file
            var folder = Path.Combine(api.Paths.ExtensionsDataPath, PluginId.ToString());
            var file = Path.Combine(folder, "config.json");
            Directory.CreateDirectory(folder);
            var config = File.Exists(file) ? Serialization.FromJsonFile<Dictionary<string, object>>(file) : new Dictionary<string, object>();
            config["ExecutablePath"] = program;
            File.WriteAllText(file, Serialization.ToJson(config, true));
        }
    }
}
