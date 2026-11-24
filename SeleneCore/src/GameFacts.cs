using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Playnite.SDK.Models;

namespace Selene
{
    // Shared bits for the card screens (Activity, Surprise me): a game's cover decoded small, its play time and last
    // played as the signed-in profile sees them, and those as people say them
    internal static class GameFacts
    {
        public static List<Game> Games()
        {
            if (HomeNav.Api == null) return new List<Game>();
            try { return HomeNav.Api.Database.Games.Where(g => g != null && !string.IsNullOrEmpty(g.Name) && HomeNav.OnSpotlight(g)).ToList(); }   // (as Spotlight: a pick from a card lands there)
            catch (Exception ex) { HomeNav.Log("games: " + ex.Message); return new List<Game>(); }
        }

        public static Game Find(Guid id)
        {
            try { return HomeNav.Api != null ? HomeNav.Api.Database.Games.Get(id) : null; }
            catch { return null; }
        }

        // the profile's own play time when it has played the game, else Playnite's total
        public static ulong Seconds(Game g)
        {
            var p = Profiles.Active;
            ulong s;
            if (p != null && p.Playtime != null && p.Playtime.TryGetValue(g.Id, out s)) return s;
            return g.Playtime;
        }

        // the profile's own last played when it has played the game, else Playnite's (a new profile would show only today)
        public static DateTime? LastPlayed(Game g)
        {
            var p = Profiles.Active;
            DateTime at;
            if (p != null && p.LastPlayed != null && p.LastPlayed.TryGetValue(g.Id, out at)) return at;
            return g.LastActivity;
        }

        public static string Hours(ulong seconds)
        {
            if (seconds == 0) return "Not played";
            if (seconds < 60) return "Under a minute";
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 100) return (int)t.TotalHours + " h";
            return t.TotalHours >= 1 ? (int)t.TotalHours + "h " + t.Minutes + "m" : t.Minutes + "m";
        }

        public static string Ago(DateTime? when)
        {
            if (when == null) return "Never";
            int days = (int)(DateTime.Now.Date - when.Value.ToLocalTime().Date).TotalDays;
            return days <= 0 ? "Today" : days == 1 ? "Yesterday" : days < 7 ? days + " days ago" : Date(when.Value.ToLocalTime(), "MMM d, yyyy");
        }

        // A date in the language Selene shows (English, or the translation in use), on the Gregorian calendar. Formatted with the
        // PC's own culture, a Thai PC got Thai month names inside English text and Buddhist-era years (2569) (#45).
        public static string Date(DateTime when, string pattern) { return when.ToString(pattern, DateCulture()); }

        private static System.Globalization.CultureInfo dateCulture;
        private static System.Globalization.CultureInfo DateCulture()
        {
            if (dateCulture != null) return dateCulture;
            System.Globalization.CultureInfo c;
            try { c = Lang.Active && !string.IsNullOrEmpty(Lang.Language) ? new System.Globalization.CultureInfo(Lang.Language.Replace('_', '-')) : new System.Globalization.CultureInfo("en-US"); }
            catch { c = new System.Globalization.CultureInfo("en-US"); }
            if (!(c.Calendar is System.Globalization.GregorianCalendar))
            {
                c = (System.Globalization.CultureInfo)c.Clone();
                foreach (var cal in c.OptionalCalendars) if (cal is System.Globalization.GregorianCalendar) { c.DateTimeFormat.Calendar = cal; break; }
            }
            return dateCulture = c;
        }

