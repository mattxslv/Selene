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
            return days <= 0 ? "Today" : days == 1 ? "Yesterday" : days < 7 ? days + " days ago" : when.Value.ToLocalTime().ToString("MMM d, yyyy");
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
                if (g == null) return null;
                var square = System.IO.Path.Combine(SelenePaths.Media("Square"), g.Id + ".jpg");
                return System.IO.File.Exists(square) ? square : null;
            }
            catch { return null; }
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

        // a cover-sized tile for a game: its cover as the face, or its name when it has none
        public static Tile CoverTile(Game g, double w, double h)
        {
            var t = new Tile(w, h, 12, 5) { FocusScale = 1.06 };
            if (CoverPath(g) == null)
                t.Face.Child = new System.Windows.Controls.TextBlock
                {
                    Text = g.Name, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = System.Windows.TextWrapping.Wrap,
                    TextAlignment = System.Windows.TextAlignment.Center, Margin = new System.Windows.Thickness(10), VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
            return t;
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
                Ui.Fade(fill, 1, 240);
            });
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
