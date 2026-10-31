using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Selene
{
    // Playnite's own fullscreen settings (General, Visuals, Audio, Layout, Menus, Input) as a Selene screen: the same shape
    // as Selene settings, switches and choices on glass rows, written straight into Playnite's settings and saved. The few
    // things that need Playnite's own pages (the monitor, the theme) are a row away.
    public class PlayniteSettingsScreen : Screen
    {
        private class Row
        {
            public string Label, About, Prop;
            public string[] Values, Names;   // a choice: the values as text (parsed into the property's type) and how they read
            public Action Run;               // an action row instead
            public bool IsChoice { get { return Values != null; } }
            public bool Slider;              // a number along a line (Left / Right) instead of a list to pick from
        }

        private class Section { public string Name; public int Icon; public List<Row> Rows = new List<Row>(); }

        private static readonly List<Section> Sections = Build();

        private static Row Switch(string prop, string label, string about) { return new Row { Prop = prop, Label = label, About = about }; }
        private static Row Choice(string prop, string label, string about, string[] values, string[] names) { return new Row { Prop = prop, Label = label, About = about, Values = values, Names = names }; }
        private static Row Slider(string prop, string label, string about, string[] values, string[] names) { return new Row { Prop = prop, Label = label, About = about, Values = values, Names = names, Slider = true }; }

        private static List<Section> Build()
        {
            var list = new List<Section>();
            var general = new Section { Name = "General", Icon = 0xe8b8 };
            general.Rows.Add(Switch("UsePrimaryDisplay", "Open on the main display", "Fullscreen mode opens on Windows' main display."));
            general.Rows.Add(Switch("HideMouserCursor", "Hide the mouse pointer", "The pointer stays hidden while you use a controller."));
            general.Rows.Add(Switch("MinimizeAfterGameStartup", "Minimize when a game starts", "Playnite gets out of the way as a game starts."));
            general.Rows.Add(Switch("ShowClock", "Clock", "The time in the top bar."));
            general.Rows.Add(Switch("ShowBattery", "Controller battery", "The controller's battery in the top bar, as a percentage."));
            general.Rows.Add(Choice("ButtonPrompts", "Button prompts", "Which controller's buttons the hints show.", new[] { "Xbox", "PlayStation" }, null));
            general.Rows.Add(Switch("AsyncImageLoading", "Load pictures in the background", "Lists stay smooth while their pictures load."));
            general.Rows.Add(Switch("SmoothScrolling", "Smooth scrolling", "Lists glide instead of stepping."));
            general.Rows.Add(Choice("ListNavigationSpeed", "Held-button speed", "How fast lists move while a direction is held.", new[] { "50", "100", "150", "200", "300" }, new[] { "Fast", "Normal", "Relaxed", "Slow", "Slowest" }));
            general.Rows.Add(new Row { Label = "Playnite's own settings", About = "The monitor, the theme and everything else, on Playnite's pages.", Run = () => LoginScene.Finish(), Prop = "open-playnite" });
            list.Add(general);

            var visuals = new Section { Name = "Visuals", Icon = 0xe40a };
            visuals.Rows.Add(Switch("EnableMainBackgroundImage", "Game art behind the library", "The selected game's art behind Playnite's own views."));
            visuals.Rows.Add(Choice("MainBackgroundImageBlurAmount", "Blur on that art", "How much the art behind is blurred.", new[] { "0", "20", "40", "60", "80" }, new[] { "None", "Light", "Medium", "Strong", "Strongest" }));
            visuals.Rows.Add(Choice("MainBackgroundImageDarkAmount", "Darkening of that art", "How much the art behind is darkened.", new[] { "0", "15", "30", "50", "70" }, new[] { "None", "A little", "Some", "Much", "Most" }));
            visuals.Rows.Add(Switch("DarkenUninstalledGamesGrid", "Dim games that aren't installed", "Uninstalled games show darker in Collection."));
            visuals.Rows.Add(Switch("ShowGameTitles", "Names under the tiles", "Each game's name under its tile in Collection."));
            visuals.Rows.Add(Slider("FontSize", "Text size", "Playnite's main text size.", new[] { "18", "20", "22", "24", "26", "28" }, null));
            visuals.Rows.Add(Slider("FontSizeSmall", "Small text size", "Playnite's smaller text.", new[] { "14", "16", "18", "20", "22" }, null));
            visuals.Rows.Add(Choice("ImageScalerMode", "Picture scaling", "How tile pictures are scaled down.", new[] { "None", "BitmapDotNet", "Custom" }, new[] { "Off", "Windows", "Playnite" }));
            list.Add(visuals);

            var audio = new Section { Name = "Audio", Icon = 0xe050 };
            audio.Rows.Add(Slider("InterfaceVolume", "Interface volume", "Playnite's own interface sounds.", new[] { "0", "0.25", "0.5", "0.75", "1" }, new[] { "Off", "25%", "50%", "75%", "100%" }));
            audio.Rows.Add(Switch("MuteInBackground", "Quiet in the background", "Sounds and music stop when Playnite isn't in front."));
            list.Add(audio);

            var layout = new Section { Name = "Layout", Icon = 0xe871 };
            layout.Rows.Add(Slider("Rows", "Rows in Collection", "How many rows of tiles Collection shows.", new[] { "1", "2", "3", "4", "5", "6" }, null));
            // (no Columns: on Selene it doesn't size the covers, and a low value left Collection's row empty past a few games)
            layout.Rows.Add(Switch("HorizontalLayout", "Scroll sideways", "Collection scrolls left and right instead of up and down."));
            list.Add(layout);

            var menus = new Section { Name = "Menus", Icon = 0xe5d2 };
            menus.Rows.Add(Switch("MainMenuShowRestart", "Restart", "A Restart item in Playnite's menu."));
            menus.Rows.Add(Switch("MainMenuShowShutdown", "Shut down", "A Shut down item."));
            menus.Rows.Add(Switch("MainMenuShowSuspend", "Sleep", "A Sleep item."));
            menus.Rows.Add(Switch("MainMenuShowHibernate", "Hibernate", "A Hibernate item."));
            menus.Rows.Add(Switch("MainMenuShowMinimize", "Minimize", "A Minimize item."));
            menus.Rows.Add(Switch("MainMenuShowLogout", "Sign out", "A Sign out of Windows item."));
            menus.Rows.Add(Switch("MainMenuShowLock", "Lock", "A Lock Windows item."));
            menus.Rows.Add(Switch("MainMenuShowTools", "Tools", "Playnite's tools in its menu."));
            menus.Rows.Add(Switch("MainMenuShowExtensions", "Extensions", "Extensions' items in its menu."));
            menus.Rows.Add(Switch("MainMenuShowClients", "Store clients", "The store apps in its menu."));
            list.Add(menus);

            var input = new Section { Name = "Input", Icon = 0xea28 };
            input.Rows.Add(Switch("EnableGameControllerSupport", "Controller", "Playnite listens to controllers."));
            input.Rows.Add(Switch("GuideButtonFocus", "Guide brings Playnite up", "The Guide button brings Playnite to the front."));
            input.Rows.Add(Switch("GuideButtonFocusOnlyWhenNoGameRunning", "…only when no game runs", "Guide leaves a running game alone."));
            input.Rows.Add(Switch("SwapStartDetailsAction", "Swap A and Start on a game", "Start plays, A opens details (or the other way round)."));
            input.Rows.Add(Switch("SwapConfirmCancelButtons", "Swap A and B", "B confirms and A goes back."));
            list.Add(input);
            return list;
        }

        // ---------------- Playnite's settings object ----------------
        private static object fullscreen, appSettings;

        private static object Fullscreen()
        {
            if (fullscreen != null) return fullscreen;
            try
            {
                var appType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Playnite.PlayniteApplication", false)).FirstOrDefault(t => t != null);
                var current = appType != null ? appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static) : null;
                var app = current != null ? current.GetValue(null, null) : null;
                var settingsProp = app != null ? app.GetType().GetProperty("AppSettings") : null;
                appSettings = settingsProp != null ? settingsProp.GetValue(app, null) : null;
                var fs = appSettings != null ? appSettings.GetType().GetProperty("Fullscreen") : null;
                fullscreen = fs != null ? fs.GetValue(appSettings, null) : null;
                if (fullscreen == null) HomeNav.Log("playnite settings: not reachable");
            }
            catch (Exception ex) { HomeNav.Log("playnite settings: " + ex.Message); }
            return fullscreen;
        }

        private static object Get(string prop)
        {
            var fs = Fullscreen();
            var p = fs != null ? fs.GetType().GetProperty(prop) : null;
            return p != null ? p.GetValue(fs, null) : null;
        }

        private static void Set(string prop, object value)
        {
            var fs = Fullscreen();
            var p = fs != null ? fs.GetType().GetProperty(prop) : null;
            if (p == null || !p.CanWrite) return;
            try
            {
                var t = p.PropertyType;
                object v = value;
                if (t.IsEnum) v = Enum.Parse(t, value.ToString());
                else if (t != typeof(string)) v = Convert.ChangeType(Convert.ToDouble(value, CultureInfo.InvariantCulture), t, CultureInfo.InvariantCulture);
                p.SetValue(fs, v, null);
                Ui.RefreshPrompts();
                var save = appSettings.GetType().GetMethod("SaveSettings", System.Type.EmptyTypes);
                if (save != null) save.Invoke(appSettings, null);
                HomeNav.Log("playnite settings: " + prop + " = " + v);
            }
            catch (Exception ex) { HomeNav.Log("playnite settings: " + prop + ": " + ex.Message); }
        }

        private static bool On(string prop) { var v = Get(prop); return v is bool && (bool)v; }

        private static string Text(object v)
        {
            if (v == null) return "";
            if (v is float || v is double) return Convert.ToDouble(v).ToString("0.##", CultureInfo.InvariantCulture);
            return v.ToString();
        }

        private static int ChoiceIndex(Row r)
        {
            var now = Text(Get(r.Prop));
            int i = Array.IndexOf(r.Values, now);
            if (i >= 0) return i;
            // not one of the offered values: the nearest number
            double d;
            if (!double.TryParse(now, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
            int best = 0; double gap = double.MaxValue;
            for (int k = 0; k < r.Values.Length; k++) { double x; if (double.TryParse(r.Values[k], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && Math.Abs(x - d) < gap) { gap = Math.Abs(x - d); best = k; } }
            return best;
        }

        private static string ChoiceName(Row r) { int i = ChoiceIndex(r); return r.Names != null ? r.Names[i] : r.Values[i]; }

        // ---------------- the screen ----------------
        private readonly List<Border> groupRows = new List<Border>();
        private readonly StackPanel optionList = new StackPanel();
        private readonly TextBlock groupTitle;
        private readonly List<Border> rows = new List<Border>();
        private readonly List<Border> knobs = new List<Border>();
        private readonly List<Border> tracks = new List<Border>();
        private readonly List<TextBlock> values = new List<TextBlock>();
        private readonly List<SliderBar> sliders = new List<SliderBar>();
        private ChoiceList picker;
        private List<Row> options = new List<Row>();
        private int group, index;
        private bool inOptions;

        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.94); } }

        public PlayniteSettingsScreen()
        {
            var layout = new Grid { Margin = new Thickness(120, 110, 120, 110) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new StackPanel();
            {
                // Playnite's own logo beside the title (these are its settings), with a soft breathing halo like Selene's mark
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 30) };
                var mark = PlayniteMark(76);
                mark.Margin = new Thickness(0, 0, 18, 0);
                head.Children.Add(mark);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(new TextBlock { Text = "Settings", FontSize = 40, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor") });
                words.Children.Add(new TextBlock { Text = "Playnite", FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), Margin = new Thickness(2, 2, 0, 0) });
                head.Children.Add(words);
                left.Children.Add(head);
            }
            foreach (var s in Sections)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = Ui.Icon(s.Icon), FontFamily = Ui.Icons, FontSize = 24, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
                line.Children.Add(new TextBlock { Text = s.Name, FontSize = 21, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
                var row = new Border { Child = line, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 14, 20, 14), Margin = new Thickness(0, 0, 24, 6) };
                left.Children.Add(row);
                groupRows.Add(row);
            }
            layout.Children.Add(left);
            var right = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
            groupTitle = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), Margin = new Thickness(8, 14, 0, 16) };
            right.Children.Add(groupTitle);
            var scroller = new ScrollViewer { Content = optionList, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 715, Focusable = false };
            right.Children.Add(PeekScroller.Wrap(scroller, optionList));   // the rows fade into the dark at the edges
            Grid.SetColumn(right, 1);
            layout.Children.Add(right);
            Root.Children.Add(layout);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Select")));
            this.scroller = scroller;
        }

        private readonly ScrollViewer scroller;

        // Playnite's logo (its default theme's picture, next to Playnite itself); nothing when it can't be found
        private static FrameworkElement PlayniteMark(double size)
        {
            var box = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            try
            {
                var home = System.IO.Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                string file = null;
                foreach (var candidate in new[] { System.IO.Path.Combine(home, "Themes", "Fullscreen", "Default", "Images", "applogo_clean.png"), System.IO.Path.Combine(home, "Themes", "Fullscreen", "Default", "Images", "applogo.png"), System.IO.Path.Combine(home, "Themes", "Desktop", "Default", "Images", "applogo.png") })
                    if (System.IO.File.Exists(candidate)) { file = candidate; break; }
                if (file == null) return box;
                var halo = new System.Windows.Shapes.Ellipse { Width = size * 1.15, Height = size * 1.15, Opacity = 0.45, Fill = new RadialGradientBrush(new GradientStopCollection { Ink.Stop("GlyphColor", 0.45, 0), Ink.Stop("GlyphColor", 0, 1) }), Margin = new Thickness(-size * 0.075) };
                halo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(2200)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                box.Children.Add(halo);
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = (int)(size * 2);
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                var img = new Image { Source = bmp, Width = size * 0.78, Height = size * 0.78, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
                box.Children.Add(img);
            }
            catch (Exception ex) { HomeNav.Log("playnite mark: " + ex.Message); }
            return box;
        }

        public override void Shown()
        {
            for (int i = 0; i < groupRows.Count; i++) Ui.Rise(groupRows[i], 40 + i * 40, 12, 260);
            Ui.Play("OpenPanel.wav", 0.8);
            if (Fullscreen() == null) groupTitle.Text = "PLAYNITE'S SETTINGS AREN'T REACHABLE";
            ShowGroup(false);
            ShowFocus();
        }

        private void ShowGroup(bool animate)
        {
            options = Sections[group].Rows;
            groupTitle.Text = Sections[group].Name.ToUpperInvariant();
            optionList.Children.Clear(); rows.Clear(); knobs.Clear(); tracks.Clear(); values.Clear(); sliders.Clear();
            for (int i = 0; i < options.Count; i++)
            {
                var f = options[i];
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = f.Label, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor") });
                text.Children.Add(new TextBlock { Text = f.About, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                Border knob = null, track;
                TextBlock value = null;
                SliderBar slider = null;
                if (f.Run != null)
                {
                    track = new Border { CornerRadius = new CornerRadius(17), Padding = new Thickness(14, 4, 14, 5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0), Background = Ink.Brush("SeleneInkColor", 0.12), Child = new TextBlock { Text = "Open", FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor") } };
                }
                else if (f.IsChoice && f.Slider)
                {
                    slider = new SliderBar();   // a number: a line with a knob (Left / Right)
                    track = slider.Root;
                    value = slider.Value;
                }
                else if (f.IsChoice)
                {
                    track = ChoiceLook.Pill(out value);   // a choice: A opens the list of them
                }
                else
                {
                    knob = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0), Background = Ink.Brush("SeleneInkColor"), RenderTransform = new TranslateTransform() };
                    track = new Border { Width = 56, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center, Child = knob, Margin = new Thickness(24, 0, 0, 0) };
                }
                var line = new DockPanel();
                DockPanel.SetDock(track, Dock.Right);
                line.Children.Add(track);
                line.Children.Add(text);
                var row = new Border { Child = line, CornerRadius = new CornerRadius(16), Padding = new Thickness(26, 18, 24, 18), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Ink.Brush("SeleneInkColor", 0.06) };
                optionList.Children.Add(row);
                rows.Add(row); knobs.Add(knob); tracks.Add(track); values.Add(value); sliders.Add(slider);
                ShowValue(i, false);
                if (animate) Ui.Rise(row, i * 40, 10, 220);
            }
        }

        private void ShowValue(int i, bool animate)
        {
            var f = options[i];
            if (f.Run != null) return;
            if (f.IsChoice && sliders[i] != null) { sliders[i].Set(ChoiceIndex(f), f.Values.Length, ChoiceName(f), animate); return; }
            if (f.IsChoice) { values[i].Text = ChoiceName(f); if (animate) Ui.Pop(tracks[i]); return; }
            bool on = On(f.Prop);
            var slide = new DoubleAnimation(on ? 26 : 0, TimeSpan.FromMilliseconds(animate ? 200 : 0)) { EasingFunction = Ui.Out() };
            ((TranslateTransform)knobs[i].RenderTransform).BeginAnimation(TranslateTransform.XProperty, slide);
            tracks[i].Background = on ? Ink.Brush("GlyphColor") : Ink.Brush("SeleneInkColor", 0.2);
            knobs[i].Background = on ? Ink.Brush("SeleneScrimColor") : Ink.Brush("SeleneInkColor");
        }

        private void ShowFocus()
        {
            for (int i = 0; i < groupRows.Count; i++)
            {
                bool chosen = i == group;
                groupRows[i].Background = chosen ? (inOptions ? Ink.Brush("SeleneInkColor", 0.1) : Ink.Brush("SeleneFocusColor")) : Brushes.Transparent;
                foreach (var t in ((StackPanel)groupRows[i].Child).Children.OfType<TextBlock>())
                    t.Foreground = chosen && !inOptions ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", chosen ? 1 : 0.7);
            }
            for (int i = 0; i < rows.Count; i++) rows[i].BorderBrush = inOptions && i == index ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
            // the focused row in view
            if (inOptions && index < rows.Count)
            {
                try
                {
                    var row = rows[index];
                    var top = row.TranslatePoint(new Point(0, 0), optionList).Y;
                    double want = scroller.VerticalOffset;
                    // the focused row clear of the peek bands (it sat half faded at the edge)
                    double pad = PeekScroller.Band;
                    if (top - pad * 0.6 < scroller.VerticalOffset) want = Math.Max(0, top - pad * 0.6);
                    else if (top + row.ActualHeight > scroller.VerticalOffset + scroller.ViewportHeight - pad) want = top + row.ActualHeight - (scroller.ViewportHeight - pad);
                    if (Math.Abs(want - scroller.VerticalOffset) > 0.5) scroller.ScrollToVerticalOffset(want);
                }
                catch { }
            }
        }

        public override void Handle(string b)
        {
            if (picker != null && picker.IsOpen) { picker.Handle(b); return; }   // a choice's list has the buttons
            if (!inOptions)
            {
                if (b == "B") { Ui.Back(); LoginScene.Show(new MenuScreen(true), -1); return; }
                if ((b == "A" || b == "DPadRight") && options.Count > 0) { inOptions = true; index = 0; Ui.Tick(); ShowFocus(); return; }
                int next = group + (b == "DPadDown" ? 1 : b == "DPadUp" ? -1 : 0);
                if (next == group || next < 0 || next >= groupRows.Count) return;
                group = next;
                Ui.Tick();
                ShowGroup(true);
                ShowFocus();
                return;
            }
            var opt = options[index];
            if (b == "B" || (b == "DPadLeft" && !(opt.IsChoice && opt.Slider))) { inOptions = false; Ui.Back(); ShowFocus(); return; }
            if (opt.Run != null)
            {
                if (b != "A") { Move(b); return; }
                Ui.Confirm();
                if (opt.Prop == "open-playnite") { LoginScene.Finish(); var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; later.Tick += (s, e) => { later.Stop(); PlayniteMenu.OpenSettings(); }; later.Start(); }
                else opt.Run();
                return;
            }
            if (opt.IsChoice && opt.Slider && (b == "DPadLeft" || b == "DPadRight"))
            {
                int i = ChoiceIndex(opt) + (b == "DPadLeft" ? -1 : 1);
                if (i < 0 || i >= opt.Values.Length) return;   // the end of the line
                Set(opt.Prop, opt.Values[i]);
                Ui.Tick();
                ShowValue(index, true);
                return;
            }
            if (opt.IsChoice && b == "A" && !opt.Slider)
            {
                int row = index;
                picker = ChoiceList.Open(Root, tracks[row], opt.Names ?? opt.Values, ChoiceIndex(opt), k =>
                {
                    Set(opt.Prop, opt.Values[k]);
                    ShowValue(row, true);
                });
                return;
            }
            if (opt.IsChoice && (b == "DPadRight" || b == "A")) return;
            if (b == "A")
            {
                Set(opt.Prop, !On(opt.Prop));
                if (opt.Prop == "ShowBattery") Set("ShowBatteryPercentage", On(opt.Prop));   // the number goes with the symbol
                Ui.Key();
                ShowValue(index, true);
                return;
            }
            Move(b);
        }

        private void Move(string b)
        {
            int n = index + (b == "DPadDown" ? 1 : b == "DPadUp" ? -1 : 0);
            if (n == index || n < 0 || n >= rows.Count) return;
            index = n;
            Ui.Tick();
            ShowFocus();
        }
    }
}
