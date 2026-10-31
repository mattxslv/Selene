// The login layer (AcceuilSettings in Main.xaml), built in code into LoginCenterShell:
//   backdrop : the profile's wallpaper over the theme's gradient, a breathing accent spotlight, drifting particles
//   first start of Playnite: logo intro (crescent crown rises in, its full moon glows, chime) ->
//   no profiles yet: setup check -> profile steps (name, avatar, theme, background) -> Who's playing?
//   otherwise: Who's playing? (A signs in, Add runs the profile steps, Y edits, X removes; power menu below)
// The menu (Extensions) can bring it back for switching profile, editing the profile or the setup check.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Selene
{
    public static class LoginScene
    {
        private static Window window;
        private static FrameworkElement loginRoot;
        private static Grid scene, stage, glow;
        private static readonly List<UIElement> backdrop = new List<UIElement>();
        // opened from the home (menu, store...): closing goes back to the home exactly as it was (no intro, same focus)
        private static bool keepHome;
        private static IInputElement returnFocus;

        public static bool KeepHome { get { return keepHome; } }

        // the login screens cover the home (not a menu over it): nothing behind them should play (music, trailers)
        public static bool CoversHome { get { return IsActive && !current.Overlay; } }
        private static Screen current;
        private static bool startupQuiet;   // from launch until the home first shows
        private static bool musicHeldAtStart;   // from launch until "Who's playing?" (or the home) shows: the chime alone, then the music
        private static void ReleaseStartMusic()
        {
            if (!musicHeldAtStart) return;
            musicHeldAtStart = false;
            HomeNav.QuietStartup(false);
            HomeNav.HoldMusicForLaunch(false);
        }
        public static bool StartingUp { get { return startupQuiet; } }
        private static bool keysHooked, introPlayed;
        // what to show the next time the layer opens (set by the menu), instead of Who's playing?
        private static Func<Screen> pending;
        private static readonly Random rnd = new Random(7);

        public static void Attach(Window w, FrameworkElement login)
        {
            try
            {
                if (w == null || login == null) return;
                window = w;
                loginRoot = login;
                // keyboard works too (Enter = A, Esc = B, arrows, typing on the name step): not everyone has a controller
                if (!keysHooked)
                {
                    keysHooked = true;
                    w.PreviewKeyDown += OnKey;
                    w.PreviewTextInput += OnText;
                }
                var shell = HomeNav.FindByName(w, "LoginCenterShell") as Grid;
                if (shell == null) { HomeNav.Log("login: LoginCenterShell not found"); return; }
                if (scene == null)
                {
                    scene = BuildScene();
                    Panel.SetZIndex(scene, -100);
                    shell.Children.Insert(0, scene);
                    HomeNav.Log("login: scene attached");
                    if (!introPlayed) { startupQuiet = true; musicHeldAtStart = true; HomeNav.HoldMusicForLaunch(true); HomeNav.QuietStartup(true); }   // no music at start-up but the chime: it starts with "Who's playing?"
                }
                login.IsVisibleChanged += (s, e) =>
                {
                    if ((bool)e.NewValue) Open();
                    else
                    {
                        if (startupQuiet) { startupQuiet = false; ReleaseStartMusic(); }   // (the layer gone some other way)
                        if (current != null) { current.Hidden(); stage.Children.Clear(); current = null; }
                    }
                };
                // developer switch: %TEMP%\selene-skip-login skips the login (to look at the home while building Selene)
                if (System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-skip-login")))
                {
                    login.Visibility = Visibility.Collapsed;
                    HomeNav.Log("login: skipped (developer switch)");
                    DevKeys();   // the scripted presses still run (on the home)
                    return;
                }
                // no intro and no Who's playing?: the last profile is already signed in, the home shows straight away
                if (!Features.On("intro") && SignInLast())
                {
                    introPlayed = true;
                    login.Visibility = Visibility.Collapsed;
                    HomeNav.Log("login: skipped (intro and Who's playing? off)");
                    return;
                }
                if (login.IsVisible) Open();
                DevKeys();
            }
            catch (Exception ex) { HomeNav.Log("login attach failed: " + ex); }
        }

        public static bool IsActive { get { return loginRoot != null && loginRoot.IsVisible && scene != null && current != null; } }

        // the sign-in, a profile being made or the first setup up (not a card's screen: Activity, Achievements...)
        public static bool InSetup { get { return IsActive && (current is IntroScreen || current is LanguageScreen || current is WhoScreen || current is ProfileSteps || current is SetupScreen); } }

        // controller buttons (from HomeNav's input hook): everything goes to the screen while the login layer is up
        // Playnite turns each controller press into a fake arrow-key press as well; those copies arrive right after the
        // controller's own event and moved everything two steps, so keys within a moment of a pad press are ignored
        private static DateTime lastPad;

        public static bool HandleButton(string pressed)
        {
            if (!IsActive) return false;
            lastPad = DateTime.Now;
            Ui.PadWent(pressed);
            return Press(pressed);
        }

        private static bool FromKeyboard(string pressed)
        {
            if ((DateTime.Now - lastPad).TotalMilliseconds < 120 || Ui.KeyIsPadCopy(pressed.StartsWith("DPad") ? pressed : null)) return true;   // the copy of a pad press: already handled (a stick push's can come later)
            return Press(pressed);
        }

        public static bool MousePress(string pressed) { return Press(pressed); }

        private static bool Press(string pressed)
        {
            if (!IsActive) return false;
            var b = Ui.Normalize(pressed);
            HomeNav.Log("login button " + pressed);
            // Start on a Selene screen: the menu over it, the screen kept underneath for B to come back to
            if (b == "Start" && under == null && current.MenuOnStart && !current.WantsText && !(current is MenuScreen))
            {
                Ui.Pressed(b);
                ShowMenuOver(new MenuScreen());
                return true;
            }
            try { current.Handle(b); }
            catch (Exception ex) { HomeNav.Log("login: " + pressed + " failed: " + ex); }
            finally { Ui.Pressed(b); }
            return true;
        }

        private static void OnKey(object sender, KeyEventArgs e)
        {
            if (!IsActive || e.GetType().Name == "GameControllerInputEventArgs") return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (current.WantsText)
            {
                if (key == Key.Back) { current.Erase(); e.Handled = true; return; }
                if (key == Key.Space) return;   // typed as text
                if (key == Key.Enter) { FromKeyboard("Start"); e.Handled = true; return; }   // Enter = done
            }
            string b = key == Key.Enter || key == Key.Space ? "A"
                     : key == Key.Escape || key == Key.Back ? "B"
                     : key == Key.Left ? "DPadLeft" : key == Key.Right ? "DPadRight"
                     : key == Key.Up ? "DPadUp" : key == Key.Down ? "DPadDown"
                     : key == Key.Delete ? "X" : key == Key.F2 ? "Y" : key == Key.F3 ? "RightStick" : key == Key.Tab ? "Start"
                     : key == Key.PageUp ? "LeftShoulder" : key == Key.PageDown ? "RightShoulder"
                     : key == Key.OemMinus || key == Key.Subtract ? "TriggerLeft" : key == Key.OemPlus || key == Key.Add ? "TriggerRight" : null;
            if (b != null) { FromKeyboard(b); e.Handled = true; }
        }

        private static void OnText(object sender, TextCompositionEventArgs e)
        {
            if (!IsActive || !current.WantsText || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            current.Type(e.Text);
            e.Handled = true;
        }

        // developer switch: %TEMP%\selene-dev-keys holds "<ms> <button>" pairs (e.g. "3000 A 800 DPadRight"), pressed in turn
        // after start, to walk through the screens without a controller
        private static void DevKeyOnHome(string button)
        {
            var key = button == "DPadLeft" ? System.Windows.Input.Key.Left : button == "DPadRight" ? System.Windows.Input.Key.Right : button == "DPadUp" ? System.Windows.Input.Key.Up
                    : button == "DPadDown" ? System.Windows.Input.Key.Down : button == "A" ? System.Windows.Input.Key.Enter : button == "B" ? System.Windows.Input.Key.Escape : System.Windows.Input.Key.None;
            var target = System.Windows.Input.Keyboard.FocusedElement as UIElement ?? Application.Current.MainWindow;
            var source = target != null ? PresentationSource.FromVisual(target) : null;
            if (key == System.Windows.Input.Key.None || source == null) return;
            foreach (var ev in new[] { System.Windows.Input.Keyboard.PreviewKeyDownEvent, System.Windows.Input.Keyboard.KeyDownEvent })
            {
                var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = ev };
                target.RaiseEvent(args);
                if (args.Handled) break;
            }
        }

        private static void DevPad(string button, bool down) { Ui.SendPad(button, down); }

        private static void DevKeys()
        {
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-keys");
            if (!System.IO.File.Exists(file)) return;
            var parts = System.IO.File.ReadAllText(file).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var queue = new Queue<string[]>();
            for (int i = 0; i + 1 < parts.Length; i += 2) queue.Enqueue(new[] { parts[i], parts[i + 1] });
            var timer = new DispatcherTimer();
            Action next = null;
            next = () =>
            {
                if (queue.Count == 0) return;
                var step = queue.Peek();
                int ms;
                timer.Interval = TimeSpan.FromMilliseconds(int.TryParse(step[0], out ms) ? ms : 500);
                timer.Start();
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var step = queue.Dequeue();
                HomeNav.Log("dev key " + step[1]);
                if (step[1].StartsWith("pad:")) DevPad(step[1].Substring(4), true);   // a real controller press, through Playnite's input path
                else if (step[1].StartsWith("padup:")) DevPad(step[1].Substring(6), false);   // and its release
                else if (step[1] == "menu") SeleneShell.OpenMenu();
                else if (step[1].StartsWith("shot:")) HomeNav.DevShot(step[1].Substring(5));
                else if (step[1] == "blank") HomeNav.DevBlank();
                else if (step[1] == "fakequit") HomeNav.BeginQuit();
                else if (step[1] == "front?") HomeNav.DevFront();
                else if (step[1] == "guide") HomeNav.DevGuide();
                else if (step[1] == "collection") SeleneShell.ShowLibrary();
                else if (step[1].StartsWith("testgame:")) HomeNav.DevTestGame(step[1].Substring(9));
                else if (step[1] == "refresh") HomeNav.ScreenRefresh("dev key");
                else if (step[1] == "screen:extensions") OpenWith(() => GameOptionsScreen.Extensions());
                else if (step[1] == "screen:currency") OpenWith(() => new StoreCurrencyScreen(false, null));
                else if (step[1] == "screen:games") OpenWith(() => new SettingsScreen(false, "Games", "gamesget"));
                else if (step[1].StartsWith("screen:settings:")) { var group = step[1].Substring(16); OpenWith(() => new SettingsScreen(false, group, null)); }
                else if (step[1] == "minimize" || step[1] == "restore") { var mw = Application.Current.MainWindow; if (mw != null) { mw.WindowState = step[1] == "minimize" ? WindowState.Minimized : WindowState.Normal; HomeNav.Log("dev lifecycle: " + step[1] + " state=" + mw.WindowState); } }   // lifecycle runs (tools/perf-bench.ps1)
                else if (step[1] == "hidetray") { CloseToTray.HideToTray(); HomeNav.Log("dev lifecycle: hidetray"); }
                else if (step[1] == "showtray") { CloseToTray.ShowFromTray(); HomeNav.Log("dev lifecycle: showtray"); }
                else if (step[1] == "windows?") foreach (Window x in Application.Current.Windows) HomeNav.Log("dev window: '" + x.Title + "' " + x.GetType().Name + " #" + x.GetHashCode() + " visible=" + x.IsVisible + " topmost=" + x.Topmost + " opacity=" + x.Opacity.ToString("0.00") + " content=" + (x.Content != null ? x.Content.GetType().Name : "null"));
                else if (step[1] == "r3") { if (!LibraryStage.ToggleSound()) HomeNav.DevR3(); }
                else if (step[1].StartsWith("filter:")) { int d; if (int.TryParse(step[1].Substring(7), out d)) HomeNav.DevCycleFilter(d); }
                else if (step[1].StartsWith("type:") && current != null) current.Type(step[1].Substring(5));
                else if (IsActive) HandleButton(step[1]);
                else DevKeyOnHome(step[1]);   // the layer closed: the key goes to the home as a keyboard press would
                next();
            };
            next();
        }

        // the backdrop changes (another theme or wallpaper picked) under a still of what it showed, which then melts away: a
        // crossfade instead of a cut. One at a time; before the backdrop is on screen the change is simply made.
        private static Image backdropStill;
        public static void CrossfadeBackdrop(Action change)
        {
            var host = backdropHost;
            var panel = host != null ? host.Parent as Panel : null;
            if (host == null || panel == null || host.Opacity < 0.99 || host.ActualWidth < 2)
            {
                change();
                return;
            }
            try
            {
                if (backdropStill != null) { panel.Children.Remove(backdropStill); backdropStill = null; }
                int w = (int)Math.Round(host.ActualWidth / 2), h = (int)Math.Round(host.ActualHeight / 2);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 48, 48, PixelFormats.Pbgra32);
                rtb.Render(host);
                rtb.Freeze();
                var still = new Image { Source = rtb, Stretch = Stretch.Fill, IsHitTestVisible = false };
                panel.Children.Insert(panel.Children.IndexOf(host) + 1, still);
                backdropStill = still;
                change();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(520)) { EasingFunction = Ui.Out() };
                fade.Completed += (s2, e2) => { panel.Children.Remove(still); if (backdropStill == still) backdropStill = null; };
                still.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch (Exception ex) { HomeNav.Log("backdrop crossfade: " + ex.Message); change(); }
        }

        // ---------------- flow ----------------
        // spotlight and particles only on the plain gradient (no wallpaper behind)
        public static void RefreshBackdrop()
        {
            ShowBackdropKind();
            RecolorDust();
            if (glow == null) return;
            bool wallpaper = Application.Current.Resources["SeleneWallpaper"] != null;
            // a menu over the home keeps it hidden (backing out of Edit profile reapplies the look under the menu)
            double to = wallpaper || !Features.On("glow") || (current != null && current.Overlay) ? 0 : 1;
            // not on screen yet (start-up): set as it should be, or the light flashed up and faded out during the fade-in
            if (backdropHost == null || backdropHost.Opacity < 0.01) { glow.BeginAnimation(UIElement.OpacityProperty, null); glow.Opacity = to; }
            else glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(300)));
            RefreshHomeGlow();
        }

        private static void Open()
        {
            if (scene == null) return;
            RefreshBackdrop();
            FadeInBackdrop();
            var next = pending;
            pending = null;
            if (next != null) { if (!musicHeldAtStart) HomeNav.HoldMusicForLaunch(false); Show(next(), 0); }
            else if (!introPlayed) { introPlayed = true; AllowBackdropVideo(); WhenCalm(scene, () => { Show(Features.On("intro") ? (Screen)new IntroScreen() : Start(), 0); }); }
            else { if (!musicHeldAtStart) HomeNav.HoldMusicForLaunch(false); Show(Start(), 0); }
        }

        // Who's playing? switched off: the last profile (or the only one) signs in by itself. False when it's still needed:
        // the first start (no profile yet) or profiles that couldn't be read
        public static bool SignInLast()
        {
            if (Features.On("login") || Profiles.LoadError != null || Profiles.FirstRun) return false;
            if (Profiles.Active == null)
            {
                var first = Profiles.All.FirstOrDefault();
                if (first == null) return false;
                Profiles.SignIn(first.Id);
            }
            return true;
        }

        // where the login starts after the intro
        public static Screen Start()
        {
            if (Profiles.LoadError == null && Profiles.FirstRun) return LanguageSetup.Complete ? (Screen)new SetupScreen(true) : new LanguageScreen();
            return new WhoScreen(null);
        }

        // switches screens: the old one slides out to one side and fades, the new one comes in from the other
        // (dir 1 = forward, -1 = back, 0 = cross-fade)
        // ---------------- the menu over a screen ----------------
        // the screen the menu was opened over (Start there): it stays in place underneath, dimmed and still, until the menu
        // closes back to it or something from the menu replaces both
        private static Screen under;
        private static List<FrameworkElement> underPrompts = new List<FrameworkElement>();   // its button hints, hidden meanwhile
        private static Border underShade;   // the dark over it (the screen made see-through showed the backdrop's light through it)

        private static void ShowMenuOver(Screen menu)
        {
            under = current;
            current = menu;
            under.Root.IsHitTestVisible = false;
            underShade = new Border { Background = Ink.Brush("SeleneScrimColor"), Opacity = 0, IsHitTestVisible = false };
            stage.Children.Add(underShade);
            Ui.Fade(underShade, 0.7, 200);
            // its button hints went under the menu's own (Close, Select): hidden while the menu is up
            underPrompts = Ui.PromptRows(under.Root);
            foreach (var row in underPrompts) Ui.Fade(row, 0, 120);
            menu.Root.Opacity = 0;
            stage.Children.Add(menu.Root);
            menu.Shown();
            Ui.Slide(menu.Root, 0, 0, 0, 1, 220);
            HomeNav.Log("menu over " + under.GetType().Name);
        }

        // the menu closing: back to the screen under it (true), or nothing was under it (false: the layer closes as usual)
        public static bool CloseMenuOver()
        {
            if (under == null) return false;
            var menu = current;
            current = under;
            under = null;
            menu.Hidden();
            var root = menu.Root;
            Ui.Fade(root, 0, 160, 0, () => stage.Children.Remove(root));
            current.Root.IsHitTestVisible = true;
            RemoveShade();
            foreach (var row in underPrompts) Ui.Fade(row, 1, 200);
            underPrompts = new List<FrameworkElement>();
            return true;
        }

        // something opened from the menu (or the layer closing): the screen under it goes too
        private static void RemoveShade()
        {
            var shade = underShade;
            underShade = null;
            if (shade != null) Ui.Fade(shade, 0, 180, 0, () => stage.Children.Remove(shade));
        }

        private static void DropUnder()
        {
            RemoveShade();
            if (under == null) return;
            var gone = under;
            under = null;
            gone.Hidden();
            var root = gone.Root;
            Ui.Fade(root, 0, 200, 0, () => stage.Children.Remove(root));
        }

        public static void Show(Screen next, int dir)
        {
            DropUnder();
            var old = current;
            current = next;
            ApplyOverlay(next.Overlay, next.OverlayShade);
            if (next is WhoScreen) ReleaseStartMusic();   // the music comes in with "Who's playing?"
            if (!next.Overlay) HomeNav.ClearSelectedGame();
            // the new screen is set up first (its grids, pictures, preview), then both slide: set up inside the slide's first frame,
            // the work made it stutter (Selene settings -> Change theme)
            next.Root.Opacity = 0;
            stage.Children.Add(next.Root);
            if (old != null) { old.Hidden(); old.Root.IsHitTestVisible = false; }
            Ui.HoldVisuals(400);
            next.Shown();
            Lang.Refresh();
            var leaving = old != null ? old.Root : null;
            stage.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (leaving != null) Ui.Slide(leaving, 0, -dir * 90, leaving.Opacity, 0, 240, () => stage.Children.Remove(leaving));
                Ui.Slide(next.Root, dir * 90, 0, 0, 1, next.ShowMs);
                Ui.MeasureFrames("screen slide", 360);
            }), System.Windows.Threading.DispatcherPriority.Background);
            if (!scene.IsKeyboardFocusWithin) scene.Dispatcher.BeginInvoke(new Action(() => { if (IsActive) Keyboard.Focus(scene); }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // signed in (or carrying on without profiles): the layer fades away to the home
        public static DateTime ClosedAt = DateTime.MinValue;

        public static void Finish()
        {
            DropUnder();
            ClosedAt = DateTime.Now;
            if (startupQuiet) { startupQuiet = false; ReleaseStartMusic(); }
            SeleneShell.HideLogin();
            if (!keepHome) return;
            // back to where the home was, once the layer has really gone: the home's watchers read KeepHome as the layer
            // collapses, and a fixed wait ran out first when the home was busy (a Find a game pick on Spotlight), so they
            // played the hub's reopen and undid the pick
            int waited = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            t.Tick += (s, e) =>
            {
                if (loginRoot != null && loginRoot.IsVisible && ++waited < 30) return;
                t.Stop();
                var el = returnFocus as UIElement;
                if (DetailsStage.InBand) DetailsStage.FocusBand();   // back on a band of the details page: not its pills
                else if (el != null && el.IsVisible) el.Focus();
                keepHome = false;
            };
            t.Start();
        }

        // closes the layer and does something on the home once it has really gone (picking a game on Spotlight while a full
        // screen was still fading away lost the pick to the row's own guard, and the home showed no art: it was still covered)
        public static void FinishThen(Action then)
        {
            Finish();
            int waited = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            t.Tick += (s, e) =>
            {
                if (loginRoot != null && loginRoot.IsVisible && ++waited < 40) return;
                t.Stop();
                then();
            };
            t.Start();
        }

        // signing in changes the home (new profile, its favorites): it starts fresh instead of as it was
        public static void HomeChanged() { keepHome = false; }

        // the screen closing moves the focus itself (Find a game picks a game in Collection): the element focused before
        // the layer opened isn't focused again (it took the pick back half a second later)
        public static void ForgetReturnFocus() { returnFocus = null; }

        // menus show over the dimmed home; the login screens have the full backdrop
        private static void ApplyOverlay(bool on, Brush shade = null)
        {
            if (on) HomeNav.PauseForOverlay();
            // the layer just opening (still invisible): no fade, or the login's backdrop flashed up under the first menu
            var ms = loginRoot != null && loginRoot.Opacity < 0.05 ? 0 : 220;
            foreach (var el in backdrop) el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 0 : 1, TimeSpan.FromMilliseconds(ms)));
            if (!on) RefreshBackdrop();
            scene.Background = on ? shade ?? Ink.Brush("SeleneScrimColor", 0.88) : null;
            var layer = loginRoot as Panel;
            if (layer == null) return;
            if (on) layer.Background = Brushes.Transparent;
            else layer.SetResourceReference(Panel.BackgroundProperty, "SeleneScrimBrush");
        }

        // menu actions: open the layer on a given screen
        public static void OpenWith(Func<Screen> screen)
        {
            if (scene == null) return;
            if (loginRoot.IsVisible) { Show(screen(), 1); return; }
            keepHome = true;
            returnFocus = Keyboard.FocusedElement;
            pending = screen;
            SeleneShell.ShowLogin();
        }

        // ---------------- backdrop ----------------
        private static Grid backdropHost;

        // the backdrop fades in from the layer's dark color: a cover over it melts away on its own thread (ThreadedFade), so
        // Playnite's busy start-up can't make it freeze and jump. It starts once the backdrop has been drawn underneath.
        private static void FadeInBackdrop()
        {
            var host = backdropHost;
            var scenePanel = host != null ? host.Parent as Panel : null;
            if (host == null || scenePanel == null || host.Opacity > 0.99)
            {
                HomeNav.Log("backdrop fade-in skipped: host=" + (host != null) + " panel=" + (scenePanel != null) + " opacity=" + (host != null ? host.Opacity.ToString("0.00") : "-"));
                return;
            }
            var dark = Application.Current.TryFindResource("SeleneScrimBrush") as Brush ?? Brushes.Black;
            // a plain cover on the UI thread (the one drawn on its own thread never showed on some PCs: the wallpaper snapped in),
            // melting away once the scene has its size and its first frame has been drawn
            var cover = new Border { Background = dark, IsHitTestVisible = false };
            scenePanel.Children.Insert(scenePanel.Children.IndexOf(host) + 1, cover);   // over the backdrop, under the logo
            host.Opacity = 1;
            WhenCalm(scene, () =>
            {
                HomeNav.Log("backdrop fade-in: fading over 2200 ms");
                // slow and even: a gentle sine curve over two seconds, at the screen's own frame rate
                var fade = Ui.Fast(new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(2200)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                fade.Completed += (s, e) => scenePanel.Children.Remove(cover);
                cover.BeginAnimation(UIElement.OpacityProperty, fade);
            });
        }

        // once the element has its size, its first frame is drawn, and the frames come evenly (the start-up rush is over: the
        // intro's fade and slide stuttered when they shared those frames with the library being built); 2.5 s at most
        private static void WhenCalm(FrameworkElement el, Action act)
        {
            if (el == null) { act(); return; }
            Action whenSized = () =>
            {
                // calm = 700 ms without a long frame (the start-up work comes in waves: eight even frames came between two of
                // them and the fade stuttered); 3.5 s at most
                int frames = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double last = 0, calmSince = 0;
                EventHandler tick = null;
                tick = (s, e) =>
                {
                    double t = sw.Elapsed.TotalMilliseconds;
                    double gap = t - last; last = t;
                    frames++;
                    if (frames > 1 && gap > 30) calmSince = t;
                    bool calm = t - calmSince >= 700;
                    if (frames < 2 || (!calm && t < 3500)) return;
                    CompositionTarget.Rendering -= tick;
                    HomeNav.Log("intro: frames calm after " + (int)t + " ms" + (calm ? "" : " (gave up waiting)"));
                    act();
                };
                CompositionTarget.Rendering += tick;
            };
            if (el.ActualWidth > 1) { whenSized(); return; }
            bool done = false;
            SizeChangedEventHandler sized = null;
            sized = (s, e) => { if (done || el.ActualWidth <= 1) return; done = true; el.SizeChanged -= sized; whenSized(); };
            el.SizeChanged += sized;
            // and never stuck: if no size comes (an odd start), the cover goes after a while anyway
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            guard.Tick += (s, e) => { guard.Stop(); if (done) return; done = true; el.SizeChanged -= sized; act(); };
            guard.Start();
        }
        private static Grid BuildScene()
        {
            var g = new Grid { ClipToBounds = true };
            backdropHost = new Grid { Opacity = 0 };   // faded in by FadeInBackdrop
            g.Children.Add(backdropHost);
            var gradient = new Border();
            gradient.SetResourceReference(Border.BackgroundProperty, "SeleneBaseBrush");
            backdropHost.Children.Add(gradient);
            backdrop.Add(gradient);
            var defaultBackdrop = new Image { Stretch = Stretch.UniformToFill };
            defaultBackdrop.SetResourceReference(Image.SourceProperty, "SeleneDefaultBackdrop");
            backdropHost.Children.Add(defaultBackdrop);
            backdrop.Add(defaultBackdrop);
            var wallpaper = new Image { Stretch = Stretch.UniformToFill };
            wallpaper.SetResourceReference(Image.SourceProperty, "SeleneWallpaper");
            RenderOptions.SetBitmapScalingMode(wallpaper, BitmapScalingMode.HighQuality);
            backdropHost.Children.Add(wallpaper);
            backdrop.Add(wallpaper);
            var veil = new Border { Background = Ink.Brush("SeleneScrimColor", 0.45) };
            backdropHost.Children.Add(veil);
            backdrop.Add(veil);
            // the spotlight, floor glow and particles dress up the theme's plain gradient; over a wallpaper they stay off
            glow = BuildGlow();
            backdropHost.Children.Add(glow);
            backdrop.Add(glow);

            stage = new Grid();
            g.Children.Add(stage);
            // keyboard focus moves in here while the layer is up, so the home behind doesn't react to input
            g.Focusable = true;
            g.FocusVisualStyle = null;
            return g;
        }

        // the login's glow: a breathing accent spotlight, a hazy floor glow and drifting particles (the home uses it too)
        // Selene's own backdrop behind the login, the menus and Spotlight's cards. On Selene's own theme it's the legacy home's
        // looping video (theme\Media\selene-background.mp4); other themes get the drawn version in their color (a light
        // beam, haze, drifting dust). Either only on themes without a wallpaper (RefreshBackdrop / RefreshHomeGlow).
        private static readonly List<KeyValuePair<MediaElement, Grid>> backdrops = new List<KeyValuePair<MediaElement, Grid>>();

        // the background video starts with the logo intro, so the intro and the screens after it share one background (the
        // logo draws on its own thread, IntroLogo, so decoding the video no longer makes it stutter)
        private static bool videoAllowed;

        public static void AllowBackdropVideo()
        {
            if (videoAllowed) return;
            videoAllowed = true;
            ShowBackdropKind();
            foreach (var pair in backdrops) if (pair.Key != null && pair.Key.IsVisible && ambientOn) pair.Key.Play();
        }

        // the same moving background for a window of its own (Playnite's progress window); gone again with the window
        public static Grid BackdropFor(Window w)
        {
            int pairs = backdrops.Count, dustCount = dusts.Count, breathCount = breathing.Count;
            var g = BuildGlow();
            var mine = backdrops.Skip(pairs).ToList();
            var myDust = dusts.Skip(dustCount).ToList();
            var myLight = breathing.Skip(breathCount).ToList();
            w.Closed += (s, e) =>
            {
                foreach (var p in mine) { backdrops.Remove(p); if (p.Key != null) { p.Key.Stop(); p.Key.Close(); } }
                foreach (var d in myDust) { dusts.Remove(d); d.Stop(); }
                foreach (var l in myLight) { l.BeginAnimation(UIElement.OpacityProperty, null); breathing.Remove(l); }
            };
            return g;
        }

        // ---- ambient motion follows ScreenPresence: paused while Selene's window is minimized, in the tray or behind a game ----
        // (WPF would go on ticking 390 dust animations and decoding the looping video for a window nobody sees)
        private static bool ambientOn = true;
        private static readonly List<Border> breathing = new List<Border>();

        private static void Breathe(Border layer, bool on)
        {
            layer.BeginAnimation(UIElement.OpacityProperty, !on ? null : Ui.Ambient(new DoubleAnimation(0.75, 1, TimeSpan.FromSeconds(9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }));
        }

        // the motes and their animations go (a cleared canvas keeps no clocks); the same seed lays the same dust out again
        private static void ClearParticles(Canvas canvas)
        {
            foreach (UIElement dot in canvas.Children)
            {
                var tr = dot.RenderTransform as TranslateTransform;
                if (tr != null) { tr.BeginAnimation(TranslateTransform.XProperty, null); tr.BeginAnimation(TranslateTransform.YProperty, null); }
                dot.BeginAnimation(UIElement.OpacityProperty, null);
            }
            canvas.Children.Clear();
        }

        public static void SetAmbient(bool on)
        {
            if (ambientOn == on) return;
            ambientOn = on;
            foreach (var pair in backdrops)
            {
                var v = pair.Key;
                if (v == null) continue;
                try { if (on) { if (videoAllowed && v.IsVisible) v.Play(); } else v.Pause(); } catch { }
            }
            foreach (var layer in breathing) Breathe(layer, on);
            if (HomeNav.Light) return;
            var accent = Ink.Of("GlyphColor"); var ink = Ink.Of("SeleneInkColor");
            foreach (var d in dusts)
            {
                var dust = d;
                dust.Run(el =>
                {
                    var c = el as Canvas;
                    if (c == null) return;
                    ClearParticles(c);
                    if (on) FillParticles(c, dust.CurrentSize, new Random(7), accent, ink);
                });
            }
            HomeNav.Log("backdrop: ambient motion " + (on ? "resumed" : "paused"));
        }
        // developer switch: %TEMP%\selene-dev-leaveout holds "video" and / or "dust": built without them, to see what each costs (tools\perf-bench.ps1)
        internal static bool DevLeaveOut(string part)
        {
            try { var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-leaveout"); return System.IO.File.Exists(file) && System.IO.File.ReadAllText(file).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        private static Grid BuildGlow()
        {
            var outer = new Grid { IsHitTestVisible = false };
            if (HomeNav.Light || DevLeaveOut("glow")) return outer; // keep the theme's still gradient; no decoder or particle thread
            MediaElement video = null;
            var file = SelenePaths.ThemeFile("Media", "selene-background.mp4");
            if (System.IO.File.Exists(file) && !DevLeaveOut("video"))
            {
                video = new MediaElement { Source = new Uri(file), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, IsMuted = true, Stretch = Stretch.UniformToFill, Opacity = 0 };
                // fades in once its first frame is there (it was black until then), over the drawn backdrop, which melts away under
                // it: hiding the drawn one first left the plain navy up for a moment after the logo intro (a dark flicker)
                var v = video;
                video.MediaOpened += (s, e) =>
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    v.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(700)) { EasingFunction = ease });
                    var drawn = v.Tag as Grid;
                    if (drawn == null || drawn.Visibility != Visibility.Visible) return;
                    var melt = new DoubleAnimation(0, TimeSpan.FromMilliseconds(700)) { EasingFunction = ease };
                    melt.Completed += (s2, e2) =>
                    {
                        if (v.Visibility == Visibility.Visible) drawn.Visibility = Visibility.Collapsed;
                        drawn.BeginAnimation(UIElement.OpacityProperty, null);
                        drawn.Opacity = 1;
                    };
                    drawn.BeginAnimation(UIElement.OpacityProperty, melt);
                };
                video.MediaEnded += (s, e) => { video.Position = TimeSpan.Zero; if (videoAllowed && ambientOn && video.IsVisible) video.Play(); };
                // plays only while it's on screen
                video.IsVisibleChanged += (s, e) => { if ((bool)e.NewValue && videoAllowed && ambientOn) video.Play(); else video.Pause(); };
                outer.Children.Add(video);
            }
            var glow = new Grid { IsHitTestVisible = false };
            outer.Children.Add(glow);
            if (video != null) video.Tag = glow;   // the drawn backdrop the video fades in over
            backdrops.Add(new KeyValuePair<MediaElement, Grid>(video, glow));
            ShowBackdropKind();
            // a beam of light in the theme's accent falling from the top left, slowly breathing
            var spot = new RadialGradientBrush { Center = new Point(0.14, -0.08), GradientOrigin = new Point(0.14, -0.08), RadiusX = 0.2, RadiusY = 0.85 };
            spot.GradientStops.Add(Ink.Stop("GlyphColor", 0.42, 0));
            spot.GradientStops.Add(Ink.Stop("GlyphColor", 0.14, 0.5));
            spot.GradientStops.Add(Ink.Stop("GlyphColor", 0, 1));
            // and a wider, fainter wash around it
            var wash = new RadialGradientBrush { Center = new Point(0.16, -0.12), GradientOrigin = new Point(0.16, -0.12), RadiusX = 0.6, RadiusY = 1.1 };
            wash.GradientStops.Add(Ink.Stop("GlyphColor", 0.16, 0));
            wash.GradientStops.Add(Ink.Stop("GlyphColor", 0, 1));
            glow.Children.Add(new Border { Background = wash });
            var spotLayer = new Border { Background = spot };
            Breathe(spotLayer, ambientOn);
            breathing.Add(spotLayer);
            glow.Children.Add(spotLayer);

            // hazy floor glow
            var haze = new RadialGradientBrush { Center = new Point(0.5, 1.02), GradientOrigin = new Point(0.5, 1.02), RadiusX = 0.95, RadiusY = 0.34 };
            haze.GradientStops.Add(Ink.Stop("GlyphColor", 0.22, 0));
            haze.GradientStops.Add(Ink.Stop("GlyphColor", 0, 1));
            glow.Children.Add(new Border { Background = haze });

            // no dust when WPF draws in software (no usable graphics card: a VM, a remote desktop, an old driver): it can't keep
            // 130 moving motes smooth, and the glow alone still reads as the animated background
            if ((System.Windows.Media.RenderCapability.Tier >> 16) < 2) { HomeNav.Log("glow: software rendering, no dust"); return outer; }
            if (DevLeaveOut("dust")) return outer;
            // the dust on the main thread: on a thread of its own it halved the whole window's rate (30 fps on a 60 Hz TV)
            var accent = Ink.Of("GlyphColor");
            var inkColor = Ink.Of("SeleneInkColor");
            var dust = new OffThread(sz => Dust(sz, accent, inkColor), "Selene dust", inline: true);
            dust.Resized = (el, sz) => { var c = el as Canvas; if (c != null) { ClearParticles(c); if (ambientOn) FillParticles(c, sz, new Random(7), accent, inkColor); } };
            glow.Children.Add(dust);
            dusts.Add(dust);
            return outer;
        }

        private static readonly List<OffThread> dusts = new List<OffThread>();

        private static Canvas Dust(Size size, Color accent, Color inkColor)
        {
            var c = new Canvas { IsHitTestVisible = false };
            if (ambientOn) FillParticles(c, size, new Random(7), accent, inkColor);   // (out of view at start: laid out when it comes into view)
            return c;
        }

        private static Color WithAlpha(Color c, double alpha) { c.A = (byte)Math.Round(255 * alpha); return c; }

        // the theme changed: the dust takes the new colors
        private static void RecolorDust()
        {
            var accentNow = Ink.Of("GlyphColor");
            var inkNow = Ink.Of("SeleneInkColor");
            foreach (var d in dusts)
                d.Run(el =>
                {
                    var c = el as Canvas;
                    if (c == null) return;
                    foreach (var child in c.Children)
                    {
                        var dot = child as Ellipse;
                        var fill = dot != null ? dot.Fill as RadialGradientBrush : null;
                        if (fill == null) continue;
                        var col = Equals(dot.Tag, "accent") ? accentNow : inkNow;
                        foreach (var stop in fill.GradientStops) { var cc = col; cc.A = stop.Color.A; stop.Color = cc; }
                    }
                });
        }

        // the video on Selene's own theme, the drawn backdrop on the others
        private static void ShowBackdropKind()
        {
            if (HomeNav.Light)
            {
                foreach (var pair in backdrops)
                {
                    if (pair.Key != null) { pair.Key.Stop(); pair.Key.Close(); pair.Key.Source = null; pair.Key.Visibility = Visibility.Collapsed; }
                    pair.Value.Visibility = Visibility.Collapsed;
                }
                foreach (var dust in dusts) dust.Stop();
                return;
            }
            var id = Application.Current.Resources["SeleneThemeId"] as string;
            bool seleneTheme = string.IsNullOrEmpty(id) || id == ThemePalettes.DefaultId;
            foreach (var pair in backdrops)
            {
                bool useVideo = seleneTheme && pair.Key != null && pair.Key.Source != null && videoAllowed;   // the drawn one until the login layer is up
                if (pair.Key != null) pair.Key.Visibility = useVideo ? Visibility.Visible : Visibility.Collapsed;
                // the video starts with the logo intro, so the drawn backdrop never shows under it: the screen rises from the dark
                // straight into the video (the drawn one showed for half a second at start, then gave way)
                pair.Value.Visibility = useVideo ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        // ---- the same glow on the home, behind the game art: seen on the cards (and with no games), off over a wallpaper ----
        private static Grid homeGlow;
        private static bool homeOnCard = true;

        public static void AttachHomeGlow(Window w)
        {
            var bg = w != null ? HomeNav.FindByName(w, "SeleneHubGameBackground") as Grid : null;
            if (bg == null || homeGlow != null) return;
            homeGlow = BuildGlow();
            homeGlow.Opacity = 0;
            bg.Children.Insert(Math.Min(2, bg.Children.Count), homeGlow);   // over the gradient and wallpaper, under the art
            RefreshHomeGlow();
        }

        // the home shows a card (Library, Store) or a game: the glow fades in behind cards, out behind a game's art
        public static void HomeSelection(bool onCard)
        {
            if (homeOnCard == onCard) return;
            homeOnCard = onCard;
            RefreshHomeGlow();
        }

        private static void RefreshHomeGlow()
        {
            if (homeGlow == null) return;
            var res = Application.Current.Resources;
            bool artHidden = res["SeleneArtOpacity"] is double && (double)res["SeleneArtOpacity"] < 0.01;
            bool on = res["SeleneWallpaper"] == null && Features.On("glow") && (homeOnCard || artHidden);
            if (on) homeGlow.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 450 : 250)) { EasingFunction = Ui.Out() };
            if (!on) fade.Completed += (s, e) => { if (homeGlow.Opacity < 0.01) homeGlow.Visibility = Visibility.Collapsed; };   // particles stop drawing
            homeGlow.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static void FillParticles(Canvas canvas, Size size, Random rnd, Color accent, Color inkColor)
        {
            // a band of fine dust rising from the lower left to the right (denser on the right), a few soft bokeh dots.
            // Their drift is slow, and WPF's usual 60 steps a second showed as uneven on a 144 Hz display (frames doubled and
            // tripled by turns): the drift asks for the display's rate, as the row's glide does
            for (int i = 0; i < 130; i++)
            {
                bool bokeh = i < 14;
                double d = bokeh ? 10 + rnd.NextDouble() * 26 : 1.5 + rnd.NextDouble() * 3;
                bool isAccent = rnd.NextDouble() < 0.72;
                var color = isAccent ? accent : inkColor;
                var fill = new RadialGradientBrush();
                fill.GradientStops.Add(new GradientStop(WithAlpha(color, bokeh ? 0.33 : 0.94), 0));
                fill.GradientStops.Add(new GradientStop(WithAlpha(color, 0), 1));
                var dot = new Ellipse { Width = d, Height = d, Fill = fill, Tag = isAccent ? "accent" : "ink" };
                double fx = Math.Sqrt(rnd.NextDouble());                       // more of them to the right
                double band = 0.74 - 0.32 * fx;                                 // the band climbs to the right
                double spread = (rnd.NextDouble() + rnd.NextDouble() - 1) * (bokeh ? 0.3 : 0.14);
                Canvas.SetLeft(dot, fx * size.Width);
                Canvas.SetTop(dot, size.Height * Math.Max(0.1, Math.Min(0.98, band + spread)));
                var tr = new TranslateTransform();
                dot.RenderTransform = tr;
                double dur = 16 + rnd.NextDouble() * 18;
                var begin = TimeSpan.FromSeconds(-rnd.NextDouble() * dur);
                var drift = new DoubleAnimation(0, -size.Height * (0.10 + rnd.NextDouble() * 0.18), TimeSpan.FromSeconds(dur)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
                Timeline.SetDesiredFrameRate(drift, Ui.AmbientRate);
                tr.BeginAnimation(TranslateTransform.YProperty, drift);
                tr.BeginAnimation(TranslateTransform.XProperty, Ui.Ambient(new DoubleAnimation(-14, 14 + rnd.NextDouble() * 20, TimeSpan.FromSeconds(5 + rnd.NextDouble() * 6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }));
                double peak = bokeh ? 0.25 + rnd.NextDouble() * 0.3 : 0.4 + rnd.NextDouble() * 0.6;
                var op = Ui.Ambient(new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(dur), RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin });
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.18)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(peak * 0.7, KeyTime.FromPercent(0.6)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                dot.BeginAnimation(UIElement.OpacityProperty, op);
                canvas.Children.Add(dot);
            }
        }
    }

    // ---------------- logo intro ----------------
    // about 2.6 s: the crescent crown rises in, the full moon's glow breathes once, a chime, the logo fades out, then on to the next screen
    public class IntroScreen : Screen
    {
        public override bool MenuOnStart { get { return false; } }   // (signing in: no menu yet)
        private readonly IntroLogo logo = new IntroLogo(190);
        private DispatcherTimer timer;
        private bool done;

        public IntroScreen()
        {
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            logo.VerticalAlignment = VerticalAlignment.Center;
            Root.Children.Add(logo);
        }

        public override void Shown()
        {
            // the logo animates on its own thread (IntroLogo): smooth however busy Playnite's start-up keeps the main one
            logo.Start(120);
            int ticks = 0;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            timer.Tick += (s, e) =>
            {
                ticks++;
                if (ticks == 1) Ui.Boot();
                if (ticks >= 6) Next();
            };
            timer.Start();
        }

        public override void Handle(string button)
        {
            if (button == "A" || button == "B" || button == "Start") Next();
        }

        public override void Hidden()
        {
            if (timer != null) timer.Stop();
            // its thread goes once the intro has slid away (stopping it at once made the logo vanish mid-fade)
            var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            later.Tick += (s, e) => { later.Stop(); logo.Stop(); };
            later.Start();
        }

        private void Next()
        {
            if (done) return;
            done = true;
            LoginScene.AllowBackdropVideo();
            if (timer != null) timer.Stop();
            // the logo fades out as it lifts, then the next screen
            logo.FadeOut(380);
            var then = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(340) };
            then.Tick += (s, e) =>
            {
                then.Stop();
                if (LoginScene.SignInLast()) LoginScene.Finish();
                else LoginScene.Show(LoginScene.Start(), 0);
            };
            then.Start();
        }
    }

    // ---------------- Who's playing? ----------------
    public class WhoScreen : Screen
    {
        public override bool MenuOnStart { get { return false; } }   // (signing in: no menu yet)
        private readonly List<Profile> profiles;
        private readonly List<Tile> tiles = new List<Tile>();
        private readonly List<FrameworkElement> items = new List<FrameworkElement>();
        private readonly Tile power;
        private readonly Border menu;
        private readonly List<Border> menuRows = new List<Border>();
        private readonly TextBlock hint;
        private int index;            // profiles, then Add (or Continue when profiles couldn't load)
        private bool onPower, menuOpen, leaving;
        private int menuIndex;
        private Dialog confirm;

        // glyph (Segoe MDL2 Assets), label, helper command, built-in fallback
        private static readonly string[][] PowerItems = {
            new[] { "\uE708", "Sleep", "power-sleep", "rundll32.exe|powrprof.dll,SetSuspendState 0,1,0" },
            new[] { "\uE777", "Restart", "power-restart", "shutdown.exe|/r /t 0" },
            new[] { "\uE7E8", "Shut down", "power-off", "shutdown.exe|/s /t 0" } };

        public WhoScreen(Guid? focus)
        {
            profiles = Profiles.All.ToList();
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -60, 0, 0) };
            col.Children.Add(Ui.Title("Who's playing?"));
            if (Profiles.LoadError != null)
                col.Children.Add(Ui.Sub("Profiles couldn't be loaded (" + Profiles.LoadError + "). You can still play; nothing is saved this time."));
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 70, 0, 0) };
            foreach (var p in profiles) AddItem(row, Avatar(p, 168), p.Name, Accent(p));   // (168: inside the tile face's 1 px border; 170 lost its right and bottom edge)
            if (Profiles.LoadError != null) AddItem(row, Glyph("\uE72A", 54), "Continue", null);
            else AddItem(row, Glyph("\uE710", 60), "Add", null);
            col.Children.Add(row);
            hint = Ui.Text("", 18, 0.6);
            hint.Margin = new Thickness(0, 34, 0, 0);
            col.Children.Add(hint);

            Root.Children.Add(col);

            // power button, bottom centre (Down from the profiles): Sleep / Restart / Shut down
            power = new Tile(64, 64, 32, 5);
            power.Face.Child = new TextBlock { Text = Ui.Icon(0xe8ac), FontSize = 30, FontFamily = Ui.Icons, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };   // the Material power icon: drawn on a centered square, so it sits in the middle of the circle
            power.Root.HorizontalAlignment = HorizontalAlignment.Center;
            power.Root.VerticalAlignment = VerticalAlignment.Bottom;
            power.Root.Margin = new Thickness(0, 0, 0, 56);
            Root.Children.Add(power.Root);
            menu = new Border
            {
                CornerRadius = new CornerRadius(16), Background = Ink.Brush("SeleneGlassColor", 0.94), BorderBrush = Ink.Brush("SeleneInkColor", 0.2), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 140),
                Padding = new Thickness(8), Width = 320, Visibility = Visibility.Collapsed
            };
            var menuStack = new StackPanel();
            foreach (var item in PowerItems)
            {
                var r = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 12, 18, 12) };
                var rs = new StackPanel { Orientation = Orientation.Horizontal };
                rs.Children.Add(new TextBlock { Text = item[0], FontSize = 20, FontFamily = Ui.Glyphs, VerticalAlignment = VerticalAlignment.Center, Width = 36 });
                rs.Children.Add(new TextBlock { Text = item[1], FontSize = 22, FontFamily = Ui.Body, VerticalAlignment = VerticalAlignment.Center });
                r.Child = rs;
                menuRows.Add(r);
                menuStack.Children.Add(r);
            }
            menu.Child = menuStack;
            Root.Children.Add(menu);

            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Select")));
            if (profiles.Count > 0) Root.Children.Add(Ui.Prompts(false, Ui.Prompt("Y", "Edit profile"), Ui.Prompt("X", "Remove")));

            var active = Profiles.Active;
            var want = focus ?? (active != null ? active.Id : Guid.Empty);
            index = Math.Max(0, profiles.FindIndex(p => p.Id == want));
        }

        private void AddItem(Panel row, FrameworkElement face, string name, Color? accent)
        {
            var tile = new Tile(170, 170, 85, 9) { FocusScale = 1.1 };
            tile.Face.Child = face;
            tile.Face.ClipToBounds = true;
            if (accent.HasValue) tile.Tint(accent.Value);
            var item = new StackPanel { Margin = new Thickness(28, 0, 28, 0) };
            item.Children.Add(tile.Root);
            item.Children.Add(new TextBlock { Text = name, FontSize = 24, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0), MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(item);
            int selectedProfile = tiles.Count;
            tile.Root.MouseLeftButtonUp += (sender, e) =>
            {
                if (leaving || confirm != null) return;
                index = selectedProfile; onPower = false; ShowFocus(); Handle("A"); e.Handled = true;
            };
            tiles.Add(tile);
            items.Add(item);
        }

        // each profile glows in its own theme's accent
        private static Color? Accent(Profile p)
        {
            var theme = ThemePalettes.Find(string.IsNullOrEmpty(p.Theme) ? ThemePalettes.DefaultId : p.Theme);
            if (theme == null || string.IsNullOrEmpty(theme.Accent)) return null;
            try { return (Color)ColorConverter.ConvertFromString(theme.Accent); } catch (FormatException) { return null; }
        }

        public static FrameworkElement Avatar(Profile p, double size)
        {
            var pic = AnimatedAvatar.Brush(Profiles.AvatarFile(p), (int)(size * 2));   // GIFs play
            if (pic != null) return new Ellipse { Width = size, Height = size, Fill = pic };
            // no picture: the first letter on the profile's accent
            var g = new Grid { Width = size, Height = size };
            var accent = Accent(p);
            g.Children.Add(new Ellipse { Fill = accent.HasValue ? new SolidColorBrush(accent.Value) : (Brush)Ink.Brush("GlyphColor") });
            g.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(p.Name) ? "?" : p.Name.Substring(0, 1).ToUpperInvariant(), FontSize = size * 0.42, FontFamily = Ui.Display, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            return g;
        }

        private static FrameworkElement Glyph(string glyph, double size)
        {
            return new TextBlock { Text = glyph, FontSize = size, FontFamily = Ui.Glyphs, Foreground = Ink.Brush("SeleneInkColor", 0.85), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        public override void Shown()
        {
            for (int i = 0; i < items.Count; i++) Ui.Rise(items[i], 160 + i * 55);
            Ui.Rise(power.Root, 200 + items.Count * 55);
            ShowFocus();
        }

        private void ShowFocus()
        {
            for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(!onPower && i == index);
            power.Focus(onPower);
            hint.Text = onPower ? "Sleep, restart or shut down" : "";
        }

        public override void Handle(string b)
        {
            if (leaving) return;
            if (menuOpen) { HandleMenu(b); return; }
            if (confirm != null)
            {
                if (b == "X") confirm.Answer(true);   // X again: remove
                else confirm.Handle(b);
                return;
            }
            if (b == "DPadDown" && !onPower) { onPower = true; Ui.Tick(); ShowFocus(); return; }
            if (b == "DPadUp" && onPower) { onPower = false; Ui.Tick(); ShowFocus(); return; }
            if (!onPower && (b == "DPadLeft" || b == "DPadRight"))
            {
                int next = index + (b == "DPadLeft" ? -1 : 1);
                if (next < 0 || next >= tiles.Count) return;
                index = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b == "A")
            {
                if (onPower) { OpenMenu(); return; }
                if (index < profiles.Count) { SignIn(index); return; }
                Ui.Confirm();
                if (Profiles.LoadError != null) { leaving = true; LoginScene.Finish(); return; }
                LoginScene.Show(new ProfileSteps(null, () => new WhoScreen(null)), 1);
                return;
            }
            if (onPower || index >= profiles.Count) return;
            if (b == "Y") { Ui.Confirm(); LoginScene.Show(new ProfileSteps(profiles[index], () => new WhoScreen(profiles[index].Id)), 1); return; }
            if (b == "X") Remove(profiles[index]);
        }

        private void SignIn(int i)
        {
            leaving = true;
            Ui.Play("LoginSelect.wav", 0.9);
            for (int k = 0; k < items.Count; k++) if (k != i) Ui.Fade(items[k], 0, 220);
            Ui.ScaleTo(tiles[i].Root, 1.22, 380);
            Profiles.SignIn(profiles[i].Id);
            LoginScene.HomeChanged();
            HomeNav.ProfileChanged();
            var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            later.Tick += (s, e) => { later.Stop(); LoginScene.Finish(); };
            later.Start();
        }

        // X twice: removes the profile (its favorites and playtime go with it; the games stay)
        // asks first (Keep is chosen to start with); removing takes the profile's favorites and playtime, not the games
        private void Remove(Profile p)
        {
            Ui.Shake(items[index]);
            confirm = new Dialog("\uE7BA", "Remove " + p.Name + "?", "Their favorites and playtime go with them. Your games stay.", "Remove", "Keep",
                () =>
                {
                    confirm = null;
                    Profiles.Remove(p.Id);
                    LoginScene.Show(Profiles.FirstRun ? (Screen)new ProfileSteps(null, () => new WhoScreen(null)) : new WhoScreen(null), 0);
                },
                () => confirm = null);
            confirm.Open(Root);
        }

        private void OpenMenu()
        {
            menuOpen = true;
            menuIndex = 0;
            ShowMenu();
            menu.Visibility = Visibility.Visible;
            Ui.Rise(menu, 0, 14, 240);
            Ui.Play("OpenPanel.wav", 0.8);
        }

        private void ShowMenu()
        {
            for (int i = 0; i < menuRows.Count; i++)
            {
                bool on = i == menuIndex;
                menuRows[i].Background = on ? (Brush)Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
                foreach (TextBlock t in ((StackPanel)menuRows[i].Child).Children)
                    t.Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor");
            }
        }

        private void HandleMenu(string b)
        {
            if (b == "B")
            {
                menuOpen = false;
                Ui.Fade(menu, 0, 160, 0, () => { if (!menuOpen) menu.Visibility = Visibility.Collapsed; });
                Ui.Back();
            }
            else if (b == "DPadUp" && menuIndex > 0) { menuIndex--; Ui.Tick(); ShowMenu(); }
            else if (b == "DPadDown" && menuIndex < menuRows.Count - 1) { menuIndex++; Ui.Tick(); ShowMenu(); }
            else if (b == "A") RunPower(menuIndex);
        }

        private void RunPower(int i)
        {
            Ui.Confirm();
            menuOpen = false;
            menu.Visibility = Visibility.Collapsed;
            try { Power.Run(i); }
            catch (Exception ex) { HomeNav.Log("login power failed: " + ex.Message); }
        }
    }
}