        public static string CoverPath(Game g)
        {
            try
            {
                if (g == null || string.IsNullOrEmpty(g.CoverImage) || HomeNav.Api == null) return null;
                var path = HomeNav.Api.Database.GetFullFilePath(g.CoverImage);
                return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        // a square picture of the game: Selene's own square art (the media folder), or null (callers use the cover then; the
        // game's icon came first before, a blurry little picture on a new install, which has no square art)
        public static string SquarePath(Game g)
        {
            try
            {
                if (g == null || !Features.On("squareart")) return null;   // (switched off: the cover, as on the tiles)
                var square = System.IO.Path.Combine(SelenePaths.Media("Square"), g.Id + ".jpg");
                return System.IO.File.Exists(square) ? square : null;
            }
            catch { return null; }
        }

        // Selene's downloaded picture of a game, unless the player's own Playnite background comes first (Look > Own backgrounds first: #153)
        public static string HeroPath(Game g)
        {
            if (g == null) return null;
            if (Features.On("ownbackground") && BackgroundPath(g) != null) return null;
            var hero = System.IO.Path.Combine(SelenePaths.Media("Hero"), g.Id + ".jpg");
            return System.IO.File.Exists(hero) ? hero : null;
        }

        // The theme's hero picture follows a hidden text holding its path (Spotlight and the game page). With Own backgrounds first,
        // a game with its own background gets no path, which the theme already handles as "no hero art".
        public static void BindHeroPath(System.Windows.Controls.TextBlock pathText, string idPath)
        {
            if (pathText == null) return;
            var path = new System.Windows.Data.MultiBinding { Converter = new HeroPathConverter() };
            path.Bindings.Add(new System.Windows.Data.Binding("Tag") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self) });
            path.Bindings.Add(new System.Windows.Data.Binding(idPath));
            pathText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, path);
        }

        private class HeroPathConverter : System.Windows.Data.IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                var root = values.Length > 0 ? values[0] as string : null;
                if (string.IsNullOrEmpty(root)) return "";
                object id = values.Length > 1 ? values[1] : null;
                if (id is Guid && Features.On("ownbackground") && HomeNav.Api != null && BackgroundPath(HomeNav.Api.Database.Games.Get((Guid)id)) != null) return "";
                return root + "\\Hero\\" + id + ".jpg";
            }
            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) { throw new NotSupportedException(); }
        }

        public static string BackgroundPath(Game g)
        {
            try
            {
                if (g == null || string.IsNullOrEmpty(g.BackgroundImage) || HomeNav.Api == null) return null;
                var path = HomeNav.Api.Database.GetFullFilePath(g.BackgroundImage);
                return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        public static ImageSource Cover(Game g, int width)
        {
            var path = CoverPath(g);
            if (path == null) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = width;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // a cover-sized tile for a game: its cover as the face, or Selene's default cover with its name when it has none
        public static Tile CoverTile(Game g, double w, double h)
        {
            var t = new Tile(w, h, 12, 5) { FocusScale = 1.06 };
            if (CoverPath(g) == null) t.Face.Child = StandIn(g.Name, 11, 15);
            return t;
        }

        // a game without a cover (a fresh install has no art yet): Selene's default cover, its name over a shade at the bottom
        // (the flat grey card it was read as broken)
        public static System.Windows.FrameworkElement StandIn(string name, double radius, double size)
        {
            var g = new System.Windows.Controls.Grid();
            var art = System.Windows.Application.Current != null ? System.Windows.Application.Current.TryFindResource("DefaultGameCover") as ImageSource : null;
            var back = new System.Windows.Controls.Border { CornerRadius = new System.Windows.CornerRadius(radius) };
            if (art != null) back.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            else back.Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneFocusColor", 0.45, 0), Ink.Stop("SeleneGlassColor", 0.9, 1) }, 60);
            g.Children.Add(back);
            g.Children.Add(new System.Windows.Controls.Border
            {
                CornerRadius = new System.Windows.CornerRadius(radius),
                Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0, 0.35), Ink.Stop("SeleneScrimColor", 0.85, 1) }, 90)
            });
            g.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = name, FontSize = size, FontWeight = System.Windows.FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"),
                TextWrapping = System.Windows.TextWrapping.Wrap, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, MaxHeight = size * 4.2,
                TextAlignment = System.Windows.TextAlignment.Center, Margin = new System.Windows.Thickness(size * 0.7), VerticalAlignment = System.Windows.VerticalAlignment.Bottom
            });
            return g;
        }

        // a soft glow in the theme's focus color behind a card screen, so a game without art sits on the theme instead of
        // plain black (art covers it when there is some)
        public static System.Windows.Controls.Border Tint()
        {
            return new System.Windows.Controls.Border
            {
                IsHitTestVisible = false,
                Background = new RadialGradientBrush(new GradientStopCollection { Ink.Stop("SeleneFocusColor", 0.28, 0), Ink.Stop("SeleneFocusColor", 0.08, 0.6), Ink.Stop("SeleneFocusColor", 0, 1) })
                { Center = new System.Windows.Point(0.72, 0.3), GradientOrigin = new System.Windows.Point(0.72, 0.3), RadiusX = 0.8, RadiusY = 0.9 }
            };
        }

        // the picture behind a game on a card screen: its background, else its cover decoded tiny (scaled up it's a soft blur
        // of its colors, without the cost of a blur effect); small says which, for the decode width
        public static string BackdropPath(Game g, out bool small)
        {
            small = false;
            var path = BackgroundPath(g);
            if (path != null) return path;
            path = CoverPath(g);
            small = path != null;
            return path;
        }

        // the covers of a row of tiles, decoded one after another off the UI thread
        public static void FillCovers(IList<Game> games, IList<Tile> tiles, int width, bool wholeCover = false)
        {
            var files = games.Select(CoverPath).ToList();
            var want = new List<string>();
            var at = new List<int>();
            for (int i = 0; i < files.Count; i++) if (files[i] != null) { want.Add(files[i]); at.Add(i); }
            if (want.Count == 0) return;
            Ui.LoadInOrder(want, width, (i, pic) =>
            {
                int idx = at[i];
                if (idx >= tiles.Count) return;
                var fill = CoverFace(pic, wholeCover);
                tiles[idx].Face.Child = fill;
                if (wholeCover) FitToCover(tiles[idx], pic);
                Ui.Fade(fill, 1, 240);
            });
        }

        // a whole cover: the card takes the cover's shape inside its slot (a square or wide cover sat in a tall card with
        // black bars, #195). It sits at the slot's bottom, centred across, so the row and the lines under it stay put.
        private static void FitToCover(Tile t, ImageSource picture)
        {
            var root = t.Root;
            double slotW = root.Width, slotH = root.Height;
            if (double.IsNaN(slotW) || double.IsNaN(slotH) || picture.Width <= 0 || picture.Height <= 0) return;
            double ratio = picture.Width / picture.Height;
            double w = slotW, h = slotW / ratio;
            if (h > slotH) { h = slotH; w = slotH * ratio; }
            if (Math.Abs(w - slotW) < 1 && Math.Abs(h - slotH) < 1) return;
            var from = root.Margin;
            var to = new System.Windows.Thickness(from.Left + (slotW - w) / 2, from.Top + slotH - h, from.Right + (slotW - w) / 2, from.Bottom);
            root.Width = w; root.Height = h; root.Margin = to;
            var time = TimeSpan.FromMilliseconds(220);
            root.BeginAnimation(System.Windows.FrameworkElement.WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(slotW, w, time) { EasingFunction = Ui.Out() });
            root.BeginAnimation(System.Windows.FrameworkElement.HeightProperty, new System.Windows.Media.Animation.DoubleAnimation(slotH, h, time) { EasingFunction = Ui.Out() });
            root.BeginAnimation(System.Windows.FrameworkElement.MarginProperty, new System.Windows.Media.Animation.ThicknessAnimation(from, to, time) { EasingFunction = Ui.Out() });
        }

        private static System.Windows.Controls.Border CoverFace(ImageSource picture, bool wholeCover)
        {
            return new System.Windows.Controls.Border { CornerRadius = new System.Windows.CornerRadius(11), Background = new ImageBrush(picture) { Stretch = wholeCover ? Stretch.Uniform : Stretch.UniformToFill }, Opacity = 0 };
        }

        // the numbers on the Activity card: games played in the last seven days, and all the play time
        public static void CardStats()
        {
            try
            {
                var games = Games();
                int week = 0; ulong total = 0;
                foreach (var g in games)
                {
                    total += Seconds(g);
                    var when = LastPlayed(g);
                    if (when.HasValue && when.Value > DateTime.Now.AddDays(-7)) week++;
                }
                var res = System.Windows.Application.Current.Resources;
                res["SeleneStatWeek"] = week == 0 ? "None yet" : week == 1 ? "1 game" : week + " games";
                res["SeleneStatHours"] = total == 0 ? "0 h" : total < 3600 ? "Under an hour" : (int)TimeSpan.FromSeconds(total).TotalHours + " h";
                ActivityScreen.FillCard(games, week, total);
                SurpriseScreen.FillCard(games);
                AchievementsHub.FillCard();
            }
            catch (Exception ex) { HomeNav.Log("activity stats: " + ex.Message); }
        }
    }
}
