using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Selene
{
    // A piece of the screen drawn and animated on a thread of its own (a HostVisual with its own dispatcher, as the login's
    // cover fade is): whatever the main thread is busy with (a game starting, the home being rebuilt), this keeps moving.
    // `build` runs on that thread and must only use what can cross threads: frozen bitmaps and brushes, plain colors, numbers.
    // Only for short scenes (a start-up cover, a game's loading screen): while a second thread animates, WPF draws the whole
    // window at half the screen's rate (30 fps on a 60 Hz TV, measured). Something that moves for good passes inline: true,
    // and is then built and animated on the main thread like any other element.
    public class OffThread : FrameworkElement
    {
        private readonly HostVisual host = new HostVisual();
        private readonly bool inline;
        private Dispatcher thread;
        private FrameworkElement root;
        private Size size;

        public Size CurrentSize { get { return size; } }

        // resized (the window changed): called on the drawing thread with the element and its new size
        public Action<FrameworkElement, Size> Resized;

        public OffThread(Func<Size, FrameworkElement> build, string name = "Selene stage", bool inline = false)
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
            size = StartSize();
            this.inline = inline;
            if (inline)
            {
                root = build(size);
                AddVisualChild(root);
                SizeChanged += (s, e) =>
                {
                    if (root == null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
                    size = e.NewSize;
                    if (Resized != null) Resized(root, size);
                };
                return;
            }
            AddVisualChild(host);
            var start = size;
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    var target = new VisualTarget(host);
                    root = build(start);
                    Arrange(start);
                    target.RootVisual = root;
                    thread = Dispatcher.CurrentDispatcher;
                    thread.UnhandledException += (s, e) => { HomeNav.Log("off-thread stage: " + e.Exception.Message); e.Handled = true; };
                    ready.Set();
                    Dispatcher.Run();
                    target.Dispose();
                }
                catch (Exception ex) { ready.Set(); HomeNav.Log("off-thread stage: " + ex.Message); }
            }) { IsBackground = true, Name = name };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.WaitOne(2000);
            SizeChanged += (s, e) =>
            {
                var d = thread;
                var sz = e.NewSize;
                if (d == null || sz.Width <= 0 || sz.Height <= 0) return;
                size = sz;
                d.BeginInvoke(new Action(() => { Arrange(sz); if (Resized != null && root != null) Resized(root, sz); }));
            };
        }

        // something to do with the element, on its thread
        public void Run(Action<FrameworkElement> act)
        {
            if (inline) { if (root != null) act(root); return; }
            var d = thread;
            if (d == null) return;
            d.BeginInvoke(new Action(() => { if (root != null) act(root); }));
        }

        // the thread ends once the element is done with (a loading screen that went away)
        public void Stop()
        {
            if (inline)
            {
                // its animations go with it (nothing left ticking for an element no longer shown)
                if (root != null) { RemoveVisualChild(root); root = null; }
                return;
            }
            var d = thread;
            thread = null;
            if (d != null) d.BeginInvokeShutdown(DispatcherPriority.Background);
        }

        // the window's size until layout gives one
        private static Size StartSize()
        {
            var w = Application.Current != null ? Application.Current.MainWindow : null;
            if (w != null && w.ActualWidth > 0) return new Size(w.ActualWidth, w.ActualHeight);
            return new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        private void Arrange(Size sz)
        {
            if (root == null) return;
            root.Measure(sz);
            root.Arrange(new Rect(sz));
        }

        protected override Size MeasureOverride(Size available)
        {
            if (inline && root != null) root.Measure(available);
            return new Size();
        }

        protected override Size ArrangeOverride(Size final)
        {
            if (inline && root != null) root.Arrange(new Rect(final));
            return final;
        }

        protected override int VisualChildrenCount { get { return inline ? (root != null ? 1 : 0) : 1; } }
        protected override Visual GetVisualChild(int index) { return inline ? (Visual)root : host; }
    }
}
