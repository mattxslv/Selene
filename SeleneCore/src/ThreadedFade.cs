using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // A cover (layers of brushes) drawn and faded on its own thread (a HostVisual, like IntroLogo). At start-up and right
    // after signing in Playnite keeps the main thread busy, and a fade run there froze and jumped; this one stays smooth.
    // The cover is up as soon as it's made; FadeOut melts it away and takes it out of its panel.
    public class ThreadedFade : FrameworkElement
    {
        private readonly HostVisual host = new HostVisual();
        private Dispatcher thread;
        private Grid root;
        private bool fading;
        private int lateWaits;
        public bool Ready { get { return thread != null; } }

        public ThreadedFade(params Brush[] layers)
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
            AddVisualChild(host);
            var size = StartSize();
            var frozen = new Brush[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] == null) continue;
                var b = layers[i].IsFrozen ? layers[i] : layers[i].Clone();
                if (b.CanFreeze) b.Freeze();
                frozen[i] = b.IsFrozen ? b : null;   // a brush that can't cross threads is left out
            }
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    var target = new VisualTarget(host);
                    root = new Grid();
                    foreach (var b in frozen) if (b != null) root.Children.Add(new Border { Background = b });
                    Arrange(size);
                    target.RootVisual = root;
                    thread = Dispatcher.CurrentDispatcher;
                    thread.UnhandledException += (s, e) => { e.Handled = true; };
                    ready.Set();
                    Dispatcher.Run();
                    target.Dispose();
                }
                catch (Exception ex) { ready.Set(); HomeNav.Log("threaded fade: " + ex.Message); }
            }) { IsBackground = true, Name = "Selene fade" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.WaitOne(2000);
            SizeChanged += (s, e) =>
            {
                var d = thread;
                var sz = e.NewSize;
                if (d != null && sz.Width > 0) d.BeginInvoke(new Action(() => Arrange(sz)));
            };
        }

        // the window's size until layout gives one (so the cover is whole from its first frame)
        private static Size StartSize()
        {
            var w = Application.Current != null ? Application.Current.MainWindow : null;
            if (w != null && w.ActualWidth > 0) return new Size(w.ActualWidth, w.ActualHeight);
            return new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        private void Arrange(Size size)
        {
            if (root == null) return;
            root.Measure(size);
            root.Arrange(new Rect(size));
        }

        protected override int VisualChildrenCount { get { return 1; } }
        protected override Visual GetVisualChild(int index) { return host; }

        public void FadeOut(int ms, int delayMs = 0)
        {
            var d = thread;
            if (fading) return;
            fading = true;
            if (d == null)
            {
                // the fade's thread isn't up yet (a busy start with a big library): waited for, a little at a time (the cover
                // used to be dropped at once, and the intro's backdrop snapped in instead of fading)
                if (++lateWaits <= 80)
                {
                    if (lateWaits == 1) HomeNav.Log("threaded fade: thread late, waiting");
                    fading = false;
                    var again = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                    again.Tick += (s2, e2) => { again.Stop(); FadeOut(ms, delayMs); };
                    again.Start();
                    return;
                }
                HomeNav.Log("threaded fade: thread never came, cover dropped");
                Remove();
                return;
            }
            d.BeginInvoke(new Action(() =>
            {
                HomeNav.Log("threaded fade: fading out over " + ms + " ms");
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                fade.Completed += (s, e) => Dispatcher.BeginInvoke(new Action(Remove));
                root.BeginAnimation(UIElement.OpacityProperty, fade);
            }));
        }

        // gone at once (no fade)
        public void Remove()
        {
            var parent = VisualTreeHelper.GetParent(this) as Panel;
            if (parent != null) parent.Children.Remove(this);
            var d = thread;
            thread = null;
            if (d != null) d.BeginInvokeShutdown(DispatcherPriority.Normal);
        }
    }
}
