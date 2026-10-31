using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Selene
{
    // A single still from the bundled backdrop video, beneath game art and the existing ambient player.
    // Keeps previews and selection gaps consistent without another video decoder or timer.
    internal static class DefaultBackdrop
    {
        private static System.Windows.Media.ImageSource picture;
        public static string FileForTheme(string id)
        {
            if (!Features.On("defaultbackdrop") || !string.Equals(id, ThemePalettes.DefaultId, StringComparison.OrdinalIgnoreCase)) return "";
            return SelenePaths.ThemeFile("Images", "SeleneDefaultBackground.jpg");
        }
        public static System.Windows.Media.ImageSource ForTheme(string id)
        {
            var file = FileForTheme(id);
            if (file == "") return null;
            return picture ?? (picture = Wallpapers.Load(file));
        }
    }

    public class WallpaperChoice
    {
        public string File { get; set; }
        // the theme it came with, or "Your wallpapers"
        public string Group { get; set; }
    }

    // Every wallpaper the background picker offers: Selene's own, grouped by theme in theme-list order, then the user's folder
    public static class Wallpapers
    {
        public const string YourGroup = "Your wallpapers";
        private static readonly Playnite.SDK.ILogger logger = Playnite.SDK.LogManager.GetLogger();
        private static readonly string[] Pictures = { ".jpg", ".jpeg", ".png", ".bmp" };

        // a wallpaper as a picture for the screens (null when there's none or it can't be read)
        public static System.Windows.Media.ImageSource Load(string file, int width = 1920)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            try
            {
                // never enlarged while decoding (a 64 px icon decoded at 300 came out smeared); the screen scales it instead
                using (var fs = File.OpenRead(file))
                {
                    var head = System.Windows.Media.Imaging.BitmapFrame.Create(fs, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                    if (head.PixelWidth > 0 && head.PixelWidth <= width) width = 0;
                }
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = width;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                logger.Warn("Selene Core: can't load wallpaper " + file + ": " + ex.Message);
                return null;
            }
        }

        // puts a wallpaper behind Selene; the one already showing is left alone (a reloaded copy flickered)
        private static string shown;
        public static string Current { get { return shown; } }   // the wallpaper behind Selene now

        public static void Show(string file)
        {
            var res = System.Windows.Application.Current.Resources;
            if (shown != null && string.Equals(shown, file ?? "", StringComparison.OrdinalIgnoreCase)) return;
            shown = file ?? "";
            HomeNav.PreviewChanged();
            res["SeleneWallpaperPath"] = file;
            var pic = Cached(file) ?? Load(file);
            Remember(file, pic);
            res["SeleneWallpaper"] = pic;
            Contrast.Wallpaper(pic);   // the home's shades as deep as this picture needs
        }

        // the last few wallpapers decoded, so browsing back to one is instant (a 4K picture took ~50-100 ms to decode)
        private static readonly List<KeyValuePair<string, System.Windows.Media.ImageSource>> recent = new List<KeyValuePair<string, System.Windows.Media.ImageSource>>();

        // a wallpaper decoded already (Prepare first), else null
        public static System.Windows.Media.ImageSource Decoded(string file) { return Cached(file); }

        private static System.Windows.Media.ImageSource Cached(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            foreach (var p in recent) if (string.Equals(p.Key, file, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }

        private static void Remember(string file, System.Windows.Media.ImageSource pic)
        {
            if (string.IsNullOrEmpty(file) || pic == null) return;
            recent.RemoveAll(p => string.Equals(p.Key, file, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, new KeyValuePair<string, System.Windows.Media.ImageSource>(file, pic));
            if (recent.Count > 8) recent.RemoveAt(recent.Count - 1);
        }

        // decodes a wallpaper on a background thread, then calls ready on the UI thread (at once when it's decoded already), so
        // Show is instant: decoding on the UI thread stalled the profile steps' glides. Only the latest request calls back.
        private static int prepareGeneration;
        public static void Prepare(string file, Action ready)
        {
            int mine = ++prepareGeneration;
            if (string.IsNullOrEmpty(file) || Cached(file) != null) { ready(); return; }
            var ui = System.Windows.Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                var pic = Load(file);   // frozen: safe to hand to the UI thread
                ui.BeginInvoke(new Action(() =>
                {
                    Remember(file, pic);
                    if (mine == prepareGeneration) ready();
                }));
            });
        }

        public static List<WallpaperChoice> All()
        {
            var list = new List<WallpaperChoice>();
            var shipped = SelenePaths.ThemeFile("Wallpapers");
            if (Directory.Exists(shipped))
            {
                var order = ThemePalettes.All().Select(t => t.Id).ToList();
                var folders = Directory.GetDirectories(shipped)
                    .OrderBy(d => { int i = order.IndexOf(Path.GetFileName(d)); return i < 0 ? int.MaxValue : i; })
                    .ThenBy(d => d);
                foreach (var folder in folders)
                {
                    var theme = ThemePalettes.Find(Path.GetFileName(folder));
                    var group = theme != null ? theme.Name : Path.GetFileName(folder);
                    list.AddRange(PicturesIn(folder, SearchOption.TopDirectoryOnly).Select(f => new WallpaperChoice { File = f, Group = group }));
                }
            }
            var own = SelenePaths.WallpapersFolder;
            if (own != "" && Directory.Exists(own))
                list.AddRange(PicturesIn(own, SearchOption.AllDirectories).Select(f => new WallpaperChoice { File = f, Group = YourGroup }));
            return list;
        }

        private static IEnumerable<string> PicturesIn(string folder, SearchOption depth)
        {
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                try
                {
                    found.AddRange(Directory.GetFiles(current).Where(f => Pictures.Contains(Path.GetExtension(f).ToLowerInvariant())));
                    if (depth == SearchOption.AllDirectories)
                        foreach (var sub in Directory.GetDirectories(current)) pending.Push(sub);
                }
                catch (UnauthorizedAccessException) { logger.Warn("Selene Core: no access to " + current + ", skipping it for wallpapers"); }
                catch (PathTooLongException) { logger.Warn("Selene Core: path too long, skipping " + current + " for wallpapers"); }
                catch (IOException ex) { logger.Warn("Selene Core: can't read " + current + " for wallpapers: " + ex.Message); }
            }
            return found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
    }
}
