using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Selene
{
    // Picking the Store's currency: asked the first time the Store opens, and with X in the Store after that
    public class StoreCurrencyScreen : Screen
    {
        private const int Columns = 6;   // 36 currencies: six rows of six fit a 1080-line screen with the title
        private readonly bool first;
        private readonly List<Tile> tiles = new List<Tile>();
        private int index;

        private readonly Func<Screen> back;   // where B and a pick go: the Store, or the settings it was opened from

        public StoreCurrencyScreen(bool first, Func<Screen> back = null)
        {
            this.first = first;
            this.back = back;
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -30, 0, 0) };
            col.Children.Add(Ui.Title("Choose your currency"));
            col.Children.Add(Ui.Sub("The stores show their prices in it. GOG and the other stores' deals stay in dollars. Change it any time with X in the Store."));
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = Columns, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 48, 0, 0) };
            foreach (var c in StoreData.Currencies)
            {
                var tile = new Tile(206, 96, 16, 5);
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = c.Code, FontSize = 26, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = c.Name, FontSize = 13, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.65), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 3, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center });
                tile.Face.Child = sp;
                tile.Root.Margin = new Thickness(9);
                grid.Children.Add(tile.Root);
                tiles.Add(tile);
            }
            col.Children.Add(grid);
            Root.Children.Add(col);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Use this currency")));
            // this PC's own currency (or the one picked before) starts focused
            // (by country first: two entries share the dollar)
            index = Array.FindIndex(StoreData.Currencies, c => c.Country == StoreData.ChosenCountry);
            if (index < 0) index = Math.Max(0, Array.FindIndex(StoreData.Currencies, c => c.Code == StoreData.CurrencyCode));
        }

        public override void Shown()
        {
            for (int i = 0; i < tiles.Count; i++) Ui.Rise(tiles[i].Root, 80 + i * 30, 14, 260);
            ShowFocus();
        }

        private void ShowFocus() { for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(i == index); }

        public override void Handle(string b)
        {
            if (b == "B") { Ui.Back(); if (first) LoginScene.Finish(); else LoginScene.Show(back != null ? back() : new StorePage(), 0); return; }
            if (b.StartsWith("DPad"))
            {
                int next = Ui.GridMove(index, b, tiles.Count, Columns);
                if (next < 0) return;
                index = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b == "A")
            {
                Ui.Confirm();
                StoreData.SetCountry(StoreData.Currencies[index].Country);
                LoginScene.Show(back != null ? back() : new StorePage(), 0);
            }
        }
    }

    // The Store card on Spotlight: a few games as small pictures with their price, this week's free ones first, then
    // the biggest sales
    public static class StoreShowcase
    {
        private static string shownKey;
        private static readonly List<StoreItem> cardItems = new List<StoreItem>();
        private static readonly List<FrameworkElement> cards = new List<FrameworkElement>();
        private static readonly List<Tile> tiles = new List<Tile>();
        private static bool active;
        private static int focus;

        // Down from the Store card goes into its pictures: Left / Right pick one, A opens it in its store (Steam, Epic
        // Games...), Up or B goes back to the row. True when the press was the pictures'.
        public static bool Handle(string b)
        {
            if (!active)
            {
                if (b != "DPadDown" || cards.Count == 0 || !HomeNav.StoreCardFocused()) return false;
                active = true;
                focus = 0;
                Ui.Tick();
                HomeNav.DimStoreCard(true);
                ShowFocus();
                return true;
            }
            if (!HomeNav.StoreCardFocused()) { Leave(); return false; }
            switch (b)
            {
                case "DPadLeft":
                case "DPadRight":
                    int next = focus + (b == "DPadRight" ? 1 : -1);
                    if (next >= 0 && next < cards.Count) { focus = next; Ui.Tick(); ShowFocus(); }
                    return true;
                case "DPadDown":
                    return true;
                case "DPadUp":
                case "B":
                    Ui.Tick();
                    Leave();
                    return true;
                case "A":
                    var item = cardItems[focus];
                    Ui.Confirm();
                    HomeNav.Log("store card: opening " + item.Title + " (" + item.Link + ")");
                    try { HomeNav.OpenOutside(item.Link); }
                    catch (Exception ex) { HomeNav.Log("store card: open failed: " + ex.Message); Ui.Shake(cards[focus]); return true; }
                    Leave();
                    return true;
                default:
                    Leave();   // anything else (Start, View, LB/RB): back to the row and let it do its thing
                    return false;
            }
        }

        private static void Leave()
        {
            active = false;
            HomeNav.DimStoreCard(false);
            ShowFocus();
        }

        private static void ShowFocus()
        {
            for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(active && i == focus);
        }

        public static void Fill()
        {
            var w = Application.Current.MainWindow;
            var panel = w != null ? HomeNav.FindByName(w, "SeleneStoreShowcase") as StackPanel : null;
            if (panel == null) return;
            // the headline's games are up top already: the pictures below are the next best things
            Headline();
            var featured = new HashSet<string>(messages.Where(m => m.Item != null).Select(m => StoreData.Key(m.Item.Title ?? "")));
            var picks = new List<StoreItem>();
            var pool = StoreData.List("epic:free").Concat(StoreData.List("steam:sale").Concat(StoreData.List("epic:sale")).Where(i => i.Discount > 0).OrderByDescending(i => i.Discount))
                .Where(i => !i.Owned && !string.IsNullOrEmpty(i.Image));
            foreach (var s in pool)
            {
                if (picks.Count >= Ui.CardTiles) break;   // big pictures across the card, as many as the screen's width holds
                var k = StoreData.Key(s.Title ?? "");
                if (!featured.Contains(k) && !picks.Any(p => StoreData.Key(p.Title ?? "") == k)) picks.Add(s);
            }
            var key = string.Join("|", picks.Select(p => p.Title + p.Price));
            if (key == shownKey) return;
            shownKey = key;
            panel.Children.Clear();
            cards.Clear(); tiles.Clear(); cardItems.Clear();
            active = false;
            for (int i = 0; i < picks.Count; i++)
            {
                var card = Card(picks[i]);
                cards.Add(card);
                cardItems.Add(picks[i]);
                panel.Children.Add(card);
                Ui.Rise(card, i * 50, 12, 280);
            }
        }

        // ---- the card's headline: what's on now, taking turns (the card's own name already says "Store") ----
        private class Message { public string Head, About; public StoreItem Item; }
        private static List<Message> messages = new List<Message>();
        private static int shown = -1;
        private static System.Windows.Threading.DispatcherTimer turns;

        private static string Names(List<StoreItem> items)
        {
            var names = items.Select(i => i.Title).ToList();
            return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names.Last();
        }

        private static void Headline()
        {
            var list = new List<Message>();
            var free = StoreData.List("epic:free");
            if (free.Count > 0)
            {
                var until = free[0].Note != null && free[0].Note.StartsWith("Free until ") ? free[0].Note.Substring(11) : null;
                list.Add(new Message
                {
                    Head = free.Count == 1 ? "Free this week" : free.Count + " free games this week",
                    About = Names(free) + (free.Count == 1 ? " is" : " are") + " free to keep on Epic Games" + (until != null ? " until " + until : "") + ".",
                    Item = free[0]
                });
            }
            var best = StoreData.List("steam:sale").Concat(StoreData.List("epic:sale")).Concat(StoreData.List("gog:sale")).Where(i => i.Discount > 0 && !i.Free && !i.Owned).OrderByDescending(i => i.Discount).FirstOrDefault();
            if (best != null)
                list.Add(new Message { Head = "Up to " + best.Discount + "% off", About = best.Title + " is " + best.Price + " on " + best.StoreName + (best.OldPrice != null ? ", down from " + best.OldPrice : "") + ".", Item = best });
            var fresh = StoreData.List("steam:new").FirstOrDefault(i => !i.Owned && !string.IsNullOrEmpty(i.Image));
            if (fresh != null)
                list.Add(new Message { Head = "New on Steam", About = fresh.Title + " just came out" + (fresh.Price != null ? " · " + fresh.Price : "") + ".", Item = fresh });
            var ending = StoreData.List("epic:sale").Where(i => !i.Owned && i.Note != null && i.Note.StartsWith("Sale ends ")).OrderByDescending(i => i.Discount).FirstOrDefault();
            if (ending != null)
                list.Add(new Message { Head = "Sale ends " + ending.Note.Substring(10), About = ending.Title + " is " + ending.Discount + "% off on Epic Games, " + ending.Price + " until then.", Item = ending });
            var next = StoreData.List("epic:next");
            if (next.Count > 0)
            {
                var from = next[0].Note != null && next[0].Note.StartsWith("Free from ") ? next[0].Note.Substring(10) : null;
                list.Add(new Message { Head = "Free next", About = Names(next) + (next.Count == 1 ? " is" : " are") + " free to keep on Epic Games" + (from != null ? " from " + from : " soon") + ".", Item = next[0] });
            }
            if (list.Count == 0) return;
            bool first = messages.Count == 0;
            messages = list;
            if (turns == null)
            {
                turns = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                turns.Tick += (s, e) => Turn();
                turns.Start();
            }
            if (first || shown >= messages.Count) { shown = -1; Turn(); }
        }

        // the next message: the words fade out and back with the new ones (the wallpaper behind stays)
        private static void Turn()
        {
            if (messages.Count == 0) return;
            var w = Application.Current.MainWindow;
            var head = w != null ? HomeNav.FindByName(w, "SeleneStoreHead") as FrameworkElement : null;
            if (head == null) return;
            if (shown >= 0 && !head.IsVisible) return;   // the card isn't showing: it waits on the same message
            shown = (shown + 1) % messages.Count;
            var m = messages[shown];
            var res = Application.Current.Resources;
            if (!head.IsVisible) { res["SeleneStoreHeadline"] = m.Head; res["SeleneStoreAbout"] = m.About; }
            else Ui.Fade(head, 0, 180, 0, () =>
            {
                res["SeleneStoreHeadline"] = m.Head;
                res["SeleneStoreAbout"] = m.About;
                Ui.Rise(head, 0, 8, 280);
            });
        }

        // the store apps on this PC as chips with their logos (instead of the plain list)
        public static void FillChips()
        {
            var w = Application.Current.MainWindow;
            var panel = w != null ? HomeNav.FindByName(w, "SeleneStoreChips") as StackPanel : null;
            var names = w != null ? HomeNav.FindByName(w, "SeleneStoreNames") as UIElement : null;
            if (panel == null) return;
            panel.Children.Clear();
            foreach (var store in StoreScreen.Installed())
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = PlatformIcons.For(store);
                if (icon != null)
                    line.Children.Add(new System.Windows.Shapes.Path { Data = icon, Fill = Ink.Brush("SeleneInkColor"), Stretch = Stretch.Uniform, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                line.Children.Add(new TextBlock { Text = store, FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(new Border
                {
                    Child = line, CornerRadius = new CornerRadius(20), Padding = new Thickness(14, 7, 18, 8), Margin = new Thickness(0, 0, 10, 0),
                    Background = Ink.Brush("SeleneInkColor", 0.1), BorderBrush = Ink.Brush("SeleneInkColor", 0.18), BorderThickness = new Thickness(1)
                });
            }
            if (names != null) names.Visibility = panel.Children.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private static FrameworkElement Card(StoreItem item)
        {
            // Selene's tile (as on the Store page): the picture as its rounded fill, the focus ring glowing and the tile
            // growing a little when it's picked
            var tile = new Tile(420, 236, 14, 5) { FocusScale = 1.04 };
            tile.Root.MouseLeftButtonUp += (sender, args) =>
            {
                try { Ui.Confirm(); HomeNav.OpenOutside(item.Link); }
                catch (Exception ex) { HomeNav.Log("store card: open failed: " + ex.Message); }
                args.Handled = true;
            };
            tile.Root.Margin = new Thickness(0, 0, 22, 0);
            var frame = new Grid();
            var pic = new Border { CornerRadius = new CornerRadius(14), Opacity = 0 };
            frame.Children.Add(pic);
            StorePage.LoadFill(pic, StoreData.Sharp(item), 900);   // sharp at high-DPI too
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            var badge = item.Free ? "FREE" : item.Discount > 0 ? "-" + item.Discount + "%" : null;
            if (badge != null)
                chip.Children.Add(new Border { Background = Ink.Brush("SeleneFocusColor"), CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 2, 8, 3), Child = new TextBlock { Text = badge, FontSize = 17, FontWeight = FontWeights.Bold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneOnFocusColor") } });
            var price = item.Free ? (item.Note ?? "") : item.Price ?? "";
            if (price.Length > 0) chip.Children.Add(new TextBlock { Text = price, FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(badge != null ? 8 : 0, 0, 0, 0) });
            if (chip.Children.Count > 0)
                frame.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10), Padding = new Thickness(5, 4, 10, 4), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), Child = chip });
            tile.Face.Child = frame;
            tiles.Add(tile);
            return tile.Root;
        }
    }
}
