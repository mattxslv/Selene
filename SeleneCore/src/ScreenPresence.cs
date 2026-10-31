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
            if (Application.Current != null && !Equals(Application.Current.Resources["SeleneAmbientMotionEnabled"], now))
                Application.Current.Resources["SeleneAmbientMotionEnabled"] = now;
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
    }
}
