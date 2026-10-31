using System;
using System.Windows;

namespace Selene
{
    // Whether Selene is really in view: Playnite's window is shown (not minimized, not hidden in the tray) and no game is in front
    // of it. Everything that moves or plays by itself is paused while it isn't: the looping background video, the trailers, the
    // dust and the breathing light. WPF keeps running animations and decoding video for a window nobody can see (measured on a
    // minimized Playnite: the same ~2 ms animation pass per frame as one in front, VRAM held), and a game in front can use that.
    // The window's content tree, bindings and image caches remain attached. Switch: Advanced > Pause when out of view ("presence").
    public static class ScreenPresence
    {
        private static Window watched;
        private static bool inView = true;
        public static bool InView { get { return inView; } }

        // raised on the UI thread, once per change
        public static event Action<bool> Changed;

        public static void Watch(Window main)
        {
            if (main == null || ReferenceEquals(main, watched)) return;
            watched = main;
            main.StateChanged += (s, e) => Evaluate();
            main.IsVisibleChanged += (s, e) => Evaluate();
            main.Activated += (s, e) => Evaluate();
            main.Deactivated += (s, e) => Evaluate();
            Evaluate();
        }

        // also called from Ui.WatchFullRate's half-second check: a game coming to the front raises no window event of Selene's own
        public static void Evaluate()
        {
            bool now = Compute();
            UpdateAmbient(now);
            if (now == inView) return;
            inView = now;
            HomeNav.Log("presence: " + (now ? "in view (media and ambient motion resume)" : "out of view (media and ambient motion paused)"));
            var handlers = Changed;
            if (handlers != null)
                foreach (Action<bool> h in handlers.GetInvocationList())
                    try { h(now); } catch (Exception ex) { HomeNav.Log("presence: " + ex.Message); }
        }

        private static bool Compute()
        {
            if (!Features.On("presence")) return true;
            var w = watched;
            if (w == null) return true;
            if (!w.IsVisible || w.WindowState == WindowState.Minimized) { behind = 0; return false; }
            // another program in front while a game runs (a full-screen game, or a borderless one over Playnite). Checked in
            // this order: the cheap test first, the games list only when something else has the screen. Two looks in a row, so
            // a moment with no foreground window (switching tasks) doesn't pause and restart everything.
            if (Ui.PlayniteInFront(w) || !HomeNav.GameRunning()) { behind = 0; return true; }
            return ++behind < 2 ? inView : false;
        }

        private static int behind;

        // ---- calm when idle: nobody has pressed anything for a while, the background motion eases to a stop ----
        // In view but untouched (Selene left on while the TV plays music, or someone reading), the moving background (looping video,
        // drifting dust, breathing light) and the focused tile's ring kept the GPU redrawing the whole screen every frame: ~10% of
        // an RTX 5070 Ti / 5060 Ti at 4K, against ~1% for themes that stand still (#91). After IdleSeconds without input they
        // pause, still pictures stay; the first press brings them back. Trailers and songs are not touched.
        // Switch: Advanced > Calm when idle ("idlecalm").
        private const int IdleSeconds = 45;
        private static bool ambient = true;
        private static System.Windows.Threading.DispatcherTimer idleCheck;

        public static bool Idle { get { return Features.On("idlecalm") && (DateTime.Now - Ui.LastInput).TotalSeconds > IdleSeconds; } }

        private static void UpdateAmbient(bool view)
        {
            if (idleCheck == null && Application.Current != null)
            {
                idleCheck = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                idleCheck.Tick += (s, e) => UpdateAmbient(inView);
                idleCheck.Start();
            }
            bool now = view && !Idle;
            if (Application.Current != null && !Equals(Application.Current.Resources["SeleneAmbientMotionEnabled"], now))
                Application.Current.Resources["SeleneAmbientMotionEnabled"] = now;
            if (now == ambient) return;
            ambient = now;
            if (view) HomeNav.Log("presence: " + (now ? "input again (background motion resumes)" : "idle " + IdleSeconds + " s (background motion paused)"));
            try { LoginScene.SetAmbient(now); } catch (Exception ex) { HomeNav.Log("presence: " + ex.Message); }
        }

        // a press while calm: the motion comes back at once (not on the next 2 s check)
        public static void Woken() { if (!ambient && inView) UpdateAmbient(inView); }
    }
}
