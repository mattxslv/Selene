using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Selene
{
    // The pictures under a Spotlight card (Captures, Activity, Surprise me), as the Store card has: a headline and a line
    // about it, then a strip of big pictures with a caption. Down from the card goes into them: Left / Right pick one,
    // A opens it, Up or B goes back to the row. One strip per card; each card fills its own.
    public static class CardShowcase
    {
        public class Entry
        {
            public string Image;          // a picture file (decoded small), or null for a glyph face
            public string Title, Sub, Glyph;
            public Action Open;
            // an achievement: its own icon on a glass tile with its words beside it (no picture behind: the same game's art
            // came back on every tile); Grade is its trophy (bronze...), Third its last line
            public string Icon, Grade, Third;
        }

        private class Strip
        {
            public string Card, Panel;
            public List<Entry> Entries = new List<Entry>();
            public List<Tile> Tiles = new List<Tile>();
        }

        private static readonly Dictionary<string, Strip> strips = new Dictionary<string, Strip>();
        private static Strip active;
        private static int focus;

        // the card's words and pictures (headline and about are resources the theme shows: Selene<Name>Headline / About)
        public static void Set(string card, string name, string headline, string about, List<Entry> entries)
        {
            var res = Application.Current.Resources;
            res["Selene" + name + "Headline"] = headline;
            res["Selene" + name + "About"] = about;
            Strip s;
            if (!strips.TryGetValue(card, out s)) { s = new Strip { Card = card, Panel = "Selene" + name + "Showcase" }; strips[card] = s; }
            s.Entries = Features.On("cardpictures") && entries != null ? entries : new List<Entry>();   // the Pictures under cards switch
            if (ReferenceEquals(active, s)) Leave();
            Fill(s);
        }

        private static void Fill(Strip s)
        {
            var w = Application.Current.MainWindow;
            var panel = w != null ? HomeNav.FindByName(w, s.Panel) as StackPanel : null;
            if (panel == null) return;
            panel.Children.Clear();
            s.Tiles.Clear();
            var files = new List<string>();
            var at = new List<int>();
            for (int i = 0; i < s.Entries.Count; i++)
            {
                var e = s.Entries[i];
                var tile = new Tile(420, 236, 14, 5) { FocusScale = 1.04 };
                tile.Root.MouseLeftButtonUp += (sender, args) => { if (e.Open != null) { Ui.Confirm(); e.Open(); } args.Handled = true; };
                tile.Root.Margin = new Thickness(0, 0, 22, 0);
                if (e.Icon != null || e.Grade != null) { tile.Face.Child = AchievementFace(e); panel.Children.Add(tile.Root); s.Tiles.Add(tile); Ui.Rise(tile.Root, i * 50, 12, 280); continue; }
                var frame = new Grid();
                if (e.Image != null) { files.Add(e.Image); at.Add(i); }
                else
                {
                    // no picture of its own (a clip): a mark on the glass face
                    frame.Children.Add(new TextBlock { Text = e.Glyph ?? "", FontFamily = Ui.Glyphs, FontSize = 44, Foreground = Ink.Brush("SeleneInkColor", 0.85), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 30) });
                }
                var chip = new StackPanel { Orientation = Orientation.Horizontal };
                chip.Children.Add(new TextBlock { Text = e.Title ?? "", FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 250, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrEmpty(e.Sub)) chip.Children.Add(new TextBlock { Text = e.Sub, FontSize = 15, FontFamily = Ui.Body, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0) });
                frame.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10), Padding = new Thickness(10, 4, 12, 5), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), Child = chip });
                tile.Face.Child = frame;
                panel.Children.Add(tile.Root);
                s.Tiles.Add(tile);
                Ui.Rise(tile.Root, i * 50, 12, 280);
            }
            if (files.Count == 0) return;
            var tiles = s.Tiles;
            Ui.LoadInOrder(files, 900, (i, pic) =>
            {
                if (at[i] >= tiles.Count || !ReferenceEquals(s.Tiles, tiles)) return;
                var frame = tiles[at[i]].Face.Child as Grid;
                if (frame == null) return;
                var fill = new Border { CornerRadius = new CornerRadius(13), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill }, Opacity = 0 };
                frame.Children.Insert(0, fill);
                Ui.Fade(fill, 1, 260);
            });
        }

        private static Grid AchievementFace(Entry e)
        {
            var g = new Grid { Margin = new Thickness(22) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // 128: a whole multiple of the 64 px icons many stores give, so those are doubled pixel for pixel, sharp
            var icon = new Border { Width = 128, Height = 128, CornerRadius = new CornerRadius(14), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Background = Ink.Brush("SeleneInkColor", 0.08) };
            icon.Child = AchievementsData.TrophyIcon(e.Grade ?? "Bronze", 56);
            ((TextBlock)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;
            if (e.Icon != null) Ui.LoadInOrder(new List<string> { e.Icon }, 256, (k, pic) =>
            {
                var bmp = pic as System.Windows.Media.Imaging.BitmapSource;
                var brush = new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
                var face = new Border { Background = brush };
                bool doubled = bmp != null && bmp.PixelWidth < 128 && 128 % bmp.PixelWidth == 0;
                var mode = doubled ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
                RenderOptions.SetBitmapScalingMode(brush, mode);
                RenderOptions.SetBitmapScalingMode(face, mode);
                icon.Child = face;
            });
            g.Children.Add(icon);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
            Grid.SetColumn(words, 1);
            words.Children.Add(new TextBlock { Text = e.Title ?? "", FontSize = 22, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, MaxHeight = 62, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(e.Sub)) words.Children.Add(new TextBlock { Text = e.Sub, FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.65), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) });
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            if (e.Grade != null)
            {
                var t = AchievementsData.TrophyIcon(e.Grade, 22);
                t.Margin = new Thickness(0, 0, 6, 0);
                line.Children.Add(t);
                line.Children.Add(new TextBlock { Text = e.Grade, FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = AchievementsData.GradeBrush(e.Grade), VerticalAlignment = VerticalAlignment.Center });
            }
            if (!string.IsNullOrEmpty(e.Third)) line.Children.Add(new TextBlock { Text = (e.Grade != null ? "  ·  " : "") + e.Third, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), VerticalAlignment = VerticalAlignment.Center });
            words.Children.Add(line);
            g.Children.Add(words);
            return g;
        }

        // Down from a card goes into its pictures; true when the press was the pictures'
        public static bool Handle(string b)
        {
            if (active == null)
            {
                if (b != "DPadDown") return false;
                var card = HomeNav.FocusedCard();
                Strip s;
                if (card == null || !strips.TryGetValue(card, out s) || s.Tiles.Count == 0) return false;
                active = s;
                focus = 0;
                Ui.Tick();
                HomeNav.DimCard(card, true);
                ShowFocus();
                return true;
            }
            if (HomeNav.FocusedCard() != active.Card) { Leave(); return false; }
            switch (b)
            {
                case "DPadLeft":
                case "DPadRight":
                    int next = focus + (b == "DPadRight" ? 1 : -1);
                    if (next >= 0 && next < active.Tiles.Count) { focus = next; Ui.Tick(); ShowFocus(); }
                    return true;
                case "DPadDown":
                    return true;
                case "DPadUp":
                case "B":
                    Ui.Tick();
                    Leave();
                    return true;
                case "A":
                {
                    var entry = focus < active.Entries.Count ? active.Entries[focus] : null;
                    Ui.Confirm();
                    Leave();
                    if (entry != null && entry.Open != null) entry.Open();
                    return true;
                }
                default:
                    Leave();   // anything else: back to the row and let it do its thing
                    return false;
            }
        }

        private static void Leave()
        {
            if (active == null) return;
            HomeNav.DimCard(active.Card, false);
            var s = active;
            active = null;
            for (int i = 0; i < s.Tiles.Count; i++) s.Tiles[i].Focus(false);
        }

        private static void ShowFocus()
        {
            if (active == null) return;
            for (int i = 0; i < active.Tiles.Count; i++) active.Tiles[i].Focus(i == focus);
        }
    }
}
