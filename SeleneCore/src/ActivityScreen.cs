using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace Selene
{
    // The Activity card: what you've been playing. The numbers in a row under the title, your top game's logo at the right,
    // then rows of covers across the screen (each slides along with the focus): most played (with a bar for its share),
    // played last, games to pick up again, and the ones still waiting. The focused game's art crossfades in behind.
    // A shows the game on Spotlight, X plays it, B goes back.
    public class ActivityScreen : Screen
    {
        private const double Side = 96, TileW = 150, TileH = 200, Pitch = TileW + 22, RowTop = 330, RowPitch = 318;
        private const int PerRow = 20, Visible = 2, Across = 10;   // games a row holds; rows and covers on screen at once
        private readonly List<List<Game>> rows = new List<List<Game>>();
        private readonly List<List<Tile>> tiles = new List<List<Tile>>();
        private readonly List<string> rowNames = new List<string>();
        private readonly List<TranslateTransform> rowShift = new List<TranslateTransform>();
        private readonly List<int> rowAlong = new List<int>();   // covers each row has slid by (kept when the focus leaves it)
        private readonly Grid page = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly TranslateTransform pageShift = new TranslateTransform();
        private readonly Image hero = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
        private readonly Image heroBack = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };   // the art going, under the one coming
        private readonly DispatcherTimer heroLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        private int zone, col, firstRow;
        private bool closed;
        // the panel at the right: the focused game's logo and numbers
        private const double CallW = 520;
        private readonly StackPanel call = new StackPanel { MinWidth = CallW - 80, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 62, Side - 30, 0) };
        private readonly TextBlock callLabel = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Grid callArt = new Grid { Height = 100, Margin = new Thickness(0, 10, 0, 0) };
        private readonly StackPanel callNums = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        private ulong totalSecs;
        private List<Game> mostList = new List<Game>();
        private Game callShown;
        private readonly StackPanel callMore = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };   // sessions, trophies
        private string heroShown;

        public ActivityScreen()
        {
            var dark = new Border();
            dark.SetResourceReference(Border.BackgroundProperty, "SeleneScrimBrush");
            Panel.SetZIndex(dark, -10);
            Root.Children.Add(dark);
            RenderOptions.SetBitmapScalingMode(hero, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(heroBack, BitmapScalingMode.HighQuality);
            Panel.SetZIndex(heroBack, -6);
            Root.Children.Add(heroBack);
            Panel.SetZIndex(hero, -5);
            Root.Children.Add(hero);
            // the art stays behind the words: dark on the left, along the bottom and a soft vignette at the edges
            var shade = new Border { IsHitTestVisible = false, Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0.94, 0), Ink.Stop("SeleneScrimColor", 0.72, 0.5), Ink.Stop("SeleneScrimColor", 0.3, 1) }, 0) };
            Panel.SetZIndex(shade, -4);
            Root.Children.Add(shade);
            var shade2 = new Border { IsHitTestVisible = false, Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0, 0.25), Ink.Stop("SeleneScrimColor", 0.88, 1) }, 90) };
            Panel.SetZIndex(shade2, -4);
            Root.Children.Add(shade2);
            var vignette = new Border { IsHitTestVisible = false, Background = new RadialGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0, 0.55), Ink.Stop("SeleneScrimColor", 0.6, 1) }) { RadiusX = 0.8, RadiusY = 0.9 } };
            Panel.SetZIndex(vignette, -3);
            Root.Children.Add(vignette);

            var games = GameFacts.Games();
            var played = games.Where(g => GameFacts.Seconds(g) > 0).ToList();
            var most = played.OrderByDescending(GameFacts.Seconds).Take(PerRow).ToList();
            var recent = games.Where(g => GameFacts.LastPlayed(g).HasValue).OrderByDescending(g => GameFacts.LastPlayed(g).Value).Take(PerRow).ToList();
            var again = games.Where(g => GameFacts.Seconds(g) >= 3600 && GameFacts.LastPlayed(g).HasValue && GameFacts.LastPlayed(g).Value < DateTime.Now.AddDays(-45))
                .OrderByDescending(GameFacts.Seconds).Take(PerRow).ToList();
            var waiting = games.Where(g => GameFacts.Seconds(g) == 0 && !GameFacts.LastPlayed(g).HasValue).OrderByDescending(g => g.IsInstalled).ThenByDescending(g => g.Added ?? DateTime.MinValue).Take(PerRow).ToList();
            int week = games.Count(g => { var w = GameFacts.LastPlayed(g); return w.HasValue && w.Value > DateTime.Now.AddDays(-7); });
            int month = games.Count(g => { var w = GameFacts.LastPlayed(g); return w.HasValue && w.Value > DateTime.Now.AddDays(-30); });
            ulong total = 0; foreach (var g in games) total += GameFacts.Seconds(g);
            int installed = games.Count(g => g.IsInstalled);
            int neverPlayed = games.Count(g => GameFacts.Seconds(g) == 0 && !GameFacts.LastPlayed(g).HasValue);

            // header: the title, then the numbers in a row
            var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side, 56, 0, 0) };
            head.Children.Add(new TextBlock { Text = "Activity", FontSize = 44, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor") });
            var stats = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 72, 0, 0) };   // (centered between the title and the first row)
            stats.Children.Add(Stat(week.ToString(), week == 1 ? "GAME THIS WEEK" : "GAMES THIS WEEK", true));
            stats.Children.Add(Stat(month.ToString(), month == 1 ? "GAME THIS MONTH" : "GAMES THIS MONTH", false));
            stats.Children.Add(Stat(total < 3600 ? GameFacts.Hours(total) : (int)TimeSpan.FromSeconds(total).TotalHours + " h", "PLAY TIME", false));
            stats.Children.Add(Stat(played.Count.ToString(), "GAMES PLAYED", false));
            stats.Children.Add(Stat(games.Count.ToString(), installed + " INSTALLED  \u00B7  " + neverPlayed + " NEVER PLAYED", false));
            head.Children.Add(stats);
            Root.Children.Add(head);
            Ui.Rise(head, 40, 14, 320);

            // the focused game at the right (your top game to begin with): label, logo and its numbers, centered
            totalSecs = total;
            mostList = most;
            call.Children.Add(callLabel);
            call.Children.Add(callArt);
            call.Children.Add(callNums);
            call.Children.Add(callMore);
            Root.Children.Add(call);
            if (most.Count > 0 && total > 0) { FillCall(most[0], 0); Ui.Rise(call, 120, 14, 320); }
            // the trophies read once in the background: the game showing gets its line when they're in
            if (Features.On("achievements") && AchievementsData.Available() && !AchievementsData.HasCache)
                AchievementsData.All(sets => { if (closed || callShown == null) return; var shownNow = callShown; callShown = null; FillCall(shownNow, zone); });

            // this week day by day, and the streak (GameActivity's sessions), between the numbers and the panel
            if (Sessions.All().Count > 0) { var chart = WeekChart(Sessions.ThisWeek(), Sessions.Streak()); Root.Children.Add(chart); Ui.Rise(chart, 80, 14, 320); }

            // the rows, on a page that glides up (two rows at a time)
            var area = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, RowTop - 30, 0, 0), Height = Visible * RowPitch + 60, ClipToBounds = true };
            double h = Visible * RowPitch + 60;
            var fade = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, h) };
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 22 / h));
            fade.GradientStops.Add(new GradientStop(Colors.Black, 30 / h));
            fade.GradientStops.Add(new GradientStop(Colors.Black, (h - 50) / h));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
            area.OpacityMask = fade;
            page.RenderTransform = pageShift;
            page.Margin = new Thickness(0, 30, 0, 0);
            area.Children.Add(new Canvas { Children = { page } });
            Root.Children.Add(area);
            // this month so far, like a year's wrap-up in small: its games by time this month, the title saying how it went
            var monthNow = Sessions.Month(DateTime.Today);
            if (monthNow.Count > 0)
            {
                var monthGames = monthNow.OrderByDescending(m => m.Value).Select(m => GameFacts.Find(m.Key)).Where(m => m != null).Take(PerRow).ToList();
                ulong monthSecs = 0; foreach (var v in monthNow.Values) monthSecs += v;
                ulong beforeSecs = 0; foreach (var v in Sessions.Month(DateTime.Today.AddMonths(-1)).Values) beforeSecs += v;
                string about = GameFacts.Hours(monthSecs);
                if (beforeSecs > 0)
                {
                    int diff = (int)Math.Round(100.0 * ((double)monthSecs - beforeSecs) / beforeSecs);
                    string before = DateTime.Today.AddMonths(-1).ToString("MMMM");
                    about += "  \u00B7  " + (diff == 0 ? "as much as " + before : Math.Abs(diff) + "% " + (diff > 0 ? "more" : "less") + " than " + before);
                }
                if (monthGames.Count > 0) about += "  \u00B7  most in " + monthGames[0].Name;
                AddRow(DateTime.Today.ToString("MMMM") + " so far", monthGames, g => GameFacts.Hours(monthNow[g.Id]), null, about);
            }
                        AddRow("Most played", most, g => GameFacts.Hours(GameFacts.Seconds(g)), null);
            AddRow("Played last", recent, g => GameFacts.Ago(GameFacts.LastPlayed(g)), null);
            AddRow("Pick up again", again, g => GameFacts.Ago(GameFacts.LastPlayed(g)), null);   // (its hours: in the panel)
            AddRow("Still waiting", waiting, g => g.IsInstalled ? "Installed, never played" : "Never played", null);
            if (rows.Count == 0)
                Root.Children.Add(new TextBlock { Text = "Nothing played yet. Your most played and last played games show here.", FontSize = 21, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side, RowTop + 20, 0, 0) });

            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            if (rows.Count > 0) Root.Children.Add(Ui.Prompts(true, Ui.Prompt("X", "Play"), Ui.Prompt("A", "Show on Spotlight")));
            heroLater.Tick += (s, e) => { heroLater.Stop(); ShowHero(); };
        }

        // the panel at the right with a game: its place (label), its logo or name, its numbers; a quick crossfade between games
        private void FillCall(Game g, int row)
        {
            if (g == null || ReferenceEquals(g, callShown)) return;
            bool first = callShown == null;
            callShown = g;
            Action fill = () =>
            {
                callLabel.Foreground = Ink.Brush("SeleneInkColor", 0.6);
                int rank = mostList.IndexOf(g);
                callLabel.Text = rank == 0 ? "YOUR TOP GAME" : rank > 0 ? "#" + (rank + 1) + " MOST PLAYED" : rowNames.Count > row ? rowNames[row].ToUpperInvariant() : "";
                callArt.Children.Clear();
                var logo = HomeNav.Api != null ? System.IO.Path.Combine(HomeNav.Api.Paths.ConfigurationPath, "ExtraMetadata", "games", g.Id.ToString(), "Logo.png") : null;
                if (logo != null && System.IO.File.Exists(logo))
                {
                    var img = new Image { MaxHeight = 100, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.Uniform };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    callArt.Children.Add(img);
                    Ui.LoadInOrder(new List<string> { logo }, 920, (k, pic) => { if (!closed && ReferenceEquals(callShown, g)) img.Source = pic; });
                }
                else callArt.Children.Add(new TextBlock { Text = g.Name, FontSize = 34, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, MaxHeight = 96, TextTrimming = TextTrimming.CharacterEllipsis });
                callNums.Children.Clear();
                ulong secs = GameFacts.Seconds(g);
                var last = GameFacts.LastPlayed(g);
                if (secs > 0)
                {
                    callNums.Children.Add(Stat(GameFacts.Hours(secs), "PLAYED", true));
                    int share = totalSecs > 0 ? (int)Math.Round(100.0 * secs / totalSecs) : 0;
                    var part = Stat((share < 1 ? "<1" : share.ToString()) + "%", "OF YOUR PLAY TIME", false);
                    var words = (StackPanel)part.Children[part.Children.Count - 1];
                    var shareBar = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Ink.Brush("SeleneInkColor", 0.18), Margin = new Thickness(1, 8, 0, 0) };
                    shareBar.Child = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Ink.Brush("SeleneFocusColor"), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(4, 130 * Math.Min(100, share) / 100.0) };
                    words.Children.Add(shareBar);
                    words.Width = 130;
                    callNums.Children.Add(part);
                }
                else callNums.Children.Add(Stat(g.IsInstalled ? "Installed" : "Not installed", "NEVER PLAYED", true));
                callMore.Children.Clear();
                if (last.HasValue) callMore.Children.Add(Line(null, "Last played " + GameFacts.Ago(last).ToLowerInvariant()));
                int count; ulong longest, average;
                if (Sessions.Facts(g.Id, out count, out longest, out average))
                    callMore.Children.Add(Line(null, (count == 1 ? "1 session" : count + " sessions") + "  \u00B7  longest " + GameFacts.Hours(longest) + "  \u00B7  about " + GameFacts.Hours(average) + " each"));
                var set = Features.On("achievements") ? AchievementsData.Cached(g.Id) : null;
                if (set != null && set.Total > 0)
                    callMore.Children.Add(Line(set.Platinum ? "Platinum" : "Gold", set.Percent + "%  \u00B7  " + set.Unlocked + " of " + set.Total + " trophies"));
            };
            if (first) { fill(); return; }
            Ui.Fade(call, 0, 120, 0, () => { if (!ReferenceEquals(callShown, g)) return; fill(); Ui.Fade(call, 1, 220); });
        }

        // a small centered line in the panel, a trophy before it when grade is given
        private static StackPanel Line(string grade, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            if (grade != null) { var t = AchievementsData.TrophyIcon(grade, 17); t.Margin = new Thickness(0, 0, 7, 0); t.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(t); }
            row.Children.Add(new TextBlock { Text = text, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.7), VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        // this week, Monday to Sunday: a bar a day for the time played (today in the focus color), growing in; the week's total
        // and the streak over it
        private static StackPanel WeekChart(ulong[] days, int streak)
        {
            var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(990, 166, 0, 0) };
            ulong week = 0; foreach (var d in days) week += d;
            string label = "THIS WEEK  \u00B7  " + (week > 0 ? GameFacts.Hours(week).ToUpperInvariant() : "NOTHING YET");
            if (streak > 1) label += "  \u00B7  " + streak + " DAYS IN A ROW";
            box.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6) });
            var bars = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            double max = Math.Max(3600, days.Max());
            int today = ((int)DateTime.Today.DayOfWeek + 6) % 7;
            var names = new[] { "M", "T", "W", "T", "F", "S", "S" };
            for (int i = 0; i < 7; i++)
            {
                var col = new StackPanel { Width = 30, Margin = new Thickness(0, 0, 8, 0) };
                var well = new Grid { Height = 60 };
                well.Children.Add(new Border { Width = 12, CornerRadius = new CornerRadius(6), Background = Ink.Brush("SeleneInkColor", 0.08), HorizontalAlignment = HorizontalAlignment.Center });
                double h = days[i] > 0 ? Math.Max(8, 60 * days[i] / max) : 0;
                var fill = new Border { Width = 12, Height = 0, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Background = i == today ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.7) };
                well.Children.Add(fill);
                if (h > 0) fill.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, h, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(220 + i * 50), EasingFunction = Ui.Out() });
                col.Children.Add(well);
                col.Children.Add(new TextBlock { Text = names[i], FontSize = 12, FontWeight = i == today ? FontWeights.Bold : FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", i == today ? 0.95 : 0.45), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
                bars.Children.Add(col);
            }
            box.Children.Add(bars);
            return box;
        }

        // a number large with what it counts under it, a thin line before it (but the first)
        private static StackPanel Stat(string value, string label, bool first)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (!first) row.Children.Add(new Border { Width = 1, Background = Ink.Brush("SeleneInkColor", 0.2), Margin = new Thickness(28, 6, 28, 6) });
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = value, FontSize = 34, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor") });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), Margin = new Thickness(1, 2, 0, 0) });
            row.Children.Add(sp);
            return row;
        }

        // a row of covers with a caption each; bar: a share of the longest (most played), shown as a line under the cover
        // the rows below the first two: made once the screen has slid in (all of them, a hundred covers, held the slide)
        private readonly List<Action> laterRows = new List<Action>();
        private bool rowsOpen;

        private void AddRow(string title, List<Game> games, Func<Game, string> caption, Func<Game, double> bar, string about = null)
        {
            if (games.Count == 0) return;
            if (!rowsOpen && rows.Count >= Visible) { laterRows.Add(() => AddRow(title, games, caption, bar, about)); return; }
            int r = rows.Count;
            var list = new List<Tile>();
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side, r * RowPitch, 0, 0) };
            var head = new TextBlock { FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), Margin = new Thickness(2, 0, 0, 14) };
            head.Inlines.Add(new System.Windows.Documents.Run(title));
            if (!string.IsNullOrEmpty(about)) head.Inlines.Add(new System.Windows.Documents.Run("   " + about) { FontSize = 17, FontWeight = FontWeights.Normal, Foreground = Ink.Brush("SeleneInkColor", 0.6) });
            panel.Children.Add(head);
            var shift = new TranslateTransform();
            var strip = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = shift };
            for (int i = 0; i < games.Count; i++)
            {
                var g = games[i];
                var t = GameFacts.CoverTile(g, TileW, TileH);
                var item = new StackPanel { Width = TileW, Margin = new Thickness(0, 0, Pitch - TileW, 0) };
                item.Children.Add(t.Root);
                // under it: just the line (the cover shows the name), low enough that the focus ring stays clear; the bar under that
                item.Children.Add(new TextBlock { Text = caption(g), FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.8), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 18, 0, 0) });
                if (bar != null)
                {
                    var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Ink.Brush("SeleneInkColor", 0.15), Margin = new Thickness(2, 8, 2, 0) };
                    track.Child = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Ink.Brush("SeleneFocusColor"), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(6, (TileW - 4) * Math.Max(0.02, Math.Min(1, bar(g)))) };
                    item.Children.Add(track);
                }
                strip.Children.Add(item);
                list.Add(t);
                // every cover rises, left to right (the ones past the screen's edge came at once, before the rest); quick
                if (r < Visible) Ui.Rise(item, 40 + r * 40 + Math.Min(i, Across) * 18, 12, 240);
            }
            // the lane the row slides in: cut off just left of the margin with a soft edge (the covers slid past hung over the
            // screen's edge), room around for the focus ring
            const double Room = 26;
            var lane = new Grid { ClipToBounds = true, Width = 1920 - Side + Room, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-Room, -Room, 0, -Room) };
            var edge = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(8, 0) };
            edge.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            edge.GradientStops.Add(new GradientStop(Colors.Black, 1));
            lane.OpacityMask = edge;
            // (on a canvas: a row wider than the lane is laid out whole and slides, the lane's edge cutting it)
            strip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            lane.Height = strip.DesiredSize.Height + 2 * Room;
            Canvas.SetLeft(strip, Room);
            Canvas.SetTop(strip, Room);
            lane.Children.Add(new Canvas { Children = { strip } });
            panel.Children.Add(lane);
            page.Children.Add(panel);
            rows.Add(games);
            tiles.Add(list);
            rowNames.Add(title);
            rowShift.Add(shift);
            rowAlong.Add(0);
            GameFacts.FillCovers(games, list, 300, Features.On("activitycoverfit"));
        }

        private Game Focused { get { return zone < rows.Count && col < rows[zone].Count ? rows[zone][col] : null; } }

        private void ShowFocus(bool animate)
        {
            for (int r = 0; r < tiles.Count; r++)
                for (int i = 0; i < tiles[r].Count; i++) tiles[r][i].Focus(r == zone && i == col);
            // the focused row in the top half (the one below peeks in), within the rows' ends
            int first = Math.Max(0, Math.Min(zone, rows.Count - Visible));
            if (first != firstRow || !animate)
            {
                firstRow = first;
                pageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-first * RowPitch, TimeSpan.FromMilliseconds(animate ? 280 : 0)) { EasingFunction = Ui.Out() });
            }
            // the focused row slides only when the focus reaches an edge of what's on screen (one cover kept beyond it), and
            // keeps where it was when the focus leaves it
            if (zone < rowShift.Count)
            {
                int along = rowAlong[zone], last = Math.Max(0, rows[zone].Count - Across);
                if (col - along > Across - 2) along = col - (Across - 2);
                else if (col - along < 1) along = col - 1;
                along = Math.Max(0, Math.Min(along, last));
                if (along != rowAlong[zone] || !animate)
                {
                    rowAlong[zone] = along;
                    rowShift[zone].BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-along * Pitch, TimeSpan.FromMilliseconds(animate ? 240 : 0)) { EasingFunction = Ui.Out() });
                }
            }
            heroLater.Stop();
            heroLater.Start();
        }

        // the focused game's art behind, once the focus rests on it
        private void ShowHero()
        {
            var g = Focused;
            FillCall(g, zone);
            var path = g != null ? GameFacts.BackgroundPath(g) : null;
            if (path == heroShown) return;
            heroShown = path;
            if (path == null) { Ui.Fade(hero, 0, 400); return; }
            Ui.LoadInOrder(new List<string> { path }, 1920, (i, pic) =>
            {
                if (closed || path != heroShown) return;
                // a crossfade: the art showing goes under, the new one fades in over it, then the old one melts away
                heroBack.BeginAnimation(UIElement.OpacityProperty, null);
                heroBack.Source = hero.Source;
                heroBack.Opacity = hero.Opacity;
                hero.BeginAnimation(UIElement.OpacityProperty, null);
                hero.Source = pic;
                hero.Opacity = 0;
                Ui.Fade(hero, 0.6, 600);
                Ui.Fade(heroBack, 0, 900, 300);
            });
        }

        public override void Shown()
        {
            ShowFocus(false);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ShowMs + 80) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                rowsOpen = true;
                foreach (var make in laterRows) { var m = make; Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => { if (!closed) m(); }), DispatcherPriority.Background); }
                laterRows.Clear();
            };
            t.Start();
        }
        public override void Hidden() { closed = true; heroLater.Stop(); }

        public override void Handle(string b)
        {
            if (b == "B") { Ui.Back(); LoginScene.Finish(); return; }
            var g = Focused;
            switch (b)
            {
                case "DPadLeft":
                case "DPadRight":
                {
                    if (zone >= rows.Count) return;
                    int next = col + (b == "DPadRight" ? 1 : -1);
                    if (next < 0 || next >= rows[zone].Count) { Ui.Shake(tiles[zone][col].Root); return; }
                    col = next;
                    Ui.Tick();
                    ShowFocus(true);
                    return;
                }
                case "DPadUp":
                case "DPadDown":
                {
                    int next = zone + (b == "DPadDown" ? 1 : -1);
                    if (next < 0 || next >= rows.Count) return;
                    // the cover in the same place on screen in the next row (no sideways jump while the page glides)
                    int slot = col - rowAlong[zone];
                    zone = next;
                    col = Math.Max(0, Math.Min(rowAlong[zone] + slot, rows[zone].Count - 1));
                    Ui.Tick();
                    ShowFocus(true);
                    return;
                }
                case "A":
                    if (g == null) return;
                    Ui.Confirm();
                    LoginScene.ForgetReturnFocus();   // the pick gets the focus on Spotlight
                    var id = g.Id;
                    LoginScene.FinishThen(() => HomeNav.ShowGame(id));
                    return;
                case "X":
                    if (g == null) return;
                    if (!g.IsInstalled) { Ui.Shake(tiles[zone][col].Root); return; }
                    Ui.Confirm();
                    LoginScene.Finish();
                    HomeNav.StartGameNow(g.Id);
                    return;
            }
        }

        // the Activity card on Spotlight: how the week went, and the games played last as pictures (Down goes into them)
        public static void FillCard(List<Game> games, int week, ulong total)
        {
            var recent = games.Where(g => GameFacts.LastPlayed(g).HasValue).OrderByDescending(g => GameFacts.LastPlayed(g).Value).Take(4).ToList();
            var most = games.Where(g => GameFacts.Seconds(g) > 0).OrderByDescending(GameFacts.Seconds).FirstOrDefault();
            string headline = week == 0 ? "Nothing played this week" : week == 1 ? "1 game this week" : week + " games this week";
            string about = total < 3600 ? "Your play time, most played and last played games show here." : (int)TimeSpan.FromSeconds(total).TotalHours + " hours in all";
            if (most != null && total >= 3600) about += ", most of them in " + most.Name + " (" + GameFacts.Hours(GameFacts.Seconds(most)) + ").";
            if (recent.Count > 0) about += " Down for the games you played last.";
            var entries = recent.Select(g =>
            {
                var id = g.Id;
                return new CardShowcase.Entry { Image = GameFacts.BackgroundPath(g) ?? GameFacts.CoverPath(g), Title = g.Name, Sub = GameFacts.Ago(GameFacts.LastPlayed(g)), Glyph = "", Open = () => HomeNav.ShowGame(id) };
            }).ToList();
            CardShowcase.Set("nav:activity", "Activity", headline, about, entries);
        }
    }
}
