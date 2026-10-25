using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Selene
{
    // Selene's Store: what's free, on sale and new across the stores, Spotlight-style. LB/RB pick which store shows (All,
    // Steam, Epic Games, GOG, Xbox, the other stores); rows of games underneath, the focused one big at the top. A opens a
    // game's page, Y the store apps themselves. Buying happens in the store's own app (Selene opens the game's page there).
    public class StorePage : Screen
    {
        private class Row
        {
            public string Title;
            public string[] Keys;
            public List<StoreItem> Items;
            public StackPanel Panel;
            public FrameworkElement Block;   // the row with its name (blurred when it isn't the focused one)
            public List<Tile> Tiles = new List<Tile>();
            public List<Action> Loaders = new List<Action>();   // each tile's picture, fetched once it's near the screen
            public TranslateTransform Shift = new TranslateTransform();
            public int Index;
        }

        private static readonly string[] Tabs = { "All", "Steam", "Epic Games", "GOG", "Xbox", "More stores" };
        private const double TileW = 420, TileH = 236, Pitch = TileW + 26, RowH = 318, Peek = 90;   // Peek: how much of the next row shows below
        private GradientStop fadeAbove, fadeTop, fadeMid;   // the rows' fade: the top edge, where it starts (under the focused row) and its first step

        // where you were, so coming back from a game's page puts you there again
        private static int savedTab = -1, savedRow;   // -1: not opened yet (the Store opens on setting decides)
        private static readonly Dictionary<string, int> savedIndex = new Dictionary<string, int>();

        private int tab, rowIndex;
        private readonly List<Row> rows = new List<Row>();
        private readonly StackPanel tabBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 36, 0, 0) };
        private readonly List<Border> tabPills = new List<Border>();
        private readonly StackPanel rowStack = new StackPanel();
        private readonly TranslateTransform rowShift = new TranslateTransform();
        private readonly Image heroA = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 }, heroB = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
        private bool heroFront;
        private readonly System.Windows.Shapes.Path storeIcon;
        private readonly TextBlock storeLabel, title, price, oldPrice, note, status, facts, about;
        private bool rebuildQueued, opening;
        private readonly Border discount, owned;
        private readonly StackPanel info;
        private readonly System.Windows.Threading.DispatcherTimer heroLater = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        private bool closed;

        public StorePage()
        {
            tab = savedTab >= 0 ? savedTab : Math.Max(0, Array.IndexOf(Tabs, Features.Value("storetab")));
            rowIndex = savedRow;

            // a dark base like Collection's (no wallpaper in the Store: the games' art is the picture here)
            var dark = new Border();
            dark.SetResourceReference(Border.BackgroundProperty, "SeleneScrimBrush");
            Root.Children.Add(dark);
            // the focused game's picture: big, top right, melting into the backdrop (the Collection trailer's mask)
            var hero = new Grid { Width = 1500, Height = 844, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            var mask = SelenePaths.ThemeFile("Images", "TrailerMask.png");
            if (System.IO.File.Exists(mask)) hero.OpacityMask = new ImageBrush(new BitmapImage(new Uri(mask))) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(heroA, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(heroB, BitmapScalingMode.HighQuality);
            hero.Children.Add(heroA);
            hero.Children.Add(heroB);
            Root.Children.Add(hero);
            // a shade from the left keeps the text readable
            Root.Children.Add(new Border
            {
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0.85, 0), Ink.Stop("SeleneScrimColor", 0.35, 0.45), Ink.Stop("SeleneScrimColor", 0, 0.7) }, 0)
            });

            // store tabs (LB / RB), drawn like the home's filter tabs
            tabBar.Children.Add(HomeNav.FilterBadge("LB"));
            foreach (var t in Tabs)
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 5, 20, 7), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5),
                    Child = new TextBlock { Text = t, FontSize = 19, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), VerticalAlignment = VerticalAlignment.Center }
                };
                tabPills.Add(pill);
                tabBar.Children.Add(pill);
            }
            tabBar.Children.Add(HomeNav.FilterBadge("RB"));
            Root.Children.Add(tabBar);

            // the focused game: store, name, price
            info = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(96, 130, 0, 0), MaxWidth = 860 };
            storeLabel = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("GlyphColor"), VerticalAlignment = VerticalAlignment.Center };
            storeIcon = new System.Windows.Shapes.Path { Width = 18, Height = 18, Stretch = Stretch.Uniform, Fill = Ink.Brush("GlyphColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
            var storeLine = new StackPanel { Orientation = Orientation.Horizontal };
            storeLine.Children.Add(storeIcon);
            storeLine.Children.Add(storeLabel);
            title = new TextBlock { FontSize = 54, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            var priceLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            discount = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 14, 0), Background = Ink.Brush("SeleneFocusColor"), VerticalAlignment = VerticalAlignment.Center };
            discount.Child = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneOnFocusColor") };
            oldPrice = new TextBlock { FontSize = 21, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.5), TextDecorations = TextDecorations.Strikethrough, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            price = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center };
            owned = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(16, 0, 0, 0), Background = Ink.Brush("SeleneInkColor", 0.16), VerticalAlignment = VerticalAlignment.Center };
            owned.Child = new TextBlock { Text = "In your library", FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor") };
            priceLine.Children.Add(discount); priceLine.Children.Add(oldPrice); priceLine.Children.Add(price); priceLine.Children.Add(owned);
            note = new TextBlock { FontSize = 18, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.7), Margin = new Thickness(0, 12, 0, 0) };
            facts = new TextBlock { FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), Margin = new Thickness(0, 10, 0, 0) };
            about = new TextBlock { FontSize = 19, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.82), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis, MaxHeight = 84, LineHeight = 28, Margin = new Thickness(0, 14, 0, 0) };
            info.Children.Add(storeLine); info.Children.Add(title); info.Children.Add(facts); info.Children.Add(priceLine); info.Children.Add(note); info.Children.Add(about);
            Root.Children.Add(info);

            // rows of games; the focused row glides to the top of this area
            // the focused row, and below it the top of the next one, blurred and fading to dark: there's more down there
            var rowsArea = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 600, 0, 0), Height = RowH + Peek, ClipToBounds = true };
            // in pixels of this area (relative to the content, the gradient stretched over every row and never faded: a hard
            // edge); the fade starts just under the focused row's pictures and reaches nothing at the bottom
            double h = RowH + Peek;
            var fade = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, h) };
            // the top edge clears when the last row sits low (the bottom of the row above ends softly instead of cut off)
            fade.GradientStops.Add(fadeAbove = new GradientStop(Colors.Black, 0));
            fade.GradientStops.Add(new GradientStop(Colors.Black, (Peek - 16) / h));
            fade.GradientStops.Add(fadeTop = new GradientStop(Colors.Black, RowH / h));   // the focused row stays whole
            fade.GradientStops.Add(fadeMid = new GradientStop(Color.FromArgb(0x70, 0, 0, 0), (RowH + 30) / h));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
            rowsArea.OpacityMask = fade;
            rowStack.RenderTransform = rowShift;
            rowStack.Margin = new Thickness(96, 0, 0, 0);
            rowsArea.Children.Add(new Canvas { Children = { rowStack } });
            Root.Children.Add(rowsArea);

            status = new TextBlock { FontSize = 18, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(98, 640, 0, 0) };
            Root.Children.Add(status);
            // nothing to show yet (first open, or the currency just changed): a loading card in the middle, not a bare dark page
            var ring = new System.Windows.Shapes.Ellipse { Width = 54, Height = 54, StrokeThickness = 4, Stroke = Ink.Brush("GlyphColor"), StrokeDashArray = new DoubleCollection { 22, 14 }, StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5), HorizontalAlignment = HorizontalAlignment.Center };
            var spin = new RotateTransform();
            ring.RenderTransform = spin;
            spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
            var loadingWords = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            loadingWords.Children.Add(ring);
            loadingWords.Children.Add(new TextBlock { Text = "Loading the store…", FontSize = 26, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) });
            loadingWords.Children.Add(new TextBlock { Text = "Fetching prices and deals.", FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            loading = new Grid { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            loading.Children.Add(loadingWords);
            Root.Children.Add(loading);

            var leftHints = Ui.Prompts(false, Ui.Prompt("B", "Back"), Ui.Prompt("Y", "Store apps"), Ui.Prompt("X", "Currency"));
            var rightHints = Ui.Prompts(true, Ui.Prompt("A", "Details"));
            leftHints.Margin = new Thickness(46, 0, 0, 30);   // lower than elsewhere: centered in the band under the rows' fade
            rightHints.Margin = new Thickness(0, 0, 64, 30);
            Root.Children.Add(leftHints);
            Root.Children.Add(rightHints);

            heroLater.Tick += (s, e) => { heroLater.Stop(); ShowHero(); };
        }

        private static Border Badge(string text)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Ink.Brush("SeleneInkColor", 0.85), Child = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneScrimColor") }
            };
        }

        public override void Shown()
        {
            ShowTabs(false);
            opening = true;
            StoreData.Load(key => { if (!closed && !opening) Arrived(key); });   // the saved lists come in at once: built below
            opening = false;
            Build();
            Ui.Rise(info, 60, 16, 320);
        }

        public override void Hidden() { closed = true; heroLater.Stop(); if (rebuildLater != null) rebuildLater.Stop(); }
        private System.Windows.Threading.DispatcherTimer rebuildLater;

        // a list came in: rebuild the rows if it belongs to this tab (keeping the place)
        private void Arrived(string key)
        {
            if (!RowsFor(tab).Any(r => r.Item2.Contains(key))) { ShowStatus(); return; }
            if (rebuildLater == null)
            {
                rebuildLater = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                rebuildLater.Tick += (s, e) => { rebuildLater.Stop(); rebuildQueued = false; if (!closed) Build(); };
            }
            rebuildQueued = true;
            rebuildLater.Stop();
            rebuildLater.Start();
        }

        // the rows each tab shows, and the lists they're made of (mixed in turn)
        private static List<Tuple<string, string[]>> RowsFor(int t)
        {
            var r = new List<Tuple<string, string[]>>();
            Action<string, string[]> add = (name, keys) => r.Add(Tuple.Create(name, keys));
            switch (Tabs[t])
            {
                case "Steam": add("On sale", new[] { "steam:sale" }); add("Top sellers", new[] { "steam:top" }); add("New releases", new[] { "steam:new" }); add("Coming soon", new[] { "steam:soon" }); break;
                case "Epic Games": add("Free now", new[] { "epic:free" }); add("Free next", new[] { "epic:next" }); add("On sale", new[] { "epic:sale" }); break;
                case "GOG": add("On sale", new[] { "gog:sale" }); add("Bestsellers", new[] { "gog:best" }); break;
                case "Xbox": add("PC Game Pass", new[] { "xbox:gamepass" }); break;
                case "More stores": add("Best deals", new[] { "deals:best" }); break;
                default:
                    add("Free now", new[] { "epic:free" });
                    add("On sale", new[] { "steam:sale", "epic:sale", "gog:sale" });
                    add("Top sellers", new[] { "steam:top" });
                    add("New releases", new[] { "steam:new" });
                    add("PC Game Pass", new[] { "xbox:gamepass" });
                    add("Best deals in other stores", new[] { "deals:best" });
                    add("Coming soon", new[] { "epic:next", "steam:soon" });
                    break;
            }
            return r;
        }

        private static List<StoreItem> Mix(string[] keys)
        {
            var lists = keys.Select(StoreData.List).ToList();
            var all = new List<StoreItem>();
            var seen = new HashSet<string>();
            for (int i = 0; lists.Any(l => i < l.Count); i++)
                foreach (var l in lists)
                    if (i < l.Count && l[i].Title != null && seen.Add(StoreData.Key(l[i].Title))) all.Add(l[i]);
            return all;
        }

        private void Build()
        {
            var focusedTitle = rows.Count > 0 && rowIndex < rows.Count ? rows[rowIndex].Title : null;
            rows.Clear();
            rowStack.Children.Clear();
            foreach (var spec in RowsFor(tab))
            {
                var items = Mix(spec.Item2);
                if (items.Count == 0) continue;
                var row = new Row { Title = spec.Item1, Items = items, Keys = spec.Item2 };
                int saved;
                row.Index = savedIndex.TryGetValue(Tabs[tab] + "/" + row.Title, out saved) ? Math.Min(saved, items.Count - 1) : 0;
                var block = new StackPanel { Height = RowH };
                block.Children.Add(new TextBlock { Text = row.Title, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), Margin = new Thickness(2, 0, 0, 14) });
                row.Panel = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = row.Shift };
                EnsureTiles(row, 8);   // the rest as you scroll (EnsureTiles in LoadNearby)
                block.Children.Add(row.Panel);
                row.Block = block;
                rowStack.Children.Add(block);
                rows.Add(row);
            }
            if (focusedTitle != null) { int i = rows.FindIndex(r => r.Title == focusedTitle); if (i >= 0) rowIndex = i; }
            rowIndex = Math.Max(0, Math.Min(rowIndex, rows.Count - 1));
            foreach (var r in rows) r.Shift.X = -Math.Max(0, r.Index - 1) * Pitch;
            PlaceRows(false);
            foreach (var r in rows) r.Shift.BeginAnimation(TranslateTransform.XProperty, null);
            LoadNearby();
            ShowFocus(false);
            ShowStatus();
        }

        // where the rows sit: the focused row at the top of the area with the next one peeking in below. The last row
        // (nothing below it) sits lower, where that peek would be, so the page doesn't end in an empty band above the
        // hints; the fade under the focused row steps out of the way then (it would dim the row's own pictures).
        private void PlaceRows(bool animate)
        {
            bool last = rowIndex == rows.Count - 1;
            double h = RowH + Peek;
            var ms = TimeSpan.FromMilliseconds(animate ? 260 : 0);
            rowShift.BeginAnimation(TranslateTransform.YProperty, Ui.Fast(new DoubleAnimation(-rowIndex * RowH + (last ? Peek : 0), ms) { EasingFunction = Ui.Out() }));
            if (animate) Ui.MeasureFrames("store row glide", 300);
            fadeAbove.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(last ? Color.FromArgb(0, 0, 0, 0) : Colors.Black, ms));
            fadeTop.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(last ? 1 : RowH / h, ms));
            fadeMid.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(last ? 1 : (RowH + 30) / h, ms));
        }

        // near the end of a row: its lists fetch their next batch, which is added to the row's end
        private void MoreFor(Row row)
        {
            if (row.Index < row.Items.Count - 6) return;
            foreach (var key in row.Keys)
                if (StoreData.CanMore(key)) StoreData.More(key, k => { if (!closed) Extend(k); });
        }

        private void Extend(string key)
        {
            foreach (var row in rows)
            {
                if (!row.Keys.Contains(key)) continue;
                var have = new HashSet<string>(row.Items.Select(i => StoreData.Key(i.Title ?? "")));
                foreach (var item in StoreData.List(key))
                {
                    if (!have.Add(StoreData.Key(item.Title ?? ""))) continue;
                    row.Items.Add(item);   // its tile is made when it comes near
                }
            }
            LoadNearby();
        }

        // the pictures of the tiles on or near the screen (a row above and two below, a few tiles around the focused one)
        // a row's tiles up to `upTo` games (made only as they come near the screen)
        private void EnsureTiles(Row row, int upTo)
        {
            int end = Math.Min(row.Items.Count, upTo);
            while (row.Tiles.Count < end)
            {
                Action load;
                var tile = MakeTile(row.Items[row.Tiles.Count], out load);
                row.Tiles.Add(tile);
                row.Loaders.Add(load);
                row.Panel.Children.Add(tile.Root);
            }
        }

        private void LoadNearby()
        {
            for (int r = Math.Max(0, rowIndex - 1); r < Math.Min(rows.Count, rowIndex + 3); r++)
            {
                var row = rows[r];
                EnsureTiles(row, row.Index + 8);
                for (int i = Math.Max(0, row.Index - 2); i < Math.Min(row.Loaders.Count, row.Index + 6); i++)
                {
                    var load = row.Loaders[i];
                    if (load == null) continue;
                    row.Loaders[i] = null;
                    load();
                }
            }
        }

        private Tile MakeTile(StoreItem item, out Action load)
        {
            var tile = new Tile(TileW, TileH, 16, 5) { FocusScale = 1.06 };
            tile.Root.Margin = new Thickness(0, 0, Pitch - TileW, 0);
            var g = new Grid();
            var pic = new Border { CornerRadius = new CornerRadius(15), Opacity = 0 };   // the picture as a rounded fill (an Image kept square corners)
            g.Children.Add(pic);
            var url = StoreData.Sharp(item);
            load = () => LoadFill(pic, url, 760);
            // price chip, bottom right
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            if (item.Discount > 0 && !item.Free)
                chip.Children.Add(new Border { Background = Ink.Brush("SeleneFocusColor"), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 6, 0), Child = new TextBlock { Text = "-" + item.Discount + "%", FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneOnFocusColor") } });
            var label = item.Free ? "Free" : item.Price ?? item.Note;
            if (!string.IsNullOrEmpty(label)) chip.Children.Add(new TextBlock { Text = label, FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            if (chip.Children.Count > 0)
                g.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(6, 4, 10, 4), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), Child = chip });
            if (item.Owned)
                g.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), Child = new TextBlock { Text = "Owned", FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Brushes.White } });
            tile.Face.Child = g;
            return tile;
        }

        // the same for a rounded Border: the picture becomes its fill
        internal static void LoadFill(Border target, string url, int width)
        {
            if (string.IsNullOrEmpty(url)) return;
            var mine = new object();
            target.Tag = mine;
            StoreImages.Get(url, width, img =>
            {
                if (img == null || target.Tag != mine) return;
                var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
                target.Background = brush;
                target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ui.Out() });
            });
        }

        // a picture from the web (StoreImages: a few at a time, kept on disk), fading in once it's there. A newer request
        // for the same Image wins (the tag).
        internal static void Load(Image target, string url, int width)
        {
            if (string.IsNullOrEmpty(url)) return;
            var mine = new object();
            target.Tag = mine;
            StoreImages.Get(url, width, img =>
            {
                if (img == null || target.Tag != mine) return;
                target.Source = img;
                target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ui.Out() });
            });
        }

        private StoreItem Focused { get { return rows.Count > 0 && rowIndex < rows.Count && rows[rowIndex].Items.Count > 0 ? rows[rowIndex].Items[rows[rowIndex].Index] : null; } }

        private void ShowTabs(bool animate)
        {
            for (int i = 0; i < tabPills.Count; i++)
            {
                bool on = i == tab;
                tabPills[i].Background = on ? (Brush)Ink.Brush("SeleneFocusColor", 0.95) : Brushes.Transparent;
                var label = (TextBlock)tabPills[i].Child;
                label.Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.7);
                label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                if (on && animate) Ui.Pop(tabPills[i]);
            }
        }

        private void ShowFocus(bool animate)
        {
            // the rows other than the focused one are softly blurred (the next one peeks in below)
            for (int r = 0; r < rows.Count; r++)
            {
                if (rows[r].Block == null) continue;
                var blur = rows[r].Block.Effect as System.Windows.Media.Effects.BlurEffect;
                if (blur == null) { blur = new System.Windows.Media.Effects.BlurEffect { Radius = r == rowIndex ? 0 : 5, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance }; rows[r].Block.Effect = blur; }
                blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(r == rowIndex ? 0 : 5, TimeSpan.FromMilliseconds(animate ? 240 : 0)));
            }
            for (int r = 0; r < rows.Count; r++)
                for (int i = 0; i < rows[r].Tiles.Count; i++) rows[r].Tiles[i].Focus(r == rowIndex && i == rows[r].Index);
            var item = Focused;
            info.Visibility = item != null ? Visibility.Visible : Visibility.Hidden;
            if (item == null) { heroA.Opacity = heroB.Opacity = 0; return; }
            storeLabel.Text = item.StoreName.ToUpperInvariant();
            var logo = PlatformIcons.For(item.StoreName);
            storeIcon.Data = logo;
            storeIcon.Visibility = logo != null ? Visibility.Visible : Visibility.Collapsed;
            title.Text = item.Title;
            price.Text = item.Free ? "Free" : item.Price ?? "";
            oldPrice.Text = item.OldPrice ?? "";
            oldPrice.Visibility = string.IsNullOrEmpty(item.OldPrice) ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)discount.Child).Text = item.Free ? "Free to keep" : "-" + item.Discount + "%";
            discount.Visibility = item.Discount > 0 ? Visibility.Visible : Visibility.Collapsed;
            owned.Visibility = item.Owned ? Visibility.Visible : Visibility.Collapsed;
            note.Text = item.Note ?? "";
            note.Visibility = string.IsNullOrEmpty(item.Note) || item.Note == price.Text ? Visibility.Collapsed : Visibility.Visible;
            facts.Text = ""; about.Text = "";   // filled once the selection rests (ShowHero)
            facts.Visibility = about.Visibility = Visibility.Collapsed;
            if (animate) { info.Opacity = 0.4; Ui.Fade(info, 1, 180); }
            heroLater.Stop();
            heroLater.Start();   // the big picture follows once the selection rests a moment
        }

        private void ShowHero()
        {
            var item = Focused;
            if (item == null) return;
            var front = heroFront ? heroA : heroB;
            var back = heroFront ? heroB : heroA;
            heroFront = !heroFront;
            back.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            front.BeginAnimation(UIElement.OpacityProperty, null);
            front.Opacity = 0;
            Panel.SetZIndex(front, 1); Panel.SetZIndex(back, 0);
            var mine = new object();
            front.Tag = mine;
            StoreData.BigArt(item, url => { if (url != null && front.Tag == mine && !closed) Load(front, url, 1920); });
            StoreData.LoadDetails(item, d =>
            {
                if (closed || Focused != item) return;
                facts.Text = d == null ? "" : string.Join("   ·   ", new[] { d.Genres, d.Released, d.Score }.Where(x => !string.IsNullOrEmpty(x)));
                about.Text = d != null ? d.About ?? "" : "";
                facts.Visibility = facts.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                about.Visibility = about.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                Ui.Fade(about, 1, 220);
            });
        }

        private Grid loading;   // the centred loading card while nothing has arrived

        private void ShowStatus()
        {
            bool any = rows.Count > 0;
            bool fetching = StoreData.LoadedAt == DateTime.MinValue;
            if (!any) status.Text = fetching ? "" : "Nothing here right now.";
            if (loading != null) loading.Visibility = !any && fetching ? Visibility.Visible : Visibility.Collapsed;
            else if (StoreData.FromDisk)
            {
                var ago = DateTime.Now - StoreData.LoadedAt;
                status.Text = "";
                note.Text = (note.Text.Length > 0 ? note.Text + "   ·   " : "") + "Offline: prices from " + (ago.TotalHours >= 1 ? (int)ago.TotalHours + " h" : Math.Max(1, (int)ago.TotalMinutes) + " min") + " ago";
                note.Visibility = Visibility.Visible;
            }
            else status.Text = "";
            status.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Remember()
        {
            savedTab = tab;
            savedRow = rowIndex;
            foreach (var r in rows) savedIndex[Tabs[tab] + "/" + r.Title] = r.Index;
        }

        public override void Handle(string b)
        {
            if (b == "B") { Remember(); Ui.Back(); LoginScene.Finish(); return; }
            if (b == "Y") { Remember(); Ui.Confirm(); LoginScene.Show(new StoreScreen(), 1); return; }
            if (b == "X") { Remember(); Ui.Confirm(); LoginScene.Show(new StoreCurrencyScreen(false), 1); return; }
            if (b == "LeftShoulder" || b == "RightShoulder")
            {
                int next = (tab + (b == "RightShoulder" ? 1 : Tabs.Length - 1)) % Tabs.Length;
                Remember();
                tab = next;
                rowIndex = 0;
                Ui.Tick();
                ShowTabs(true);
                rows.Clear();
                Build();
                rowStack.Opacity = 0;
                Ui.Fade(rowStack, 1, 220);
                return;
            }
            if (rows.Count == 0) return;
            var row = rows[rowIndex];
            switch (b)
            {
                case "DPadLeft":
                case "DPadRight":
                {
                    int next = row.Index + (b == "DPadRight" ? 1 : -1);
                    if (next < 0 || next >= row.Items.Count) return;
                    row.Index = next;
                    row.Shift.BeginAnimation(TranslateTransform.XProperty, Ui.Fast(new DoubleAnimation(-Math.Max(0, next - 1) * Pitch, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ui.Out() }));
                    Ui.MeasureFrames("store tile glide", 260);
                    Ui.Tick();
                    LoadNearby();
                    MoreFor(row);
                    ShowFocus(true);
                    return;
                }
                case "DPadUp":
                case "DPadDown":
                {
                    int next = rowIndex + (b == "DPadDown" ? 1 : -1);
                    if (next < 0 || next >= rows.Count) return;
                    rowIndex = next;
                    PlaceRows(true);
                    Ui.Tick();
                    LoadNearby();
                    ShowFocus(true);
                    return;
                }
                case "A":
                {
                    var item = Focused;
                    if (item == null) return;
                    Remember();
                    Ui.Confirm();
                    LoginScene.Show(new StoreGameScreen(item), 0);   // a crossfade (sliding in sideways looked off)
                    return;
                }
            }
        }
    }
}
