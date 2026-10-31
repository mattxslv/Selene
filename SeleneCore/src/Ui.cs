using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Selene
{
    // Building blocks of Selene's own screens (login, setup check, profile steps): theme-colored text and tiles, the
    // focus look, motion and sounds, so every screen moves and sounds the same (see docs/guide/building.md).
    public static class Ui
    {
        public static Button MouseButton(string label, Action action)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var button = new Button
            {
                Content = new TextBlock { Text = label, FontSize = 17, FontFamily = Body, Foreground = Ink.Brush("SeleneInkColor") },
                Background = Ink.Brush("SeleneGlassColor", 0.95), Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(3), MinWidth = 46, Focusable = false,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = border }
            };
            button.MouseEnter += (s, e) => button.Background = Ink.Brush("SeleneFocusColor", 0.35);
            button.MouseLeave += (s, e) => button.Background = Ink.Brush("SeleneGlassColor", 0.95);
            button.Click += (s, e) => action();
            return button;
        }
        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily Body = new FontFamily("Segoe UI");
        public static readonly FontFamily Glyphs = new FontFamily("Segoe MDL2 Assets");
        private static FontFamily icons;


        // solid icons: Google's Material Icons (theme\Fonts\MaterialIcons-Regular.ttf); Segoe's thin ones when it's missing
        public static FontFamily Icons
        {
            get
            {
                if (icons != null) return icons;
                var folder = SelenePaths.ThemeFile("Fonts");
                icons = System.IO.File.Exists(System.IO.Path.Combine(folder, "MaterialIcons-Regular.ttf"))
                    ? new FontFamily(new Uri(folder.TrimEnd('\\') + "\\"), "./#Material Icons") : Glyphs;
                return icons;
            }
        }

        // the words everywhere Selene doesn't name a font: Lato, shipped in theme\Fonts (it used to be Playnite's font setting,
        // which is Trebuchet MS on a new install: Selene only looked right on a PC with Lato picked there)
        private static FontFamily text;
        public static FontFamily TextFont
        {
            get
            {
                if (text != null) return text;
                var folder = SelenePaths.ThemeFile("Fonts");
                text = System.IO.File.Exists(System.IO.Path.Combine(folder, "Lato-Regular.ttf"))
                    ? new FontFamily(new Uri(folder.TrimEnd('\\') + "\\"), "./#Lato, Segoe UI") : new FontFamily("Lato, Segoe UI");
                return text;
            }
        }

        // a Material Icons character from its code point (the font's codepoints list)
        public static string Icon(int code) { return ((char)code).ToString(); }

        // An icon centred by its ink, not its line box: a text box centred in a circle put every icon high or low (a font's line
        // box has room above and below the glyph, and a different amount per font), and each circle got its own nudge. The glyph's
        // outline, moved so its own bounds start at 0,0, centres exactly. The text box, nudged, when the outline can't be had.
        // byMass: centred on the shape's weight rather than its box, for a lopsided one (the puzzle piece: its knobs stick out of
        // one side and the top, and centred by the box its body sat off to a corner)
        public static FrameworkElement Glyph(string text, FontFamily font, double size, Brush fill, bool byMass = false)
        {
            try
            {
                var typeface = new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
                var geo = ft.BuildGeometry(new Point(0, 0));
                var b = geo.Bounds;
                if (b.IsEmpty || b.Width < 1 || b.Height < 1) throw new InvalidOperationException("no outline");
                double dx = 0, dy = 0;
                if (byMass)
                {
                    // the filled area's middle, found on a grid of points over the box
                    double sx = 0, sy = 0; int n = 0;
                    for (int iy = 0; iy < 32; iy++)
                        for (int ix = 0; ix < 32; ix++)
                        {
                            var pt = new Point(b.X + (ix + 0.5) / 32 * b.Width, b.Y + (iy + 0.5) / 32 * b.Height);
                            if (geo.FillContains(pt)) { sx += pt.X; sy += pt.Y; n++; }
                        }
                    if (n > 0) { dx = sx / n - (b.X + b.Width / 2); dy = sy / n - (b.Y + b.Height / 2); }
                }
                geo.Transform = new TranslateTransform(-b.X, -b.Y);
                geo.Freeze();
                var path = new System.Windows.Shapes.Path { Data = geo, Fill = fill, Width = b.Width, Height = b.Height, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                // (the shape moved so its weight sits at the centre: a margin twice the offset on the far side)
                if (dx != 0 || dy != 0) path.Margin = new Thickness(Math.Max(0, -2 * dx), Math.Max(0, -2 * dy), Math.Max(0, 2 * dx), Math.Max(0, 2 * dy));
                return path;
            }
            catch
            {
                return new TextBlock { Text = text, FontFamily = font, FontSize = size, Foreground = fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, size * 0.12) };
            }
        }

        // ---------------- text ----------------
        public static TextBlock Text(string text, double size, double alpha = 1, FontFamily font = null)
        {
            return new TextBlock
            {
                Text = text, FontSize = size, FontFamily = font ?? Body, Foreground = Ink.Brush("SeleneInkColor", alpha),
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        public static TextBlock Title(string text) { return Text(text, 44, 1, Display); }

        public static TextBlock Sub(string text)
        {
            var t = Text(text, 21, 0.7);
            t.Margin = new Thickness(0, 10, 0, 0);
            return t;
        }

        // (A) Next style hint for the bottom corners
        // ---------------- which controller's buttons the hints name ----------------
        // Playnite's Fullscreen setting "Button prompts" (Xbox or PlayStation): Selene's hints follow it. The theme's own hints
        // read the SelenePrompt* strings; code-built ones call PromptLabel.
        private static bool? playStation;
        public static bool PlayStationPrompts
        {
            get
            {
                if (playStation == null) RefreshPrompts();
                return playStation == true;
            }
        }

        public static void RefreshPrompts()
        {
            bool ps = false;
            try
            {
                var appType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Playnite.PlayniteApplication", false)).FirstOrDefault(t => t != null);
                var current = appType != null ? appType.GetProperty("Current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) : null;
                var app = current != null ? current.GetValue(null, null) : null;
                var settingsProp = app != null ? app.GetType().GetProperty("AppSettings") : null;
                var appSettings = settingsProp != null ? settingsProp.GetValue(app, null) : null;
                var fsProp = appSettings != null ? appSettings.GetType().GetProperty("Fullscreen") : null;
                var fs = fsProp != null ? fsProp.GetValue(appSettings, null) : null;
                var bp = fs != null ? fs.GetType().GetProperty("ButtonPrompts") : null;
                var value = bp != null ? bp.GetValue(fs, null) : null;
                ps = value != null && value.ToString().IndexOf("PlayStation", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception ex) { HomeNav.Log("prompts: " + ex.Message); }
            // Selene's own choice wins when it isn't "as Playnite's"
            var mine = Features.Value("prompts");
            if (mine == "xbox") ps = false; else if (mine == "playstation") ps = true;
            bool changed = playStation != ps;
            playStation = ps;
            try
            {
                var res = Application.Current.Resources;
                foreach (var b in new[] { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "RS" }) res["SelenePrompt" + b] = PromptLabel(b);
            }
            catch { }
            InputHints.Refresh();
            if (changed) { HomeNav.Log("prompts: " + (ps ? "PlayStation" : "Xbox")); try { HomeHints.Update(); } catch { } }
        }

        public static void PublishPrompts()
        {
            var app = Application.Current; if (app == null) return;
            app.Resources["SeleneDetailsBadgeWidth"] = Features.On("detailsbadges") ? 32.0 : 48.0;
            // the Scroll hint: a controller's two-letter stick label fits a round 32 px badge; "Wheel" and "PgUp/Dn" are longer and widen it
            app.Resources["SeleneScrollBadgeWidth"] = Features.On("detailsbadges") && InputHints.Kind == InputKind.Controller ? 32.0 : 48.0;
            foreach (var button in new[] { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "RS", "Start", "View" }) app.Resources["SelenePrompt" + button] = PromptLabel(button);
            app.Resources["SeleneScrollPrompt"] = InputHints.Kind == InputKind.Controller ? PromptLabel("RS") : InputHints.Kind == InputKind.Mouse ? "Wheel" : "PgUp/Dn";
        }

        public static void LiveHint(FrameworkElement element, Action refresh)
        {
            bool attached = false;
            element.Loaded += (s, e) => { if (!attached) { attached = true; InputHints.Changed += refresh; } refresh(); };
            element.Unloaded += (s, e) => { if (attached) { attached = false; InputHints.Changed -= refresh; } };
            refresh();
        }

        public static string PromptLabel(string button)
        {
            var desktop = InputHints.Label(button); if (desktop != null) return desktop;
            if (!PlayStationPrompts) return button;
            switch (button)
            {
                case "A": return "\u2715";   // cross
                case "B": return "\u25CB";   // circle
                case "X": return "\u25A1";   // square
                case "Y": return "\u25B3";   // triangle
                case "LB": return "L1";
                case "RB": return "R1";
                case "LT": return "L2";
                case "RT": return "R2";
                case "RS": return "R3";
                default: return button;
            }
        }

        public static StackPanel Prompt(string button, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0) };
            var dot = new Grid { Width = 32, Height = 32 };
            LiveHint(dot, () =>
            {
                string shown = PromptLabel(button);
                dot.Width = Math.Max(32, shown.Length > 1 ? shown.Length * 8 + 16 : 32);
                dot.Children.Clear();
                dot.Children.Add(new Border { CornerRadius = new CornerRadius(InputHints.Kind == InputKind.Controller ? 16 : 7), Background = Ink.Brush("SeleneInkColor") });
                dot.Children.Add(Letter(shown, shown.Length > 1 ? 12 : 16, FontWeights.SemiBold, Ink.Brush("SeleneScrimColor")));
            });
            sp.Children.Add(dot);
            sp.Children.Add(new TextBlock { Text = label, FontSize = 21, FontFamily = Body, Foreground = Ink.Brush("SeleneInkColor", 0.9), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            sp.Background = Brushes.Transparent;
            sp.MouseLeftButtonUp += (s, e) => { if (LoginScene.IsActive) { LoginScene.MousePress(button); e.Handled = true; } };
            return sp;
        }

        // a button's letter centred on its drawn shape in a round badge (a TextBlock centres its line box, so capitals sat
        // off-centre, and nudging it was right for one letter and wrong for the next)
        public static FrameworkElement Letter(string text, double size, FontWeight weight, Brush brush)
        {
            var typeface = new Typeface(Body, FontStyles.Normal, weight, FontStretches.Normal);
            var ft = new FormattedText(text ?? "", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
            var geometry = ft.BuildGeometry(new Point(0, 0));
            var b = geometry.Bounds;
            if (b.IsEmpty) return new TextBlock { Text = text, FontSize = size, FontWeight = weight, FontFamily = Body, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            geometry.Transform = new TranslateTransform(-b.X, -b.Y);
            return new System.Windows.Shapes.Path
            {
                Data = geometry, Fill = brush, Width = b.Width, Height = b.Height, Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = false
            };
        }

        // a badge letter's color (Letter's shape, or a plain TextBlock)
        public static void LetterBrush(UIElement letter, Brush brush)
        {
            var path = letter as System.Windows.Shapes.Shape;
            if (path != null) { path.Fill = brush; return; }
            var tb = letter as TextBlock;
            if (tb != null) tb.Foreground = brush;
        }

        // prompts row, bottom right (or left)
        public static StackPanel Prompts(bool right, params StackPanel[] items)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = right ? new Thickness(0, 0, 64, 48) : new Thickness(46, 0, 0, 48)
            };
            foreach (var i in items) row.Children.Add(i);
            row.Tag = PromptsTag;   // (found again to hide while the menu is over the screen)
            return row;
        }

        public const string PromptsTag = "selene-prompts";

        // a screen's button hints (the rows Prompts made), anywhere in it
        public static List<FrameworkElement> PromptRows(DependencyObject root)
        {
            var found = new List<FrameworkElement>();
            var fe = root as FrameworkElement;
            if (fe != null && Equals(fe.Tag, PromptsTag)) { found.Add(fe); return found; }
            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                var d = child as DependencyObject;
                if (d != null) found.AddRange(PromptRows(d));
            }
            return found;
        }

        // ---------------- motion ----------------
        public static IEasingFunction Out() { return new CubicEase { EasingMode = EasingMode.EaseOut }; }

        // at the screen's own speed (WPF caps an animation near 60 frames a second unless told otherwise; a monitor may run at
        // 120 / 144). Exactly the screen's rate: asking 144 on a 60 Hz TV ticked animations ~2x per refresh, unevenly (judder)
        public static T Fast<T>(T t) where T : Timeline { Timeline.SetDesiredFrameRate(t, FrameRate); return t; }
        public static int FrameRate { get { int r = ScreenRate(); return r >= 48 && r <= 360 ? r : 60; } }

        // Motion that runs on its own (the dust, the breathing light, the focus ring's glint) drifts a few pixels a second: it
        // doesn't need the screen's full rate, and every frame it asks for is one WPF has to compute on the UI thread (about 2 ms
        // for the whole backdrop, measured) and the GPU has to present. It runs at the largest even share of the refresh rate
        // that is 40 or less (30 on 60 Hz, 36 on 144, 40 on 120) so the frames stay evenly spaced. Presses and transitions keep
        // Fast's full rate. Switch: Advanced > Calm background motion ("calmmotion"); off, ambient motion asks for the full rate.
        public static int AmbientRate
        {
            get
            {
                int r = FrameRate;
                if (!Features.On("calmmotion")) return r;
                return Math.Max(15, r / Math.Max(1, (int)Math.Ceiling(r / 40.0)));
            }
        }
        public static T Ambient<T>(T t) where T : Timeline { Timeline.SetDesiredFrameRate(t, AmbientRate); return t; }

        public static void Fade(UIElement el, double to, int ms, int delay = 0, Action done = null)
        {
            var a = Fast(new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Out() });
            if (done != null) a.Completed += (s, e) => done();
            el.BeginAnimation(UIElement.OpacityProperty, a);
        }

        private static TranslateTransform Shift(UIElement el)
        {
            var t = el.RenderTransform as TranslateTransform;
            if (t == null || t.IsFrozen) { t = new TranslateTransform(); el.RenderTransform = t; }
            return t;
        }

        // appears: rises a few pixels while fading in
        public static void Rise(UIElement el, int delay = 0, double from = 18, int ms = 320)
        {
            el.Opacity = 0;
            Fade(el, 1, ms, delay);
            Shift(el).BeginAnimation(TranslateTransform.YProperty, Fast(new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Out() }));
        }

        // slides sideways while fading (screen changes)
        public static void Slide(UIElement el, double fromX, double toX, double fromOpacity, double toOpacity, int ms, Action done = null)
        {
            el.Opacity = fromOpacity;
            Fade(el, toOpacity, ms, 0, done);
            Shift(el).BeginAnimation(TranslateTransform.XProperty, Fast(new DoubleAnimation(fromX, toX, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out() }));
        }

        public static ScaleTransform Scaler(FrameworkElement el)
        {
            var s = el.RenderTransform as ScaleTransform;
            if (s == null || s.IsFrozen) { s = new ScaleTransform(1, 1); el.RenderTransform = s; el.RenderTransformOrigin = new Point(0.5, 0.5); }
            return s;
        }

        public static void ScaleTo(FrameworkElement el, double to, int ms = 200)
        {
            var s = Scaler(el);
            s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out() });
            s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out() });
        }

        // a value or state changed: a small pop
        public static void Pop(FrameworkElement el)
        {
            var s = Scaler(el);
            s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = Out() });
            s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = Out() });
        }

        // "can't do that": a short sideways shake
        // nothing moves at a list's end or on a refused press: the shake that did is gone (it read as a fault); the callers
        // still mark where a press goes nowhere
        public static void Shake(UIElement el) { }

        // ---------------- sounds ----------------
        private static readonly Dictionary<string, MediaPlayer> players = new Dictionary<string, MediaPlayer>();

        public static void Play(string file, double volume)
        {
            if (!Features.On("sounds")) return;
            var path = SelenePaths.ThemeAudio(file);
            if (!System.IO.File.Exists(path)) return;
            try
            {
                MediaPlayer mp;
                if (!players.TryGetValue(file, out mp)) { mp = new MediaPlayer(); mp.Open(new Uri(path)); players[file] = mp; }
                mp.Volume = HomeNav.Isolated ? 0 : Math.Max(0, Math.Min(1, volume * Features.Number("soundvolume") / 100.0));   // the Sound volume setting
                mp.Position = TimeSpan.Zero;
                mp.Play();
            }
            catch (Exception ex) { HomeNav.Log("sound " + file + ": " + ex.Message); }
        }

        // moving and confirming go through Playnite's own player (same sound, same volume as its views); the file is the fallback
        // a short note at the top of the screen (a glass pill): rises in, stays a few seconds, fades; one at a time
        // A card with a soft shadow that stays sharp. An Effect turns the element it sits on, and everything inside it, into a
        // bitmap: soft text and rough round edges. So the shadow goes on a twin of the card's shape behind it, and the card and
        // its words are drawn as they are, on whole pixels. Animate the returned Grid (its Rise, fade or scale carries both).
        public static Grid Lifted(Border card, double blur, double opacity)
        {
            card.Effect = null;
            var lifted = new Grid { HorizontalAlignment = card.HorizontalAlignment, VerticalAlignment = card.VerticalAlignment, Margin = card.Margin, UseLayoutRounding = true, IsHitTestVisible = card.IsHitTestVisible };
            card.Margin = new Thickness(0);
            lifted.Children.Add(ShadowOf(card, blur, opacity));
            lifted.Children.Add(card);
            TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
            return lifted;
        }

        // the twin: the card's shape, size and fill, with the shadow (the card covers it)
        public static Border ShadowOf(Border card, double blur, double opacity)
        {
            var twin = new Border { CornerRadius = card.CornerRadius, Background = card.Background ?? Brushes.Black, IsHitTestVisible = false, Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = blur, Opacity = opacity, Color = Colors.Black } };
            twin.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = card });
            twin.SetBinding(FrameworkElement.HeightProperty, new System.Windows.Data.Binding("ActualHeight") { Source = card });
            twin.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("Visibility") { Source = card });
            return twin;
        }

        private static FrameworkElement notice;
        public static void Notice(string text, int ms = 5000)
        {
            try
            {
                var w = Application.Current != null ? Application.Current.MainWindow : null;
                var host = w != null ? HomeNav.FindByName(w, "PART_MainHost") as Grid : null;
                if (host == null) return;
                if (notice != null) { host.Children.Remove(notice); notice = null; }
                var pill = Lifted(new Border
                {
                    CornerRadius = new CornerRadius(22), Padding = new Thickness(26, 11, 26, 13), MaxWidth = 900,
                    Background = Ink.Brush("SeleneGlassColor", 0.94), BorderBrush = Ink.Brush("SeleneInkColor", 0.18), BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, LegacyTasks.BannerUp ? 112 : 26, 0, 0),   // (under the old-tasks banner)
                    Child = new TextBlock { Text = text, FontSize = 19, FontFamily = Body, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                    IsHitTestVisible = false
                }, 30, 0.45);
                Grid.SetRowSpan(pill, 99); Grid.SetColumnSpan(pill, 99);
                Panel.SetZIndex(pill, 9999);
                host.Children.Add(pill);
                notice = pill;
                Rise(pill, 0, 12, 260);
                NotifySound();
                var gone = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                gone.Tick += (s2, e2) =>
                {
                    gone.Stop();
                    if (notice != pill) return;
                    var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = Out() };
                    fade.Completed += (s3, e3) => { host.Children.Remove(pill); if (notice == pill) notice = null; };
                    pill.BeginAnimation(UIElement.OpacityProperty, fade);
                };
                gone.Start();
            }
            catch (Exception ex) { HomeNav.Log("notice: " + ex.Message); }
        }

        // frame times over the next `ms`, to the log: how smooth a move really was (count, average, worst gap)
        private static bool measuring;
        // Full refresh rate: WPF draws only when something animates, and its animations step at 60 fps; on a 120/144 Hz screen
        // 60 doesn't divide the refresh, so frames landed unevenly (a stutter). Listening to every frame makes it draw at the
        // screen's own rate. Only while Playnite's window is the active one (never behind a game). Switch: "fullrate".
        private static bool fullRateOn;
        private static void EveryFrame(object sender, EventArgs e) { }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        private static readonly uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        // Playnite is what's in front: any of its windows has the foreground (its hidden progress window takes the activation
        // while a notification shows: going by the main window's own "active", the full rate went off just then)
        internal static bool PlayniteInFront(Window main)
        {
            if (main == null || !main.IsVisible || main.WindowState == WindowState.Minimized) return false;
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return pid == ownPid;
        }

        private static Window fullRateWatched;
        public static void WatchFullRate(Window main)
        {
            if (main == null || ReferenceEquals(main, fullRateWatched)) return;
            fullRateWatched = main;
            main.Activated += (s, e) => { Poke(); FullRateChanged(); };
            main.Deactivated += (s, e) => FullRateChanged();
            main.StateChanged += (s, e) => FullRateChanged();
            main.IsVisibleChanged += (s, e) => FullRateChanged();
            var check = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            check.Tick += (s, e) => { FullRateChanged(); ScreenPresence.Evaluate(); };
            check.Start();
            System.Windows.Input.InputManager.Current.PreProcessInput += OnInput;
            Poke(15000);   // start-up: the logo and the first screens move at the full rate
            FullRateChanged();
        }

        // ---- active / quiet ----
        // A Rendering handler, even an empty one, keeps WPF's render loop running on every frame of the screen, also on a window
        // where nothing moves (measured with a probe: 0 render passes a second without one, one per frame with it). The two Selene
        // keeps (the full-refresh-rate one and the slow-frame watchdog) are on only while something is being pressed or just was;
        // quiet, the background motion runs at its own calm rate and nothing asks for more.
        private static DateTime activeUntil = DateTime.MinValue;
        public static bool RecentlyActive { get { return DateTime.Now < activeUntil; } }

        public static void Poke(int ms = 3500)
        {
            bool wasQuiet = !RecentlyActive;
            var until = DateTime.Now.AddMilliseconds(ms);
            if (until > activeUntil) activeUntil = until;
            if (wasQuiet && fullRateWatched != null) FullRateChanged();
        }

        private static void OnInput(object sender, System.Windows.Input.PreProcessInputEventArgs e)
        {
            var input = e.StagingItem != null ? e.StagingItem.Input : null;
            // Controller presses arrive as key events; mouse navigation also wakes full-rate transitions.
            if (input is System.Windows.Input.KeyEventArgs || input is System.Windows.Input.MouseEventArgs) Poke();
        }

        // The display containing Playnite, not necessarily the primary monitor (the TV may have a different rate).
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct DevMode
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);
        private static int screenRate; private static DateTime screenRateAt = DateTime.MinValue;
        private static string screenDevice;
        public static int ScreenRate()
        {
            // asked for by every animation made (Fast, Ambient) and every half second: answered from the last look for 2 s, before
            // any window or display is touched (the lookup below allocates a Screen each time)
            if (screenRate > 0 && (DateTime.Now - screenRateAt).TotalSeconds < 2) return screenRate;
            try
            {
                var main = Application.Current != null ? Application.Current.MainWindow : null;
                var handle = main != null ? new System.Windows.Interop.WindowInteropHelper(main).Handle : IntPtr.Zero;
                var device = handle != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(handle).DeviceName : null;
                if ((DateTime.Now - screenRateAt).TotalSeconds < 10 && device == screenDevice) return screenRate;
                screenRateAt = DateTime.Now; screenDevice = device; screenRate = 0;
                var dm = new DevMode(); dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DevMode));
                if (EnumDisplaySettings(device, -1, ref dm)) screenRate = dm.dmDisplayFrequency;
            }
            catch { }
            return screenRate;
        }

        public static void FullRateChanged()
        {
            // in front and in use (pressed lately): the slow-frame watchdog listens too; quiet, neither does (see Poke)
            bool active = RecentlyActive && PlayniteInFront(fullRateWatched ?? Application.Current.MainWindow);
            Smoothness.Listen(active);
            // only worth it above 60 Hz (drawing every frame keeps the GPU a little busy; on a 60 Hz TV there's nothing to gain)
            bool want = active && Features.On("fullrate") && ScreenRate() > 62;
            if (want == fullRateOn) return;
            fullRateOn = want;
            if (want) System.Windows.Media.CompositionTarget.Rendering += EveryFrame;
            else System.Windows.Media.CompositionTarget.Rendering -= EveryFrame;
            if (!fullRateLogged || DevFrames) { fullRateLogged = true; HomeNav.Log("full refresh rate " + (want ? "on" : "off") + " while in use (screen " + ScreenRate() + " Hz)"); }
        }
        private static bool fullRateLogged;

        // developer switch: %TEMP%\selene-dev-frames makes the log say, each second, how many frames were drawn and the longest
        // gap between two (to find what makes Selene stutter)
        public static void StartupFrameLog()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            WatchSlowJobs(sw, 40, 9000);
            double last = 0; int n = 0;
            EventHandler tick = null;
            tick = (s, e) =>
            {
                double t = sw.Elapsed.TotalMilliseconds;
                if (n++ > 0 && t - last > 40) HomeNav.Log("start-up frame gap " + (t - last).ToString("0") + " ms");
                last = t;
                if (t > 9000) System.Windows.Media.CompositionTarget.Rendering -= tick;
            };
            System.Windows.Media.CompositionTarget.Rendering += tick;
        }

        // a controller press (or release) exactly as Playnite's controller manager sends it: a GameControllerInputEventArgs through the
        // InputManager, so Selene's pad handling and the lists treat it as a real press (held-direction steps, automated runs)
        private static Type padArgs, padInput, padState;
        public static bool SendPad(string button, bool down)
        {
            try
            {
                if (padArgs == null || padInput == null || padState == null)
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!asm.GetName().Name.StartsWith("Playnite")) continue;
                        Type[] types;
                        try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                        if (padArgs == null) padArgs = types.FirstOrDefault(t => t.Name == "GameControllerInputEventArgs");
                        if (padInput == null) padInput = types.FirstOrDefault(t => t.IsEnum && t.Name == "ControllerInput");
                        if (padState == null) padState = types.FirstOrDefault(t => t.IsEnum && t.Name == "ControllerInputState");
                    }
                if (padArgs == null || padInput == null || padState == null) { HomeNav.Log("pad: Playnite's controller types not found"); return false; }
                var key = button == "DPadLeft" ? System.Windows.Input.Key.Left : button == "DPadRight" ? System.Windows.Input.Key.Right : button == "DPadUp" ? System.Windows.Input.Key.Up
                    : button == "DPadDown" ? System.Windows.Input.Key.Down : button == "A" ? System.Windows.Input.Key.Enter : System.Windows.Input.Key.Escape;
                var names = Enum.GetNames(padState);
                var state = Enum.Parse(padState, down ? (names.FirstOrDefault(n => n.IndexOf("Press", StringComparison.OrdinalIgnoreCase) >= 0) ?? names[0])
                                                      : (names.FirstOrDefault(n => n.IndexOf("Releas", StringComparison.OrdinalIgnoreCase) >= 0) ?? names[names.Length - 1]));
                // both stages, each its own event, as Playnite sends them: Selene's row moves on the Preview, its pad handling
                // (repeats, holds) reads the KeyDown
                foreach (var stage in down ? new[] { System.Windows.Input.Keyboard.PreviewKeyDownEvent, System.Windows.Input.Keyboard.KeyDownEvent }
                                           : new[] { System.Windows.Input.Keyboard.PreviewKeyUpEvent, System.Windows.Input.Keyboard.KeyUpEvent })
                {
                    var e = (System.Windows.Input.KeyEventArgs)Activator.CreateInstance(padArgs, key, state, Enum.Parse(padInput, button));
                    e.RoutedEvent = stage;
                    System.Windows.Input.InputManager.Current.ProcessInput(e);
                }
                return true;
            }
            catch (Exception ex) { HomeNav.Log("pad: " + (ex.InnerException ?? ex).Message); return false; }
        }

        public static readonly bool DevFrames = System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-frames"));
        public static void DevFrameLog()
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-frames"))) return;
            DevSampler();
            JobCensus();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double last = 0, worst = 0, second = 0; int n = 0;
            double frameAt = -1; int unique = 0, late = 0;
            WatchSlowJobs(sw, 14, 0);
            System.Windows.Media.CompositionTarget.Rendering += (s, e) =>
            {
                double t = sw.Elapsed.TotalMilliseconds;
                // frames really drawn (WPF can call this more than once per frame) and the ones that came late (> 1.25 refreshes)
                var re = e as System.Windows.Media.RenderingEventArgs;
                double ft = re != null ? re.RenderingTime.TotalMilliseconds : t;
                if (ft != frameAt) { if (frameAt >= 0 && ft - frameAt > 1250.0 / FrameRate) late++; frameAt = ft; unique++; }
                if (n > 0 && t - last > 60) HomeNav.Log("frame gap " + (t - last).ToString("0") + " ms");
                if (n > 0) worst = Math.Max(worst, t - last);
                last = t; n++;
                if (t - second >= 1000)
                {
                    HomeNav.Log("fps " + n + ", worst gap " + worst.ToString("0") + " ms, frames " + unique + ", late " + late + (ProgressToast.Busy ? " (notification up)" : ""));
                    second = t; n = 0; worst = 0; unique = 0; late = 0;
                }
            };
        }

        // developer switch: %TEMP%\selene-dev-sample (with selene-dev-frames) samples the UI thread's stack every 4 ms while
        // one job runs past 10 ms, and logs the methods found most often every 15 s (what a stutter is spent in)
        private static void DevSampler()
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-sample"))) return;
            var ui = System.Threading.Thread.CurrentThread;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long busySince = -1;
            var hooks = Application.Current.Dispatcher.Hooks;
            hooks.OperationStarted += (s, e) => System.Threading.Interlocked.Exchange(ref busySince, clock.ElapsedMilliseconds);
            hooks.OperationCompleted += (s, e) => System.Threading.Interlocked.Exchange(ref busySince, -1);
            var counts = new Dictionary<string, int>();
            int samples = 0;
            var t = new System.Threading.Thread(() =>
            {
                long lastReport = clock.ElapsedMilliseconds;
                while (true)
                {
                    System.Threading.Thread.Sleep(4);
                    long since = System.Threading.Interlocked.Read(ref busySince);
                    if (since >= 0 && clock.ElapsedMilliseconds - since > 10)
                    {
                        System.Diagnostics.StackTrace st = null;
#pragma warning disable 618
                        try { ui.Suspend(); try { st = new System.Diagnostics.StackTrace(ui, false); } finally { ui.Resume(); } } catch { }
#pragma warning restore 618
                        if (st != null)
                        {
                            samples++;
                            var seen = new HashSet<string>();
                            foreach (var f in st.GetFrames() ?? new System.Diagnostics.StackFrame[0])
                            {
                                var m = f.GetMethod();
                                if (m == null) continue;
                                var name = (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name;
                                if (seen.Add(name)) { int c; counts.TryGetValue(name, out c); counts[name] = c + 1; }
                            }
                        }
                    }
                    if (clock.ElapsedMilliseconds - lastReport > 15000 && samples > 0)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        var top = counts.Where(kv => !kv.Key.StartsWith("System.Windows.Threading.") && !kv.Key.StartsWith("MS.Internal.Threading") && !kv.Key.StartsWith("System.Threading.ExecutionContext"))
                            .OrderByDescending(kv => kv.Value).Take(45).Select(kv => kv.Value * 100 / samples + "% " + kv.Key);
                        HomeNav.Log("sampler (" + samples + " samples while busy): " + string.Join(" | ", top));
                        counts.Clear(); samples = 0;
                    }
                }
            }) { IsBackground = true, Name = "Selene dev sampler" };
            t.Start();
        }

        // the method a dispatcher operation will run (a timer's tick shows the handlers it calls and its interval)
        private static string JobName(System.Windows.Threading.DispatcherOperation op)
        {
            string name = "?";
            try
            {
                var f = typeof(System.Windows.Threading.DispatcherOperation).GetField("_method", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var d = f != null ? f.GetValue(op) as Delegate : null;
                if (d != null) name = (d.Method.DeclaringType != null ? d.Method.DeclaringType.FullName : "") + "." + d.Method.Name + (d.Target != null ? " on " + d.Target.GetType().Name : "");
                var timer = d != null ? d.Target as System.Windows.Threading.DispatcherTimer : null;
                if (timer != null)
                {
                    var tf = typeof(System.Windows.Threading.DispatcherTimer).GetField("Tick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var tick = tf != null ? tf.GetValue(timer) as Delegate : null;
                    if (tick != null) foreach (var h in tick.GetInvocationList()) name += " -> " + (h.Method.DeclaringType != null ? h.Method.DeclaringType.FullName : "") + "." + h.Method.Name;
                    name += " every " + timer.Interval.TotalMilliseconds + " ms";
                }
            }
            catch { }
            return name;
        }

        // developer switch: %TEMP%\selene-dev-census (with selene-dev-frames) counts every UI-thread job by name and logs, every
        // 30 s, how often each ran and how long it held the thread: what Selene does while nothing is pressed
        private static void JobCensus()
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-census"))) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var started = new Dictionary<System.Windows.Threading.DispatcherOperation, double>();
            var count = new Dictionary<string, int>(); var total = new Dictionary<string, double>();
            double since = 0; int all = 0; double busy = 0;
            var hooks = Application.Current.Dispatcher.Hooks;
            hooks.OperationStarted += (s, e) => { started[e.Operation] = clock.Elapsed.TotalMilliseconds; };
            hooks.OperationCompleted += (s, e) =>
            {
                double t0;
                if (!started.TryGetValue(e.Operation, out t0)) return;
                started.Remove(e.Operation);
                double now = clock.Elapsed.TotalMilliseconds, took = now - t0;
                var name = JobName(e.Operation) + " (" + e.Operation.Priority + ")";
                int c; count.TryGetValue(name, out c); count[name] = c + 1;
                double ms; total.TryGetValue(name, out ms); total[name] = ms + took;
                all++; busy += took;
                if (now - since < 30000) return;
                var top = total.OrderByDescending(kv => kv.Value).Take(14).Select(kv => count[kv.Key] + "x " + kv.Value.ToString("0") + " ms " + kv.Key);
                string memory = "";
                try { using (var me = System.Diagnostics.Process.GetCurrentProcess()) memory = ", private " + (me.PrivateMemorySize64 >> 20) + " MB, working set " + (me.WorkingSet64 >> 20) + " MB, managed " + (GC.GetTotalMemory(false) >> 20) + " MB, collections " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2); } catch { }
                HomeNav.Log("census " + ((now - since) / 1000).ToString("0") + " s: " + all + " jobs, UI thread busy " + (busy * 100 / (now - since)).ToString("0.0") + "%" + memory + " | " + string.Join(" | ", top));
                count.Clear(); total.Clear(); all = 0; busy = 0; since = now;
            };
        }
        // every UI-thread job longer than 'threshold' ms goes to the log by name (for 'forMs' ms, 0 = for good)
        private static void WatchSlowJobs(System.Diagnostics.Stopwatch sw, double threshold, double forMs)
        {
            var started = new Dictionary<System.Windows.Threading.DispatcherOperation, double>();
            var hooks = Application.Current.Dispatcher.Hooks;
            System.Windows.Threading.DispatcherHookEventHandler onStart = null, onDone = null;
            onStart = (s, e) => { started[e.Operation] = sw.Elapsed.TotalMilliseconds; };
            onDone = (s, e) =>
            {
                if (forMs > 0 && sw.Elapsed.TotalMilliseconds > forMs) { hooks.OperationStarted -= onStart; hooks.OperationCompleted -= onDone; return; }
                double t0;
                if (!started.TryGetValue(e.Operation, out t0)) return;
                started.Remove(e.Operation);
                double took = sw.Elapsed.TotalMilliseconds - t0;
                if (took < threshold) return;
                string name = JobName(e.Operation);
                HomeNav.Log("slow job " + took.ToString("0") + " ms: " + name + " (" + e.Operation.Priority + ")");
            };
            hooks.OperationStarted += onStart;
            hooks.OperationCompleted += onDone;
        }

        public static void MeasureFrames(string what, int ms)
        {
            if (measuring) return;
            measuring = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double last = 0, worst = 0; int n = 0;
            EventHandler tick = null;
            tick = (s2, e2) =>
            {
                double t = sw.Elapsed.TotalMilliseconds;
                if (n > 0) worst = Math.Max(worst, t - last);
                last = t; n++;
                if (t >= ms)
                {
                    System.Windows.Media.CompositionTarget.Rendering -= tick;
                    measuring = false;
                    HomeNav.Log("frames: " + what + ": " + n + " in " + (int)t + " ms, avg " + (n > 1 ? (t / (n - 1)).ToString("0.0") : "-") + " ms, worst " + worst.ToString("0.0") + " ms");
                }
            };
            System.Windows.Media.CompositionTarget.Rendering += tick;
        }

        public static void Tick() { if (Features.On("sounds") && !HomeNav.PlayPlayniteSound(false)) Play("navigation.wav", 0.7); }
        public static void Key() { Play("Key.wav", 0.8); }              // typing, erasing, flipping a switch
        public static void Confirm() { if (Features.On("sounds") && !HomeNav.PlayPlayniteSound(true)) Play("activation.wav", 0.85); }
        public static void Back() { Play("CloseAdditionalView.wav", 0.8); }
        public static void Chime() { Play("Success.wav", 0.85); }   // something finished: a profile made, a setup step done
        public static void Boot() { if (Features.On("startsound")) Play("Boot.wav", 0.9); }
        public static void Alert() { Play("Alert.wav", 0.85); }
        // a notification shows (Selene's notices, a progress card): theme\Audio\Notification.wav, else the panel sound
        public static void NotifySound()
        {
            if (!Features.On("notifysound")) return;
            if (System.IO.File.Exists(SelenePaths.ThemeAudio("Notification.wav"))) Play("Notification.wav", 0.8); else Play("OpenPanel.wav", 0.55);
        }   // asking before something can't be undone

        // decodes pictures one after another off the UI thread and hands each over on it (avatar and wallpaper grids)
        public static void LoadInOrder(IList<string> files, int width, Action<int, ImageSource> loaded)
        {
            var dispatcher = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                for (int i = 0; i < files.Count; i++)
                {
                    var pic = Wallpapers.Load(files[i], width);
                    if (pic == null) continue;
                    int at = i;
                    dispatcher.BeginInvoke(new Action(() => AfterHold(() => loaded(at, pic))), System.Windows.Threading.DispatcherPriority.Background);
                }
            });
        }

        // a page slide in progress: changes to what's on it wait until it's over (each picture arriving mid-slide redrew the
        // whole sliding page: the step slides stuttered)
        private static DateTime holdUntil = DateTime.MinValue;
        public static void HoldVisuals(int ms) { holdUntil = DateTime.Now.AddMilliseconds(ms); }

        public static void AfterHold(Action act)
        {
            var wait = (holdUntil - DateTime.Now).TotalMilliseconds;
            if (wait <= 0) { act(); return; }
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait + 10) };
            t.Tick += (s, e) => { t.Stop(); act(); };
            t.Start();
        }

        // a button-like pill: the controller badge inside, then what it does (the main one filled with the focus color)
        public static Border ActionPill(string button, string label, bool main)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Grid { Width = 26, Height = 26 };
            dot.Children.Add(new Ellipse { Fill = main ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.9) });
            dot.Children.Add(Letter(button, 13, FontWeights.Bold, main ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneScrimColor")));
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = label, FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Body, Foreground = main ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 1) });
            return new Border
            {
                Child = row, CornerRadius = new CornerRadius(20), Padding = new Thickness(6, 6, 16, 6), Margin = new Thickness(0, 0, 10, 0),
                Background = main ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.12),
                BorderBrush = main ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.2), BorderThickness = new Thickness(1)
            };
        }

        // ---------------- navigation ----------------
        // controller and keyboard both arrive as button names; the left stick moves like the d-pad
        public static string Normalize(string button)
        {
            if (button == null) return "";
            if (button.StartsWith("LeftStick") && button != "LeftStick") return "DPad" + button.Substring("LeftStick".Length);
            return button;
        }

        // true when the same direction came again within a moment (a held stick or d-pad repeating), so a move that
        // leaves one group for another (cards -> buttons) can wait for a fresh press
        private static string lastDirection;
        private static DateTime lastDirectionAt;
        public static bool Repeated(string button)
        {
            return button == lastDirection && (DateTime.Now - lastDirectionAt).TotalMilliseconds < 300;
        }

        // A controller press reaches Selene twice: as the pad button and as the keyboard key Playnite makes from it. A pad
        // press Selene handled marks the moment; the key copy arriving right after is dropped (it moved things twice: a
        // selection skipped one). Every keyboard handler checks KeyIsPadCopy first.
        private static DateTime padHandledAt = DateTime.MinValue;
        public static void PadHandled() { padHandledAt = DateTime.Now; }

        // the controller: when a button last went down (a press that came from it), and buttons being let go
        public static DateTime PadPressAt = DateTime.MinValue;
        public static bool PressFromPad { get { return (DateTime.Now - PadPressAt).TotalMilliseconds < 80; } }
        public static event Action<string> ButtonReleased;
        public static void Released(string button) { var h = ButtonReleased; if (h != null) h(button); }
        public static bool KeyIsPadCopy() { return (DateTime.Now - padHandledAt).TotalMilliseconds < 120; }

        // the copy of a stick push can come later than that (126-136 ms seen, so the stick moved two groups in Selene settings):
        // an arrow key in the direction the pad last went, within a quarter second, is that copy too (no one presses the same
        // way on the pad and the keyboard that close together)
        private static string padDirection;
        private static DateTime padDirectionAt = DateTime.MinValue;
        public static void PadWent(string button) { var b = Normalize(button); if (b.StartsWith("DPad")) { padDirection = b; padDirectionAt = DateTime.Now; } }
        public static bool KeyIsPadCopy(string direction) { return KeyIsPadCopy() || (direction != null && direction == padDirection && (DateTime.Now - padDirectionAt).TotalMilliseconds < 250); }

        public static void Pressed(string button)
        {
            if (button.StartsWith("DPad")) { lastDirection = button; lastDirectionAt = DateTime.Now; }
        }

        // moves an index around a grid of `count` items, `cols` wide; returns -1 when it would leave the grid on that side
        public static int GridMove(int index, string dir, int count, int cols)
        {
            int row = index / cols, col = index % cols;
            switch (dir)
            {
                case "DPadLeft": return col > 0 ? index - 1 : -1;
                case "DPadRight": return col < cols - 1 && index + 1 < count ? index + 1 : -1;
                case "DPadUp": return row > 0 ? index - cols : -1;
                case "DPadDown":
                    if (index + cols < count) return index + cols;
                    return row < (count - 1) / cols ? count - 1 : -1;   // short last row: the last item
            }
            return -1;
        }

        // ---------------- the crescent crown ----------------
        // Selene's mark as vectors so it takes the theme's colors: crescent in the text color, full moon glowing in the accent
        public static Grid Mark(double size, out UIElement glow)
        {
            var g = new Grid { Width = size, Height = size };
            var canvas = new Canvas { Width = 72, Height = 72 };
            var view = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
            var halo = new Ellipse { Width = 64, Height = 64 };
            var haloBrush = new RadialGradientBrush();
            haloBrush.GradientStops.Add(Ink.Stop("GlyphColor", 0.7, 0));
            haloBrush.GradientStops.Add(Ink.Stop("GlyphColor", 0.38, 0.3));
            haloBrush.GradientStops.Add(Ink.Stop("GlyphColor", 0.12, 0.65));
            haloBrush.GradientStops.Add(Ink.Stop("GlyphColor", 0, 1));
            halo.Fill = haloBrush;
            Canvas.SetLeft(halo, 36 - 32); Canvas.SetTop(halo, 18 - 32);
            canvas.Children.Add(halo);
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 9.109,20.577 A 31,31 0 1 0 62.891,20.577 A 27,27 0 1 1 9.109,20.577 Z"),
                Fill = Ink.Brush("SeleneInkColor")
            });
            var ring = new Ellipse { Width = 18, Height = 18, Stroke = Ink.Brush("SeleneInkColor"), StrokeThickness = 3.2 };
            Canvas.SetLeft(ring, 36 - 9); Canvas.SetTop(ring, 18 - 9);
            canvas.Children.Add(ring);
            var dot = new Ellipse { Width = 6.4, Height = 6.4, Fill = Ink.Brush("SeleneInkColor") };
            Canvas.SetLeft(dot, 36 - 3.2); Canvas.SetTop(dot, 18 - 3.2);
            canvas.Children.Add(dot);
            g.Children.Add(view);
            glow = halo;
            return g;
        }
    }

    // A choice between a few options, changed with one button: a label, the options in a glass pill (the chosen one
    // filled with the focus color), and the button badge
    public class Segmented
    {
        public readonly StackPanel Root = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly List<Border> segments = new List<Border>();
        private readonly List<TextBlock> labels = new List<TextBlock>();
        private readonly Border highlight;
        private readonly TranslateTransform slide = new TranslateTransform();
        private int selected = -1;

        public Segmented(string title, string[] options, string button)
        {
            Root.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var o in options)
            {
                // one weight for every option: a bolder chosen one was wider and pushed the badge around
                var label = new TextBlock { Text = o, FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.8) };
                var seg = new Border { CornerRadius = new CornerRadius(19), Padding = new Thickness(18, 7, 18, 8), Child = label };
                row.Children.Add(seg);
                segments.Add(seg);
                labels.Add(label);
            }
            // the filled highlight sits under the options and slides to the chosen one
            // a real starting width: an automatic (NaN) width can't be animated and crashed Playnite
            highlight = new Border { CornerRadius = new CornerRadius(19), Background = Ink.Brush("SeleneFocusColor"), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = slide, Opacity = 0, Width = 0 };
            var track = new Grid();
            track.Children.Add(highlight);
            track.Children.Add(row);
            Root.Children.Add(new Border
            {
                Child = track, CornerRadius = new CornerRadius(23), Padding = new Thickness(4),
                Background = Ink.Brush("SeleneInkColor", 0.1), BorderBrush = Ink.Brush("SeleneInkColor", 0.18), BorderThickness = new Thickness(1)
            });
            var badge = new Grid { Width = 28, Height = 28, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.Children.Add(new Ellipse { Fill = Ink.Brush("SeleneInkColor", 0.9) });
            badge.Children.Add(new TextBlock { Text = button, FontSize = 14, FontWeight = FontWeights.Bold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) });
            Root.Children.Add(badge);
        }

        public void Select(int index, bool animate)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                labels[i].Foreground = i == index ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.8);
            }
            selected = index;
            var seg = segments[index];
            if (seg.ActualWidth == 0)
            {
                // not laid out yet: place it once it is
                seg.Dispatcher.BeginInvoke(new Action(() => { if (selected == index) Place(index, false); }), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }
            Place(index, animate);
        }

        private void Place(int index, bool animate)
        {
            var seg = segments[index];
            double x = 0;
            for (int i = 0; i < index; i++) x += segments[i].ActualWidth;
            var ms = TimeSpan.FromMilliseconds(animate ? 260 : 0);
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, ms) { EasingFunction = Ui.Out() });
            if (!animate || double.IsNaN(highlight.Width) || highlight.Width == 0) { highlight.BeginAnimation(FrameworkElement.WidthProperty, null); highlight.Width = seg.ActualWidth; }
            else highlight.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(highlight.Width, seg.ActualWidth, ms) { EasingFunction = Ui.Out() });
            highlight.Height = seg.ActualHeight;
            highlight.Opacity = 1;
        }
    }

    public enum Tone { Accent, Success, Warning, Muted }

    // The panel that explains whatever is focused: its icon in a soft tinted circle, its name with a status chip,
    // one line about it, and what the buttons do as pills (badge inside) with a small note beside them
    public class DetailPanel
    {
        public readonly StackPanel Root = new StackPanel();
        private readonly Ellipse circle;
        private readonly TextBlock glyph, title, chipText, about, note;
        private readonly Border chip;
        private readonly StackPanel actions;

        // everything centred: icon, name and chip on one line, the line about it, then the action pills and the note
        public DetailPanel(double width)
        {
            Root.Width = width;
            Root.HorizontalAlignment = HorizontalAlignment.Center;
            var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var badge = new Grid { Width = 52, Height = 52, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            circle = new Ellipse();
            glyph = new TextBlock { FontFamily = Ui.Glyphs, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
            badge.Children.Add(circle);
            badge.Children.Add(glyph);
            head.Children.Add(badge);
            title = new TextBlock { FontSize = 28, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center };
            chipText = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = Ui.Body };
            chip = new Border { Child = chipText, CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 3, 11, 4), Margin = new Thickness(14, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(title);
            head.Children.Add(chip);
            Root.Children.Add(head);
            about = new TextBlock { FontSize = 19, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.82), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 52, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            Root.Children.Add(about);
            var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
            actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            note = new TextBlock { FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = width - 260 };
            foot.Children.Add(actions);
            foot.Children.Add(note);
            Root.Children.Add(foot);
        }

        // actions: pairs of button + label, e.g. "A", "Install"; the first is drawn as the main one
        public void Show(string icon, Tone tone, string name, string status, string text, string noteText, params string[] buttons)
        {
            var key = tone == Tone.Success ? "SeleneSuccessColor" : tone == Tone.Muted ? "SeleneInkColor" : "GlyphColor";
            if (tone == Tone.Warning)
            {
                circle.Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x6B, 0x6B));
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                chipText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                chip.Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0x6B, 0x6B));
            }
            else
            {
                circle.Fill = Ink.Brush(key, 0.16);
                glyph.Foreground = Ink.Brush(key);
                chipText.Foreground = Ink.Brush(key);
                chip.Background = Ink.Brush(key, 0.16);
            }
            glyph.Text = icon;
            title.Text = name;
            chipText.Text = (status ?? "").ToUpperInvariant();
            chip.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
            about.Text = text;
            note.Text = noteText ?? "";
            note.Margin = new Thickness(buttons.Length > 0 ? 8 : 0, 0, 0, 0);
            actions.Children.Clear();
            for (int i = 0; i + 1 < buttons.Length; i += 2) actions.Children.Add(Ui.ActionPill(buttons[i], buttons[i + 1], i == 0));
        }
    }

    // A centred question over a dimmed screen: icon, title, line, and two buttons (the safe one chosen first).
    // Left/right picks, A answers, B is the safe answer; the caller sends it buttons while it's open.
    public class Dialog
    {
        public readonly Grid Root;
        private readonly Pill yes, no;
        private readonly Action onYes, onNo;
        private bool onYesButton, done;
        private readonly FrameworkElement panel;
        private readonly bool warning;
        private readonly ScrollViewer bodyScroll;

        // warning: a red badge and a red Yes (Remove...); otherwise the theme's accent (Install all, Restart)
        public Dialog(string icon, string title, string text, string yesLabel, string noLabel, Action onYes, Action onNo, bool warning = true, bool material = false, double maxBodyHeight = 0, double width = 640)
        {
            this.onYes = onYes;
            this.onNo = onNo;
            this.warning = warning;
            Root = new Grid { Background = Ink.Brush("SeleneScrimColor", 0.62) };
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var badge = new Grid { Width = 84, Height = 84, HorizontalAlignment = HorizontalAlignment.Center };
            badge.Children.Add(new Ellipse { Fill = warning ? (Brush)new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x6B, 0x6B)) : Ink.Brush("GlyphColor", 0.19) });
            // the icon centred by its ink (material: a Material Icons code, else Segoe MDL2)
            var glyph = Ui.Glyph(icon, material ? Ui.Icons : Ui.Glyphs, 36, Ink.Brush("GlyphColor"));
            if (warning) glyph.SetResourceReference(glyph is System.Windows.Shapes.Shape ? System.Windows.Shapes.Shape.FillProperty : TextBlock.ForegroundProperty, "WarningBrush");
            badge.Children.Add(glyph);
            col.Children.Add(badge);
            col.Children.Add(new TextBlock { Text = title, FontSize = 34, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) });
            var body = new TextBlock { Text = text, FontSize = 19, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.75), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = width == 640 ? 520 : width - 80, Margin = new Thickness(0, 10, 0, 0) };
            if (maxBodyHeight > 0)
            {
                bodyScroll = new ScrollViewer { MaxHeight = maxBodyHeight, Content = body, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                col.Children.Add(bodyScroll);
            }
            else col.Children.Add(body);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 34, 0, 0) };
            no = new Pill(noLabel, 190);
            yes = new Pill(yesLabel, 190);
            no.Root.MouseLeftButtonUp += (s, e) => { Answer(false); e.Handled = true; };
            yes.Root.MouseLeftButtonUp += (s, e) => { Answer(true); e.Handled = true; };
            buttons.Children.Add(no.Root);
            buttons.Children.Add(yes.Root);
            col.Children.Add(buttons);
            panel = Ui.Lifted(new Border
            {
                Child = col, Width = width, CornerRadius = new CornerRadius(28), Padding = new Thickness(40, 40, 40, 36),
                Background = Ink.Brush("SeleneGlassColor", 0.96), BorderBrush = Ink.Brush("SeleneInkColor", 0.16), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }, 60, 0.55);   // (the shadow behind it: an Effect on the card blurred its words)
            Root.Children.Add(panel);
            ShowFocus();
        }

        public void Open(Panel host)
        {
            host.Children.Add(Root);
            Root.Opacity = 0;
            Ui.Fade(Root, 1, 200);
            panel.RenderTransformOrigin = new Point(0.5, 0.5);
            var s = Ui.Scaler(panel);
            s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ui.Out() });
            s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ui.Out() });
            Ui.Alert();
        }

        private void ShowFocus()
        {
            no.Focus(!onYesButton);
            yes.Focus(onYesButton);
            // the destructive answer turns red when chosen
            if (onYesButton && warning) { yes.Face.SetResourceReference(Border.BackgroundProperty, "WarningBrush"); yes.Face.SetResourceReference(Border.BorderBrushProperty, "WarningBrush"); }
        }

        // starts on Yes (for a question whose Yes is the usual answer)
        public Dialog StartOnYes() { onYesButton = true; ShowFocus(); return this; }

        public void Handle(string b)
        {
            if (done) return;
            if (bodyScroll != null && (b == "DPadDown" || b == "DPadUp")) { bodyScroll.ScrollToVerticalOffset(bodyScroll.VerticalOffset + (b == "DPadDown" ? 60 : -60)); return; }
            if ((b == "DPadLeft" && onYesButton) || (b == "DPadRight" && !onYesButton)) { onYesButton = !onYesButton; Ui.Tick(); ShowFocus(); return; }
            if (b == "A") Answer(onYesButton);
            else if (b == "B") Answer(false);
        }

        public void Answer(bool sure)
        {
            if (done) return;
            done = true;
            var host = Root.Parent as Panel;
            Ui.Fade(Root, 0, 160, 0, () => { if (host != null) host.Children.Remove(Root); });
            if (sure) onYes(); else { Ui.Back(); onNo(); }
        }
    }

    // A focusable tile: grows and shows a glowing ring in the theme's focus color when focused
    public class Tile
    {
        public readonly Grid Root;
        public readonly Border Face;
        private readonly Border ring;
        public double FocusScale = 1.07;
        public bool Focused { get; private set; }

        public Tile(double width, double height, double radius, double gap = 6)
        {
            Root = new Grid { Width = width, Height = height };
            // glass, deep enough to read over a busy wallpaper (the near-clear face lost its words in the art)
            Face = new Border
            {
                CornerRadius = new CornerRadius(radius), Background = Ink.Brush("SeleneGlassColor", 0.72),
                BorderBrush = Ink.Brush("SeleneInkColor", 0.16), BorderThickness = new Thickness(1)
            };
            ring = new Border
            {
                CornerRadius = new CornerRadius(radius + gap), Margin = new Thickness(-gap), BorderThickness = new Thickness(3),
                BorderBrush = Ink.Brush("SeleneFocusColor"), Opacity = 0, IsHitTestVisible = false
            };
            // the ring's glow is a blur: it's on the ring only while the ring shows (hundreds of tiles each blurring an invisible
            // ring on every repaint made big grids crawl)
            glow = Ink.Track(new DropShadowEffect { ShadowDepth = 0, BlurRadius = 22, Opacity = 0.55 }, DropShadowEffect.ColorProperty, "SeleneFocusColor");
            Root.Children.Add(Face);
            Root.Children.Add(ring);
            // drawn once into a texture: moving it, scaling it or repainting what's under it costs a blit, not a redraw
            Root.CacheMode = new System.Windows.Media.BitmapCache { RenderAtScale = 1.12, SnapsToDevicePixels = true };   // a little larger than shown: grown (focused) it stays crisp
        }

        private DropShadowEffect glow;

        // a ring in a fixed color instead of the theme's focus color (each profile glows in its own theme's accent)
        public void Tint(Color color)
        {
            ring.BorderBrush = new SolidColorBrush(color);
            glow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 26, Opacity = 0.7, Color = color };
            if (Focused) ring.Effect = glow;
        }

        public void Focus(bool on)
        {
            if (on == Focused) return;
            Focused = on;
            Ui.ScaleTo(Root, on ? FocusScale : 1, on ? 200 : 160);
            if (on) ring.Effect = glow;
            Ui.Fade(ring, on ? 1 : 0, on ? 180 : 140);
            if (!on)
            {
                var gone = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
                gone.Tick += (s, e) => { gone.Stop(); if (!Focused) ring.Effect = null; };
                gone.Start();
            }
        }
    }

    // A rounded button: glass at rest, filled with the focus color (and its text color) when focused
    public class Pill
    {
        // Root is what goes in a layout (and scales): the face with its words, and behind it, only while focused, a twin of its
        // shape that carries the glow. A glow on the face itself drew its words soft.
        public readonly Grid Root;
        public readonly Border Face;
        private readonly Border halo;
        private readonly TextBlock label;
        public bool Focused { get; private set; }

        public Pill(string text, double minWidth = 200)
        {
            label = new TextBlock { Text = text, FontSize = 21, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Face = new Border
            {
                Child = label, CornerRadius = new CornerRadius(27), Height = 54, MinWidth = minWidth, Padding = new Thickness(34, 0, 34, 2),
                Background = Ink.Brush("SeleneGlassColor", 0.72), BorderBrush = Ink.Brush("SeleneInkColor", 0.22), BorderThickness = new Thickness(1),   // glass that reads over art
                UseLayoutRounding = true
            };
            TextOptions.SetTextFormattingMode(Face, TextFormattingMode.Display);
            halo = new Border { CornerRadius = Face.CornerRadius, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            halo.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { Source = Face });
            halo.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = Face });
            halo.SetBinding(FrameworkElement.HeightProperty, new System.Windows.Data.Binding("ActualHeight") { Source = Face });
            Root = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            Root.Children.Add(halo);
            Root.Children.Add(Face);
        }

        public string Text { get { return label.Text; } set { label.Text = value; } }

        public void Focus(bool on)
        {
            if (on == Focused) return;
            Focused = on;
            Face.Background = on ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.14);
            Face.BorderBrush = on ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.22);
            label.Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor");
            halo.Effect = on ? Ink.Track(new DropShadowEffect { ShadowDepth = 0, BlurRadius = 22, Opacity = 0.4 }, DropShadowEffect.ColorProperty, "SeleneFocusColor") : null;
            halo.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Ui.ScaleTo(Root, on ? 1.06 : 1, 160);
        }
    }

    // One screen of the login layer (intro, setup check, profile steps, who's playing)
    public abstract class Screen
    {
        public Grid Root = new Grid();
        // a controller button (normalized: the left stick arrives as DPad*); keyboard keys arrive mapped to these too
        public abstract void Handle(string button);
        // typed text (keyboard), for screens that take it
        public virtual bool WantsText { get { return false; } }
        public virtual void Type(string text) { }
        public virtual void Erase() { }
        public virtual void Shown() { }
        public virtual void Hidden() { }
        // true = shown over the dimmed home (menus) instead of on the login backdrop
        public virtual bool Overlay { get { return false; } }
        // the shade over the home for an overlay screen (null = the usual deep one)
        public virtual Brush OverlayShade { get { return null; } }
        // how long it takes to slide in
        public virtual int ShowMs { get { return 320; } }
        // Start opens Selene's menu over it (as a console's control center, everywhere); false where Start is the screen's
        // own (done, next) or the menu has no place (signing in, setting up)
        public virtual bool MenuOnStart { get { return true; } }
    }
}
