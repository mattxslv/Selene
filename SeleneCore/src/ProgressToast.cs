// Playnite's progress screen (add-ons downloading logos or trailers after a library update, a library update itself...)
// as a notification instead of a full screen: a glass card at the top right, under the clock, with the task's words.
// Playnite opens that screen as a modal window and waits for it to close before carrying on, so it isn't held open: it is
// moved off screen (invisible, the main window handed its input back) and the card is a window of Selene's own that mirrors
// it. The card slides in from beyond the right edge with a sound, stays at least a few seconds so it can be read (the bar
// runs to the end when the task is done), and slides back out. Tasks that come one after another share the card.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    public static class ProgressToast
    {
        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hWnd, bool enable);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        // a note's or task's card: a compact strip as a console's (a 48 px icon, two lines), as wide as its words between these;
        // the session card alone is a panel
        private const double CardWidth = 520, CardMinWidth = 240, CardHeight = 84, Edge = 28, ShadowRoom = 24, Drift = 26;
        private const int MinShownMs = 3000, AfterDoneMs = 1100, InMs = 260, OutMs = 220;
        private static Window hiddenProgress;   // Playnite's own window, kept out of sight
        private static DateTime busyUntil = DateTime.MinValue;

        // a progress task is being shown as a card (or just was): focus moving around then is Playnite's own business
        public static bool Busy { get { return source != null || hiddenProgress != null || DateTime.Now < busyUntil || (layer != null && layer.Visibility == Visibility.Visible); } }

        // the card (one at a time)
        // the card's layer inside Playnite's own window (a window of its own flashed a black box as it appeared, and overlapping
        // the full-screen window made Windows present Playnite's frames the slow way: everything stuttered while it showed)
        private static Grid layer;
        private static Border icon;
        private static FrameworkElement downloadGlyph;   // a task's icon (centred by its outline)
        private static int downloadGlyphCode;
        private static ContentControl titleLine;
        private static StackPanel text;
        private const double SessionWidth = 640, SessionHeight = 128;   // the session card: the one panel-sized card
        private static DockPanel row;
        private static Brush glass, sessionGlass;

        // a progress card's shape (wide, one row: icon tile and words) or the session card's (Legacy's: words left, logo right)
        private static void Shape(bool session)
        {
            card.BeginAnimation(FrameworkElement.WidthProperty, null);   // (a width still gliding to the last words)
            card.Width = session ? SessionWidth : CardWidth;   // (a note's or task's width follows its words: Size())
            card.MaxWidth = double.PositiveInfinity;
            card.Height = session ? SessionHeight : CardHeight;
            card.Padding = session ? new Thickness(0) : new Thickness(16, 0, 16, 0);
            icon.Margin = new Thickness(0, 0, 14, 0);   // (a picture-only card before took these away)
            icon.Width = 48;
            icon.Padding = new Thickness(0);
            text.Visibility = Visibility.Visible;
            card.CornerRadius = new CornerRadius(session ? 14 : 12);
            card.Background = session ? sessionGlass : glass;
            if (!session && !ReferenceEquals(card.Child, row)) card.Child = row;
            layer.Width = Math.Max(CardWidth, SessionWidth) + ShadowRoom + Edge;
            text.HorizontalAlignment = HorizontalAlignment.Left;
            titleLine.HorizontalAlignment = HorizontalAlignment.Left;
            words.HorizontalAlignment = HorizontalAlignment.Left;
            words.TextAlignment = TextAlignment.Left;
            words.MaxWidth = CardWidth - Around;
            words.Visibility = session ? Visibility.Collapsed : Visibility.Visible;
            titleLine.Margin = session ? new Thickness(0) : new Thickness(0, 0, 0, 3);
        }
        private static Border card, track, fill;
        private static TextBlock words;
        private static TranslateTransform slideIn, sweep;
        private static DispatcherTimer poll, leaveTimer;
        private static DateTime shownAt;
        private static object source;      // the progress window's view model being mirrored (null: its task is done)
        private static bool sweeping, leaving;
        private static double barShown = -1;

        // Playnite's progress window draws its first frame (an opaque dark screen) before it is Loaded: it is made invisible as
        // it activates, which comes first (the black flash before the card)
        // Playnite closing: its progress window stays invisible and off the screen
        // Playnite's "Closing Playnite" screen is never shown: quitting looks instant, Playnite closes out of sight
        public static void HideForQuit(Window w)
        {
            try
            {
                if (w.Resources.Contains("SeleneQuitHidden")) return;   // (Activated and Loaded both bring it here: once per window)
                w.Resources["SeleneQuitHidden"] = true;
                w.BeginAnimation(UIElement.OpacityProperty, null);
                w.Opacity = 0;
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 1, 1, 0x0010 | 0x0004 | 0x0200);
                HomeNav.HideForQuit(w);   // cloaked, and the focus stays with what had the screen
            }
            catch (Exception ex) { HomeNav.Log("quit: hiding the closing screen failed: " + ex.Message); }
        }

        public static void Prehide(Window w)
        {
            if (HomeNav.Quitting) { HideForQuit(w); return; }
            if (!Features.On("dialogs") || !Features.On("progresstoast")) return;
            var main = Application.Current.MainWindow;
            if (main == null || ReferenceEquals(main, w) || !main.IsVisible) return;
            w.BeginAnimation(UIElement.OpacityProperty, null);
            w.Opacity = 0;
            w.Background = Brushes.Transparent;
            HoldDim(main, true);
            // shown as a card (the same test as Make): emptied, tiny and off the screen already now. It's a see-through window
            // the size of the screen, which Windows redraws the slow way on every frame until it's made small (it froze
            // everything moving for a few hundred ms before each card)
            if (main.WindowState == WindowState.Minimized || main.ActualWidth < 400) return;
            w.Content = null;
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 1, 1, 0x0010 /* NOACTIVATE */ | 0x0004 /* NOZORDER */ | 0x0200 /* NOOWNERZORDER */);
        }

        // Playnite's own dim layer (BorderDim in its main window) held hidden while a card shows: switching its flag off was not
        // enough, Playnite sends the "off" about 10 ms late from another thread and the dark layer flashed (the flicker)
        private static UIElement dimLayer;
        private static void HoldDim(Window main, bool hold)
        {
            try
            {
                if (dimLayer == null && main.Template != null) dimLayer = main.Template.FindName("BorderDim", main) as UIElement;
                if (dimLayer == null) return;
                if (hold) dimLayer.Visibility = Visibility.Collapsed;   // a local value outranks Playnite's trigger
                else dimLayer.ClearValue(UIElement.VisibilityProperty);
            }
            catch { }
        }

        // Playnite dims its whole main window while any child window is open (its ChildOpened flag shows an 80% dark layer, and
        // its sounds go quiet). The hidden progress window counts as one: while the card shows, the flag is put back at once,
        // unless a real dialog (a menu, a question) is open too.
        private static bool vmHooked;
        public static void WatchDimming(Window main)
        {
            if (vmHooked || main == null) return;
            var vm = main.DataContext as System.ComponentModel.INotifyPropertyChanged;
            var prop = vm != null ? vm.GetType().GetProperty("ChildOpened") : null;
            if (prop == null || !prop.CanWrite) return;
            vmHooked = true;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != "ChildOpened" || !Equals(prop.GetValue(vm, null), true)) return;
                if (!Features.On("progresstoast") || !OnlyProgressOpen(main)) return;
                prop.SetValue(vm, false, null);
            };
        }

        // the only child windows up are Playnite's progress windows (shown as the card)
        private static bool OnlyProgressOpen(Window main)
        {
            bool any = false;
            foreach (Window w in Application.Current.Windows)
            {
                if (ReferenceEquals(w, main) || !w.IsVisible && w.GetType().Name != "ProgressWindow") continue;
                if (!(w.GetType().Namespace ?? "").StartsWith("Playnite")) continue;   // Selene's own windows don't count
                if (w.GetType().Name != "ProgressWindow") return false;
                any = true;
            }
            return any || Busy;
        }

        // false: leave it as Playnite's full screen (no main window to sit on yet, at start-up)
        private static bool devStackOnce = true;   // the first of Playnite's own progress windows says who asked for it (log)
        public static bool Make(Window w)
        {
            var main = Application.Current.MainWindow;
            if (main == null || ReferenceEquals(main, w) || !main.IsVisible || main.WindowState == WindowState.Minimized || main.ActualWidth < 400) return false;
            var timing = System.Diagnostics.Stopwatch.StartNew();
            var steps = new System.Text.StringBuilder();

            // Playnite's own window: out of sight and out of the way (it closes by itself when the task ends). Emptied and hidden the
            // Windows way too, and kept so (Playnite fades / places it after this: a dark veil over the screen stayed otherwise)
            w.BeginAnimation(UIElement.OpacityProperty, null);
            w.Content = null;
            steps.Append(" a" + timing.ElapsedMilliseconds);
            w.Background = Brushes.Transparent;
            hiddenProgress = w;
            w.Opacity = 0;
            steps.Append(" b" + timing.ElapsedMilliseconds);
            var hwnd = new WindowInteropHelper(w).Handle;
            // off the screen, tiny, off the taskbar: one call to Windows (set one WPF property at a time - size, place, state,
            // taskbar - it took ~130 ms, a hitch in everything moving each time a notification came up)
            if (hwnd != IntPtr.Zero)
            {
                SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */);
                SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 1, 1, 0x0010 /* NOACTIVATE */ | 0x0004 /* NOZORDER */ | 0x0200 /* NOOWNERZORDER */);
            }
            var mainHwnd = new WindowInteropHelper(main).Handle;
            // the main window first gets its input back and the focus, THEN Playnite's window goes: hidden while the main one
            // was still locked by it, Windows handed the focus to another program, and Selene's "a game covers Playnite" guard
            // blanked the home (the black screen, flickering as tasks came and went)
            busyUntil = DateTime.Now.AddSeconds(6);
            EnableWindow(mainHwnd, true);
            main.Activate();
            steps.Append(" c" + timing.ElapsedMilliseconds);
            // (not hidden the Windows way too: it's tiny, see-through and off the screen already, and hiding it cost ~40 ms)
            steps.Append(" d" + timing.ElapsedMilliseconds);
            // the modal window disabled the main one: it gets its input back, so browsing goes on while the task runs
            w.Dispatcher.BeginInvoke(new Action(() => { var t0 = System.Diagnostics.Stopwatch.StartNew(); HideProgress(); EnableWindow(mainHwnd, true); if (!main.IsActive) main.Activate(); if (t0.ElapsedMilliseconds > 8) HomeNav.Log("notification: second pass " + t0.ElapsedMilliseconds + " ms"); }), DispatcherPriority.Background);
            w.Closed += (s, e) => { if (ReferenceEquals(hiddenProgress, w)) hiddenProgress = null; TaskDone(w.DataContext); };
            WatchDimming(main);
            UndimNow(main);

            steps.Append(" e" + timing.ElapsedMilliseconds);
            long before = timing.ElapsedMilliseconds;
            Show(main, w.DataContext);
            if (timing.ElapsedMilliseconds > 60) HomeNav.Log("notification: made in " + before + " + " + (timing.ElapsedMilliseconds - before) + " ms (" + steps + ")");
            HomeNav.Log("progress look: notification card");
            if (devStackOnce)
            {
                devStackOnce = false;
                var frames = new System.Diagnostics.StackTrace().GetFrames() ?? new System.Diagnostics.StackFrame[0];
                HomeNav.Log("progress window asked for by: " + string.Join(" < ", frames.Select(f => f.GetMethod()).Where(m => m != null && m.DeclaringType != null && !(m.DeclaringType.Namespace ?? "").StartsWith("System") && !(m.DeclaringType.Namespace ?? "").StartsWith("MS.")).Skip(12).Take(20).Select(m => m.DeclaringType.FullName + "." + m.Name)));
            }
            return true;
        }

        // a task's card. Like a console's: it waits for the home to have come up (no card over the start-up and the intro), it
        // keeps one of three widths for as long as it's up (its words change in place), and a different task doesn't reshape
        // the card on screen: that one slides away first and the new one slides in.
        private static object displayed;   // the task (or note) the card on screen belongs to
        // a note (a new version, achievements...) stays this long before a task's card or another note takes its place (the
        // library update's card replaced the new-version note within a second)
        private const int NoteHoldMs = 6000 + 350;   // (6 s once it has slid in)
        private static DateTime noteHeldUntil = DateTime.MinValue;
        private static int NoteHoldLeft { get { return displayed is string && layer != null && layer.Visibility == Visibility.Visible && !leaving ? (int)(noteHeldUntil - DateTime.Now).TotalMilliseconds : 0; } }

        // a task's card stays long enough to be read before the next one takes its place (quick steps at start-up: "Library
        // update - Finished", then "Downloading logos" for half a second)
        private static int TaskHoldLeft { get { return !(displayed is string) && layer != null && layer.Visibility == Visibility.Visible && !leaving ? MinShownMs - (int)(DateTime.Now - shownAt).TotalMilliseconds : 0; } }

        private static void Show(Window main, object vm)
        {
            bool swap = layer != null && layer.Visibility == Visibility.Visible && !leaving && displayed != null && !ReferenceEquals(displayed, vm);
            source = vm;
            shownText = null;
            if (layer == null) Build();
            if (!Ready) { HomeNav.Log("card: held for the start-up (" + TaskText(vm) + ")"); WhenReady(() => { if (ReferenceEquals(source, vm)) Show(main, vm); else HomeNav.Log("card: done before the start-up was over"); }); return; }
            if (swap && NoteHoldLeft > 0) { int wait = NoteHoldLeft; HomeNav.Log("card: " + TaskText(vm) + " waits for the note (" + wait + " ms)"); Later(wait + 50, () => { if (ReferenceEquals(source, vm)) Show(main, vm); }); return; }
            if (swap && TaskHoldLeft > 0)
            {
                int wait = TaskHoldLeft;
                HomeNav.Log("card: " + TaskText(vm) + " waits for the card up (" + wait + " ms)");
                Later(wait + 50, () =>
                {
                    if (ReferenceEquals(source, vm)) Show(main, vm);
                    else if (source == null && layer.Visibility == Visibility.Visible && !leaving && !(displayed is string) && (leaveTimer == null || !leaveTimer.IsEnabled)) Leave();   // (done while it waited: the card up goes)
                });
                return;
            }
            if (swap) { HomeNav.Log("card: swapping for " + TaskText(vm)); SwapOut(() => { if (ReferenceEquals(source, vm)) Present(main, vm, true); }); return; }
            Present(main, vm, layer.Visibility != Visibility.Visible || leaving);
        }

        private static void Present(Window main, object vm, bool enter, bool sound = true)
        {
            HomeNav.Log("card: showing \"" + TaskText(vm) + "\"" + (enter ? " (in)" : " (in place)"));
            displayed = vm;
            icon.Background = Ink.Brush("GlyphColor", 0.18);
            icon.Child = downloadGlyph;
            icon.Visibility = Visibility.Visible;
            titleLine.Visibility = Visibility.Collapsed;
            words.FontSize = 15;
            words.Foreground = Ink.Brush("SeleneInkColor", 0.95);
            words.TextWrapping = TextWrapping.Wrap;
            Shape(false);
            var text = TaskText(vm);
            if (text.Length > 0) { shownText = text; ShowWords(text); IconFor(text); }
            Size(Need(titleLine.Visibility == Visibility.Visible ? ((TextBlock)titleLine.Content).Text : "", words.Text, 18, 15));
            if (leaveTimer != null) leaveTimer.Stop();
            barShown = -1;
            Place(main);
            if (enter)
            {
                leaving = false;
                shownAt = DateTime.Now;
                layer.Visibility = Visibility.Visible;
                SlideIn();   // solid all the way: in from beyond the right edge (a fade showed the art through it)
                if (sound) Ui.NotifySound();
            }
            Update();
            poll.Start();
        }

        // a message without its numbers (a count or a percentage changing is the same message)
        private static string Kind(string text) { return System.Text.RegularExpressions.Regex.Replace(text ?? "", @"[\d/%.,()]+", "").Trim(); }

        private static string TaskText(object vm)
        {
            var t = Prop(Prop(vm, "ProgressArgs"), "Text") as string;
            return t != null ? t.Trim() : "";
        }

        // ---- a card as wide as its words, between the least and the most (a console's strip grows with the text) ----
        private const double Around = 98;   // the icon tile, its gap and the card's padding

        private static double Need(string head, string line, double headSize, double lineSize)
        {
            return Math.Max(Measure(head, Ui.Display, headSize, FontWeights.SemiBold), Measure(line, Ui.Body, lineSize, FontWeights.Normal)) + Around;
        }

        private static double Measure(string text, FontFamily font, double size, FontWeight weight)
        {
            if (string.IsNullOrEmpty(text)) return 0;
#pragma warning disable 618
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White);
#pragma warning restore 618
            return Math.Ceiling(ft.WidthIncludingTrailingWhitespace);
        }

        private static void Size(double need) { Size(need, false); }

        // tiny: no least width (a picture-only card is just its tile)
        private static void Size(double need, bool tiny)
        {
            double w = Math.Min(CardWidth, Math.Max(tiny ? 0 : CardMinWidth, Math.Ceiling(need / 2) * 2));
            // a card already up grows or shrinks to its new words (200 ms) instead of jumping
            bool live = layer.Visibility == Visibility.Visible && card.ActualWidth > 1 && Math.Abs(card.ActualWidth - w) > 1;
            double from = card.ActualWidth;
            card.BeginAnimation(FrameworkElement.WidthProperty, null);
            if (live)
            {
                card.Width = from;
                var grow = Ui.Fast(new DoubleAnimation(w, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                grow.Completed += (s, e) => { card.BeginAnimation(FrameworkElement.WidthProperty, null); card.Width = w; };
                card.BeginAnimation(FrameworkElement.WidthProperty, grow);
            }
            else card.Width = w;
            card.MaxWidth = double.PositiveInfinity;
            words.MaxWidth = Math.Max(0, w - Around);
        }

        // the card on screen out past the right edge, then the next one
        private static bool swapping;
        private static void SwapOut(Action then)
        {
            if (swapping) return;
            swapping = true;
            if (leaveTimer != null) leaveTimer.Stop();
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var slide = Ui.Fast(new DoubleAnimation(PeekIn, TimeSpan.FromMilliseconds(180)) { EasingFunction = easeIn });
            slide.Completed += (s, e) => { swapping = false; layer.Visibility = Visibility.Collapsed; then(); };
            slideIn.BeginAnimation(TranslateTransform.XProperty, slide);
            card.BeginAnimation(UIElement.OpacityProperty, Ui.Fast(new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = easeIn }));
        }

        // ---- not over the start-up: the home up and its intro done ----
        public static bool Ready { get { return !LoginScene.StartingUp && !HomeNav.IntroRunning && (DateTime.Now - LoginScene.ClosedAt).TotalMilliseconds > 400; } }

        private static DispatcherTimer readyWatch;
        private static Action whenReady;
        private static void WhenReady(Action then)
        {
            whenReady = then;
            if (readyWatch == null)
            {
                readyWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                readyWatch.Tick += (s, e) =>
                {
                    if (!Ready) return;
                    readyWatch.Stop();
                    var go = whenReady;
                    whenReady = null;
                    if (go != null) go();
                };
            }
            readyWatch.Start();
        }

        // the top layer of Playnite's own window, at the top right under the bar (the clock and the profile), not over it
        private static void Place(Window main)
        {
            var host = HomeNav.FindByName(main, "PART_MainHost") as Grid;
            if (host == null || ReferenceEquals(layer.Parent, host)) return;
            var old = layer.Parent as Panel;
            if (old != null) old.Children.Remove(layer);
            Grid.SetRowSpan(layer, 99);
            Grid.SetColumnSpan(layer, 99);
            Panel.SetZIndex(layer, 9998);
            host.Children.Add(layer);
        }

        private static void Build()
        {
            words = new TextBlock
            {
                FontFamily = Ui.Body, FontSize = 15, Foreground = Ink.Brush("SeleneInkColor", 0.95), TextWrapping = TextWrapping.Wrap, LineHeight = 20,
                MaxWidth = CardWidth - Around, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 42
            };
            var title = new TextBlock { Text = "Selene", FontFamily = Ui.Display, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor", 0.55), Margin = new Thickness(0, 0, 0, 3) };
            track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ink.Brush("SeleneInkColor", 0.14), Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
            fill = new Border
            {
                CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = Ink.Brush("GlyphColor"),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Opacity = 0.7, Color = Ink.Of("GlyphColor") }
            };
            sweep = new TranslateTransform();
            fill.RenderTransform = sweep;
            track.Child = fill;
            // like a console's notification: an icon tile on the left, the task's words beside it (the bar is kept, unshown, so
            // the task's progress code has somewhere to go)
            downloadGlyphCode = 0xE2C4;   /* file_download */
            downloadGlyph = Ui.Glyph(Ui.Icon(downloadGlyphCode), Ui.Icons, 23, Ink.Brush("GlyphColor"));
            icon = new Border
            {
                Width = 48, Height = 48, CornerRadius = new CornerRadius(11), Background = Ink.Brush("GlyphColor", 0.18), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0), Child = downloadGlyph, ClipToBounds = true
            };
            titleLine = new ContentControl { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
            text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(titleLine);
            text.Children.Add(words);
            row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);
            slideIn = new TranslateTransform();
            glass = Ink.Brush("SeleneGlassColor", 1);   // solid: see-through, busy art behind showed through the words
            sessionGlass = Ink.Brush("SeleneGlassColor", 0.92);   // the session card: darker, the game's art shows faintly in it
            card = new Border
            {
                Width = CardWidth, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 0, 16, 0), Height = CardHeight,
                Background = glass, BorderBrush = Ink.Brush("SeleneInkColor", 0.08), BorderThickness = new Thickness(1),   // see-through
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 30, Opacity = 0.45, Color = Colors.Black },
                Child = row, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,   // (right: a card narrower than the layer keeps to the screen's edge)
                Margin = new Thickness(ShadowRoom, ShadowRoom, Edge, ShadowRoom), RenderTransform = slideIn,
                CacheMode = new BitmapCache { SnapsToDevicePixels = true }   // drawn once: the shadow's blur wasn't redrawn every frame of a slide
            };
            layer = new Grid
            {
                IsHitTestVisible = false, ClipToBounds = true, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Width = CardWidth + ShadowRoom + Edge, Margin = new Thickness(0, 112 - ShadowRoom, 0, 0)
            };
            // on whole pixels (as wide as its words, the card's edges fell between pixels and its text went soft); the text keeps
            // WPF's smooth shapes (drawn "for the screen" it went jaggy)
            layer.UseLayoutRounding = true;
            layer.SnapsToDevicePixels = true;
            layer.Children.Add(card);
            poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            poll.Tick += (s, e) =>
            {
                HideProgress(); Update();
                var mainNow = Application.Current.MainWindow;
                if (mainNow != null) { UndimNow(mainNow); HoldDim(mainNow, OnlyProgressOpen(mainNow)); }   // a real dialog: Playnite's dim back
                // never a blank home behind the card: if it was blanked anyway, it comes back
                var m = Application.Current.MainWindow;
                var r = m != null ? m.Content as UIElement : null;
                if (m != null && m.IsActive && r != null && r.Opacity < 0.01) { HomeNav.FadeInRoot(r); HomeNav.Log("progress look: home was blank, shown again"); }
            };
        }

        // the icon tile by what the task says: a download arrow for fetching, a sync sign for updates and imports, a gear otherwise
        private static void IconFor(string text)
        {
            if (downloadGlyph == null || !ReferenceEquals(icon.Child, downloadGlyph)) return;
            var t = text.ToLowerInvariant();
            int was = downloadGlyphCode;
            int glyph = t.Contains("download") || t.Contains("fetch") || t.Contains("logo") || t.Contains("trailer") || t.Contains("video") ? 0xE2C4 /* file_download */
                      : t.Contains("updat") || t.Contains("import") || t.Contains("library") || t.Contains("sync") || t.Contains("scan") ? 0xE627 /* sync */
                      : 0xE8B8 /* settings */;
            if (glyph == was) return;
            downloadGlyphCode = glyph;
            downloadGlyph = Ui.Glyph(Ui.Icon(glyph), Ui.Icons, 23, Ink.Brush("GlyphColor"));
            icon.Child = downloadGlyph;
        }

        // words and bar from the task (polled: Playnite changes them on its worker thread)
        private static void Update()
        {
            try
            {
                if (source == null || !ReferenceEquals(displayed, source)) return;   // (a task waiting for a note: not over its words)
                var args = Prop(source, "ProgressArgs");
                var text = Prop(args, "Text") as string;
                if (text != null) text = text.Trim();   // Playnite's texts can start with spaces (they sat indented)
                if (!string.IsNullOrEmpty(text) && shownText != text)
                {
                    // a new message ("Importing Xbox games" after "Importing Steam games") is a new card, as a console's; a count
                    // going up (22/40, 23/40) changes in place
                    if (shownText != null && Kind(text) != Kind(shownText) && !swapping && !leaving && !(source is Status))   // (the library update: one card for all its steps)
                    {
                        if (TaskHoldLeft > 0) return;   // (the message up stays readable; looked at again on the next poll)
                        var vm = source;
                        shownText = text;
                        SwapOut(() => { if (ReferenceEquals(source, vm)) Present(Application.Current.MainWindow, vm, true, false); });   // (the same task: no new sound)
                        return;
                    }
                    shownText = text; ShowWords(text); IconFor(text);
                }
                bool indeterminate = Equals(Prop(args, "IsIndeterminate"), true);
                double max = ToDouble(Prop(args, "ProgressMaxValue")), value = ToDouble(Prop(args, "CurrentProgressValue"));
                double width = track.ActualWidth;
                if (width < 1) return;
                if (indeterminate || max <= 0) { Sweep(width); return; }
                Bar(width * Math.Max(0, Math.Min(1, value / max)));
            }
            catch { }
        }

        // "HowLongToBeat - Downloading data from new games...": the add-on's name as a small heading, its message one line under
        // it (as one wrapped line they ran together)
        private static string shownText;

        private static void ShowWords(string text)
        {
            string head = null, line = text;
            int dash = text.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash <= 32) { head = text.Substring(0, dash).Trim(); line = text.Substring(dash + 3).Trim(); }
            if (line.Length > 0) line = char.ToUpper(line[0]) + line.Substring(1);
            if (head != null)
            {
                titleLine.Content = new TextBlock { Text = head, FontFamily = Ui.Display, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = CardWidth - Around };
                titleLine.Margin = new Thickness(0, 0, 0, 2);
                titleLine.Visibility = Visibility.Visible;
                words.Foreground = Ink.Brush("SeleneInkColor", 0.72);
            }
            else { titleLine.Visibility = Visibility.Collapsed; words.Foreground = Ink.Brush("SeleneInkColor", 0.95); }
            words.TextWrapping = head != null ? TextWrapping.NoWrap : TextWrapping.Wrap;
            line = line.TrimEnd('.', '\u2026', ' ');   // ("Importing Xbox games...": the dots say nothing a card doesn't)
            words.Text = line;
            Size(Need(head ?? "", line, 18, 15));   // (the words changed while the card is up: it fits them again)
        }

        private static void Sweep(double width)
        {
            if (sweeping) return;
            sweeping = true;
            fill.BeginAnimation(FrameworkElement.WidthProperty, null);
            fill.Width = width * 0.32;
            sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-width * 0.32, width, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }

        private static void Bar(double target)
        {
            if (sweeping) { sweeping = false; sweep.BeginAnimation(TranslateTransform.XProperty, null); sweep.X = 0; fill.Width = 0; barShown = -1; }
            if (Math.Abs(target - barShown) < 0.5) return;
            barShown = target;
            fill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // the task ended: the bar runs to the end, the card stays to be read, then slides out (unless another task takes it)
        // a task Selene runs itself (SeleneDialogs): the card without any Playnite window behind it
        // ---- Playnite's own library-update status ("Importing Steam games...", "Library update finished"): the same card, ----
        // instead of a line of words in Collection's bar. Its words and visibility are followed; the line itself stays hidden.
        public class StatusArgs { public string Text { get; set; } public bool IsIndeterminate { get { return true; } } }
        public class Status { private readonly StatusArgs args = new StatusArgs(); public StatusArgs ProgressArgs { get { return args; } } }
        private static Status libraryStatus;
        private static FrameworkElement statusLine;
        private static TextBlock statusWords;
        private static DispatcherTimer statusRetry;

        public static void WatchLibraryStatus(Window main)
        {
            statusLine = HomeNav.FindByName(main, "PART_ElemProgressIndicator") as FrameworkElement;
            statusWords = HomeNav.FindByName(main, "PART_TextProgressTooltip") as TextBlock;
            if (statusLine == null || statusWords == null) { HomeNav.Log("library status: parts not found"); return; }
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(FrameworkElement)).AddValueChanged(statusLine, (s, e) => SyncStatus());
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(statusWords, (s, e) => SyncStatus());
            statusRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            statusRetry.Tick += (s, e) => { statusRetry.Stop(); SyncStatus(); };
            SyncStatus();
        }

        private static void SyncStatus()
        {
            try
            {
                bool on = Features.On("dialogs") && Features.On("progresstoast");
                statusLine.Opacity = on ? 0 : 1;
                if (!on) return;
                var raw = (statusWords.Text ?? "").Trim();
                // one card for the whole update: "Library update" as its heading, the step it's on under it
                var text = raw.Length == 0 ? "" : "Library update - " + (raw.IndexOf("finished", StringComparison.OrdinalIgnoreCase) >= 0 ? "Finished" : raw);
                bool active = statusLine.Visibility == Visibility.Visible && text.Length > 0;
                if (active)
                {
                    if (libraryStatus == null)
                    {
                        // another task's card is up (an add-on downloading): the status waits for it
                        if (source != null || !Usable()) { statusRetry.Stop(); statusRetry.Start(); return; }
                        libraryStatus = new Status();
                        libraryStatus.ProgressArgs.Text = text;
                        ShowTask(libraryStatus);
                    }
                    libraryStatus.ProgressArgs.Text = text;
                }
                else if (libraryStatus != null)
                {
                    var done = libraryStatus;
                    libraryStatus = null;
                    EndTask(done);
                }
            }
            catch (Exception ex) { HomeNav.Log("library status: " + ex.Message); }
        }

        public static bool Usable()
        {
            var main = Application.Current != null ? Application.Current.MainWindow : null;
            return main != null && main.IsVisible && main.WindowState != WindowState.Minimized && main.ActualWidth >= 400;
        }
        public static void ShowTask(object vm)
        {
            var main = Application.Current.MainWindow;
            if (main == null) return;
            Show(main, vm);
            HomeNav.Log("progress look: notification card (Selene's own task)");
        }
        public static void EndTask(object vm)
        {
            // (Playnite closing while the task ran: the app is going away, nothing to tidy up)
            if (Application.Current == null || Application.Current.Dispatcher.HasShutdownStarted) return;
            try { TaskDone(vm); } catch (Exception ex) { HomeNav.Log("notification end: " + ex.Message); }
        }

        private static void TaskDone(object vm)
        {
            var m0 = Application.Current.MainWindow;
            if (m0 != null) m0.Dispatcher.BeginInvoke(new Action(() => { if (!Busy || source == null) HoldDim(m0, false); }), DispatcherPriority.Background);
            if (!ReferenceEquals(vm, source) || layer == null) return;
            source = null;
            poll.Stop();
            if (!ReferenceEquals(displayed, vm)) return;   // (done while it waited for a note: the note keeps its card and time)
            if (track.ActualWidth > 1) Bar(track.ActualWidth);
            int wait = Math.Max(AfterDoneMs, MinShownMs - (int)(DateTime.Now - shownAt).TotalMilliseconds);
            leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
            leaveTimer.Tick += (s, e) => { leaveTimer.Stop(); Leave(); };
            leaveTimer.Start();
        }

        // task and note cards: solid, sliding the whole way in and out past the right edge (the layer clips there)

        private static void SlideIn()
        {
            Sharpen();
            // as a console's: a short quick slide from the right with a fade, the fade over in the first moments (so it's only
            // see-through for a blink), at the screen's own rate
            slideIn.BeginAnimation(TranslateTransform.XProperty, Ui.Fast(new DoubleAnimation(PeekIn, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }));
            card.BeginAnimation(UIElement.OpacityProperty, Ui.Fast(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } }));
        }

        private const double PeekIn = 40;   // how far a card slides in and out

        // the card's texture at the size it really has on the screen (Playnite scales its whole window to the display, and
        // the display may scale too): drawn at 1:1 its words went soft
        private static void Sharpen()
        {
            try
            {
                var cache = card.CacheMode as BitmapCache;
                var root = Application.Current.MainWindow;
                var src = root != null ? PresentationSource.FromVisual(root) : null;
                if (cache == null || src == null || !card.IsDescendantOf(root)) return;
                var t = card.TransformToAncestor(root);
                double scale = Math.Abs(t.Transform(new Point(100, 0)).X - t.Transform(new Point(0, 0)).X) / 100 * src.CompositionTarget.TransformToDevice.M11;
                cache.RenderAtScale = Math.Max(1, Math.Min(4, scale));
            }
            catch { }
        }

        private static void Leave()
        {
            if (source != null || layer == null) return;
            HomeNav.Log("card: leaving");
            leaving = true;
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var slide = Ui.Fast(new DoubleAnimation(PeekIn, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeIn });
            slide.Completed += (s, e) =>
            {
                if (!leaving) return;   // a new task brought it back meanwhile
                leaving = false;
                layer.Visibility = Visibility.Collapsed;
                displayed = null;
                sweeping = false;
                sweep.BeginAnimation(TranslateTransform.XProperty, null); sweep.X = 0;
                fill.BeginAnimation(FrameworkElement.WidthProperty, null); fill.Width = 0;
            };
            slideIn.BeginAnimation(TranslateTransform.XProperty, slide);
            card.BeginAnimation(UIElement.OpacityProperty, Ui.Fast(new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeIn }));
        }

        // Playnite's dim layer off now (it was switched on as the progress window loaded, before Selene saw it)
        private static void UndimNow(Window main)
        {
            try
            {
                var vm = main.DataContext;
                var prop = vm != null ? vm.GetType().GetProperty("ChildOpened") : null;
                if (prop != null && prop.CanWrite && Equals(prop.GetValue(vm, null), true) && OnlyProgressOpen(main)) prop.SetValue(vm, false, null);
            }
            catch { }
        }

        // Playnite's progress window back out of sight if it came back (moved over the screen, faded in), the main window usable
        private static void HideProgress()
        {
            var w = hiddenProgress;
            if (w == null) return;
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                // Playnite placed it back over the screen: off it again (one cheap call, only when it moved)
                RECT r;
                if (h != IntPtr.Zero && GetWindowRect(h, out r) && (r.Left > -30000 || r.Right - r.Left > 2))
                    SetWindowPos(h, IntPtr.Zero, -32000, -32000, 1, 1, 0x0010 /* NOACTIVATE */ | 0x0004 /* NOZORDER */ | 0x0200 /* NOOWNERZORDER */);
                if (w.Opacity > 0) { w.BeginAnimation(UIElement.OpacityProperty, null); w.Opacity = 0; }
                var main = Application.Current.MainWindow;
                if (main != null) EnableWindow(new WindowInteropHelper(main).Handle, true);
            }
            catch { }
        }

        // ---- a game just closed: its cover, its logo (or name) and how long it was played, like a console's "you played" card ----
        public static void ShowSession(Playnite.SDK.Models.Game game, ulong seconds) { ShowSession(game, seconds, false); }

        private static void ShowSession(Playnite.SDK.Models.Game game, ulong seconds, bool fetched)
        {
            var main = Application.Current.MainWindow;
            if (main == null || game == null) return;
            // a Steam game without its logo or cover here: Steam's are fetched first (kept for next time), then the card shows
            var appId = SteamAppId(game);
            if (!fetched && appId != null && DownloadConsent.Allowed("artdownload") && (!System.IO.File.Exists(SteamFile(appId, "logo.png")) || !System.IO.File.Exists(SteamFile(appId, "library_hero.jpg"))))
            {
                var ui = main.Dispatcher;
                System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    Fetch(appId, "logo.png");
                    Fetch(appId, "library_hero.jpg");
                    ui.BeginInvoke(new Action(() => ShowSession(game, seconds, true)));
                });
                return;
            }
            // a progress card up now: this one waits its turn
            if (source != null) { Later(1500, () => ShowSession(game, seconds)); return; }
            if (layer == null) Build();
            Place(main);
            var api = HomeNav.Api;
            // as Selene Legacy had it: the game's art faint on the right; "Session ended", the name and two figures on the left;
            // the logo on the right
            ImageSource art = null, pic = null;
            try
            {
                var file = !string.IsNullOrEmpty(game.BackgroundImage) ? api.Database.GetFullFilePath(game.BackgroundImage) : null;
                if (file != null && System.IO.File.Exists(file)) art = Wallpapers.Load(file, 760);
                if (art == null && appId != null && System.IO.File.Exists(SteamFile(appId, "library_hero.jpg"))) art = Wallpapers.Load(SteamFile(appId, "library_hero.jpg"), 760);
                var logo = System.IO.Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", game.Id.ToString(), "logo.png");
                if (!System.IO.File.Exists(logo) && appId != null) logo = SteamFile(appId, "logo.png");
                if (System.IO.File.Exists(logo)) pic = Wallpapers.Load(logo, 440);
            }
            catch { }
            var content = new Grid { Width = SessionWidth, Height = SessionHeight, Clip = new RectangleGeometry(new Rect(0, 0, SessionWidth, SessionHeight), 14, 14) };
            if (art != null)
            {
                var picture = new Image
                {
                    Source = art, Stretch = Stretch.UniformToFill, Opacity = 0.42,
                    OpacityMask = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.15), new GradientStop(Colors.Black, 0.85) }, 0)
                };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.Fant);
                content.Children.Add(new Border
                {
                    Child = picture,
                    OpacityMask = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0.45), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) }, 90)
                });
            }
            var inside = new Grid { Margin = new Thickness(20, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
            inside.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inside.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = "Session ended", FontFamily = Ui.Body, FontSize = 15, Foreground = Ink.Brush("SeleneInkColor", 0.62) });
            left.Children.Add(new TextBlock { Text = game.Name, FontFamily = Ui.Display, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 8) });
            var figures = new WrapPanel { Orientation = Orientation.Horizontal };   // (wraps rather than cutting a long total off)
            figures.Children.Add(Figure(Duration(seconds), "this session"));
            if (game.Playtime > 0)
            {
                figures.Children.Add(new TextBlock { Text = "\u00B7", FontFamily = Ui.Body, FontSize = 18, Foreground = Ink.Brush("SeleneInkColor", 0.45), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 3) });
                figures.Children.Add(Figure(Duration(game.Playtime), "total"));
            }
            left.Children.Add(figures);
            inside.Children.Add(left);
            if (pic != null)
            {
                var logoImage = new Image
                {
                    Source = pic, MaxWidth = 170, MaxHeight = 88, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 4, 0),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Opacity = 0.6, Color = Colors.Black }
                };
                RenderOptions.SetBitmapScalingMode(logoImage, BitmapScalingMode.Fant);
                Grid.SetColumn(logoImage, 1);
                inside.Children.Add(logoImage);
            }
            content.Children.Add(inside);
            Shape(true);
            card.Child = content;
            // in, held for a few seconds, out (no task behind it)
            if (leaveTimer != null) leaveTimer.Stop();
            leaving = false;
            shownAt = DateTime.Now;
            layer.Visibility = Visibility.Visible;
            SlideIn();   // the same way in as every card
            Ui.NotifySound();
            leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8000) };
            leaveTimer.Tick += (s, e) => { leaveTimer.Stop(); Leave(); };
            leaveTimer.Start();
            HomeNav.Log("session card: " + game.Name + ", " + seconds + " s");
        }

        // a short note of Selene's own (a new version is out...): the usual card with an icon, a bold line and a line under it,
        // held for a few seconds
        public static void ShowNote(string title, string line, int glyph) { ShowNote(title, line, glyph, false); }
        public static void ShowNote(string title, string line, int glyph, bool first) { ShowNote(title, line, () => Ui.Glyph(Ui.Icon(glyph), Ui.Icons, 22, Ink.Brush("GlyphColor")), first); }

        // a picture alone (the controller's battery): the card is just the picture's tile, nothing written
        public static void ShowPicture(Func<UIElement> picture) { ShowNote("", "", picture, false); }

        // first: a note that goes before any task's card (a new Selene): the task's card steps aside for it and comes back after
        // (the picture made fresh each showing: a card put off and shown later can't reuse an element already placed)
        public static void ShowNote(string title, string line, Func<UIElement> picture, bool first)
        {
            var main = Application.Current.MainWindow;
            if (main == null) return;
            if ((source != null && !first) || !Ready || swapping) { Later(first ? 300 : 1500, () => ShowNote(title, line, picture, first)); return; }   // a task's card up, or the start-up: after it
            if (first && source != null)
            {
                var task = source;
                Later(NoteHoldMs + 400, () => { if (ReferenceEquals(source, task) && Equals(displayed, title)) { HomeNav.Log("card: back to " + TaskText(task)); Show(main, task); } });
            }
            if (layer == null) Build();
            if (NoteHoldLeft > 0) { Later(NoteHoldLeft + 50, () => ShowNote(title, line, picture, first)); return; }   // (the note up keeps its time)
            if (layer.Visibility == Visibility.Visible && !leaving) { SwapOut(() => ShowNote(title, line, picture, first)); return; }
            Place(main);
            Shape(false);
            displayed = title;
            noteHeldUntil = DateTime.Now.AddMilliseconds(NoteHoldMs);
            icon.Background = Ink.Brush("GlyphColor", 0.18);
            icon.Child = picture();
            icon.Visibility = Visibility.Visible;
            bool pictureOnly = string.IsNullOrEmpty(title) && string.IsNullOrEmpty(line);
            if (pictureOnly)
            {
                // a slim pill with the picture on its glass, as a console's controller notice: no tile, no words
                icon.Margin = new Thickness(0);
                icon.Background = Brushes.Transparent;
                icon.Width = double.NaN;
                icon.Padding = new Thickness(6, 0, 6, 0);
                text.Visibility = Visibility.Collapsed;
                titleLine.Visibility = Visibility.Collapsed;
                words.Text = "";
                card.Height = 60;
                card.Padding = new Thickness(14, 0, 14, 0);
                icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Size(icon.DesiredSize.Width + 28 + 2, true);
            }
            else
            {
            titleLine.Content = new TextBlock { Text = title, FontFamily = Ui.Display, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = CardWidth - Around };
            titleLine.Visibility = Visibility.Visible;
            titleLine.Margin = new Thickness(0, 0, 0, 2);
            words.Text = line;
            words.FontSize = 15;
            words.Foreground = Ink.Brush("SeleneInkColor", 0.72);
            words.TextWrapping = TextWrapping.NoWrap;
            Size(Need(title, line, 18, 15));
            }
            if (leaveTimer != null) leaveTimer.Stop();
            leaving = false;
            shownAt = DateTime.Now;
            layer.Visibility = Visibility.Visible;
            SlideIn();   // solid all the way: in from beyond the right edge (a fade showed the art through it)
            Ui.NotifySound();
            leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(pictureOnly ? 6000 : 9000) };
            leaveTimer.Tick += (s, e) => { leaveTimer.Stop(); Leave(); };
            leaveTimer.Start();
            HomeNav.Log("note card: " + (pictureOnly ? "(picture)" : title));
        }

        // the game's Steam app id: a Steam game's own, or the stand-in's (Expedition 33); null for anything else
        private const string SteamPlugin = "cb91dfc9-b977-43bf-8e70-55f46e410fab";
        private static string SteamAppId(Playnite.SDK.Models.Game game)
        {
            if (game.PluginId.ToString().Equals(SteamPlugin, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(game.GameId)) return game.GameId;
            if (game.Id == Guid.Empty) return game.GameId ?? "1903340";   // (the stand-in only: a game of yours by that name has its own id)
            return null;
        }

        private static string SteamFile(string appId, string name) { return System.IO.Path.Combine(SelenePaths.Media("Steam"), appId + "-" + name); }

        private static void Fetch(string appId, string name)
        {
            if (!DownloadConsent.Allowed("artdownload")) return;
            var file = SteamFile(appId, name);
            if (System.IO.File.Exists(file)) return;
            try
            {
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
                using (var web = new System.Net.WebClient()) web.DownloadFile("https://cdn.akamai.steamstatic.com/steam/apps/" + appId + "/" + name, file + ".part");
                if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                System.IO.File.Move(file + ".part", file);
            }
            catch (Exception ex) { HomeNav.Log("session card: no " + name + " for " + appId + " (" + ex.Message + ")"); try { System.IO.File.Delete(file + ".part"); } catch { } }
        }

        // one of the session card's figures: the value, its words after it on the same line
        private static FrameworkElement Figure(string value, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = value, FontFamily = Ui.Display, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Bottom });
            sp.Children.Add(new TextBlock { Text = label, FontFamily = Ui.Body, FontSize = 15, Foreground = Ink.Brush("SeleneInkColor", 0.6), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(7, 0, 0, 2) });
            return sp;
        }

        private static string Duration(ulong seconds)
        {
            if (seconds < 60) return "less than a minute";
            ulong h = seconds / 3600, m = seconds % 3600 / 60;
            if (h == 0) return m + (m == 1 ? " minute" : " minutes");
            return h + " h" + (m > 0 ? " " + m + " min" : "");
        }

        private static void Later(int ms, Action act)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); act(); };
            t.Start();
        }

        // dev: %TEMP%\selene-dev-session makes a pretend session card a few seconds after start (a game from the library, else a
        // stand-in), then the file goes
        public static void DevSessionLater()
        {
            var flag = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-session");
            if (!System.IO.File.Exists(flag)) return;
            try { System.IO.File.Delete(flag); } catch { }
            Later(6000, () =>
            {
                var api = HomeNav.Api;
                Playnite.SDK.Models.Game game = null;
                try { game = api != null ? api.Database.Games.OrderByDescending(g => g.Playtime).FirstOrDefault() : null; } catch { }
                if (game == null) game = new Playnite.SDK.Models.Game("Clair Obscur: Expedition 33") { Playtime = 45 * 3600 + 20 * 60, GameId = "1903340" };
                HomeNav.Log("dev: session card preview");
                ShowSession(game, 5040);   // 1 h 24 min
            });
        }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name);
            return p != null ? p.GetValue(o, null) : null;
        }

        private static double ToDouble(object o)
        {
            try { return o == null ? 0 : Convert.ToDouble(o); } catch { return 0; }
        }

        // ---- a dev preview: %TEMP%\selene-dev-toast makes a pretend 8 s task a few seconds after start (then the file goes) ----
        public static void DevPreviewLater()
        {
            var flag = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-toast");
            if (!System.IO.File.Exists(flag)) return;
            try { System.IO.File.Delete(flag); } catch { }
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                var api = HomeNav.Api;
                if (api == null) return;
                HomeNav.Log("dev: notification preview");
                api.Dialogs.ActivateGlobalProgress(a =>
                {
                    a.ProgressMaxValue = 40;
                    for (int i = 0; i <= 40 && !a.CancelToken.IsCancellationRequested; i++)
                    {
                        a.CurrentProgressValue = i;
                        a.Text = "Downloading logos of newly added games… " + i + "/40";
                        System.Threading.Thread.Sleep(200);
                    }
                }, new Playnite.SDK.GlobalProgressOptions("Downloading logos of newly added games…", false) { IsIndeterminate = false });
            };
            t.Start();
        }
    }
}
