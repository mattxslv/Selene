// console-style Left/Right for the "SeleneGameRow" list on the old theme's Welcome Hub.
// the old theme plugin handles every Left/Right on the hub itself (nearest focusable element, else next page),
// which skips tiles and jumps pages. A Window class handler runs before the old theme plugin's instance handler,
// so for focus inside SeleneGameRow we move exactly one visible tile, keep it at the left edge, and mark
// the key handled. Everything else is left to the old theme plugin.
// Loaded by the ClaudeLibraryTools extension via Add-Type (C# 5: no ?. or string interpolation).
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Selene
{
    public static partial class HomeNav
    {
        private const int AnchorSlot = 2;  // zero-based: third tile from the left
        private static bool installed;

        // "tv": Handheld games hidden in the row. "all": a streamed (remote-session) Playnite, marked by fullscreenConfig.tv.json existing
        // while that session runs -> whole library.
        public static string RowMode = "tv";

        // Streamed (remote) session: Playnite runs where WPF draws in SOFTWARE. The console extras (trailer cards,
        // hub video, cached tiles, slides) made it crawl, so they are off there. TV / PC keep everything.
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
        private static bool? lite;
        public static bool Lite
        {
            get
            {
                if (lite == null)
                {
                    try { lite = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId != WTSGetActiveConsoleSessionId(); }
                    catch { lite = false; }
                }
                return lite.Value;
            }
        }
        // Performance mode (Look) or a streamed session: no moving background, no trailer previews, smaller preloaded
        // screenshots; motion, slides and art stay. Read once at start (the background is built then)
        private static bool? light;
        public static bool Light { get { if (light == null) light = Lite || Features.On("perfmode"); return light.Value; } }
        public static string LastError;
        public static void ApplyPerformanceMode()
        {
            GameMedia.ApplyPerformanceMode();
            Log("memory mode: " + (Features.On("lightweight") ? "smaller screenshot cache and nearby preloading" : "standard cache"));
        }
        public static Playnite.SDK.IPlayniteAPI Api;
        internal static bool StartupReady { get { return !launching && !guideSwitching && !DialogUp(); } }

        public static void Install()
        {
            if (installed) return;
            installed = true;
            InputHints.Changed += Ui.PublishPrompts;
            LocalGames.StartRunningWatch();
            Smoothness.Start();
            // Every feature remains enabled; optional startup jobs wait for the intro and quiet frames.
            StartupWork.AfterHome("library refresh", () => { if (Features.On("startrefresh")) RefreshLibrary("start"); }, 2500);
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnWindowPreviewGotFocus));
            SeleneDialogs.Install(Api);   // add-ons' progress tasks behind Selene's card (no Playnite window built for each)
            DialogLook.Install();   // Playnite's message boxes as Selene cards
            ProgressToast.DevPreviewLater();
            ProgressToast.DevSessionLater();   // %TEMP%\selene-dev-session: a pretend session card   // %TEMP%\selene-dev-toast: a pretend task to see the notification card
            StartupWork.AfterHome("update check", UpdateCheck.Later);
            StartupWork.AfterHome("missing game details", GameInfo.CompleteLater);
            StartupWork.AfterHome("trim logos", TrailerFetch.TrimAllLogos);
            StartupWork.AfterHome("achievement setup", AchievementsData.SetUpLater);
            // Playnite re-filters the whole library when the preset changes (over a second on a big one): done once after
            // start-up, not on the first press of View
            StartupWork.AfterHome("collection filter", ApplyCollectionFilter);
            IdleScreen.Install();  // the idle screen's clock
            Ui.DevFrameLog();   // %TEMP%\selene-dev-frames: frames per second in the log
            Ui.StartupFrameLog();   // the first seconds: long frames in the log (the intro must not stutter)
            EventManager.RegisterClassHandler(typeof(ListBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnListLoaded));
            EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnTextLoaded));
            CoverCrop.Install();   // the home tiles' covers cropped where the detail is
            TileCovers.Install();   // Spotlight's and Collection's covers decoded off the UI thread
            var tvBackup = System.IO.Path.Combine(SelenePaths.PlayniteConfig, "fullscreenConfig.tv.json");
            RowMode = System.IO.File.Exists(tvBackup) ? "all" : "tv";
            var app = Application.Current;
            if (app != null) StartupWork.Yield(
                () => { app.Resources["SeleneRowMode"] = RowMode; app.Exit += (s, e) => { GamePause.Unhush(); GamePause.Resume(); CloseToTray.UnmuteOnExit(); RestoreTaskbar(); }; },
                () => { EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnAnyWindowLoaded)); InputManager.Current.PreProcessInput += OnPreProcessInput; },
                HookExistingRows,
                () => { AutoHideTaskbar(); KeepPlayniteOpenOnLaunch(); },
                StartGuideWatcher,
                () => StartupWork.AfterHome("Guide shield warm-up", WarmShield),
                () => { StartBlankWatch(); StartDeviceModeWatch(); Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged; },
                () => { if (Features.On("tray")) CloseToTray.Install(); });
        }

        private static Key heldKey = Key.None;   // Tab or V is down on the home or the library (a held key repeats: one hold only)

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                // a dialog without the focus (raised in the middle of a game start): Enter and Escape are its, not the home's behind it
                if (DialogLook.IsOpen && !Ui.KeyIsPadCopy() && e.GetType().Name != "GameControllerInputEventArgs" && ReferenceEquals(sender, Application.Current.MainWindow) && (e.Key == Key.Enter || e.Key == Key.Space || e.Key == Key.Escape))
                {
                    if (e.Key == Key.Escape ? DialogLook.Back() : DialogLook.Take("A")) { e.Handled = true; return; }
                }
                // Collection with no games, keyboard: Escape / Backspace back to Spotlight, the game keys do nothing
                if (!LoginScene.IsActive && SeleneShell.CollectionActive && CollectionEmpty() && !Ui.KeyIsPadCopy())
                {
                    var key = e.Key == Key.System ? e.SystemKey : e.Key;
                    if (key == Key.Escape || key == Key.Back)
                    {
                        var back = FindByName(Application.Current.MainWindow, "SeleneNav_hub") as System.Windows.Controls.Primitives.ButtonBase;
                        if (back != null && back.Command != null) { Ui.Back(); back.Command.Execute(null); }
                        e.Handled = true;
                        return;
                    }
                    if (key == Key.Enter || key == Key.Space || key == Key.Delete || key == Key.F2 || key == Key.F3) { e.Handled = true; return; }
                }
                // the login layer (menu, profiles...) is up: its keys are its own (this class handler runs before the
                // layer's, and moved the home row behind the menu)
                if (LoginScene.IsActive) return;
                // the keyboard copy of a pad press Selene already handled (it moved things a second time)
                if (Ui.KeyIsPadCopy(e.Key == Key.Left ? "DPadLeft" : e.Key == Key.Right ? "DPadRight" : e.Key == Key.Up ? "DPadUp" : e.Key == Key.Down ? "DPadDown" : null) && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Enter || e.Key == Key.Escape)) { e.Handled = true; return; }
                // the cards are still flying in: nothing moves yet
                if (IntroRunning && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Enter || e.Key == Key.Space) && FocusOnHome()) { e.Handled = true; return; }
                // the Store card's game pictures (Down from the card goes into them)
                var asPad = e.Key == Key.Down ? "DPadDown" : e.Key == Key.Up ? "DPadUp" : e.Key == Key.Left ? "DPadLeft" : e.Key == Key.Right ? "DPadRight" : e.Key == Key.Enter ? "A" : e.Key == Key.Escape || e.Key == Key.Back ? "B" : null;
                // a stray Enter as the layer closes over the details page (a capture) reopened it; pad presses come the other way
                if (asPad == "A" && (DateTime.Now - LoginScene.ClosedAt).TotalMilliseconds < 1500) { Log("keys: Enter right after the layer closed, ignored"); e.Handled = true; return; }
                if (asPad != null && !e.Handled && (StoreShowcase.Handle(asPad) || CardShowcase.Handle(asPad) || DetailsStage.Handle(asPad))) { e.Handled = true; return; }
                // Up/Down never shrink the card; Left/Right only while moving inside the row (not in the bar below)
                bool dirKey = e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right;
                if (!e.Handled && previewShowing && (!dirKey || ((e.Key == Key.Left || e.Key == Key.Right) && IsInRow(Keyboard.FocusedElement as DependencyObject)))) SmoothClosePreview();
                if (e.Handled || (e.Key != Key.Left && e.Key != Key.Right && e.Key != Key.Enter && e.Key != Key.Down && e.Key != Key.Up)) return;
                var focused = Keyboard.FocusedElement as DependencyObject;
                ListBox row = null;
                ListBoxItem item = null;
                for (var d = focused; d != null; d = GetParent(d))
                {
                    if (item == null && d is ListBoxItem) item = (ListBoxItem)d;
                    var lb = d as ListBox;
                    if (lb != null && lb.Name == "SeleneGameRow") { row = lb; break; }
                }
                // Collection's game list: nothing above or below it (Up / Down wandered off to hidden parts and the list
                // lost its focus)
                if (row == null && (e.Key == Key.Up || e.Key == Key.Down) && InLibraryList(focused)) { e.Handled = true; return; }
                if (row == null) { HandleHubKeys(e, focused); return; }
                // Spotlight's row: nothing above it either (the top bar has nothing to pick; focus went to hidden parts)
                if (e.Key == Key.Up) { e.Handled = true; return; }
                if (e.Key == Key.Down)
                {
                    if (row.SelectedItem is string) { e.Handled = true; return; }   // a card (Collection, Store): nothing below it
                    if (FocusNews(row)) e.Handled = true;
                    return;
                }
                var navItem = item != null ? item.DataContext as string : null;
                if (e.Key == Key.Enter)
                {
                    Log("enter on " + (navItem ?? (item != null ? item.DataContext : row.SelectedItem)) + " api=" + (Api != null));
                    // A on a section card opens that section instead of launching the selected game
                    if (navItem == null || !navItem.StartsWith("nav:"))
                    {
                        var game = item != null ? item.DataContext : row.SelectedItem;
                        var idProp = game != null ? game.GetType().GetProperty("Id") : null;
                        if (Api != null && idProp != null)
                        {
                            e.Handled = true;
                            StartGameNow((Guid)idProp.GetValue(game, null));
                        }
                        return;
                    }
                    e.Handled = true;
                    if (movingCard != null) { movingCard = null; SetMoveVisual(item, false); return; }
                    RunCard(row, navItem.Substring(4));
                    return;
                }
                e.Handled = true;
                int dir = e.Key == Key.Right ? 1 : -1;
                if (movingCard != null && navItem == movingCard) { MoveCard(row, item, dir); return; }
                if (movingGame != null && item != null && ReferenceEquals(item.DataContext, movingGame)) { MoveGame(row, item, dir); return; }

                int cur = item != null ? row.ItemContainerGenerator.IndexFromContainer(item) : -1;
                if (cur < 0) cur = Math.Max(0, row.SelectedIndex);
                int next = -1;
                for (int i = cur + dir; i >= 0 && i < row.Items.Count; i += dir)
                {
                    if (IsShown(row.Items[i])) { next = i; break; }
                }
                if (next < 0) return;
                MoveTo(row, cur, next);
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                Log(LastError);
            }
        }

        // ---- the rest of the hub (news cards, other sections) ----
        // Left/Right move to the nearest item on the same row and NEVER switch hub sections (only LB/RB do);
        // Up from the news cards returns to the selected game. Top bar keys stay with the old theme plugin.
        private static void HandleHubKeys(KeyEventArgs e, DependencyObject focused)
        {
            if (focused == null || e.Key == Key.Enter) return;
            var w = Window.GetWindow(focused);
            var hubRoot = w != null ? FindByName(w, "HubRoot") : null;
            if (hubRoot == null || !hubRoot.IsVisible || !IsDescendant(focused, hubRoot)) return;
            var topBar = FindByName(w, "HubTopBarBackground");
            if (topBar != null && IsDescendant(focused, topBar)) return;

            var cards = FindByName(hubRoot, "HubBottomCards");
            if (e.Key == Key.Up)
            {
                if (cards == null || !IsDescendant(focused, cards)) return;
                var row = FindByName(hubRoot, "SeleneGameRow") as ListBox;
                var c = row != null ? row.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, row.SelectedIndex)) as UIElement : null;
                if (c != null) { c.Focus(); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Down) return;

            int dir = e.Key == Key.Right ? 1 : -1;
            var me = focused as UIElement;
            if (me == null) return;
            var page = (cards != null && IsDescendant(focused, cards)) ? cards : hubRoot;
            var origin = me.TranslatePoint(new Point(me.RenderSize.Width / 2, me.RenderSize.Height / 2), hubRoot);
            UIElement best = null;
            double bestScore = double.MaxValue;
            foreach (var el in Focusables(page))
            {
                if (ReferenceEquals(el, me) || IsDescendant(el, me) || IsDescendant(me, el)) continue;
                if (topBar != null && IsDescendant(el, topBar)) continue;
                var c = el.TranslatePoint(new Point(el.RenderSize.Width / 2, el.RenderSize.Height / 2), hubRoot);
                double dx = c.X - origin.X, dy = Math.Abs(c.Y - origin.Y);
                if (dir * dx <= 20 || dy > Math.Max(80, me.RenderSize.Height * 0.75)) continue;
                double score = Math.Abs(dx) + dy * 0.25;
                if (score < bestScore) { bestScore = score; best = el; }
            }
            if (best != null) best.Focus();
            e.Handled = true;  // at the edge: stay put (no section switch)
        }

        // the games right after (and just before) the selected one, for the screenshot strip to decode ahead
        private static System.Collections.IList RowNeighbours(ListBox row)
        {
            var list = new System.Collections.ArrayList();
            int i = row.SelectedIndex, n = row.Items.Count;
            foreach (var d in new[] { 1, 2, 3, -1 })
                if (i + d >= 0 && i + d < n) list.Add(row.Items[i + d]);
            return list;
        }

        private static bool FocusTopBar(ListBox row, FrameworkElement fromItem)
        {
            var w = Window.GetWindow(row);
            var bar = w != null ? FindByName(w, "HubTopBarBackground") : null;
            if (bar == null) return false;
            var from = fromItem ?? row;
            double x = from.TranslatePoint(new Point(from.ActualWidth / 2, 0), w).X;
            UIElement best = null; double bestD = double.MaxValue;
            foreach (var el in Focusables(bar))
            {
                double ex = el.TranslatePoint(new Point(el.RenderSize.Width / 2, 0), w).X;
                double d = Math.Abs(ex - x);
                if (d < bestD) { bestD = d; best = el; }
            }
            if (best == null) return false;
            best.Focus();
            return true;
        }

        private static System.Collections.Generic.List<UIElement> Focusables(DependencyObject root)
        {
            var list = new System.Collections.Generic.List<UIElement>();
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                var u = c as UIElement;
                if (u != null && (!u.IsVisible || u.Opacity <= 0.01)) continue;
                if (u != null && u.Focusable && u.IsEnabled && u.RenderSize.Width > 0) list.Add(u);
                list.AddRange(Focusables(c));
            }
            return list;
        }

        private static bool IsDescendant(DependencyObject d, DependencyObject ancestor)
        {
            for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : null)
                if (ReferenceEquals(d, ancestor)) return true;
            return false;
        }

        // Down from the row: straight to the first news card (the old theme plugin's first-time visual-tree scan caused a hitch)
        private static bool FocusNews(ListBox row)
        {
            var scope = VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(row)));
            DependencyObject hub = row;
            for (int i = 0; i < 12 && hub != null; i++) { hub = VisualTreeHelper.GetParent(hub); var fe = hub as FrameworkElement; if (fe != null && fe.Name == "HubRoot") break; }
            var cards = hub != null ? FindByName(hub, "HubBottomCards") : null;
            if (cards == null) return false;
            UIElement first = null; double bestX = double.MaxValue;
            foreach (var el in Focusables(cards))
            {
                double x = el.TranslatePoint(new Point(0, 0), cards).X;
                if (x < bestX) { bestX = x; first = el; }
            }
            if (first == null) return false;
            first.Focus();
            return true;
        }

        private static DateTime lastMoveAt = DateTime.MinValue;

        private static void MoveTo(ListBox row, int cur, int next)
        {
            MoveToCore(row, cur, next);
        }

        private static void MoveToCore(ListBox row, int cur, int next)
        {
            var sv = FindChild<ScrollViewer>(row);
            var panel = FindChild<VirtualizingStackPanel>(row);
            double before = double.NaN;
            var target = row.ItemContainerGenerator.ContainerFromIndex(next) as FrameworkElement;
            if (target != null && panel != null) before = target.TranslatePoint(new Point(0, 0), row).X;

            // No SelectedIndex here: the tile style selects whatever has keyboard focus, and setting both
            // re-enters Selector and throws. Focus the tile after scrolling instead.
            // Item-based scrolling: the selection walks to the third slot, then stays there and the row moves.
            // ScrollViewer clamps the offset at the end, so the selection walks out to the last tile.
            double offsetBefore = sv != null ? sv.HorizontalOffset : 0;
            if (sv != null) sv.ScrollToHorizontalOffset(AnchorOffset(next));
            row.UpdateLayout();
            // the row only glides when it really scrolled: moving between tiles that stay put (the cards at the start)
            // changes the selected tile's size, and gliding that made the whole row bounce
            bool scrolled = sv != null && Math.Abs(sv.HorizontalOffset - offsetBefore) > 0.01;

            target = row.ItemContainerGenerator.ContainerFromIndex(next) as FrameworkElement;
            if (target == null)
            {
                row.ScrollIntoView(row.Items[next]);
                row.UpdateLayout();
                target = row.ItemContainerGenerator.ContainerFromIndex(next) as FrameworkElement;
            }
            if (target == null) return;

            ScheduleTitle(row, 230);
            // Leaving a section card: the game info still shows the old game until the delayed SelectedGame
            // binding (250 ms) catches up, so keep it hidden for that moment.
            if (row.Items[cur] is string && !(row.Items[next] is string))
            {
                var info = FindByName(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(row)), "SeleneInfoIntro");
                if (info != null) info.BeginAnimation(UIElement.OpacityProperty, Keys(0, 1, 0.3, 0.25, null));
            }

            // Slide the row from where the tile was to where it is now.
            if (panel != null && !double.IsNaN(before) && scrolled)
            {
                double delta = before - target.TranslatePoint(new Point(0, 0), row).X;
                if (Math.Abs(delta) > 1 && !Lite)  // streamed session: instant moves (no slide)
                {
                    var shift = panel.RenderTransform as TranslateTransform;
                    if (shift == null) { shift = new TranslateTransform(); panel.RenderTransform = shift; }
                    // start from where the row visibly IS: a quick second press used to restart from 'delta' and
                    // drop the unfinished part of the previous slide (a little jump on every fast move)
                    // held stick: moves arrive every ~100-150 ms; braking at the end of each slide gave a pulsing, low-fps
                    // look, so chained moves glide at constant speed for exactly the repeat interval. A single tap eases out.
                    double sinceLast = (DateTime.Now - lastMoveAt).TotalMilliseconds;
                    lastMoveAt = DateTime.Now;
                    Glide(shift, delta, sinceLast < 260, RowRest(row));
                }
            }
            target.Focus();
            if (rideFirst < 0) EnsureTileCaches(row);   // (a walk caches its tiles once it lands: a cache per passed tile stuttered)
        }

        // what a newly selected game brings: Playnite's selection, the trailer card's wait, its art ahead, the heart, the screenshots
        private static void AfterSelection(ListBox row)
        {
            QueuePush(row);
            ArmPreview(row);
            PreloadCoverArt(row.SelectedItem);
            ShowHeart(row.SelectedItem);
            GameMedia.OnSelected(row.SelectedItem, RowNeighbours(row));
        }

        // ---- the row to the edges ----
        // The row's scroll window reaches the screen's edges (SeleneGameRow's margin). At the row's start a padding keeps the first
        // card in from the left, as before; once scrolled the padding goes (the glide carries it off with the first move) and the
        // tiles run to the edge on both sides, dissolving over the last 200 px into the screen's darkened sides. Switch: Spotlight >
        // Row to the edges (off: the padding stays, no dissolve, no shade).
        private const double StartPad = 80, RowEdgePx = 200;

        private static double RowRest(ListBox row)
        {
            if (!Features.On("rowedges")) return StartPad;
            var sv = FindChild<ScrollViewer>(row);
            return sv == null || sv.HorizontalOffset < 0.5 ? StartPad : 0;
        }

        private static void RestRow(ListBox row, ScrollViewer sv)
        {
            var panel = FindChild<VirtualizingStackPanel>(row);
            if (panel == null) return;
            var shift = panel.RenderTransform as TranslateTransform;
            if (shift == null || shift.IsFrozen) { shift = new TranslateTransform(); panel.RenderTransform = shift; }
            double rest = RowRest(row);
            if (glideHooked && ReferenceEquals(glideShift, shift)) { glideRest = rest; return; }   // the spring carries it there
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = rest;
        }

        private static void RowEdges(ListBox row, ScrollViewer sv)
        {
            try
            {
                if (!Features.On("rowedges")) { if (row.OpacityMask != null) row.OpacityMask = null; return; }
                var brush = row.OpacityMask as LinearGradientBrush;
                if (brush == null || brush.GradientStops.Count != 4)
                {
                    brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                    brush.GradientStops.Add(new GradientStop(Colors.Black, 0));
                    brush.GradientStops.Add(new GradientStop(Colors.Black, 0.1));
                    brush.GradientStops.Add(new GradientStop(Colors.Black, 0.9));
                    brush.GradientStops.Add(new GradientStop(Colors.Black, 1));
                    row.OpacityMask = brush;
                }
                // the row runs past the screen on both sides (its margins), so each dissolve ends off the screen and the tiles reach
                // the edge before they're gone, the same either side (ending the right one at the screen's edge left the last
                // 200 px fading out in view, as if the row stopped short)
                double w = row.ActualWidth;
                if (w > RowEdgePx * 3)
                {
                    brush.GradientStops[1].Offset = RowEdgePx / w;
                    brush.GradientStops[2].Offset = Math.Max(0.5, (w - RowEdgePx) / w);
                    brush.GradientStops[3].Offset = 1;
                }
                bool left = sv.HorizontalOffset > 0.5, right = sv.HorizontalOffset < sv.ScrollableWidth - 0.5;
                RowEdge(brush.GradientStops[0], left);
                RowEdge(brush.GradientStops[3], right);
            }
            catch (Exception ex) { Log("row edges: " + ex.Message); }
        }

        private static void RowEdge(GradientStop stop, bool dissolve)
        {
            var to = dissolve ? Color.FromArgb(0, 0, 0, 0) : Colors.Black;
            if (stop.Color == to && !stop.HasAnimatedProperties) return;
            stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // ---- row slide: one continuous spring instead of a new animation per press (like Playnite's library) ----
        // Restarting an animation on every press reset the row's speed each time, so a held stick looked like a chain
        // of little slides (low-fps feel without dropped frames). Now a press only moves the target: the row keeps its
        // speed and follows on a critically damped spring (no overshoot), stepped on every rendered frame.
        // A single tap starts at full speed and eases out (console feel); presses while moving add no kick (smooth hold).
        private const double GlideOmega = 18;   // 1/s: a tap is ~95% there after 0.17 s
        private static TranslateTransform glideShift;
        private static double glideV;
        private static TimeSpan glideLast;
        private static bool glideHooked;
        private static readonly TranslateTransform glideClock = new TranslateTransform();

        internal static void Glide(TranslateTransform shift, double delta, bool chained) { Glide(shift, delta, chained, 0); }

        // rest: where the content settles (Spotlight's row: its start padding at the row's start, 0 once scrolled; Collection: 0)
        private static double glideRest;
        internal static void Glide(TranslateTransform shift, double delta, bool chained, double rest)
        {
            if (glideShift != shift)
            {
                // The shared spring must settle the old row before following another one.
                if (glideShift != null && !glideShift.IsFrozen) { glideShift.BeginAnimation(TranslateTransform.XProperty, null); glideShift.X = glideRest; }
                glideV = 0; glideShift = shift;
            }
            glideRest = rest;
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X += delta;                       // the tiles jumped by -delta in layout: start where they were seen
            if (!chained) glideV = -GlideOmega * (shift.X - rest);   // pure exponential ease-out from here
            if (glideHooked) return;
            glideHooked = true;
            glideLast = TimeSpan.Zero;
            CompositionTarget.Rendering += OnGlideFrame;
            // WPF renders at 60 fps unless an animation asks for more: keep a dummy one running at the screen refresh rate
            var clock = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(clock, Ui.FrameRate);
            glideClock.BeginAnimation(TranslateTransform.XProperty, clock);
        }

        private static void OnGlideFrame(object sender, EventArgs e)
        {
            var shift = glideShift;
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == glideLast) return;           // Rendering can fire more than once per frame
            double dt = glideLast == TimeSpan.Zero ? 1.0 / Ui.FrameRate : Math.Min(0.05, (now - glideLast).TotalSeconds);
            glideLast = now;
            if (shift == null) { StopGlide(); return; }
            // exact critically damped step: x(t) = (x0 + (v0 + w x0) t) e^-wt, around the rest
            double rest = glideRest;
            double x0 = shift.X - rest, v0 = glideV, w = GlideOmega, k = Math.Exp(-w * dt), b = v0 + w * x0;
            double x = (x0 + b * dt) * k;
            glideV = (v0 - w * b * dt) * k;
            if (Math.Abs(x) < 0.3 && Math.Abs(glideV) < 6) { shift.X = rest; StopGlide(); return; }
            shift.X = x + rest;
        }

        internal static void CancelGlide(TranslateTransform shift)
        {
            if (shift == null || shift != glideShift) return;
            shift.X = glideRest;
            StopGlide();
        }

        private static void StopGlide()
        {
            glideV = 0;
            if (!glideHooked) return;
            glideHooked = false;
            CompositionTarget.Rendering -= OnGlideFrame;
            glideClock.BeginAnimation(TranslateTransform.XProperty, null);
        }

        // Each tile (card + ring + sheen) is one cached picture: sliding the row moves pictures instead of redrawing
        // every tile, shadow and effect each frame. This is what made the boot intro smooth.
        private static void EnsureTileCaches(ListBox row)
        {
            if (Lite)   // software rendering: no caches, but the end padding must still be invisible
            {
                for (int i = 0; i < row.Items.Count; i++)
                {
                    var pc = row.Items[i] is RowPad ? row.ItemContainerGenerator.ContainerFromIndex(i) as UIElement : null;
                    if (pc != null) { pc.Opacity = 0; pc.IsHitTestVisible = false; pc.Focusable = false; }
                    var gc = !(row.Items[i] is RowPad) ? row.ItemContainerGenerator.ContainerFromIndex(i) as UIElement : null;
                    if (gc != null && !gc.Focusable) { gc.Opacity = 1; gc.IsHitTestVisible = true; gc.Focusable = true; }
                }
                return;
            }
            for (int i = 0; i < row.Items.Count; i++)
            {
                var c = row.ItemContainerGenerator.ContainerFromIndex(i) as UIElement;
                if (c != null && row.Items[i] is RowPad) { c.BeginAnimation(UIElement.OpacityProperty, null); c.Opacity = 0; c.IsHitTestVisible = false; c.Focusable = false; continue; }
                if (c != null && !c.Focusable) { c.Opacity = 1; c.IsHitTestVisible = true; c.Focusable = true; }  // recycled from a spacer
                if (c != null && c.CacheMode == null) c.CacheMode = new BitmapCache(1.0) { SnapsToDevicePixels = false };
            }
        }

        // Mirrors the tile style triggers: hidden games and "Handheld" games are collapsed.
        private static bool IsShown(object game) { return IsShownBase(game) && PassesRowFilter(game); }

        // Placeholder after the last game (see RebuildGames): never shown, focused or selected.
        public sealed class RowPad { public override string ToString() { return "pad"; } }
        private static readonly RowPad[] RowPads = { new RowPad(), new RowPad(), new RowPad(), new RowPad(), new RowPad(), new RowPad(), new RowPad(), new RowPad(), new RowPad() };

        private static bool IsShownBase(object game)
        {
            if (game == null || game is RowPad) return false;
            var t = game.GetType();
            var hidden = t.GetProperty("Hidden");
            if (hidden != null && Equals(hidden.GetValue(game, null), true)) return false;
            // in TV mode, the games in the "Handheld" category stay off (any of its categories: only the first was looked at);
            // only with the streaming device switch set up (a "Handheld" category of anyone else's stays as it is)
            if (RowMode == "all" || SelenePaths.StreamLogs.Length == 0) return true;
            var cats = t.GetProperty("Categories");
            var list = cats != null ? cats.GetValue(game, null) as IEnumerable : null;
            if (list != null)
                foreach (var c in list)
                {
                    var name = c != null ? c.GetType().GetProperty("Name") : null;
                    if (name != null && Equals(name.GetValue(c, null), "Handheld")) return false;
                }
            return true;
        }

        // a game Spotlight shows (not hidden, not a Handheld game in TV mode): the cards list only these, so a pick lands
        public static bool OnSpotlight(object game) { return IsShownBase(game); }

        // console boot-in: whenever the row becomes visible, each on-screen tile flies out from the centre of the
        // row to its slot, one after another, with a small overshoot.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, object> hookedRows =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, object>();

        private static void OnListLoaded(object sender, RoutedEventArgs e)
        {
            HookRow(sender as ListBox);
        }

        // The hub (and the row) can be built before this extension starts, so also look for an existing row.
        // WPF only broadcasts Loaded to elements with instance handlers, so the class handler above can miss the
        // row. Watch the login screen and the hub overlay instead (both exist from startup) and find the row then.
        private static void HookExistingRows()
        {
            foreach (Window w in Application.Current.Windows)
            {
                var row = FindByName(w, "SeleneGameRow") as ListBox;
                if (row != null) HookRow(row);
                var login = FindByName(w, "AcceuilSettings");
                var hub = FindByName(w, "WelcomeHubOverlay");
                Log("watch: login=" + (login != null) + " hub=" + (hub != null) + " row=" + (row != null));
                var win = w;
                WatchDetails(w);
                if (login != null) login.IsVisibleChanged += (s, a) => { if (!(bool)a.NewValue && !LoginScene.KeepHome) FindAndPlay(win); };
                HookHub(win);
            }
        }

        // The hub overlay may not exist yet when HookExistingRows runs (Playnite builds it later on some starts),
        // so this is also called once the row is found. Hooks each hub overlay once.
        private static WeakReference hookedHub;
        private static void HookHub(Window win)
        {
            var hub = FindByName(win, "WelcomeHubOverlay");
            if (hub == null || (hookedHub != null && hookedHub.Target == hub)) return;
            hookedHub = new WeakReference(hub);
            ReplaceFadeImages(win);
            CacheNewsCards(win);
            RenderFilterTabs(win);
            HomeHints.Attach(win);
            hub.IsVisibleChanged += (s, a) => { if ((bool)a.NewValue) { FindAndPlay(win); RenderFilterTabs(win); } else { RefreshLibraryFades(); HidePreview(); ForceHorizontalLibrary(); EnsureBigScreenFilter(); if (!openingDetails) FocusLibraryLater(win); } };
            Log("hub hooked");
        }

        // ---- no stutter from background changes ----
        // Playnite's FadeImage calls GC.Collect() whenever its first image finishes fading out (FadeImage.
        // Image1FadeOut_Completed): with every selection changing several full-screen backgrounds, that forced a full
        // garbage collection (2-3 frozen frames, 60-150 ms) ~1 s after every second move - right in the next slide.
        // The selection-driven FadeImages (bound to SelectedGame / the hero path) are unplugged and replaced in place
        // by SeleneCrossfade: same crossfade, image decoded off the UI thread at screen size, no forced collection.
        private static void ReplaceFadeImages(Window win)
        {
            int n = 0;
            foreach (var fi in FindAll(win, d => d.GetType().Name == "FadeImage"))
            {
                try
                {
                    var dpd = DependencyPropertyDescriptor.FromName("Source", fi.GetType(), fi.GetType());
                    if (dpd == null) continue;
                    var be = BindingOperations.GetBindingBase(fi, dpd.DependencyProperty) as Binding;
                    if (be == null) continue;
                    string path = (be.Path != null ? be.Path.Path : "") + " " + (be.ElementName ?? "");
                    Log("fade image: " + path + " | dc=" + (fi.DataContext != null ? fi.DataContext.GetType().Name : "null") + " | in " + Ancestry(fi));
                    if (path.IndexOf("SelectedGame", StringComparison.Ordinal) < 0 && path.IndexOf("SeleneHeroPath", StringComparison.Ordinal) < 0)
                    {
                        // left as it is, but without the fade storyboard (its completion is what calls GC.Collect)
                        var animOther = fi.GetType().GetProperty("AnimationEnabled");
                        if (animOther != null && animOther.CanWrite) animOther.SetValue(fi, false, null);
                        continue;
                    }
                    if (Ancestry(fi).IndexOf("WelcomeHubOverlay", StringComparison.Ordinal) < 0)
                    {
                        // library side: keep Playnite's own image (its sources are Playnite types), just no fade storyboard
                        var animLib = fi.GetType().GetProperty("AnimationEnabled");
                        if (animLib != null && animLib.CanWrite) animLib.SetValue(fi, false, null);
                        continue;
                    }
                    var parent = VisualTreeHelper.GetParent(fi) as Panel;
                    if (parent == null)
                    {
                        // e.g. a Border: give it a Grid holding the original (unplugged) and the replacement
                        var deco = VisualTreeHelper.GetParent(fi) as Decorator;
                        if (deco == null || deco.Child != fi) { Log("fade image skipped, parent " + (VisualTreeHelper.GetParent(fi) ?? (object)"null").GetType().Name); continue; }
                        var wrap = new Grid();
                        deco.Child = null;
                        wrap.Children.Add(fi);
                        deco.Child = wrap;
                        parent = wrap;
                    }
                    var cf = new SeleneCrossfade();
                    string where = Ancestry(fi);
                    cf.Hub = where.IndexOf("WelcomeHubOverlay", StringComparison.Ordinal) >= 0;
                    if (where.IndexOf("WelcomeHubOverlay", StringComparison.Ordinal) < 0)
                    {
                        libraryFades.Add(new WeakReference(cf));
                    }
                    else if (path.IndexOf("SeleneHeroPath", StringComparison.Ordinal) < 0)
                    {
                        // home background: the PS Store hero above covers it whenever there is one
                        var heroPath = FindByName(parent, "SeleneHeroPath") as TextBlock;
                        if (heroPath != null) cf.CanLoad = () => !System.IO.File.Exists(heroPath.Text);
                    }
                    var stretch = fi.GetType().GetProperty("Stretch");
                    if (stretch != null) cf.Stretch = (Stretch)stretch.GetValue(fi, null);
                    cf.SetResourceReference(SeleneCrossfade.StretchProperty, "SeleneBackgroundStretch");
                    RenderOptions.SetBitmapScalingMode(cf, RenderOptions.GetBitmapScalingMode(fi));
                    cf.Margin = fi.Margin; cf.HorizontalAlignment = fi.HorizontalAlignment; cf.VerticalAlignment = fi.VerticalAlignment;
                    cf.Opacity = fi.Opacity; cf.OpacityMask = fi.OpacityMask; cf.Effect = fi.Effect; cf.IsHitTestVisible = false;
                    Grid.SetRow(cf, Grid.GetRow(fi)); Grid.SetColumn(cf, Grid.GetColumn(fi));
                    Grid.SetRowSpan(cf, Grid.GetRowSpan(fi)); Grid.SetColumnSpan(cf, Grid.GetColumnSpan(fi));
                    Panel.SetZIndex(cf, Panel.GetZIndex(fi));
                    parent.Children.Insert(parent.Children.IndexOf(fi), cf);
                    BindingOperations.SetBinding(cf, SeleneCrossfade.SourceProperty, be);
                    // keep the original (theme storyboards may name it) but never let it change again
                    BindingOperations.ClearBinding(fi, dpd.DependencyProperty);
                    // a style binding survives ClearBinding: pin a local null over it, and no fade storyboard either
                    fi.SetValue(dpd.DependencyProperty, null);
                    var anim = fi.GetType().GetProperty("AnimationEnabled");
                    if (anim != null && anim.CanWrite) anim.SetValue(fi, false, null);
                    fi.Visibility = Visibility.Collapsed;
                    n++;
                }
                catch (Exception ex) { Log("fade replace failed: " + ex.Message); }
            }
            Log("fade images replaced: " + n);
        }

        private static string Ancestry(DependencyObject d)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; d != null && i < 40; i++)
            {
                d = VisualTreeHelper.GetParent(d);
                var fe = d as FrameworkElement;
                if (fe != null && fe.Name.Length > 0) { sb.Append(fe.Name).Append(" < "); if (sb.Length > 160) break; }
            }
            return sb.ToString();
        }

        // The news cards sit on the playing background video: without a cache their pictures and shadows were
        // redrawn on every video frame (row 94 fps; cards hidden 118). Cached, they are blended as one picture.
        // LB/RB rebuilds the page with new cards, so this runs on every row load too.
        private static void CacheNewsCards(DependencyObject root)
        {
            if (Lite || root == null) return;
            foreach (var card in FindAll(root, d => { var fe = d as FrameworkElement; return fe != null && fe.Name == "SeleneShotStrip"; }))
                if (card.CacheMode == null) card.CacheMode = new BitmapCache(1.0) { SnapsToDevicePixels = true };
        }

        private static readonly List<WeakReference> libraryFades = new List<WeakReference>();

        private static void RefreshLibraryFades()
        {
            foreach (var w in libraryFades) { var cf = w.Target as SeleneCrossfade; if (cf != null) cf.Refresh(); }
        }

        private static List<FrameworkElement> FindAll(DependencyObject root, Func<DependencyObject, bool> match)
        {
            // visual AND logical children: some theme parts (e.g. a FadeImage in a button that is not templated yet) are
            // only in the logical tree but still animate
            var found = new List<FrameworkElement>();
            var visited = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (!visited.Add(d)) continue;
                var fe = d as FrameworkElement;
                if (fe != null && match(d)) { found.Add(fe); continue; }
                if (d is Visual) for (int i = VisualTreeHelper.GetChildrenCount(d) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
                foreach (var c in LogicalTreeHelper.GetChildren(d)) { var cd = c as DependencyObject; if (cd != null) stack.Push(cd); }
            }
            return found;
        }

        // The library always opens in the horizontal layout (Hold-Y "Change layout" may have switched it).
        private static void ForceHorizontalLibrary()
        {
            try
            {
                Type appType = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Playnite") { appType = a.GetType("Playnite.PlayniteApplication"); break; }
                var app = appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                var settings = app.GetType().GetProperty("AppSettings").GetValue(app, null);
                var fs = settings.GetType().GetProperty("Fullscreen").GetValue(settings, null);
                var hl = fs.GetType().GetProperty("HorizontalLayout");
                if (hl != null && !Equals(hl.GetValue(fs, null), true)) { hl.SetValue(fs, true, null); Log("library layout -> horizontal"); }
                // covers only, no names under them (Selene's library shows the selected game's logo instead)
                var titles = fs.GetType().GetProperty("ShowGameTitles");
                if (titles != null && Equals(titles.GetValue(fs, null), true)) { titles.SetValue(fs, false, null); Log("library titles -> off"); }
                // Selene's library is one row under the trailer stage
                var rows = fs.GetType().GetProperty("Rows");
                if (rows != null && rows.PropertyType == typeof(int) && !Equals(rows.GetValue(fs, null), 1)) { rows.SetValue(fs, 1, null); Log("library rows -> 1"); }
                // Columns: Playnite gives each cover the row's width / Columns and draws only Columns + 1 of them. Selene's one row
                // of big covers wants as many as fit at its height (about 300 px each on a 1920 px row): fewer (the default 4)
                // drew five covers and left the rest of the row empty, more (16) made them small, in two rows
                var cols = fs.GetType().GetProperty("Columns");
                var main = Application.Current.MainWindow;
                var list = main != null ? FindByName(main, "PART_ListGameItems") as FrameworkElement : null;
                double width = list != null && list.ActualWidth > 100 ? list.ActualWidth : (main != null && main.ActualWidth > 100 ? main.ActualWidth : 1920);
                int want = Math.Max(4, (int)Math.Ceiling(width / 298.0));
                if (cols != null && cols.PropertyType == typeof(int) && (int)cols.GetValue(fs, null) != want) { cols.SetValue(fs, want, null); Log("library columns -> " + want + " (row " + (int)width + " px)"); }
            }
            catch (Exception ex) { Log("horizontal: " + ex.Message); }
        }

        // ---- which screen is watching: a streaming client (tablet/phone) sees every game, the TV hides the Handheld category ----
        // Optional (settings: streaming host log folder + client name): the newest streaming-host log (Sunshine-style)
        // says whether a client is connected and its name. A Playnite started for a stream (RowMode "all") is not watched.
        private static bool startedAsChild;

        // Playnite in front yet blank: its root is kept at opacity 0 behind a running game and fades back in on Activated,
        // but a game that closes without Windows re-activating Playnite left a black screen with the sounds still playing
        // (#24). Two checks a second apart, then it is shown.
        // Back from a game, Playnite could stay black with its sounds playing until Alt+Tab (#24). A game that switches the
        // display mode (exclusive fullscreen, another refresh rate) can leave WPF's drawing surface lost: nothing new is drawn
        // until something forces a frame. Selene's own covers (loading veil, Guide's black shield) and a faded-out home are put
        // right here too. Run once back from a game, and whenever the display mode changes.
        private static DateTime gameEndedAt = DateTime.MinValue;
        private static bool refreshQueued;
        private static string pendingRefresh;
        private static DateTime pendingRefreshAt;
        // Playnite comes back on its own schedule (restored by Windows, no event to WPF): looked at twice a second
        private static System.Windows.Threading.DispatcherTimer pendingWatch;
        private static void WatchPendingRefresh()
        {
            if (pendingWatch == null)
            {
                pendingWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                pendingWatch.Tick += (s, e) =>
                {
                    var w = Application.Current.MainWindow;
                    if (pendingRefresh == null || (DateTime.Now - pendingRefreshAt).TotalMinutes >= 3) { pendingWatch.Stop(); pendingRefresh = null; return; }
                    if (launching || Quitting || w == null || !w.IsVisible || IsIconic(new System.Windows.Interop.WindowInteropHelper(w).Handle) || !PlayniteInFront()) return;
                    pendingWatch.Stop();
                    RunPendingRefresh();
                };
            }
            pendingWatch.Start();
        }

        private static void RunPendingRefresh()
        {
            if (pendingRefresh == null) return;
            var why = pendingRefresh;
            pendingRefresh = null;
            if ((DateTime.Now - pendingRefreshAt).TotalMinutes < 3) QueueScreenRefresh(why + ", now on screen", 300);
        }
        public static void GameEnded()
        {
            gameEndedAt = DateTime.Now;
            guideAtHome = false;
            lastGameSnap = null;
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (launchAttempt != null && launchAttempt.Cancelled && !launchAttempt.Completed) return;
                if (launching) { AbortLaunchCover(IntPtr.Zero, "the game stopped while loading"); return; }
                var w = app.MainWindow;
                if (w != null && w.IsActive) { gameEndedAt = DateTime.MinValue; QueueScreenRefresh("back from a game", 400); }
            }));
        }

        public static void ScreenRefresh(string why)
        {
            try
            {
                var w = Application.Current.MainWindow;
                // (minimized as Windows sees it: Playnite restores its window through Windows and WPF's WindowState can stay "Minimized")
                bool iconic = w != null && IsIconic(new System.Windows.Interop.WindowInteropHelper(w).Handle);
                if (launching || Quitting || w == null || !w.IsVisible || iconic)
                {
                    Log("screen refresh (" + why + "): waits (" + (launching ? "loading screen up" : Quitting ? "quitting" : w == null ? "no window" : !w.IsVisible ? "Playnite hidden" : "minimized") + ")");
                    if (!Quitting) { pendingRefresh = why; pendingRefreshAt = DateTime.Now; WatchPendingRefresh(); }   // done once Playnite is on screen and in front again
                    return;
                }
                pendingRefresh = null;
                var fixes = new System.Collections.Generic.List<string>();
                if (coverWindow != null) { CloseCoverWindow(false, null); fixes.Add("loading cover closed"); }
                int leftovers = CloseLeftoverCovers();
                if (leftovers > 0) fixes.Add(leftovers + " leftover cover window(s) closed");
                if (shield != null && shield.IsVisible) { HideShield(); fixes.Add("black shield hidden"); }
                var root = w.Content as UIElement;
                if (root != null && root.Opacity < 0.99 && !guideSwitching) { FadeInRoot(root); fixes.Add("home faded in (opacity " + root.Opacity.ToString("0.00") + ")"); }
                // a new drawing surface: a frame drawn in software, then the graphics card again
                var src = PresentationSource.FromVisual(w) as System.Windows.Interop.HwndSource;
                if (src != null && src.CompositionTarget != null && src.CompositionTarget.RenderMode == System.Windows.Interop.RenderMode.Default)
                {
                    src.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                    w.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (src.CompositionTarget != null) src.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.Default; } catch { }
                        if (root != null) root.InvalidateVisual();
                    }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                    fixes.Add("drawing surface renewed");
                }
                Log("screen refresh (" + why + "): " + string.Join(", ", fixes));
            }
            catch (Exception ex) { Log("screen refresh: " + ex.Message); }
        }

        // the loading screen's own windows (its cover, the veil the game fades in under) still up with no game loading
        private static int CloseLeftoverCovers()
        {
            if (launching) return 0;
            int n = 0;
            try
            {
                foreach (Window x in Application.Current.Windows)
                    if ((x.Title == "SeleneLaunchCover" || x.Title == "SeleneLaunchVeil") && !ReferenceEquals(x, coverWindow)) { try { var xh = new System.Windows.Interop.WindowInteropHelper(x).Handle; x.Close(); n++; Log("leftover cover: closed #" + x.GetHashCode() + " hwnd " + xh + " (window still there: " + (xh != IntPtr.Zero && IsWindow(xh)) + ")"); } catch (Exception ex) { Log("leftover cover: close failed: " + ex.Message); } }
            }
            catch { }
            return n;
        }

        private static void QueueScreenRefresh(string why, int ms)
        {
            if (refreshQueued) return;
            refreshQueued = true;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); refreshQueued = false; ScreenRefresh(why); };
            t.Start();
        }

        private static int blankTicks;

        // developer switch %TEMP%\selene-dev-isolated: a test copy run beside a PC in use (on a hidden desktop) - it reads no
        // controller (the pad belongs to whatever the user is playing) and its own sound stays muted
        public static readonly bool Isolated = System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-isolated"));
        // a link, a store app or a file opened outside Playnite; a test copy beside a PC in use opens nothing (it would open in the
        // user's own browser or store launcher)
        public static void OpenOutside(string target)
        {
            if (Isolated) { Log("dev isolated: not opened (" + target + ")"); return; }
            System.Diagnostics.Process.Start(target);
        }

        public static void StartIsolatedMute()
        {
            if (!Isolated) return;
            Log("dev isolated: own audio muted");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            t.Tick += (s, e) => { CloseToTray.SetOwnAudioMute(true); if (t.Interval.TotalMilliseconds < 1000 && (DateTime.Now - Process0).TotalSeconds > 20) t.Interval = TimeSpan.FromSeconds(1); };
            t.Start();
        }
        private static readonly DateTime Process0 = DateTime.Now;

        // dev keys for tests without a screen (LoginScene.DevKeys): a picture drawn by WPF itself (a hidden desktop has no
        // compositor to capture), a blank screen as after a game, Spotlight's LB / RB, and what Windows thinks is in front
        public static void DevShot(string name)
        {
            try
            {
                var w = Application.Current.MainWindow;
                var root = w != null ? w.Content as FrameworkElement : null;
                if (root == null) return;
                int pw = (int)Math.Max(1, root.ActualWidth), ph = (int)Math.Max(1, root.ActualHeight);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, pw, ph)); dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, pw, ph)); }
                rtb.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-shot-" + name + ".png");
                using (var fs = System.IO.File.Create(file)) enc.Save(fs);
                Log("dev shot: " + file + " (root opacity " + root.Opacity.ToString("0.00") + ")");
            }
            catch (Exception ex) { Log("dev shot failed: " + ex.Message); }
        }
        public static void DevBlank()
        {
            var w = Application.Current.MainWindow;
            var root = w != null ? w.Content as UIElement : null;
            if (root != null) root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
            Log("dev blank: root opacity 0 (as behind a game)");
        }
        public static void DevCycleFilter(int dir) { CycleRowFilter(dir); }
        public static void DevGuide() { Log("dev guide: as the Guide button"); GoHomeFromGuide(); }
        public static void DevR3() { TogglePreviewSound(); }
        // dev key "testgame:<exe>": a game of that program, started like any game (launch cover, return); removed once it stops
        public static Guid DevGameId = Guid.Empty;
        public static void DevTestGame(string exe)
        {
            try
            {
                string args = null;
                var bar = exe.IndexOf('|');
                if (bar > 0) { args = exe.Substring(bar + 1); exe = exe.Substring(0, bar); }   // "testgame:<exe>|<arguments>"
                var g = Api.Database.Games.FirstOrDefault(x => x.Name == "Selene test game");
                if (g == null)
                {
                    g = new Playnite.SDK.Models.Game("Selene test game") { IsInstalled = true, Hidden = true, InstallDirectory = System.IO.Path.GetDirectoryName(exe) };
                    g.GameActions = new System.Collections.ObjectModel.ObservableCollection<Playnite.SDK.Models.GameAction> {
                        new Playnite.SDK.Models.GameAction { Name = "Play", IsPlayAction = true, Type = Playnite.SDK.Models.GameActionType.File, Path = exe, WorkingDir = System.IO.Path.GetDirectoryName(exe), TrackingMode = Playnite.SDK.Models.TrackingMode.Process } };
                    Api.Database.Games.Add(g);
                }
                var play = g.GameActions != null ? g.GameActions.FirstOrDefault(a => a.IsPlayAction) : null;
                if (play != null && play.Arguments != args) { play.Arguments = args; Api.Database.Games.Update(g); }
                DevGameId = g.Id;
                Log("dev test game: starting " + exe + (args != null ? " " + args : ""));
                Api.StartGame(g.Id);
            }
            catch (Exception ex) { Log("dev test game: " + ex.Message); }
        }
        public static void DevFront()
        {
            var w = Application.Current.MainWindow;
            var root = w != null ? w.Content as UIElement : null;
            Log("dev front: window " + (w != null ? w.WindowState.ToString() : "?") + ", Playnite in front=" + PlayniteInFront() + ", window active=" + (w != null && w.IsActive) + ", root opacity " + (root != null ? root.Opacity.ToString("0.00") : "?") + ", quitting=" + Quitting);
        }
        private static void StartBlankWatch()
        {
            var since = DateTime.Now;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (s, e) =>
            {
                try
                {
                    if ((DateTime.Now - since).TotalSeconds < 20) return;   // (the intro has its own covers)
                    var w = Application.Current.MainWindow;
                    var root = w != null ? w.Content as UIElement : null;
                    bool blank = root != null && root.Opacity < 0.01 && w.IsVisible && !launching && !guideSwitching && !Quitting && PlayniteInFront();
                    blankTicks = blank ? blankTicks + 1 : 0;
                    if (blankTicks < 2) return;
                    blankTicks = 0;
                    Log("screen: Playnite in front but blank for 2 s -> shown");
                    PlayEnterAnimation(w);
                    ScreenRefresh("blank in front");
                }
                catch (Exception ex) { Log("blank watch: " + ex.Message); }
            };
            t.Start();
        }

        private static void StartDeviceModeWatch()
        {
            startedAsChild = RowMode == "all";
            if (startedAsChild || string.IsNullOrWhiteSpace(SelenePaths.StreamLogs)) return;
            var dispatcher = Application.Current.Dispatcher;
            var thread = new System.Threading.Thread(() =>
            {
                string last = RowMode;
                while (true)
                {
                    string mode = "tv";
                    try { if (TabletStreaming()) mode = "all"; } catch { }
                    if (mode != last)
                    {
                        last = mode;
                        var m = mode;
                        dispatcher.BeginInvoke(new Action(() => ApplyDeviceMode(m)));
                    }
                    System.Threading.Thread.Sleep(3000);
                }
            }) { IsBackground = true, Name = "SeleneDeviceMode" };
            thread.Start();
        }

        private static bool TabletStreaming()
        {
            var dir = new System.IO.DirectoryInfo(SelenePaths.StreamLogs);
            if (!dir.Exists) return false;
            System.IO.FileInfo newest = null;
            foreach (var f in dir.GetFiles("*.log")) if (newest == null || f.LastWriteTime > newest.LastWriteTime) newest = f;
            if (newest == null) return false;
            string text;
            using (var fs = new System.IO.FileStream(newest.FullName, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            {
                fs.Seek(Math.Max(0, fs.Length - 512 * 1024), System.IO.SeekOrigin.Begin);
                text = new System.IO.StreamReader(fs).ReadToEnd();
            }
            var events = System.Text.RegularExpressions.Regex.Matches(text, "CLIENT (CONNECTED|DISCONNECTED)");
            if (events.Count == 0 || events[events.Count - 1].Groups[1].Value != "CONNECTED") return false;
            var names = System.Text.RegularExpressions.Regex.Matches(text, @"Display mode for client \[([^\]]+)\]");
            if (names.Count == 0) return false;
            var client = names[names.Count - 1].Groups[1].Value;
            var match = SelenePaths.StreamClientMatch;  // empty = any client
            return match == "" || System.Text.RegularExpressions.Regex.IsMatch(client, match, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static void ApplyDeviceMode(string mode)
        {
            try
            {
                if (startedAsChild || mode == RowMode) return;
                RowMode = mode;
                Application.Current.Resources["SeleneRowMode"] = mode;
                var row = LiveRow();
                if (row != null) RebuildGames(row);
                if (mode == "tv") EnsureBigScreenFilter();
                else if (Api != null && Api.MainView.GetActiveFilterPreset() == BigScreenPreset)
                {
                    foreach (var fp in Api.Database.FilterPresets)
                        if (fp.Name == "All") { Api.MainView.ApplyFilterPreset(fp.Id); break; }
                }
                Log("device mode: " + (mode == "all" ? "tablet stream (all games)" : "TV (Handheld games hidden)"));
            }
            catch (Exception ex) { Log("device mode failed: " + ex.Message); }
        }

        // ---- sleeping the PC ends the stream properly (clients used to freeze until they timed out) ----
        // Any sleep -> ask the streaming host (Sunshine-style web API on localhost:47990) to disconnect connected clients first.
        // Optional: settings point at a credentials file (line 1 user, line 2 password; keep it readable by this account only).
        private static void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Suspend) return;
            try { DisconnectStreamClients(); } catch (Exception ex) { Log("sleep disconnect failed: " + ex.Message); }
        }

        private static void DisconnectStreamClients()
        {
            var credFile = SelenePaths.StreamCredentials;
            if (string.IsNullOrWhiteSpace(credFile) || !System.IO.File.Exists(credFile)) return;
            var lines = System.IO.File.ReadAllLines(credFile);
            if (lines.Length < 2) return;
            var auth = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(lines[0].Trim() + ":" + lines[1].Trim()));
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;  // .NET Framework defaults to old TLS
            System.Net.ServicePointManager.ServerCertificateValidationCallback = (s, cert, chain, errors) =>
            {
                var req = s as System.Net.HttpWebRequest;
                return req != null && req.RequestUri.Host == "localhost";  // the host's self-signed certificate, local only
            };
            string list = HttpCall("GET", "https://localhost:47990/api/clients/list", auth, null);
            int n = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(list, "\\{[^{}]*\"connected\":true[^{}]*\\}"))
            {
                var uuid = System.Text.RegularExpressions.Regex.Match(m.Value, "\"uuid\":\"([^\"]+)\"").Groups[1].Value;
                if (uuid.Length == 0) continue;
                HttpCall("POST", "https://localhost:47990/api/clients/disconnect", auth, "{\"uuid\":\"" + uuid + "\"}");
                n++;
            }
            Log("sleep: disconnected " + n + " stream client(s)");
        }

        private static string HttpCall(string method, string url, string auth, string body)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 2500;
            req.ReadWriteTimeout = 2500;
            req.KeepAlive = false;
            req.Headers["Authorization"] = auth;
            if (body != null)
            {
                req.ContentType = "application/json";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            using (var resp = req.GetResponse())
            using (var r = new System.IO.StreamReader(resp.GetResponseStream()))
                return r.ReadToEnd();
        }

        // TV: the library shows the "Big Screen" filter preset (a streamed session keeps the whole library)
        private static readonly Guid BigScreenPreset = new Guid("e1fb63ec-0f24-4eaf-bfd2-68fbe05d1411");

        private static void EnsureBigScreenFilter()
        {
            try
            {
                if (Api == null || RowMode != "tv") return;
                // only when nothing is chosen: a tab picked with LB / RB in Collection stays
                return;   // Collection follows Selene's tabs now (CollectionPresets); the old Big Screen preset isn't forced
            }
            catch (Exception ex) { Log("filter: " + ex.Message); }
        }

        // Switching the layout rebuilds the library list and drops focus (controller dead). Put focus on the
        // selected game (or the first one) once the list exists.
        private static void FocusLibraryLater(Window w) { FocusLibraryLater(w, true); }

        // fromHome=false: keep the library's own selection (coming back to a game launched from the library)
        private static void FocusLibraryLater(Window w, bool fromHome)
        {
            int tries = 0;
            // quick looks first (the list is usually there within a frame or two; a 150 ms first look read as a delay), then slower
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (s, e) =>
            {
                tries++;
                if (tries == 6) timer.Interval = TimeSpan.FromMilliseconds(150);
                var list = FindByName(w, "PART_ListGameItems") as ListBox;
                if (list == null || !list.IsVisible || list.Items.Count == 0) { if (tries > 20) { timer.Stop(); Log("library open: list not found/visible"); } return; }
                timer.Stop();
                // the game that was selected on the home row (cards excepted) is the one the library opens on
                // (the list holds GamesCollectionViewEntry wrappers, not Game objects: match by Id)
                int want = -1;
                var wantId = fromHome && lastSelected != null && !(lastSelected is string) ? Prop(lastSelected, "Id") : null;
                if (wantId != null)
                    for (int i = 0; i < list.Items.Count; i++)
                    {
                        var it = list.Items[i];
                        var g = Prop(it, "Game");
                        if (wantId.Equals(Prop(it, "Id")) || (g != null && wantId.Equals(Prop(g, "Id")))) { want = i; break; }
                    }
                if (tries <= 1) Log("library open: home=" + lastSelected + " id=" + wantId + " -> index " + want + " of " + list.Items.Count
                    + " (was " + list.SelectedIndex + ", focused=" + list.IsKeyboardFocusWithin + ", item type=" + (list.Items.Count > 0 ? list.Items[0].GetType().Name : "-") + ")");
                if (wantId != null && want < 0 && tries <= 1) Log("library focus: " + lastSelected + " not in list");
                if (want >= 0 && list.SelectedIndex != want) list.SelectedIndex = want;
                else if (list.SelectedIndex < 0) list.SelectedIndex = 0;
                if (list.IsKeyboardFocusWithin && want < 0) return;
                // ScrollIntoView does nothing in the horizontal layout (virtualized panel, tile not generated yet):
                // ask the panel itself to bring the index into view, then the container exists and can take focus
                var c = list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as UIElement;
                if (c == null || !c.IsVisible)
                {
                    var vp = FindChild<VirtualizingPanel>(list);
                    var vsp = vp as VirtualizingStackPanel;
                    try
                    {
                        // Playnite's FullscreenTilePanel ignores BringIndexIntoView but has its own public ScrollToItem
                        var scrollTo = vp != null ? vp.GetType().GetMethod("ScrollToItem", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(object) }, null) : null;
                        if (scrollTo != null) scrollTo.Invoke(vp, new object[] { list.SelectedItem });
                        else if (vsp != null) vsp.BringIndexIntoViewPublic(list.SelectedIndex);
                        else if (vp != null) typeof(VirtualizingPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(vp, new object[] { list.SelectedIndex });
                        else list.ScrollIntoView(list.SelectedItem);
                    }
                    catch (Exception ex) { if (tries <= 1) Log("library scroll: " + ex.Message); }
                    list.UpdateLayout();
                    c = list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as UIElement;
                    if (tries <= 1) Log("library scroll: panel=" + (vp != null ? vp.GetType().Name : "none") + " container=" + (c != null));
                }
                if (c == null || !c.IsVisible)
                {
                    if (tries < 25) { timer.Start(); return; }
                    Log("library focus: tile never appeared, focusing list");
                    list.Focus(); return;
                }
                // the tile with one whole cover to its left (at the very edge, Left had nothing built to land on and did nothing)
                SnapToWholeCovers(list, list.SelectedItem);
                c = list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as UIElement ?? c;
                c.Focus();
                Log("library focus -> " + list.SelectedItem + " (tile focused=" + c.IsKeyboardFocusWithin + ")");
            };
            timer.Start();
        }

        private static void FindAndPlay(Window w)
        {
            // the hub page template may still be materialising; give layout a moment
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                var row = FindByName(w, "SeleneGameRow") as ListBox;
                Log("find row: " + (row != null));
                if (row == null) return;
                HookRow(row);
                HookHub(w);
                HookNewsSlide(row);
                if (Lite)
                {
                    foreach (var name in new[] { "HubBackgroundVideo" })
                    {
                        var v = FindByName(w, name) as MediaElement;
                        if (v != null && v.Visibility == Visibility.Visible) { v.Stop(); v.Source = null; v.Visibility = Visibility.Collapsed; Log("lite: " + name + " off"); }
                    }
                }
                QueueIntro(row);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static void HookRow(ListBox row)
        {
            object dummy;
            if (row == null || row.Name != "SeleneGameRow" || hookedRows.TryGetValue(row, out dummy)) return;
            hookedRows.Add(row, null);
            liveRow = new WeakReference(row);
            HookMoveCommand(row);
            ShrinkRowCache(row);
            UseOwnGameList(row);
            // the old theme rebuilds the row on every LB/RB page switch and focuses its first item (a card), which made the
            // row jump. Start the new row at the default spot and redirect focus that enters from outside.
            var sv0 = FindChild<ScrollViewer>(row);
            if (sv0 != null) sv0.ScrollToHorizontalOffset(Math.Max(0, FirstGameIndex(row) - AnchorSlot));
            row.PreviewGotKeyboardFocus += OnRowPreviewGotFocus;
            // a mouse click on a card opens it as A does (Playnite's own click handling treated it like a game)
            row.PreviewMouseLeftButtonDown += (s, a) =>
            {
                var item = ItemsControl.ContainerFromElement(row, a.OriginalSource as DependencyObject) as ListBoxItem;
                var nav = item != null ? item.DataContext as string : null;
                if (nav == null || !nav.StartsWith("nav:")) return;
                a.Handled = true;
                item.Focus();
                RunCard(row, nav.Substring(4));
            };
            // Going up to the top bar (or anywhere outside the row) must not scroll the row back to the start.
            row.LostKeyboardFocus += (s, a) =>
            {
                var nf = a.NewFocus as DependencyObject;
                if (nf != null && IsInRow(nf)) return;
                var sv = FindChild<ScrollViewer>(row);
                if (sv != null) heldOffset = sv.HorizontalOffset;
            };
            row.GotKeyboardFocus += (s, a) => { heldOffset = -1; };
            // focus outline only while the row itself has focus (down to Play/Details or up to the top bar drops it)
            row.IsKeyboardFocusWithinChanged += (s, a) =>
            {
                var win = Window.GetWindow(row);
                if (win != null && !win.IsActive) return;  // switching to a game: leave the look alone
                if (!(bool)a.NewValue)
                {
                    // the focus went to another window (Playnite's hidden progress window as a notification comes up, a
                    // dialog): the ring stays (it blinked off and on with every notification)
                    var now = Keyboard.FocusedElement as DependencyObject;
                    if (now == null || Window.GetWindow(now) != win) return;
                }
                SetRowFocusLook(row, (bool)a.NewValue);
            };
            Action hookScroll = () =>
            {
                var sv = FindChild<ScrollViewer>(row);
                if (sv == null) return;
                RestRow(row, sv);
                RowEdges(row, sv);
                row.SizeChanged += (s2, a2) => RowEdges(row, sv);
                sv.ScrollChanged += (s2, a2) =>
                {
                    if (heldOffset >= 0 && !row.IsKeyboardFocusWithin && Math.Abs(sv.HorizontalOffset - heldOffset) > 0.5)
                    {
                        sv.ScrollToHorizontalOffset(heldOffset);
                    }
                    if (!glideHooked) RestRow(row, sv);   // (a jump, not a move: the rest at once)
                    RowEdges(row, sv);
                };
            };
            row.ItemContainerGenerator.StatusChanged += (s, a) =>
            {
                if (row.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) EnsureTileCaches(row);
            };
            row.SelectionChanged += (s, a) =>
            {
                if (!row.IsVisible || row.SelectedItem == null) return;
                // an old row from before an LB/RB rebuild can still fire (its list is refreshed): only the live row
                // decides where home resumes (the old one kept putting it back on NBA 2K26)
                if (LiveRow() != null && !ReferenceEquals(LiveRow(), row)) return;
                lastSelected = row.SelectedItem;
                if (rideFirst >= 0) return;   // a walk to a picked game: the work per game waits for the game it lands on (it stuttered)
                AfterSelection(row);
            };
            // the game already selected when the row appears never raises SelectionChanged
            row.IsVisibleChanged += (s, a) => { if (row.IsVisible && row.SelectedItem != null) GameMedia.OnSelected(row.SelectedItem, RowNeighbours(row)); };
            if (row.IsVisible && row.SelectedItem != null) GameMedia.OnSelected(row.SelectedItem, RowNeighbours(row));
            if (row.IsLoaded) hookScroll(); else row.Loaded += (s, a) => hookScroll();
            // Selection follows focus but is NOT cleared when focus leaves the row (the old style trigger did that,
            // which blanked SelectedGame for a moment: highlight flicker + music restart on LB/RB and overlays).
            row.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnRowGotFocus), true);
            Log("row loaded, visible=" + row.IsVisible);
            // LB/RB rebuilt the page: its new backgrounds / cards need the same treatment as the first load
            var rw = Window.GetWindow(row);
            if (rw != null) { ReplaceFadeImages(rw); CacheNewsCards(rw); RenderFilterTabs(rw); }
            // (no row.IsVisibleChanged hook: LB/RB page switches hide/show the row and must not replay anything)
            // the old theme opens the hub at startup behind the login screen ("AcceuilSettings"), so the row is already
            // visible when the user presses A: replay the intro when the login screen goes away.
            var window = Window.GetWindow(row);
            var login = window != null ? FindByName(window, "AcceuilSettings") : null;
            Log("login screen found=" + (login != null) + (login != null ? " visible=" + login.IsVisible : ""));
            if (login != null) login.IsVisibleChanged += (s, a) => { if (!(bool)a.NewValue && row.IsVisible && !LoginScene.KeepHome) QueueIntro(row); };
            if (login != null) LoginScene.Attach(window, login);
            // A visible new row after the first intro = the old theme rebuilt page 1 on an LB/RB switch: keep the user's spot
            // (resetting changed SelectedGame -> background flash and music restart).
            if (row.IsVisible) { if (introDone && lastSelected != null) RestoreSelection(row); else QueueIntro(row); }
        }

        private static object lastSelected;
        private static double heldOffset = -1;
        private static WeakReference liveRow;
        private static ListBox LiveRow() { return liveRow != null ? liveRow.Target as ListBox : null; }

        // The row tells Playnite which game is selected (SelectedGame drives background, info, music). Only real games
        // are pushed, 250 ms after the selection settles (holding Left/Right stays smooth); cards and "nothing" never are.
        private static System.Windows.Threading.DispatcherTimer pushTimer;

        private static DateTime lastPushAt = DateTime.MinValue;

        private static void QueuePush(ListBox row)
        {
            // first move after a rest goes through immediately (music fades back to the default right away);
            // fast scrolling stays debounced so holding the stick does not stutter
            if (pushTimer == null)
            {
                pushTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                pushTimer.Tick += (s, e) => { pushTimer.Stop(); lastPushAt = DateTime.Now; var r = pushTimer.Tag as ListBox; if (r != null) PushSelected(r); };
            }
            pushTimer.Stop();
            pushTimer.Tag = row;
            pushTimer.Start();
        }

        // the home's game art fades in or out when a profile's home background changes
        public static void FadeArt(double to)
        {
            var w = Application.Current.MainWindow;
            var art = w != null ? FindByName(w, "SeleneHubArt") : null;
            if (art == null) return;
            if (LoginScene.IsActive) { art.BeginAnimation(UIElement.OpacityProperty, null); return; }   // hidden under the profile screen: at once
            var fade = new DoubleAnimation(art.Opacity, to, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fade.Completed += (s, e) => art.BeginAnimation(UIElement.OpacityProperty, null);
            art.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // the background step's preview: the row's first game's art shown in the home's art layer (its own picture, not
        // Playnite's selected game, so no song starts). False when the library has no game with art to borrow.
        private static Image previewArt;

        // something the profile steps' preview shows changed (it is pictured again only then)
        public static int PreviewVersion { get; private set; }
        public static DateTime PreviewChangedAt { get; private set; }
        public static void PreviewChanged() { PreviewVersion++; PreviewChangedAt = DateTime.Now; }

        public static bool ShowArtForPreview(bool on)
        {
            if (on && previewLayer != null && previewLayer.Parent != null) return lastHasArt;   // already there: nothing to redo
            previewWanted = on;
            PreviewChanged();
            var w = Application.Current.MainWindow;
            var art = w != null ? FindByName(w, "SeleneHubArt") as Panel : null;
            if (previewLayer != null)
            {
                var parent = VisualTreeHelper.GetParent(previewLayer) as Panel;
                if (parent != null) parent.Children.Remove(previewLayer);
                previewLayer = null;
            }
            previewArt = null;
            if (!on) return false;
            if (art == null) { Log("preview art: no art layer"); return false; }
            var row = LiveRow();
            string file = null, name = null;
            if (row != null && Api != null)
                foreach (var item in row.Items)
                {
                    if (item is string || item is RowPad) continue;
                    var id = Prop(item, "Id");
                    if (!(id is Guid)) continue;
                    var game = Api.Database.Games.Get((Guid)id);
                    if (game == null) continue;
                    var hero = System.IO.Path.Combine(SelenePaths.Media("Hero"), game.Id + ".jpg");
                    file = System.IO.File.Exists(hero) ? hero
                         : !string.IsNullOrEmpty(game.BackgroundImage) ? Api.Database.GetFullFilePath(game.BackgroundImage) : null;
                    if (file != null && !file.StartsWith("http", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(file)) { name = game.Name; break; }
                    file = null;
                }
            var pic = file != null ? Wallpapers.Load(file) : null;
            Log("preview art: the example game (library art: " + (pic != null ? name : "none") + ")");
            // always the example game (the same picture for everyone, whatever the library holds); the row's own game only
            // tells the note below whether the library has art of its own
            SampleArtForPreview(art);
            lastHasArt = pic != null;
            return lastHasArt;
        }

        private static void PutPreviewArt(Panel art, ImageSource pic, bool sample)
        {
            var img = new Image { Source = pic, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var layer = new Grid { Opacity = 1, IsHitTestVisible = false };
            layer.Children.Add(img);
            if (sample)
                layer.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 60, 150),
                    CornerRadius = new CornerRadius(16), Padding = new Thickness(18, 7, 18, 8), Background = Ink.Brush("SeleneGlassColor", 0.6),
                    Child = new TextBlock { Text = "Example game art \u00B7 " + SampleName, FontSize = 18, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.85) }
                });
            previewArt = new Image();   // a marker for removal: the layer is what's shown
            art.Children.Add(layer);
            previewLayer = layer;
            PreviewChanged();
        }

        // no game in the library yet: an example game's art (Steam's library art for it, fetched once and kept in Selene's media
        // folder, not shipped with Selene), so the Game art / Background only choice shows what it does
        private const string SampleName = "Clair Obscur: Expedition 33";
        private const string SampleUrl = "https://cdn.akamai.steamstatic.com/steam/apps/1903340/library_hero.jpg";
        private static Grid previewLayer;

        private static ImageSource samplePic, sampleCover, sampleLogo;
        public static ImageSource SamplePicture { get { return samplePic ?? (samplePic = LoadSample("expedition33-hero.jpg", 1920)); } }
        public static ImageSource SampleCover { get { return sampleCover ?? (sampleCover = LoadSample("expedition33-cover.jpg", 300)); } }
        public static ImageSource SampleLogo { get { return sampleLogo ?? (sampleLogo = LoadSample("expedition33-logo.png", 700)); } }

        // the preview's row: a few well-known games' covers beside the sample (Steam's, fetched once)
        private static readonly string[] RowGames = { "1145350", "1030300", "1086940", "1245620", "1091500" };   // Hades II, Silksong, Baldur's Gate 3, Elden Ring, Cyberpunk 2077
        private static ImageSource[] rowCovers;
        public static ImageSource[] SampleRowCovers
        {
            get
            {
                if (rowCovers == null || rowCovers.Any(c => c == null)) rowCovers = RowGames.Select(id => LoadSample("cover-" + id + ".jpg", 240)).ToArray();
                return rowCovers;
            }
        }

        private static ImageSource LoadSample(string name, int width)
        {
            var f = System.IO.Path.Combine(SelenePaths.Media("Sample"), name);
            return System.IO.File.Exists(f) ? Wallpapers.Load(f, width) : null;
        }

        // the sample game's pictures from Steam (once; kept in Selene's media folder, not shipped), then `ready` on the UI thread
        private static bool fetchingSample;
        public static void FetchSample(Action ready)
        {
            if (!DownloadConsent.Allowed("artdownload")) return;
            if (fetchingSample) return;
            fetchingSample = true;
            var ui = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
                var wanted = new List<string[]> { new[] { "1903340", "library_hero.jpg", "expedition33-hero.jpg" }, new[] { "1903340", "library_600x900.jpg", "expedition33-cover.jpg" }, new[] { "1903340", "logo.png", "expedition33-logo.png" } };
                foreach (var id in RowGames) wanted.Add(new[] { id, "library_600x900.jpg", "cover-" + id + ".jpg" });
                foreach (var w in wanted)
                {
                    var pair = new[] { w[1], w[2] };
                    var file = System.IO.Path.Combine(SelenePaths.Media("Sample"), pair[1]);
                    if (System.IO.File.Exists(file)) continue;
                    try
                    {
                        if (!DownloadConsent.Allowed("artdownload")) break;
                        using (var web = new System.Net.WebClient()) web.DownloadFile("https://cdn.akamai.steamstatic.com/steam/apps/" + w[0] + "/" + pair[0], file + ".part");
                        System.IO.File.Move(file + ".part", file);
                    }
                    catch (Exception ex) { Log("sample: " + pair[0] + " failed: " + ex.Message); }
                }
                ui.BeginInvoke(new Action(() => { fetchingSample = false; samplePic = sampleCover = sampleLogo = null; rowCovers = null; ready(); }));
            });
        }
        private static bool SampleArtForPreview(Panel art)
        {
            var file = System.IO.Path.Combine(SelenePaths.Media("Sample"), "expedition33-hero.jpg");
            if (samplePic == null && System.IO.File.Exists(file)) samplePic = Wallpapers.Load(file);   // decoded once, kept
            if (samplePic != null) { PutPreviewArt(art, samplePic, true); return true; }
            if (!DownloadConsent.Allowed("artdownload")) return false;
            var ui = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try
                {
                    try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
                    if (!DownloadConsent.Allowed("artdownload")) return;
                    using (var web = new System.Net.WebClient()) web.DownloadFile(SampleUrl, file + ".part");
                    if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                    System.IO.File.Move(file + ".part", file);
                    var pic = Wallpapers.Load(file);
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (pic == null || !previewWanted) return;
                        PutPreviewArt(art, pic, true);
                        ProfileSteps.RefreshPreviewLater();
                    }));
                }
                catch (Exception ex) { Log("preview art: sample download failed: " + ex.Message); }
            });
            return true;   // it comes in a moment
        }
        private static bool previewWanted, lastHasArt;

        // Collection's hidden game list stays tied to Playnite's selected game, and picked its first game (First Light) again
        // whenever Spotlight set "no game" for a card: Spotlight then loaded that art (the flash). While Spotlight shows,
        // Playnite's selection is kept to Spotlight's own: nothing on a card, the row's game on a game.
        private static bool guarding;

        public static void GuardSpotlightSelection()
        {
            if (guarding || launching || SeleneShell.CollectionActive) return;
            var w = Application.Current.MainWindow;
            var hub = w != null ? FindByName(w, "WelcomeHubOverlay") : null;
            var row = LiveRow();
            if (hub == null || !hub.IsVisible || row == null || LoginScene.IsActive) return;
            var vm = w.DataContext;
            var prop = vm != null ? vm.GetType().GetProperty("SelectedGame") : null;
            if (prop == null || !prop.CanWrite) return;
            var item = row.SelectedItem;
            object want = item is string || item is RowPad ? null : item;
            var now = prop.GetValue(vm, null);
            if (ReferenceEquals(now, want) || (want != null && now == null)) return;   // (a game being pushed after its delay is fine)
            guarding = true;
            try { prop.SetValue(vm, want, null); Log("spotlight: selection put back (" + (now != null ? Prop(now, "Name") : "null") + " -> " + (want != null ? Prop(want, "Name") : "none") + ")"); }
            finally { guarding = false; }
        }

        // back from the library: Playnite still has the library's game selected, and the home drew its art for a moment
        // before the row's own selection caught up; the row's selection (nothing for a card) goes to Playnite first
        // Find a game's pick, shown where the search was opened: on Spotlight's row when the home is up, in Collection
        // when Collection is
        public static void ShowGame(Guid id)
        {
            if (SeleneShell.CollectionActive) ShowInCollection(id);
            else ShowInSpotlight(id);
        }

        // Spotlight's row moves to this game (on "All games" when the current tab doesn't have it)
        public static void ShowInSpotlight(Guid id)
        {
            var w = Application.Current.MainWindow;
            var row = LiveRow();
            if (w == null || row == null) { ShowInCollection(id); return; }
            HidePreview();
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                int idx = RowIndexOf(row, id);
                if (idx < 0 && RowFilters[RowFilter] != "all")
                {
                    rowFilter = Array.IndexOf(RowFilters, "all");
                    RenderFilterTabs(w, 0);
                    ApplyRowFilter(row);
                    idx = RowIndexOf(row, id);
                }
                // not by its id: the same game by name (DuplicateHider shows one copy of a game you have twice, and the pick may be
                // the other copy)
                if (idx < 0) idx = RowIndexOfName(row, id);
                Log("find a game: " + (idx >= 0 ? "picked on Spotlight (tile " + idx + ")" : "not on Spotlight's row"));
                // (Spotlight's row is Playnite's own list, its filter can leave a game out: its options then, Play among them)
                if (idx < 0) { LogMissing(id); GameOptionsScreen.Open(id); return; }
                int cur = row.SelectedIndex >= 0 && row.SelectedIndex < row.Items.Count ? row.SelectedIndex : 0;
                // the focus comes from outside the row (the search layer): the row's guard would send it back to the old
                // selection, so it's told which tile is wanted
                // a screen still closing (a card's) can take the focus back with the old tile: looked at again, a few times
                Action settle = () =>
                {
                    Log("find a game: row focused=" + row.IsKeyboardFocusWithin + " selected=" + row.SelectedIndex);
                    int tries = 0;
                    var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    again.Tick += (s2, e2) =>
                    {
                        if (++tries > 6) { again.Stop(); return; }
                        if (LoginScene.IsActive) return;
                        if (row.SelectedIndex == idx) { again.Stop(); return; }   // (stops at once once right: a move of yours isn't undone)
                        Log("find a game: the selection went back (" + row.SelectedIndex + "), again");
                        wantedFocusIndex = idx;
                        try { PlaceAtLeft(row, idx); }
                        catch (Exception ex) { again.Stop(); Log("find a game: again failed: " + ex.Message); }
                        finally { wantedFocusIndex = -1; }
                    };
                    again.Start();
                };
                if (!RideTo(row, cur, idx, settle))
                {
                    wantedFocusIndex = idx;
                    try { PlaceAtLeft(row, idx); }   // the game you went looking for, first on the row
                    finally { wantedFocusIndex = -1; }
                    settle();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // The row walks to a picked game the way a held stick would: the ring steps tile to tile, each step with its tick and
        // the row's glide, until it lands on it (it used to be there at once). From far away it lands six tiles short first,
        // then walks the rest. False when it doesn't apply (the switch off, nothing to walk): the caller places it at once.
        // Switch: Spotlight > Scroll to a picked game
        // the row's first tile during a walk (-1: none): the scroll never goes left of it, so the ring crosses the first slots
        // first, as it does at a row's start
        private static int rideFirst = -1;
        private static double AnchorOffset(int idx) { return Math.Max(rideFirst >= 0 ? rideFirst : 0, idx - AnchorSlot); }

        private static bool RideTo(ListBox row, int from, int idx, Action done)
        {
            try
            {
                if (Lite || !Features.On("pickscroll") || idx < 0 || idx >= row.Items.Count) return false;
                // from the row's first game, as a held stick from the start would; from far away, from 24 games before it: every
                // game passed has its tile built and its cover decoded, and hundreds of those in a second stutter on any PC
                const int reach = 24;
                int start = Math.Max(Math.Max(0, FirstGameIndex(row)), idx - reach);
                if (start >= idx) return false;
                int span = idx - start;
                // slow over the first slots, quick through the middle, easing down onto the game (a held stick's pace, then its speed)
                double seconds = Math.Min(1.2, 0.45 + span * 0.03);
                Ui.MeasureFrames("picked game walk", (int)(seconds * 1000) + 300);
                rideFirst = start;
                wantedFocusIndex = start;
                try { PlaceAtLeft(row, start); }   // the first game at the row's left edge
                finally { wantedFocusIndex = -1; }
                var step = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };   // (every 15 ms stuttered: the row has to lay out each time)
                int waits = 0;
                DateTime began = DateTime.MinValue, lastTick = DateTime.MinValue;
                Action finish = () => { step.Stop(); rideFirst = -1; EnsureTileCaches(row); AfterSelection(row); done(); };
                step.Tick += (s, e) =>
                {
                    if (LoginScene.IsActive) { if (++waits < 120) return; finish(); return; }   // (the screen it was picked on still closing: a moment)
                    var now = DateTime.Now;
                    if (began == DateTime.MinValue) began = now;
                    double p = Math.Min(1, (now - began).TotalSeconds / seconds);
                    double eased = p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;
                    int want = start + (int)Math.Round(span * eased);
                    int cur = row.SelectedIndex;
                    if (cur >= 0 && want > cur)
                    {
                        int next = want;
                        while (next < idx && !IsShown(row.Items[next])) next++;
                        if (next > idx) next = idx;
                        wantedFocusIndex = next;
                        try
                        {
                            MoveTo(row, cur, next);
                            if ((now - lastTick).TotalMilliseconds >= 55) { Ui.Tick(); lastTick = now; }   // (the tick plays on real presses only: here by hand)
                        }
                        finally { wantedFocusIndex = -1; }
                    }
                    if (p >= 1 || row.SelectedIndex == idx) finish();
                };
                step.Start();
                return true;
            }
            catch (Exception ex) { rideFirst = -1; Log("find a game: ride: " + ex.Message); return false; }
        }

        // a tile at the left edge of the row, focused (a pick from a search or a card): the usual move keeps the third slot and,
        // with the tile not built yet, left it at the right edge instead
        private static void PlaceAtLeft(ListBox row, int idx)
        {
            if (idx < 0 || idx >= row.Items.Count) return;
            // the row holds its place while the focus is outside it (the search layer still has it here): that hold undid the
            // scroll, so it's let go first. The game lands where a move would put it: the third slot
            heldOffset = -1;
            double offset = AnchorOffset(idx);
            var sv = FindChild<ScrollViewer>(row);
            if (sv != null) { sv.ScrollToHorizontalOffset(offset); row.UpdateLayout(); }
            var c = row.ItemContainerGenerator.ContainerFromIndex(idx) as UIElement;
            if (c == null)
            {
                row.ScrollIntoView(row.Items[idx]);
                row.UpdateLayout();
                if (sv != null) { sv.ScrollToHorizontalOffset(offset); row.UpdateLayout(); }
                c = row.ItemContainerGenerator.ContainerFromIndex(idx) as UIElement;
            }
            var panel = FindChild<VirtualizingStackPanel>(row);
            var shift = panel != null ? panel.RenderTransform as TranslateTransform : null;
            if (shift != null && !shift.IsFrozen) { shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = glideRest = RowRest(row); }   // no glide left over
            if (c != null) c.Focus(); else row.SelectedIndex = idx;   // (never set while the row's own selection is changing: that crashed Playnite)
            if (sv != null && Math.Abs(sv.HorizontalOffset - offset) > 0.5) { sv.ScrollToHorizontalOffset(offset); row.UpdateLayout(); }   // once more with the focus in the row
            ScheduleTitle(row, 230);
            EnsureTileCaches(row);
        }

        // why a game isn't on Spotlight's row: what the row is made from, and the game's own facts
        private static void LogMissing(Guid id)
        {
            try
            {
                var g = GameFacts.Find(id);
                int source = 0; bool inSource = false;
                if (gameSource != null) foreach (var o in gameSource) { source++; var gid = Prop(o, "Id"); if (gid is Guid && (Guid)gid == id) inSource = true; }
                bool inFiltered = Api != null && Api.MainView.FilteredGames.Any(x => x.Id == id);
                Log("find a game: missing " + (g != null ? g.Name : id.ToString()) + ": in Playnite's list " + inSource + " (" + source + " games), in its filtered games " + inFiltered
                    + " (" + (Api != null ? Api.MainView.FilteredGames.Count : 0) + "), preset " + (Api != null ? Api.MainView.GetActiveFilterPreset().ToString() : "?")
                    + (g != null ? ", hidden " + g.Hidden + ", source " + (g.Source != null ? g.Source.Name : "none") + ", platforms " + (g.Platforms != null ? string.Join("/", g.Platforms.Select(x => x.Name)) : "none") + ", installed " + g.IsInstalled : ""));
            }
            catch (Exception ex) { Log("find a game: missing: " + ex.Message); }
        }

        private static int RowIndexOfName(ListBox row, Guid id)
        {
            var g = GameFacts.Find(id);
            if (g == null || string.IsNullOrEmpty(g.Name)) return -1;
            for (int i = 0; i < row.Items.Count; i++)
            {
                var item = row.Items[i];
                if (item is string || item is RowPad) continue;
                if (string.Equals(Convert.ToString(Prop(item, "Name")), g.Name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static int RowIndexOf(ListBox row, Guid id)
        {
            for (int i = 0; i < row.Items.Count; i++)
            {
                var item = row.Items[i];
                if (item is string || item is RowPad) continue;
                var gid = Prop(item, "Id");
                if (gid is Guid && (Guid)gid == id) return i;
            }
            return -1;
        }

        // Collection with this game picked (Find a game): Collection on "All games" so the game is surely in it
        public static void ShowInCollection(Guid id)
        {
            var w = Application.Current.MainWindow;
            if (w == null) return;
            HidePreview();
            if (RowFilters[RowFilter] != "all") { rowFilter = Array.IndexOf(RowFilters, "all"); RenderFilterTabs(w, 0); var r = LiveRow(); if (r != null) ApplyRowFilter(r); }
            SeleneShell.ShowLibrary();
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                var list = FindByName(w, "PART_ListGameItems") as ListBox;
                if (list == null) return;
                object hit = null;
                foreach (var item in list.Items) { var gid = Prop(item, "Id"); if (gid is Guid && (Guid)gid == id) { hit = item; break; } }
                Log("find a game: " + (hit != null ? "picked in Collection" : "not in Collection's list"));
                if (hit == null) return;
                list.SelectedItem = hit;
                // its tile may not exist yet (the list only makes the ones near the screen): Playnite's panel scrolls to it,
                // then the tile can take the focus (else the focus stayed on the first game and took the selection back)
                int tries = 0;
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                t.Tick += (s2, e2) =>
                {
                    tries++;
                    if (!ReferenceEquals(list.SelectedItem, hit)) list.SelectedItem = hit;
                    var c = list.ItemContainerGenerator.ContainerFromItem(hit) as UIElement;
                    if (c == null || !c.IsVisible)
                    {
                        try
                        {
                            var vp = FindChild<VirtualizingPanel>(list);
                            var scrollTo = vp != null ? vp.GetType().GetMethod("ScrollToItem", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(object) }, null) : null;
                            if (scrollTo != null) scrollTo.Invoke(vp, new object[] { hit });
                            else list.ScrollIntoView(hit);
                        }
                        catch (Exception ex) { Log("find a game scroll: " + ex.Message); }
                        list.UpdateLayout();
                        c = list.ItemContainerGenerator.ContainerFromItem(hit) as UIElement;
                    }
                    if ((c == null || !c.IsVisible) && tries < 25) return;
                    t.Stop();
                    if (c != null) SnapToWholeCovers(list, hit);
                    c = list.ItemContainerGenerator.ContainerFromItem(hit) as UIElement ?? c;
                    if (c != null) { c.Focus(); Log("find a game: focused " + Prop(hit, "Name")); }
                    else Log("find a game: its tile never appeared");
                };
                t.Start();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Readable text everywhere: Selene's and the theme's text in the theme's ink color gets a soft halo in the theme's
        // background color (dark in dark themes, light in light ones), so it reads over bright art and wallpapers too.
        // One shared halo that follows theme changes; text that already has an effect keeps it.
        private static System.Windows.Media.Effects.DropShadowEffect textHalo;

        private static void OnTextLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var t = sender as TextBlock;
                if (t == null) return;
                Lang.Track(t);   // Selene's words in Playnite's language, when there's a file for it
                if (t.Effect != null || !Features.On("textshadow")) return;
                var b = t.Foreground as SolidColorBrush;
                if (b == null || b.Opacity < 0.45) return;
                var ink = Ink.Of("SeleneInkColor");
                var c = b.Color;
                if (c.A < 115 || Math.Abs(c.R - ink.R) + Math.Abs(c.G - ink.G) + Math.Abs(c.B - ink.B) > 60) return;   // only the ink-colored text
                // on the home and in Collection the halo adapts to the picture behind each text (none where it reads fine)
                if (AdaptiveText.Track(t)) return;
                if (textHalo == null)
                    textHalo = Ink.Track(new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 14, Opacity = 0.95, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance },
                        System.Windows.Media.Effects.DropShadowEffect.ColorProperty, "SeleneScrimColor");
                t.Effect = textHalo;
            }
            catch { }
        }

        // after jumping to a game, Collection's row sits on whole covers again (the jump left it part of a cover off, and the
        // next move cut a cover's ring at the left edge), the game one cover in from the left
        private static void SnapToWholeCovers(ListBox list, object item)
        {
            try
            {
                var sv = FindChild<ScrollViewer>(list);
                int idx = list.Items.IndexOf(item);
                if (sv == null || idx < 0) return;
                int first = Math.Max(0, idx - 1);
                bool itemUnits = Math.Abs(sv.ExtentWidth - list.Items.Count) < 2;   // item scrolling counts in covers
                double offset = first;
                if (!itemUnits)
                {
                    var a = list.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;
                    var b = list.ItemContainerGenerator.ContainerFromIndex(idx > 0 ? idx - 1 : idx + 1) as FrameworkElement;
                    if (a == null || b == null) return;
                    double pitch = Math.Abs(a.TranslatePoint(new Point(0, 0), list).X - b.TranslatePoint(new Point(0, 0), list).X);
                    if (pitch < 1) return;
                    offset = first * pitch;
                }
                sv.ScrollToHorizontalOffset(offset);
                list.UpdateLayout();
                Log("find a game: snapped to cover " + first + (itemUnits ? " (items)" : " (pixels " + offset.ToString("0") + ")"));
            }
            catch (Exception ex) { Log("find a game snap: " + ex.Message); }
        }

        // the Store card's own tile on the row: its highlight steps back while the focus is in the card's pictures
        public static void DimStoreCard(bool dim) { DimCard("nav:store", dim); }

        // the card steps back (ring off, no growth) while its pictures below are being gone through
        public static void DimCard(string card, bool dim)
        {
            var row = LiveRow();
            if (row == null) return;
            var c = row.ItemContainerGenerator.ContainerFromItem(card) as Control;
            if (c == null || c.Template == null) return;
            var ring = c.Template.FindName("Ring", c) as UIElement;
            var scale = c.Template.FindName("TileScale", c) as ScaleTransform;
            var ms = TimeSpan.FromMilliseconds(180);
            if (ring != null)
            {
                if (dim) ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, ms));
                else ring.BeginAnimation(UIElement.OpacityProperty, null);
            }
            if (scale != null)
            {
                // back from the pictures: the animation lets go when it's done (held, it kept the card big after the focus
                // had moved on to a game: the tile's own shrink couldn't get through)
                var to = new DoubleAnimation(dim ? 1 : 1.3, ms) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = dim ? FillBehavior.HoldEnd : FillBehavior.Stop };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, to);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, to);
            }
        }

        // the Store card is the one picked on Spotlight's row (its game pictures can be gone into with Down)
        public static bool StoreCardFocused() { return FocusedCard() == "nav:store"; }

        // the card ("nav:...") picked on Spotlight's row with the focus in the row, else null
        public static string FocusedCard()
        {
            if (SeleneShell.CollectionActive || LoginScene.IsActive) return null;
            var row = LiveRow();
            if (row == null || !row.IsVisible || !IsInRow(Keyboard.FocusedElement as DependencyObject)) return null;
            return row.SelectedItem as string;
        }

        public static void SyncSelectionForHome(Action artReady = null)
        {
            bool reported = false;
            Action ready = () => { if (!reported) { reported = true; if (artReady != null) artReady(); } };
            try
            {
                var row = LiveRow();
                var w = Application.Current.MainWindow;
                var vm = w != null ? w.DataContext : null;
                var prop = vm != null ? vm.GetType().GetProperty("SelectedGame") : null;
                if (row == null || prop == null || !prop.CanWrite) { ready(); return; }
                var item = row.SelectedItem;
                object game = item is string || item is RowPad ? null : item;
                var art = FindByName(w, "SeleneHubArt") as UIElement;
                // Spotlight's pictures held still while Collection showed (SeleneCrossfade.Hub): they already show its game,
                // a check afterwards only loads anything if they don't (e.g. Playnite started on Collection)
                if (art != null) foreach (var cf in FindAll(art, d => d is SeleneCrossfade)) { var x = (SeleneCrossfade)cf; x.Dispatcher.BeginInvoke(new Action(x.Refresh), System.Windows.Threading.DispatcherPriority.Background); }
                if (!ReferenceEquals(prop.GetValue(vm, null), game)) prop.SetValue(vm, game, null);
                ready();
            }
            catch (Exception ex) { Log("home selection sync: " + ex.Message); ready(); }
        }

        // while the login screens cover the home nothing is selected for Playnite (its music add-ons would play)
        public static void ClearSelectedGame()
        {
            SetMusic("login");
            var w = Application.Current.MainWindow;
            var vm = w != null ? w.DataContext : null;
            var prop = vm != null ? vm.GetType().GetProperty("SelectedGame") : null;
            if (prop != null && prop.CanWrite && prop.GetValue(vm, null) != null) prop.SetValue(vm, null, null);
        }

        private static void PushSelected(ListBox row)
        {
            if (LoginScene.CoversHome) { ClearSelectedGame(); return; }
            var game = row.SelectedItem;
            if (game == null) return;
            LoginScene.HomeSelection(game is string);
            // the default track on the home, cards and games alike: a game's song comes with its trailer card
            SetMusic(previewShowing ? "trailer" : "home");
            var vm = row.DataContext;
            var prop = vm != null ? vm.GetType().GetProperty("SelectedGame") : null;
            if (prop == null || !prop.CanWrite) return;
            if (game is string) { if (prop.GetValue(vm, null) != null) prop.SetValue(vm, null, null); return; }  // card: default background
            if (!ReferenceEquals(prop.GetValue(vm, null), game)) prop.SetValue(vm, game, null);
        }

        private static string lastShoulder = "RightShoulder";

        // page 1's host ContentControl is only enabled while page 1 is the active hub section
        private static bool RowPageActive(ListBox row)
        {
            if (row == null || !row.IsVisible) return false;
            for (DependencyObject d = row; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var cc = d as ContentControl;
                if (cc != null && !(cc is ListBoxItem) && cc.ContentTemplate != null) return cc.IsEnabled && cc.Opacity > 0.5;
            }
            return row.IsEnabled;
        }

        // LB/RB back to page 1: row and game info glide in from the side we came from (LB = from the left).
        private static void PlayPageEnter(ListBox row)
        {
            // signing in rebuilds the row too: nothing slides then, the home is simply there as the login clears
            if (coveredForSignIn || profileRebuild) return;
            var host = VisualTreeHelper.GetParent(row) as UIElement;
            var inf = FindByName(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(row)), "SeleneInfoIntro");
            if (host != null) host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
            if (inf != null) inf.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
            int frames = 0;
            EventHandler onFrame = null;
            onFrame = (s, e) =>
            {
                if (++frames < 2) return;  // first frame after the (slow) rebuild has been presented
                CompositionTarget.Rendering -= onFrame;
                StartPageEnter(row);
            };
            CompositionTarget.Rendering += onFrame;
        }

        private static void StartPageEnter(ListBox row)
        {
            double from = lastShoulder == "LeftShoulder" ? -90 : 90;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var rowHost = VisualTreeHelper.GetParent(row) as UIElement;
            var info = FindByName(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(row)), "SeleneInfoIntro");
            var targets = new UIElement[] { rowHost, info };
            for (int i = 0; i < targets.Length; i++)
            {
                var el = targets[i];
                if (el == null) continue;
                double delay = i * 0.06;
                var shift = new TranslateTransform();
                if (el == info)
                {
                    var existing = el.RenderTransform as TranslateTransform;
                    if (existing != null && !existing.IsFrozen) shift = existing; else el.RenderTransform = shift;
                }
                else el.RenderTransform = shift;
                el.BeginAnimation(UIElement.OpacityProperty, Keys(0, 1, delay, 0.3, null));
                shift.BeginAnimation(TranslateTransform.XProperty, Keys(from, 0, delay, 0.38, ease));
            }
        }

        // ---- PS3/PSP-style trailer preview ----
        // Rest on a game for 2.5 s: the tile widens to 16:9 and plays ExtraMetadata\games\<id>\VideoTrailer.mp4 (muted)
        // for 30 s, then shrinks back. Any press closes it at once.
        private const double TileSize = 160, TileWide = 284;   // 16:9 at the tile height
        private const double ZoomNormal = 1.3, ZoomPreview = 1.5;
        private static System.Windows.Threading.DispatcherTimer previewArm;
        private static bool previewShowing;
        private static WeakReference previewRow, previewItem;
        private static readonly List<WeakReference> previewFaded = new List<WeakReference>();

        // Centering offset for the wide card lives on the ItemsPresenter: it is inside the row's scroll window, so the
        // window stays put and the tiles past the right edge slide in (moving the whole row left an empty strip).
        private static TranslateTransform PreviewShift(ListBox row)
        {
            var ip = FindChild<ItemsPresenter>(row);
            UIElement host = ip != null ? (UIElement)ip : row;
            var shift = host.RenderTransform as TranslateTransform;
            if (shift == null || shift.IsFrozen) { shift = new TranslateTransform(); host.RenderTransform = shift; }
            return shift;
        }

        private static void ArmPreview(ListBox row)
        {
            if (Light || launching) return;  // no trailer cards in a streamed session or performance mode, nor under a loading screen
            if (previewArm == null)
            {
                previewArm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                previewArm.Tick += (s, e) => { previewArm.Stop(); var r = previewArm.Tag as ListBox; if (r != null) ShowPreview(r); };
            }
            previewArm.Stop();
            previewArm.Tag = row;
            double after = Features.Number("trailerdelay");   // the Trailer delay setting (0: never)
            if (after <= 0) return;
            previewArm.Interval = TimeSpan.FromSeconds(after);
            if (row.SelectedItem != null && !(row.SelectedItem is string)) previewArm.Start();
        }

        private static string TrailerPath(object game)
        {
            var id = Prop(game, "Id");
            if (id == null || Api == null) return null;
            // silent 60 s clip made by make-preview-clips.py (a file without audio needs no muting)
            var path = System.IO.Path.Combine(SelenePaths.Media("PreviewClips"), id.ToString() + ".mp4");
            return System.IO.File.Exists(path) ? path : null;
        }

        // The selected tile itself grows to a bigger 16:9 card (both sides, neighbours move aside, a bit lower like
        // the normal zoom) and plays its trailer inside, muted, for 30 s. Any press closes it.
        private static void ShowPreview(ListBox row)
        {
            try
            {
                if (previewShowing || !row.IsVisible || !row.IsKeyboardFocusWithin || movingCard != null || movingGame != null || LoginScene.IsActive) return;
                var game = row.SelectedItem;
                var path = CardVideo(game);
                var item = row.ItemContainerGenerator.ContainerFromItem(game) as ListBoxItem;
                if (path == null || item == null) return;
                var tile = FindByName(item, "Tile");
                var video = TileVideo(item, true);
                var scale = item.Template.FindName("TileScale", item) as ScaleTransform;
                var panel = FindChild<VirtualizingStackPanel>(row);
                if (tile == null || video == null || scale == null || panel == null) return;

                previewShowing = true;
                UpdateMusicOverride();  // the game's song comes with the card
                previewRow = new WeakReference(row);
                previewItem = new WeakReference(item);
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var dur = TimeSpan.FromMilliseconds(480);
                double after = (TileWide + 14) * ZoomPreview;
                // grow to the right; slide left only as far as needed to keep the card inside the row, and fade out
                // tiles that slide past the left edge (centring the card cut the leftmost tile in half)
                double itemLeft = item.TranslatePoint(new Point(0, 0), row).X;
                double target = -Math.Max(0, itemLeft + after - (row.ActualWidth - 24));
                var shift = PreviewShift(row);
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, target, dur) { EasingFunction = ease });
                previewFaded.Clear();
                foreach (var child in panel.Children)
                {
                    var li = child as ListBoxItem;
                    if (li == null || li == item || li.TranslatePoint(new Point(0, 0), row).X + target > -1) continue;
                    li.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)));
                    previewFaded.Add(new WeakReference(li));
                }
                tile.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(TileSize, TileWide, dur) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(ZoomNormal, ZoomPreview, dur) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(ZoomNormal, ZoomPreview, dur) { EasingFunction = ease });

                video.SizeChanged -= OnTileVideoSize;
                video.SizeChanged += OnTileVideoSize;
                var tileGrid = VisualTreeHelper.GetParent(video) as FrameworkElement;
                if (tileGrid != null)
                {
                    SizeChangedEventHandler follow = null;
                    follow = (s, e) => { if (!previewShowing) { tileGrid.SizeChanged -= follow; return; } ClipVideoToTile(video); };
                    tileGrid.SizeChanged += follow;
                }
                ApplyVideoPause();
                video.IsMuted = !previewSound;   // a silent clip has nothing to mute; the trailer's sound waits for R3
                video.Source = new Uri(path);
                WatchTrailer(video, path);   // what Windows can't decode comes from the browser (#28)
                video.Opacity = 0;
                video.MediaOpened -= OnPreviewOpened;
                video.MediaOpened += OnPreviewOpened;
                video.MediaEnded -= OnPreviewEnded;
                video.MediaEnded += OnPreviewEnded;
                video.Play();
                ShowSoundBadge(item, true);

                var title = FindTitle(row);
                if (title != null) title.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
                // the title stays hidden while the 16:9 card is open; HidePreview puts it back under the tile

                Log("preview: " + game + " (" + Prop(game, "Id") + ") " + System.IO.Path.GetFileName(path));
                var songFor = Prop(game, "Id");
                if (songFor is Guid) SongSetup.NoteMissing((Guid)songFor);   // (no song yet while they're still coming: said once)
            }
            catch (Exception ex) { Log("preview failed: " + ex.Message); }
        }

        private static void OnPreviewOpened(object sender, RoutedEventArgs e)
        {
            var video = (MediaElement)sender;
            Log("preview: playing " + (video.Source != null ? System.IO.Path.GetFileName(video.Source.LocalPath) : "?") + " (" + (video.NaturalDuration.HasTimeSpan ? video.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.0") + " s, " : "") + video.NaturalVideoWidth + "x" + video.NaturalVideoHeight + ")");
            if (pendingSeek.HasValue) { var seek = pendingSeek.Value; pendingSeek = null; try { video.Position = seek; } catch { } video.Opacity = 1; return; }
            video.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromMilliseconds(200) });
        }

        // ---- trailer sound (R3) ----
        private static bool previewSound;          // stays on for the session once switched on

        // the settings that are a choice of values, read once (the rest are read where they're used)
        public static void ApplyValueSettings()
        {
            previewSound = Features.On("trailersound") && !Isolated;
            Ui.RefreshPrompts();   // Button names
            // the tab set may have changed (Selene's / Playnite's / both): rebuilt, shown, and the row refiltered
            try
            {
                var before = string.Join("|", RowFilters);
                RefreshRowFilters();
                if (before != string.Join("|", RowFilters))
                {
                    if (rowFilter >= RowFilters.Length || rowFilter < 0) rowFilter = 0;
                    var w = Application.Current.MainWindow;
                    if (w != null) RenderFilterTabs(w, 0);
                    var row = LiveRow();
                    if (row != null) ApplyRowFilter(row);
                }
            }
            catch (Exception ex) { Log("filter tabs: " + ex.Message); }
        }
        private static TimeSpan? pendingSeek;

        // the library's background trailer: the full trailer when there is one (it has sound for R3), else the silent clip
        public static bool TrailerFor(object game, out string path, out bool hasSound)
        {
            path = game is string ? null : FullTrailerPath(game);
            hasSound = path != null;
            if (path == null && !(game is string)) path = TrailerPath(game) ?? MicroTrailerPath(game);
            return path != null;
        }

        // the library trailer's own sound is on: the music add-on pauses for it
        private static bool libraryTrailerSound;
        public static void TrailerSound(bool on) { libraryTrailerSound = on; ApplyVideoPause(); }
        // a clip playing in Captures: the music waits (it played on under the clip's own sound)
        public static void ClipPlaying(bool on) { clipPlaying = on; ApplyVideoPause(); }
        private static bool clipPlaying;
        public static bool ClipIsPlaying { get { return clipPlaying; } }

        private static string FullTrailerPath(object game) { return ExtraMetadataVideo(game, "VideoTrailer.mp4"); }
        // Extra Metadata's short looping clip (what it downloads by default): silent, shown when there is no full trailer or preview clip (#28)
        private static string MicroTrailerPath(object game) { return ExtraMetadataVideo(game, "VideoMicrotrailer.mp4"); }

        private static string ExtraMetadataVideo(object game, string file)
        {
            var id = Prop(game, "Id");
            if (id == null || Api == null) return null;
            var path = System.IO.Path.Combine(Api.Paths.ConfigurationPath, "ExtraMetadata", "games", id.ToString(), file);
            return System.IO.File.Exists(path) ? path : null;
        }

        private static void ShowSoundBadge(ListBoxItem item, bool show)
        {
            var badge = item != null ? FindByName(item, "SoundBadge") : null;
            var glyph = item != null ? FindByName(item, "SoundGlyph") as TextBlock : null;
            if (badge == null) return;
            badge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (glyph != null) glyph.Text = previewSound ? "\uE767" : "\uE74F";  // volume / mute
        }

        private static void TogglePreviewSound()
        {
            var item = previewItem != null ? previewItem.Target as ListBoxItem : null;
            var video = item != null ? TileVideo(item, false) : null;
            if (video == null) return;
            var game = item.DataContext;
            var full = FullTrailerPath(game); var clip = TrailerPath(game);
            if (full == null) return;
            previewSound = !previewSound;   // (a test copy stays silent through its muted sound session)
            video.IsMuted = !previewSound;
            if (CardVideo(game) == video.Source.LocalPath || clip == null)
            {
                // the trailer stays (it was playing already): only its sound changes
                ApplyVideoPause();
                ShowSoundBadge(item, true);
                Log("trailer sound " + (previewSound ? "on" : "off"));
                return;
            }
            var pos = video.Position;
            // the silent clip starts 4 s into the trailer
            pendingSeek = previewSound ? pos + TimeSpan.FromSeconds(4) : (pos > TimeSpan.FromSeconds(4) ? pos - TimeSpan.FromSeconds(4) : TimeSpan.Zero);
            ApplyVideoPause();
            video.Source = new Uri(previewSound ? full : clip);
            WatchTrailer(video, previewSound ? full : clip);
            video.Play();
            ShowSoundBadge(item, true);
            Log("trailer sound " + (previewSound ? "on" : "off"));
        }

        private static TrailerFix previewFix;
        private static void WatchTrailer(MediaElement video, string path)
        {
            if (previewFix != null) previewFix.Stop();
            previewFix = TrailerFix.Watch(video, path, null);
        }

        private static void OnPreviewEnded(object sender, RoutedEventArgs e)
        {
            var v = (MediaElement)sender;
            v.Position = TimeSpan.Zero;  // loop while the card stays selected
            v.Play();
        }

        // What the music add-on (UniPlaySong) plays, by where the user is:
        //   "login"   behind the login screens: its default track
        //   "home"    the home row: its default track too (like the legacy theme), never the selected game's song
        //   "trailer" a game's trailer card is open: that game's song
        //   "normal"  UniPlaySong as usual
        private static string musicMode;

        public static void SetMusic(string mode)
        {
            bool changed = musicMode != mode;
            musicMode = mode;
            // With background music off, UniPlaySong owns the selected game's song. Opening a muted trailer
            // must not remove/reapply pauses or rewrite its override: either can restart the current playback.
            if (!Features.On("music"))
            {
                ApplyVideoPause();
                if (changed) LogUpsStateLater();
                return;
            }
            SetUpsSetting("ForceDefaultMusicOverride", WantDefault);
            ApplyVideoPause();
            if (musicHeldForLaunch) SetUpsVideoPlaying(true);
            if (changed) LogUpsStateLater();
            if (changed || (WantDefault && !musicHeldForLaunch)) EmptyLibraryMusic();   // retry after a startup hold, even when the mode itself did not change
            // UniPlaySong can switch to a game's song on its own when Playnite's selection changes: like the legacy theme,
            // the wanted state is checked 4x a second and put back
            if (musicGuard == null)
            {
                musicGuard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                musicGuard.Tick += (s, e) =>
                {
                    var live = LiveUps();
                    var prop = live != null ? live.GetType().GetProperty("ForceDefaultMusicOverride") : null;
                    if (prop == null || !Features.On("music")) return;
                    if (!Equals(prop.GetValue(live, null), WantDefault)) { prop.SetValue(live, WantDefault, null); Log("UPS ForceDefaultMusicOverride put back to " + WantDefault); }
                    if ((DateTime.Now - emptyMusicAt).TotalSeconds > 3) EmptyLibraryMusic();
                };
                musicGuard.Start();
            }
        }

        private static System.Windows.Threading.DispatcherTimer musicGuard;

        // what UniPlaySong is really doing a moment after a switch (playing? paused, and by what? which song, how loud?): the
        // music went silent on a PC with everything else looking right
        private static System.Windows.Threading.DispatcherTimer upsStateTimer;
        private static void LogUpsStateLater()
        {
            if (upsStateTimer == null)
            {
                upsStateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                upsStateTimer.Tick += (s, e) =>
                {
                    upsStateTimer.Stop();
                    try
                    {
                        var plugin = Api != null ? Api.Addons.Plugins.FirstOrDefault(pl => pl.Id == UniPlaySongId) : null;
                        var field = plugin != null ? plugin.GetType().GetField("_playbackService", BindingFlags.Instance | BindingFlags.NonPublic) : null;
                        var svc = field != null ? field.GetValue(plugin) : null;
                        if (svc == null) return;
                        Func<string, object> get = n => { var p = svc.GetType().GetProperty(n); return p != null ? p.GetValue(svc, null) : null; };
                        var sources = svc.GetType().GetField("_activePauseSources", BindingFlags.Instance | BindingFlags.NonPublic);
                        var set = sources != null ? sources.GetValue(svc) as System.Collections.IEnumerable : null;
                        var paused = set != null ? string.Join(",", set.Cast<object>().Select(o => o.ToString()).ToArray()) : "?";
                        var song = get("CurrentSongPath") as string;
                        var vol = svc.GetType().GetMethod("GetVolume");
                        Log("UPS state (" + musicMode + "): playing=" + get("IsPlaying") + " paused by [" + paused + "] default=" + get("IsPlayingDefaultMusic")
                            + " song=" + (string.IsNullOrEmpty(song) ? "none" : System.IO.Path.GetFileName(song)) + " volume=" + (vol != null ? vol.Invoke(svc, null) : "?"));
                    }
                    catch (Exception ex) { Log("UPS state: " + ex.Message); }
                };
            }
            upsStateTimer.Stop();
            upsStateTimer.Start();
        }

        // No games yet: the add-on starts its default track only when a game gets selected, so with nothing to select the
        // home and the login stayed silent. It's asked for the default track with a stand-in game (one with no songs).
        private static DateTime emptyMusicAt = DateTime.MinValue;
        private static Playnite.SDK.Models.Game musicStandIn;
        private static DateTime idleMusicLoggedAt = DateTime.MinValue;
        private static void EmptyLibraryMusic()
        {
            emptyMusicAt = DateTime.Now;
            try
            {
                if (Api == null || !WantDefault || !Features.On("music") || musicHeldForLaunch || videoPause == true) return;
                if (quietUntilSelection || (quietWatch != null && quietWatch.IsEnabled)) return;   // (a hold of Selene's own is on)
                var settings = LiveUps();
                var plugin = Api.Addons.Plugins.FirstOrDefault(pl => pl.Id == UniPlaySongId);
                if (settings == null || plugin == null) return;
                var field = plugin.GetType().GetField("_playbackService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var service = field != null ? field.GetValue(plugin) : null;
                if (service == null) return;
                var playing = service.GetType().GetProperty("IsPlayingDefaultMusic");
                if (playing != null && Equals(playing.GetValue(service, null), true)) return;
                // with games too: a start-up hold left it with nothing loaded (its first song was cut short), and it only tried again
                // when the selection moved, so the login and the home stayed silent. Only when it is idle: nothing playing, nothing
                // pausing it (a game's song, a video, a pause of its own are left alone)
                var isPlaying = service.GetType().GetProperty("IsPlaying");
                if (isPlaying != null && Equals(isPlaying.GetValue(service, null), true)) return;
                var sources = service.GetType().GetField("_activePauseSources", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var paused = sources != null ? sources.GetValue(service) as System.Collections.IEnumerable : null;
                if (paused != null && paused.Cast<object>().Any()) return;
                var interim = service.GetType().GetMethod("PlayDefaultMusicInterim");
                if (interim == null) return;
                if (musicStandIn == null) musicStandIn = new Playnite.SDK.Models.Game("Selene") { IsInstalled = true };
                interim.Invoke(service, new object[] { musicStandIn, settings });
                if ((DateTime.Now - idleMusicLoggedAt).TotalSeconds > 60) { idleMusicLoggedAt = DateTime.Now; Log("UPS: idle, default track asked for"); }
            }
            catch (Exception ex) { Log("UPS empty-library music: " + ex.Message); }
        }
        private static bool WantDefault { get { return musicMode == "login" || musicMode == "home"; } }

        // UniPlaySong pauses while its "video is playing" flag is up: under a trailer with sound
        private static bool? videoPause;

        // from Play until the game shows: no default music over the loading screen (UniPlaySong pauses for the running game after)
        private static bool musicHeldForLaunch;

        // start-up stays quiet but for Selene's chime: UniPlaySong starts music on its own as Playnite opens, past the
        // VideoIsPlaying hold, so its player is turned down, gets a pause source of its own, and is paused again if it starts
        private static object upsPlayer;
        private static System.Windows.Threading.DispatcherTimer quietWatch;
        private static bool pausedManually;
        private static bool hushedEarly;
        public static bool HushedAtStart { get { return hushedEarly; } }

        private static object FindUpsPlayer(Playnite.SDK.IPlayniteAPI api)
        {
            var plugin = api != null ? api.Addons.Plugins.FirstOrDefault(pl => pl.Id == UniPlaySongId) : null;
            for (var t = plugin != null ? plugin.GetType() : null; t != null; t = t.BaseType)
            {
                var f = t.GetField("_playbackService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (f != null) return f.GetValue(plugin);
            }
            return null;
        }

        // UniPlaySong starts the song of the game selected when Playnite last closed as soon as Playnite selects it, before the
        // login's hold: Selene is loaded first, so its handler for that first selection (or for the start, whichever comes first)
        // holds the music already. The login's hold takes over and lets it go; one that never comes lets it go after 20 s.
        public static void HushAtStart(Playnite.SDK.IPlayniteAPI api)
        {
            if (hushedEarly) return;
            hushedEarly = true;
            if (!Features.On("music")) return;
            try
            {
                if (upsPlayer == null) upsPlayer = FindUpsPlayer(api);
                if (upsPlayer == null) return;
                // before it starts: the default track only (none at start-up) and "a video is playing", as it was left before
                // 1.3.1 put its settings back on exit. A pause or no volume alone didn't stop its fade-in of the game's song.
                var plugin = api.Addons.Plugins.FirstOrDefault(pl => pl.Id == UniPlaySongId);
                var live = plugin != null ? FindSettingsWith(plugin, "ForceDefaultMusicOverride", 3) : null;
                if (live != null)
                    foreach (var name in new[] { "ForceDefaultMusicOverride", "VideoIsPlaying" })
                    {
                        var prop = live.GetType().GetProperty(name);
                        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool)) prop.SetValue(live, true, null);
                    }
                Log("start-up: early hold at the first selection" + (live == null ? " (settings not found)" : ""));
                QuietStartup(true);
                var fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                fallback.Tick += (s, e) => { fallback.Stop(); if (hushedEarly && !LoginScene.StartingUp && quietWatch != null && quietWatch.IsEnabled) { Log("start-up: early hold not taken over"); QuietStartup(false); videoPause = null; ApplyVideoPause(); if (musicMode != null) SetMusic(musicMode); else SetUpsSetting("ForceDefaultMusicOverride", false); } };
                fallback.Start();
            }
            catch (Exception ex) { Log("start-up early hold: " + ex.Message); }
        }

        public static void QuietStartup(bool on)
        {
            try
            {
                if (upsPlayer == null) upsPlayer = FindUpsPlayer(Api);
                if (upsPlayer == null) return;
                var type = upsPlayer.GetType();
                var add = type.GetMethod("AddPauseSourceImmediate");
                var remove = type.GetMethod("RemovePauseSource");
                if (add == null || remove == null) return;
                var source = Enum.Parse(add.GetParameters()[0].ParameterType, "Settings");
                var volume = type.GetMethod("SetVolumeMultiplier");
                if (on)
                {
                    // silent too: a game's own song (the one selected when Playnite last closed) starts past any pause
                    if (volume != null) volume.Invoke(upsPlayer, new object[] { 0.0 });
                    add.Invoke(upsPlayer, new[] { source });
                    if (quietWatch == null)
                    {
                        quietWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                        quietWatch.Tick += (s, e) =>
                        {
                            try
                            {
                                var paused = type.GetProperty("IsPaused");
                                var loaded = type.GetProperty("IsLoaded");
                                if (paused != null && loaded != null && (bool)loaded.GetValue(upsPlayer, null) && !(bool)paused.GetValue(upsPlayer, null))
                                {
                                    type.GetMethod("PauseImmediate").Invoke(upsPlayer, null);
                                    pausedManually = true;   // (that pause is UniPlaySong's "Manual" one: lifted with the hold)
                                    Log("start-up: UniPlaySong started its music, paused it");
                                }
                            }
                            catch (Exception ex) { quietWatch.Stop(); Log("start-up quiet: " + ex.Message); }
                        };
                    }
                    quietWatch.Start();
                    Log("start-up: music held (UniPlaySong pause source)");
                }
                else
                {
                    if (quietWatch != null) quietWatch.Stop();
                    remove.Invoke(upsPlayer, new[] { source });
                    // the pause put on it while held is UniPlaySong's "Manual" one: left on, the music stayed silent the whole
                    // session (the home's and every game's song)
                    if (pausedManually) { remove.Invoke(upsPlayer, new[] { Enum.Parse(add.GetParameters()[0].ParameterType, "Manual") }); pausedManually = false; Log("start-up: the hold's pause lifted"); }
                    if (volume != null) volume.Invoke(upsPlayer, new object[] { 1.0 });
                    Log("start-up: music released");
                }
            }
            catch (Exception ex) { Log("start-up quiet: " + ex.Message); }
        }

        public static void HoldMusicForLaunch(bool on)
        {
            if (musicHeldForLaunch == on) return;
            musicHeldForLaunch = on;
            if (on)
            {
                CancelMusicOffFade();
                FadeLaunchMusic(0, 350, ApplyVideoPause);
            }
            else
            {
                ApplyVideoPause();
                FadeLaunchMusic(1, 250, null);
            }
            if (!on) SetMusic(musicMode ?? "home");   // startup can set the mode while held; reapply it when the hold lifts
        }

        private static System.Windows.Threading.DispatcherTimer launchMusicFade;
        private static double launchMusicLevel = 1;
        private static void FadeLaunchMusic(double target, int milliseconds, Action done)
        {
            if (launchMusicFade != null) { launchMusicFade.Stop(); launchMusicFade = null; }
            try
            {
                if (upsPlayer == null) upsPlayer = FindUpsPlayer(Api);
                var player = upsPlayer;
                var volume = player != null ? player.GetType().GetMethod("SetVolumeMultiplier") : null;
                if (volume == null) { if (done != null) done(); return; }
                var from = launchMusicLevel;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                launchMusicFade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                launchMusicFade.Tick += (s, e) =>
                {
                    try
                    {
                        double progress = Math.Min(1, elapsed.Elapsed.TotalMilliseconds / milliseconds);
                        launchMusicLevel = from + (target - from) * progress;
                        volume.Invoke(player, new object[] { launchMusicLevel });
                        if (progress < 1) return;
                    }
                    catch (Exception ex) { Log("loading music fade: " + ex.Message); }
                    launchMusicFade.Stop(); launchMusicFade = null;
                    if (done != null) done();
                };
                launchMusicFade.Start();
            }
            catch (Exception ex) { Log("loading music fade: " + ex.Message); if (done != null) done(); }
        }

        private static void ApplyVideoPause()
        {
            if (musicHeldForLaunch && launchMusicFade != null) return;   // fade the current song before pausing it
            bool want = (previewShowing && previewSound) || musicHeldForLaunch || libraryTrailerSound || clipPlaying;
            if (videoPause == want) return;
            videoPause = want;
            SetUpsVideoPlaying(want);
        }

        // UniPlaySong swapped its settings object (a save does): looked up again on the next use
        internal static void ForgetUps() { upsSettings = null; }

        // UniPlaySong's live settings, the object its player reads: found inside the add-on like the legacy theme did
        // (asking Playnite for the add-on's settings can hand back a copy, so changes never reached the player)
        private static object LiveUps()
        {
            if (upsSettings != null || Api == null) return upsSettings;
            foreach (var plugin in Api.Addons.Plugins)
            {
                if (plugin.Id != UniPlaySongId) continue;
                upsSettings = FindSettingsWith(plugin, "ForceDefaultMusicOverride", 3);
                Log("UPS settings " + (upsSettings != null ? "found: " + upsSettings.GetType().FullName : "not found"));
            }
            return upsSettings;
        }

        // Turning background music off fades the current track; the next game selection releases that hold.
        // UniPlaySong then plays the game's song continuously, including when its muted trailer opens.
        private static bool quietUntilSelection;
        private static System.Windows.Threading.DispatcherTimer musicOffFade;
        private static void CancelMusicOffFade()
        {
            if (musicOffFade == null) return;
            musicOffFade.Stop(); musicOffFade = null;
            try
            {
                var volume = upsPlayer != null ? upsPlayer.GetType().GetMethod("SetVolumeMultiplier") : null;
                if (volume != null) volume.Invoke(upsPlayer, new object[] { 1.0 });
            }
            catch (Exception ex) { Log("music fade reset: " + ex.Message); }
        }
        public static void ReleaseMusicQuietly()
        {
            try
            {
                if (upsPlayer == null) upsPlayer = FindUpsPlayer(Api);
                var volume = upsPlayer != null ? upsPlayer.GetType().GetMethod("SetVolumeMultiplier") : null;
                if (volume != null && !previewShowing && !(quietWatch != null && quietWatch.IsEnabled))
                {
                    if (musicOffFade != null) return;
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    musicOffFade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    musicOffFade.Tick += (s, e) =>
                    {
                        try
                        {
                            if (Features.On("music")) { CancelMusicOffFade(); return; }
                            var progress = Math.Min(1, started.Elapsed.TotalMilliseconds / 450);
                            volume.Invoke(upsPlayer, new object[] { 1 - progress });
                            if (progress < 1) return;
                        }
                        catch (Exception ex) { Log("music fade: " + ex.Message); }
                        // Pause before restoring the multiplier, so the next song keeps its normal volume.
                        musicOffFade.Stop(); musicOffFade = null;
                        SongsOnlyWithTrailer(false);
                        ReleaseMusic();
                        try { volume.Invoke(upsPlayer, new object[] { 1.0 }); } catch { }
                    };
                    musicOffFade.Start();
                    return;
                }
            }
            catch (Exception ex) { Log("music fade: " + ex.Message); }
            SongsOnlyWithTrailer(previewShowing);
            ReleaseMusic();
        }

        private static void SongsOnlyWithTrailer(bool trailer)
        {
            if (musicOffFade != null)
            {
                if (!trailer) return;
                CancelMusicOffFade();
            }
            try
            {
                if (quietWatch != null && quietWatch.IsEnabled) return;   // (the start-up hold is on: it lets go by itself)
                if (upsPlayer == null) upsPlayer = FindUpsPlayer(Api);
                var type = upsPlayer != null ? upsPlayer.GetType() : null;
                if (type == null) return;
                if (trailer == !quietUntilSelection) return;   // already so
                var add = type.GetMethod("AddPauseSourceImmediate");
                var remove = type.GetMethod("RemovePauseSource");
                if (add == null || remove == null) return;
                var source = Enum.Parse(add.GetParameters()[0].ParameterType, "Settings");
                if (trailer) remove.Invoke(upsPlayer, new[] { source }); else add.Invoke(upsPlayer, new[] { source });
                quietUntilSelection = !trailer;
                Log("music off: " + (trailer ? "selected game's song released" : "quiet until another game is selected"));
            }
            catch (Exception ex) { Log("music off: " + ex.Message); }
        }

        // the music back on (or the add-on gone from the picture): the quiet from switching it off ends
        public static void LetMusicGo() { SongsOnlyWithTrailer(true); }

        // music control switched off: every pause Selene set on the music add-on is let go now
        public static void ReleaseMusic()
        {
            if (launchMusicFade != null) { launchMusicFade.Stop(); launchMusicFade = null; }
            if (launchMusicLevel != 1)
            {
                try { var volume = upsPlayer != null ? upsPlayer.GetType().GetMethod("SetVolumeMultiplier") : null; if (volume != null) volume.Invoke(upsPlayer, new object[] { 1.0 }); }
                catch (Exception ex) { Log("loading music reset: " + ex.Message); }
                launchMusicLevel = 1;
            }
            CancelMusicOffFade();
            if (musicGuard != null) { musicGuard.Stop(); musicGuard = null; }
            musicMode = null;
            videoPause = null;
            SetUpsSetting("ForceDefaultMusicOverride", false);
            SetUpsSetting("ThemeOverlayActive", false);
            SetUpsVideoPlaying(false);
        }

        // UniPlaySong keeps its settings in its own file, and what Selene put in them (play the default music only, a theme is
        // showing) was saved along with them when Playnite closed. In any other theme the add-on then played nothing (it was
        // asked for a default track that isn't there) or paused itself. Put back to normal when Playnite closes, and again at the
        // next start whatever theme is in use, for the ones already left that way.
        public static void CleanMusicLeftovers(Playnite.SDK.IPlayniteAPI api)
        {
            try
            {
                foreach (var plugin in api.Addons.Plugins)
                {
                    if (plugin.Id != UniPlaySongId) continue;
                    var settings = FindSettingsWith(plugin, "ForceDefaultMusicOverride", 3);
                    if (settings == null) return;
                    var fixedNames = new List<string>();
                    foreach (var name in new[] { "ForceDefaultMusicOverride", "ThemeOverlayActive", "VideoIsPlaying" })
                    {
                        var prop = settings.GetType().GetProperty(name);
                        if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(bool) || !(bool)prop.GetValue(settings, null)) continue;
                        prop.SetValue(settings, false, null);
                        fixedNames.Add(name);
                    }
                    if (fixedNames.Count > 0) Log("UPS: put back " + string.Join(", ", fixedNames.ToArray()));
                }
            }
            catch (Exception ex) { Log("UPS clean: " + ex.Message); }
        }

        private static void SetUpsSetting(string name, bool value)
        {
            try
            {
                if (Api == null) return;
                var settings = LiveUps();
                if (settings == null) return;
                if (!Features.On("music")) value = false;   // music control off: every pause Selene set is let go
                var prop = settings.GetType().GetProperty(name);
                if (prop != null && prop.CanWrite) prop.SetValue(settings, value, null);
                Log("UPS " + name + "=" + value + (prop == null ? " (not found)" : ""));
            }
            catch (Exception ex) { Log("UPS " + name + ": " + ex.Message); }
        }

        private static void SetUpsVideoPlaying(bool playing)
        {
            try
            {
                if (Api == null) return;
                var settings = LiveUps();
                if (settings == null) return;
                var prop = settings.GetType().GetProperty("VideoIsPlaying");
                // Loading screens, audible trailers and captures pause game songs even with background music off.
                if (prop != null && prop.CanWrite) prop.SetValue(settings, playing, null);
                Log("UPS VideoIsPlaying=" + playing + (prop == null ? " (property not found on " + settings.GetType().Name + ")" : ""));
            }
            catch (Exception ex) { Log("UPS pause: " + ex.Message); }
        }

        // The video is laid out at its full 16:9 width even while the card is still narrower (it spilled past the
        // card's sides, uneven gap to the ring). Clip it to the card's own rounded rectangle, centred.
        private static void OnTileVideoSize(object sender, SizeChangedEventArgs e) { ClipVideoToTile((FrameworkElement)sender); }

        // The trailer player only exists on the tile being previewed: a MediaElement in every tile made each row rebuild slow
        // (and left an audio session per tile behind).
        private static MediaElement TileVideo(ListBoxItem item, bool create)
        {
            var existing = FindByName(item, "TileVideo") as MediaElement;
            if (existing != null || !create) return existing;
            var host = FindByName(item, "TileVideoHost") as Panel;
            if (host == null) return null;
            var video = new MediaElement
            {
                Name = "TileVideo", LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Volume = 1,
                Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false
            };
            // a file Windows has no decoder for (HEVC, AV1, VP9, Opus sound...) plays nothing: said in the log at least (#28)
            video.MediaFailed += (s, e) => Log("preview: can't play " + ((MediaElement)s).Source + ": " + (e.ErrorException != null ? e.ErrorException.Message : "unknown error") + " (no Windows decoder for it? H.264 video with AAC sound always plays)");
            host.Children.Add(video);
            return video;
        }

        private static void ClipVideoToTile(FrameworkElement v)
        {
            var tile = VisualTreeHelper.GetParent(v) as FrameworkElement;  // the tile's Grid
            double tw = tile != null && tile.ActualWidth > 0 ? tile.ActualWidth : v.ActualWidth;
            double th = tile != null && tile.ActualHeight > 0 ? tile.ActualHeight : v.ActualHeight;
            if (Math.Abs(v.Width - tw) > 0.5 || double.IsNaN(v.Width)) v.Width = tw;
            if (Math.Abs(v.Height - th) > 0.5 || double.IsNaN(v.Height)) v.Height = th;
            v.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(1, tw), Math.Max(1, th)), 12, 12);
        }

        // Closing because the user moved: the tile's layout snaps back at once (so navigation measures real sizes),
        // but it is drawn shrinking from the 16:9 card to the square tile around its own centre, and the row glides
        // from where it was instead of jumping.
        // a menu opened over the home: the trailer closes and doesn't start underneath it
        public static void PauseForOverlay()
        {
            if (previewArm != null) previewArm.Stop();
            if (previewShowing) SmoothClosePreview();
        }

        private static void SmoothClosePreview()
        {
            var row = previewRow != null ? previewRow.Target as ListBox : null;
            var item = previewItem != null ? previewItem.Target as ListBoxItem : null;
            var tile = item != null ? FindByName(item, "Tile") : null;
            if (row == null || item == null || tile == null) { HidePreview(false); return; }
            var root = Window.GetWindow(row) as UIElement;
            Point preL = tile.TranslatePoint(new Point(0, 0), root), preR = tile.TranslatePoint(new Point(tile.ActualWidth, tile.ActualHeight), root);
            HidePreview(false);  // layout + row offset back to normal, preview animations released
            row.UpdateLayout();
            Point postL = tile.TranslatePoint(new Point(0, 0), root), postR = tile.TranslatePoint(new Point(tile.ActualWidth, tile.ActualHeight), root);
            double preW = preR.X - preL.X, postW = Math.Max(1, postR.X - postL.X);
            double preH = preR.Y - preL.Y, postH = Math.Max(1, postR.Y - postL.Y);
            double preC = (preL.X + preR.X) / 2, postC = (postL.X + postR.X) / 2;
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(230);

            // row: start where the card's centre was, glide to the new layout
            var shift = PreviewShift(row);
            var back = new DoubleAnimation(preC - postC, 0, dur) { EasingFunction = ease };
            back.Completed += (s, e) => shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.BeginAnimation(TranslateTransform.XProperty, back);

            // the old card: drawn from its wide size down to the tile
            var shrink = new ScaleTransform(preW / postW, preH / postH);
            tile.RenderTransformOrigin = new Point(0.5, 0);
            tile.RenderTransform = shrink;
            var sx = new DoubleAnimation(preW / postW, 1, dur) { EasingFunction = ease };
            sx.Completed += (s, e) => { tile.RenderTransform = Transform.Identity; };
            shrink.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            shrink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(preH / postH, 1, dur) { EasingFunction = ease });
        }

        private static void HidePreview() { HidePreview(false); }
        private static bool HidePreview(bool animate, int ms) { bool was = previewShowing; previewMs = ms; HidePreview(animate); previewMs = 380; return was; }
        private static int previewMs = 380;

        // animate=true when the 30 s are up; false on a press (snap back instantly so navigation measures true sizes)
        private static void HidePreview(bool animate)
        {
            if (previewArm != null) previewArm.Stop();
            if (!previewShowing) return;
            previewShowing = false;
            UpdateMusicOverride();  // back to the default music at once
            var row = previewRow != null ? previewRow.Target as ListBox : null;
            var item = previewItem != null ? previewItem.Target as ListBoxItem : null;
            if (row == null || item == null) return;
            var tile = FindByName(item, "Tile");
            var video = TileVideo(item, false);
            var scale = item.Template.FindName("TileScale", item) as ScaleTransform;
            var panel = FindChild<VirtualizingStackPanel>(row);
            if (video != null)
            {
                video.BeginAnimation(UIElement.OpacityProperty, null); video.Opacity = 0; video.Stop(); video.Source = null; video.ClearValue(FrameworkElement.WidthProperty); video.ClearValue(FrameworkElement.HeightProperty);
                video.SizeChanged -= OnTileVideoSize;
                var vh = video.Parent as Panel;
                if (vh != null) vh.Children.Remove(video);  // frees the player (one per tile used to cost a lot on every row rebuild)
            }
            ShowSoundBadge(item, false);
            if (previewFix != null) { previewFix.Stop(); previewFix = null; }
            ApplyVideoPause();
            foreach (var w in previewFaded)
            {
                var li = w.Target as ListBoxItem;
                if (li == null) continue;
                var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(230));
                fadeIn.Completed += (s, e) => li.BeginAnimation(UIElement.OpacityProperty, null);
                li.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
            previewFaded.Clear();

            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var dur = TimeSpan.FromMilliseconds(animate ? previewMs : 0);
            double zoomBack = item.IsSelected ? ZoomNormal : 1;
            if (tile != null)
            {
                if (animate)
                {
                    var aw = new DoubleAnimation(TileWide, TileSize, dur) { EasingFunction = ease };
                    aw.Completed += (s, e) => tile.BeginAnimation(FrameworkElement.WidthProperty, null);
                    tile.BeginAnimation(FrameworkElement.WidthProperty, aw);
                }
                else tile.BeginAnimation(FrameworkElement.WidthProperty, null);
            }
            if (scale != null)
            {
                if (animate)
                {
                    var ax = new DoubleAnimation(zoomBack, dur) { EasingFunction = ease };
                    ax.Completed += (s, e) => { scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(zoomBack, dur) { EasingFunction = ease });
                }
                else
                {
                    // hand control back to the tile's own selection zoom storyboard
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                }
            }
            var shift = PreviewShift(row);
            if (shift != null) shift.BeginAnimation(TranslateTransform.XProperty, animate ? new DoubleAnimation(0, dur) { EasingFunction = ease } : null);
            if (!animate) row.UpdateLayout();
            var title = FindTitle(row);
            if (title != null)
            {
                if (animate)
                {
                    var settle = new System.Windows.Threading.DispatcherTimer { Interval = dur + TimeSpan.FromMilliseconds(40) };
                    settle.Tick += (s, e) => { settle.Stop(); PlaceTitle(row, item, 0); };
                    settle.Start();
                }
                else PlaceTitle(row, item, 0);
            }
        }

        private static void RestoreSelection(ListBox row)
        {
            ApplyCardOrder(row);
            HookNewsSlide(row);  // LB/RB rebuilt the page: its news row is new too
            int idx = row.Items.IndexOf(lastSelected);
            if (idx < 0) idx = FirstGameIndex(row);
            row.SelectedIndex = idx;
            var sv = FindChild<ScrollViewer>(row);
            if (sv != null) sv.ScrollToHorizontalOffset(Math.Max(0, idx - AnchorSlot));
            row.UpdateLayout();
            var c = row.ItemContainerGenerator.ContainerFromIndex(idx) as UIElement;
            if (c != null) c.Focus();
            ScheduleTitle(row, 400);
            PlayPageEnter(row);
            ArmPreview(row);
            Log("page switch: restored " + lastSelected);
        }

        private static void OnRowGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var row = (ListBox)sender;
            ListBoxItem item = null;
            for (var d = e.NewFocus as DependencyObject; d != null && d != row; d = VisualTreeHelper.GetParent(d))
                if (d is ListBoxItem) { item = (ListBoxItem)d; break; }
            if (item == null || item.IsSelected) return;
            row.Dispatcher.BeginInvoke(new Action(() => { if (item.IsKeyboardFocusWithin && !item.IsSelected) item.IsSelected = true; }),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private static int wantedFocusIndex = -1;   // a tile Selene itself is focusing from outside the row (Find a game's pick)

        private static void OnRowPreviewGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var row = (ListBox)sender;
            var oldFocus = e.OldFocus as DependencyObject;
            if (oldFocus != null && IsInRow(oldFocus)) return;  // normal movement inside the row
            int first = FirstGameIndex(row);
            int target = wantedFocusIndex >= 0 ? wantedFocusIndex : row.SelectedIndex >= 0 ? row.SelectedIndex : first;
            var c = row.ItemContainerGenerator.ContainerFromIndex(target) as UIElement;
            if (c == null || ReferenceEquals(e.NewFocus, c)) return;
            e.Handled = true;  // cancel focus landing on a card / the list itself
            row.Dispatcher.BeginInvoke(new Action(() =>
            {
                var sv = FindChild<ScrollViewer>(row);
                if (sv != null) sv.ScrollToHorizontalOffset(Math.Max(0, target - AnchorSlot));
                row.UpdateLayout();
                var c2 = row.ItemContainerGenerator.ContainerFromIndex(target) as UIElement;
                if (c2 != null) c2.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private static void QueueIntro(ListBox row)
        {
            row.Dispatcher.BeginInvoke(new Action(() =>
            {
                var window = Window.GetWindow(row);
                var login = window != null ? FindByName(window, "AcceuilSettings") : null;
                Log("intro queued, login visible=" + (login != null && login.IsVisible));
                if (login != null && login.IsVisible) return;  // still behind the login screen
                if (introDone) { var held = FindByName(window, "SeleneRowIntro") as UIElement; if (held != null) held.BeginAnimation(UIElement.OpacityProperty, null); PlayIntro(row); return; }
                // first intro after start: tile art is still decoding; wait for an idle moment so the fly-in is smooth
                var holder = FindByName(window, "SeleneRowIntro") as UIElement;
                if (holder != null) holder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
                var infoHold = FindByName(window, "SeleneInfoIntro") as UIElement;
                var bgHold = FindByName(window, "SeleneHubGameBackground") as UIElement;
                if (infoHold != null) infoHold.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
                if (bgHold != null && !coveredForSignIn) bgHold.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
                try
                {
                    RebuildGames(row);
                    int first0 = startOnCards ? StartCardIndex(row) : FirstGameIndex(row);
                    if (first0 >= 0) { row.SelectedIndex = first0; PushSelected(row); }
                }
                catch { }
                row.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                    wait.Tick += (s, e) =>
                    {
                        wait.Stop();
                        if (holder != null) holder.BeginAnimation(UIElement.OpacityProperty, null);
                        PlayIntro(row);
                    };
                    wait.Start();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static DateTime lastIntro;
        // the cards' fly-in is still going: moves wait for it (a move mid-flight left the row half-landed, a gap in it)
        private static DateTime introUntil = DateTime.MinValue;
        internal static bool IntroRunning { get { return DateTime.Now < introUntil; } }

        // Playnite plays its move sound whenever anything gets keyboard focus, so focus Selene moves itself (signing in,
        // the home's intro, Play) sounded like moving. For a moment its move sound is emptied (Playnite skips an empty
        // one), then put back; the user's own moves outside that moment tick as usual.
        private static PropertyInfo navigateSound;
        private static IntPtr savedNavigateSound;
        private static DateTime quietUntil;
        private static System.Windows.Threading.DispatcherTimer quietTimer;

        public static void QuietFocus(int ms)
        {
            try
            {
                if (navigateSound == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("Playnite.FullscreenApp.FullscreenApplication", false);
                        if (t == null) continue;
                        navigateSound = t.GetProperty("NavigateSound", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        break;
                    }
                    if (navigateSound == null) { Log("quiet focus: Playnite's move sound not found"); return; }
                }
                var until = DateTime.Now.AddMilliseconds(ms);
                if (until > quietUntil) quietUntil = until;
                var current = (IntPtr)navigateSound.GetValue(null, null);
                if (current != IntPtr.Zero) { savedNavigateSound = current; navigateSound.SetValue(null, IntPtr.Zero, null); }
                if (quietTimer == null)
                {
                    // at startup Playnite loads its move sound while Selene is already quiet: checked often (low priority, so
                    // drawing comes first) until it has been caught, then only now and then
                    quietTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
                    quietTimer.Tick += (s, e) =>
                    {
                        if (DateTime.Now < quietUntil)
                        {
                            var loaded = (IntPtr)navigateSound.GetValue(null, null);
                            if (loaded != IntPtr.Zero) { savedNavigateSound = loaded; navigateSound.SetValue(null, IntPtr.Zero, null); }
                            var every = TimeSpan.FromMilliseconds(savedNavigateSound == IntPtr.Zero ? 15 : 100);
                            if (quietTimer.Interval != every) quietTimer.Interval = every;
                            return;
                        }
                        quietTimer.Stop();
                        if (savedNavigateSound != IntPtr.Zero && (IntPtr)navigateSound.GetValue(null, null) == IntPtr.Zero) navigateSound.SetValue(null, savedNavigateSound, null);
                    };
                }
                quietTimer.Start();
            }
            catch (Exception ex) { Log("quiet focus: " + ex.Message); }
        }

        // ONE player for moving and confirming everywhere: Selene's screens ask Playnite's own sound player (the one that
        // plays when the selection moves in Playnite's views, at its interface volume), so every move sounds the same.
        // During a quiet moment its move sound is emptied: it's put in just for this one play. False if Playnite's player
        // can't be reached (the caller plays the file itself then).
        private static MethodInfo playNavigate, playActivate;

        public static bool PlayPlayniteSound(bool confirm)
        {
            try
            {
                if (playNavigate == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("Playnite.FullscreenApp.FullscreenApplication", false);
                        if (t == null) continue;
                        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                        playNavigate = t.GetMethod("PlayNavigateSound", any);
                        playActivate = t.GetMethod("PlayActivateSound", any);
                        if (navigateSound == null) navigateSound = t.GetProperty("NavigateSound", any);
                        break;
                    }
                    if (playNavigate == null || playActivate == null) return false;
                }
                if (confirm) { playActivate.Invoke(null, null); return true; }
                var current = navigateSound != null ? (IntPtr)navigateSound.GetValue(null, null) : IntPtr.Zero;
                if (current == IntPtr.Zero && savedNavigateSound == IntPtr.Zero) return false;   // Playnite has no tick loaded yet (first setup): Selene's own plays
                bool borrow = current == IntPtr.Zero && savedNavigateSound != IntPtr.Zero;
                if (borrow) navigateSound.SetValue(null, savedNavigateSound, null);
                playNavigate.Invoke(null, null);
                if (borrow) navigateSound.SetValue(null, IntPtr.Zero, null);
                return true;
            }
            catch (Exception ex) { Log("playnite sound: " + ex.Message); return false; }
        }

        public static void EndQuietFocus()
        {
            if (quietUntil == DateTime.MinValue) return;
            quietUntil = DateTime.MinValue;
            try { if (navigateSound != null && savedNavigateSound != IntPtr.Zero && (IntPtr)navigateSound.GetValue(null, null) == IntPtr.Zero) navigateSound.SetValue(null, savedNavigateSound, null); }
            catch (Exception ex) { Log("quiet focus: " + ex.Message); }
        }

        // the login is about to fade off a home that will play its intro. The home's parts are rebuilt while the login
        // fades (new copies, nothing to hide ahead of time), so a cover (the wallpaper) goes over the whole home instead and lifts
        // when the intro starts; if no intro follows it lifts on its own.
        private static ThreadedFade introCover;
        private static bool coveredForSignIn;   // the intro then keeps the wallpaper up instead of fading it in from dark

        public static void HoldHomeForIntro()
        {
            QuietFocus(3000);   // the intro puts focus on the row (twice): not a move
            var w = Application.Current.MainWindow;
            var hub = w != null ? FindByName(w, "WelcomeHubOverlay") as Grid : null;
            if (hub == null) { Log("intro: no home to cover"); return; }
            // the wallpaper itself (as the home draws it, under the login's veil), so it stays up through signing in instead
            // of going dark; drawn and faded on its own thread (ThreadedFade): the home's busy rebuild made a fade jump
            if (introCover != null) introCover.Remove();
            var res = Application.Current.Resources;
            var pic = res["SeleneWallpaper"] as ImageSource;
            introCover = new ThreadedFade(
                Application.Current.TryFindResource("SeleneBaseBrush") as Brush,
                pic != null ? new ImageBrush(pic) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top } : null,   // as the home's Image places it (centered, it sat off and the wallpaper jumped)
                Ink.Brush("SeleneScrimColor", 0.45));
            Panel.SetZIndex(introCover, 1000);
            Grid.SetRowSpan(introCover, 100);
            Grid.SetColumnSpan(introCover, 100);
            hub.Children.Add(introCover);
            coveredForSignIn = true;
            Log("intro: home covered for the login fade");
            var since = DateTime.Now;
            var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            check.Tick += (s, e) =>
            {
                check.Stop();
                if (lastIntro >= since) return;
                Log("intro: none followed the login, showing the home as it is");
                LiftIntroCover();
            };
            check.Start();
        }

        private static void LiftIntroCover()
        {
            var cover = introCover;
            if (cover == null || !(VisualTreeHelper.GetParent(cover) is Panel)) return;
            // right after the rebuilt home has been drawn under it: the login's dimmed wallpaper clears slowly to the home
            // (on its own thread, smooth however busy)
            cover.Dispatcher.BeginInvoke(new Action(() => cover.FadeOut(450)), System.Windows.Threading.DispatcherPriority.Background);   // the cards fly in as it clears
        }

        private static bool introDone;
        // right after Playnite starts or someone signs in, the row starts on the first card (Library), not a game
        private static bool startOnCards = true;

        private static void PlayIntro(ListBox row)
        {
            // Collection is up (Find a game opened it as the layer closed): the home's selection would overwrite the game
            // picked there (both follow Playnite's selected game); the home gets its own back when it shows (SyncSelectionForHome)
            if (SeleneShell.CollectionActive) { LiftIntroCover(); return; }
            if ((DateTime.Now - lastIntro).TotalMilliseconds < 800) return;  // several watchers fire for one hub open
            lastIntro = DateTime.Now;
            LiftIntroCover();
            // the cards' fly-in: the first time the home shows, and after every sign-in (as the login's dimmed wallpaper clears)
            bool animate = (!introDone || coveredForSignIn) && !Lite;  // full intro only the first time the hub is seen after Playnite starts (never in a streamed session)
            introDone = true;
            try
            {
                // The row's SelectedItem -> SelectedGame binding only pushes on change, so on open Playnite
                // would keep its old selection (music, background) while the row shows its first game.
                ApplyCardOrder(row);
                RebuildGames(row);
                int first = startOnCards && row.Items.Count > 0 ? StartCardIndex(row) : FirstGameIndex(row);
                // a game running (home from inside it): straight on that game (on the first one, the first game's art
                // flashed up before the focus went over to the running one)
                int running = RunningGameIndex(row);
                if (running >= 0) first = running;
                startOnCards = false;
                movingCard = null;
                movingGame = null;
                {
                    var sv0 = FindChild<ScrollViewer>(row);
                    if (sv0 != null) { sv0.ScrollToHorizontalOffset(Math.Max(0, first - AnchorSlot)); row.UpdateLayout(); }
                    var fc = row.ItemContainerGenerator.ContainerFromIndex(first) as UIElement;
                    if (fc != null) fc.Focus(); else row.SelectedIndex = first;
                    var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                    again.Tick += (s2, e2) =>
                    {
                        again.Stop();
                        if (row.IsVisible && !row.IsKeyboardFocusWithin)
                        {
                            var fc2 = row.ItemContainerGenerator.ContainerFromIndex(first) as UIElement;
                            if (fc2 != null) { fc2.Focus(); Log("refocused first game"); }
                        }
                    };
                    again.Start();
                }
                PushSelected(row);  // hub open: Playnite's selection follows the row right away
                ArmPreview(row);    // same game still selected -> no SelectionChanged; restart the "rested on it" timer

                row.UpdateLayout();
                ScheduleTitle(row, animate ? 1300 : 250);

                if (!animate)
                {
                    coveredForSignIn = false;
                    // the game info was held hidden for a fly-in that isn't coming
                    var heldInfo = FindByName(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(row)), "SeleneInfoIntro");
                    if (heldInfo != null) heldInfo.BeginAnimation(UIElement.OpacityProperty, null);
                    Log("hub reopened: default view, no intro");
                    return;
                }

                // Background fades up first, game info rises in after the tiles.
                var window = Window.GetWindow(row);
                var bg = window != null ? FindByName(window, "SeleneHubGameBackground") : null;
                if (bg != null && !coveredForSignIn) bg.BeginAnimation(UIElement.OpacityProperty, Keys(0, 1, 0, 0.9, new CubicEase { EasingMode = EasingMode.EaseOut }));
                else if (bg != null) bg.BeginAnimation(UIElement.OpacityProperty, null);   // signed in: the wallpaper just stays
                coveredForSignIn = false;
                var info = FindByName(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(row)), "SeleneInfoIntro");
                if (info != null)
                {
                    info.BeginAnimation(UIElement.OpacityProperty, Keys(0, 1, 0.9, 0.5, null));
                    var lift = info.RenderTransform as TranslateTransform;
                    if (lift != null && !lift.IsFrozen) lift.BeginAnimation(TranslateTransform.YProperty, Keys(40, 0, 0.9, 0.55, new CubicEase { EasingMode = EasingMode.EaseOut }));
                }
                double center = row.ActualWidth / 2;
                int k = 0;
                for (int i = 0; i < row.Items.Count; i++)
                {
                    var c = row.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                    if (c == null || c.Visibility != Visibility.Visible || c.ActualWidth <= 0 || row.Items[i] is RowPad) continue;
                    double x = c.TranslatePoint(new Point(0, 0), row).X;
                    if (x + c.ActualWidth < 0) continue;
                    if (x > row.ActualWidth) break;

                    var scale = new ScaleTransform(0.35, 0.35);
                    var shift = new TranslateTransform();
                    var group = new TransformGroup();
                    group.Children.Add(scale);
                    group.Children.Add(shift);
                    c.RenderTransformOrigin = new Point(0.5, 0.5);
                    c.RenderTransform = group;

                    double delay = 0.30 + k * 0.045;
                    double fromX = (center - (x + c.ActualWidth / 2)) * 0.75;
                    // fly a cached picture of the tile (its shadow effect is not redrawn every frame), live again after landing
                    c.CacheMode = new BitmapCache(1.0) { SnapsToDevicePixels = false };  // stays on (EnsureTileCaches)
                    var glide = new CubicEase { EasingMode = EasingMode.EaseOut };
                    c.BeginAnimation(UIElement.OpacityProperty, Keys(0, 1, delay, 0.35, null));
                    shift.BeginAnimation(TranslateTransform.XProperty, Keys(fromX, 0, delay, 0.65, glide));
                    shift.BeginAnimation(TranslateTransform.YProperty, Keys(-18, 0, delay, 0.65, glide));
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, Keys(0.35, 1, delay, 0.65, glide));
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, Keys(0.35, 1, delay, 0.65, glide));
                    k++;
                }
                introUntil = DateTime.Now.AddSeconds(0.15 + Math.Min(6, Math.Max(0, k - 1)) * 0.03 + 0.25);   // moving is free once the first tiles have landed (the rest finish as you go)
                Log("intro played on " + k + " tiles");
                // an empty tab's message comes in with them, just after the last card lands
                RowEmptyIn(0.30 + k * 0.045 + 0.2);
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                Log(LastError);
            }
        }

        // Puts SeleneTileTitle under the selected tile (final zoomed size 160 x 1.3) and fades it in after 'delay' s.
        private const double ZoomedTile = 208;
        private const double SmallTile = 160;

        // Tiles shrink/zoom and the row slides for ~0.22 s after a move, so hide the title at once and place it
        // when the layout has settled (also keeps it hidden while Left/Right is held).
        private static System.Windows.Threading.DispatcherTimer titleTimer;

        private static void ScheduleTitle(ListBox row, int ms)
        {
            if (movingCard != null || movingGame != null) return;
            var title = FindTitle(row);
            if (title != null) { title.BeginAnimation(UIElement.OpacityProperty, null); title.Opacity = 0; }
            if (titleTimer == null) titleTimer = new System.Windows.Threading.DispatcherTimer();
            titleTimer.Stop();
            titleTimer.Interval = TimeSpan.FromMilliseconds(ms);
            titleTimer.Tag = row;
            titleTimer.Tick -= OnTitleTimer;
            titleTimer.Tick += OnTitleTimer;
            titleTimer.Start();
        }

        private static void OnTitleTimer(object sender, EventArgs e)
        {
            titleTimer.Stop();
            var row = titleTimer.Tag as ListBox;
            if (row == null) return;
            var c = row.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, row.SelectedIndex)) as FrameworkElement;
            if (c != null) PlaceTitle(row, c, 0);
        }

        private static FrameworkElement FindTitle(ListBox row)
        {
            var parent = VisualTreeHelper.GetParent(row);
            return parent != null ? FindByName(parent, "SeleneTileTitle") : null;
        }

        private static void PlaceTitle(ListBox row, FrameworkElement container, double delay)
        {
            if (previewShowing) return;  // no title beside the open 16:9 card
            var title = FindTitle(row);
            var canvas = title != null ? VisualTreeHelper.GetParent(title) as UIElement : null;
            var tile = FindByName(container, "Tile");
            if (title == null || canvas == null || tile == null) return;
            var p = RestingPoint(tile, container, canvas);
            // right edge of the highlight ring (it sits outside the card), measured where the row will come to rest:
            // the continuous slide may still be running, so its remaining offset is taken out
            var edge = FindByName(container, "Ring") ?? tile;
            double animated = p.X - RestingLeftRaw(tile, canvas);       // minus the container's intro/drop offset
            var panel = FindChild<VirtualizingStackPanel>(row);
            var slide = panel != null ? panel.RenderTransform as TranslateTransform : null;
            if (slide != null) animated -= slide.X - glideRest;           // minus what is left of the row's slide (not the offset it rests on)
            double right = edge.TranslatePoint(new Point(edge.ActualWidth, 0), canvas).X + animated;
            right = Math.Max(right, RestingLeftRaw(tile, canvas) + animated + ZoomedTile);
            Canvas.SetLeft(title, right + 16);
            Canvas.SetTop(title, p.Y + SmallTile + 14);
            title.BeginAnimation(UIElement.OpacityProperty, Keys(0, 1, delay, 0.2, null));
        }

        // the last element found for a name in a window, kept while it's still in that window: the whole tree walked on every
        // look-up took up to ~100 ms on a big library, and timers look things up many times a second (hitches)
        private static readonly Dictionary<string, WeakReference> foundInWindow = new Dictionary<string, WeakReference>();

        internal static FrameworkElement FindByName(DependencyObject root, string n)
        {
            var window = root as Window;
            if (window == null || n == null) return Search(root, n);
            WeakReference known;
            if (foundInWindow.TryGetValue(n, out known))
            {
                var fe = known.Target as FrameworkElement;
                if (fe != null && fe.Name == n && window.IsAncestorOf(fe)) return fe;
            }
            var hit = Search(root, n);
            if (hit != null) foundInWindow[n] = new WeakReference(hit);
            return hit;
        }

        private static FrameworkElement Search(DependencyObject root, string n)
        {
            int k = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < k; i++)
            {
                var ch = VisualTreeHelper.GetChild(root, i);
                var fe = ch as FrameworkElement;
                if (fe != null && fe.Name == n) return fe;
                var hit = Search(ch, n);
                if (hit != null) return hit;
            }
            return null;
        }

        // Holds 'from' until 'delay', then eases to 'to' over 'duration' seconds.
        private static DoubleAnimationUsingKeyFrames Keys(double from, double to, double delay, double duration, IEasingFunction ease)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(delay))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(delay + duration)), ease));
            return a;
        }

        // Section cards: items "nav:<name>" at the start of the row. A runs hidden proxy button SeleneNav_<name>;
        // Y (or F2) picks a card up, Left/Right moves it among the cards, Y/A drops it. Order is saved.
        private static string movingCard;
        private static readonly string CardOrderFile = System.IO.Path.Combine(SelenePaths.PlayniteConfig, "selene-card-order.txt");

        // where the row starts after Playnite starts or someone signs in: the Store card (the first card without it)
        private static int StartCardIndex(ListBox row)
        {
            int i = row.Items.IndexOf("nav:store");
            return i >= 0 ? i : 0;
        }

        private static int RunningGameIndex(ListBox row)
        {
            for (int i = 0; i < row.Items.Count; i++)
            {
                var g = row.Items[i];
                if (g == null || g is string || g is RowPad) continue;
                var running = Prop(g, "IsRunning");
                if (running is bool && (bool)running) return i;
            }
            return -1;
        }

        private static int FirstGameIndex(ListBox row)
        {
            int i = 0;
            while (i < row.Items.Count && row.Items[i] is string) i++;
            if (i < row.Items.Count && row.Items[i] is RowPad) return i - 1;   // filter left no games: last nav card
            return i;
        }

        private static void RunCard(ListBox row, string name)
        {
            var scope = VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(row));
            var proxy = scope != null ? FindByName(scope, "SeleneNav_" + name) as System.Windows.Controls.Primitives.ButtonBase : null;
            // the proxies sit higher up than the row's own panels: the whole window then
            if (proxy == null) { var w = Window.GetWindow(row); proxy = w != null ? FindByName(w, "SeleneNav_" + name) as System.Windows.Controls.Primitives.ButtonBase : null; }
            Log("card " + name + ": " + (proxy == null ? "no proxy" : proxy.Command == null ? "no command" : "run"));
            if (proxy != null) Ui.Confirm();   // opening Store or Collection sounds like any other A
            try { if (proxy != null && proxy.Command != null && proxy.Command.CanExecute(proxy.CommandParameter)) proxy.Command.Execute(proxy.CommandParameter); }
            catch (Exception ex) { Log("card " + name + " failed: " + ex); }
        }

        private static bool returnToHub;

        private static ListBox FindRow(DependencyObject d)
        {
            for (; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var lb = d as ListBox;
                if (lb != null && lb.Name == "SeleneGameRow") return lb;
            }
            return null;
        }

        private static bool InLibraryList(DependencyObject d)
        {
            for (; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var lb = d as ListBox;
                if (lb != null) return lb.Name == "PART_ListGameItems" || lb.Name == "PART_ListGameItemsDetailed";
            }
            return false;
        }

        private static bool IsInRow(DependencyObject d)
        {
            for (; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var lb = d as ListBox;
                if (lb != null && lb.Name == "SeleneGameRow") return true;
            }
            return false;
        }

        // B on the game details opened from Spotlight: the home shows again under the page first, then the details close.
        // (Closing first left Playnite's library grid, then a still of it melting over Spotlight, on screen for a moment.)
        public static bool CloseDetailsToHub()
        {
            if (!returnToHub) return false;
            returnToHub = false;
            try
            {
                var w = Application.Current.MainWindow;
                SeleneShell.FadeFromDetails(w != null ? FindByName(w, "PART_ElemGameDetails") : null, 280);
                SeleneShell.ShowHome(true);
                PlayniteMenu.Main("ToggleGameDetailsCommand");
            }
            catch (Exception ex) { Log("details close: " + ex.Message); }
            return true;
        }

        private static void WatchDetails(Window w)
        {
            var details = FindByName(w, "PART_ElemGameDetails");
            Log("details view found=" + (details != null));
            if (details == null) return;
            details.IsVisibleChanged += (s, a) =>
            {
                if ((bool)a.NewValue || !returnToHub) return;
                returnToHub = false;
                w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var hub = FindByName(w, "WelcomeHubOverlay");
                    var proxy = FindByName(w, "SeleneNav_hub") as System.Windows.Controls.Primitives.ButtonBase;
                    Log("details closed, reopening hub: hubVisible=" + (hub != null && hub.IsVisible) + " proxy=" + (proxy != null));
                    if (hub != null && hub.IsVisible) return;
                    if (proxy != null && proxy.Command != null && proxy.Command.CanExecute(null)) proxy.Command.Execute(null);
                }), System.Windows.Threading.DispatcherPriority.Normal);   // before the next frame: at Background, Playnite's library grid showed for a moment
            };
        }

        private static readonly HashSet<string> padHeld = new HashSet<string>();
        private static string lastPadButton, lastPadRaw;
        private static DateTime lastPadAt;

        // Steady hold scrolling: a held Left / Right on Spotlight's row repeats every 85 ms from Playnite (measured), faster than
        // the row can follow on a 4K TV (50-57 fps then; smooth from ~120 ms). Its repeats are dropped and the row steps at its
        // own steady pace while the button stays down; the first press still moves at once. Switch: Spotlight > Steady hold scrolling.
        private const int SteadyStepMs = 120;
        private static System.Windows.Threading.DispatcherTimer steady;
        private static string steadyButton;
        private static DateTime steadyHeardAt;
        private static bool steadyReleased;   // a release came: unless another repeat follows at once, the hold is over
        private static bool steadyStepping;
        private static bool padLetGo = true;   // the last pressed button was released since (the next press is a new one)   // one of the steady steps is going through (not a repeat to drop)

        private static bool SteadyRepeat(string button)
        {
            if (!Features.On("steadyhold") || (button != "DPadLeft" && button != "DPadRight")) return false;
            var f = Keyboard.FocusedElement as DependencyObject;
            if (f == null || !IsInRow(f)) { StopSteady(); return false; }
            steadyHeardAt = DateTime.Now;   // Playnite still repeating: the button is still down
            steadyReleased = false;
            if (steady != null && steady.IsEnabled && steadyButton == button) return true;
            steadyButton = button;
            if (steady == null)
            {
                steady = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(SteadyStepMs) };
                steady.Tick += (s, e) =>
                {
                    // let go without a release reaching us (focus moved, a dialog): stop when Playnite's repeats stop
                    var focused = Keyboard.FocusedElement as UIElement;
                    double quiet = (DateTime.Now - steadyHeardAt).TotalMilliseconds;
                    if ((steadyReleased && quiet > 95) || quiet > 300 || focused == null || !IsInRow(focused)) { StopSteady(); return; }
                    // the step: the row's own move (as a press ends up doing), so the selection keeps its slot and the row slides
                    if (!StepRow(steadyButton == "DPadLeft" ? -1 : 1)) StopSteady();
                    if (Ui.DevFrames)
                    {
                        var row = ItemsControl.ItemsControlFromItemContainer(Keyboard.FocusedElement as DependencyObject ?? focused) as ListBox;
                        var sv = row != null ? FindChild<ScrollViewer>(row) : null;
                        Log("steady step " + steadyButton + " -> " + (row != null ? row.SelectedIndex.ToString() : "?") + " offset " + (sv != null ? sv.HorizontalOffset.ToString("0") : "?"));
                    }
                };
            }
            steady.Start();
            return true;
        }

        private static void StopSteady() { if (steady != null) steady.Stop(); }

        // one move along Spotlight's row from the focused tile (what Left / Right on the row does): false at its end or off the row
        private static bool StepRow(int dir)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            ListBox row = null;
            ListBoxItem item = null;
            for (var d = focused; d != null; d = GetParent(d))
            {
                if (item == null && d is ListBoxItem) item = (ListBoxItem)d;
                var lb = d as ListBox;
                if (lb != null && lb.Name == "SeleneGameRow") { row = lb; break; }
            }
            if (row == null || movingCard != null || movingGame != null) return false;
            int cur = item != null ? row.ItemContainerGenerator.IndexFromContainer(item) : -1;
            if (cur < 0) cur = Math.Max(0, row.SelectedIndex);
            for (int i = cur + dir; i >= 0 && i < row.Items.Count; i += dir)
                if (IsShown(row.Items[i])) { MoveTo(row, cur, i); return true; }
            return false;
        }

        // developer (selene-dev-frames): how a held direction's repeats arrive, every 2 s while one is held (the gaps between presses)
        private static readonly List<int> devGaps = new List<int>();
        private static DateTime devGapsAt = DateTime.MinValue;
        private static void DevRepeat(string button, DateTime now)
        {
            if (button != lastPadButton || (now - lastPadAt).TotalMilliseconds > 600) { devGaps.Clear(); devGapsAt = now; return; }
            devGaps.Add((int)(now - lastPadAt).TotalMilliseconds);
            if ((now - devGapsAt).TotalSeconds < 2) return;
            Log("pad repeats " + button + ": " + string.Join(" ", devGaps));
            devGaps.Clear(); devGapsAt = now;
        }

        private static void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (!(e.StagingItem.Input is MouseEventArgs) || DateTime.Now >= ignoreMouseUntil) InputHints.Observe(e.StagingItem.Input);
            bool fromPad = false;
            string padPressed = null;
            try
            {
                var ke = e.StagingItem.Input as KeyEventArgs;
                if (HandleLaunchBack(ke)) { e.Cancel(); return; }
                if (heldKey != Key.None && ke != null && ke.Key == heldKey && ke.GetType().Name != "GameControllerInputEventArgs")
                {
                    // the hold's key repeats while it's down (even after settings opened over the home, where Tab would mean Start
                    // again and again): only the release counts
                    if (ke.RoutedEvent == Keyboard.PreviewKeyUpEvent || ke.RoutedEvent == Keyboard.KeyUpEvent) { heldKey = Key.None; EndHold(true); }   // (the preview's release: cancelling it ends the key's events)
                    e.Cancel(); return;
                }
                if (ke != null && ke.GetType().Name != "GameControllerInputEventArgs" && !Ui.KeyIsPadCopy() && !Ui.PressFromPad
                    && ke.RoutedEvent == Keyboard.PreviewKeyDownEvent && !LoginScene.IsActive)
                {
                    if ((ke.Key == Key.PageUp || ke.Key == Key.PageDown) && DetailsStage.Active) { DetailsStage.ScrollPage(ke.Key == Key.PageDown); e.Cancel(); return; }
                    if ((ke.Key == Key.PageUp || ke.Key == Key.PageDown) && (SpotlightShowing() || SeleneShell.CollectionActive))
                    {
                        int direction = ke.Key == Key.PageDown ? 1 : -1;
                        if (SpotlightShowing()) CycleRowFilter(direction); else CyclePreset(direction);
                        e.Cancel(); return;
                    }
                    if (ke.Key == Key.Tab && OnHomeOrLibrary()) { if (heldKey == Key.None) { heldKey = Key.Tab; BeginHold("Start"); } e.Cancel(); return; }   // like Start on the pad: a tap opens the menu, a hold Selene settings
                    if (ke.Key == Key.V && OnHomeOrLibrary()) { if (heldKey == Key.None) { heldKey = Key.V; BeginHold("Back"); } e.Cancel(); return; }   // like View on the pad: a tap switches home and library, a hold finds a game
                    if (ke.Key == Key.F3 && OnHomeOrLibrary()) { if (LibraryStage.Playing) LibraryStage.ToggleSound(); else if (previewShowing) TogglePreviewSound(); e.Cancel(); return; }
                }
                // a press: not idle; one that wakes the idle screen does nothing else
                if (ke != null && ke.RoutedEvent != Keyboard.KeyUpEvent)
                {
                    bool woke = IdleScreen.Showing;
                    IdleScreen.Touched();
                    if (woke) { e.Cancel(); return; }
                }
                // Playnite also turns LT/RT into PageUp/PageDown, which scrolled the hub to the last section. On the game
                // row the triggers are the filter only, so those keys are dropped there.
                if (ke != null && (ke.Key == Key.PageUp || ke.Key == Key.PageDown || ke.Key == Key.Home || ke.Key == Key.End)
                    && (ke.RoutedEvent == Keyboard.KeyDownEvent || ke.RoutedEvent == Keyboard.PreviewKeyDownEvent))
                {
                    if (!LoginScene.IsActive && FocusOnHome()) { e.Cancel(); return; }
                }
                // a real key on the home ends the quiet moment at once (only Selene's own focus moves stay silent)
                if (ke != null && ke.RoutedEvent == Keyboard.KeyDownEvent && !LoginScene.IsActive) EndQuietFocus();
                if (ke == null || ke.GetType().Name != "GameControllerInputEventArgs") return;
                var mainW = Application.Current.MainWindow;
                if (mainW != null && (mainW.WindowState == WindowState.Minimized || !PlayniteInFront()))
                {
                    e.Cancel();  // Playnite out of sight: the pad belongs to whatever is in front (a menu here could restart the PC)
                    if (ke.RoutedEvent == Keyboard.KeyDownEvent) Log("pad: press ignored, Playnite not in front (" + ForegroundWho() + ")");
                    return;
                }
                // a button let go: screens that tell a tap from a hold hear of it (the clip player's Left / Right)
                if (ke.RoutedEvent == Keyboard.KeyUpEvent)
                {
                    var relProp = ke.GetType().GetProperty("Button");
                    var rel = relProp != null ? relProp.GetValue(ke, null) : null;
                    if (rel != null) { Ui.Released(Ui.Normalize(rel.ToString())); padHeld.Remove(rel.ToString()); steadyReleased = true; padLetGo = true; }
                }
                else Ui.PadPressAt = DateTime.Now;
                // Start / View released: a short press does its usual thing (a hold already did its own)
                if (ke.RoutedEvent == Keyboard.KeyUpEvent && holdButton != null)
                {
                    var upProp = ke.GetType().GetProperty("Button");
                    var up = upProp != null ? upProp.GetValue(ke, null).ToString() : null;
                    if (up == holdButton) { e.Cancel(); EndHold(true); }
                    return;
                }
                if (ke.RoutedEvent != Keyboard.KeyDownEvent) return;
                var button = ke.GetType().GetProperty("Button");
                if (button == null) return;
                var pressed = button.GetValue(ke, null).ToString();
                fromPad = true;
                // the same press arriving twice (one pad seen as two devices, or stick and d-pad together) moved two
                // items at a time: a second identical direction within a moment is dropped
                var same = Ui.Normalize(pressed);
                padPressed = pressed;
                var now = DateTime.Now;
                if (!steadyStepping && same == lastPadButton && (now - lastPadAt).TotalMilliseconds < 60)
                {
                    Log("pad: dropped double " + pressed + " (" + (int)(now - lastPadAt).TotalMilliseconds + " ms after " + lastPadRaw + ")");
                    e.Cancel();
                    return;
                }
                if (Ui.DevFrames) DevRepeat(same, now);
                // a held button's repeat: the same direction again with no release in between (quick taps always have one, and
                // each is a move of its own, at once)
                bool repeat = !steadyStepping && !padLetGo && same == lastPadButton && (now - lastPadAt).TotalMilliseconds < 400;
                padLetGo = false;
                if (!repeat) StopSteady();
                lastPadButton = same; lastPadRaw = pressed; lastPadAt = now;
                if (repeat && SteadyRepeat(same)) { e.Cancel(); return; }
                HideCursorForController();
                // both sticks clicked together: Report a problem, with a picture of the screen as it is (View + Start is often
                // taken: Steam and others put the PC to sleep with it)
                if (((pressed == "LeftStick" && padHeld.Contains("RightStick")) || (pressed == "RightStick" && padHeld.Contains("LeftStick"))) && Features.On("reportchord"))
                {
                    e.Cancel();
                    padHeld.Clear();
                    ReportScreen.OpenFromShortcut();
                    return;
                }
                padHeld.Add(pressed);
                // one of Playnite's dialogs is up (a yes / no box, an add-on install): the press is the dialog's alone (it moved
                // the screen behind too)
                if (DialogUp() || DialogLook.IsOpen)
                {
                    Log("pad: " + pressed + " went to a window over Playnite (" + ActiveWindowWho() + ")");
                    var answer = Ui.Normalize(pressed);
                    // B answers it the safe way (No / Cancel); Playnite's own dialogs did nothing on B
                    if (answer == "B" && DialogLook.Back()) e.Cancel();
                    else if (DialogLook.Take(answer)) e.Cancel();   // (a dialog without the focus: the press is still its own)
                    return;
                }
                if (LoginScene.HandleButton(pressed)) { e.Cancel(); return; }
                // starting up (the home not shown yet, its intro still to come): the home takes no presses (an A then opened the
                // card the focus sat on, unseen behind the start-up screen)
                if (!introDone && FocusOnHome() && pressed != "Guide") { Log("pad: " + pressed + " before the home was up, ignored"); e.Cancel(); return; }
                if (IntroRunning && pressed != "Start" && pressed != "Back" && pressed != "Guide" && FocusOnHome()) { e.Cancel(); return; }
                if (StoreShowcase.Handle(Ui.Normalize(pressed)) || CardShowcase.Handle(Ui.Normalize(pressed))) { e.Cancel(); return; }   // the cards' pictures
                EndQuietFocus();   // the user is moving now: their moves tick
                // View (Playnite calls it Back): home <-> library, like the legacy theme (Playnite opened its settings)
                // R3 in the library while a trailer plays: its sound (else Playnite's filter, as usual)
                if (pressed == "RightStick" && LibraryStage.Playing && LibraryStage.ToggleSound()) { e.Cancel(); return; }
                // LS (pressing the left stick) opened Playnite's filter-preset list, in Collection and on Spotlight's cards: the
                // tabs (LB / RB) do that now, so it does nothing on either screen
                if (pressed == "LeftStick" && OnHomeOrLibrary()) { e.Cancel(); return; }
                // Start and View on the home or the library: short press or hold (Selene settings / library search)
                if ((pressed == "Start" || pressed == "Back") && OnHomeOrLibrary())
                {
                    e.Cancel();
                    if (holdButton != pressed) BeginHold(pressed);   // a held button repeats: one hold only
                    return;
                }
                // Start opens Selene's menu (Playnite's own is under More in it)
                if (pressed == "Start" && mainW != null && FocusOnHome()) { SeleneShell.OpenMenu(); e.Cancel(); return; }   // (home row not visible: other home sections)
                if (GameMedia.HandleButton(pressed, Keyboard.FocusedElement)) { e.Cancel(); return; }
                if (DetailsStage.Handle(Ui.Normalize(pressed))) { e.Cancel(); return; }   // the details page's bands
                if (pressed == "RightStick" && previewShowing) { TogglePreviewSound(); e.Cancel(); return; }
                // A still launches the game below; Up/Down leave the card playing (only sideways moves shrink it)
                bool dirPad = pressed.StartsWith("DPad") || (pressed.StartsWith("LeftStick") && pressed != "LeftStick");
                bool sideways = dirPad && (pressed.EndsWith("Left") || pressed.EndsWith("Right"));
                if (previewShowing && pressed != "A" && (!dirPad || (sideways && IsInRow(Keyboard.FocusedElement as DependencyObject)))) SmoothClosePreview();
                if (HandleInfoButtons(pressed, dirPad)) { e.Cancel(); return; }
                // Collection's list: nothing above or below it (Up / Down took the focus off to hidden parts)
                if (dirPad && (pressed.EndsWith("Up") || pressed.EndsWith("Down")) && InLibraryList(Keyboard.FocusedElement as DependencyObject)) { e.Cancel(); return; }
                var item = Keyboard.FocusedElement as ListBoxItem;
// per-press file logging removed (disk write on the UI thread every move)
                //                 Log("pad " + pressed + " focus=" + (Keyboard.FocusedElement == null ? "null" : Keyboard.FocusedElement.GetType().Name) + " item=" + (item != null ? item.DataContext : null));
                // LB / RB switch the home's filters (All games, Installed...); moving a tile: nothing
                if ((pressed == "LeftShoulder" || pressed == "RightShoulder") && SpotlightShowing())
                {
                    if (movingCard == null && movingGame == null) CycleRowFilter(pressed == "RightShoulder" ? 1 : -1);   // also brings focus back to the game row
                    e.Cancel();
                    return;
                }
                // LB / RB in Collection: Playnite's filter presets, like Spotlight's tabs
                if ((pressed == "LeftShoulder" || pressed == "RightShoulder") && CyclePreset(pressed == "RightShoulder" ? 1 : -1)) { e.Cancel(); return; }
                if (pressed == "LeftShoulder" || pressed == "RightShoulder")
                {
                    lastShoulder = pressed;
                    var r0 = LiveRow();
                    bool wasActive = RowPageActive(r0);
                    var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                    check.Tick += (s2, e2) =>
                    {
                        check.Stop();
                        var r1 = LiveRow();
                        if (!wasActive && RowPageActive(r1) && ReferenceEquals(r0, r1)) PlayPageEnter(r1);  // rebuilt rows animate in RestoreSelection
                    };
                    check.Start();
                }
                bool onHome = movingCard == null && movingGame == null && FocusOnHome();
                if ((pressed == "TriggerLeft" || pressed == "TriggerRight") && onHome)
                {
                    // LT / RT: the start of the row, or its end (the filters are on LB / RB)
                    var tr = LiveRow();
                    if (tr != null && tr.Items.Count > 0)
                    {
                        int last = tr.Items.Count - 1;
                        while (last > 0 && tr.Items[last] is RowPad) last--;
                        int to = pressed == "TriggerLeft" ? 0 : last;
                        if (to != tr.SelectedIndex) { HidePreview(); Ui.Tick(); PlaceAtLeft(tr, to); }
                    }
                    e.Cancel();
                    return;
                }
                if (pressed == "X" && onHome)
                {
                    var hr = LiveRow();
                    var sel = hr != null ? hr.SelectedItem : null;
                    var xRunning = sel != null ? Prop(sel, "IsRunning") : null;
                    bool closes = xRunning is bool && (bool)xRunning && CanReturnToGame();
                    if (sel != null && !(sel is string) && !(sel is RowPad) && !closes)
                    {
                        // X: the game's details, as in Collection (Favorite is in Game options and on the heart)
                        HidePreview();
                        OpenDetailsFromHome(hr, sel, Window.GetWindow(hr));
                        e.Cancel();
                        return;
                    }
                }
                if (pressed == "X" && item != null && IsInRow(item)) returnToHub = true;
                if (pressed == "B" && item != null && IsInRow(item) && (movingCard != null || movingGame != null))
                {
                    // B leaves move mode (item stays where it is), nothing else happens
                    movingCard = null;
                    movingGame = null;
                    SetMoveVisual(item, false);
                    e.Cancel();
                    return;
                }
                // Collection with no games: B goes back to Spotlight, and the game buttons do nothing (Y opened Playnite's search
                // keyboard, A/X had no game to act on)
                if (SeleneShell.CollectionActive && CollectionEmpty())
                {
                    var norm = Ui.Normalize(pressed);
                    if (norm == "B")
                    {
                        var mw0 = Application.Current.MainWindow;
                        var back = mw0 != null ? FindByName(mw0, "SeleneNav_hub") as System.Windows.Controls.Primitives.ButtonBase : null;
                        if (back != null && back.Command != null) { Log("B in empty Collection -> Spotlight"); Ui.Back(); back.Command.Execute(null); }
                        e.Cancel();
                        return;
                    }
                    if (norm == "A" || norm == "X" || norm == "Y" || pressed == "RightStick") { e.Cancel(); return; }
                }
                if (pressed == "B" && item != null && InLibraryList(item))
                {
                    var w = Window.GetWindow(item);
                    var hub = w != null ? FindByName(w, "WelcomeHubOverlay") : null;
                    var details = w != null ? FindByName(w, "PART_ElemGameDetails") : null;
                    var proxy = w != null ? FindByName(w, "SeleneNav_hub") as System.Windows.Controls.Primitives.ButtonBase : null;
                    if ((hub == null || !hub.IsVisible) && (details == null || !details.IsVisible) && proxy != null && proxy.Command != null)
                    {
                        Log("B in library -> hub");
                        proxy.Command.Execute(null);
                        e.Cancel();
                        return;
                    }
                }
                // a game in Collection: A plays it as Spotlight's A does (Selene's loading screen at once, the same way through), X opens
                // its details (Playnite's own A)
                if ((pressed == "A" || pressed == "X" || pressed == "Y") && item != null && InLibraryList(item) && Api != null)
                {
                    if (pressed == "X")
                    {
                        try { PlayniteMenu.Main("ToggleGameDetailsCommand"); } catch (Exception ex) { Log("details in Collection: " + ex.Message); }   // (ActivateSelected was Play / Install: X opened Steam)
                        e.Cancel();
                        return;
                    }
                    if (pressed == "Y")
                    {
                        // Game options, as the hint says (Playnite's own Y opened its search box): Selene's screen, or Playnite's menu
                        var ydc = item.DataContext;
                        var yid = Prop(ydc, "Id");
                        if (!(yid is Guid)) { var yg = Prop(ydc, "Game"); if (yg != null) yid = Prop(yg, "Id"); }
                        if (!(yid is Guid && GameOptionsScreen.Open((Guid)yid)))
                            try { PlayniteMenu.Main("OpenGameMenuCommand"); } catch (Exception ex) { Log("game options in Collection: " + ex.Message); }
                        e.Cancel();
                        return;
                    }
                    var dc = item.DataContext;
                    var gid = Prop(dc, "Id");
                    if (!(gid is Guid)) { var g = Prop(dc, "Game"); if (g != null) gid = Prop(g, "Id"); }
                    if (gid is Guid)
                    {
                        if (Equals(Prop(dc, "IsInstalled"), true)) { Log("play in Collection -> " + Prop(dc, "Name")); StartGameNow((Guid)gid); }
                        else Api.InstallGame((Guid)gid);
                        e.Cancel();
                        return;
                    }
                }
                if (pressed == "X" && item != null && IsInRow(item) && CanReturnToGame())
                {
                    var g = item.DataContext;
                    var running = g == null || g is string ? null : Prop(g, "IsRunning");
                    if (running is bool && (bool)running) { e.Cancel(); CloseGame(g); return; }
                }
                if (pressed == "A" && item != null && CanReturnToGame())
                {
                    var g = item.DataContext;
                    var running = g == null || g is string ? null : Prop(g, "IsRunning");
                    if (running is bool && (bool)running)
                    {
                        Log("A on running game -> back to it: " + g);
                        ReturnToGame(Window.GetWindow(item));
                        e.Cancel();
                        return;
                    }
                }
                if (pressed == "A" && item != null && IsInRow(item))
                {
                    var nav = item.DataContext as string;
                    if (nav != null && nav.StartsWith("nav:"))
                    {
                        if (movingCard != null) { movingCard = null; SetMoveVisual(item, false); }
                        else RunCard(FindRow(item), nav.Substring(4));
                    }
                    else if (movingGame != null) { movingGame = null; SetMoveVisual(item, false); }
                    else
                    {
                        var idProp = item.DataContext != null ? item.DataContext.GetType().GetProperty("Id") : null;
                        if (Api != null && idProp != null) StartGameNow((Guid)idProp.GetValue(item.DataContext, null));
                    }
                    e.Cancel();
                    return;
                }
                if (pressed != "Y") return;
                if (item == null || !IsInRow(item)) return;
                var name = item.DataContext as string;
                if (name != null && name.StartsWith("nav:"))
                {
                    movingCard = movingCard == null ? name : null;
                    SetMoveVisual(item, movingCard != null);
                }
                else if (item.DataContext != null)
                {
                    bool canMove = RowFilters[RowFilter] != "recent";   // order there is last played, not arranged
                    var moveItem = item;
                    Action move = canMove ? (Action)(() => { movingGame = moveItem.DataContext; SetMoveVisual(moveItem, true); Ui.Tick(); }) : null;
                    var yid = Prop(item.DataContext, "Id");
                    if (movingGame == null && yid is Guid && GameOptionsScreen.Open((Guid)yid, move)) { e.Cancel(); return; }
                    // (Selene's game options off, or ending a move: Y moves, as before)
                    if (!canMove) { e.Cancel(); return; }
                    movingGame = movingGame == null ? item.DataContext : null;
                    SetMoveVisual(item, movingGame != null);
                }
                e.Cancel();
            }
            catch (Exception ex) { Log("Y: " + ex.Message); }
            finally { if (fromPad && e.Canceled) { Ui.PadHandled(); Ui.PadWent(padPressed); } }   // handled here: its keyboard copy is dropped (Playnite moves on the others from it)
        }

        // ---- Play / Details pills under the logo (SelenePlayBtn / SeleneDetailsBtn in the home block) ----
        // Down from the row lands on Play, Left/Right swaps, Up goes back to the row, A presses. Handled here because
        // the old theme's navigation service eats hub Left/Right and A never reaches a Button as a click.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, Button[]> rowPills =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, Button[]>();

        // ---- mouse cursor hides while the controller is in use ----
        private static bool cursorHidden;
        private static Point lastMousePos = new Point(double.NaN, double.NaN);
        private static bool cursorHooked;

        private static DateTime ignoreMouseUntil = DateTime.MinValue;

        private static void HideCursorForController()
        {
            if (cursorHidden) return;
            var w = Application.Current.MainWindow;
            if (w == null) return;
            if (!cursorHooked)
            {
                cursorHooked = true;
                w.PreviewMouseMove += (s, e) =>
                {
                    var pos = e.GetPosition(w);
                    if (DateTime.Now < ignoreMouseUntil) { lastMousePos = pos; return; }   // window switch (Guide), not the user
                    // WPF also raises MouseMove when the cursor is hidden/shown or content moves under it: only a real move counts
                    bool moved = double.IsNaN(lastMousePos.X) || Math.Abs(pos.X - lastMousePos.X) > 3 || Math.Abs(pos.Y - lastMousePos.Y) > 3;
                    lastMousePos = pos;
                    if (moved && cursorHidden) { cursorHidden = false; Mouse.OverrideCursor = null; }
                };
            }
            try { lastMousePos = Mouse.GetPosition(w); } catch { }
            cursorHidden = true;
            Mouse.OverrideCursor = Cursors.None;
        }

        private static bool HandleInfoButtons(string pressed, bool dirPad)
        {
            var focused = Keyboard.FocusedElement as FrameworkElement;
            var row = LiveRow();
            if (row == null || !row.IsVisible) return false;
            var w = Window.GetWindow(row);
            bool down = dirPad && pressed.EndsWith("Down"), up = dirPad && pressed.EndsWith("Up");
            bool left = dirPad && pressed.EndsWith("Left"), right = dirPad && pressed.EndsWith("Right");
            // cheap exits first: this runs on every controller press, and left/right inside the row never concerns the pills
            if (!dirPad && pressed != "A") return false;
            bool inRow = focused != null && IsInRow(focused);
            if (inRow && up) return true;   // nothing above the row (focus went to hidden parts and the row dropped away)
            if (inRow && !down) return false;
            // the Library and Store cards have nothing under them: Down stays put (it used to move focus somewhere unseen)
            if (inRow && down && movingCard == null && movingGame == null && row.SelectedItem is string) return true;
            // the pills next to the LIVE row (LB/RB rebuilds leave hidden copies of the hub page around), found once per row
            Button[] pills;
            if (!rowPills.TryGetValue(row, out pills) || pills[0] == null)
            {
                pills = new Button[3];
                for (DependencyObject d = row; d != null && pills[0] == null; d = VisualTreeHelper.GetParent(d))
                {
                    pills[0] = FindByName(d, "SelenePlayBtn") as Button;
                    pills[1] = pills[0] != null ? FindByName(d, "SeleneDetailsBtn") as Button : null;
                    pills[2] = pills[0] != null ? FindByName(d, "SeleneFavBtn") as Button : null;
                }
                if (pills[0] != null) { rowPills.Remove(row); rowPills.Add(row, pills); }
            }
            Button play = pills[0], details = pills[1], fav = pills[2];
            if (play == null || details == null || !play.IsVisible) return false;

            if (focused != null && IsInRow(focused) && down && movingCard == null && movingGame == null)
            {
                var it = row.SelectedItem;
                if (it == null || it is string) return true;   // cards: nothing below
                FocusPill(play);
                return true;
            }
            bool onPlay = ReferenceEquals(focused, play), onDetails = ReferenceEquals(focused, details), onFav = fav != null && ReferenceEquals(focused, fav);
            // Up from anything below the pills on the home screen (Library News / Latest News) lands on Play, not the row
            if (up && !onPlay && !onDetails && !onFav && focused != null && !IsInRow(focused) && w != null)
            {
                var hub = FindByName(w, "WelcomeHubOverlay");
                if (hub != null && hub.IsVisible && focused.IsVisible && hub.IsAncestorOf(focused))
                {
                    double focusedY = focused.TranslatePoint(new Point(0, 0), w).Y;
                    double playBottom = play.TranslatePoint(new Point(0, play.ActualHeight), w).Y;
                    var it = row.SelectedItem;
                    if (focusedY > playBottom && it != null && !(it is string)) { FocusPill(play); return true; }
                }
            }
            if (!onPlay && !onDetails && !onFav) return false;
            if (up) { ReleasePillLock(); FocusRowSoon(0); return true; }
            if (left || right)
            {
                // Play | Details | Favourite (ends stay put)
                var order = fav != null && fav.IsVisible ? new[] { play, details, fav } : new[] { play, details };
                int at = onPlay ? 0 : onDetails ? 1 : 2;
                FocusPill(order[Math.Max(0, Math.Min(order.Length - 1, at + (left ? -1 : 1)))]);
                return true;
            }
            if (pressed != "A") { if (down) ReleasePillLock(); return false; }
            ReleasePillLock();

            var game = row.SelectedItem;
            if (game == null || game is string) return true;
            if (onFav) { ToggleFavorite(game); return true; }
            var running = Prop(game, "IsRunning");
            bool isRunning = running is bool && (bool)running && CanReturnToGame();
            HidePreview();
            if (onPlay)
            {
                if (isRunning) { ReturnToGame(w); return true; }
                var id = Prop(game, "Id");
                if (id is Guid && Api != null) { Log("play button -> " + game); StartGameNow((Guid)id); }
                return true;
            }
            if (isRunning) { CloseGame(game); return true; }
            OpenDetailsFromHome(row, game, w);
            return true;
        }

        // a game's details page from Spotlight (the Details pill, X): the home overlay closes first (as the Library card does),
        // then details open; closing them brings the home back (WatchDetails)
        private static void OpenDetailsFromHome(ListBox row, object game, Window w)
        {
            PushSelected(row);
            var vm = row.DataContext;
            var cmd = vm != null ? vm.GetType().GetProperty("ToggleGameDetailsCommand") : null;
            var command = cmd != null ? cmd.GetValue(vm, null) as ICommand : null;
            if (command != null)
            {
                // the details view lives under the home overlay: close the overlay first (as the Library card does),
                // then open details; closing details brings home back (WatchDetails)
                returnToHub = true;
                openingDetails = true;
                var close = FindByName(w, "SeleneNav_library") as System.Windows.Controls.Primitives.ButtonBase;
                if (close != null && close.Command != null && close.Command.CanExecute(null)) close.Command.Execute(null);
                w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    HoldSelection(vm, game);
                    try { command.Execute(null); } finally { openingDetails = false; }
                    Log("details button -> " + game);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // The details page shows Playnite's selected game, and a library update reloading the list (the one at start, a few
        // seconds in) puts that selection back on the list's first game: the page opened on another game than the one asked
        // for. For a few seconds after opening, the selection is put back on the game asked for whenever it moves away.
        private static void HoldSelection(object vm, object game)
        {
            var prop = vm != null ? vm.GetType().GetProperty("SelectedGame") : null;
            var notify = vm as System.ComponentModel.INotifyPropertyChanged;
            if (prop == null || !prop.CanWrite || game == null || game is string) return;
            var wantId = Prop(game, "Id");
            Func<bool> wrong = () => { var now = prop.GetValue(vm, null); return now == null || !Equals(Prop(now, "Id"), wantId); };
            if (wrong()) prop.SetValue(vm, game, null);
            if (notify == null) return;
            var until = DateTime.Now.AddSeconds(4);
            System.ComponentModel.PropertyChangedEventHandler watch = null;
            watch = (s, e) =>
            {
                if (e.PropertyName != "SelectedGame") return;
                if (DateTime.Now > until) { notify.PropertyChanged -= watch; return; }
                var main = Application.Current.MainWindow;
                var page = main != null ? FindByName(main, "PART_ElemGameDetails") : null;
                if (page != null && !page.IsVisible && DateTime.Now > until.AddSeconds(-3.5)) { notify.PropertyChanged -= watch; return; }   // left the page: the next pick is the user's
                if (!wrong()) return;
                Log("details: the selection moved to " + prop.GetValue(vm, null) + ", back to " + game);
                Application.Current.Dispatcher.BeginInvoke(new Action(() => { if (wrong()) prop.SetValue(vm, game, null); }));
            };
            notify.PropertyChanged += watch;
            var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4.2) };
            stop.Tick += (s, e) => { stop.Stop(); notify.PropertyChanged -= watch; };
            stop.Start();
        }

        // the old theme's navigation reacts to the same Down first and moves focus to the news a moment later (deferred), which
        // flashed the news before Play. While the lock runs, focus may only go to the chosen pill.
        private static bool openingDetails;

        // focus somewhere on the visible home page (row, pills, news...), not in the top bar or an overlay window
        private static bool FocusOnHome()
        {
            var f = Keyboard.FocusedElement as DependencyObject;
            if (f == null) return false;
            if (IsInRow(f)) return true;
            var w = Window.GetWindow(f);
            if (w == null || w != Application.Current.MainWindow) return false;
            var hub = FindByName(w, "WelcomeHubOverlay");
            return hub != null && hub.IsVisible && hub.IsAncestorOf(f);  // the whole home screen, top bar included
        }

        // Spotlight is the screen showing, even with the focus nowhere useful (just after signing in it sat on nothing, or on
        // something hidden: the first LB / RB went to Playnite and did nothing)
        private static bool SpotlightShowing()
        {
            if (FocusOnHome()) return true;
            if (LoginScene.IsActive || SeleneShell.CollectionActive) return false;
            var w = Application.Current.MainWindow;
            if (w == null || !w.IsActive) return false;
            var f = Keyboard.FocusedElement as UIElement;
            if (f != null && Window.GetWindow(f) == w && f.IsVisible) return false;   // something real of the main window has it
            var hub = FindByName(w, "WelcomeHubOverlay");
            return hub != null && hub.IsVisible;
        }



        private static readonly HashSet<Window> ccHooked = new HashSet<Window>();
        private static readonly HashSet<Window> ccClosing = new HashSet<Window>();

        private static DateTime lastToolWindowAt = DateTime.MinValue;

        // a window of Playnite's other than the main one has the focus (a dialog)
        // Collection's list has nothing in it (no games, or none on this tab)
        public static bool CollectionEmpty()
        {
            var mw = Application.Current.MainWindow;
            var list = mw != null ? FindByName(mw, "PART_ListGameItems") as ItemsControl : null;
            return list != null && list.Items.Count == 0;
        }

        public static bool DialogUp()
        {
            try
            {
                var main = Application.Current.MainWindow;
                foreach (Window w in Application.Current.Windows)
                    if (!ReferenceEquals(w, main) && w.IsVisible && w.IsActive) return true;
            }
            catch { }
            return false;
        }

        private static string ForegroundWho()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static string ActiveWindowWho()
        {
            try
            {
                foreach (Window w in Application.Current.Windows)
                    if (!ReferenceEquals(w, Application.Current.MainWindow) && w.IsVisible && w.IsActive) return w.GetType().FullName + " \"" + w.Title + "\"";
            }
            catch { }
            return "?";
        }

        private static bool PlayniteInFront()
        {
            // (a hidden test desktop never has Windows' foreground window: there, Playnite's active window counts)
            if (Isolated && GetForegroundWindow() == IntPtr.Zero) { var mw = Application.Current.MainWindow; return mw != null && mw.IsActive; }
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }



        private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
        {
            var win = sender as Window;
            if (win != null && win != Application.Current.MainWindow) lastToolWindowAt = DateTime.Now;
            if (win == null || win == Application.Current.MainWindow || ccHooked.Contains(win)) return;
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ccHooked.Contains(win) || FindByName(win, "QuickAccessPanel") == null) return;
                ccHooked.Add(win);
                win.Closing += OnControlCenterClosing;
                LightenControlCenterDim(win);
                // opens on Power ; after the old theme's own FocusFirst has run
                var focusPower = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                focusPower.Tick += (s2, e2) =>
                {
                    focusPower.Stop();
                    var power = FindByName(win, "QuickAccessPowerButton") as UIElement;
                    if (power != null && power.IsVisible) { power.Focus(); Keyboard.Focus(power); }
                };
                focusPower.Start();
                win.Closed += (s, a) => { ccHooked.Remove(win); ccClosing.Remove(win); };
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // the old theme dims the whole screen behind the control centre (heavy); lighten it to a soft veil. The dim is either the
        // window background or a full-size dark layer behind the panel: both are found and logged.
        private static void LightenControlCenterDim(Window win)
        {
            try
            {
                var bg = win.Background as SolidColorBrush;
                Log("control centre window: transparent=" + win.AllowsTransparency + " background=" + (bg != null ? bg.Color.ToString() : (win.Background == null ? "null" : win.Background.GetType().Name)));
                if (win.AllowsTransparency && bg != null && bg.Color.A > 0x50)
                    win.Background = new SolidColorBrush(Color.FromArgb(0x50, bg.Color.R, bg.Color.G, bg.Color.B));
                var panel = FindByName(win, "QuickAccessPanel");
                var walk = new Stack<DependencyObject>();
                walk.Push(win);
                while (walk.Count > 0)
                {
                    var d0 = walk.Pop();
                    if (d0 is Visual) for (int i = VisualTreeHelper.GetChildrenCount(d0) - 1; i >= 0; i--) walk.Push(VisualTreeHelper.GetChild(d0, i));
                    var fe = d0 as FrameworkElement;
                    if (fe == null || !(fe is Border || fe is System.Windows.Shapes.Rectangle || fe is Grid || fe is ContentPresenter)) continue;
                    if (fe is ContentPresenter) continue;
                    if (panel != null && (panel.IsAncestorOf(fe) || ReferenceEquals(fe, panel))) continue;
                    if (fe.ActualWidth < win.ActualWidth * 0.9 || fe.ActualHeight < win.ActualHeight * 0.9) continue;
                    Brush b = fe is Border ? ((Border)fe).Background : fe is Grid ? ((Grid)fe).Background : ((System.Windows.Shapes.Rectangle)fe).Fill;
                    var sb = b as SolidColorBrush;
                    if (sb == null || sb.Color.A < 0x30 || sb.Color.R > 0x40 || sb.Color.G > 0x40 || sb.Color.B > 0x40) continue;
                    Log("control centre dim layer: " + fe.GetType().Name + " '" + fe.Name + "' " + sb.Color + " -> lighter");
                    fe.Opacity = Math.Min(fe.Opacity, 0.45);
                }
            }
            catch (Exception ex) { Log("control centre dim: " + ex.Message); }
        }

        private static void OnControlCenterClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var win = sender as Window;
            var panel = win != null ? FindByName(win, "QuickAccessPanel") as UIElement : null;
            if (panel == null || ccClosing.Contains(win)) return;   // second pass: really close
            e.Cancel = true;
            ccClosing.Add(win);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var slide = new TranslateTransform();
            panel.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 70, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
            fade.Completed += (s, a) => { try { win.Close(); } catch { } };
            panel.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static void RemoveFromRowAnimated(object game)
        {
            var row = LiveRow();
            if (row == null || games == null) return;
            int k = games.IndexOf(game);
            if (k < 0) return;
            object next = null;
            if (k + 1 < games.Count && !(games[k + 1] is RowPad)) next = games[k + 1];
            else if (k - 1 >= 0) next = games[k - 1];
            var c = row.ItemContainerGenerator.ContainerFromItem(game) as UIElement;
            Action finish = () =>
            {
                RebuildGames(row);
                row.UpdateLayout();
                int idx = next != null ? row.Items.IndexOf(next) : -1;
                if (idx < 0) idx = FirstGameIndex(row);
                if (idx < 0) return;
                row.SelectedIndex = idx;
                var sv = FindChild<ScrollViewer>(row);
                if (sv != null) sv.ScrollToHorizontalOffset(Math.Max(0, idx - AnchorSlot));
                row.UpdateLayout();
                var nc = row.ItemContainerGenerator.ContainerFromIndex(idx) as UIElement;
                if (nc != null) nc.Focus();
                EnsureTileCaches(row);
                ScheduleTitle(row, 200);
            };
            if (c == null || Lite) { finish(); return; }
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var shrink = new ScaleTransform(1, 1);
            c.RenderTransformOrigin = new Point(0.5, 0.5);
            c.RenderTransform = shrink;
            shrink.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            shrink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            fade.Completed += (s, a) => { c.BeginAnimation(UIElement.OpacityProperty, null); c.Opacity = 1; c.RenderTransform = null; finish(); };
            c.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // favorites and last played belong to the signed-in Selene profile (Playnite's own flags when nobody is signed in)
        private static bool IsFavoriteOf(object game)
        {
            var id = Prop(game, "Id");
            return id is Guid && Profiles.IsFavorite((Guid)id, Equals(Prop(game, "Favorite"), true));
        }

        private static DateTime? LastPlayedOf(object game)
        {
            var id = Prop(game, "Id");
            var playnite = Prop(game, "LastActivity") as DateTime?;
            return id is Guid ? Profiles.LastPlayed((Guid)id, playnite) : playnite;
        }

        // the heart on the home's Favourite pill (Main.xaml binds it to SeleneHeartGlyph)
        private static void ShowHeart(object game)
        {
            Application.Current.Resources["SeleneHeartGlyph"] = game != null && !(game is string) && IsFavoriteOf(game) ? "\uE00B" : "\uE006";
        }

        // another profile signed in: its favorites, recently played and order
        private static bool profileRebuild;

        // a profile is signing in (the row is rebuilt under the login): the home's own animations wait
        public static bool SigningIn { get { return coveredForSignIn || profileRebuild; } }

        public static void ProfileChanged()
        {
            startOnCards = true;
            var row = LiveRow();
            profileRebuild = true;
            try { if (row != null) { ApplyRowFilter(row); ShowHeart(row.SelectedItem); } }
            finally { profileRebuild = false; }
        }

        // Favourite pill: the signed-in profile's favorite (Playnite's Favorite flag when nobody is signed in)
        private static void ToggleFavorite(object game)
        {
            try
            {
                var id = Prop(game, "Id");
                if (!(id is Guid) || Api == null) return;
                bool now;
                if (Profiles.Active != null) now = Profiles.ToggleFavorite((Guid)id);
                else
                {
                    var g = Api.Database.Games.Get((Guid)id);
                    if (g == null) return;
                    g.Favorite = !g.Favorite;
                    Api.Database.Games.Update(g);
                    now = g.Favorite;
                }
                Log("favorite " + (now ? "on" : "off") + " -> " + Prop(game, "Name"));
                ShowHeart(game);
                if (!now && RowFilters[RowFilter] == "favorites") RemoveFromRowAnimated(game);
            }
            catch (Exception ex) { Log("favorite: " + ex.Message); }
        }
        private static DateTime pillLockUntil = DateTime.MinValue;
        private static WeakReference pillTarget;

        private static void OnWindowPreviewGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (DateTime.Now > pillLockUntil || pillTarget == null) return;
            var target = pillTarget.Target as Button;
            if (target == null || ReferenceEquals(e.NewFocus, target)) return;
            e.Handled = true;  // refuse the stray move
        }

        private static void ReleasePillLock() { pillLockUntil = DateTime.MinValue; pillTarget = null; }

        private static void FocusPill(Button b)
        {
            pillTarget = new WeakReference(b);
            pillLockUntil = DateTime.Now.AddMilliseconds(350);
            b.Focus();
            Keyboard.Focus(b);
            int n = 0;
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (s, e) =>
            {
                n++;
                // the user moved on (Up to the row, the other pill, A): stop, or focus bounces back to this pill
                if (pillTarget == null || !ReferenceEquals(pillTarget.Target, b)) { timer.Stop(); return; }
                if (!b.IsKeyboardFocused) { b.Focus(); Keyboard.Focus(b); }
                if (n >= 6) { timer.Stop(); Log("pill focus " + b.Name + ": focused=" + b.IsKeyboardFocused + " now=" + (Keyboard.FocusedElement == null ? "null" : Keyboard.FocusedElement.GetType().Name)); }
            };
            timer.Start();
        }

        private static void SetRowFocusLook(ListBox row, bool focused)
        {
            for (int i = 0; i < row.Items.Count; i++)
            {
                var c = row.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                if (c == null) continue;
                foreach (var name in new[] { "Ring", "Shine" })
                {
                    var el = FindByName(c, name);
                    if (el != null) el.Visibility = focused || !c.IsSelected ? Visibility.Visible : Visibility.Hidden;
                }
            }
        }

        // ---- News row: big cards fade out at the bottom; focusing one slides the page up ----
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> hookedNews =
            new System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object>();
        private const double NewsSlide = 190;

        private static void HookNewsSlide(ListBox row)
        {
            FrameworkElement cards = null, top = null;
            for (DependencyObject d = row; d != null && cards == null; d = VisualTreeHelper.GetParent(d))
            {
                cards = FindByName(d, "HubBottomCards") as FrameworkElement;
                if (cards != null) top = FindByName(d, "HubTopProfileSummary") as FrameworkElement;
            }
            object dummy;
            if (cards == null || top == null || hookedNews.TryGetValue(cards, out dummy)) return;
            hookedNews.Add(cards, null);
            var fadeStop = new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.92);
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.42));
            mask.GradientStops.Add(fadeStop);
            cards.OpacityMask = mask;
            // move with render transforms (GPU, no re-layout each frame) and fade the game row out on the way up
            var topShift = top.RenderTransform as TranslateTransform;
            if (topShift == null || topShift.IsFrozen) { topShift = new TranslateTransform(); top.RenderTransform = topShift; }
            var cardShift = cards.RenderTransform as TranslateTransform;
            if (cardShift == null || cardShift.IsFrozen) { cardShift = new TranslateTransform(); cards.RenderTransform = cardShift; }
            var rowIntro = FindByName(top, "SeleneRowIntro") as UIElement;
            cards.IsKeyboardFocusWithinChanged += (s, a) =>
            {
                bool on = (bool)a.NewValue;
                var win = Window.GetWindow(cards);
                if (win != null && !win.IsActive) return;
                if (on) HidePreview();
                var dur = TimeSpan.FromMilliseconds(460);
                var ease = new QuinticEase { EasingMode = EasingMode.EaseInOut };
                double y = on ? -NewsSlide : 0;
                topShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, dur) { EasingFunction = ease });
                cardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, dur) { EasingFunction = ease });
                fadeStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(on ? Colors.Black : Color.FromArgb(0, 0, 0, 0), dur) { EasingFunction = ease });
                if (rowIntro != null)
                {
                    var fade = new DoubleAnimation(on ? 0 : 1, TimeSpan.FromMilliseconds(on ? 260 : 380)) { BeginTime = TimeSpan.FromMilliseconds(on ? 0 : 120), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    if (!on) fade.Completed += (s2, e2) => rowIntro.BeginAnimation(UIElement.OpacityProperty, null);
                    rowIntro.BeginAnimation(UIElement.OpacityProperty, fade);
                }
            };
            Log("news slide hooked");
        }

        // ---- library refresh: Playnite only checks its libraries about weekly, and the old theme's menu has no "Update library" ----
        // Runs Playnite's own update-games command (what its menus use) on start and whenever Playnite comes back from the
        // tray, at most every 10 minutes, so a game installed in Epic/Steam/Xbox appears without touching a menu.
        private static DateTime lastLibraryRefresh = DateTime.MinValue;

        // the menu's Update library: starts Playnite's update now (no 10-minute limit); false with Playnite's status when one
        // is already running (Playnite would only wait behind it)
        public static bool UpdateLibraryNow(out string status)
        {
            status = "";
            var w = Application.Current.MainWindow;
            var vm = w != null ? w.DataContext : null;
            if (vm == null) { status = "Playnite isn't ready"; return false; }
            var active = Prop(vm, "ProgressActive");
            if (active is bool && (bool)active)
            {
                status = Prop(vm, "ProgressStatus") as string;
                if (string.IsNullOrEmpty(status)) status = "Playnite is busy";
                Log("library update: already running (" + status + ")");
                return false;
            }
            lastLibraryRefresh = DateTime.MinValue;
            RefreshLibrary("menu");
            return true;
        }

        public static void RefreshLibrary(string why)
        {
            try
            {
                if ((DateTime.Now - lastLibraryRefresh).TotalMinutes < 10) return;
                // the automatic ones never under the login / profile setup (add-ons' progress screens would cover it)
                if (why != "menu" && LoginScene.InSetup) { Log("library refresh (" + why + ") skipped: login is up"); return; }
                LocalGames.Scan();  // games installed outside a store (the game folders) -> library, before the refresh's new-game setup
                var w = Application.Current.MainWindow;
                var vm = w != null ? w.DataContext : null;
                if (vm == null) return;
                ICommand cmd = null;
                foreach (var name in new[] { "UpdateGamesCommand", "UpdateLibraryCommand" })
                {
                    var pi = vm.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    cmd = pi != null ? pi.GetValue(vm, null) as ICommand : null;
                    if (cmd != null) break;
                }
                if (cmd == null)
                {
                    // Fullscreen mode has no command for it; Playnite's base view model has UpdateLibrary(metadata, partial, checkUpdates)
                    var sigs = new System.Text.StringBuilder();
                    foreach (var m in vm.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name != "UpdateLibrary") continue;
                        var ps = m.GetParameters();
                        sigs.Append("(" + string.Join(",", Array.ConvertAll(ps, x => x.ParameterType.Name + " " + x.Name)) + ") ");
                        if (ps.Length == 3 && Array.TrueForAll(ps, x => x.ParameterType == typeof(bool)))
                        {
                            lastLibraryRefresh = DateTime.Now;
                            // (metaForNewGames, updateIntegrations, updateEmu): covers/metadata for new games, store libraries, no emulators
                            m.Invoke(vm, new object[] { true, true, false });
                            Log("library refresh started via UpdateLibrary(" + string.Join(",", Array.ConvertAll(ps, x => x.Name)) + ") (" + why + ")");
                            return;
                        }
                    }
                    Log("library refresh: no usable UpdateLibrary: " + sigs);
                    return;
                }
                if (!cmd.CanExecute(null)) { Log("library refresh: busy (" + why + ")"); return; }
                lastLibraryRefresh = DateTime.Now;
                cmd.Execute(null);
                Log("library refresh started (" + why + ")");
            }
            catch (Exception ex) { Log("library refresh failed: " + ex.Message); }
        }

        private static void HookMoveCommand(ListBox row)
        {
            row.CommandBindings.Add(new CommandBinding(ApplicationCommands.Properties, (s, e) =>
            {
                var item = Keyboard.FocusedElement as ListBoxItem;
                var name = item != null ? item.DataContext as string : null;
                if (name == null || !name.StartsWith("nav:")) return;
                movingCard = movingCard == null ? name : null;
                SetMoveVisual(item, movingCard != null);
            }));
        }

        // Move mode: the card drops down (no fading, it looked like it vanished) and the title becomes a hint.
        // for the home's button hints
        internal static bool Moving { get { return movingCard != null || movingGame != null; } }
        internal static bool InRow(DependencyObject d) { return IsInRow(d); }
        internal static bool HasTrailer(object game) { return CardVideo(game) != null; }
        // R3 has something to switch on: a full trailer (preview clips and Extra Metadata's short clips are silent)
        internal static bool HasTrailerSound(object game) { return game != null && !(game is string) && FullTrailerPath(game) != null; }

        // a trailer just downloaded for the selected game: its card opens after the usual wait, and its hints learn R3
        public static void TrailerReady(Guid id)
        {
            var row = LiveRow();
            if (row == null || previewShowing || !row.IsVisible || row.SelectedItem == null) return;
            var gid = Prop(row.SelectedItem, "Id");
            if (gid == null || !id.Equals(gid)) return;
            ArmPreview(row);
            HomeHints.Update();
        }

        // what a trailer card plays: a silent preview clip while the sound is off, if one was made; else the game's
        // trailer (Selene's download or Extra Metadata Loader's), its sound following the R3 switch
        private static string CardVideo(object game)
        {
            if (game == null || game is string) return null;
            var clip = TrailerPath(game);
            return (clip != null && !previewSound ? clip : FullTrailerPath(game) ?? clip) ?? MicroTrailerPath(game);   // Extra Metadata's short clip last (#28)
        }

        private static void SetMoveVisual(ListBoxItem item, bool on)
        {
            item.Dispatcher.BeginInvoke(new Action(HomeHints.Update), System.Windows.Threading.DispatcherPriority.Background);
            item.BeginAnimation(UIElement.OpacityProperty, null);
            item.Opacity = 1;
            var lift = item.RenderTransform as TransformGroup;
            TranslateTransform shift = null;
            if (lift != null) foreach (var tr in lift.Children) if (tr is TranslateTransform) shift = (TranslateTransform)tr;
            if (shift == null || shift.IsFrozen)
            {
                var g = new TransformGroup();
                g.Children.Add(new ScaleTransform(1, 1));
                shift = new TranslateTransform();
                g.Children.Add(shift);
                item.RenderTransform = g;
            }
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? 16 : 0, TimeSpan.FromMilliseconds(160)));

            var row = FindRow(item);
            var title = row != null ? FindTitle(row) as TextBlock : null;
            if (title == null) return;
            if (on)
            {
                // [<][>] Move   (Y) Done - white button glyphs like the Play / Details pills
                title.Inlines.Clear();
                title.Inlines.Add(new System.Windows.Documents.InlineUIContainer(Keycap("\u25C0", false)) { BaselineAlignment = BaselineAlignment.Center });
                title.Inlines.Add(new System.Windows.Documents.InlineUIContainer(Keycap("\u25B6", false)) { BaselineAlignment = BaselineAlignment.Center });
                title.Inlines.Add(new System.Windows.Documents.Run(" Move      "));
                title.Inlines.Add(new System.Windows.Documents.InlineUIContainer(Keycap("Y", true)) { BaselineAlignment = BaselineAlignment.Center });
                title.Inlines.Add(new System.Windows.Documents.Run(" Done"));
                title.BeginAnimation(UIElement.OpacityProperty, null);
                title.Opacity = 1;
                PlaceHint(row, item);
            }
            else
            {
                title.Inlines.Clear();
                title.ClearValue(TextBlock.TextProperty);
                PlaceTitle(row, item, 0);
            }
        }

        // Move hint sits under the held (zoomed) tile, placed without any fade so it stays steady while moving.
        private static void PlaceHint(ListBox row, FrameworkElement container)
        {
            var title = FindTitle(row);
            var canvas = title != null ? VisualTreeHelper.GetParent(title) as UIElement : null;
            var tile = FindByName(container, "Tile");
            if (title == null || canvas == null || tile == null) return;
            var p = RestingPoint(tile, container, canvas);
            Canvas.SetLeft(title, p.X + 2);
            Canvas.SetTop(title, p.Y + ZoomedTile + 16 + 22);  // below the dropped (move mode) tile and its ring
        }

        // Tile position without the container's animated offsets (move-mode drop / intro), so the name never lands
        // in a half-animated spot.
        private static double RestingLeftRaw(FrameworkElement tile, UIElement canvas) { return tile.TranslatePoint(new Point(0, 0), canvas).X; }

        private static Point RestingPoint(FrameworkElement tile, FrameworkElement container, UIElement canvas)
        {
            var p = tile.TranslatePoint(new Point(0, 0), canvas);
            var group = container.RenderTransform as TransformGroup;
            if (group != null)
                foreach (var tr in group.Children)
                {
                    var tt = tr as TranslateTransform;
                    if (tt != null) { p.X -= tt.X; p.Y -= tt.Y; }
                }
            return p;
        }

        // While moving, keep the held item in the anchor slot like normal navigation (no jump afterwards).
        private static void KeepAnchored(ListBox row, ListBoxItem item)
        {
            var sv = FindChild<ScrollViewer>(row);
            int idx = row.ItemContainerGenerator.IndexFromContainer(item);
            if (sv != null && idx >= 0) sv.ScrollToHorizontalOffset(Math.Max(0, idx - AnchorSlot));
            row.UpdateLayout();
        }

        private static FrameworkElement Keycap(string glyph, bool round)
        {
            var text = new TextBlock
            {
                Text = glyph, Foreground = Ink.Brush("SeleneScrimColor"), FontWeight = FontWeights.Bold,
                FontSize = round ? 16 : 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            return new Border
            {
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 6, 0), Background = Ink.Brush("SeleneInkColor"),
                CornerRadius = new CornerRadius(round ? 14 : 7), Child = text
            };
        }

        private static void MoveCard(ListBox row, ListBoxItem item, int dir)
        {
            var items = row.ItemsSource as System.Windows.Data.CompositeCollection;
            if (items == null) return;
            int i = items.IndexOf(movingCard);
            int j = i + dir;
            if (i < 0 || j < 0 || j >= items.Count || !(items[j] is string)) return;
            // Move the neighbour, never the selected card: removing the selected item re-enters Selector and throws.
            var other = items[j];
            items.RemoveAt(j);
            items.Insert(i, other);
            row.UpdateLayout();
            KeepAnchored(row, item);
            item.Focus();
            PlaceHint(row, item);
            SaveCardOrder(items);
        }

        // ---- games: own list so the user can pin an order ----
        private static object movingGame;
        private static System.Collections.ObjectModel.ObservableCollection<object> games;
        private static IEnumerable gameSource;
        private static System.Windows.Data.CollectionContainer gameContainer;
        private static readonly string GameOrderFile = System.IO.Path.Combine(SelenePaths.PlayniteConfig, "selene-game-order.txt");

        private static void UseOwnGameList(ListBox row)
        {
            var items = row.ItemsSource as System.Windows.Data.CompositeCollection;
            if (items == null) return;
            foreach (var o in items)
            {
                var cc = o as System.Windows.Data.CollectionContainer;
                if (cc == null) continue;
                var view = cc.Collection as System.ComponentModel.ICollectionView;
                gameSource = view != null ? view.SourceCollection : cc.Collection;
                games = new System.Collections.ObjectModel.ObservableCollection<object>();
                cc.Collection = games;
                gameContainer = cc;
                RebuildGames(row);
                Log("own game list: " + games.Count + " games");
                return;
            }
        }

        private static object Prop(object o, string name)
        {
            var pi = o != null ? o.GetType().GetProperty(name) : null;
            return pi != null ? pi.GetValue(o, null) : null;
        }

        // ---- home-row filter (LT / RT): All, Installed, Recently played, Favorites, Not played yet ----
        private static readonly string[] BuiltInFilters = { "all", "installed", "recent", "favorites", "unplayed" };
        private static readonly string[] BuiltInFilterLabels = { "All games", "Installed", "Recently played", "Favorites", "Not played yet" };
        private static string[] RowFilters = BuiltInFilters;
        private static string[] RowFilterLabels = BuiltInFilterLabels;
        private const string PresetKey = "preset:";
        private static readonly Dictionary<Guid, int> presetGames = new Dictionary<Guid, int>();   // game -> its place in Playnite's order for the preset
        private static Guid presetGamesFor = Guid.Empty;
        private static bool presetsWatched;

        // the tabs: the five built in, then the user's own Playnite filter presets (not Selene's, by name), when the switch is on
        public static void RefreshRowFilters()
        {
            var mode = Features.Value("filtertabs");   // selene / playnite / both
            var keys = new List<string>();
            var labels = new List<string>();
            if (mode != "playnite") { keys.AddRange(BuiltInFilters); labels.AddRange(BuiltInFilterLabels); }
            try
            {
                if (Api != null && mode != "selene")
                {
                    var own = new HashSet<Guid>(BuiltInFilters.Select(CollectionPresets.For));
                    own.Add(BigScreenPreset);   // Selene's own working preset
                    foreach (var fp in Api.Database.FilterPresets.OrderBy(p => p.Name))
                    {
                        if (own.Contains(fp.Id) || string.IsNullOrWhiteSpace(fp.Name)) continue;
                        keys.Add(PresetKey + fp.Id);
                        labels.Add(fp.Name);
                    }
                    if (!presetsWatched)
                    {
                        presetsWatched = true;
                        Api.Database.FilterPresets.ItemCollectionChanged += (s2, e2) => Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            RefreshRowFilters();
                            var w = Application.Current.MainWindow;
                            if (w != null) RenderFilterTabs(w, 0);
                        }));
                    }
                }
            }
            catch (Exception ex) { Log("filter presets: " + ex.Message); }
            if (keys.Count == 0) { keys.AddRange(BuiltInFilters); labels.AddRange(BuiltInFilterLabels); }   // no presets of your own yet
            var current = rowFilter >= 0 && rowFilter < RowFilters.Length ? RowFilters[rowFilter] : null;
            RowFilters = keys.ToArray();
            RowFilterLabels = labels.ToArray();
            if (current != null) rowFilter = Math.Max(0, Array.IndexOf(RowFilters, current));
        }

        private static bool IsPresetTab(string key) { return key != null && key.StartsWith(PresetKey); }
        private static int PresetPlace(object game) { var id = Prop(game, "Id"); int i; return id is Guid && presetGames.TryGetValue((Guid)id, out i) ? i : int.MaxValue; }
        private static Guid PresetOf(string key) { Guid id; return IsPresetTab(key) && Guid.TryParse(key.Substring(PresetKey.Length), out id) ? id : Guid.Empty; }

        // a preset tab: Playnite applies the preset (as Collection does) and the row shows the games it leaves
        private static void PreparePresetFilter()
        {
            var id = PresetOf(RowFilters[RowFilter]);
            if (id == Guid.Empty || Api == null) { presetGamesFor = Guid.Empty; return; }
            try
            {
                if (Api.MainView.GetActiveFilterPreset() != id) Api.MainView.ApplyFilterPreset(id);
                presetGames.Clear();
                // Playnite's filtered list comes in the preset's own sorting (Newest = by date added...): kept for the row (#23)
                int place = 0;
                foreach (var g in Api.MainView.FilteredGames) if (!presetGames.ContainsKey(g.Id)) presetGames[g.Id] = place++;
                presetGamesFor = id;
            }
            catch (Exception ex) { Log("filter preset: " + ex.Message); }
        }
        private static readonly string RowFilterFile = System.IO.Path.Combine(SelenePaths.PlayniteConfig, "selene-row-filter.txt");
        private static int rowFilter = -1;
        private static Border filterPill;
        private static TextBlock filterPillText;

        private static int RowFilter
        {
            get
            {
                if (rowFilter >= RowFilters.Length) rowFilter = 0;   // a preset tab that's gone
                if (rowFilter < 0)
                {
                    rowFilter = 0;
                    var start = Features.Value("starttab");   // the Starting tab setting, else where it was left
                    if (start != "last" && start != "") rowFilter = Math.Max(0, Array.IndexOf(RowFilters, start));
                    else try { if (System.IO.File.Exists(RowFilterFile)) rowFilter = Math.Max(0, Array.IndexOf(RowFilters, System.IO.File.ReadAllText(RowFilterFile).Trim())); } catch { }
                }
                return rowFilter;
            }
        }

        private static bool PassesRowFilter(object game)
        {
            if (game == null || game is string || game is RowPad) return true;
            var key = RowFilters[RowFilter];
            if (IsPresetTab(key))
            {
                if (presetGamesFor != PresetOf(key)) PreparePresetFilter();
                var idObj = Prop(game, "Id");
                return idObj is Guid && presetGames.ContainsKey((Guid)idObj);
            }
            switch (key)
            {
                case "installed": return Equals(Prop(game, "IsInstalled"), true);
                case "recent": { var la = LastPlayedOf(game); return la.HasValue && la.Value > DateTime.Now.AddDays(-30); }
                case "favorites": return IsFavoriteOf(game);
                case "unplayed": { var pt = Prop(game, "Playtime"); return pt == null || Convert.ToUInt64(pt) == 0; }
                default: return true;
            }
        }

        // LT/RT: the row is rebuilt on every press (~10-40 ms now that tiles are light). Grouping quick presses made the
        // filter look selected twice when the delayed rebuild landed after the burst.
        private static int filterDir;

        private static void CycleRowFilter(int dir)
        {
            var row = LiveRow();
            if (row == null) return;
            rowFilter = (RowFilter + dir + RowFilters.Length) % RowFilters.Length;
            try { System.IO.File.WriteAllText(RowFilterFile, RowFilters[rowFilter]); } catch { }
            filterDir = dir;
            ApplyRowFilter(row);
        }

        // The row keeps 12 tiles behind / 16 ahead built (CacheLength) so moving is smooth, but building all ~40 at once was
        // the ~105 ms switch freeze. On a switch only the visible tiles (+ a few) are built; the cache grows back over the
        // next frames, a few tiles per step.
        private static System.Windows.Threading.DispatcherTimer cacheGrow;
        private static readonly VirtualizationCacheLength[] cacheSteps =
        {
            new VirtualizationCacheLength(2, 3), new VirtualizationCacheLength(3, 4), new VirtualizationCacheLength(4, 5),
            new VirtualizationCacheLength(5, 6), new VirtualizationCacheLength(6, 7), new VirtualizationCacheLength(7, 8),
            new VirtualizationCacheLength(8, 9), new VirtualizationCacheLength(9, 10), new VirtualizationCacheLength(10, 11),
            new VirtualizationCacheLength(11, 12), new VirtualizationCacheLength(12, 13), new VirtualizationCacheLength(12, 14),
            new VirtualizationCacheLength(12, 15), new VirtualizationCacheLength(12, 16),
        };
        private static int cacheStep;

        private static void ShrinkRowCache(ListBox row)
        {
            if (cacheGrow != null) cacheGrow.Stop();
            VirtualizingPanel.SetCacheLength(row, new VirtualizationCacheLength(1, 2));
            cacheStep = 0;
            var rowRef = new WeakReference(row);
            if (cacheGrow == null)
            {
                cacheGrow = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
                cacheGrow.Tick += (s, e) =>
                {
                    var r = cacheGrow.Tag as WeakReference;
                    var lb = r != null ? r.Target as ListBox : null;
                    if (lb == null || cacheStep >= cacheSteps.Length) { cacheGrow.Stop(); return; }
                    if (!lb.IsVisible || LoginScene.CoversHome || LoginScene.StartingUp || IntroRunning) return;
                    VirtualizingPanel.SetCacheLength(lb, cacheSteps[cacheStep++]);
                    if (cacheStep == cacheSteps.Length) { cacheGrow.Stop(); lb.Dispatcher.BeginInvoke(new Action(() => EnsureTileCaches(lb)), System.Windows.Threading.DispatcherPriority.Background); }
                };
            }
            cacheGrow.Tag = rowRef;
            cacheGrow.Interval = TimeSpan.FromMilliseconds(350);  // first step after the switch animation has started
            cacheGrow.Start();
            EventHandler fast = null;
            fast = (s, e) => { cacheGrow.Tick -= fast; cacheGrow.Interval = TimeSpan.FromMilliseconds(70); };
            cacheGrow.Tick += fast;
        }

        private static void ApplyRowFilter(ListBox row)
        {
            int dir = filterDir;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            PreparePresetFilter();
            HidePreview(false, 0);
            ShrinkRowCache(row);
            RebuildGames(row);
            row.UpdateLayout();
            int idx = FirstGameIndex(row);
            if (idx >= 0)
            {
                row.SelectedIndex = idx;
                var sv = FindChild<ScrollViewer>(row);
                if (sv != null) sv.ScrollToHorizontalOffset(Math.Max(0, idx - AnchorSlot));
                row.UpdateLayout();
                var c = row.ItemContainerGenerator.ContainerFromIndex(idx) as UIElement;
                if (c != null) c.Focus();
                EnsureTileCaches(row);
                ScheduleTitle(row, 230);
            }
            RenderFilterTabs(Window.GetWindow(row), dir);
            PlayFilterSwitch(row, dir);
            Log("row filter -> " + RowFilters[rowFilter] + " (" + games.Count + " games) " + sw.ElapsedMilliseconds + " ms");
            try
            {
                Log("row order: " + string.Join(" | ", games.Where(g => !(g is string) && !(g is RowPad)).Take(6).Select(g => Convert.ToString(Prop(g, "Name")))));
                if (IsPresetTab(RowFilters[rowFilter]) && Api != null)
                    Log("preset order (Playnite): " + string.Join(" | ", Api.MainView.FilteredGames.Take(6).Select(g => g.Name)));
            }
            catch { }
        }

        private static object FindWithProperty(object root, string prop, int depth)
        {
            if (root == null) return null;
            var type = root.GetType();
            if (type.GetProperty(prop) != null) return root;
            if (depth <= 0 || type.Namespace == "System") return null;
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var f in type.GetFields(any))
            {
                if (f.FieldType.IsValueType || f.FieldType == typeof(string)) continue;
                object v; try { v = f.GetValue(root); } catch { continue; }
                var hit = FindWithProperty(v, prop, depth - 1);
                if (hit != null) return hit;
            }
            return null;
        }

        private static bool TriggerHeld()
        {
            if (Isolated) return false;
            for (int i = 0; i < 4; i++)
            {
                XInputStateEx s;
                try { if (XInputGetStateEx(i, out s) == 0 && (s.LT > 60 || s.RT > 60)) return true; } catch { return false; }
            }
            return false;
        }


        // Filter tabs in the home top bar (left slot, SeleneFilterTabs): LT / RT badges around the
        // five filters, the active one on a white pill (console tab look), the others dimmed.
        // New set of cards: they fade in and slide a little from the side the filter moved to (0.26 s, eased).
        private static void PlayFilterSwitch(ListBox row, int dir)
        {
            if (Lite) return;
            var ip = FindChild<ItemsPresenter>(row);
            var target = (UIElement)FindChild<VirtualizingStackPanel>(row) ?? ip;
            if (target == null) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            // the panel's own TranslateTransform carries the row slide: offset that, the spring brings it home
            var shift = target.RenderTransform as TranslateTransform;
            if (shift == null || shift.IsFrozen) { shift = new TranslateTransform(); target.RenderTransform = shift; }
            Glide(shift, 60 * dir, false);
        }

        private static void RenderFilterTabs(Window w) { RenderFilterTabs(w, 0); }

        private static void RenderFilterTabs(Window w, int dir)
        {
            var host = w != null ? FindByName(w, "SeleneFilterTabs") as StackPanel : null;
            if (host == null) return;
            host.Children.Clear();
            host.Children.Add(FilterBadge("LB"));
            for (int i = 0; i < RowFilters.Length; i++)
            {
                bool on = i == RowFilter;
                var label = new TextBlock
                {
                    Text = RowFilterLabels[i], FontSize = 19, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                    FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.7)
                };
                var tab = new Border
                {
                    Child = label, CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 5, 20, 7), Margin = new Thickness(2, 0, 2, 0),
                    Background = on ? (Brush)Ink.Brush("SeleneFocusColor", 0.95) : Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5)
                };
                host.Children.Add(tab);
                if (on && dir != 0)
                {
                    var pop = new ScaleTransform(0.86, 0.86);
                    tab.RenderTransform = pop;
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                    pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                    tab.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(160)));
                }
            }
            host.Children.Add(FilterBadge("RB"));
        }

        internal static Border FilterBadge(string text)
        {
            var label = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Ink.Brush("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) };
            var badge = new Border
            {
                // filled, like the button hints (white with dark letters)
                Child = label,
                Width = 36, Height = 24, CornerRadius = new CornerRadius(8), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Ink.Brush("SeleneInkColor")
            };
            Ui.LiveHint(badge, () => { label.Text = Ui.PromptLabel(text); badge.Width = Math.Max(36, label.Text.Length * 8 + 12); });
            return badge;
        }

        // console-style pill just above the row; stays while a filter is on, flashes and fades for "All games"
        private static void ShowFilterPill(ListBox row, bool changed)
        {
            var host = FindByName(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(row)) ?? row, "SeleneRowIntro") as Grid;
            if (host == null) host = VisualTreeHelper.GetParent(row) as Grid;
            if (host == null) return;
            if (filterPill == null || VisualTreeHelper.GetParent(filterPill) != host)
            {
                if (filterPill != null) { var old = VisualTreeHelper.GetParent(filterPill) as Panel; if (old != null) old.Children.Remove(filterPill); }
                filterPillText = new TextBlock { Foreground = Ink.Brush("SeleneInkColor"), FontSize = 17, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                var hint = new TextBlock { Text = "LT \u25C2  \u25B8 RT", Foreground = Ink.Brush("SeleneInkColor", 0.7), FontSize = 13, Margin = new Thickness(14, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(filterPillText);
                sp.Children.Add(hint);
                filterPill = new Border
                {
                    Child = sp, CornerRadius = new CornerRadius(17), Padding = new Thickness(16, 5, 16, 6),
                    Background = Ink.Brush("SeleneGlassColor", 0.45),
                    BorderBrush = Ink.Brush("SeleneInkColor", 0.25), BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(8, -40, 0, 0), IsHitTestVisible = false, Opacity = 0
                };
                Panel.SetZIndex(filterPill, 50);
                host.Children.Add(filterPill);
            }
            filterPillText.Text = RowFilterLabels[RowFilter] + "  \u00B7  " + games.Count;
            bool keep = RowFilter != 0;
            if (!changed) { filterPill.BeginAnimation(UIElement.OpacityProperty, null); filterPill.Opacity = keep ? 1 : 0; return; }
            filterPill.BeginAnimation(UIElement.OpacityProperty, keep ? (AnimationTimeline)
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
                : Keys(1, 0, 1.4, 0.4, null));
        }


        // a tab with no games says so (and how it fills up) next to the cards
        private static void ShowRowEmpty(ListBox row, bool empty)
        {
            var w = Window.GetWindow(row);
            var box = w != null ? FindByName(w, "SeleneRowEmpty") : null;
            if (box == null) return;
            if (!empty) { box.Visibility = Visibility.Collapsed; return; }
            string title, hint;
            switch (RowFilters[RowFilter])
            {
                case "installed": title = "Nothing installed"; hint = "Install a game from the Library or a store and it shows up here."; break;
                case "recent": title = "Nothing played lately"; hint = "Games you play show up here for a month."; break;
                case "favorites": title = "No favorites yet"; hint = "Press X on a game to add it to your favorites."; break;
                case "unplayed": title = "You've started everything"; hint = "Every game in your library has been played at least once."; break;
                default: title = "No games yet"; hint = "Connect a store in Playnite's settings, or run the setup check: Menu, Extensions, Selene."; break;
            }
            var titleBlock = (TextBlock)FindByName(box, "SeleneRowEmptyTitle");
            // the row is rebuilt more than once on a tab switch: the same message already showing stays put (no second slide-in),
            // still following the row
            if (box.Visibility == Visibility.Visible && titleBlock.Text == title)
            {
                emptyRow = row; emptyBox = box;
                System.Windows.Media.CompositionTarget.Rendering -= OnFollowRowEmpty;
                System.Windows.Media.CompositionTarget.Rendering += OnFollowRowEmpty;
                return;
            }
            titleBlock.Text = title;
            ((TextBlock)FindByName(box, "SeleneRowEmptyHint")).Text = hint;
            // it sits in the row, so it moves with the row's own filter-switch animation (a second one of its own doubled it)
            bool wasShown = box.Visibility == Visibility.Visible;
            box.BeginAnimation(UIElement.OpacityProperty, null);
            box.Opacity = 1;
            box.RenderTransform = null;
            box.Visibility = Visibility.Visible;
            FollowRowEmpty(row, box);
            if (!wasShown) RowEmptyIn(0.12);   // never just pops in
        }

        // the empty-tab message rises and fades in (after a delay in seconds), like the cards do
        private static void RowEmptyIn(double delay)
        {
            var box = emptyBox;
            if (box == null || box.Visibility != Visibility.Visible) return;
            var inner = VisualTreeHelper.GetChildrenCount(box) > 0 ? VisualTreeHelper.GetChild(box, 0) as FrameworkElement : null;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            box.BeginAnimation(UIElement.OpacityProperty, Keys(0, 1, delay, 0.45, ease));
            if (inner != null)
            {
                var rise = new TranslateTransform();
                inner.RenderTransform = rise;
                rise.BeginAnimation(TranslateTransform.YProperty, Keys(16, 0, delay, 0.55, ease));
            }
        }

        // the message stays beside the row's last card, which slides as the row scrolls and glides: while it shows, it is put
        // next to the first spacer after the cards on every frame (a spot worked out once ended up on the cards or far off)
        private static ListBox emptyRow;
        private static FrameworkElement emptyBox;
        private static TranslateTransform emptyAt;

        private static void FollowRowEmpty(ListBox row, FrameworkElement box)
        {
            emptyRow = row;
            emptyBox = box;
            emptyAt = new TranslateTransform();
            box.Margin = new Thickness(0);
            box.RenderTransform = emptyAt;
            // room around the words for the readable glow (the box sits 36 further left to keep the words in place)
            var inner = VisualTreeHelper.GetChildrenCount(box) > 0 ? VisualTreeHelper.GetChild(box, 0) as FrameworkElement : null;
            if (inner != null) inner.Margin = new Thickness(36, 22, 36, 22);
            AdaptiveText.Watch(box, Contrast.Behind);
            System.Windows.Media.CompositionTarget.Rendering -= OnFollowRowEmpty;
            System.Windows.Media.CompositionTarget.Rendering += OnFollowRowEmpty;
            OnFollowRowEmpty(null, EventArgs.Empty);
        }

        private static void OnFollowRowEmpty(object sender, EventArgs e)
        {
            try
            {
                var box = emptyBox; var row = emptyRow;
                // gone for good (the tab has games again): stop; only out of sight for now (the login over the home): wait
                if (box == null || row == null || box.Visibility != Visibility.Visible)
                {
                    System.Windows.Media.CompositionTarget.Rendering -= OnFollowRowEmpty;
                    return;
                }
                if (!box.IsVisible) return;
                var parent = VisualTreeHelper.GetParent(box) as Visual;
                FrameworkElement pad = null;
                for (int i = 0; i < row.Items.Count; i++)
                    if (row.Items[i] is RowPad) { pad = row.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement; break; }
                if (parent == null || pad == null || pad.ActualWidth < 1 || box.ActualHeight < 1) return;
                var at = pad.TransformToVisual(parent).Transform(new Point(0, pad.ActualHeight / 2));
                double x = at.X + 4, y = at.Y - box.ActualHeight / 2;
                if (Math.Abs(emptyAt.X - x) > 0.25) emptyAt.X = x;
                if (Math.Abs(emptyAt.Y - y) > 0.25) emptyAt.Y = y;
            }
            catch { }
        }

        // Collection with no games at all: its filter tabs have nothing to filter and hide (Spotlight keeps them; so does a
        // Collection tab that's only empty because of its filter)
        private static int libraryGames = -1;
        public static void UpdateFilterTabs()
        {
            var mw = Application.Current.MainWindow;
            var tabs = mw != null ? FindByName(mw, "SeleneFilterTabs") as UIElement : null;
            if (tabs != null) tabs.Visibility = SeleneShell.CollectionActive && libraryGames == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // the numbers on the Library and Store cards
        private static void UpdateCardStats()
        {
            int all = 0, installed = 0, recent = 0;
            foreach (var g in gameSource)
            {
                if (!IsShownBase(g)) continue;
                all++;
                if (Equals(Prop(g, "IsInstalled"), true)) installed++;
                var la = LastPlayedOf(g);
                if (la.HasValue && la.Value > DateTime.Now.AddDays(-30)) recent++;
            }
            var res = Application.Current.Resources;
            libraryGames = all;
            UpdateFilterTabs();
            res["SeleneStatGames"] = all.ToString();
            res["SeleneStatInstalled"] = installed.ToString();
            res["SeleneStatRecent"] = recent.ToString();
            GameFacts.CardStats();
            if (!res.Contains("SeleneStatStoresSet")) { res["SeleneStatStores"] = StoreScreen.InstalledNames(); res["SeleneStatStoresSet"] = true; }
        }

        private static void RebuildGames(ListBox row)
        {
            if (games == null || gameSource == null) return;
            var pinned = new System.Collections.Generic.List<string>();
            try { if (System.IO.File.Exists(GameOrderFile)) pinned.AddRange(System.IO.File.ReadAllLines(GameOrderFile)); } catch { }
            var list = new System.Collections.Generic.List<object>();
            foreach (var g in gameSource) if (IsShown(g)) list.Add(g);
            // Recently played is strictly last-played first (no manual order there)
            if (RowFilters[RowFilter] == "recent") pinned.Clear();
            // one of Playnite's own filter presets: the row follows the order Playnite sorted it in (#23)
            bool presetOrder = IsPresetTab(RowFilters[RowFilter]) && presetGamesFor != Guid.Empty && presetGamesFor == PresetOf(RowFilters[RowFilter]);
            if (presetOrder) pinned.Clear();
            list = GameRowOrder.Sort(list, pinned, g => Convert.ToString(Prop(g, "Id")), LastPlayedOf,
                g => Convert.ToString(Prop(g, "Name")), presetOrder ? (Func<object, int>)PresetPlace : null);
            ShowRowEmpty(row, list.Count == 0);
            UpdateCardStats();
            // console: the selection keeps its slot to the very end, empty space opens on the right. Without padding the row hit
            // its scroll limit, the selection walked to the edge and the limit shifted with the zoomed card (jiggle).
            list.AddRange(RowPads);
            bool same = list.Count == games.Count;
            for (int i = 0; same && i < list.Count; i++) same = ReferenceEquals(list[i], games[i]);
            if (same) return;
            // nothing in the row may hold focus/selection while the list is replaced (Selector re-entrancy)
            if (row.IsKeyboardFocusWithin) Keyboard.ClearFocus();
            row.SelectedIndex = -1;
            // one swap instead of Clear + 90 Adds (each Add re-laid the row out: the filter switch stuttered). Tiles are built
            // fresh; they are light now (no per-tile video player, cached cover).
            if (gameContainer != null)
            {
                games = new System.Collections.ObjectModel.ObservableCollection<object>(list);
                gameContainer.Collection = games;
            }
            else
            {
                games.Clear();
                foreach (var g in list) games.Add(g);
            }
        }

        private static void MoveGame(ListBox row, ListBoxItem item, int dir)
        {
            int k = games.IndexOf(movingGame), n = k + dir;
            if (k < 0 || n < 0 || n >= games.Count || games[n] is RowPad) return;
            games.Move(n, k);  // move the neighbour; the selected game is never removed
            row.UpdateLayout();
            KeepAnchored(row, item);
            item.Focus();
            PlaceHint(row, item);
            // pin everything from the start of the row up to the moved pair, in the order now shown
            var ids = new System.Collections.Generic.List<string>();
            for (int i = 0; i <= Math.Max(k, n); i++) ids.Add(Convert.ToString(Prop(games[i], "Id")));
            try { System.IO.File.WriteAllLines(GameOrderFile, ids.ToArray()); } catch (Exception ex) { Log("save game order: " + ex.Message); }
        }

        private static void SaveCardOrder(System.Windows.Data.CompositeCollection items)
        {
            try
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var o in items) { var s = o as string; if (s != null) names.Add(s); }
                System.IO.File.WriteAllLines(CardOrderFile, names.ToArray());
            }
            catch (Exception ex) { Log("save card order: " + ex.Message); }
        }

        // the row's cards and the switch each one answers to (the Store card is always there)
        private static readonly System.Collections.Generic.KeyValuePair<string, string>[] CardFeatures =
        {
            new System.Collections.Generic.KeyValuePair<string, string>("nav:library", "cardcollection"),
            new System.Collections.Generic.KeyValuePair<string, string>("nav:activity", "cardactivity"),
            new System.Collections.Generic.KeyValuePair<string, string>("nav:captures", "cardcaptures"),
            new System.Collections.Generic.KeyValuePair<string, string>("nav:surprise", "cardsurprise"),
            new System.Collections.Generic.KeyValuePair<string, string>("nav:news", "cardnews"),
            new System.Collections.Generic.KeyValuePair<string, string>("nav:achievements", "cardachievements"),
        };

        private static void ApplyCardOrder(ListBox row)
        {
            try
            {
                var items = row.ItemsSource as System.Windows.Data.CompositeCollection;
                if (items == null) return;
                // cards switched off in Selene settings leave the row
                foreach (var card in CardFeatures)
                    if (!Features.On(card.Value) || (card.Key == "nav:achievements" && !AchievementsData.Available())) { int at = items.IndexOf(card.Key); if (at >= 0) items.RemoveAt(at); }   // (achievements: only with Playnite Achievements)
                if (!System.IO.File.Exists(CardOrderFile)) return;
                // a saved order from before a card existed would push the new cards to the end: it's ignored until the cards
                // are moved again (the next move saves a complete one)
                var saved = new System.Collections.Generic.List<string>();
                foreach (var l in System.IO.File.ReadAllLines(CardOrderFile)) saved.Add(l.Trim());
                foreach (var o in items) { var card = o as string; if (card != null && !saved.Contains(card)) { Log("card order: saved order predates " + card + ", ignored"); return; } }
                int slot = 0;
                foreach (var line in saved)
                {
                    int i = items.IndexOf(line.Trim());
                    if (i < 0) continue;
                    if (i != slot) { var o = items[i]; items.RemoveAt(i); items.Insert(slot, o); }
                    slot++;
                }
            }
            catch (Exception ex) { Log("apply card order: " + ex.Message); }
        }

        // ---- Guide (ring) button = home, from anywhere ----
        // Playnite's own GuideButtonFocus never fired for some controllers (its Guide shows up only through the hidden
        // XInputGetStateEx, on whichever slot the pad has), so poll all four slots ourselves.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct XInputStateEx { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

        [System.Runtime.InteropServices.DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern int XInputGetStateEx(int userIndex, out XInputStateEx state);

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ---- taskbar auto-hide while the fullscreen console runs (it popped up between screens) ----
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
        [System.Runtime.InteropServices.DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        private static int taskbarStateBefore = -1;

        // remembers the user's own taskbar state across crashes (Playnite closed without its exit step left it hidden)
        private static readonly string TaskbarFlag = System.IO.Path.Combine(SelenePaths.PlayniteConfig, "selene-taskbar-before.txt");
        private static bool IsFullscreenApp { get { return System.Diagnostics.Process.GetCurrentProcess().ProcessName.IndexOf("Fullscreen", StringComparison.OrdinalIgnoreCase) >= 0; } }

        // Taskbar auto-hide is OFF (user's choice, 2026-10-10): the taskbar is left as the user set it. Only a state that an
        // older, crashed fullscreen run left behind (flag file) is put back.
        private static void AutoHideTaskbar()
        {
            try
            {
                int saved = -1;
                if (System.IO.File.Exists(TaskbarFlag)) int.TryParse(System.IO.File.ReadAllText(TaskbarFlag).Trim(), out saved);
                if (saved < 0) return;
                var abd = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA)), lParam = new IntPtr(saved) };
                SHAppBarMessage(10 /* ABM_SETSTATE */, ref abd);
                System.IO.File.Delete(TaskbarFlag);
                Log("taskbar: restored after an unclean exit");
            }
            catch (Exception ex) { Log("taskbar restore failed: " + ex.Message); }
        }

        private static void RestoreTaskbar()
        {
            try
            {
                if (taskbarStateBefore < 0) return;
                var abd = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA)), lParam = new IntPtr(taskbarStateBefore) };
                SHAppBarMessage(10, ref abd);
                if (System.IO.File.Exists(TaskbarFlag)) System.IO.File.Delete(TaskbarFlag);
            }
            catch { }
        }

        private static Window shield;

        // ---- Home over a game: a crossfade from the game's own picture ----
        // The press takes a picture of the screen (the game as it is). That picture covers the screen at once (no cut to
        // black), Selene comes forward under it, and then the game's picture shrinks a little and fades while the home settles
        // in from a slight zoom. A game in exclusive fullscreen can't be pictured (the copy comes back black): then the
        // old fade from black. Switch: Look > "Fade in over a game".
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

        private static System.Drawing.Rectangle snapBounds;   // the monitor, in screen pixels
        private static BitmapSource lastGameSnap;   // the game as it was at the last Home press (frozen since: what it still shows)
        private static Window snapWindow;
        private static SnapAdorner snapAdorner;

        private static BitmapSource SnapScreen(IntPtr hwnd)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                var b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
                screen = GetDC(IntPtr.Zero);
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, b.Width, b.Height);
                old = SelectObject(mem, bmp);
                if (!BitBlt(mem, 0, 0, b.Width, b.Height, screen, b.Left, b.Top, 0x00CC0020 /* SRCCOPY */)) return null;
                SelectObject(mem, old); old = IntPtr.Zero;
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                // (a dark scene, or a game the copy can't see that comes back black: crossfading from it is the same as the
                // fade from black, so the picture is always used)
                snapBounds = b;
                Log("guide: game pictured (" + b.Width + "x" + b.Height + ", " + sw.ElapsedMilliseconds + " ms)");
                return src;
            }
            catch (Exception ex) { Log("guide: picture failed: " + ex.Message); return null; }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // the picture over the whole screen, at once: a new window each time, drawn while cloaked (a window shown again first
        // shows its old frame)
        private static void ShowSnapWindow(Window main, BitmapSource snap)
        {
            try
            {
                var src = PresentationSource.FromVisual(main);
                var toDip = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice : Matrix.Identity;
                var tl = toDip.Transform(new Point(snapBounds.Left, snapBounds.Top));
                var br = toDip.Transform(new Point(snapBounds.Right, snapBounds.Bottom));
                var win = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                    Title = "SeleneGuidePicture", Left = tl.X, Top = tl.Y, Width = br.X - tl.X, Height = br.Y - tl.Y,
                    Background = new ImageBrush(snap) { Stretch = Stretch.Fill }
                };
                win.SourceInitialized += (s, e) =>
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                    DisableWindowTransitions(h);
                    SetCloak(h, true);
                };
                win.Show();
                win.UpdateLayout();
                win.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
                System.Threading.Thread.Sleep(16);   // its first frame drawn, still cloaked
                var wh = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                SetWindowPos(wh, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                SetCloak(wh, false);
                snapWindow = win;
                var safety = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
                safety.Tick += (s, e) => { safety.Stop(); if (snapWindow == win) { CloseSnapWindow(); Log("guide: picture window still up after 3 s -> removed"); } };
                safety.Start();
            }
            catch (Exception ex) { Log("guide: picture window failed: " + ex.Message); snapWindow = null; }
        }

        private static void CloseSnapWindow()
        {
            var win = snapWindow;
            snapWindow = null;
            if (win == null) return;
            try { SetCloak(new System.Windows.Interop.WindowInteropHelper(win).Handle, true); win.Close(); } catch { }
        }

        // the same picture inside Selene's own window, over everything (the window's adorner layer: Selene's content can fade
        // and zoom under it without moving it)
        private class SnapAdorner : System.Windows.Documents.Adorner
        {
            private readonly ImageSource picture;
            private readonly Rect place;
            public SnapAdorner(UIElement root, ImageSource picture, Rect place) : base(root)
            {
                this.picture = picture;
                this.place = place;
                IsHitTestVisible = false;
                RenderTransformOrigin = new Point(0.5, 0.5);
                RenderTransform = new ScaleTransform(1, 1);
            }
            protected override void OnRender(DrawingContext dc) { dc.DrawImage(picture, place); }
            public override GeneralTransform GetDesiredTransform(GeneralTransform transform) { return Transform.Identity; }   // not zoomed with the content
        }

        private static bool AddSnapAdorner(Window w, BitmapSource snap)
        {
            RemoveSnapAdorner();
            var root = w.Content as UIElement;
            var layer = root != null ? System.Windows.Documents.AdornerLayer.GetAdornerLayer(root) : null;
            if (layer == null) { Log("guide: no adorner layer, fade from black"); return false; }
            var place = new Rect(layer.PointFromScreen(new Point(snapBounds.Left, snapBounds.Top)), layer.PointFromScreen(new Point(snapBounds.Right, snapBounds.Bottom)));
            snapAdorner = new SnapAdorner(root, snap, place);
            layer.Add(snapAdorner);
            return true;
        }

        private static void RemoveSnapAdorner()
        {
            var ad = snapAdorner;
            snapAdorner = null;
            if (ad == null) return;
            try { var layer = VisualTreeHelper.GetParent(ad) as System.Windows.Documents.AdornerLayer; if (layer != null) layer.Remove(ad); } catch { }
        }

        // the crossfade: the game's picture shrinks a little and fades, the home settles in from a slight zoom under it
        private static void PlayGuideEnter(Window w)
        {
            var ad = snapAdorner;
            CloseSnapWindow();   // Selene's window shows the same picture now: nothing changes on screen
            if (ad == null) { PlayEnterAnimation(w); return; }
            var root = w.Content as FrameworkElement;
            if (root == null) { RemoveSnapAdorner(); return; }
            // a gentle curve over a little longer than a tap's feedback: a whole-screen change read as a jump with the sharper one
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var gameOut = TimeSpan.FromMilliseconds(240);
            var homeIn = TimeSpan.FromMilliseconds(300);
            root.CacheMode = new BitmapCache(1.0) { SnapsToDevicePixels = false };
            var fadeIn = new DoubleAnimation(0, 1, homeIn) { EasingFunction = ease };
            fadeIn.Completed += (s, e) => root.CacheMode = null;
            root.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            var sc = RootScale(root);
            if (sc != null)
            {
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, 1, homeIn) { EasingFunction = ease });
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, 1, homeIn) { EasingFunction = ease });
            }
            var adScale = ad.RenderTransform as ScaleTransform;
            if (adScale != null)
            {
                adScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.94, gameOut) { EasingFunction = ease });
                adScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.94, gameOut) { EasingFunction = ease });
            }
            var gone = new DoubleAnimation(1, 0, gameOut) { EasingFunction = ease };
            gone.Completed += (s, e) => { if (snapAdorner == ad) RemoveSnapAdorner(); };
            ad.BeginAnimation(UIElement.OpacityProperty, gone);
            Log("guide: crossfade from the game's picture");
        }

        // a plain black, topmost, never-activated window over the TV; its last rendered frame is black, so showing it
        // again is instant and clean
        private static void ShowShield(Window main)
        {
            try
            {
                if (shield == null)
                {
                    shield = new Window
                    {
                        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                        Background = Brushes.Black, Title = "SeleneShield"
                    };
                    shield.SourceInitialized += (s, e) => DisableWindowTransitions(new System.Windows.Interop.WindowInteropHelper(shield).Handle);
                }
                shield.Left = main.Left; shield.Top = main.Top; shield.Width = main.ActualWidth; shield.Height = main.ActualHeight;
                bool firstTimeHere = !shield.IsVisible;
                shield.Show();
                var autoHide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                autoHide.Tick += (s2, e2) => { autoHide.Stop(); if (shield != null && shield.IsVisible) { shield.Hide(); Log("shield: still up after 2.5 s -> removed"); } };
                autoHide.Start();
                SetWindowPos(new System.Windows.Interop.WindowInteropHelper(shield).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                if (firstTimeHere && main.ActualWidth > 100)
                {
                    // make sure the shield is actually on screen before Playnite comes forward (the first press showed
                    // Playnite's old bright picture for 3 frames while the shield was still being moved/sized)
                    shield.UpdateLayout();
                    shield.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
                    System.Threading.Thread.Sleep(35);
                }
            }
            catch (Exception ex) { Log("shield failed: " + ex.Message); }
        }

        // render the shield once at startup, off-screen, so its first real appearance is already black
        private static void WarmShield()
        {
            try
            {
                if (launching || guideSwitching || (shield != null && shield.IsVisible)) return;
                var main = Application.Current.MainWindow;
                if (main == null) return;
                var off = new Window { Left = -20000, Top = -20000, Width = 64, Height = 64 };
                ShowShield(off);
                shield.Left = -20000; shield.Top = -20000;
                var t0 = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                t0.Tick += (s, e) => { t0.Stop(); HideShield(); };
                t0.Start();
            }
            catch (Exception ex) { Log("shield warm-up failed: " + ex.Message); }
        }

        private static void HideShield()
        {
            if (shield != null && shield.IsVisible) shield.Hide();
        }

        private static void SetCloak(IntPtr hwnd, bool on)
        {
            int v = on ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref v, sizeof(int)); } catch { }
        }

        // No Windows minimise/restore animation for Playnite (it minimises on game start; coming back slid up from the taskbar)
        private static void DisableWindowTransitions(IntPtr hwnd)
        {
            int on = 1;
            try { DwmSetWindowAttribute(hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref on, sizeof(int)); } catch { }
        }

        private const ushort GuideBit = 0x0400;
        private static bool leftFromHub = true;  // where Playnite was when it lost focus (game start): hub or library
        private static IntPtr gameWindow = IntPtr.Zero;
        private static bool guideSwitching;  // GoHomeFromGuide runs its own enter animation
        private static bool guideAtHome;      // Home put Selene over the running game: the next press goes back to it
        private static int guideSeq;          // each Home press: timers of an older one leave the screen alone
        // Games that just started often pull themselves to the front once more (PoE2 did ~3 s after Guide brought
        // Playnite up). For a few seconds after Guide, a jump back by the game's own process is undone.
        private static DateTime stealGuardUntil = DateTime.MinValue;
        private static DateTime stealGuardStart = DateTime.MinValue;
        private static System.Windows.Threading.DispatcherTimer guardEndTimer;

        private static void StartStealGuard(Window w)
        {
            stealGuardStart = DateTime.Now;
            stealGuardUntil = DateTime.Now.AddSeconds(8);
            w.Topmost = true;
            if (guardEndTimer == null)
            {
                guardEndTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                guardEndTimer.Tick += (s, e) =>
                {
                    if (DateTime.Now < stealGuardUntil) return;
                    if (launching && coverCancelledByGuide) { stealGuardUntil = DateTime.Now.AddSeconds(3); return; }  // still loading
                    guardEndTimer.Stop();
                    var mw = Application.Current.MainWindow;
                    if (mw != null && !launching) mw.Topmost = false;
                };
            }
            guardEndTimer.Start();
        }

        private static void StopStealGuard(Window w)
        {
            stealGuardUntil = DateTime.MinValue;
            if (guardEndTimer != null) guardEndTimer.Stop();
            if (w != null && !launching) w.Topmost = false;
        }  // the window Guide pulled us away from (the running game)

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        // Guide pauses the game like a console: muted, and frozen unless it is an online game (GamePause.cs)
        private static void PauseRunningGame()
        {
            try
            {
                if (gameWindow == IntPtr.Zero) return;
                uint pid;
                GetWindowThreadProcessId(gameWindow, out pid);
                if (pid == 0 || pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
                string name = null;
                if (Api != null) foreach (var g in Api.Database.Games) if (g.IsRunning) { name = g.Name; break; }
                // only a game Playnite is running, and never the Windows shell: Guide on the desktop "paused" Explorer
                // (freezing it would hang the taskbar and desktop)
                string proc = "";
                try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { }
                if (name == null || proc == "explorer" || proc == "shellexperiencehost" || proc == "startmenuexperiencehost" || proc == "searchhost")
                {
                    Log("pause skipped: " + (name == null ? "no game running" : proc));
                    return;
                }
                // Elevated games are outside Selene's process-control scope until a protected broker exists.
                if (LocalGames.IsElevated((int)pid)) { Log("pause skipped: game runs as administrator"); return; }
                // freezing switched off: every game is treated like an online one (only muted)
                bool online = !Features.On("freeze") || GamePause.IsOnline((int)pid);
                if (!online) ShowWindow(gameWindow, 7);  // SW_SHOWMINNOACTIVE: parked, so Windows will not ghost it once frozen
                var target = gameWindow;
                var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(online ? 1 : 450) };
                later.Tick += (s, e) =>
                {
                    later.Stop();
                    if (gameWindow != target) return;  // already went back
                    bool parked = online || IsIconic(target);
                    GamePause.Pause((int)pid, name ?? "", parked);
                };
                later.Start();
            }
            catch (Exception ex) { Log("pause failed: " + ex.Message); }
        }

        private static bool AnyGameRunning()
        {
            try { if (Api != null) foreach (var g in Api.Database.Games) if (g.IsRunning) return true; } catch { }
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        // console "Play Game" while it runs: close it (ask first), then start it fresh. WM_CLOSE first; if the game is
        // still up after 8 s its process is ended. Playnite notices the exit, then the game is started again.
        // ---- Close Game from the home screen (console style): "Closing this game or app..." with its icon, then home ----
        // inside Playnite's own window, over everything (a window of its own was covered at once when Playnite brought its
        // main window forward as the game ended: the home cut in, still showing "Now playing")
        private static Grid closingLayer;

        private static void CloseGame(object game)
        {
            try
            {
                var id = Prop(game, "Id");
                if (!(id is Guid) || Api == null) return;
                var g0 = Api.Database.Games.Get((Guid)id);
                var name = Convert.ToString(Prop(game, "Name"));
                // Check every target before sending any close message or starting the closing screen.
                var pids = new System.Collections.Generic.HashSet<int>();
                if (gameWindow != IntPtr.Zero) { uint wp; GetWindowThreadProcessId(gameWindow, out wp); if (wp != 0) pids.Add((int)wp); }
                foreach (var pid in LocalGames.ProcessesInGameFolder(g0)) pids.Add(pid);
                if (pids.Any(pid => LocalGames.IsElevated(pid)))
                {
                    Api.Dialogs.ShowMessage("Close this administrator game from its own menu. Selene's elevated game controls are currently unavailable.", "Close game");
                    return;
                }
                var main = Application.Current.MainWindow;
                ShowClosingScreen(main, g0, name);
                GamePause.Resume();
                if (gameWindow != IntPtr.Zero) PostMessage(gameWindow, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                foreach (var pid in pids)
                {
                    try
                    {
                        var pr = System.Diagnostics.Process.GetProcessById(pid);
                        if (pr.MainWindowHandle != IntPtr.Zero) pr.CloseMainWindow();
                    }
                    catch { }
                }
                Log("close game: " + name + " (" + pids.Count + " process(es))");
                var started = DateTime.Now;
                bool killed = false;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                timer.Tick += (s, e) =>
                {
                    var secs = (DateTime.Now - started).TotalSeconds;
                    bool alive = false;
                    foreach (var pid in pids) if (ProcessAlive(pid)) alive = true;
                    var gNow = Api.Database.Games.Get((Guid)id);
                    if (!alive && gNow != null && gNow.IsRunning && LocalGames.IsMarkedRunning(gNow.Id)) { gNow.IsRunning = false; Api.Database.Games.Update(gNow); }
                    // the home still showed "Now playing / Resume" for ~2 s after: wait until Playnite has seen the game stop
                    bool playniteSeesIt = gNow == null || !gNow.IsRunning || secs > 12;
                    if (!alive && secs >= 1.4 && playniteSeesIt)
                    {
                        timer.Stop();
                        gameWindow = IntPtr.Zero;
                        Log("close game: " + name + " closed after " + secs.ToString("0.0") + " s");
                        HideClosingScreen();
                        return;
                    }
                    if (alive && secs > 4 && !killed)
                    {
                        killed = true;
                        foreach (var pid in pids) try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
                        Log("close game: ended " + name + " (did not close in 4 s)");
                    }
                    if (secs > 20) { timer.Stop(); gameWindow = IntPtr.Zero; HideClosingScreen(); Log("close game: gave up on " + name); }
                };
                timer.Start();
            }
            catch (Exception ex) { Log("close game failed: " + ex.Message); HideClosingScreen(); }
        }

        // an admin process can't be opened from here (HasExited throws): it counted as closed while still running
        private static bool ProcessAlive(int pid)
        {
            System.Diagnostics.Process pr;
            try { pr = System.Diagnostics.Process.GetProcessById(pid); } catch { return false; }
            try { return !pr.HasExited; } catch { return true; }
        }

        // the window in front belongs to a program running as administrator (Playnite can't take the screen back from it alone)
        private static bool ForegroundIsAdmin()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (pid == 0 || pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return false;
                return LocalGames.IsElevated((int)pid);
            }
            catch { return false; }
        }

        // the closing screen's backdrop: Selene's wide art when there is some, else the game's own background (a new install has
        // no wide art: the screen was plain black there)
        private static string ClosingArt(Playnite.SDK.Models.Game g)
        {
            if (g == null) return null;
            var hero = System.IO.Path.Combine(SelenePaths.Media("Hero"), g.Id + ".jpg");
            return System.IO.File.Exists(hero) ? hero : GameFacts.BackgroundPath(g);
        }

        private static void ShowClosingScreen(Window main, Playnite.SDK.Models.Game g, string name)
        {
            try
            {
                HideClosingScreen();
                var root = new Grid { Opacity = 0, Background = Brushes.Black };
                // the home's own animated background, a little dimmed
                string bgVideo = SelenePaths.ThemeFile("Media", "home-background.mp4");
                if (System.IO.File.Exists(bgVideo))
                {
                    var me = new MediaElement { Source = new Uri(bgVideo), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true, Stretch = Stretch.UniformToFill };
                    me.MediaEnded += (s, e) => { me.Position = TimeSpan.Zero; me.Play(); };
                    root.Children.Add(me);
                    me.Play();
                }
                else if (ClosingArt(g) != null)
                {
                    // no theme video: the game's own art, soft and dark, behind the closing tile
                    var art = new Image { Source = LoadNow(ClosingArt(g), 1280), Stretch = Stretch.UniformToFill,
                                          Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 40 }, Opacity = 0.55,
                                          RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1.1, 1.1) };
                    root.Children.Add(art);
                }
                var veil = new RadialGradientBrush { Center = new Point(0.5, 0.48), GradientOrigin = new Point(0.5, 0.48), RadiusX = 0.7, RadiusY = 0.75 };
                veil.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0, 0, 0), 0));
                veil.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 1));
                root.Children.Add(new Border { Background = veil });

                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5) };
                var lift = new TranslateTransform(0, 18);
                stack.RenderTransform = lift;
                string icon = null;
                if (g != null && Api != null)
                {
                    var sq = System.IO.Path.Combine(SelenePaths.Media("Square"), g.Id + ".jpg");
                    if (System.IO.File.Exists(sq)) icon = sq;
                    else if (!string.IsNullOrEmpty(g.CoverImage)) icon = Api.Database.GetFullFilePath(g.CoverImage);
                    else if (!string.IsNullOrEmpty(g.Icon)) icon = Api.Database.GetFullFilePath(g.Icon);
                }
                if (icon != null && System.IO.File.Exists(icon))
                {
                    var tile = new Border
                    {
                        Width = 220, Height = 220, CornerRadius = new CornerRadius(26), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center,
                        Background = new ImageBrush(LoadNow(icon, 440)) { Stretch = Stretch.UniformToFill },
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(2),
                        Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 46, Opacity = 0.65, Color = Colors.Black }
                    };
                    stack.Children.Add(tile);
                }
                stack.Children.Add(new TextBlock
                {
                    Text = name, FontSize = 38, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 34, 0, 0), TextAlignment = TextAlignment.Center
                });
                var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
                status.Children.Add(new TextBlock { Text = "Closing this game or app", FontSize = 24, FontFamily = new FontFamily("Segoe UI"), Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
                // three soft pulsing dots
                for (int i = 0; i < 3; i++)
                {
                    var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.White, Margin = new Thickness(i == 0 ? 12 : 6, 6, 0, 0), Opacity = 0.25, VerticalAlignment = VerticalAlignment.Center };
                    dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(480)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 160) });
                    status.Children.Add(dot);
                }
                stack.Children.Add(status);
                root.Children.Add(stack);

                var host = FindByName(main, "PART_MainHost") as Grid;
                if (host == null) { Log("closing screen: no host"); return; }
                Grid.SetRowSpan(root, 99);
                Grid.SetColumnSpan(root, 99);
                Panel.SetZIndex(root, 9997);
                host.Children.Add(root);
                closingLayer = root;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
                lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
            }
            catch (Exception ex) { Log("closing screen failed: " + ex.Message); }
        }

        // the home under it has caught up first (Now playing / Resume gives way to Play a moment after the game ends), then
        // the screen fades away into it
        private static void HideClosingScreen()
        {
            var layer = closingLayer;
            closingLayer = null;
            if (layer == null) return;
            var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            wait.Tick += (s0, e0) =>
            {
                wait.Stop();
                var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                anim.Completed += (s, e) =>
                {
                    foreach (var me in layer.Children.OfType<MediaElement>()) { try { me.Stop(); me.Close(); } catch { } }
                    var parent = layer.Parent as Panel;
                    if (parent != null) parent.Children.Remove(layer);
                };
                layer.BeginAnimation(UIElement.OpacityProperty, anim);
            };
            wait.Start();
        }

        private static void RestartGame(object game)
        {
            try
            {
                var id = Prop(game, "Id");
                if (!(id is Guid) || Api == null) return;
                var name = Convert.ToString(Prop(game, "Name"));
                var target = gameWindow;
                uint pid;
                GetWindowThreadProcessId(target, out pid);
                if (pid == 0 || LocalGames.IsElevated((int)pid))
                {
                    Api.Dialogs.ShowMessage("Close this administrator game from its own menu, then start it again. Selene's elevated game controls are currently unavailable.", "Restart game");
                    return;
                }
                var answer = Api.Dialogs.ShowMessage("Close " + name + " and start it again?" + Environment.NewLine + "Unsaved progress will be lost.", "Restart game",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                GamePause.Resume();
                PostMessage(target, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                Log("restart: closing " + name + " (pid " + pid + ")");
                var started = DateTime.Now;
                bool killed = false;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (s, e) =>
                {
                    var g = Api.Database.Games.Get((Guid)id);
                    if (g == null) { timer.Stop(); return; }
                    var secs = (DateTime.Now - started).TotalSeconds;
                    if (!g.IsRunning)
                    {
                        timer.Stop();
                        gameWindow = IntPtr.Zero;
                        Log("restart: " + name + " closed after " + secs.ToString("0.0") + " s, starting again");
                        StartGameNow((Guid)id);
                        return;
                    }
                    if (secs > 8 && !killed && pid != 0)
                    {
                        killed = true;
                        try { System.Diagnostics.Process.GetProcessById((int)pid).Kill(); Log("restart: ended process " + pid); } catch (Exception ex) { Log("restart: kill failed: " + ex.Message); }
                    }
                    if (secs > 30) { timer.Stop(); Log("restart: gave up, " + name + " still running"); }
                };
                timer.Start();
            }
            catch (Exception ex) { Log("restart failed: " + ex.Message); }
        }

        // ---- Launch cover: keep the loading screen up until the game is really there ----
        // Called by the ClaudeLibraryTools extension (OnGameStarting). Playnite's loading screen stays on top (Topmost) so
        // windowed start-up screens, black windows and launchers stay hidden behind it. When a window of another process
        // covers the whole screen for ~0.75 s, the loading screen fades to black and the game is brought forward (that
        // black frame is also what Windows remembers, so the first Guide press has no stale picture). Gives up after 45 s
        // (a launcher or dialog may need input); Guide during loading cancels it.
        private static bool launching;
        private static LaunchAttempt launchAttempt;   // the press of Play now loading (null before the first)

        // B (pad or keyboard) while the loading screen is up. True when the press was taken: the launch is cancelled.
        private static bool HandleLaunchBack(KeyEventArgs ke)
        {
            if (ke == null || !launching || launchAttempt == null || !launchAttempt.CanCancel) return false;
            bool down = ke.RoutedEvent == Keyboard.KeyDownEvent || ke.RoutedEvent == Keyboard.PreviewKeyDownEvent;
            bool isB;
            if (ke.GetType().Name == "GameControllerInputEventArgs")
            {
                var prop = ke.GetType().GetProperty("Button");
                var name = prop != null ? prop.GetValue(ke, null) : null;
                isB = name != null && Ui.Normalize(name.ToString()) == "B";
            }
            else { var key = ke.Key == Key.System ? ke.SystemKey : ke.Key; isB = key == Key.Escape || key == Key.Back; }
            if (!isB) return false;
            if (down) CancelLaunch();
            return true;   // (and its release)
        }

        // B on the loading screen: back to Selene with the fade-in, and the game is stopped too. Playnite has already been told
        // to start it, so dropping the cover isn't enough: its processes appear (or keep appearing) a few seconds later and the
        // game came up over the desktop. Whatever of the game started after Play is closed (ended after 1.5 s) until it has been
        // gone for a moment; programs that were already running (a launcher) are left alone.
        private static void CancelLaunch()
        {
            var attempt = launchAttempt;
            if (attempt == null || !attempt.Cancel()) return;
            var id = attempt.GameId;
            Log("launch cancelled by B (dispatched: " + attempt.Dispatched + ")");
            AbortLaunchCover(IntPtr.Zero, "B pressed");
            if (!attempt.Dispatched || Api == null) { attempt.Complete(); return; }   // Playnite was never asked: nothing to stop
            var started = DateTime.Now;
            var asked = new HashSet<int>();
            int quiet = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (s, e) =>
            {
                try
                {
                    var secs = (DateTime.Now - started).TotalSeconds;
                    var g = Api.Database.Games.Get(id);
                    if (g == null || secs > 30) { timer.Stop(); attempt.Complete(); return; }
                    // Playnite minimises itself when the game starts: keep it on screen
                    var w = Application.Current.MainWindow;
                    var mh = w != null ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero;
                    if (mh != IntPtr.Zero && IsIconic(mh)) { ShowWindow(mh, 9 /* SW_RESTORE */); w.Activate(); }
                    var pids = new List<int>();
                    foreach (var pid in LocalGames.ProcessesInGameFolder(g)) if (attempt.CanCloseProcess(pid)) pids.Add(pid);
                    foreach (var pid in pids)
                    {
                        try
                        {
                            if (LocalGames.IsElevated(pid)) continue;
                            var pr = System.Diagnostics.Process.GetProcessById(pid);
                            if (asked.Add(pid)) { if (pr.MainWindowHandle != IntPtr.Zero) pr.CloseMainWindow(); }
                            else if (secs > 1.5) pr.Kill();
                        }
                        catch { }
                    }
                    if (pids.Count > 0) { quiet = 0; return; }
                    if (g.IsRunning && LocalGames.IsMarkedRunning(g.Id)) { g.IsRunning = false; Api.Database.Games.Update(g); }
                    // nothing left, and Playnite no longer waits for the game to start
                    if (!g.IsLaunching && secs >= 3 && ++quiet >= 6) { timer.Stop(); attempt.Complete(); Log("launch cancelled: " + g.Name + " is stopped"); }
                }
                catch (Exception ex) { timer.Stop(); attempt.Complete(); Log("cancel launch failed: " + ex.Message); }
            };
            timer.Start();
        }
        public static bool Launching { get { return launching; } }
        public static bool Quitting;   // Playnite is being closed for real (Exit, Alt+F4, power actions)
        private static bool quitFaded;

        // Playnite closing: Selene's windows leave the screen at once (no dark screen, no "Exiting Playnite" screen) and
        // Windows hands the screen to what is underneath; Playnite finishes closing out of sight (saving its library, the
        // add-ons' last steps), which takes a few seconds
        public static bool ShutdownBegun;          // Playnite's own close has started (SeleneCore.OnApplicationStopped)
        public static IntPtr QuitHandoff;           // the window that had the screen after Selene's left it
        public static void BeginQuit()
        {
            Quitting = true;
            if (quitFaded) return;
            quitFaded = true;
            try
            {
                var w = Application.Current.MainWindow;
                var hwnd = w != null ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero;
                if (w != null && w.IsVisible && hwnd != IntPtr.Zero)
                {
                    SetCloak(hwnd, true);   // off the screen this very frame
                    HideShield();
                    w.Hide();               // and Windows gives the focus to the window beneath (the desktop, a game)
                    uint pid;
                    var fg = GetForegroundWindow();
                    GetWindowThreadProcessId(fg, out pid);
                    QuitHandoff = pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id ? fg : IntPtr.Zero;
                    Log("quit: Selene's windows hidden, Playnite closes out of sight");
                    StopStealGuard(w);   // (Guide's guard must not pull Playnite back now)
                    // Playnite's closing window activates itself (an invisible window would hold the focus for the seconds
                    // Playnite takes to close: the game under it deaf to the pad): the focus goes straight back
                    var handoff = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    var handoffEnd = DateTime.Now.AddSeconds(40);
                    int handedBack = 0;
                    handoff.Tick += (s, e) =>
                    {
                        if (!Quitting || DateTime.Now > handoffEnd) { handoff.Stop(); return; }
                        uint fp; var fgw = GetForegroundWindow(); GetWindowThreadProcessId(fgw, out fp);
                        if (fp != (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                        {
                            // (right after the hide Windows hasn't moved the focus yet: the window it lands on is learnt here)
                            if (fgw != IntPtr.Zero && (QuitHandoff == IntPtr.Zero || !IsWindow(QuitHandoff))) QuitHandoff = fgw;
                            return;
                        }
                        if (QuitHandoff == IntPtr.Zero || !IsWindow(QuitHandoff)) { if (handedBack++ < 3) Log("quit: Playnite's closing window has the focus, nothing to hand it back to"); return; }
                        bool ok = SetForegroundWindow(QuitHandoff);
                        if (handedBack++ < 5) Log("quit: focus handed back from Playnite's window " + fgw + " (" + (ok ? "done" : "refused") + ")");
                    };
                    handoff.Start();
                }
                // never gone for good when the quit didn't happen: Playnite hasn't begun closing 8 s later -> Selene comes back (#27, #26)
                var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                guard.Tick += (s, e) =>
                {
                    guard.Stop();
                    try
                    {
                        if (ShutdownBegun || CloseToTray.AppShuttingDown()) return;
                        Quitting = false; quitFaded = false;
                        var main = Application.Current.MainWindow;
                        if (main != null)
                        {
                            main.Show();
                            var mh = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                            SetCloak(mh, false);
                            // back in front the way Guide does it (the window that got the screen may be a game run as admin)
                            ForceForegroundAsync(mh, () => main.Activate());
                            var r = main.Content as UIElement;
                            if (r != null && r.Opacity < 0.99) FadeInRoot(r);
                        }
                        Log("quit: Playnite did not close in 8 s -> Selene back");
                    }
                    catch (Exception ex) { Log("quit guard: " + ex.Message); }
                };
                guard.Start();
                // and never left running out of sight: still closing after 30 s = stuck; it is ended, with Playnite's crash
                // marker cleared (a normal close clears it too) so the next start isn't offered safe mode
                var configDir = Api != null ? Api.Paths.ConfigurationPath : null;
                new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(30000);
                    if (!Quitting || !ShutdownBegun) return;
                    Log("quit: Playnite still closing after 30 s -> ended");
                    try { if (configDir != null) System.IO.File.Delete(System.IO.Path.Combine(configDir, "safestart.flag")); } catch { }
                    try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
                }) { IsBackground = true, Name = "SeleneQuitWatch" }.Start();
            }
            catch (Exception ex) { Log("quit: " + ex.Message); }
        }

        // a window Playnite opens while it closes (its "Closing" progress screen): never shown, never given the focus
        public static void HideForQuit(Window w)
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero) SetCloak(h, true);   // (the focus: BeginQuit's hand-back)
            }
            catch { }
        }
        public static bool GameRunning()
        {
            try { return gameWindow != IntPtr.Zero || (Api != null && Api.Database.Games.Any(g => g.IsRunning)); } catch { return false; }
        }
        private static bool coverCancelledByGuide;
        private static Guid launchGameId = Guid.Empty;
        // games that start through another launcher (Ubisoft Connect, the EA app, Rockstar, Battle.net, the Xbox app) take
        // longer before anything of the game shows: their waits are stretched by this
        private static double launchGrace = 1;
        private static readonly string[] slowLaunchers = { "ubisoft", "uplay", "ea app", "ea play", "origin", "rockstar", "battle.net", "blizzard", "xbox", "microsoft store" };
        private static double LaunchGrace(Guid id)
        {
            try
            {
                var g = Api != null ? Api.Database.Games.Get(id) : null;
                if (g == null) return 1;
                var names = new List<string>();
                if (g.Source != null) names.Add(g.Source.Name);
                var plugin = Api.Addons.Plugins.FirstOrDefault(p => p.Id == g.PluginId);
                if (plugin != null) names.Add(plugin.GetType().Name);
                if (g.GameActions != null) foreach (var act in g.GameActions) { if (act.Path != null) names.Add(act.Path); if (act.Arguments != null) names.Add(act.Arguments); }
                foreach (var n in names) { var low = n.ToLowerInvariant(); foreach (var k in slowLaunchers) if (low.Contains(k)) { Log("play: launcher game (" + n + "), longer waits"); return 2.2; } }
            }
            catch (Exception ex) { Log("play: launcher check " + ex.Message); }
            return 1;
        }
        private static IntPtr steamDialog = IntPtr.Zero;
        private static int steamDialogTicks;
        private static DateTime launchStart;
        private static System.Windows.Threading.DispatcherTimer launchTimer;
        private static DateTime gameWindowSeenAt = DateTime.MinValue;
        private static DateTime gameGoneSince = DateTime.MinValue;
        private static IntPtr lastGameWindow = IntPtr.Zero;
        private static readonly HashSet<int> gamePids = new HashSet<int>();
        private static DateTime gamePidsAt = DateTime.MinValue, soundSince = DateTime.MinValue, lastSoundAt = DateTime.MinValue;
        private static int soundTick;

        public static void GameStarting() { GameStarting(Guid.Empty); }

        private static Window coverWindow;

        // console-style launch screen in our own window: the game's key art full screen (sharp hero art first), fading up from
        // black with a slow continuous drift, then the logo large in the centre, easing in. Topmost and never activated, so the
        // game can start behind it; RevealGame fades it out. An opaque window (not AllowsTransparency, which is drawn in
        // software and made full-screen animation stutter); the art is bitmap-cached so the drift is a cheap GPU scale.
        // Play: the loading screen's hero art (4K jpg) and logo are decoded ahead, while the game is only selected
        private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> coverArt = new Dictionary<string, System.Windows.Media.Imaging.BitmapImage>();
        private static readonly Queue<string> coverArtOrder = new Queue<string>();

        private static void CoverArtPaths(Guid gameId, out string bg, out string logo)
        {
            bg = null; logo = null;
            if (gameId == Guid.Empty || Api == null) return;
            var hero = System.IO.Path.Combine(SelenePaths.Media("Hero"), gameId + ".jpg");
            var g = Api.Database.Games.Get(gameId);
            if (System.IO.File.Exists(hero)) bg = hero;
            else if (g != null && !string.IsNullOrEmpty(g.BackgroundImage)) bg = Api.Database.GetFullFilePath(g.BackgroundImage);
            var lp = System.IO.Path.Combine(Api.Paths.ConfigurationPath, "ExtraMetadata", "games", gameId.ToString(), "logo.png");
            if (System.IO.File.Exists(lp)) logo = lp;
        }

        private static void PreloadCoverArt(object item)
        {
            try
            {
                var id = item != null && !(item is string) ? Prop(item, "Id") : null;
                if (!(id is Guid)) return;
                var main = Application.Current.MainWindow;
                if (main == null) return;
                int width = (int)Math.Max(1920, main.ActualWidth);
                string bg, logo;
                CoverArtPaths((Guid)id, out bg, out logo);
                var jobs = new List<KeyValuePair<string, int>>();
                if (bg != null) jobs.Add(new KeyValuePair<string, int>(bg, width));
                if (logo != null) jobs.Add(new KeyValuePair<string, int>(logo, 0));
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    foreach (var j in jobs)
                    {
                        var key = j.Key + "|" + j.Value;
                        lock (coverArt) { if (coverArt.ContainsKey(key)) continue; }
                        System.Windows.Media.Imaging.BitmapImage bmp;
                        try { bmp = Decode(j.Key, j.Value); } catch { continue; }  // frozen: usable from the UI thread
                        lock (coverArt)
                        {
                            if (coverArt.ContainsKey(key)) continue;
                            coverArt[key] = bmp;
                            coverArtOrder.Enqueue(key);
                            while (coverArtOrder.Count > 12) coverArt.Remove(coverArtOrder.Dequeue());  // a few games' worth
                        }
                    }
                });
            }
            catch { }
        }

        private static void ShowCoverWindow(Window main, Guid gameId)
        {
            try
            {
                if (coverWindow != null) { try { coverWindow.Close(); } catch { } coverWindow = null; }
                string bg, logo;
                CoverArtPaths(gameId, out bg, out logo);
                var root = new Grid { Background = Brushes.Black, ClipToBounds = true };
                // (no snapshot of the home any more: the whole window fades in over the live home instead, see below)
                var scene = new Grid();
                var launchStyle = Features.Value("launchstyle");
                bool showArt = launchStyle == "artlogo" || launchStyle == "art";
                bool showLogo = launchStyle == "artlogo" || launchStyle == "logo";
                bool moveArt = Features.On("launchmotion") && BackgroundFit.Current != Stretch.Uniform;
                var artStretch = BackgroundFit.Current;
                double shade = Features.Number("launchshade");
                if (coverArtStage != null) { coverArtStage.Stop(); coverArtStage = null; }
                if (showArt && bg != null && System.IO.File.Exists(bg))
                {
                    // the art and its slow drift on a thread of their own: Playnite's start and the game's launch block the UI
                    // thread for a few hundred ms at a time, which froze the drift mid-way
                    var bmp = LoadNow(bg, (int)Math.Max(1920, main.ActualWidth));
                    var art = new OffThread(sz =>
                    {
                        var img = new Image { Source = bmp, Stretch = artStretch, RenderTransformOrigin = new Point(0.5, 0.5) };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        var zoom = new ScaleTransform(moveArt ? 1.04 : 1, moveArt ? 1.04 : 1);
                        img.RenderTransform = zoom;
                        if (moveArt) zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.04, 1.12, TimeSpan.FromSeconds(20)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
                        if (moveArt) zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.04, 1.12, TimeSpan.FromSeconds(20)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
                        return img;
                    }, "Selene loading art");
                    scene.Children.Add(art);
                    coverArtStage = art;
                }
                // light, even dim so a white logo reads on bright art (console keeps the art bright)
                var dim = new RadialGradientBrush { GradientOrigin = new Point(0.5, 0.5), Center = new Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.75 };
                dim.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(0x60 * shade), 0, 0, 0), 0));
                dim.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(0xE0 * shade), 0, 0, 0), 1));
                root.Children.Add(scene);
                var dimLayer = new Border { Background = dim, Opacity = 0 };
                root.Children.Add(dimLayer);
                coverArtLayers = new UIElement[] { scene, dimLayer };
                FrameworkElement logoEl = null;
                var logoScale = new ScaleTransform(0.94, 0.94);
                if (showLogo && logo != null)
                {
                    logoEl = new Image
                    {
                        Source = LoadNow(logo, 0), MaxWidth = Math.Min(980, main.ActualWidth * 0.5), MaxHeight = Math.Min(420, main.ActualHeight * 0.38),
                        Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = logoScale,
                        Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 30, Opacity = 0.55 }
                    };
                    RenderOptions.SetBitmapScalingMode(logoEl, BitmapScalingMode.HighQuality);
                    root.Children.Add(logoEl);
                }
                else if (launchStyle != "art")
                {
                    // no logo (none downloaded yet, or the store has none): the game's name, large, in its place
                    var g = Api != null && gameId != Guid.Empty ? Api.Database.Games.Get(gameId) : null;
                    if (g != null && !string.IsNullOrEmpty(g.Name))
                    {
                        logoEl = new TextBlock
                        {
                            Text = g.Name, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, FontSize = 76, Foreground = Brushes.White,
                            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = Math.Min(1200, main.ActualWidth * 0.7),
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                            Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = logoScale,
                            Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 30, Opacity = 0.6 }
                        };
                        root.Children.Add(logoEl);
                    }
                }
                coverBlack = new Border { Background = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
                root.Children.Add(coverBlack);
                coverDark = false;
                coverWindow = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                    Background = Brushes.Black, Content = root, Left = main.Left, Top = main.Top, Width = main.ActualWidth, Height = main.ActualHeight,
                    Title = "SeleneLaunchCover"
                };
                var cwShow = coverWindow;
                coverWindow.SourceInitialized += (s, e) => DisableWindowTransitions(new System.Windows.Interop.WindowInteropHelper(cwShow).Handle);
                // The loading screen fades in over the live home INSIDE Playnite's own window (GPU, smooth), then moves into
                // the topmost window that covers the launch (same picture: no visible change). A separate window can't fade
                // over the home: layered alpha did nothing (popped in), the old home snapshot cost ~700 ms.
                var host = CoverFadeHost(main);
                if (host == null) Log("loading screen: no panel to fade in over (" + (main.Content == null ? "null" : main.Content.GetType().Name) + "), shown directly");
                if (host != null && main.IsVisible && !CloseToTray.IsHidden)
                {
                    cwShow.Content = null;
                    var overlay = new Grid { Opacity = 0, IsHitTestVisible = false };
                    Grid.SetRowSpan(overlay, 99); Grid.SetColumnSpan(overlay, 99);
                    Panel.SetZIndex(overlay, 100000);
                    overlay.Children.Add(root);
                    host.Children.Add(overlay);
                    Action handOver = null;
                    handOver = () =>
                    {
                        if (removeCoverOverlay != handOver) return;
                        removeCoverOverlay = null;
                        overlay.Children.Remove(root);
                        host.Children.Remove(overlay);
                        if (coverWindow != cwShow) return;  // cancelled meanwhile
                        cwShow.Content = root;
                        cwShow.Show();
                        var startLogo = pendingLogo;
                        pendingLogo = null;
                        if (startLogo != null) startLogo();
                    };
                    removeCoverOverlay = handOver;
                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    fadeIn.Completed += (s, e) => handOver();
                    overlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                else coverWindow.Show();

                var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
                var softOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
                // art up from black, then a slow drift for as long as the game takes to load (the drift runs on the art's own thread)
                // art cross-fades in over the home still (user preferred this to a console-style slide)
                dimLayer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(300) });
                pendingLogo = null;
                if (logoEl != null)
                {
                    // logo: a soft fade while it rises gently into place (no zoom - user preference). It starts after the art
                    // has faded in (it used to fade in together with the art and looked like it just appeared)
                    var at = TimeSpan.FromMilliseconds(60);
                    logoScale.ScaleX = 1; logoScale.ScaleY = 1;
                    var rise = new TranslateTransform(0, 30);
                    var logoGroup = new TransformGroup();
                    logoGroup.Children.Add(logoScale);
                    logoGroup.Children.Add(rise);
                    logoEl.RenderTransform = logoGroup;
                    var logoTarget = logoEl;
                    Action startLogo = () =>
                    {
                        logoTarget.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(750)) { BeginTime = at, EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
                        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(1000)) { BeginTime = at, EasingFunction = softOut });
                    };
                    if (removeCoverOverlay != null) pendingLogo = startLogo;  // still fading in inside Playnite's window
                    else startLogo();
                }
            }
            catch (Exception ex) { Log("cover window failed: " + ex.Message); }
        }

        private static System.Windows.Media.Imaging.BitmapImage LoadNow(string path, int decodeWidth)
        {
            var key = path + "|" + decodeWidth;
            System.Windows.Media.Imaging.BitmapImage ready;
            lock (coverArt) { if (coverArt.TryGetValue(key, out ready)) return ready; }
            return Decode(path, decodeWidth);
        }

        private static System.Windows.Media.Imaging.BitmapImage Decode(string path, int decodeWidth)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;  // decoded here, ready on the first frame
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);

        // console: once the game's window shows up the art darkens slowly to black and stays black until the game is ready.
        // (A fullscreen game takes the screen the moment its window appears, ~3 s before "ready": darkening only then
        // happened unseen behind the game, which looked like a hard cut.)
        private static Border coverBlack;
        private static bool coverDark;

        // Playnite's screen comes back with a short fade instead of popping in
        public static void FadeInRoot(UIElement root)
        {
            if (root == null) return;
            double from = root.Opacity;
            if (from >= 0.99 && !root.HasAnimatedProperties) return;
            var a = new DoubleAnimation(Math.Min(from, 0.0), 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            a.Completed += (s, e) => root.BeginAnimation(UIElement.OpacityProperty, null);
            root.BeginAnimation(UIElement.OpacityProperty, a);
        }

        private static UIElement[] coverArtLayers;
        private static bool coverArtGone;

        // the loading screen fades to black (art and logo), then (once dark) the next step: the game's own screen switch,
        // whenever it comes, can't be seen on black
        private static void ArtToLogo(Action then)
        {
            var black = coverBlack;
            if (black == null) { then(); return; }
            black.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            t.Tick += (s, e) => { t.Stop(); then(); };
            t.Start();
            Log("launch cover: game window up, fading to black");
        }

        private static void DarkenCover(bool on)
        {
            if (coverBlack == null || on == coverDark) return;
            coverDark = on;
            coverBlack.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 1000 : 500))
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            Log("launch cover: " + (on ? "darkening (game window appeared)" : "brightening again (it was a passing window)"));
        }

        // the outermost window-sized Panel of Playnite's window (its Content is a Border/ContentControl, not a Panel)
        private static Panel coverHost;
        private static Panel CoverFadeHost(Window main)
        {
            if (coverHost != null && coverHost.IsVisible && Window.GetWindow(coverHost) == main) return coverHost;
            coverHost = null;
            var queue = new Queue<KeyValuePair<DependencyObject, int>>();
            var start = main.Content as DependencyObject;
            if (start == null) return null;
            queue.Enqueue(new KeyValuePair<DependencyObject, int>(start, 0));
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                var panel = cur.Key as Panel;
                if (panel != null && !panel.IsItemsHost && panel.ActualWidth >= main.ActualWidth - 4 && panel.ActualHeight >= main.ActualHeight - 4
                    && (panel is Grid || panel is Canvas || panel is DockPanel))
                {
                    coverHost = panel;
                    Log("loading screen: fades in over " + panel.GetType().Name + " '" + panel.Name + "' (depth " + cur.Value + ")");
                    return panel;
                }
                if (cur.Value >= 8) continue;
                int n = VisualTreeHelper.GetChildrenCount(cur.Key);
                for (int i = 0; i < n; i++) queue.Enqueue(new KeyValuePair<DependencyObject, int>(VisualTreeHelper.GetChild(cur.Key, i), cur.Value + 1));
            }
            return null;
        }

        // set while the loading screen is still fading in inside Playnite's window (before it moves to its own window)
        private static Action removeCoverOverlay;
        private static Action pendingLogo;

        private static void CloseCoverWindow(bool fade, Action after)
        {
            if (removeCoverOverlay != null) { var r = removeCoverOverlay; coverWindow = null; r(); }
            var cw = coverWindow;
            coverWindow = null;
            if (cw == null) { if (after != null) after(); return; }
            var black = coverBlack;
            coverBlack = null;
            if (!fade || black == null) { if (after != null) after(); try { cw.Close(); } catch { } return; }
            Action fadeIntoGame = () =>
            {
                // black now: a black veil of its own goes over everything, the loading screen under it closes, the game comes
                // forward under the veil, and the veil melts away (0.7 s) so the game fades in. The veil is a window made
                // see-through from the start, which WPF fades reliably; turning the loading screen itself into one after the
                // fact left it blank at once (a cut, with a flash of what was behind).
                // (a game in exclusive fullscreen still cuts in: nothing can be drawn over it)
                Window veil = null;
                try
                {
                    veil = new Window
                    {
                        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Black,
                        Topmost = true, ShowInTaskbar = false, ShowActivated = false, Left = cw.Left, Top = cw.Top, Width = cw.Width, Height = cw.Height,
                        Title = "SeleneLaunchVeil", Opacity = 1
                    };
                    var v0 = veil;
                    veil.SourceInitialized += (s3, e3) =>
                    {
                        var h = new System.Windows.Interop.WindowInteropHelper(v0).Handle;
                        SetWindowLong(h, -20 /* GWL_EXSTYLE */, GetWindowLong(h, -20) | 0x20 /* WS_EX_TRANSPARENT */ | 0x08000000 /* WS_EX_NOACTIVATE */);
                        DisableWindowTransitions(h);
                    };
                    veil.Show();
                }
                catch (Exception ex) { Log("launch veil: " + ex.Message); veil = null; }
                try { cw.Close(); } catch { }
                if (after != null) after();   // the game forward, under the veil
                if (veil == null) return;
                var v = veil;
                var hold = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };   // a beat of black while the game draws its first frames
                hold.Tick += (s4, e4) =>
                {
                    hold.Stop();
                    var h = new System.Windows.Interop.WindowInteropHelper(v).Handle;
                    if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0013 /* NOSIZE|NOMOVE|NOACTIVATE */);
                    var melt = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                    melt.Completed += (s5, e5) => { try { v.Close(); } catch { } };
                    v.BeginAnimation(UIElement.OpacityProperty, melt);
                    var sure = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                    sure.Tick += (s6, e6) => { sure.Stop(); if (v.IsLoaded || v.IsVisible) { try { v.Close(); Log("launch veil: closed (its fade didn't finish)"); } catch { } } };
                    sure.Start();
                };
                hold.Start();
            };
            if (coverDark) { fadeIntoGame(); return; }
            var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(800)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            anim.Completed += (s, e) => fadeIntoGame();
            black.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // Play pressed: the loading screen goes up at once. Playnite only starts the game after its OnGameStarting script
        // hook has run (~1 s through PowerShell), and the loading screen used to wait for that hook too.
        private static DateTime earlyCoverAt = DateTime.MinValue;
        private static bool startingSeen;

        internal static void StartGameNow(Guid id)
        {
            if (Api == null) return;
            var g = Api.Database.Games.Get(id);
            // one identity per press: the delayed start below and B's cancel both act on this attempt only
            var attempt = new LaunchAttempt(id, g != null ? LocalGames.ProcessesInGameFolder(g) : new List<int>());
            launchAttempt = attempt;
            if (g != null && g.IsInstalled && !g.IsRunning && !launching)
            {
                try
                {
                    startingSeen = false;
                    launchedFromCollection = SeleneShell.CollectionActive;   // for After a game: where you were
                    QuietFocus(2500);     // the loading screen and the game take focus: not a move
                    if (previewArm != null) previewArm.Stop();   // no trailer card under the loading screen
                    if (!Features.On("loadingscreen")) { Api.StartGame(id); Log("play: Playnite's plain start (loading screen off)"); return; }
                    HoldMusicForLaunch(true);
                    PlayStartingSound();  // Playnite played it when IT started the game (~1 s after the press now)
                    var swCover = System.Diagnostics.Stopwatch.StartNew();
                    GameStarting(id);
                    earlyCoverAt = DateTime.Now;
                    Log("play: loading screen up at once (" + g.Name + ", built in " + swCover.ElapsedMilliseconds + " ms)");
                    // Playnite may still refuse (error dialog behind the loading screen): never keep covering it
                    var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                    guard.Tick += (s, e) => { guard.Stop(); if (launching && launchGameId == id && !startingSeen) AbortLaunchCover(IntPtr.Zero, "Playnite did not start the game"); };
                    guard.Start();
                }
                catch (Exception ex) { Log("play: early loading screen failed " + ex.Message); }
            }
            // Playnite starts the game on the UI thread and runs every plugin's "game starting" step there: the fade-in and the
            // logo froze mid-way and then jumped (looked like a delay). Let them play first; the loading screen covers the wait.
            Action start = () =>
            {
                if (!attempt.TryDispatch(launchAttempt)) { Log("play: cancelled before the game was started"); return; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Api.StartGame(id);
                Log("play: Playnite's start took " + sw.ElapsedMilliseconds + " ms on the UI thread");
            };
            if (launching && launchGameId == id)
            {
                // after the loading screen's own fade and the logo's rise (about 1.2 s): Playnite's start blocks the UI thread
                // for a few hundred ms, which froze the animation mid-way at 950 ms
                var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                later.Tick += (s, e) => { later.Stop(); if (launching && launchGameId == id) start(); else Log("play: cancelled before the game was started"); };
                later.Start();
            }
            else Application.Current.Dispatcher.BeginInvoke(start, System.Windows.Threading.DispatcherPriority.Background);
        }

        private static bool launchedFromCollection;

        public static void GameStarting(Guid gameId)
        {
            var app = Application.Current;
            if (app == null || !Features.On("loadingscreen")) return;
            QuietFocus(2500);
            app.Dispatcher.Invoke(new Action(() =>
            {
                var w = app.MainWindow;
                if (w == null) return;
                if (launching && (launchGameId == gameId || gameId == Guid.Empty) && (DateTime.Now - earlyCoverAt).TotalSeconds < 15)
                {
                    startingSeen = true;  // Playnite's hook for the launch we already covered: nothing to do, let it start the game
                    Log("game starting: loading screen already up");
                    return;
                }
                if (previewShowing)
                {
                    var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                    later.Tick += (s, e) => { later.Stop(); HidePreview(); };
                    later.Start();
                }
                var hub = FindByName(w, "WelcomeHubOverlay");
                leftFromHub = hub == null || hub.IsVisible;
                launching = true;
                launchStart = DateTime.Now;
                gameWindowSeenAt = DateTime.MinValue;
                gameGoneSince = DateTime.MinValue;
                coverArtGone = false;
                lastGameWindow = IntPtr.Zero;
                gamePids.Clear();
                gamePidsAt = DateTime.MinValue;
                soundSince = DateTime.MinValue;
                lastSoundAt = DateTime.MinValue;
                launchGameId = gameId;
                launchGrace = LaunchGrace(gameId);
                coverCancelledByGuide = false;
                steamDialog = IntPtr.Zero;
                steamDialogTicks = 0;
                var phase = System.Diagnostics.Stopwatch.StartNew();
                if (removeCoverOverlay != null) removeCoverOverlay();  // a previous one still fading
                SnapshotWindows();
                long tSnap = phase.ElapsedMilliseconds;
                ShowCoverWindow(w, gameId);
                long tCover = phase.ElapsedMilliseconds;
                // Steam's window is found and minimised on a thread of its own: on the UI thread it froze the loading screen's
                // first frames (about 180 ms of looking through every window)
                System.Threading.Tasks.Task.Factory.StartNew(MinimizeSteamMainWindow);
                Log("game starting: timings windows=" + tSnap + " ms, loading screen=" + (tCover - tSnap) + " ms");
                if (launchTimer == null)
                {
                    launchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    launchTimer.Tick += (s, e) => CheckGameReady();
                }
                launchTimer.Start();
                WatchForeground();
                if (Features.On("launchquiet")) GamePause.WatchLaunch();   // the game's first notes are turned down as they start
                Log("game starting: loading screen kept on top");
            }));
        }

        private static void CheckGameReady()
        {
            var w = Application.Current.MainWindow;
            if (!launching || w == null) { if (launchTimer != null) launchTimer.Stop(); return; }
            var waited = (DateTime.Now - launchStart).TotalSeconds;
            if (waited > 25)
            {
                // launchers (launcher -> shaders -> game) take longer: keep waiting while the game is alive
                bool alive = false;
                try { var lg = Api != null ? Api.Database.Games.Get(launchGameId) : null; alive = lg != null && (lg.IsRunning || LocalGames.ProcessesInGameFolder(lg).Count > 0); } catch { }
                if (!alive || waited > 120) { RevealGame(IntPtr.Zero, alive ? "timeout, 2 min" : "timeout, nothing running"); return; }
            }
            var me = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            double elapsed = (DateTime.Now - launchStart).TotalSeconds;
            if (elapsed > 3 && launchGameId != Guid.Empty && Api != null)
            {
                var g = Api.Database.Games.Get(launchGameId);
                if (g != null && !g.IsRunning && !g.IsLaunching) { AbortLaunchCover(IntPtr.Zero, "game is not starting"); return; }
            }
            // a Steam window that stays up (e.g. "Game already running" error) needs the user: show it
            var sd = FindSteamDialog();
            if (sd != IntPtr.Zero && sd == steamDialog) steamDialogTicks++; else { steamDialog = sd; steamDialogTicks = 0; }
            if (sd != IntPtr.Zero && steamDialogTicks >= 30) { AbortLaunchCover(sd, "Steam dialog"); return; }
            // ONE rule for every game (no per-game timing): the game is ready when it is really showing something -
            //   its own (nearly) full-screen window is up AND it is making sound (intro logos / menu music)
            // Fallbacks: window up 12 s but silent -> show it; sound for 10 s but no full-screen window (windowed game) ->
            // show it; nothing of the game running after 25 s -> back to Playnite (timeouts above).
            // (A big window alone revealed too early: launchers, splash screens, black windows before the first frame.)
            var found = FindFullscreenGameWindow(me);
            var now = DateTime.Now;
            if ((now - gamePidsAt).TotalSeconds >= 1)
            {
                gamePidsAt = now;
                gamePids.Clear();
                try { var lg = Api != null ? Api.Database.Games.Get(launchGameId) : null; if (lg != null) foreach (var pid in LocalGames.ProcessesInGameFolder(lg)) gamePids.Add(pid); } catch { }
            }
            // the game's window came and went: closed (or crashed) while the loading screen waited for it to be ready. The
            // cover went on waiting (for sound, or up to 2 min while a launcher kept the game "running") and Playnite sat
            // black behind it (#24). Nothing of the game left in its folder for 1 s -> back to Playnite; processes still
            // there but no game window for 8 s -> back too.
            if (gameWindowSeenAt != DateTime.MinValue && found == IntPtr.Zero && (lastGameWindow == IntPtr.Zero || !IsWindow(lastGameWindow)))
            {
                if (gameGoneSince == DateTime.MinValue) gameGoneSince = now;
                var gone = (now - gameGoneSince).TotalSeconds;
                if ((gamePids.Count == 0 && gone >= 1) || gone >= 8) { AbortLaunchCover(IntPtr.Zero, gamePids.Count == 0 ? "the game closed while loading" : "the game's window is gone (8 s)"); return; }
            }
            else gameGoneSince = DateTime.MinValue;
            uint foundPid = 0;
            if (found != IntPtr.Zero) GetWindowThreadProcessId(found, out foundPid);
            // a full-screen window of some other program (not from the game's folder) doesn't count as the game
            bool ours = found != IntPtr.Zero && (gamePids.Count == 0 || gamePids.Contains((int)foundPid));
            if (found != IntPtr.Zero && found != lastGameWindow)
            {
                // COD sat behind the loading screen without focus until a button was pressed: the game gets the focus as soon
                // as its window shows (the loading screen stays on top)
                lastGameWindow = found;
                if (gameWindowSeenAt == DateTime.MinValue) gameWindowSeenAt = now;
                if (!coverCancelledByGuide)
                {
                    // the game's window often switches the display into its own full-screen mode, which blanks the screen
                    // for a moment (no window can cover that, and it comes on the game's own schedule): the loading screen
                    // fades to black first and the game gets the focus once it's dark, so the blank can't be seen
                    var gameHwnd = found;
                    Action focusGame = () => { if (launching && IsWindow(gameHwnd)) ForceForegroundAsync(gameHwnd, KeepCoverOnTop); };
                    if (!coverArtGone) { coverArtGone = true; ArtToLogo(focusGame); }
                    else focusGame();
                }
            }
            if (++soundTick % 3 == 0)
            {
                var listen = new HashSet<int>(gamePids);
                if (foundPid != 0) listen.Add((int)foundPid);
                // the game's intro stays quiet behind the loading screen; the check hears it through the lowered volume
                if (!coverCancelledByGuide && Features.On("launchquiet")) GamePause.Hush(listen);
                float peak = GamePause.PeakOf(listen);
                if (GamePause.Hushing) peak /= GamePause.HushLevel;
                if (peak > 0.01f) { if (soundSince == DateTime.MinValue) { soundSince = now; Log("launch: the game is making sound (" + (now - launchStart).TotalSeconds.ToString("0.0") + " s)"); } lastSoundAt = now; }
                else if (soundSince != DateTime.MinValue && (now - lastSoundAt).TotalSeconds > 2) soundSince = DateTime.MinValue;  // a blip, not the game
            }
            bool sounding = soundSince != DateTime.MinValue && (now - soundSince).TotalSeconds >= 0.4;
            IntPtr readyWindow = IntPtr.Zero;
            string why = null;
            if (ours && sounding) { readyWindow = found; why = "game window + sound"; }
            else if (gameWindowSeenAt != DateTime.MinValue && (now - gameWindowSeenAt).TotalSeconds >= 12 * launchGrace && lastGameWindow != IntPtr.Zero && IsWindow(lastGameWindow))
            { readyWindow = lastGameWindow; why = "game window up 12 s, no sound"; }
            else if (soundSince != DateTime.MinValue && (now - soundSince).TotalSeconds >= 10 && found == IntPtr.Zero)
            {
                foreach (var pid in gamePids)
                {
                    try { var h = System.Diagnostics.Process.GetProcessById(pid).MainWindowHandle; if (h != IntPtr.Zero && IsWindowVisible(h)) { readyWindow = h; break; } } catch { }
                }
                why = "game sound for 10 s (windowed game)";
                if (readyWindow == IntPtr.Zero) readyWindow = GetForegroundWindow();
            }
            if (why != null)
            {
                if (coverCancelledByGuide)
                {
                    if (launchAttempt != null) launchAttempt.Complete();
                    GamePause.Unhush();
                    HoldMusicForLaunch(false);
                    launching = false;
                    launchTimer.Stop();
                    gameWindow = readyWindow;
                    PlayReadySound();
                    if (DateTime.Now < stealGuardUntil || w.Topmost) { stealGuardStart = DateTime.Now; stealGuardUntil = DateTime.Now.AddSeconds(8); }  // it may grab once more
                    Log("game ready in the background (Resume to play)");
                    return;
                }
                RevealGame(readyWindow, why);
                return;
            }
            // Wilds marks its own start-up windows topmost too and the newest topmost window wins: push the loading screen
            // back above them every tick, without taking focus from the game
            var coverHwnd = coverWindow != null ? new System.Windows.Interop.WindowInteropHelper(coverWindow).Handle : IntPtr.Zero;
            // ...but once the art has darkened to black, let the game come over it: pushing back over a game that is already
            // drawing made its first frame blink through for a moment
            if (coverHwnd != IntPtr.Zero && !coverDark) SetWindowPos(coverHwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);  // HWND_TOPMOST, NOSIZE | NOMOVE | NOACTIVATE
        }

        private static void RevealGame(IntPtr game, string why)
        {
            if (launchAttempt != null) launchAttempt.Complete();   // the game is there: B no longer cancels it
            GamePause.FadeBack(2000, 2000);   // the ready sound first, then the game's sound fades in
            HoldMusicForLaunch(false);
            launching = false;
            if (launchTimer != null) launchTimer.Stop();
            var w = Application.Current.MainWindow;
            if (w == null) return;
            var root = w.Content as UIElement;
            PlayReadySound();
            if (game != IntPtr.Zero)
            {
                // where the game really runs from (fixes a wrong or generic game folder automatically)
                uint gamePid;
                GetWindowThreadProcessId(game, out gamePid);
                LocalGames.LearnFolder(launchGameId, LocalGames.ExeOf((int)gamePid));
            }
            Log("launch cover: revealing game (" + why + ", " + (DateTime.Now - launchStart).TotalSeconds.ToString("0.0") + " s)");
            if (root != null) root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));  // main window black behind
            w.Topmost = false;
            CloseCoverWindow(true, () =>
            {
                var target = game != IntPtr.Zero ? game : GetForegroundWindow();
                var me = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (target != IntPtr.Zero && target != me) { gameWindow = target; ForceForegroundAsync(target, null); }
                else if (root != null) FadeInRoot(root);  // nothing to show: Playnite back
            });
        }

        // the theme's audio\Events\GameStarting.wav, moved to the media folder: played on the Play press
        private static MediaPlayer startingPlayer;
        private static void PlayStartingSound()
        {
            try
            {
                string path = SelenePaths.ThemeAudio("GameStarting.wav");
                if (!System.IO.File.Exists(path)) return;
                if (startingPlayer == null) { startingPlayer = new MediaPlayer(); startingPlayer.Open(new Uri(path)); }
                startingPlayer.Stop();
                startingPlayer.Position = TimeSpan.Zero;
                startingPlayer.Volume = Isolated ? 0 : 0.5; startingPlayer.Play();
            }
            catch { }
        }

        // the old theme's audio\Events\GameStarted.wav was moved to the media folder (Playnite played it mid-loading)
        private static MediaPlayer readyPlayer;
        private static void PlayReadySound()
        {
            try
            {
                string path = SelenePaths.ThemeAudio("GameReady.wav");
                if (!System.IO.File.Exists(path)) return;
                if (readyPlayer == null) { readyPlayer = new MediaPlayer(); readyPlayer.Open(new Uri(path)); }
                readyPlayer.Position = TimeSpan.Zero;
                readyPlayer.Volume = Isolated ? 0 : 1.0;
                // Playnite hides (and mutes itself) in the tray while a game starts: unmute just for the chime
                bool hidden = CloseToTray.IsHidden;
                if (hidden) CloseToTray.SetOwnAudioMute(false);
                readyPlayer.Play();
                if (hidden)
                {
                    var remute = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                    remute.Tick += (s, e) => { remute.Stop(); if (CloseToTray.IsHidden) CloseToTray.SetOwnAudioMute(true); };
                    remute.Start();
                }
            }
            catch (Exception ex) { Log("ready sound failed: " + ex.Message); }
        }

        // launch failed or Steam wants attention: drop the cover, show Playnite (or the dialog)
        private static void AbortLaunchCover(IntPtr bringForward, string why)
        {
            if (launchAttempt != null && !launchAttempt.Cancelled) launchAttempt.Complete();   // a failed launch: nothing left to cancel
            GamePause.Unhush();
            HoldMusicForLaunch(false);
            launching = false;
            if (launchTimer != null) launchTimer.Stop();
            var w = Application.Current.MainWindow;
            if (w != null)
            {
                w.Topmost = false;
                var root = w.Content as UIElement;
                if (root != null) FadeInRoot(root);
                // Playnite minimizes itself as a game starts: back on screen first (minimized, the loading screen's fade never
                // finished and its black window stayed over everything, #24)
                var mh = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (bringForward == IntPtr.Zero && mh != IntPtr.Zero && IsIconic(mh)) { ShowWindow(mh, 9 /* SW_RESTORE */); Log("launch cover: Playnite restored"); }
            }
            var sweep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            sweep.Tick += (s, e) => { sweep.Stop(); int n = CloseLeftoverCovers(); if (n > 0) Log("launch cover: " + n + " leftover window(s) closed"); };
            sweep.Start();
            // Playnite hides in the tray while a game starts: bring it back, or there is only the black desktop
            // closed at once: the fade-into-the-game (a veil melting away) has no game to show, and with Playnite just restored its
            // animations could stall and leave the black veil up (#24)
            CloseCoverWindow(false, () => { if (bringForward != IntPtr.Zero) ForceForeground(bringForward); else if (w != null) { if (CloseToTray.IsHidden) CloseToTray.ShowFromTray(); w.Activate(); FocusRowSoon(0); } });
            Log("launch cover: aborted (" + why + ")");
            QueueScreenRefresh("loading screen ended without the game", 600);
        }

        private static readonly string[] SteamProcesses = { "steam", "steamwebhelper" };

        private static string ProcessNameOf(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { return ""; }
        }

        // a new, visible, dialog-sized Steam window (not its main client window)
        private static IntPtr FindSteamDialog()
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h) || preLaunchWindows.Contains(h)) return true;
                if (Array.IndexOf(SteamProcesses, ProcessNameOf(h)) < 0) return true;
                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                int wdt = r.Right - r.Left, hgt = r.Bottom - r.Top;
                if (wdt < 250 || hgt < 120 || wdt > 1400 || hgt > 900) return true;  // dialogs only
                int ex = GetWindowLong(h, -20);
                if ((ex & 0x20) != 0 || (ex & 0x80) != 0) return true;  // overlays / tool windows
                result = h;
                return false;
            }, IntPtr.Zero);
            return result;
        }

        // Steam's library window flashed up for a moment when it started the game: tuck it away first
        private static void MinimizeSteamMainWindow()
        {
            try
            {
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h) || IsIconic(h)) return true;
                    if (Array.IndexOf(SteamProcesses, ProcessNameOf(h)) < 0) return true;
                    RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    if (r.Right - r.Left < 900 || r.Bottom - r.Top < 600) return true;  // the big client window only
                    ShowWindow(h, 7);  // SW_SHOWMINNOACTIVE
                    Log("launch: Steam main window minimised");
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { Log("steam minimise failed: " + ex.Message); }
        }

        // the loading screen's art, drawn on its own thread (let go a while after the screen has gone)
        private static OffThread coverArtStage;

        private static void StopCoverArtLater()
        {
            var stage = coverArtStage;
            if (stage == null) return;
            coverArtStage = null;
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            later.Tick += (s, e) => { later.Stop(); stage.Stop(); };
            later.Start();
        }

        private static void EndLaunchCover()
        {
            if (!launching) return;
            launching = false;
            if (launchAttempt != null) launchAttempt.Complete();
            StopCoverArtLater();
            if (launchTimer != null) launchTimer.Stop();
            var w = Application.Current.MainWindow;
            if (w != null) w.Topmost = false;
            CloseCoverWindow(false, null);
            Log("launch cover: cancelled by Guide");
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder sb, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

        private static readonly string[] NotGameProcesses = { "explorer", "steam", "steamwebhelper", "nvidia overlay", "nvcontainer", "nvidia app", "textinputhost",
            "applicationframehost", "shellexperiencehost", "searchhost", "startmenuexperiencehost", "gamebar", "gamebarftserver", "discord", "playnite.fullscreenapp", "playnite.desktopapp" };

        private static readonly HashSet<IntPtr> preLaunchWindows = new HashSet<IntPtr>();

        private static void SnapshotWindows()
        {
            preLaunchWindows.Clear();
            EnumWindows((h, l) => { preLaunchWindows.Add(h); return true; }, IntPtr.Zero);
        }

        private static IntPtr FindFullscreenGameWindow(IntPtr playnite)
        {
            var mi = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(playnite, 2), ref mi)) return IntPtr.Zero;
            int mw = mi.Monitor.Right - mi.Monitor.Left, mh = mi.Monitor.Bottom - mi.Monitor.Top;
            uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr result = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (h == playnite || !IsWindowVisible(h) || IsIconic(h)) return true;
                int st = GetWindowLong(h, -16);  // GWL_STYLE
                if ((st & 0x00C00000) == 0x00C00000) return true;  // WS_CAPTION: a normal window sized to the screen, not full-screen
                int ex = GetWindowLong(h, -20);  // GWL_EXSTYLE
                if ((ex & 0x20) != 0 || (ex & 0x80) != 0 || (ex & 0x08000000) != 0) return true;  // transparent / tool / no-activate (overlays)
                int cloaked;
                if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                int ix = Math.Min(r.Right, mi.Monitor.Right) - Math.Max(r.Left, mi.Monitor.Left);
                int iy = Math.Min(r.Bottom, mi.Monitor.Bottom) - Math.Max(r.Top, mi.Monitor.Top);
                // (nearly) the whole screen with no caption: COD's window sat a little short of full size for ~33 s while its
                // music already played. A maximised normal app has a caption (skipped above).
                if (ix < mw * 0.9 || iy < mh * 0.9) return true;
                if (preLaunchWindows.Contains(h)) return true;  // only windows that appeared after Play
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == ownPid) return true;
                var cls = new System.Text.StringBuilder(64);
                GetClassName(h, cls, 64);
                var c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c.StartsWith("CEF-OSC")) return true;
                string name;
                try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { return true; }
                if (Array.IndexOf(NotGameProcesses, name) >= 0) return true;
                result = h;
                return false;
            }, IntPtr.Zero);
            return result;
        }

        private static bool CanReturnToGame() { return gameWindow != IntPtr.Zero && IsWindow(gameWindow) && AnyGameRunning(); }

        // While the loading screen is up, any window coming to the front (the game's first window, its launcher) is put back
        // under the art at once; the 100 ms check alone let a black game window show for a moment.
        private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        private static WinEventProc foregroundProc;   // kept alive: Windows calls it
        private static IntPtr foregroundHook;

        private static void WatchForeground()
        {
            if (foregroundHook != IntPtr.Zero) return;
            foregroundProc = (h, evt, hwnd, o, c, t, time) => { if (launching) KeepCoverOnTop(); };
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundProc, 0, 0, 0);   // EVENT_SYSTEM_FOREGROUND, out of context
        }

        private static void KeepCoverOnTop()
        {
            if (coverWindow == null || coverDark) return;   // once dark, the game may come over it
            var coverHwnd = new System.Windows.Interop.WindowInteropHelper(coverWindow).Handle;
            if (coverHwnd != IntPtr.Zero) SetWindowPos(coverHwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // TOPMOST, NOSIZE | NOMOVE | NOACTIVATE
        }

        // the same off the UI thread: attaching to the game's input thread waits for that thread to answer, and a game in the
        // middle of its display-mode switch answered seconds later, freezing the loading screen's fade (and the veil's) with it
        private static void ForceForegroundAsync(IntPtr hwnd, Action then)
        {
            var app = Application.Current;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try { ForceForeground(hwnd); } catch (Exception ex) { Log("foreground: " + ex.Message); }
                if (then != null && app != null) app.Dispatcher.BeginInvoke(then);
            });
        }

        private static void ForceForeground(IntPtr hwnd)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);  // SW_RESTORE
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            bool attached = fg != 0 && fg != me && AttachThreadInput(me, fg, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(me, fg, false);
        }

        // console-style switch: Playnite settles in from a slight zoom while fading in ...
        private static ScaleTransform RootScale(FrameworkElement root)
        {
            var st = root.RenderTransform as ScaleTransform;
            if (st != null && !st.IsFrozen) return st;
            if (root.RenderTransform != null && !root.RenderTransform.Value.IsIdentity) return null;  // theme owns it: fade only
            st = new ScaleTransform();
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = st;
            return st;
        }

        private static void PlayEnterAnimation(Window w)
        {
            var root = w.Content as FrameworkElement;
            if (root == null) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(320);
            // animate one cached bitmap instead of re-rendering every tile/effect per frame (first Guide press of each
            // game stuttered while the game was still loading and busy on the GPU)
            root.CacheMode = new BitmapCache(1.0) { SnapsToDevicePixels = false };
            var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            fadeIn.Completed += (s2, e2) => root.CacheMode = null;
            root.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            var sc = RootScale(root);
            if (sc != null)
            {
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, 1, dur) { EasingFunction = ease });
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, 1, dur) { EasingFunction = ease });
            }
        }

        // ... and on the way back it zooms towards you and fades out, then the game comes forward
        // Back to the game, mirrored from the way in: the game wakes and is restored behind Selene at once; Selene's home recedes
        // while the game's picture (taken at the Home press: the frozen game still shows exactly that) grows back to full size;
        // then the game gets the screen under the finished picture, so the hand-over can't be seen.
        private static void ReturnWithCrossfade(Window w, FrameworkElement root, IntPtr target)
        {
            StopStealGuard(w);
            HidePreview();
            w.Topmost = true;   // the woken game is restored under Selene, not over it
            GamePause.Resume(false);
            if (IsIconic(target)) ShowWindow(target, 4);   // (frozen by Selene itself: SW_SHOWNOACTIVATE, behind Selene)
            if (!AddSnapAdorner(w, lastGameSnap)) { w.Topmost = false; ForceForeground(target); return; }
            var ad = snapAdorner;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(200);
            ad.Opacity = 0;
            var adScale = ad.RenderTransform as ScaleTransform;
            if (adScale != null)
            {
                adScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
                adScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
            }
            root.CacheMode = new BitmapCache(1.0) { SnapsToDevicePixels = false };
            var sc = RootScale(root);
            if (sc != null)
            {
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.04, dur) { EasingFunction = ease });
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.04, dur) { EasingFunction = ease });
            }
            var show = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            show.Completed += (s, e) =>
            {
                // the picture is up: Selene goes blank under it and the game takes the screen
                root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
                root.CacheMode = null;
                w.Topmost = false;
                ForceForeground(target);
                var verify = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                int attempt = 0;
                verify.Tick += (s2, e2) =>
                {
                    attempt++;
                    bool ok = GetForegroundWindow() == target;
                    if (!ok && attempt == 1) { ForceForeground(target); return; }
                    if (!ok && attempt < 4) return;
                    verify.Stop();
                    if (snapAdorner == ad) RemoveSnapAdorner();
                    Log("guide -> back to game with the crossfade (foreground=" + ok + ", attempts=" + attempt + ")");
                    if (!ok && w.IsActive) PlayEnterAnimation(w);   // the game didn't come: Selene back, never a frozen picture
                };
                verify.Start();
            };
            ad.BeginAnimation(UIElement.OpacityProperty, show);
        }

        private static void ReturnToGame(Window w)
        {
            var target = gameWindow;
            var root = w.Content as FrameworkElement;
            CloseSnapWindow(); RemoveSnapAdorner();
            if (root != null && target != IntPtr.Zero && lastGameSnap != null && GamePause.Frozen && Features.On("guidefade")) { ReturnWithCrossfade(w, root, target); return; }
            GamePause.Resume();   // at once: the game wakes while Selene fades
            StopStealGuard(w);  // the user asked for the game
            HidePreview();
            Action go = () =>
            {
                GamePause.Resume();
                ForceForeground(target);
                // Playnite stays blank behind the game (restoring it there made Windows show that stale bright frame
                // on the next Guide press); Activated fades it back in. Retry once, and if the game still didn't
                // come forward, fade Playnite back in rather than leaving a black screen.
                var verify = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                int attempt = 0;
                verify.Tick += (s, e) =>
                {
                    attempt++;
                    bool ok = GetForegroundWindow() == target;
                    if (!ok && attempt == 1) { ForceForeground(target); return; }
                    verify.Stop();
                    Log("guide -> back to game (foreground=" + ok + ", attempts=" + attempt + ")");
                    if (!ok && w.IsActive) PlayEnterAnimation(w);
                };
                verify.Start();
            };
            if (root == null) { go(); return; }
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var dur = TimeSpan.FromMilliseconds(160);
            root.CacheMode = new BitmapCache(1.0) { SnapsToDevicePixels = false };
            var fade = new DoubleAnimation(0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
            fade.Completed += (s, e) => { root.CacheMode = null; go(); };
            var st = RootScale(root);
            if (st != null)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, dur) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, dur) { EasingFunction = ease });
            }
            root.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // Playnite's "After launching a game: Minimize" fires several seconds after a Steam game starts (when it detects
        // the process), which threw the first Guide press straight back to the game. GameStarting() already blanks
        // Playnite behind the game, so switch the option to None (Playnite saves it on exit like a normal change).
        private static void KeepPlayniteOpenOnLaunch()
        {
            try
            {
                var appType = Type.GetType("Playnite.PlayniteApplication, Playnite");
                var current = appType != null ? appType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null, null) : null;
                var settings = current != null ? appType.GetProperty("AppSettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(current, null) : null;
                var prop = settings != null ? settings.GetType().GetProperty("AfterLaunch") : null;
                if (prop == null) { Log("after-launch: setting not found"); return; }
                var none = Enum.Parse(prop.PropertyType, "None");
                if (!Equals(prop.GetValue(settings, null), none)) { prop.SetValue(settings, none, null); Log("after-launch: Minimize -> None"); }
                // Volume: 100% (set here earlier) was much louder than games once the system volume is raised for them.
                // Bring the old forced 100% down once (music 45%, sounds 50%); any level the user picks afterwards stays.
                var fs = settings.GetType().GetProperty("Fullscreen") != null ? settings.GetType().GetProperty("Fullscreen").GetValue(settings, null) : null;
                if (fs != null)
                    foreach (var pair in new[] { new KeyValuePair<string, double>("BackgroundVolume", 0.45), new KeyValuePair<string, double>("InterfaceVolume", 0.5) })
                    {
                        var vp = fs.GetType().GetProperty(pair.Key);
                        if (vp == null) continue;
                        if (Convert.ToDouble(vp.GetValue(fs, null)) >= 0.99) { vp.SetValue(fs, Convert.ChangeType(pair.Value, vp.PropertyType), null); Log("volume: " + pair.Key + " 100% -> " + (pair.Value * 100) + "%"); }
                    }
            }
            catch (Exception ex) { Log("after-launch failed: " + ex.Message); }
        }



        private static object FindSettingsWith(object o, string propName, int depth)
        {
            if (o == null || depth < 0) return null;
            if (o.GetType().GetProperty(propName) != null) return o;
            foreach (var pr in o.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (pr.GetIndexParameters().Length > 0 || !pr.Name.Contains("Settings")) continue;
                object v;
                try { v = pr.GetValue(o, null); } catch { continue; }
                var r = FindSettingsWith(v, propName, depth - 1);
                if (r != null) return r;
            }
            return null;
        }

        private static readonly Guid UniPlaySongId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static object upsSettings;

        // UniPlaySong "continues the same song" for the home music (its private _lastDefaultMusicPath), so every start
        // came back to one track. Seed that with a random track from the folder: a different song per Playnite start,
        // and UniPlaySong still picks a new random one when a song ends.
        private static void ShuffleFirstHomeSong(object plugin)
        {
            try
            {
                var folder = Convert.ToString(upsSettings.GetType().GetProperty("DefaultMusicFolderPath").GetValue(upsSettings, null));
                if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)) return;
                var files = System.IO.Directory.GetFiles(folder, "*.mp3");
                if (files.Length < 2) return;
                var svc = FindWithField(plugin, "_lastDefaultMusicPath", 4);
                if (svc == null) { Log("music shuffle: playback service not found"); return; }
                var pick = files[new Random().Next(files.Length)];
                svc.GetType().GetField("_lastDefaultMusicPath", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(svc, pick);
                Log("music shuffle: first home song " + System.IO.Path.GetFileName(pick));
            }
            catch (Exception ex) { Log("music shuffle: " + ex.Message); }
        }

        private static object FindWithField(object root, string field, int depth)
        {
            if (root == null) return null;
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = root.GetType();
            if (type.GetField(field, any) != null) return root;
            if (depth <= 0 || type.Namespace == "System" || type.IsPrimitive) return null;
            foreach (var f in type.GetFields(any))
            {
                if (f.FieldType.IsValueType || f.FieldType == typeof(string) || f.FieldType.IsArray) continue;
                object v; try { v = f.GetValue(root); } catch { continue; }
                var hit = FindWithField(v, field, depth - 1);
                if (hit != null) return hit;
            }
            return null;
        }


        // Home: quiet, a game's own song only while its trailer card is open (like the legacy theme)
        private static void UpdateMusicOverride() { SetMusic(previewShowing ? "trailer" : "home"); }


        // the window fills (nearly) the whole monitor Playnite is on (a game / fullscreen app, not a popup)
        private static bool CoversScreen(IntPtr h, IntPtr playnite)
        {
            if (h == IntPtr.Zero) return false;
            var mi = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(playnite, 2), ref mi)) return false;
            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            int mw = mi.Monitor.Right - mi.Monitor.Left, mh = mi.Monitor.Bottom - mi.Monitor.Top;
            int ix = Math.Min(r.Right, mi.Monitor.Right) - Math.Max(r.Left, mi.Monitor.Left);
            int iy = Math.Min(r.Bottom, mi.Monitor.Bottom) - Math.Max(r.Top, mi.Monitor.Top);
            return ix >= mw * 0.9 && iy >= mh * 0.9;
        }

        private static void StartGuideWatcher()
        {
            var dispatcher = Application.Current.Dispatcher;
            var mw = Application.Current.MainWindow;
            if (mw != null)
            {
                mw.Background = Brushes.Black;
                mw.StateChanged += (s, e) => { if (mw.WindowState == WindowState.Minimized) StopStealGuard(mw); };
                mw.Deactivated += (s, e) =>
                {
                    if (mw.WindowState == WindowState.Minimized) { StopStealGuard(mw); return; }  // minimized on purpose: let it go
                    if (launching && !coverCancelledByGuide) return;  // the loading screen stays up (launch cover) until the game is ready
                    if (DateTime.Now < stealGuardUntil)
                    {
                        var me2 = new System.Windows.Interop.WindowInteropHelper(mw).Handle;
                        var reclaim = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                        reclaim.Tick += (s3, e3) =>
                        {
                            reclaim.Stop();
                            if (mw.WindowState == WindowState.Minimized) { StopStealGuard(mw); return; }
                            var fg = GetForegroundWindow();
                            uint fgPid;
                            GetWindowThreadProcessId(fg, out fgPid);
                            string who = "?";
                            try { who = System.Diagnostics.Process.GetProcessById((int)fgPid).ProcessName; } catch { }
                            bool own = fgPid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                            bool otherScreen = fg != IntPtr.Zero && MonitorFromWindow(fg, 2) != MonitorFromWindow(me2, 2);
                            if (!own && !otherScreen && DateTime.Now < stealGuardUntil)
                            {
                                // any window jumping over Playnite on the TV right after Guide (the game or a helper of it)
                                if (fg != IntPtr.Zero && fg != me2) gameWindow = fg;  // that is where the game really lives now
                                // Playnite is Topmost meanwhile, so the grab is invisible; keep guarding while it continues
                                var extended = DateTime.Now.AddSeconds(3);
                                var cap = stealGuardStart.AddSeconds(launching ? 120 : 30);  // a background load can take a while
                                if (extended > stealGuardUntil) stealGuardUntil = extended < cap ? extended : cap;
                                ForceForeground(me2);
                                mw.Activate();
                                Log("guide: " + who + " jumped to the front, Playnite kept");
                            }
                            else Log("guide guard: focus went to " + who + " (own=" + own + ", other screen=" + otherScreen + ")");
                        };
                        reclaim.Start();
                        return;
                    }
                    var hub = FindByName(mw, "WelcomeHubOverlay");
                    leftFromHub = hub == null || hub.IsVisible;
                    // Windows shows the last frame Playnite drew when it comes back, before any animation can run (a
                    // full-brightness flash, then the fade): blank the content - but ONLY when a full-screen window of another
                    // program (a game) really covers Playnite. Blanking on every focus loss left Playnite black on screen
                    // behind popups, notifications and small windows.
                    var root = mw.Content as UIElement;
                    if (root == null) return;
                    var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                    check.Tick += (s2, e2) =>
                    {
                        check.Stop();
                        if (mw.IsActive) return;
                        if (ProgressToast.Busy) return;   // a progress notification moved the focus around: not a game
                        uint pid;
                        var fgw = GetForegroundWindow();
                        GetWindowThreadProcessId(fgw, out pid);
                        var me = new System.Windows.Interop.WindowInteropHelper(mw).Handle;
                        if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;  // one of Playnite's own windows
                        if (MonitorFromWindow(fgw, 2) != MonitorFromWindow(me, 2)) return;  // e.g. clicking the desk monitor
                        if (!CoversScreen(fgw, me)) return;  // a popup / small window: Playnite stays as it is
                        root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
                    };
                    check.Start();
                };
                // back by any route (Guide, Alt-Tab, game exit): fade in from the blank frame
                mw.Activated += (s, e) =>
                {
                    var root = mw.Content as UIElement;
                    if (root != null && root.Opacity < 0.01 && !guideSwitching) PlayEnterAnimation(mw);
                    if ((DateTime.Now - gameEndedAt).TotalSeconds < 90) { gameEndedAt = DateTime.MinValue; QueueScreenRefresh("back from a game", 400); }
                    RunPendingRefresh();
                };
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => mw.Dispatcher.BeginInvoke(new Action(() => QueueScreenRefresh("display mode changed", 700)));
                mw.StateChanged += (s, e) => { if (mw.WindowState != WindowState.Minimized && mw.IsActive) RunPendingRefresh(); };
                var h = new System.Windows.Interop.WindowInteropHelper(mw).Handle;
                if (h != IntPtr.Zero) DisableWindowTransitions(h); else mw.SourceInitialized += (s, e) => DisableWindowTransitions(new System.Windows.Interop.WindowInteropHelper(mw).Handle);
            }
            var thread = new System.Threading.Thread(() =>
            {
                bool wasDown = false, bWasDown = false;
                DateTime last = DateTime.MinValue;
                while (true)
                {
                    bool down = false, bDown = false;
                    try
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            XInputStateEx s;
                            if (XInputGetStateEx(i, out s) != 0) continue;
                            if ((s.Buttons & GuideBit) != 0) down = true;
                            if ((s.Buttons & 0x2000) != 0) bDown = true;  // B
                        }
                    }
                    catch (Exception ex) { Log("guide watcher stopped: " + ex.Message); return; }
                    if (down && !wasDown && (DateTime.Now - last).TotalMilliseconds > 200)
                    {
                        last = DateTime.Now;
                        dispatcher.BeginInvoke(new Action(GoHomeFromGuide));
                    }
                    wasDown = down;
                    if (bDown && !bWasDown && launching) dispatcher.BeginInvoke(new Action(() => { if (launching) CancelLaunch(); }));   // (the game in front: Playnite hears no pad then)
                    bWasDown = bDown;
                    System.Threading.Thread.Sleep(30);
                }
            }) { IsBackground = true, Name = "SeleneGuideWatcher" };
            if (Isolated) { Log("dev isolated: controllers not read"); return; }
            thread.Start();
        }

        private static void FocusRowSoon(int ms)
        {
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var row = LiveRow();
                if (row == null || !row.IsVisible || row.IsKeyboardFocusWithin) return;
                int idx = row.SelectedIndex >= 0 ? row.SelectedIndex : FirstGameIndex(row);
                if (idx < 0) return;
                var sv = FindChild<ScrollViewer>(row);
                if (row.ItemContainerGenerator.ContainerFromIndex(idx) == null && sv != null) { sv.ScrollToHorizontalOffset(Math.Max(0, idx - AnchorSlot)); row.UpdateLayout(); }
                var c = row.ItemContainerGenerator.ContainerFromIndex(idx) as UIElement;
                if (c == null) return;
                c.Focus();
                ArmPreview(row);
                Log("row focus -> " + row.Items[idx] + " (in row=" + row.IsKeyboardFocusWithin + ")");
            };
            timer.Start();
        }

        // ---- Start / View: a short press, or held for 0.6 s (fires while still held, like a console) ----
        private static string holdButton;
        private static bool holdFired;
        private static System.Windows.Threading.DispatcherTimer holdTimer;

        private static bool OnHomeOrLibrary()
        {
            var w = Application.Current.MainWindow;
            if (w == null || !PlayniteInFront()) return false;
            var details = FindByName(w, "PART_ElemGameDetails");
            if (details != null && details.IsVisible) return false;
            var hub = FindByName(w, "WelcomeHubOverlay");
            var library = FindByName(w, "SeleneLibrary");
            return (hub != null && hub.IsVisible) || (library != null && library.IsVisible);
        }

        private static void BeginHold(string button)
        {
            holdButton = button;
            holdFired = false;
            if (holdTimer == null)
            {
                holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                holdTimer.Tick += (s, e) =>
                {
                    holdTimer.Stop();
                    if (holdButton == null) return;
                    holdFired = true;
                    if (holdButton == "Start") { Ui.Confirm(); LoginScene.OpenWith(() => new SettingsScreen()); Log("hold start -> Selene settings"); }
                    else OpenLibrarySearch();
                };
            }
            holdTimer.Stop();
            holdTimer.Start();
        }

        private static void EndHold(bool released)
        {
            var button = holdButton;
            holdButton = null;
            if (holdTimer != null) holdTimer.Stop();
            if (!released || holdFired || button == null) return;
            if (button == "Start") SeleneShell.OpenMenu();
            else if (Features.On("viewswitch")) ToggleHomeLibrary();
        }

        // hold View: Selene's Find a game (its keyboard, the matching games as you type)
        private static void OpenLibrarySearch()
        {
            var w = Application.Current.MainWindow;
            if (w == null) return;
            HidePreview();
            Ui.Play("OpenPanel.wav", 0.8);
            LoginScene.OpenWith(() => new SearchScreen());
            Log("hold view -> find a game");
        }

        // (Playnite's own search in the library, no longer used)
        private static void OpenPlayniteSearch()
        {
            var w = Application.Current.MainWindow;
            if (w == null) return;
            SeleneShell.ShowLibrary();
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                var search = FindByName(w, "PART_ButtonSearch") as System.Windows.Controls.Primitives.ButtonBase;
                if (search != null && search.Command != null && search.Command.CanExecute(search.CommandParameter)) search.Command.Execute(search.CommandParameter);
                Log("hold view -> library search" + (search == null ? " (no search button)" : ""));
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // LB / RB in Collection: the same five tabs as Spotlight (one choice for both screens); Collection applies the
        // matching filter preset. False when Collection isn't showing.
        private static bool CyclePreset(int dir)
        {
            var w = Application.Current.MainWindow;
            var library = w != null ? FindByName(w, "SeleneLibrary") : null;
            if (library == null || !library.IsVisible) return false;
            rowFilter = (RowFilter + dir + RowFilters.Length) % RowFilters.Length;
            try { System.IO.File.WriteAllText(RowFilterFile, RowFilters[rowFilter]); } catch { }
            RenderFilterTabs(w, dir);
            ApplyCollectionFilter();
            var row = LiveRow();
            if (row != null) { filterDir = dir; ApplyRowFilter(row); }   // Spotlight shows the same when you go back
            Ui.Tick();
            return true;
        }

        public static void ApplyCollectionFilter()
        {
            try
            {
                var key = RowFilters[RowFilter];
                var id = IsPresetTab(key) ? PresetOf(key) : CollectionPresets.For(key);
                if (Api == null || id == Guid.Empty) return;
                if (Api.MainView.GetActiveFilterPreset() != id)
                {
                    var took = System.Diagnostics.Stopwatch.StartNew();
                    Api.MainView.ApplyFilterPreset(id);
                    Log("collection filter -> " + RowFilters[RowFilter] + " (" + took.ElapsedMilliseconds + " ms)");
                }
            }
            catch (Exception ex) { Log("collection filter: " + ex.Message); }
        }

        // the View button on the home or the library: switches to the other one (false elsewhere: details, overlays)
        private static bool ToggleHomeLibrary()
        {
            var w = Application.Current.MainWindow;
            if (w == null || !PlayniteInFront()) return false;
            var details = FindByName(w, "PART_ElemGameDetails");
            if (details != null && details.IsVisible) return false;
            var hub = FindByName(w, "WelcomeHubOverlay");
            var library = FindByName(w, "SeleneLibrary");
            if (hub != null && hub.IsVisible) { HidePreview(); Ui.Play("OpenPanel.wav", 0.8); SeleneShell.ShowLibrary(); Log("view button -> library"); return true; }
            if (library != null && library.IsVisible) { Ui.Play("OpenPanel.wav", 0.8); SeleneShell.ShowHome(); Log("view button -> home"); return true; }
            return false;
        }

        // Playnite brought itself forward while a game runs (taskbar click, Alt+Tab) and put up its "game is running"
        // screen, which Selene's theme leaves empty: the home on the running game instead, game paused, like Guide
        public static void GameStatusShown()
        {
            if (launching) return;   // the loading screen is up: Playnite's status belongs behind it
            var w = Application.Current.MainWindow;
            if (w == null) return;
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var status = FindByName(w, "PART_ElemGameStatus") as UIElement;
                    if (status == null || !status.IsVisible || launching) return;
                    HidePreview();
                    var hub = FindByName(w, "WelcomeHubOverlay");
                    var proxy = FindByName(w, "SeleneNav_hub") as System.Windows.Controls.Primitives.ButtonBase;
                    if (Features.Value("aftergame") == "where" && launchedFromCollection) { SeleneShell.ShowLibrary(); Log("game status -> Collection (After a game: where you were)"); return; }
                    if ((hub == null || !hub.IsVisible) && proxy != null && proxy.Command != null && proxy.Command.CanExecute(null)) proxy.Command.Execute(null);
                    FocusRowSoon(0);
                    FocusRowSoon(300);
                    if (gameWindow != IntPtr.Zero) PauseRunningGame();
                    Log("game status -> home (Playnite came forward while a game runs)");
                }
                catch (Exception ex) { Log("game status -> home failed: " + ex.Message); }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static void GoHomeFromGuide()
        {
            if (!Features.On("guidefocus")) return;
            try
            {
                // Guide is a controller action: keep the cursor hidden through the window switch
                ignoreMouseUntil = DateTime.Now.AddMilliseconds(1500);
                cursorHidden = false;
                HideCursorForController();
                var w = Application.Current.MainWindow;
                if (w == null) return;
                if (CloseToTray.IsHidden) { CloseToTray.Unhide(); Log("guide -> unhidden from tray"); }
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                DisableWindowTransitions(hwnd);
                if (launching) { Log("guide ignored: game still loading"); return; }  // stay on the loading screen until the game starts
                // Home brought Selene up over the game: the next press goes back, even while Selene is still coming forward
                // (Windows hadn't handed Selene the screen yet, so that press was taken for "go home" again and did nothing)
                if (guideAtHome && gameWindow != IntPtr.Zero && GetForegroundWindow() == gameWindow && !IsIconic(gameWindow)) guideAtHome = false;   // went back some other way
                if (guideAtHome && CanReturnToGame()) { guideAtHome = false; guideSeq++; ReturnToGame(w); return; }
                bool wasBehind = GetForegroundWindow() != hwnd;
                if (!wasBehind && CanReturnToGame()) { ReturnToGame(w); return; }  // like the PS button: toggle back
                if (wasBehind && ForegroundIsAdmin())
                {
                    // Ordinary window switching only. Never cover or freeze an elevated game on the assumption
                    // that focus was acquired: Windows can refuse this request.
                    var elevatedWindow = GetForegroundWindow();
                    ForceForegroundAsync(hwnd, () =>
                    {
                        if (!PlayniteInFront()) { Log("guide: ordinary focus request declined for administrator game; use Alt+Tab"); return; }
                        if (AnyGameRunning()) { gameWindow = elevatedWindow; guideAtHome = true; }
                        var content = w.Content as UIElement;
                        if (content != null) FadeInRoot(content);
                        FocusRowSoon(0);
                        Ui.Notice("Administrator game is still running. Pause it from its own menu.", 6000);
                        Log("guide: ordinary window switch from administrator game; no process control");
                    });
                    return;
                }
                if (wasBehind)
                {
                    guideAtHome = true;
                    var seq = ++guideSeq;
                    guideSwitching = true;
                    // the game's picture over everything at once (or black when it can't be pictured): no desktop, no taskbar
                    var snap = Features.On("guidefade") ? SnapScreen(hwnd) : null;
                    lastGameSnap = snap;
                    if (snap != null) ShowSnapWindow(w, snap);
                    if (snapWindow == null) { snap = null; ShowShield(w); }
                    SetCloak(hwnd, true);  // and Playnite's old picture can never be shown while it comes forward
                    if (snap != null && !AddSnapAdorner(w, snap)) { CloseSnapWindow(); ShowShield(w); }
                    gameWindow = GetForegroundWindow();
                    StartStealGuard(w);
                    var root = w.Content as UIElement;
                    if (root != null) root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));  // no stale frame before the fade-in
                    // a background process may not steal focus: borrow the foreground thread's input queue for the call
                    if (IsIconic(hwnd)) ShowWindow(hwnd, 9);  // SW_RESTORE (back to maximised)
                    uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
                    bool attached = fg != 0 && fg != me && AttachThreadInput(me, fg, true);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    if (attached) AttachThreadInput(me, fg, false);
                    w.Activate();
                    // never left on a black screen (the game's music-less home playing behind black): whatever happened
                    // above, once Playnite is in front it is visible
                    var neverBlack = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2700) };   // (after the reveal wait and its crossfade)
                    neverBlack.Tick += (sb, eb) =>
                    {
                        neverBlack.Stop();
                        if (!PlayniteInFront()) return;
                        bool fixedUp = false;
                        if (shield != null && shield.IsVisible) { HideShield(); fixedUp = true; }
                        if (snapWindow != null) { CloseSnapWindow(); fixedUp = true; }
                        if (snapAdorner != null && snapAdorner.Opacity > 0.99) { RemoveSnapAdorner(); fixedUp = true; }
                        SetCloak(hwnd, false);
                        var r = w.Content as UIElement;
                        if (r != null && r.Opacity < 0.99) { FadeInRoot(r); fixedUp = true; }
                        if (fixedUp) Log("guide: Playnite was still hidden/black after 2.7 s -> shown");
                    };
                    neverBlack.Start();
                    // Windows would first show the picture it kept of Playnite from before the game (a bright flash).
                    // Stay cloaked until Playnite has drawn its black starting frame, then show it and fade in.
                    var cloakHwnd = hwnd;
                    int frames = 0;
                    EventHandler reveal = null;
                    bool revealed = false;
                    Action doReveal = () =>
                    {
                        if (revealed) return;
                        revealed = true;
                        if (seq != guideSeq) { CompositionTarget.Rendering -= reveal; return; }   // already back in the game
                        CompositionTarget.Rendering -= reveal;
                        SetCloak(cloakHwnd, false);
                        HideShield();
                        PlayGuideEnter(w);
                    };
                    // shown once Selene has drawn a few frames AND Windows has really put it in front (a game run as administrator
                    // gives the screen up only when the helper asks): until then the game's picture (or black) stays
                    reveal = (s2, e2) => { if (++frames >= 3 && PlayniteInFront()) doReveal(); };
                    CompositionTarget.Rendering += reveal;
                    var safety = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
                    safety.Tick += (s3, e3) => { safety.Stop(); if (!revealed && seq == guideSeq) Log("guide: Selene not in front after 2 s -> shown anyway"); doReveal(); };  // never stay invisible
                    safety.Start();
                    guideSwitching = false;
                    PauseRunningGame();
                }
                HidePreview();
                if (wasBehind && !leftFromHub)
                {
                    // the game was started from the library / game details: go back there, not home
                    var list = FindByName(w, "PART_ListGameItems") as ListBox;
                    var details = FindByName(w, "PART_ElemGameDetails");
                    if (details != null && details.IsVisible) { }
                    else if (list != null && list.IsVisible && !list.IsKeyboardFocusWithin) FocusLibraryLater(w, false);
                    Log("guide -> back to library/details (was behind=True, now foreground=" + (GetForegroundWindow() == hwnd) + ")");
                    return;
                }
                var hub = FindByName(w, "WelcomeHubOverlay");
                var proxy = FindByName(w, "SeleneNav_hub") as System.Windows.Controls.Primitives.ButtonBase;
                bool opened = false;
                if ((hub == null || !hub.IsVisible) && proxy != null && proxy.Command != null && proxy.Command.CanExecute(null)) { proxy.Command.Execute(null); opened = true; }
                // window activation hands focus back to the last focused bar button: put it on the row's game instead
                // (twice: once after the hub opens, again after WPF's own activation focus restore)
                FocusRowSoon(0);
                FocusRowSoon(300);
                Log("guide -> home (was behind=" + wasBehind + ", now foreground=" + (GetForegroundWindow() == hwnd) + ", hub opened=" + opened + ")");
            }
            catch (Exception ex)
            {
                Log("guide home failed: " + ex.Message);
                try { HideShield(); CloseSnapWindow(); RemoveSnapAdorner(); SetCloak(new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle, false); } catch { }
                var root = Application.Current.MainWindow != null ? Application.Current.MainWindow.Content as UIElement : null;
                if (root != null) FadeInRoot(root);  // never leave Playnite faded out
            }
        }

        public static string FullPath(string path)
        {
            if (string.IsNullOrEmpty(path) || System.IO.Path.IsPathRooted(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
            try { return Api != null ? Api.Database.GetFullFilePath(path) : path; } catch { return path; }
        }

        public static string FindFocusedName()
        {
            var f = Keyboard.FocusedElement as FrameworkElement;
            if (f == null) return null;
            for (DependencyObject d = f; d != null; d = GetParent(d))
            {
                var fe = d as FrameworkElement;
                if (fe != null && (fe.Name == "WelcomeHubOverlay" || fe.Name == "PART_ListGameItems" || fe.Name == "PART_ElemGameDetails" || fe.Name == "AcceuilSettings")) return fe.Name;
            }
            return f.GetType().Name;
        }

        public static string ThemeDir { get { return SelenePaths.ThemeDir; } }

        public static void Log(string s)
        {
            PerformanceRecorder.FromLog(s);
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
        }

        private static DependencyObject GetParent(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
            var fce = d as FrameworkContentElement;
            return fce != null ? fce.Parent : null;
        }

        private static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                var hit = c as T ?? FindChild<T>(c);
                if (hit != null) return hit;
            }
            return null;
        }
    }

    // Two stacked images; a new source is decoded on a worker thread (downscaled to the screen width, frozen) and faded
    // in over the old one. Missing/empty source fades the current image out. No GC.Collect (see ReplaceFadeImages).
    public class SeleneCrossfade : Grid
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register("Source", typeof(object), typeof(SeleneCrossfade),
            new PropertyMetadata(null, (d, e) => ((SeleneCrossfade)d).OnSource(e.NewValue)));
        public object Source { get { return GetValue(SourceProperty); } set { SetValue(SourceProperty, value); } }
        public static readonly DependencyProperty StretchProperty = DependencyProperty.Register("Stretch", typeof(Stretch), typeof(SeleneCrossfade),
            new PropertyMetadata(Stretch.UniformToFill, (d, e) =>
            {
                var image = (SeleneCrossfade)d;
                image.front.Stretch = (Stretch)e.NewValue; image.back.Stretch = (Stretch)e.NewValue;
            }));
        public Stretch Stretch { get { return (Stretch)GetValue(StretchProperty); } set { SetValue(StretchProperty, value); } }

        // WPF itself forces a full garbage collection (MS.Internal.MemoryPressure -> GC.Collect(2)) when big bitmaps
        // are created in a row: four 1080p backgrounds per move did that ~1 s after every second move. So: load only
        // what is visible, wait until the selection rests, and reuse recently decoded pictures.
        public Func<bool> CanLoad;
        // Spotlight's pictures: they stay as they are while Collection shows (Playnite's selected game is Collection's then),
        // so Spotlight still has its own art when you come back: nothing to reload, nothing stale to fade out
        public bool Hub;
        public static bool Disabled;  // test harness switch
        private static readonly Dictionary<string, ImageSource> decoded = new Dictionary<string, ImageSource>();
        private static readonly LinkedList<string> decodedOrder = new LinkedList<string>();
        private const int DecodedMax = 8;
        private System.Windows.Threading.DispatcherTimer settle;
        private object pending;
        private object shown = DependencyProperty.UnsetValue;

        public void Refresh() { Load(Source); }

        private Image front = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
        private Image back = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
        private int request;
        private const double FadeMs = 350;

        public SeleneCrossfade()
        {
            Children.Add(back);
            Children.Add(front);
        }

        private void OnSource(object value)
        {
            pending = value;
            if (settle == null)
            {
                settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                settle.Tick += (s, e) => { settle.Stop(); Load(pending); };
            }
            settle.Stop();
            settle.Start();
        }

        private void Load(object value)
        {
            if (Disabled || (CanLoad != null && !CanLoad())) return;
            if (Hub && SeleneShell.CollectionActive) return;
            if (Equals(value, shown)) return;
            shown = value;
            int id = ++request;
            var ready = value as ImageSource;
            if (ready != null) { Show(ready); return; }
            var path = HomeNav.FullPath(value as string);
            if (string.IsNullOrEmpty(path) || (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !System.IO.File.Exists(path))) { Show(null); return; }
            ImageSource hit;
            if (decoded.TryGetValue(path, out hit)) { decodedOrder.Remove(path); decodedOrder.AddFirst(path); Show(hit); return; }
            int width = (int)Math.Max(1280, SystemParameters.PrimaryScreenWidth);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bi.UriSource = new Uri(path);
                    bi.DecodePixelWidth = width;
                    bi.EndInit();
                    bi.Freeze();
                    return (ImageSource)bi;
                }
                catch { return null; }
            }).ContinueWith(task =>
            {
                if (task.Result != null && !decoded.ContainsKey(path))
                {
                    decoded[path] = task.Result;
                    decodedOrder.AddFirst(path);
                    while (decodedOrder.Count > DecodedMax) { decoded.Remove(decodedOrder.Last.Value); decodedOrder.RemoveLast(); }
                }
                if (id == request) Show(task.Result);  // a newer selection wins
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void Show(ImageSource img)
        {
            if (Hub) Contrast.Art(this, img);   // the home's shades as deep as this picture needs
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            if (img == null)
            {
                // nothing to show (a card is selected): both layers go, the one still fading out included
                front.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeMs)) { EasingFunction = ease });
                back.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeMs)) { EasingFunction = ease });
                return;
            }
            // the old picture goes behind, the new one fades in on top; the old one is released after the fade
            var old = front;
            front = back; back = old;
            Children.Remove(front); Children.Add(front);
            front.BeginAnimation(OpacityProperty, null);
            front.Opacity = 0;
            front.Source = img;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs)) { EasingFunction = ease };
            var leaving = back;
            fadeIn.Completed += (s, e) => { if (leaving != front) { leaving.BeginAnimation(OpacityProperty, null); leaving.Opacity = 0; leaving.Source = null; } };
            front.BeginAnimation(OpacityProperty, fadeIn);
        }
    }
}
