using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Selene
{
    // A setting's choices as a list that opens over its value (A on a choice row): the current one under the value, Up / Down
    // go through them, A picks, B closes without a change. Settings screens hand it their buttons while it's open.
    public class ChoiceList
    {
        private const double RowHeight = 46;
        private readonly Canvas layer;
        private readonly Border panel;
        private readonly List<Border> rows = new List<Border>();
        private readonly Action<int> chosen;
        private int index;
        public bool IsOpen { get; private set; }

        private ChoiceList(Panel host, FrameworkElement anchor, IList<string> names, int current, Action<int> chosen)
        {
            this.chosen = chosen;
            index = Math.Max(0, Math.Min(names.Count - 1, current));
            var list = new StackPanel();
            for (int i = 0; i < names.Count; i++)
            {
                var line = new DockPanel { LastChildFill = true };
                var tick = new TextBlock { Text = i == current ? Ui.Icon(0xE5CA /* check */) : "", FontFamily = Ui.Icons, FontSize = 20, Width = 26, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                DockPanel.SetDock(tick, Dock.Right);
                line.Children.Add(tick);
                line.Children.Add(new TextBlock { Text = names[i], FontSize = 19, FontFamily = Ui.Body, VerticalAlignment = VerticalAlignment.Center });
                var row = new Border { Height = RowHeight, CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 0, 12, 2), Child = line };
                rows.Add(row);
                list.Children.Add(row);
            }
            panel = new Border
            {
                Child = list, MinWidth = Math.Max(220, anchor.ActualWidth + 40), Padding = new Thickness(6), CornerRadius = new CornerRadius(18),
                Background = Ink.Brush("SeleneGlassColor", 0.98), BorderBrush = Ink.Brush("SeleneInkColor", 0.14), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 36, Opacity = 0.55, Color = Colors.Black },
                RenderTransformOrigin = new Point(1, 0.5), Opacity = 0
            };
            layer = new Canvas { Background = Brushes.Transparent };
            Grid.SetRowSpan(layer, 99);
            Grid.SetColumnSpan(layer, 99);
            Panel.SetZIndex(layer, 500);
            layer.Children.Add(panel);
            host.Children.Add(layer);
            Look();
            // over the value: right edges lined up, the current choice level with it (kept on the screen)
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var at = anchor.TranslatePoint(new Point(0, 0), host);
            double width = panel.DesiredSize.Width, height = panel.DesiredSize.Height;
            double left = at.X + anchor.ActualWidth - width + 20;
            double top = at.Y + anchor.ActualHeight / 2 - 6 - RowHeight / 2 - index * RowHeight;
            double hostHeight = host.ActualHeight > 0 ? host.ActualHeight : 1080;
            top = Math.Max(40, Math.Min(hostHeight - height - 40, top));
            Canvas.SetLeft(panel, Math.Max(20, left));
            Canvas.SetTop(panel, top);
            var grow = new ScaleTransform(0.96, 0.96);
            panel.RenderTransform = grow;
            var ease = Ui.Out();
            panel.BeginAnimation(UIElement.OpacityProperty, Ui.Fast(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease }));
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, Ui.Fast(new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease }));
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, Ui.Fast(new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease }));
            IsOpen = true;
        }

        public static ChoiceList Open(Panel host, FrameworkElement anchor, IList<string> names, int current, Action<int> chosen)
        {
            Ui.Confirm();
            return new ChoiceList(host, anchor, names, current, chosen);
        }

        private void Look()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                bool on = i == index;
                rows[i].Background = on ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
                var line = (DockPanel)rows[i].Child;
                foreach (var t in line.Children)
                {
                    var tb = t as TextBlock;
                    if (tb != null) tb.Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.9);
                }
            }
        }

        // the screen's buttons while the list is open
        public void Handle(string b)
        {
            if (!IsOpen) return;
            if (b == "B") { Ui.Back(); Close(); return; }
            if (b == "A") { Ui.Key(); Close(); chosen(index); return; }
            int next = index + (b == "DPadDown" ? 1 : b == "DPadUp" ? -1 : 0);
            if (next == index || next < 0 || next >= rows.Count) return;
            index = next;
            Ui.Tick();
            Look();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            var fade = Ui.Fast(new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
            fade.Completed += (s, e) => { var p = layer.Parent as Panel; if (p != null) p.Children.Remove(layer); };
            panel.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    // a choice that opens the list: its value with a small "opens" arrow
    public static class ChoiceLook
    {
        public static Border Pill(out TextBlock value)
        {
            var pill = new StackPanel { Orientation = Orientation.Horizontal };
            value = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 110, TextAlignment = TextAlignment.Center };
            pill.Children.Add(value);
            pill.Children.Add(new TextBlock { Text = Ui.Icon(0xE5CF /* expand_more */), FontFamily = Ui.Icons, FontSize = 22, Foreground = Ink.Brush("SeleneInkColor", 0.6), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            return new Border { CornerRadius = new CornerRadius(17), Padding = new Thickness(14, 4, 10, 5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0), Background = Ink.Brush("SeleneInkColor", 0.12), Child = pill };
        }
    }

    // a number setting (text size, volume): a line with a knob along it and the value beside it; Left / Right move it
    public class SliderBar
    {
        private const double Length = 220;
        public readonly Border Root;
        public readonly TextBlock Value;
        private readonly Border fill, knob;
        private readonly TranslateTransform knobAt = new TranslateTransform();

        public SliderBar()
        {
            // (Width starts at 0: animating a Border's Width from "not set" throws, and an animation exception ends Playnite)
            fill = new Border { Width = 0, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = Ink.Brush("GlyphColor") };
            knob = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, Background = Ink.Brush("SeleneInkColor"), RenderTransform = knobAt, Margin = new Thickness(-10, 0, 0, 0) };
            var line = new Grid { Width = Length, Height = 24, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ink.Brush("SeleneInkColor", 0.2) });
            line.Children.Add(fill);
            line.Children.Add(knob);
            Value = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 56, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 18, 0) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Value);
            row.Children.Add(line);
            Root = new Border { Child = row, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 6, 0) };
        }

        public void Set(int index, int count, string label, bool animate)
        {
            Value.Text = label;
            double f = count > 1 ? (double)index / (count - 1) : 1;
            if (!animate) { fill.BeginAnimation(FrameworkElement.WidthProperty, null); fill.Width = Length * f; knobAt.BeginAnimation(TranslateTransform.XProperty, null); knobAt.X = Length * f; return; }
            var ms = TimeSpan.FromMilliseconds(160);
            fill.BeginAnimation(FrameworkElement.WidthProperty, Ui.Fast(new DoubleAnimation(Length * f, ms) { EasingFunction = Ui.Out() }));
            knobAt.BeginAnimation(TranslateTransform.XProperty, Ui.Fast(new DoubleAnimation(Length * f, ms) { EasingFunction = Ui.Out() }));
        }
    }
}
