using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // One game from the Store. Its screenshots fill the screen behind (dimmed on the left), with the store, name, facts,
    // price and description on the left and the main button: Play / Install when it's in your library, Get it free, or Buy
    // on its store. A row of screenshots along the bottom (Left / Right show each one behind), the cheapest price in another
    // store when there is one (Y), and Down: more games from the same store.
    public class StoreGameScreen : Screen
    {
        private readonly StoreItem item;
        private readonly Playnite.SDK.Models.Game owned;
        private readonly Image shotA = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 }, shotB = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
        private readonly TextBlock about, facts, elsewhereText;
        private readonly FrameworkElement elsewhereLine;
        private readonly StackPanel shotStrip = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly List<Tile> shotTiles = new List<Tile>();
        private readonly List<string> shots = new List<string>();
        private readonly StackPanel moreRow = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TranslateTransform moreShift = new TranslateTransform();
        private readonly List<Tile> moreTiles = new List<Tile>();
        private readonly List<StoreItem> more;
        private readonly TranslateTransform pageShift = new TranslateTransform();
        private readonly DispatcherTimer slides = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        private StoreData.Elsewhere elsewhere;
        private WebTrailer trailer;
        private Grid trailerHost;
        private int shot, moreIndex, zone;   // zone 0: the screenshots, 1: more from the store
        // the buttons (Buy / Play, Web page) are where the focus starts; Down goes to the screenshots, Down again to "More from"
        private readonly List<Border> pills = new List<Border>();
        private readonly List<Action> pillActions = new List<Action>();
        private int pill;
        private bool onActions = true;
        private Grid shotViewer;   // a screenshot on the whole screen (A on the strip)
        private Image bigShot;
        private bool viewing;
        private bool front, closed;
        private const double MoreW = 380, MoreH = 214, MorePitch = MoreW + 24;
        private const double MoreLift = 300;   // how far the page glides up for the "More from" row: just enough to show it above the hints
        // the left edge of the words and the screenshots: six 272 px screenshots, 20 apart, centered on the 1920 screen
        private const double Side = (1920 - (6 * 272 + 5 * 20)) / 2.0;
        private StackPanel moreSection;

        // the "More from" row as a peek (blurred, fading out) or whole; it melts between the two along with the page's
        // glide (switched at once, the row vanished before the page had moved)
        private readonly LinearGradientBrush peekFade = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, 220),
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0.4), new GradientStop(Color.FromArgb(0x60, 0, 0, 0), 0.55), new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.72) }
        };
        private readonly System.Windows.Media.Effects.BlurEffect peekBlur = new System.Windows.Media.Effects.BlurEffect { Radius = 0, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };

        private void Peek(bool on, bool animate)
        {
            if (moreSection == null) return;
            if (moreSection.OpacityMask == null) { moreSection.OpacityMask = peekFade; moreRow.Effect = peekBlur; }
            var ms = TimeSpan.FromMilliseconds(animate ? 320 : 0);
            // whole: the fade's end is pushed far below the row (all of it opaque) instead of taking the mask off
            peekFade.BeginAnimation(LinearGradientBrush.EndPointProperty, new PointAnimation(new Point(0, on ? 220 : 1400), ms) { EasingFunction = Ui.Out() });
            peekBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(on ? 5 : 0, ms));
        }

        private readonly Func<Screen> back;   // where B goes: the Store (also from a game opened in another's "More from" row)

        public StoreGameScreen(StoreItem item, Func<Screen> back = null, int startZone = 0, int startMore = 0)
        {
            this.item = item;
            this.back = back;
            owned = StoreData.FindOwned(item);
            more = StoreData.MoreFrom(item);
            var dark = new Border();
            dark.SetResourceReference(Border.BackgroundProperty, "SeleneScrimBrush");
            Panel.SetZIndex(dark, -10);   // under the screenshots (they sit at -1 / -2; at 0 this hid them: a black page)
            Root.Children.Add(dark);
            foreach (var img in new[] { shotA, shotB }) { RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality); Root.Children.Add(img); }
            // the trailer as in Collection: a 16:9 box at the top right, its edges melting into the dark (the trailer mask)
            trailerHost = new Grid { IsHitTestVisible = false, Width = 1422, Height = 800, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
            var mask = SelenePaths.ThemeFile("Images", "TrailerMask.png");
            if (System.IO.File.Exists(mask)) trailerHost.OpacityMask = new ImageBrush(new System.Windows.Media.Imaging.BitmapImage(new Uri(mask))) { Stretch = Stretch.Fill };
            Panel.SetZIndex(trailerHost, -1);   // over the screenshots, under the words
            Root.Children.Add(trailerHost);
            Root.Children.Add(new Border
            {
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0.92, 0), Ink.Stop("SeleneScrimColor", 0.6, 0.45), Ink.Stop("SeleneScrimColor", 0.1, 0.85) }, 0)
            });
            Root.Children.Add(new Border
            {
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0, 0.55), Ink.Stop("SeleneScrimColor", 0.9, 1) }, 90)
            });

            // on a canvas, as wide as the screen and taller: a page laid out in the screen's own grid got clipped to the screen,
            // and the clip moved up with it (the "More from" row stayed cut off even after gliding up)
            var page = new Grid { RenderTransform = pageShift, Height = 1700 };
            page.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = Root });
            var sheet = new Canvas();
            sheet.Children.Add(page);
            Root.Children.Add(sheet);

            // left: the store, the name, the facts, the price, the description, the buttons
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side, 120, 0, 0), MaxWidth = 860 };
            var storeLine = new StackPanel { Orientation = Orientation.Horizontal };
            var logo = PlatformIcons.For(item.StoreName);
            if (logo != null) storeLine.Children.Add(new System.Windows.Shapes.Path { Data = logo, Width = 20, Height = 20, Stretch = Stretch.Uniform, Fill = Ink.Brush("GlyphColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            storeLine.Children.Add(new TextBlock { Text = item.StoreName.ToUpperInvariant(), FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("GlyphColor"), VerticalAlignment = VerticalAlignment.Center });
            col.Children.Add(storeLine);
            col.Children.Add(new TextBlock { Text = item.Title, FontSize = 60, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            facts = new TextBlock { FontSize = 18, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.65), Margin = new Thickness(0, 10, 0, 0) };
            col.Children.Add(facts);
            var priceLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
            if (item.Discount > 0)
                priceLine.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 14, 0), Background = Ink.Brush("SeleneFocusColor"), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = item.Free ? "Free to keep" : "-" + item.Discount + "%", FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneOnFocusColor") } });
            if (!string.IsNullOrEmpty(item.OldPrice))
                priceLine.Children.Add(new TextBlock { Text = item.OldPrice, FontSize = 22, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.5), TextDecorations = TextDecorations.Strikethrough, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            priceLine.Children.Add(new TextBlock { Text = item.Free ? "Free" : item.Price ?? item.Note ?? "", FontSize = 34, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
            if (owned != null)
                priceLine.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(16, 0, 0, 0), Background = Ink.Brush("SeleneInkColor", 0.16), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "In your library", FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor") } });
            col.Children.Add(priceLine);
            if (!string.IsNullOrEmpty(item.Note) && item.Note != item.Price && item.Price != null)
                col.Children.Add(new TextBlock { Text = item.Note, FontSize = 18, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.7), Margin = new Thickness(0, 10, 0, 0) });
            // the cheapest price in another store (filled in when found)
            elsewhereText = new TextBlock { FontSize = 18, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.85), VerticalAlignment = VerticalAlignment.Center };
            var elsewhereRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed, Opacity = 0 };
            elsewhereRow.Children.Add(Ui.Prompt("Y", ""));
            ((StackPanel)elsewhereRow.Children[0]).Margin = new Thickness(0);
            elsewhereRow.Children.Add(elsewhereText);
            elsewhereText.Margin = new Thickness(10, 0, 0, 0);
            elsewhereLine = elsewhereRow;
            col.Children.Add(elsewhereRow);
            about = new TextBlock { Opacity = 0, FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.85), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis, MaxHeight = 120, LineHeight = 30, Margin = new Thickness(0, 22, 0, 0) };
            col.Children.Add(about);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 30, 0, 0) };
            AddPill(actions, "A", MainLabel(), RunMain);
            if (item.WebLink != null && item.WebLink != item.Link) AddPill(actions, "X", "Web page", OpenWeb);
            col.Children.Add(actions);
            page.Children.Add(col);

            // bottom: the screenshots, then more from the same store (Down)
            var bottom = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side, 750, 0, 0) };
            bottom.Children.Add(shotStrip);
            if (more.Count > 0)
            {
                // more from the store: under the screenshots it only peeks in, lightly blurred and fading to dark; Down shows it
                moreSection = new StackPanel();
                moreSection.Children.Add(new TextBlock { Text = "More from " + item.StoreName, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), Margin = new Thickness(2, 64, 0, 14) });
                moreRow.RenderTransform = moreShift;
                foreach (var m in more)
                {
                    var t = new Tile(MoreW, MoreH, 15, 5) { FocusScale = 1.05 };
                    t.Root.Margin = new Thickness(0, 0, MorePitch - MoreW, 0);
                    var fill = new Border { CornerRadius = new CornerRadius(14), Opacity = 0 };
                    t.Face.Child = fill;
                    StorePage.LoadFill(fill, StoreData.Sharp(m), 760);
                    moreRow.Children.Add(t.Root);
                    moreTiles.Add(t);
                }
                // on a canvas: laid out in the page, the row got trimmed to its slot, and the trim moved with it as it
                // scrolled (games cut off on the right) and as a tile grew (its ring cut at the top and bottom)
                var rowSheet = new Canvas { Height = MoreH + 24, Margin = new Thickness(0, 0, 0, 12) };
                Canvas.SetTop(moreRow, 12);
                rowSheet.Children.Add(moreRow);
                moreSection.Children.Add(rowSheet);
                bottom.Children.Add(moreSection);
                Peek(true, false);
            }
            page.Children.Add(bottom);

            var left = Ui.Prompts(false, Ui.Prompt("B", "Back"));
            var right = Ui.Prompts(true, Ui.Prompt("A", "Select"));
            left.Margin = new Thickness(46, 0, 0, 30);
            right.Margin = new Thickness(0, 0, 64, 30);
            Root.Children.Add(left);
            Root.Children.Add(right);
            slides.Tick += (s, e) => { if (zone == 0 && shots.Count > 1) ShowShot((shot + 1) % shots.Count, false); };
            // coming back to this page from a game opened in its "More from" row: there, on that game
            if (startZone == 1 && moreTiles.Count > 0)
            {
                zone = 1;
                onActions = false;
                moreIndex = Math.Max(0, Math.Min(startMore, moreTiles.Count - 1));
                pageShift.Y = -MoreLift;
                moreShift.X = -Math.Max(0, moreIndex - 1) * MorePitch;
                Peek(false, false);
            }
        }

        // the main button: play it if it's yours, get it if it's free, else buy it in its store
        private string MainLabel()
        {
            if (owned != null) return owned.IsInstalled ? "Play" : "Install";
            if (item.Free) return "Get it free on " + item.StoreName;
            if (item.Price == null) return "See it on " + item.StoreName;   // coming soon
            return "Buy on " + item.StoreName;
        }

        public override void Shown()
        {
            if (!string.IsNullOrEmpty(item.Backdrop)) shots.Add(item.Backdrop);
            StoreData.BigArt(item, url =>
            {
                if (closed) return;
                if (url != null && !shots.Contains(url)) shots.Insert(0, url);
                FillShots();
            });
            StoreData.LoadDetails(item, d =>
            {
                if (closed || d == null) return;
                about.Text = d.About ?? "";
                Ui.Fade(about, 1, 260);
                facts.Text = string.Join("   ·   ", new[] { d.Genres, d.Released, d.Score }.Where(x => !string.IsNullOrEmpty(x)));
                foreach (var u in d.Shots) if (!shots.Contains(u)) shots.Add(u);
                FillShots();
                StartTrailer(d.Trailer);
            });
            StoreData.FindElsewhere(item, e =>
            {
                if (closed || e == null) return;
                elsewhere = e;
                elsewhereText.Text = e.Price + " on " + e.Store;
                elsewhereLine.Visibility = Visibility.Visible;
                Ui.Fade(elsewhereLine, 1, 260);
            });
            ShowFocus();
        }

        public override void Hidden()
        {
            closed = true;
            slides.Stop();
            if (trailer != null) { trailer.Stop(); trailer = null; }
        }

        // the trailer behind the page, a moment after it opens (shown once it plays; the screenshots stay until then)
        private void StartTrailer(string url)
        {
            if (string.IsNullOrEmpty(url) || !Features.On("storetrailers")) return;
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            wait.Tick += (s, e) =>
            {
                wait.Stop();
                if (closed || zone != 0) return;
                // playing: the screenshots step aside (dark behind it, as in Collection) and stop changing
                trailer = WebTrailer.Create(url, () =>
                {
                    slides.Stop();
                    foreach (var img in new[] { shotA, shotB }) img.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(700)));
                });
                if (trailer != null) trailerHost.Children.Add(trailer.View);
            };
            wait.Start();
        }

        // the screenshot thumbnails along the bottom (rebuilt as more come in)
        private void FillShots()
        {
            var list = shots.Take(6).ToList();
            if (list.Count == shotTiles.Count) return;
            shotStrip.Children.Clear();
            shotTiles.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var t = new Tile(272, 153, 13, 5) { FocusScale = 1.06 };   // big enough to see what each one shows
                t.Root.Margin = new Thickness(0, 0, 20, 0);
                var fill = new Border { CornerRadius = new CornerRadius(12), Opacity = 0 };
                t.Face.Child = fill;
                StorePage.LoadFill(fill, list[i], 560);
                shotStrip.Children.Add(t.Root);
                shotTiles.Add(t);
            }
            if (shotA.Source == null && shotB.Source == null && list.Count > 0) ShowShot(0, false);
            if (list.Count > 1) slides.Start();
            ShowFocus();
        }

        private void ShowShot(int i, bool picked)
        {
            if (i < 0 || i >= shots.Count) return;
            shot = i;
            var a = front ? shotA : shotB;
            var b = front ? shotB : shotA;
            front = !front;
            Panel.SetZIndex(a, -1); Panel.SetZIndex(b, -2);   // under the words
            a.BeginAnimation(UIElement.OpacityProperty, null);
            a.Opacity = 0;
            StorePage.Load(a, shots[i], 1920);
            b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(300) });
            if (picked) { slides.Stop(); if (trailer == null) slides.Start(); }   // a picked one stays a while (and they don't take turns under the trailer)
            ShowFocus();
        }

        private void ShowFocus()
        {
            for (int i = 0; i < shotTiles.Count; i++) shotTiles[i].Focus(!onActions && zone == 0 && i == shot);
            for (int i = 0; i < moreTiles.Count; i++) moreTiles[i].Focus(zone == 1 && i == moreIndex);
            for (int i = 0; i < pills.Count; i++) PillLook(pills[i], onActions && i == pill);
        }

        private void AddPill(Panel into, string button, string label, Action run)
        {
            var p = Ui.ActionPill(button, label, false);
            p.RenderTransformOrigin = new Point(0.5, 0.5);
            into.Children.Add(p);
            pills.Add(p);
            pillActions.Add(run);
        }

        // the focused button is the filled one (the focus color), the others glass
        private static void PillLook(Border p, bool on)
        {
            p.Background = on ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.12);
            p.BorderBrush = on ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.2);
            var row = p.Child as StackPanel;
            if (row == null || row.Children.Count < 2) return;
            var dot = row.Children[0] as Grid;
            if (dot != null && dot.Children.Count >= 2)
            {
                ((System.Windows.Shapes.Ellipse)dot.Children[0]).Fill = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.9);
                Ui.LetterBrush(dot.Children[1], on ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneScrimColor"));
            }
            ((TextBlock)row.Children[1]).Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor");
            Ui.ScaleTo(p, on ? 1.06 : 1, 160);
        }

        // ---- a screenshot on the whole screen ----
        private void OpenViewer()
        {
            if (shots.Count == 0) return;
            if (shotViewer == null)
            {
                shotViewer = new Grid { Opacity = 0 };
                shotViewer.SetResourceReference(Panel.BackgroundProperty, "SeleneScrimBrush");
                bigShot = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(40, 40, 40, 90) };
                RenderOptions.SetBitmapScalingMode(bigShot, BitmapScalingMode.HighQuality);
                shotViewer.Children.Add(bigShot);
                shotViewer.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
                shotViewer.Children.Add(Ui.Prompts(true, Ui.Prompt("\u25C0 \u25B6", "Previous / next")));
                Panel.SetZIndex(shotViewer, 30);
                Root.Children.Add(shotViewer);
            }
            viewing = true;
            slides.Stop();
            shotViewer.Visibility = Visibility.Visible;
            ShowBig();
            Ui.Fade(shotViewer, 1, 220);
        }

        private void ShowBig() { if (shot < shots.Count) StorePage.Load(bigShot, shots[shot], 1920); }

        private void CloseViewer()
        {
            viewing = false;
            Ui.Fade(shotViewer, 0, 200, 0, () => { shotViewer.Visibility = Visibility.Collapsed; });
            // the viewer goes through every screenshot, the strip shows the first six: back on the strip, the focus is on one of
            // its own (past the end, Left and Right had nowhere to go and no tile was ringed)
            if (shot >= shotTiles.Count && shotTiles.Count > 0) { shot = shotTiles.Count - 1; if (trailer == null) ShowShot(shot, false); }
            ShowFocus();
            if (trailer == null && shots.Count > 1) slides.Start();
        }

        private void RunMain()
        {
            Ui.Confirm();
            if (owned != null)
            {
                var id = owned.Id;
                bool installed = owned.IsInstalled;
                LoginScene.Finish();
                if (installed) HomeNav.StartGameNow(id);
                else if (HomeNav.Api != null) HomeNav.Api.InstallGame(id);
                HomeNav.Log("store: " + (installed ? "play " : "install ") + item.Title);
                return;
            }
            HomeNav.Log("store: opening " + item.Title + " (" + item.Link + ")");
            Open(item.Link);
            LoginScene.Finish();
        }

        private void OpenWeb()
        {
            if (item.WebLink == null) return;
            Ui.Confirm();
            Open(item.WebLink);
            LoginScene.Finish();
        }

        private void SetZone(int z)
        {
            zone = z;
            Peek(z == 0, true);
            // more from the store: the page glides up to show its row
            pageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(z == 1 ? -MoreLift : 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = Ui.Out() });
            Ui.Tick();
            ShowFocus();
        }

        private static void Open(string link)
        {
            try { System.Diagnostics.Process.Start(link); }
            catch (Exception ex) { HomeNav.Log("store: open failed: " + ex.Message); }
        }

        public override void Handle(string b)
        {
            if (viewing)
            {
                if (b == "B") { Ui.Back(); CloseViewer(); return; }
                if (b == "DPadLeft" || b == "DPadRight")
                {
                    int next = shot + (b == "DPadRight" ? 1 : -1);
                    if (next < 0 || next >= shots.Count) return;
                    Ui.Tick();
                    if (trailer != null) shot = next; else ShowShot(next, true);
                    ShowBig();
                    ShowFocus();
                }
                return;
            }
            if (b == "B")
            {
                // from the "More from" row too: B leaves the page (Up is the way back to the screenshots)
                Ui.Back();
                LoginScene.Show(back != null ? back() : new StorePage(), 0);   // crossfades back
                return;
            }
            switch (b)
            {
                case "DPadDown":
                    if (onActions)
                    {
                        if (shotTiles.Count > 0) { onActions = false; Ui.Tick(); ShowFocus(); }
                        else if (moreTiles.Count > 0) { onActions = false; SetZone(1); }
                        return;
                    }
                    if (zone == 0 && moreTiles.Count > 0) SetZone(1);
                    return;
                case "DPadUp":
                    if (onActions) return;
                    if (zone == 1) { SetZone(0); if (shotTiles.Count == 0) { onActions = true; ShowFocus(); } return; }
                    onActions = true;
                    Ui.Tick();
                    ShowFocus();
                    return;
                case "DPadLeft":
                case "DPadRight":
                {
                    int dir = b == "DPadRight" ? 1 : -1;
                    if (onActions)
                    {
                        int nextPill = pill + dir;
                        if (nextPill < 0 || nextPill >= pills.Count) return;
                        pill = nextPill;
                        Ui.Tick();
                        ShowFocus();
                        return;
                    }
                    if (zone == 0)
                    {
                        int next = shot + dir;
                        if (next < 0 || next >= shotTiles.Count) return;
                        Ui.Tick();
                        // the trailer playing: only the focus moves along the strip (the dark stays behind the trailer, as in
                        // Collection); without it, the picked screenshot shows behind
                        if (trailer != null) { shot = next; ShowFocus(); return; }
                        ShowShot(next, true);
                    }
                    else
                    {
                        int next = moreIndex + dir;
                        if (next < 0 || next >= moreTiles.Count) return;
                        moreIndex = next;
                        moreShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-Math.Max(0, next - 1) * MorePitch, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ui.Out() });
                        Ui.Tick();
                        ShowFocus();
                    }
                    return;
                }
                case "Y":
                    if (elsewhere == null) return;
                    Ui.Confirm();
                    HomeNav.Log("store: " + item.Title + " elsewhere: " + elsewhere.Store);
                    Open(elsewhere.Link);
                    LoginScene.Finish();
                    return;
                case "X":
                    OpenWeb();
                    return;
                case "A":
                    if (onActions) { if (pill < pillActions.Count) pillActions[pill](); return; }
                    if (zone == 1)
                    {
                        Ui.Confirm();
                        // B on that game goes where B goes from here (the Store), not back to this game
                        LoginScene.Show(new StoreGameScreen(more[moreIndex], back), 0);
                        return;
                    }
                    // a screenshot: big, on the whole screen (buying is the button's, up top)
                    Ui.Confirm();
                    OpenViewer();
                    return;
            }
        }
    }
}
