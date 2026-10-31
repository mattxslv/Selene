using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Selene
{
        // the bar under a setting whose work runs: across the words' width to the control, filled as far as it has got, the
        // percentage at its end, a soft light sweeping along it (so it reads as going, not stuck) while it's on screen
        internal class WorkBar
        {
            public readonly DockPanel Root = new DockPanel { Margin = new Thickness(0, 9, 0, 2), Visibility = Visibility.Collapsed, LastChildFill = true };
            private readonly Grid bar;
            private readonly Border fill;
            private readonly Border light;   // the band of light gliding along the fill
            private readonly TextBlock percent;
            private readonly TranslateTransform slide = new TranslateTransform(-1.2, 0);
            private bool running;
            private double share = -1;   // where it stands (the fill glides there)
            private int shownPercent = -1;

            public WorkBar()
            {
                // (the figure's box sits lower than its digits: lifted a little, so the digits centre on the bar)
                percent = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("GlyphColor"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, MinWidth = 36, Margin = new Thickness(12, 0, 0, 3) };
                DockPanel.SetDock(percent, Dock.Right);
                Root.Children.Add(percent);
                bar = new Grid { Height = 6, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Ink.Brush("SeleneInkColor", 0.14) });
                fill = new Border { CornerRadius = new CornerRadius(3), Background = Ink.Brush("GlyphColor"), Width = 6, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
                // the light: a band gliding along the filled part, wide and strong, in the colour that stands out from the fill (white on
                // a dark fill, dark on a light one: white on a light theme's cream fill was invisible)
                light = new Border
                {
                    Opacity = 0.8, IsHitTestVisible = false,
                    OpacityMask = new LinearGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.05), new GradientStop(Color.FromArgb(0xFF, 255, 255, 255), 0.5), new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.95)
                    }, new Point(0, 0.5), new Point(1, 0.5)) { RelativeTransform = slide }
                };
                Tone();
                fill.Child = light;
                bar.Children.Add(fill);
                Root.Children.Add(bar);
                bar.SizeChanged += (s, e) => Place(false);   // its width known (or changed): the fill set straight there
                Root.Loaded += (s, e) => { if (Root.Visibility == Visibility.Visible) Start(); };
                Root.Unloaded += (s, e) => Stop();   // (nothing ticking for a row gone)
                Root.IsVisibleChanged += (s, e) => { if (Root.IsVisible) Start(); else Stop(); };
            }

            private void Tone()
            {
                var c = Ink.Of("GlyphColor");
                double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
                light.Background = luminance > 0.5 ? Brushes.Black : Brushes.White;
                light.Opacity = luminance > 0.5 ? 0.45 : 0.8;
            }

            // how far: 0..1 shows it so (the fill gliding on to it, the figure counting to it), below 0 takes it away
            public void Set(double next)
            {
                if (double.IsNaN(next) || double.IsInfinity(next)) next = -1;
                if (next < 0) { Root.Visibility = Visibility.Collapsed; Stop(); share = -1; return; }
                next = Math.Max(0, Math.Min(1, next));
                bool first = share < 0 || Root.Visibility != Visibility.Visible;
                share = next;
                Root.Visibility = Visibility.Visible;
                Tone();   // (the theme may have changed)
                Place(!first);
                Count((int)Math.Round(share * 100), !first);
                if (next >= 1) { Count(100, false); Stop(); }
                else if (Root.IsVisible) Start();
            }

            public void Indeterminate()
            {
                Set(0.35);
                if (counter != null) counter.Stop();
                percent.Text = "—";
                shownPercent = -1;
            }

            public void Suspend() { Stop(); }

            // the fill to where it stands: gliding there (eased, 600 ms), or set at once (first shown, or the bar resized)
            private void Place(bool glide)
            {
                if (share < 0 || bar.ActualWidth < 1) return;
                double to = Math.Max(6, Math.Round(bar.ActualWidth * share));
                if (!glide) { fill.BeginAnimation(FrameworkElement.WidthProperty, null); fill.Width = to; return; }
                if (Math.Abs(to - fill.Width) < 0.5) return;
                var move = new DoubleAnimation(to, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                move.Completed += (s, e) => { fill.BeginAnimation(FrameworkElement.WidthProperty, null); fill.Width = to; };   // settled: the next glide starts from here
                fill.BeginAnimation(FrameworkElement.WidthProperty, move);
            }

            // the figure counts to the new one (a step every few frames) rather than jumping
            private System.Windows.Threading.DispatcherTimer counter;
            private void Count(int to, bool glide)
            {
                if (!glide || shownPercent < 0) { if (counter != null) counter.Stop(); shownPercent = to; percent.Text = to + "%"; return; }
                if (counter == null)
                {
                    counter = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                    counter.Tick += (s, e) =>
                    {
                        int goal = (int)Math.Round(Math.Max(0, share) * 100);
                        if (shownPercent == goal) { counter.Stop(); return; }
                        shownPercent += Math.Sign(goal - shownPercent);
                        percent.Text = shownPercent + "%";
                    };
                }
                if (to != shownPercent) counter.Start();
            }

            private void Start()
            {
                if (running || !Root.IsVisible || share >= 1) return;
                running = true;
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1.2, 1.2, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            }

            private void Stop()
            {
                running = false;
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                if (counter != null) counter.Stop();
            }
        }

}
