// Playnite's message boxes (an add-on install, "Restart now?", errors, yes / no questions) dressed as Selene's own cards.
// The theme can only style the card, the title, the text and the buttons (theme\Views\MessageBox.xaml); Playnite lays them
// out itself, flush left like a desktop dialog. When one opens, this rebuilds its inside: an icon in a soft ring at the top,
// the title and text centred, an add-on's name, author and version set out as a little card of their own, the buttons
// centred, a glow of the theme's accent at the top, and the card rises in.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Selene
{
    public static class DialogLook
    {
        public static void Install()
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            // Playnite's windows raise a routed "activated" as they appear, before their first frame (Loaded comes after it): the
            // progress window is made invisible there, so its dark first frame never shows
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Playnite") continue;
                    var wb = asm.GetType("Playnite.Controls.WindowBase");
                    var field = wb != null ? wb.GetField("ActivatedRoutedEvent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) : null;
                    var evt = field != null ? field.GetValue(null) as RoutedEvent : null;
                    if (evt != null)
                        EventManager.RegisterClassHandler(wb, evt, new RoutedEventHandler((s, e) =>
                        {
                            var w = s as Window;
                            if (w != null && w.GetType().Name == "ProgressWindow") ProgressToast.Prehide(w);
                        }));
                    break;
                }
            }
            catch (Exception ex) { HomeNav.Log("dialog look: early hook failed: " + ex.Message); }
            var mw = Application.Current.MainWindow;
            if (mw != null) ProgressToast.WatchDimming(mw);
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            var w = sender as Window;
            if (w == null || !Features.On("dialogs")) return;
            if (w.GetType().Name == "ProgressWindow")
            {
                if (HomeNav.Quitting) { ProgressToast.HideForQuit(w); return; }   // Playnite's "Closing" screen: not shown at all
                try { if (!(Features.On("progresstoast") && ProgressToast.Make(w))) DressProgress(w); }
                catch (Exception ex) { HomeNav.Log("progress look: " + ex.Message); }
                return;
            }
            if (w.GetType().Name != "MessageBoxWindow") return;
            try { Dress(w); }
            catch (Exception ex) { HomeNav.Log("dialog look: " + ex.Message); }
        }

        private static void Dress(Window w)
        {
            var buttons = w.FindName("StackButtons") as StackPanel;
            var panel = buttons != null ? buttons.Parent as StackPanel : null;
            var card = panel != null ? panel.Parent as Border : null;
            if (card == null || panel.Children.Count < 2) return;
            var caption = panel.Children[0] as TextBlock;
            var message = panel.Children[1] as TextBlock;
            if (caption == null || message == null) return;

            string title = caption.Text ?? "", text = message.Text ?? "";
            HomeNav.Log("dialog look: \"" + title + "\"");

            // the card: fixed, roomy width; its padding moves inside so the accent glow can reach its edges
            card.Padding = new Thickness(0);
            card.CornerRadius = new CornerRadius(28);
            card.Width = 640;
            card.MinWidth = 0;
            card.MaxWidth = double.PositiveInfinity;
            card.BorderBrush = Ink.Brush("SeleneInkColor", 0.16);
            card.Background = Ink.Brush("SeleneGlassColor", 0.96);   // a touch see-through, like Selene's own dialogs
            card.Child = null;
            var inside = new Grid();
            // the shadow as on Selene's own dialogs, on a twin behind the card: an Effect on the card itself drew its words and
            // round edges soft and jagged
            card.Effect = null;
            var host = card.Parent as Panel;
            if (host != null)
            {
                var twin = Ui.ShadowOf(card, 60, 0.55);
                twin.HorizontalAlignment = card.HorizontalAlignment;
                twin.VerticalAlignment = card.VerticalAlignment;
                twin.Margin = card.Margin;
                Grid.SetRow(twin, Grid.GetRow(card)); Grid.SetColumn(twin, Grid.GetColumn(card));
                Grid.SetRowSpan(twin, Grid.GetRowSpan(card)); Grid.SetColumnSpan(twin, Grid.GetColumnSpan(card));
                twin.SetBinding(UIElement.OpacityProperty, new System.Windows.Data.Binding("Opacity") { Source = card });
                twin.SetBinding(UIElement.RenderTransformProperty, new System.Windows.Data.Binding("RenderTransform") { Source = card });
                twin.SetBinding(UIElement.RenderTransformOriginProperty, new System.Windows.Data.Binding("RenderTransformOrigin") { Source = card });
                host.Children.Insert(host.Children.IndexOf(card), twin);
            }
            else card.Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 60, Opacity = 0.55, Color = Colors.Black };
            card.UseLayoutRounding = true;
            panel.Margin = new Thickness(40, 40, 40, 36);
            inside.Children.Add(panel);
            card.Child = inside;
            TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);   // crisp at the screen's pixels
            TextOptions.SetTextRenderingMode(card, TextRenderingMode.ClearType);

            // the icon in a ring, for what kind of question it is
            panel.Children.Insert(0, IconRing(KindOf(title, text, buttons)));

            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.TextAlignment = TextAlignment.Center;
            caption.FontSize = 34;
            caption.FontFamily = Ui.Display;
            caption.FontWeight = FontWeights.SemiBold;
            caption.Margin = new Thickness(0, 22, 0, 10);
            caption.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;

            message.TextAlignment = TextAlignment.Center;
            message.HorizontalAlignment = HorizontalAlignment.Center;
            message.MaxWidth = 560;
            message.FontSize = 19;
            message.FontFamily = Ui.Body;
            message.Foreground = Ink.Brush("SeleneInkColor", 0.75);
            message.Opacity = 1;

            // "Do you want to install a new extension?" + a block of short lines (name, author, version): the block becomes a
            // card of its own with the name standing out
            string lead, block;
            if (SplitDetails(text, out lead, out block))
            {
                message.Text = lead;
                panel.Children.Insert(panel.Children.IndexOf(message) + 1, DetailsCard(block));
            }

            if (KindOf(title, text, buttons) == Kind.Addon) WatchInstallYes(w);
            if (title.ToLowerInvariant().Contains("restart") && text.ToLowerInvariant().Contains("install")) RestartAdvice(w, panel, buttons);

            buttons.HorizontalAlignment = HorizontalAlignment.Center;
            buttons.Margin = new Thickness(0, 34, 0, 0);
            // the pills' right margin (for a left-aligned row) would push a centred row off centre
            foreach (var b in buttons.Children.OfType<FrameworkElement>()) b.Margin = new Thickness(7, 0, 7, 0);

            try { if (w.AllowsTransparency) w.Background = Ink.Brush("SeleneScrimColor", 0.62); } catch { }

            // in: the card rises and settles, a soft sound with it
            var scale = new ScaleTransform(0.96, 0.96);
            var move = new TranslateTransform(0, 22);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(move);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = group;
            card.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var t = TimeSpan.FromMilliseconds(260);
            var fadeIn = new DoubleAnimation(1, t) { EasingFunction = ease };
            // settled: the animations come off and the transform goes, so the text is drawn sharp again (text under a held
            // animated transform stays in WPF's blurrier animation mode)
            fadeIn.Completed += (s, e) =>
            {
                card.BeginAnimation(UIElement.OpacityProperty, null);
                card.Opacity = 1;
                card.RenderTransform = Transform.Identity;
            };
            card.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, t) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, t) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, t) { EasingFunction = ease });
            // the dimmed screen behind fades in with it
            w.Opacity = 0;
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            w.Opacity = 1;
            if (Features.On("sounds")) Ui.Play("OpenPanel.wav", 0.6);

            // out: whatever answers it (a button, B, Escape), the card sinks and fades with the dim behind it, then it closes
            bool closingDone = false, closing = false;
            w.Closing += (s, e) =>
            {
                if (closingDone || closing) { if (closing && !closingDone) e.Cancel = true; return; }
                e.Cancel = true;
                closing = true;
                var answer = w.DialogResult;   // set already when an answer closes it; kept for the real close
                var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
                var outT = TimeSpan.FromMilliseconds(170);
                var sink = new ScaleTransform(1, 1);
                card.RenderTransformOrigin = new Point(0.5, 0.5);
                card.RenderTransform = sink;
                sink.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, outT) { EasingFunction = easeIn });
                sink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, outT) { EasingFunction = easeIn });
                var fade = new DoubleAnimation(0, outT) { EasingFunction = easeIn };
                fade.Completed += (s2, e2) =>
                {
                    closingDone = true;
                    try { if (answer.HasValue) w.DialogResult = answer; else w.Close(); }
                    catch { try { w.Close(); } catch { } }
                };
                w.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            w.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape && Back(w)) e.Handled = true; };
            open = w;
        }

        // Playnite's progress window (downloading an add-on, updating the library): Selene's moving background behind the
        // breathing logo instead of Playnite's plain gradient, fading in
        internal static void DressProgress(Window w)
        {
            var grid = w.FindName("GridMain") as Grid;
            if (grid == null) return;
            var baseFill = new Border();
            baseFill.SetResourceReference(Border.BackgroundProperty, "SeleneBaseBrush");
            Grid.SetRowSpan(baseFill, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(baseFill, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.Children.Insert(0, baseFill);
            var backdrop = LoginScene.BackdropFor(w);
            Grid.SetRowSpan(backdrop, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(backdrop, Math.Max(1, grid.ColumnDefinitions.Count));
            backdrop.Margin = new Thickness(0);
            grid.Children.Insert(1, backdrop);
            backdrop.Opacity = 0;
            backdrop.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            HomeNav.Log("progress look: Selene background");
        }

        // the dialog up now (one at a time), for B from the pad
        private static Window open;

        // B on a pad: the open dialog's safe answer
        public static bool Back()
        {
            var w = open;
            if (w == null || !w.IsVisible) return false;
            return Back(w);
        }

        public static bool IsOpen { get { var w = open; return w != null && w.IsVisible; } }

        // A pad press while a dialog is up that doesn't have the focus (one raised in the middle of a game start, under Selene's
        // loading screen): it is the dialog's, not the home behind it (A started the game again). The dialog is brought forward;
        // A answers it with its default button, the other buttons do nothing. False when the dialog has the focus itself (its
        // own handling takes the press).
        public static bool Take(string button)
        {
            var w = open;
            if (w == null || !w.IsVisible || w.IsActive) return false;
            try { w.Activate(); w.Focus(); } catch { }
            if (button != "A") return true;
            System.Windows.Controls.Primitives.ButtonBase target = null;
            var focused = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
            for (var d = focused; d != null && target == null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                target = d as System.Windows.Controls.Primitives.ButtonBase;
            if (target == null || !target.IsVisible)
                foreach (var name in new[] { "ButtonOK", "ButtonYes" })
                {
                    var b = w.FindName(name) as System.Windows.Controls.Primitives.ButtonBase;
                    if (b != null && b.Visibility == Visibility.Visible && b.IsEnabled) { target = b; break; }
                }
            if (target == null) return true;
            HomeNav.Log("dialog look: A -> " + (target.Name ?? "button") + " (the dialog had no focus)");
            Ui.Confirm();
            target.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, target));
            return true;
        }

        // the safe answer: No, else Cancel, else OK (a note with only OK just goes)
        private static bool Back(Window w)
        {
            foreach (var name in new[] { "ButtonNo", "ButtonCancel", "ButtonOK" })
            {
                var b = w.FindName(name) as System.Windows.Controls.Primitives.ButtonBase;
                if (b == null || b.Visibility != Visibility.Visible || !b.IsEnabled) continue;
                HomeNav.Log("dialog look: B -> " + name);
                Ui.Back();
                b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));
                return true;
            }
            return false;
        }

        // Yes in an add-on's install question: that add-on goes in on the next restart
        private static void WatchInstallYes(Window w)
        {
            var asked = SetupCheck.LastAsked;
            var yes = w.FindName("ButtonYes") as System.Windows.Controls.Primitives.ButtonBase;
            if (yes != null && asked != null) yes.Click += (s, e) => SetupCheck.Accept(asked);
            SetupCheck.LastAsked = null;   // one question per request (an install from elsewhere isn't this one)
        }

        // "Restart Playnite?" after an install: say how many add-ons are left, so people install them all and restart once
        private static void RestartAdvice(Window w, StackPanel panel, StackPanel buttons)
        {
            int left = 0;
            try { if (HomeNav.Api != null) left = SetupCheck.LeftToInstall(HomeNav.Api).Count; } catch { }
            var yes = w.FindName("ButtonYes") as ContentControl;
            var no = w.FindName("ButtonNo") as ContentControl;
            string note;
            Control first;
            if (left > 0)
            {
                note = left + (left == 1 ? " more add-on" : " more add-ons") + " to install. Keep installing and restart once at the end; they all start working together.";
                if (no != null) no.Content = "Keep installing";
                first = no;
            }
            else
            {
                note = "That's every add-on Selene works with. Restart now and they all start working.";
                if (no != null) no.Content = "Later";
                first = yes;
            }
            if (yes != null) yes.Content = "Restart now";
            if (first != null) w.Dispatcher.BeginInvoke(new Action(() => first.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            panel.Children.Insert(panel.Children.IndexOf(buttons), new Border
            {
                Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(24, 14, 24, 14), CornerRadius = new CornerRadius(16),
                Background = Ink.Brush("GlyphColor", 0.10), BorderBrush = Ink.Brush("GlyphColor", 0.28), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 560,
                Child = new TextBlock
                {
                    Text = note, FontFamily = Ui.Body, FontSize = 18, LineHeight = 26, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                    Foreground = Ink.Brush("SeleneInkColor", 0.9)
                }
            });
            HomeNav.Log("dialog look: restart advice, " + left + " left");
        }

        private enum Kind { Addon, Restart, Problem, Question, Note }

        private static Kind KindOf(string title, string text, StackPanel buttons)
        {
            var all = (title + " " + text).ToLowerInvariant();
            if (all.Contains("extension") || all.Contains("add-on") || all.Contains("addon") || all.Contains("theme")) return Kind.Addon;
            if (all.Contains("restart")) return Kind.Restart;
            if (all.Contains("error") || all.Contains("fail") || all.Contains("couldn't") || all.Contains("cannot") || all.Contains("can't")) return Kind.Problem;
            // a question: more than one button showing
            if (buttons.Children.OfType<UIElement>().Count(b => b.Visibility == Visibility.Visible) > 1) return Kind.Question;
            return Kind.Note;
        }

        private static FrameworkElement IconRing(Kind kind)
        {
            int code;
            const string tone = "GlyphColor";   // the theme's accent
            switch (kind)
            {
                case Kind.Addon: code = 0xE87B; break;     // extension
                case Kind.Restart: code = 0xE5D5; break;   // refresh
                case Kind.Problem: code = 0xE002; break;   // warning
                case Kind.Question: code = 0xE887; break;  // help
                default: code = 0xE88E; break;             // info
            }
            var badge = new Border
            {
                Width = 84, Height = 84, CornerRadius = new CornerRadius(42), HorizontalAlignment = HorizontalAlignment.Center,
                Background = Ink.Brush(tone, 0.19),
                Child = new TextBlock
                {
                    Text = Ui.Icon(code), FontFamily = Ui.Icons, FontSize = 38, Foreground = Ink.Brush(tone),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            return badge;
        }

        // text = a sentence, an empty line, then 2-4 short lines (an add-on's name, author, version)
        private static bool SplitDetails(string text, out string lead, out string block)
        {
            lead = text; block = null;
            var cut = text.Replace("\r\n", "\n").LastIndexOf("\n\n", StringComparison.Ordinal);
            if (cut <= 0) return false;
            var norm = text.Replace("\r\n", "\n");
            var lines = norm.Substring(cut + 2).Split('\n').Select(l => l.Trim()).Where(l => l != "").ToArray();
            if (lines.Length < 2 || lines.Length > 4 || lines.Any(l => l.Length > 60)) return false;
            lead = norm.Substring(0, cut).Trim();
            block = string.Join("\n", lines);
            return true;
        }

        private static FrameworkElement DetailsCard(string block)
        {
            var lines = block.Split('\n');
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = lines[0], FontFamily = Ui.Display, FontSize = 24, FontWeight = FontWeights.SemiBold,
                Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", lines.Skip(1)), FontFamily = Ui.Body, FontSize = 17, Margin = new Thickness(0, 6, 0, 0),
                Foreground = Ink.Brush("SeleneInkColor", 0.6), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap
            });
            return new Border
            {
                Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(28, 18, 28, 18), CornerRadius = new CornerRadius(18),
                Background = Ink.Brush("SeleneInkColor", 0.06), BorderBrush = Ink.Brush("SeleneInkColor", 0.10), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 360,
                Child = stack
            };
        }
    }
}
