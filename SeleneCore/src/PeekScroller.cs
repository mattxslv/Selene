using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Selene
{
    // A list that scrolls shows there's more: the rows past the edge peek in, blurred and fading into the dark (the bottom
    // when more is below, the top once it has scrolled), as the Store's "More from" row does. Over the band the list's own
    // rows are masked out and a blurred copy of them shows in their place.
    public static class PeekScroller
    {
        public const double Band = 110;

        public static Grid Wrap(ScrollViewer scroller, FrameworkElement content)
        {
            var host = new Grid { ClipToBounds = true };
            host.Children.Add(scroller);
            var bottom = MakeBand(content, VerticalAlignment.Bottom);
            var top = MakeBand(content, VerticalAlignment.Top);
            host.Children.Add(bottom.Item1);
            host.Children.Add(top.Item1);
            // the list's rows fade out under the bands (stops moved with the scroll state, never shown as a cut)
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            var mTop0 = new GradientStop(Colors.Black, 0); var mTop1 = new GradientStop(Colors.Black, 0);
            var mBot0 = new GradientStop(Colors.Black, 1); var mBot1 = new GradientStop(Colors.Black, 1);
            mask.GradientStops.Add(mTop0); mask.GradientStops.Add(mTop1); mask.GradientStops.Add(mBot0); mask.GradientStops.Add(mBot1);
            scroller.OpacityMask = mask;
            Action update = () =>
            {
                double h = scroller.ViewportHeight, w = scroller.ViewportWidth;
                if (h < 1 || w < 1) return;
                bool more = scroller.ScrollableHeight - scroller.VerticalOffset > 2;
                bool above = scroller.VerticalOffset > 2;
                double fb = Band / h, ft = Band * 0.6 / h;
                mTop0.Color = above ? Color.FromArgb(0, 0, 0, 0) : Colors.Black;
                mTop1.Offset = above ? ft : 0;
                mBot1.Color = more ? Color.FromArgb(0, 0, 0, 0) : Colors.Black;
                mBot0.Offset = more ? 1 - fb : 1;
                // the blurred copies: the slice of the list that sits under each band
                bottom.Item1.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
                top.Item1.Visibility = above ? Visibility.Visible : Visibility.Collapsed;
                bottom.Item1.Width = top.Item1.Width = w;
                bottom.Item2.Viewbox = new Rect(0, scroller.VerticalOffset + h - Band, w, Band);
                top.Item2.Viewbox = new Rect(0, scroller.VerticalOffset, w, Band * 0.6);
            };
            scroller.ScrollChanged += (s, e) => update();
            scroller.SizeChanged += (s, e) => update();
            content.SizeChanged += (s, e) => update();
            scroller.Loaded += (s, e) => update();
            return host;
        }

        // a band: the list's rows there, blurred, fading into the dark towards the edge
        private static Tuple<Grid, VisualBrush> MakeBand(FrameworkElement content, VerticalAlignment edge)
        {
            bool bottomEdge = edge == VerticalAlignment.Bottom;
            double height = bottomEdge ? Band : Band * 0.6;
            var brush = new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, TileMode = TileMode.None };
            var copy = new Rectangle { Fill = brush, Effect = new BlurEffect { Radius = 9, RenderingBias = RenderingBias.Performance } };
            // fading: whole where it meets the rows, gone at the edge
            var fade = new LinearGradientBrush { StartPoint = new Point(0, bottomEdge ? 0 : 1), EndPoint = new Point(0, bottomEdge ? 1 : 0) };
            fade.GradientStops.Add(new GradientStop(Colors.Black, 0));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 0, 0, 0), 0.6));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
            var band = new Grid { Height = height, VerticalAlignment = edge, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, OpacityMask = fade, Visibility = Visibility.Collapsed };
            band.Children.Add(copy);
            // and darkening towards the edge, into the screen's shade
            var dark = new Border();
            var shade = new LinearGradientBrush { StartPoint = new Point(0, bottomEdge ? 0 : 1), EndPoint = new Point(0, bottomEdge ? 1 : 0) };
            shade.GradientStops.Add(Ink.Stop("SeleneScrimColor", 0, 0));
            shade.GradientStops.Add(Ink.Stop("SeleneScrimColor", 0.85, 1));
            dark.Background = shade;
            band.Children.Add(dark);
            return Tuple.Create(band, brush);
        }
    }
}
