// Playnite settings Selene changes for its own layout (the grid's cover shape, Fullscreen's rows / columns / titles) are shared
// with Desktop mode and every other theme. Selene used to leave its values behind, so a 1:1 grid became 2:3 everywhere (#105).
// Now the user's own value is written down the first time Selene changes one, and put back whenever Playnite runs without
// Selene (Desktop mode, another fullscreen theme) or when the Selene switch that needed it is turned off.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Selene
{
    public static class PlayniteLook
    {
        private static string File { get { return Path.Combine(SelenePaths.DataFolder, "playnite-look.txt"); } }

        // "AppSettings.GridItemWidthRatio" / "Fullscreen.Rows" -> the value the user had before Selene changed it
        private static Dictionary<string, string> Saved()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (System.IO.File.Exists(File))
                    foreach (var line in System.IO.File.ReadAllLines(File))
                    {
                        int at = line.IndexOf('=');
                        if (at > 0) map[line.Substring(0, at)] = line.Substring(at + 1);
                    }
            }
            catch { }
            return map;
        }

        private static void Write(Dictionary<string, string> map)
        {
            try
            {
                if (map.Count == 0) { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); return; }
                var lines = new List<string>();
                foreach (var kv in map) lines.Add(kv.Key + "=" + kv.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(File));
                System.IO.File.WriteAllLines(File, lines.ToArray());
            }
            catch (Exception ex) { HomeNav.Log("playnite look: can't save: " + ex.Message); }
        }

        // Selene sets a Playnite setting: the user's value is kept first (only the first time: later it's Selene's own)
        public static void Set(object owner, string ownerName, string property, object value)
        {
            if (owner == null) return;
            var p = owner.GetType().GetProperty(property);
            if (p == null || !p.CanWrite) return;
            try { value = Convert.ChangeType(value, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return; }
            var now = p.GetValue(owner, null);
            if (Equals(now, value)) return;
            var key = ownerName + "." + property;
            var map = Saved();
            if (!map.ContainsKey(key)) { map[key] = Convert.ToString(now, System.Globalization.CultureInfo.InvariantCulture); Write(map); }
            p.SetValue(owner, value, null);
            HomeNav.Log("playnite look: " + key + " " + now + " -> " + value);
        }

        // the user's own value back (Selene's switch for it turned off), if Selene ever changed it
        public static void Restore(object owner, string ownerName, string property)
        {
            var key = ownerName + "." + property;
            var map = Saved();
            string was;
            if (owner == null || !map.TryGetValue(key, out was)) return;
            if (Put(owner, property, was)) HomeNav.Log("playnite look: " + key + " back to " + was);
            map.Remove(key);
            Write(map);
        }

        // Playnite without Selene (Desktop mode, another theme): every value Selene changed goes back to the user's
        public static void RestoreAll(object appSettings)
        {
            var map = Saved();
            if (map.Count == 0 || appSettings == null) return;
            object fullscreen = null;
            try { fullscreen = appSettings.GetType().GetProperty("Fullscreen").GetValue(appSettings, null); } catch { }
            foreach (var kv in map)
            {
                int dot = kv.Key.IndexOf('.');
                if (dot < 0) continue;
                var owner = kv.Key.Substring(0, dot) == "Fullscreen" ? fullscreen : appSettings;
                if (owner != null && Put(owner, kv.Key.Substring(dot + 1), kv.Value)) HomeNav.Log("playnite look: " + kv.Key + " back to " + kv.Value + " (Playnite without Selene)");
            }
            Write(new Dictionary<string, string>());
        }

        private static bool Put(object owner, string property, string text)
        {
            try
            {
                var p = owner.GetType().GetProperty(property);
                if (p == null || !p.CanWrite) return false;
                var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                object value = type.IsEnum ? Enum.Parse(type, text) : Convert.ChangeType(text, type, System.Globalization.CultureInfo.InvariantCulture);
                p.SetValue(owner, value, null);
                return true;
            }
            catch { return false; }
        }

        // Playnite's application settings object (PlayniteApplication.Current.AppSettings), found loosely
        public static object AppSettings()
        {
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Playnite")
                    {
                        var appType = a.GetType("Playnite.PlayniteApplication");
                        var app = appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                        return app != null ? app.GetType().GetProperty("AppSettings").GetValue(app, null) : null;
                    }
            }
            catch { }
            return null;
        }
    }
}
