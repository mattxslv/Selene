using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace Selene
{
    public class ThemeInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Mode { get; set; }
        public string Accent { get; set; }
        public string Background { get; set; }
        public string Foreground { get; set; }
        public string Wallpaper { get; set; }
        // its colors, for the strip under the theme step's preview
        public List<string> Swatches { get; set; }
    }

    // Selene's color themes: theme\Palettes\<id>.xaml (made by tools/make_palettes.py) layered over theme\Constants.xaml
    public static class ThemePalettes
    {
        public const string DefaultId = "selene";
        private static readonly ILogger logger = LogManager.GetLogger();
        private static ResourceDictionary applied;
        private static string current;
        private static List<ThemeInfo> themes;

        // the theme list ships with the theme and doesn't change while Playnite runs, so it's read once
        public static List<ThemeInfo> All()
        {
            if (themes != null) return themes;
            var index = SelenePaths.ThemeFile("Palettes", "palettes.json");
            if (!File.Exists(index))
            {
                logger.Error("Selene Core: theme list missing at " + index);
                return new List<ThemeInfo>();
            }
            themes = Serialization.FromJsonFile<List<ThemeInfo>>(index) ?? new List<ThemeInfo>();
            return themes;
        }

        public static ThemeInfo Find(string id)
        {
            return All().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // absolute path of a theme's default wallpaper, "" when it has none (Selene's own look uses its gradient)
        public static string DefaultWallpaper(string id)
        {
            var theme = Find(id);
            if (theme == null || string.IsNullOrEmpty(theme.Wallpaper)) return "";
            return SelenePaths.ThemeFile(theme.Wallpaper.Split('/'));
        }

        // a theme's value for a key without applying it (the theme step's preview): its palette, else Selene's own constants
        private static readonly Dictionary<string, ResourceDictionary> peeked = new Dictionary<string, ResourceDictionary>(StringComparer.OrdinalIgnoreCase);
        public static object Peek(string id, string key)
        {
            if (!string.IsNullOrEmpty(id) && !string.Equals(id, DefaultId, StringComparison.OrdinalIgnoreCase))
            {
                ResourceDictionary colors;
                if (!peeked.TryGetValue(id, out colors))
                {
                    var file = SelenePaths.ThemeFile("Palettes", id + ".xaml");
                    try { if (File.Exists(file)) using (var stream = File.OpenRead(file)) colors = (ResourceDictionary)XamlReader.Load(stream); }
                    catch (Exception ex) { HomeNav.Log("theme: can't read " + file + ": " + ex.Message); }
                    peeked[id] = colors;
                }
                if (colors != null && colors.Contains(key)) return colors[key];
            }
            var merged = Application.Current.Resources.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(merged[i], applied)) continue;
                var v = merged[i][key];
                if (v != null) return v;
            }
            return Application.Current.TryFindResource(key);
        }

        // swaps the colors of the whole theme; DynamicResource users update at once.
        // Selene's own look is theme\Constants.xaml itself, so it just removes any override.
        public static void Apply(string id)
        {
            Application.Current.Resources["SeleneDefaultBackdrop"] = DefaultBackdrop.ForTheme(string.IsNullOrEmpty(id) ? DefaultId : id);
            if (string.IsNullOrEmpty(id)) id = DefaultId;
            // the same theme again does nothing (re-adding its colors redrew every screen: a flicker)
            if (string.Equals(id, current, StringComparison.OrdinalIgnoreCase)) return;
            current = id;
            HomeNav.PreviewChanged();
            var merged = Application.Current.Resources.MergedDictionaries;
            if (string.Equals(id, DefaultId, StringComparison.OrdinalIgnoreCase))
            {
                if (applied != null) merged.Remove(applied);
                applied = null;
                Ink.Refresh();
                HomeNav.Log("theme: " + id);
                return;
            }
            var known = Find(id);
            if (known != null) id = known.Id;
            var file = SelenePaths.ThemeFile("Palettes", id + ".xaml");
            if (!File.Exists(file))
            {
                logger.Error("Selene Core: theme '" + id + "' not found at " + file + ", keeping the current colors");
                HomeNav.Log("theme: missing " + file);
                return;
            }
            ResourceDictionary colors;
            using (var stream = File.OpenRead(file)) colors = (ResourceDictionary)XamlReader.Load(stream);
            if (applied != null) merged.Remove(applied);
            merged.Add(colors);
            applied = colors;
            Ink.Refresh();
            HomeNav.Log("theme: " + id);
        }
    }

    // Theme colors for screens built in code: brushes and stops made here follow theme changes live
    // (XAML gets the same through DynamicResource). Keys are the Color keys of theme\Constants.xaml.
    public static class Ink
    {
        private class Live
        {
            public WeakReference<DependencyObject> Target;
            public DependencyProperty Property;
            public string Key;
            public double Alpha;
        }

        private static readonly List<Live> live = new List<Live>();

        public static Color Of(string key, double alpha = 1)
        {
            var found = Application.Current != null ? Application.Current.TryFindResource(key) : null;
            var c = found is Color ? (Color)found : Colors.White;
            c.A = (byte)Math.Round(c.A * alpha);
            return c;
        }

        public static SolidColorBrush Brush(string key, double alpha = 1)
        {
            return Track(new SolidColorBrush(Of(key, alpha)), SolidColorBrush.ColorProperty, key, alpha);
        }

        public static GradientStop Stop(string key, double alpha, double offset)
        {
            return Track(new GradientStop(Of(key, alpha), offset), GradientStop.ColorProperty, key, alpha);
        }

        // keeps a Color property (a brush, gradient stop, shadow...) on a theme color
        public static T Track<T>(T target, DependencyProperty property, string key, double alpha = 1) where T : DependencyObject
        {
            target.SetValue(property, Of(key, alpha));
            live.Add(new Live { Target = new WeakReference<DependencyObject>(target), Property = property, Key = key, Alpha = alpha });
            return target;
        }

        public static void Refresh()
        {
            live.RemoveAll(l =>
            {
                DependencyObject target;
                if (!l.Target.TryGetTarget(out target)) return true;
                var freezable = target as Freezable;
                if (freezable != null && freezable.IsFrozen) return true;
                target.SetValue(l.Property, Of(l.Key, l.Alpha));
                return false;
            });
        }
    }
}
