// Close-to-tray for Playnite Fullscreen: Exit / X / taskbar Close hide Playnite to the notification area (hidden icons) and
// mute it, so the next TV switch or Guide press brings it back instantly instead of a cold start. Real shutdowns
// (Windows shutting down, Playnite restarting itself) still go through. Alt+F4, or right-click the tray icon, quits for real.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Selene
{
    public static class CloseToTray
    {
        private static System.Windows.Forms.NotifyIcon tray;
        private static ICommand originalExit;
        private static bool reallyQuit;
        private static bool explicitShow;          // the user asked to see Playnite (tray click, Guide)
        private static DateTime keepHiddenUntil;   // --startclosedtotray: Playnite's own startup restore must not undo it

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // No Windows fade when Playnite hides to the tray or comes back: instant, like Solaris' exit
        private static void NoTransitions(Window w)
        {
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int on = 1;
            DwmSetWindowAttribute(h, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref on, sizeof(int));
        }

        public static void Install()
        {
            var w = Application.Current.MainWindow;
            if (w == null) return;
            NoTransitions(w);
            w.SourceInitialized += (s, e) => NoTransitions(w);
            // Alt+F4 / title-bar X / taskbar Close arrive as SC_CLOSE; Playnite's own Closing handler starts its shutdown
            // before ours can cancel, so swallow SC_CLOSE here first. (Window.Close() from Playnite's quit paths sends
            // WM_CLOSE, not SC_CLOSE, and still goes through; the tray menu is the way to quit.)
            var src = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(w).Handle);
            if (src != null) src.AddHook(SysCloseHook);
            else w.SourceInitialized += (s, e) => System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(w).Handle).AddHook(SysCloseHook);
            w.Closing += (s, e) =>
            {
                if (reallyQuit || AppShuttingDown()) return;
                e.Cancel = true;
                HideToTray();
            };
            w.IsVisibleChanged += (s, e) => { if (w.IsVisible && w.WindowState != WindowState.Minimized) Shown(); };
            w.StateChanged += (s, e) =>
            {
                // minimized = into the tray too: no taskbar button, only the hidden-icons area
                if (w.WindowState == WindowState.Minimized)
                    w.Dispatcher.BeginInvoke(new Action(() => { if (w.WindowState == WindowState.Minimized && w.IsVisible) HideToTray(); }));
                else if (w.IsVisible) Shown();
            };
            Application.Current.SessionEnding += (s, e) => { reallyQuit = true; HomeNav.BeginQuit(); };
            // Alt+F4 quits for real from anywhere in Playnite: caught as the raw key message on Playnite's thread, before any
            // screen, dialog or handler sees it (the WPF key event and the close command it becomes didn't always arrive)
            System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage += OnThreadMessage;

            // Playnite only honours --startclosedtotray in Desktop mode; do it for Fullscreen (background preload at login)
            foreach (var a in Environment.GetCommandLineArgs())
                if (a.Equals("--startclosedtotray", StringComparison.OrdinalIgnoreCase)) keepHiddenUntil = DateTime.Now.AddSeconds(40);
            if (DateTime.Now < keepHiddenUntil)
            {
                HideToTray();
                w.IsVisibleChanged += (s, e) =>
                {
                    if (w.IsVisible && !explicitShow && DateTime.Now < keepHiddenUntil)
                        w.Dispatcher.BeginInvoke(new Action(() => { if (w.IsVisible && !explicitShow) HideToTray(); }));
                };
                HomeNav.Log("tray: started hidden (background preload)");
            }
            else
            {
                // Windows remembers an app's mute in the volume mixer: if Playnite was closed while hidden (muted),
                // it would start muted. Audio sessions appear as sounds first play, so clear it a few times early on.
                foreach (var sec in new[] { 1, 4, 10, 25 })
                {
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
                    t.Tick += (s2, e2) => { t.Stop(); if (w.IsVisible && w.WindowState != WindowState.Minimized) System.Threading.Tasks.Task.Factory.StartNew(() => SetOwnAudioMute(false)); };   // off the UI thread: it took ~100 ms (a hitch in the intro)
                    t.Start();
                }
            }
            // only when it was muted (going through every audio device on each activation took ~100 ms: a hitch whenever a
            // notification or a dialog handed the focus back)
            w.Activated += (s, e) => { if (ownMuted && w.IsVisible && w.WindowState != WindowState.Minimized) SetOwnAudioMute(false); };
            Application.Current.Exit += (s, e) => { if (tray != null) { tray.Visible = false; tray.Dispose(); } };

            // The theme builds its power panel only when it opens, so catch "Exit Playnite" at the moment it is pressed:
            // ButtonBase raises Click before running its Command, so swapping the command here re-routes that press.
            EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyButtonClick), true);
        }

        // TV mode = the main screen is the 4K TV
        private static bool OnTv()
        {
            try { return System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width >= 3000; } catch { return false; }
        }

        private static IntPtr SysCloseHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0112 /* WM_SYSCOMMAND */ && ((int)wParam & 0xFFF0) == 0xF060 /* SC_CLOSE */ && !reallyQuit && !AppShuttingDown())
            {
                HomeNav.Log("tray: close command (Alt " + ((GetAsyncKeyState(0x12) & 0x8000) != 0) + ", F4 " + ((GetAsyncKeyState(0x73) & 0x8000) != 0) + ")");
                // Alt+F4 quits for real: the close goes on to Playnite as usual (Exit in the menu, the X, the taskbar still hide)
                if (AltF4Held())
                {
                    reallyQuit = true;
                    HomeNav.BeginQuit();
                    if (tray != null) tray.Visible = false;
                    HomeNav.Log("tray: Alt+F4 -> quit");
                    return IntPtr.Zero;
                }
                handled = true;
                // on the TV (console mode) Playnite is the whole screen: hiding it left a black desktop the pad can't reach
                if (OnTv()) { HomeNav.Log("tray: close ignored on the TV"); return IntPtr.Zero; }
                Application.Current.Dispatcher.BeginInvoke(new Action(HideToTray));
                HomeNav.Log("tray: Alt+F4 / close -> tray");
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

        // the close came from Alt+F4 (both keys down as Windows turns them into the close command)
        private static bool AltF4Held()
        {
            return (GetAsyncKeyState(0x73 /* VK_F4 */) & 0x8000) != 0 && (GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
        }

        private static void OnAnyButtonClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as ButtonBase;
            if (btn == null || btn.Command is ActionCommand) return;
            var w = Application.Current.MainWindow;
            if (btn.Name == "ExitCommand")  // power panel "Exit Playnite"
            {
                originalExit = btn.Command;
                btn.Command = new ActionCommand(() =>
                {
                    HideToTray();  // vanish first, so the power panel's closing animation is never seen
                    var power = HomeNav.FindByName(w, "ButtonPower") as ToggleButton;
                    if (power != null) power.IsChecked = true;  // then close the power panel (same as pressing B there)
                });
                HomeNav.Log("tray: power panel Exit re-routed to the tray");
            }
            else if (btn.Name == "MinimizeCommand")  // power panel "Minimize": the panel stayed open while hidden, and on return
            {                                         // the D-pad moved the home behind it
                btn.Command = new ActionCommand(() =>
                {
                    HideToTray();
                    var power = HomeNav.FindByName(w, "ButtonPower") as ToggleButton;
                    if (power != null) power.IsChecked = true;
                });
                HomeNav.Log("tray: power panel Minimize re-routed to the tray");
            }
            else if (btn.Name == "PART_ButtonMinimize")  // Start menu "Minimize"
            {
                btn.Command = new ActionCommand(() =>
                {
                    HideToTray();
                    var menu = Window.GetWindow(btn);
                    if (menu != null && menu != w) menu.Close();
                });
            }
            else if (btn.Name == "PART_ButtonExitPlaynite")  // Start menu "Exit Playnite" (the old theme rewires it to video + quit)
            {
                btn.Command = new ActionCommand(() =>
                {
                    HideToTray();
                    var menu = Window.GetWindow(btn);
                    if (menu != null && menu != w) menu.Close();
                });
                HomeNav.Log("tray: Start menu Exit re-routed to the tray");
            }
        }

        public static bool IsHidden { get { var w = Application.Current.MainWindow; return w != null && !w.IsVisible; } }

        public static void HideToTray()
        {
            var w = Application.Current.MainWindow;
            if (w == null) return;
            // a game paused (frozen) by Guide must never be left frozen behind a hidden Playnite: it got stuck, pad and all
            GamePause.Resume();
            // Settings panel / menus are separate windows: hidden with Playnite they came back without focus (the panel
            // stayed up while the pad drove the home behind it). Close them, Playnite always returns to a clean home.
            // only Playnite's own panels/menus: our launch cover ("SeleneLaunchCover") is a window too and must stay up while
            // the game loads (closing it showed the Windows desktop instead of the loading screen)
            foreach (Window other in Application.Current.Windows)
                if (other != w && other.IsVisible && other.Title != "SeleneLaunchCover" && (other.GetType().Namespace ?? "").StartsWith("Playnite"))
                { try { other.Close(); HomeNav.Log("tray: closed " + other.GetType().FullName + " before hiding"); } catch { } }
            EnsureTray();
            tray.Visible = true;
            NoTransitions(w);
            // start hidden-black, so coming back fades in (no flash of the old frame)
            var root = w.Content as UIElement;
            if (root != null) { root.BeginAnimation(UIElement.OpacityProperty, null); root.Opacity = 0; root.Opacity = 1; root.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.Zero)); }
            cameFromTray = true;
            w.Hide();
            SetOwnAudioMute(true);
            HomeNav.Log("tray: Playnite hidden to the tray (muted)");
        }

        // Guide during a game: make the window showable again, then let the normal Guide routine bring it forward
        public static void Unhide()
        {
            var w = Application.Current.MainWindow;
            if (w == null || w.IsVisible) return;
            explicitShow = true;
            w.Show();
            Shown();
        }

        public static void ShowFromTray()
        {
            var w = Application.Current.MainWindow;
            if (w == null) return;
            explicitShow = true;
            w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            Shown();
        }

        private static bool cameFromTray;

        private static void Shown()
        {
            if (cameFromTray)
            {
                cameFromTray = false;
                var w = Application.Current.MainWindow;
                if (w != null) HomeNav.FadeInRoot(w.Content as UIElement);
            }
            SetOwnAudioMute(false);
            if (Features.On("startrefresh")) HomeNav.RefreshLibrary("shown");
            if (tray != null) tray.Visible = false;
        }

        private static void OnThreadMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
        {
            // WM_SYSKEYDOWN for F4 with Alt held (lParam bit 29), not a key repeat (bit 30)
            if (handled || msg.message != 0x0104 || (int)msg.wParam != 0x73) return;
            long lp = msg.lParam.ToInt64();
            if (((lp >> 29) & 1) == 0 || ((lp >> 30) & 1) == 1) return;
            handled = true;
            AltF4();
        }

        // Alt+F4: quit for real, the way Alt+F4 quits Playnite without Selene (its main window closes, Playnite shuts down)
        public static void AltF4() { QuitNow("Alt+F4"); }

        // Playnite closed for real, from anywhere (Alt+F4, the power menu's Quit): the main window is closed, which is Playnite's
        // own way out. (Playnite's Exit command was not used: it first closes its menu window, and on a menu that was never
        // opened that call waits forever, so the screen went dark and stayed (#27, #26).)
        public static void QuitNow(string why)
        {
            HomeNav.BeginQuit();
            if (reallyQuit) return;
            HomeNav.Log("tray: " + why + " -> quit");
            reallyQuit = true;
            if (tray != null) tray.Visible = false;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var main = Application.Current.MainWindow;
                    // a dialog up (an install question...) goes first, or the main window can't close under it
                    foreach (Window other in Application.Current.Windows) if (!ReferenceEquals(other, main) && other.IsVisible) { try { other.Close(); } catch { } }
                    if (main != null) main.Close(); else Application.Current.Shutdown();
                }
                catch (Exception ex) { HomeNav.Log("tray: Alt+F4 quit failed: " + ex.Message); Application.Current.Shutdown(); }
            }));
        }

        private static void QuitForReal()
        {
            reallyQuit = true;
            HomeNav.BeginQuit();
            if (tray != null) tray.Visible = false;
            if (originalExit != null && originalExit.CanExecute(null)) originalExit.Execute(null);
            else Application.Current.Shutdown();
        }

        private static void EnsureTray()
        {
            if (tray != null) return;
            tray = new System.Windows.Forms.NotifyIcon();
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName); }
            catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            tray.Text = "Playnite (click to open)";
            tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open Playnite", null, (s, e) => ShowFromTray());
            menu.Items.Add("Quit Playnite completely", null, (s, e) => QuitForReal());
            tray.ContextMenuStrip = menu;
        }

        // Application.Shutdown closes windows too; never block that (Playnite restarting itself, updates, exit paths)
        public static bool AppShuttingDown()
        {
            foreach (var name in new[] { "_appIsShutdown", "_isShuttingDown" })
            {
                var f = typeof(Application).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(Application.Current)) return true;
            }
            return Application.Current.Dispatcher.HasShutdownStarted;
        }

        private sealed class ActionCommand : ICommand
        {
            private readonly Action action;
            public ActionCommand(Action action) { this.action = action; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) { return true; }
            public void Execute(object parameter) { action(); }
        }

        // ---- mute this process in the Windows volume mixer (UniPlaySong music, trailers, UI sounds) ----

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection { int GetCount(out int n); int Item(int i, out IMMDevice d); }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        }
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
            int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
            int GetSessionEnumerator(out IAudioSessionEnumerator e);
        }
        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int n);
            int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object s);
        }
        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            int GetState(out int s);
            int GetDisplayName(IntPtr p);
            int SetDisplayName(IntPtr a, IntPtr b);
            int GetIconPath(IntPtr p);
            int SetIconPath(IntPtr a, IntPtr b);
            int GetGroupingParam(IntPtr p);
            int SetGroupingParam(IntPtr a, IntPtr b);
            int RegisterAudioSessionNotification(IntPtr a);
            int UnregisterAudioSessionNotification(IntPtr a);
            int GetSessionIdentifier(IntPtr p);
            int GetSessionInstanceIdentifier(IntPtr p);
            int GetProcessId(out uint pid);
        }
        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float v, ref Guid ctx);
            int GetMasterVolume(out float v);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        }

        // another app's volume in the Windows volume mixer (Spotify, when it is the ambient music): the level it had before
        // is returned the first time, so it can be put back. -1 when the app has no audio session (not running).
        public static float SetAppVolume(string process, float volume)
        {
            float before = -1;
            try
            {
                var pids = new System.Collections.Generic.HashSet<uint>(System.Linq.Enumerable.Select(System.Diagnostics.Process.GetProcessesByName(process), p => (uint)p.Id));
                if (pids.Count == 0) return -1;
                var en = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
                IMMDeviceCollection devs;
                en.EnumAudioEndpoints(0, 1, out devs);
                int n; devs.GetCount(out n);
                var iid = typeof(IAudioSessionManager2).GUID;
                var ctx = Guid.Empty;
                for (int i = 0; i < n; i++)
                {
                    IMMDevice dev; devs.Item(i, out dev);
                    object o;
                    if (dev.Activate(ref iid, 1, IntPtr.Zero, out o) != 0) continue;
                    IAudioSessionEnumerator se;
                    ((IAudioSessionManager2)o).GetSessionEnumerator(out se);
                    int count; se.GetCount(out count);
                    for (int k = 0; k < count; k++)
                    {
                        object sess; se.GetSession(k, out sess);
                        uint pid;
                        if (((IAudioSessionControl2)sess).GetProcessId(out pid) != 0 || !pids.Contains(pid)) continue;
                        var vol = (ISimpleAudioVolume)sess;
                        float v; if (before < 0 && vol.GetMasterVolume(out v) == 0) before = v;
                        vol.SetMasterVolume(volume, ref ctx);
                    }
                }
            }
            catch (Exception ex) { HomeNav.Log("app volume " + process + ": " + ex.Message); }
            return before;
        }

        private static bool ownMuted;
        // Playnite quit while hidden in the tray: Windows remembers an app's mute, so it is lifted on the way out (another theme would start silent)
        public static void UnmuteOnExit() { if (ownMuted && !HomeNav.Isolated) SetOwnAudioMute(false); }

        public static void SetOwnAudioMute(bool mute)
        {
            if (HomeNav.Isolated) mute = true;
            ownMuted = mute;
            try
            {
                uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                var en = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
                IMMDeviceCollection devs;
                en.EnumAudioEndpoints(0, 1, out devs);  // active playback devices
                int n; devs.GetCount(out n);
                var iid = typeof(IAudioSessionManager2).GUID;
                var ctx = Guid.Empty;
                int changed = 0;
                for (int i = 0; i < n; i++)
                {
                    IMMDevice dev; devs.Item(i, out dev);
                    object o;
                    if (dev.Activate(ref iid, 1, IntPtr.Zero, out o) != 0) continue;
                    IAudioSessionEnumerator se;
                    ((IAudioSessionManager2)o).GetSessionEnumerator(out se);
                    int count; se.GetCount(out count);
                    for (int k = 0; k < count; k++)
                    {
                        object sess; se.GetSession(k, out sess);
                        uint pid;
                        if (((IAudioSessionControl2)sess).GetProcessId(out pid) == 0 && pid == me)
                        {
                            ((ISimpleAudioVolume)sess).SetMute(mute, ref ctx);
                            changed++;
                        }
                    }
                }
                HomeNav.Log("tray: audio " + (mute ? "muted" : "unmuted") + " (" + changed + " session(s))");
            }
            catch (Exception ex) { HomeNav.Log("tray: mute failed: " + ex.Message); }
        }
    }
}
