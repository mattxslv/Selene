using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Playnite.SDK.Models;

namespace Selene
{
    // A game's achievements, like a console's trophy list (Playnite Achievements' data): the logo, a completion ring and the
    // count, then the list. LB / RB: all, unlocked, locked. Y: the order (default, newest unlocked, rarest). A: one
    // achievement large. X: a hidden one's words. B: back.
    public class AchievementsScreen : Screen
    {
        private const double Side = 96, ListTop = 452, IconSize = 64, Page = 24;   // (rows made at a time: more as the focus nears the last)
        private const double TileW = 96, Pitch = TileW + 16, StripTop = 196;   // the latest unlocks under the header
        private const int RecentMax = 14;
        private static readonly string[] Tabs = { "All", "Unlocked", "Locked" };
        private static readonly string[] Sorts = { "Default order", "Newest unlocked", "Rarest first" };

        private readonly Guid gameId;
        private readonly Func<Screen> back;
        private readonly Game game;
        private AchievementsData.GameSet set;
        private List<AchievementsData.Item> shown = new List<AchievementsData.Item>();
        private readonly HashSet<AchievementsData.Item> revealed = new HashSet<AchievementsData.Item>();
        private readonly List<Border> rows = new List<Border>();
        private readonly List<Border> tabPills = new List<Border>();
        private readonly StackPanel list = new StackPanel();
        private readonly ScrollViewer scroller;
        private readonly Grid header = new Grid();
        private readonly TextBlock sortLabel, note;
        private readonly Image hero = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
        private Grid detail;
        // the game's latest unlocks: a row under the header (Up from the top of the list goes in), the focused one's name under it
        private readonly List<AchievementsData.Item> recent = new List<AchievementsData.Item>();
        private readonly List<Tile> recentTiles = new List<Tile>();
        private readonly StackPanel recentStrip = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock recentLine = new TextBlock { FontSize = 15, FontFamily = Ui.Body, Opacity = 0 };
        private bool inRecent;
        private int recentIndex;
        private int tab, sort, index, built;
        private bool closed;

        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.96); } }

        public AchievementsScreen(Guid gameId, Func<Screen> back)
        {
            this.gameId = gameId;
            this.back = back;
            game = GameFacts.Find(gameId);

            // the game's art faint behind, dark at the left and the bottom (as Activity)
            RenderOptions.SetBitmapScalingMode(hero, BitmapScalingMode.HighQuality);
            Root.Children.Add(hero);
            Root.Children.Add(new Border { IsHitTestVisible = false, Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0.96, 0), Ink.Stop("SeleneScrimColor", 0.75, 0.55), Ink.Stop("SeleneScrimColor", 0.45, 1) }, 0) });
            Root.Children.Add(new Border { IsHitTestVisible = false, Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0, 0.2), Ink.Stop("SeleneScrimColor", 0.92, 0.75) }, 90) });

            header.HorizontalAlignment = HorizontalAlignment.Stretch;
            header.VerticalAlignment = VerticalAlignment.Top;
            header.Margin = new Thickness(Side, 56, Side, 0);
            Root.Children.Add(header);

            var tabsRow = new DockPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side, ListTop - 70, Side, 0), LastChildFill = false };
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            tabs.Children.Add(Badge("LB"));
            for (int i = 0; i < Tabs.Length; i++)
            {
                var pill = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(18, 7, 18, 8), Margin = new Thickness(6, 0, 6, 0), Child = new TextBlock { FontSize = 17, FontFamily = Ui.Body, FontWeight = FontWeights.SemiBold } };
                tabPills.Add(pill);
                tabs.Children.Add(pill);
            }
            tabs.Children.Add(Badge("RB"));
            DockPanel.SetDock(tabs, Dock.Left);
            tabsRow.Children.Add(tabs);
            sortLabel = new TextBlock { FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(sortLabel, Dock.Right);
            tabsRow.Children.Add(sortLabel);
            Root.Children.Add(tabsRow);

            scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false };
            var area = PeekScroller.Wrap(scroller, list);
            area.VerticalAlignment = VerticalAlignment.Top;
            area.Margin = new Thickness(Side, ListTop, Side, 0);
            area.Height = 1080 - ListTop - 120;
            Root.Children.Add(area);

            note = new TextBlock { FontSize = 21, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side + 4, ListTop + 10, 0, 0), Text = "" };
            Root.Children.Add(note);

            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("Y", "Order"), Ui.Prompt("A", "Open")));
            ShowTabs();
        }

        private static Border Badge(string text)
        {
            return HomeNav.FilterBadge(text);
        }

        public override void Shown()
        {
            Ui.Play("OpenPanel.wav", 0.8);
            Ui.Rise(header, 40, 14, 320);
            var art = game != null ? GameFacts.BackgroundPath(game) : null;
            if (art != null) Ui.LoadInOrder(new List<string> { art }, 1920, (k, pic) => { if (closed) return; hero.Source = pic; Ui.Fade(hero, 0.5, 600); });
            AchievementsData.ForGame(gameId, s =>
            {
                if (closed) return;
                set = s;
                BuildHeader();
                if (set == null || set.Total == 0)
                {
                    note.Text = AchievementsData.Available()
                        ? "No achievements for this game yet. Playnite Achievements fills them in when it next refreshes this game."
                        : "Achievements need the Playnite Achievements add-on.";
                    return;
                }
                note.Visibility = Visibility.Collapsed;
                BuildRecent();
                Fill();
            });
        }

        private void BuildRecent()
        {
            recent.AddRange(set.Items.Where(i => i.Unlocked && i.When.HasValue).OrderByDescending(i => i.When.Value).Take(RecentMax));
            var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side + 2, StripTop, 0, 0) };
            box.Children.Add(new TextBlock { Text = "Recently unlocked", FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), Margin = new Thickness(0, 0, 0, 12) });
            if (recent.Count == 0)
            {
                box.Children.Add(new TextBlock { Text = "Nothing unlocked yet.", FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55) });
                Root.Children.Add(box);
                return;
            }
            var files = new List<string>();
            var at = new List<int>();
            for (int i = 0; i < recent.Count; i++)
            {
                var t = new Tile(TileW, TileW, 14) { FocusScale = 1.06 };
                int clickedRecent = i;
                t.Click = () => { if (detail != null) return; inRecent = true; recentIndex = clickedRecent; ShowFocus(); ShowRecentFocus(); Handle("A"); };   // (#40)
                t.Root.Margin = new Thickness(0, 0, Pitch - TileW, 0);
                t.Face.Child = AchievementsData.TrophyIcon(recent[i].Grade, 34);
                ((TextBlock)t.Face.Child).HorizontalAlignment = HorizontalAlignment.Center;
                ((TextBlock)t.Face.Child).VerticalAlignment = VerticalAlignment.Center;
                if (recent[i].Icon != null) { files.Add(recent[i].Icon); at.Add(i); }
                recentStrip.Children.Add(t.Root);
                recentTiles.Add(t);
                Ui.Rise(t.Root, 120 + i * 40, 12, 280);
            }
            box.Children.Add(recentStrip);
            recentLine.Margin = new Thickness(0, 14, 0, 0);
            box.Children.Add(recentLine);
            Root.Children.Add(box);
            Ui.LoadInOrder(files, 192, (k, pic) =>
            {
                if (closed || k >= at.Count) return;
                var face = new Border { CornerRadius = new CornerRadius(13), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } };
                RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.HighQuality);
                recentTiles[at[k]].Face.Child = face;
            });
        }

        // the row's focus (or none, the list having it), the focused unlock's name and date under it
        private void ShowRecentFocus()
        {
            for (int i = 0; i < recentTiles.Count; i++) recentTiles[i].Focus(inRecent && i == recentIndex);
            if (!inRecent || recentIndex >= recent.Count) { Ui.Fade(recentLine, 0, 140); return; }
            var it = recent[recentIndex];
            recentLine.Inlines.Clear();
            recentLine.Inlines.Add(new System.Windows.Documents.Run(it.Name ?? "") { FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor") });
            recentLine.Inlines.Add(new System.Windows.Documents.Run("  \u00B7  " + it.Grade + "  \u00B7  " + GameFacts.Ago(it.When)) { Foreground = Ink.Brush("SeleneInkColor", 0.6) });
            Ui.Fade(recentLine, 1, 180);
        }

        public override void Hidden() { closed = true; }

        // ---- the header: logo (or name), the ring, the count ----
        private void BuildHeader()
        {
            header.Children.Clear();
            var left = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            var logo = game != null && HomeNav.Api != null ? System.IO.Path.Combine(HomeNav.Api.Paths.ConfigurationPath, "ExtraMetadata", "games", game.Id.ToString(), "Logo.png") : null;
            string name = game != null ? game.Name : set != null ? set.Name : "";
            if (logo != null && File.Exists(logo))
            {
                var img = new Image { MaxHeight = 120, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Ui.LoadInOrder(new List<string> { logo }, 1040, (k, pic) => img.Source = pic);
                left.Children.Add(img);
            }
            else left.Children.Add(new TextBlock { Text = name, FontSize = 44, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 1100 });
            header.Children.Add(left);
            if (set == null || set.Total == 0) return;

            // at the right, as a console's trophy list: the share large with a line under it, then the trophies, earned of
            // all of each grade (platinum: every one earned)
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var share = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 190, Margin = new Thickness(0, 0, 18, 0) };
            share.Children.Add(new TextBlock { Text = set.Percent + "%", FontSize = 56, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Right });
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ink.Brush("SeleneInkColor", 0.2), Margin = new Thickness(30, 2, 0, 0) };
            track.Child = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ink.Brush(set.Platinum ? "GlyphColor" : "SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(3, 160.0 * set.Unlocked / Math.Max(1, set.Total)) };
            share.Children.Add(track);
            top.Children.Add(share);
            foreach (var grade in new[] { "Platinum", "Gold", "Silver", "Bronze" })
            {
                bool platinum = grade == "Platinum";
                var cell = new StackPanel { Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 };
                var icon = AchievementsData.TrophyIcon(grade, 40);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                if (platinum && !set.Platinum) icon.Opacity = 0.3;
                cell.Children.Add(icon);
                var count = new TextBlock { FontSize = 17, FontFamily = Ui.Body, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                count.Inlines.Add(new System.Windows.Documents.Run(platinum ? (set.Platinum ? "1" : "0") : set.Earned(grade).ToString()) { FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor") });
                count.Inlines.Add(new System.Windows.Documents.Run(" / " + (platinum ? "1" : set.Count(grade).ToString())) { Foreground = Ink.Brush("SeleneInkColor", 0.5) });
                cell.Children.Add(count);
                top.Children.Add(cell);
            }
            right.Children.Add(top);
            var lineText = set.Unlocked + " of " + set.Total + " unlocked";
            var last = set.LastUnlock;
            if (last.HasValue) lineText += "  \u00B7  last one " + GameFacts.Ago(last).ToLowerInvariant();
            right.Children.Add(new TextBlock { Text = lineText, FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) });
            header.Children.Add(right);
            Ui.Rise(right, 80, 12, 320);
        }

        // a completion ring: the track, the share as an arc, the percent in the middle
        internal static Grid Ring(double share, double size)
        {
            share = Math.Max(0, Math.Min(1, share));
            var g = new Grid { Width = size, Height = size };
            double stroke = size * 0.08, r = (size - stroke) / 2, c = size / 2;
            g.Children.Add(new Ellipse { Stroke = Ink.Brush("SeleneInkColor", 0.15), StrokeThickness = stroke });
            if (share >= 0.999) g.Children.Add(new Ellipse { Stroke = Ink.Brush("SeleneFocusColor"), StrokeThickness = stroke });
            else if (share > 0)
            {
                double angle = share * 2 * Math.PI;
                var start = new Point(c, c - r);
                var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
                var fig = new PathFigure { StartPoint = start, IsClosed = false };
                fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, share > 0.5, SweepDirection.Clockwise, true));
                g.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { fig }), Stroke = Ink.Brush("SeleneFocusColor"), StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            }
            g.Children.Add(new TextBlock { Text = (int)Math.Floor(share * 100) + "%", FontSize = size * 0.24, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            return g;
        }

        // ---- the list ----
        private void Fill()
        {
            IEnumerable<AchievementsData.Item> items = set.Items;
            if (tab == 1) items = items.Where(i => i.Unlocked);
            if (tab == 2) items = items.Where(i => !i.Unlocked);
            if (sort == 1) items = items.OrderByDescending(i => i.Unlocked).ThenByDescending(i => i.When ?? DateTime.MinValue);
            else if (sort == 2) items = items.OrderBy(i => i.Rarity ?? 101);
            else items = items.OrderBy(i => i.Order);
            shown = items.ToList();
            list.Children.Clear();
            rows.Clear();
            built = 0;
            index = 0;
            scroller.ScrollToVerticalOffset(0);
            ShowTabs();
            if (shown.Count == 0)
            {
                note.Text = tab == 1 ? "Nothing unlocked yet." : "Everything's unlocked.";
                bool all = tab == 2 && set.Items.Count > 0 && Features.On("achcelebrate");
                note.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
                if (all) Celebrate();
                return;
            }
            note.Visibility = Visibility.Collapsed;
            HideCelebration();
            BuildMore();
            ShowFocus();
        }

        // ---- every achievement unlocked: a gold medal in the middle, a glow, a few twinkling sparkles and a line to be proud of ----
        private Grid celebration;
        private static readonly string[][] Proud =
        {
            new[] { "You got everything.", "Every. Single. One." },
            new[] { "Nothing left to unlock.", "Legend status: confirmed." },
            new[] { "100%. Take a bow.", "The game has nothing more to teach you." },
            new[] { "Completionist.", "Not one trophy escaped you." },
        };

        private void Celebrate()
        {
            HideCelebration();
            var gold = Color.FromRgb(0xF2, 0xC2, 0x4B);   // the trophies' gold (the same in every theme)
            var words = Proud[new Random().Next(Proud.Length)];
            celebration = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, ListTop + 20, 0, 0), IsHitTestVisible = false };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            // the medal: a soft gold glow, a ring, the trophy
            var medal = new Grid { Width = 260, Height = 200, HorizontalAlignment = HorizontalAlignment.Center };
            var glow = new System.Windows.Shapes.Ellipse { Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -20, 0, -20) };
            var halo = new RadialGradientBrush();
            halo.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, gold.R, gold.G, gold.B), 0));
            halo.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, gold.R, gold.G, gold.B), 1));
            glow.Fill = halo;
            medal.Children.Add(glow);
            var ring = new System.Windows.Shapes.Ellipse { Width = 128, Height = 128, StrokeThickness = 3, Stroke = new SolidColorBrush(Color.FromArgb(0xCC, gold.R, gold.G, gold.B)), Fill = new SolidColorBrush(Color.FromArgb(0x30, gold.R, gold.G, gold.B)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            medal.Children.Add(ring);
            var trophy = Ui.Glyph(Ui.Icon(0xea23), Ui.Icons, 68, new SolidColorBrush(gold));
            trophy.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = gold, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.7 };
            medal.Children.Add(trophy);
            // sparkles around it, each twinkling on its own beat
            var spots = new[] { new Point(-96, -58), new Point(100, -66), new Point(-118, 22), new Point(116, 30), new Point(-62, 74), new Point(70, 80), new Point(4, -96) };
            var rnd = new Random();
            for (int i = 0; i < spots.Length; i++)
            {
                var star = new TextBlock { Text = "\u2726", FontSize = 14 + rnd.Next(12), Foreground = new SolidColorBrush(i % 2 == 0 ? gold : Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = new TranslateTransform(spots[i].X, spots[i].Y), Opacity = 0 };
                var twinkle = new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(900 + rnd.Next(700))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(300 + i * 140), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                star.BeginAnimation(UIElement.OpacityProperty, twinkle);
                medal.Children.Add(star);
            }
            stack.Children.Add(medal);
            stack.Children.Add(new TextBlock { Text = words[0], FontSize = 40, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(new TextBlock { Text = words[1], FontSize = 21, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.65), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
            celebration.Children.Add(stack);
            Root.Children.Add(celebration);
            // in: rises and grows into place, eased (no bounce), the glow breathing after
            var scale = new ScaleTransform(0.82, 0.82);
            var lift = new TranslateTransform(0, 24);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(lift);
            celebration.RenderTransformOrigin = new Point(0.5, 0.3);
            celebration.RenderTransform = group;
            celebration.Opacity = 0;
            var dur = TimeSpan.FromMilliseconds(420);
            celebration.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, dur) { EasingFunction = Ui.Out() });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, dur) { EasingFunction = Ui.Out() });
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = Ui.Out() });
            glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(1800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            if (Features.On("sounds")) Ui.Play("Success.wav", 0.8);
        }

        private void HideCelebration()
        {
            if (celebration == null) return;
            Root.Children.Remove(celebration);
            celebration = null;
        }

        // rows are built a page at a time (some games have hundreds, or thousands)
        private void BuildMore()
        {
            int end = Math.Min(shown.Count, built + (int)Page);
            var files = new List<string>();
            var targets = new List<Tuple<Border, bool>>();
            for (int i = built; i < end; i++)
            {
                var it = shown[i];
                var icon = new Border { Width = IconSize, Height = IconSize, CornerRadius = new CornerRadius(10), Background = Ink.Brush("SeleneInkColor", 0.08), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
                if (!it.Unlocked) icon.Opacity = 0.55;
                if (it.Icon != null) { files.Add(it.Icon); targets.Add(Tuple.Create(icon, !it.Unlocked)); }
                else icon.Child = Ui.Glyph(Ui.Icon(0xea23), Ui.Icons, 30, Ink.Brush("SeleneInkColor", 0.5));

                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock { FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", it.Unlocked ? 1 : 0.8), TextTrimming = TextTrimming.CharacterEllipsis };
                var about = new TextBlock { FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
                Words(it, name, about);
                words.Children.Add(name);
                words.Children.Add(about);

                var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0), MinWidth = 180 };
                side.Children.Add(GradeLine(it, 15));
                side.Children.Add(new TextBlock { Text = Status(it), FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 0, 0) });

                var line = new DockPanel();
                DockPanel.SetDock(icon, Dock.Left);
                DockPanel.SetDock(side, Dock.Right);
                line.Children.Add(icon);
                line.Children.Add(side);
                line.Children.Add(words);
                var row = new Border { Child = line, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 12, 24, 12), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Ink.Brush("SeleneGlassColor", 0.72), Tag = it };
                int clicked = rows.Count;
                row.MouseLeftButtonUp += (sender, args) => { args.Handled = true; if (detail != null) return; inRecent = false; index = clicked; ShowRecentFocus(); ShowFocus(); Handle("A"); };   // a click opens it, as A (#40)
                list.Children.Add(row);
                rows.Add(row);
                if (i < 8) Ui.Rise(row, 60 + i * 40, 12, 280);
            }
            built = end;
            Ui.LoadInOrder(files, 128, (k, pic) =>
            {
                if (closed || k >= targets.Count) return;
                var src = pic as BitmapSource;
                // locked: in grey, as a console shows the ones still to get
                if (targets[k].Item2 && src != null) { try { var grey = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0); grey.Freeze(); pic = grey; } catch { } }
                targets[k].Item1.Child = new Border { Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } };
            });
        }

        // a hidden one stays hidden until it's unlocked (or X shows it)
        private void Words(AchievementsData.Item it, TextBlock name, TextBlock about)
        {
            bool secret = it.Hidden && !it.Unlocked && !revealed.Contains(it);
            name.Text = secret ? "Hidden achievement" : it.Name;
            about.Text = secret ? "Keep playing to find out. X shows it." : (string.IsNullOrWhiteSpace(it.Description) ? " " : it.Description);
        }

        private static string Status(AchievementsData.Item it)
        {
            if (it.Unlocked) return it.When.HasValue ? "Unlocked " + GameFacts.Date(it.When.Value, "MMM d, yyyy") : "Unlocked";
            if (it.ProgressNum.HasValue && it.ProgressDenom.HasValue && it.ProgressDenom.Value > 0) return it.ProgressNum.Value + " / " + it.ProgressDenom.Value;
            return "Locked";
        }

        // a trophy in its metal's colour, its grade, and how rare it is
        internal static StackPanel GradeLine(AchievementsData.Item it, double size)
        {
            var grade = it.Grade;
            var brush = AchievementsData.GradeBrush(grade);
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            line.Children.Add(new TextBlock { Text = Ui.Icon(0xea23), FontFamily = Ui.Icons, FontSize = size + 2, Foreground = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0), Opacity = it.Unlocked ? 1 : 0.6 });
            line.Children.Add(new TextBlock { Text = grade, FontSize = size, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = brush, VerticalAlignment = VerticalAlignment.Center, Opacity = it.Unlocked ? 1 : 0.7 });
            if (it.Rarity.HasValue)
                line.Children.Add(new TextBlock { Text = "  \u00B7  " + (it.Rarity.Value < 1 ? it.Rarity.Value.ToString("0.0") : Math.Round(it.Rarity.Value).ToString("0")) + "%", FontSize = size, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), VerticalAlignment = VerticalAlignment.Center });
            return line;
        }

        // the trophies earned, grade by grade, as a console counts them
        internal static StackPanel GradeCounts(AchievementsData.GameSet set, double size)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var grade in new[] { "Gold", "Silver", "Bronze" })
            {
                row.Children.Add(new TextBlock { Text = Ui.Icon(0xea23), FontFamily = Ui.Icons, FontSize = size + 2, Foreground = AchievementsData.GradeBrush(grade), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                row.Children.Add(new TextBlock { Text = set.Earned(grade).ToString(), FontSize = size, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.9), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) });
            }
            return row;
        }

        internal static Brush TierBrush(AchievementsData.Item it)
        {
            if (it.Tier == "Ultra rare") return Ink.Brush("GlyphColor");
            if (it.Tier == "Rare") return Ink.Brush("GlyphColor", 0.8);
            return Ink.Brush("SeleneInkColor", 0.6);
        }

        private void ShowTabs()
        {
            for (int i = 0; i < tabPills.Count; i++)
            {
                int count = set == null ? 0 : i == 0 ? set.Total : i == 1 ? set.Unlocked : set.Total - set.Unlocked;
                var t = (TextBlock)tabPills[i].Child;
                t.Text = Tabs[i] + (set != null ? "  " + count : "");
                bool on = i == tab;
                tabPills[i].Background = on ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.1);
                t.Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.85);
            }
            sortLabel.Text = Sorts[sort];
        }

        private void ShowFocus()
        {
            if (index >= built - 10 && built < shown.Count) BuildMore();
            for (int i = 0; i < rows.Count; i++)
            {
                bool on = i == index && !inRecent;
                rows[i].BorderBrush = on ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
                rows[i].Background = Ink.Brush("SeleneGlassColor", on ? 0.95 : 0.72);
            }
            if (index >= rows.Count) return;
            try
            {
                scroller.UpdateLayout();
                var row = rows[index];
                var top = row.TranslatePoint(new Point(0, 0), list).Y;
                double want = scroller.VerticalOffset, pad = PeekScroller.Band;
                if (top - pad * 0.6 < scroller.VerticalOffset) want = Math.Max(0, top - pad * 0.6);
                else if (top + row.ActualHeight > scroller.VerticalOffset + scroller.ViewportHeight - pad) want = top + row.ActualHeight - (scroller.ViewportHeight - pad);
                if (Math.Abs(want - scroller.VerticalOffset) > 0.5) scroller.ScrollToVerticalOffset(want);
            }
            catch { }
        }

        // ---- one achievement, large ----
        private void OpenDetail()
        {
            if (index >= shown.Count) return;
            OpenDetail(shown[index]);
        }

        private void OpenDetail(AchievementsData.Item it)
        {
            bool secret = it.Hidden && !it.Unlocked && !revealed.Contains(it);
            detail = new Grid { Background = Ink.Brush("SeleneScrimColor", 0.85) };
            var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 760 };
            var icon = new Border { Width = 168, Height = 168, CornerRadius = new CornerRadius(24), HorizontalAlignment = HorizontalAlignment.Center, Background = Ink.Brush("SeleneInkColor", 0.08), ClipToBounds = true };
            if (it.Icon != null && !secret)
                Ui.LoadInOrder(new List<string> { it.Icon }, 336, (k, pic) =>
                {
                    var src = pic as BitmapSource;
                    if (!it.Unlocked && src != null) { try { var grey = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0); grey.Freeze(); pic = grey; } catch { } }
                    icon.Child = new Border { Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } };
                });
            else icon.Child = Ui.Glyph(Ui.Icon(0xea23), Ui.Icons, 72, Ink.Brush("SeleneInkColor", 0.5));
            if (!it.Unlocked) icon.Opacity = 0.6;
            card.Children.Add(icon);
            card.Children.Add(new TextBlock { Text = secret ? "Hidden achievement" : it.Name, FontSize = 34, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 26, 0, 0) });
            card.Children.Add(new TextBlock { Text = secret ? "Keep playing to find out." : it.Description, FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.75), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            var facts = new List<string>();
            var gradeLine = GradeLine(it, 20);
            gradeLine.HorizontalAlignment = HorizontalAlignment.Center;
            gradeLine.Margin = new Thickness(0, 18, 0, 0);
            card.Children.Add(gradeLine);
            facts.Add(it.Unlocked ? (it.When.HasValue ? "Unlocked " + GameFacts.Date(it.When.Value, "MMMM d, yyyy  ·  HH:mm") : "Unlocked") : Status(it));
            if (it.Rarity.HasValue) facts.Add((it.Rarity.Value < 1 ? it.Rarity.Value.ToString("0.0") : Math.Round(it.Rarity.Value).ToString("0")) + "% of players have it");
            foreach (var f in facts) card.Children.Add(new TextBlock { Text = f, FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) });
            detail.Children.Add(card);
            detail.Children.Add(Ui.Prompts(true, Ui.Prompt("B", "Close")));
            Panel.SetZIndex(detail, 50);
            Root.Children.Add(detail);
            detail.Opacity = 0;
            Ui.Fade(detail, 1, 200);
            Ui.Rise(card, 0, 16, 280);
        }

        private void CloseDetail()
        {
            var d = detail;
            detail = null;
            Ui.Fade(d, 0, 160, 0, () => Root.Children.Remove(d));
        }

        public override void Handle(string b)
        {
            if (detail != null) { if (b == "B" || b == "A") { Ui.Back(); CloseDetail(); } return; }
            if (b == "B") { Ui.Back(); if (back == null) LoginScene.Finish(); else LoginScene.Show(back(), -1); return; }
            if (set == null || set.Total == 0) return;
            if (inRecent)
            {
                switch (b)
                {
                    case "DPadLeft":
                    case "DPadRight":
                    {
                        int next = recentIndex + (b == "DPadRight" ? 1 : -1);
                        if (next < 0 || next >= recent.Count) return;
                        recentIndex = next;
                        Ui.Tick();
                        ShowRecentFocus();
                        return;
                    }
                    case "DPadDown":
                        if (shown.Count == 0) return;
                        inRecent = false;
                        Ui.Tick();
                        ShowRecentFocus();
                        ShowFocus();
                        return;
                    case "A":
                        Ui.Confirm();
                        OpenDetail(recent[recentIndex]);
                        return;
                }
                return;
            }
            if (b == "DPadUp" && index == 0 && recent.Count > 0)
            {
                inRecent = true;
                Ui.Tick();
                ShowFocus();
                ShowRecentFocus();
                return;
            }
            switch (b)
            {
                case "LeftShoulder":
                case "RightShoulder":
                    tab = (tab + (b == "RightShoulder" ? 1 : Tabs.Length - 1)) % Tabs.Length;
                    Ui.Tick();
                    Fill();
                    Ui.Pop(tabPills[tab]);
                    return;
                case "Y":
                    sort = (sort + 1) % Sorts.Length;
                    Ui.Key();
                    Fill();
                    Ui.Pop(sortLabel);
                    return;
                case "DPadUp":
                case "DPadDown":
                {
                    int next = index + (b == "DPadDown" ? 1 : -1);
                    if (next < 0 || next >= shown.Count) { if (index < rows.Count) Ui.Shake(rows[index]); return; }
                    index = next;
                    Ui.Tick();
                    ShowFocus();
                    return;
                }
                case "A":
                    if (index >= shown.Count) return;
                    Ui.Confirm();
                    OpenDetail();
                    return;
                case "X":
                {
                    if (index >= shown.Count) return;
                    var it = shown[index];
                    if (!it.Hidden || it.Unlocked || revealed.Contains(it)) return;
                    revealed.Add(it);
                    Ui.Key();
                    var line = (DockPanel)rows[index].Child;
                    var words = line.Children.OfType<StackPanel>().First(p => p.Children.Count == 2 && p.Margin.Left == 0);
                    Words(it, (TextBlock)words.Children[0], (TextBlock)words.Children[1]);
                    Ui.Pop(rows[index]);
                    return;
                }
            }
        }
    }
}
