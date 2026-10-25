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
        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily Body = new FontFamily("Segoe UI");
        public static readonly FontFamily Glyphs = new FontFamily("Segoe MDL2 Assets");
        private static FontFamily icons;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private struct DevMode
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        private static int hz;

        // the primary screen's refresh rate: WPF renders at 60 fps unless an animation asks for more, so every animation
        // that wants a smooth move asks for this (a fixed 144 capped a 360 Hz screen at 144)
        public static int RefreshHz
        {
            get
            {
                if (hz > 0) return hz;
                int found = 0;
                try
                {
                    var dm = new DevMode();
                    dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DevMode));
                    if (EnumDisplaySettings(null, -1, ref dm)) found = dm.dmDisplayFrequency;   // -1: the current mode
                }
                catch { }
                hz = found >= 30 && found <= 1000 ? found : 144;
                return hz;
            }
        }

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

        // a Material Icons character from its code point (the font's codepoints list)
        public static string Icon(int code) { return ((char)code).ToString(); }

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
                foreach (var b in new[] { "A", "B", "X", "Y", "LB", "RB", "LT", "RT" }) res["SelenePrompt" + b] = PromptLabel(b);
            }
            catch { }
            if (changed) HomeNav.Log("prompts: " + (ps ? "PlayStation" : "Xbox"));
        }

        public static string PromptLabel(string button)
        {
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
                default: return button;
            }
        }

        public static StackPanel Prompt(string button, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0) };
            var dot = new Grid { Width = 32, Height = 32 };
            dot.Children.Add(new Ellipse { Fill = Ink.Brush("SeleneInkColor") });
            button = PromptLabel(button);
            dot.Children.Add(new TextBlock { Text = button, FontSize = button.Length > 1 ? 11 : 16, FontWeight = FontWeights.SemiBold, FontFamily = Body, Foreground = Ink.Brush("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) });   // capitals sit low in their box
            sp.Children.Add(dot);
            sp.Children.Add(new TextBlock { Text = label, FontSize = 21, FontFamily = Body, Foreground = Ink.Brush("SeleneInkColor", 0.9), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            return sp;
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
            return row;
        }

        // ---------------- motion ----------------
        public static IEasingFunction Out() { return new CubicEase { EasingMode = EasingMode.EaseOut }; }

        public static void Fade(UIElement el, double to, int ms, int delay = 0, Action done = null)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Out() };
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
            Shift(el).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Out() });
        }

        // slides sideways while fading (screen changes)
        public static void Slide(UIElement el, double fromX, double toX, double fromOpacity, double toOpacity, int ms, Action done = null)
        {
            el.Opacity = fromOpacity;
            Fade(el, toOpacity, ms, 0, done);
            Shift(el).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, toX, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out() });
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
        public static void Shake(UIElement el)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
            foreach (var kv in new[] { new[] { 0.0, 0 }, new[] { 0.18, -9 }, new[] { 0.4, 8 }, new[] { 0.62, -5 }, new[] { 0.82, 3 }, new[] { 1.0, 0 } })
                a.KeyFrames.Add(new LinearDoubleKeyFrame(kv[1], KeyTime.FromPercent(kv[0])));
            Shift(el).BeginAnimation(TranslateTransform.XProperty, a);
        }

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
                mp.Volume = Math.Max(0, Math.Min(1, volume * Features.Number("soundvolume") / 100.0));   // the Sound volume setting
                mp.Position = TimeSpan.Zero;
                mp.Play();
            }
            catch (Exception ex) { HomeNav.Log("sound " + file + ": " + ex.Message); }
        }

        // moving and confirming go through Playnite's own player (same sound, same volume as its views); the file is the fallback
        // a short note at the top of the screen (a glass pill): rises in, stays a few seconds, fades; one at a time
        private static Border notice;
        public static void Notice(string text, int ms = 5000)
        {
            try
            {
                var w = Application.Current != null ? Application.Current.MainWindow : null;
                var host = w != null ? HomeNav.FindByName(w, "PART_MainHost") as Grid : null;
                if (host == null) return;
                if (notice != null) { host.Children.Remove(notice); notice = null; }
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(22), Padding = new Thickness(26, 11, 26, 13), MaxWidth = 900,
                    Background = Ink.Brush("SeleneGlassColor", 0.94), BorderBrush = Ink.Brush("SeleneInkColor", 0.18), BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 26, 0, 0),
                    Child = new TextBlock { Text = text, FontSize = 19, FontFamily = Body, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                    Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 30, Opacity = 0.45, Color = Colors.Black },
                    IsHitTestVisible = false
                };
                Grid.SetRowSpan(pill, 99); Grid.SetColumnSpan(pill, 99);
                Panel.SetZIndex(pill, 9999);
                host.Children.Add(pill);
                notice = pill;
                Rise(pill, 0, 12, 260);
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
        public static void Boot() { Play("Boot.wav", 0.9); }
        public static void Alert() { Play("Alert.wav", 0.85); }   // asking before something can't be undone

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
                    dispatcher.BeginInvoke(new Action(() => loaded(at, pic)), System.Windows.Threading.DispatcherPriority.Background);
                }
            });
        }

        // a button-like pill: the controller badge inside, then what it does (the main one filled with the focus color)
        public static Border ActionPill(string button, string label, bool main)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Grid { Width = 26, Height = 26 };
            dot.Children.Add(new Ellipse { Fill = main ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.9) });
            dot.Children.Add(new TextBlock { Text = button, FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = Body, Foreground = main ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) });   // capitals sit low in their box
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
        public static bool KeyIsPadCopy() { return (DateTime.Now - padHandledAt).TotalMilliseconds < 120; }

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

        public Dialog(string icon, string title, string text, string yesLabel, string noLabel, Action onYes, Action onNo)
        {
            this.onYes = onYes;
            this.onNo = onNo;
            Root = new Grid { Background = Ink.Brush("SeleneScrimColor", 0.62) };
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var badge = new Grid { Width = 84, Height = 84, HorizontalAlignment = HorizontalAlignment.Center };
            badge.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x6B, 0x6B)) });
            // icon glyphs sit a little low in their box: nudged to the circle's middle
            var glyph = new TextBlock { Text = icon, FontFamily = Ui.Glyphs, FontSize = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 5) };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            badge.Children.Add(glyph);
            col.Children.Add(badge);
            col.Children.Add(new TextBlock { Text = title, FontSize = 34, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) });
            col.Children.Add(new TextBlock { Text = text, FontSize = 19, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.75), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 10, 0, 0) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 34, 0, 0) };
            no = new Pill(noLabel, 190);
            yes = new Pill(yesLabel, 190);
            buttons.Children.Add(no.Root);
            buttons.Children.Add(yes.Root);
            col.Children.Add(buttons);
            panel = new Border
            {
                Child = col, Width = 640, CornerRadius = new CornerRadius(28), Padding = new Thickness(40, 40, 40, 36),
                Background = Ink.Brush("SeleneGlassColor", 0.96), BorderBrush = Ink.Brush("SeleneInkColor", 0.16), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 60, Opacity = 0.55, Color = Colors.Black }
            };
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
            if (onYesButton) { yes.Root.SetResourceReference(Border.BackgroundProperty, "WarningBrush"); yes.Root.SetResourceReference(Border.BorderBrushProperty, "WarningBrush"); }
        }

        public void Handle(string b)
        {
            if (done) return;
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
        public readonly Border Root;
        private readonly TextBlock label;
        public bool Focused { get; private set; }

        public Pill(string text, double minWidth = 200)
        {
            label = new TextBlock { Text = text, FontSize = 21, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Root = new Border
            {
                Child = label, CornerRadius = new CornerRadius(27), Height = 54, MinWidth = minWidth, Padding = new Thickness(34, 0, 34, 2),
                Background = Ink.Brush("SeleneGlassColor", 0.72), BorderBrush = Ink.Brush("SeleneInkColor", 0.22), BorderThickness = new Thickness(1),   // glass that reads over art
                Margin = new Thickness(8, 0, 8, 0)
            };
        }

        public string Text { get { return label.Text; } set { label.Text = value; } }

        public void Focus(bool on)
        {
            if (on == Focused) return;
            Focused = on;
            Root.Background = on ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.14);
            Root.BorderBrush = on ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.22);
            label.Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor");
            Root.Effect = on ? Ink.Track(new DropShadowEffect { ShadowDepth = 0, BlurRadius = 22, Opacity = 0.4 }, DropShadowEffect.ColorProperty, "SeleneFocusColor") : null;
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
    }
}
