using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Selene
{
    // Selene's own menu (replaces Playnite's list), like a console's control center: one row of solid icons along the
    // bottom over the lightly shaded home. The focused icon gets a ring and its name above it; Power opens a short list
    // (sleep, restart, shut down, exit) above its icon. Start or the menu button opens it; B or Start closes it.
    public class MenuScreen : Screen
    {
        private class Item
        {
            public string Icon, Label, Sub;   // Sub: a second, quieter line (the profile item's action)
            public FrameworkElement Picture;   // instead of an icon (the profile's avatar)
            public Action Run;
            public bool Badge;   // a red dot on the icon: something new there (a newer Selene)
        }

        private const double Cell = 96, Ring = 70;
        private readonly List<Item> items = new List<Item>();
        private readonly List<Grid> cells = new List<Grid>();
        private readonly List<Ellipse> rings = new List<Ellipse>();
        private readonly List<FrameworkElement> labels = new List<FrameworkElement>();
        private readonly List<ScaleTransform> scales = new List<ScaleTransform>();
        private readonly StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 70) };
        private readonly TextBlock status = new TextBlock { FontSize = 16, FontFamily = Ui.Body, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 30) };
        private Border powerList;
        private Border soundList;   // the Sound card: Windows' volume and which device plays
        private SliderBar soundSlider;
        private TextBlock soundOut;
        private readonly List<Border> soundRows = new List<Border>();
        private List<SystemAudio.Device> outputs = new List<SystemAudio.Device>();
        private int soundIndex;
        private ChoiceList soundPicker;   // the devices, as a list over the Output row (A)
        private Dialog ask;
        private readonly List<Border> powerRows = new List<Border>();
        private int index, powerIndex;
        private bool leaving;
        private DispatcherTimer soundRefresh;
        private bool readingSound;
        private int soundGeneration, soundRevision;
        private double shownVolume = double.NaN;
        private bool shownMute;
        private TextBlock soundSummary;

        private static readonly object[][] PowerItems =
        {
            new object[] { 0xef44, "Sleep", 0 },
            new object[] { 0xf053, "Restart", 1 },
            new object[] { 0xe8ac, "Shut down", 2 },
            new object[] { 0xe9ba, "Exit Playnite", -1 },
        };

        public override bool Overlay { get { return true; } }

        // the home stays in view: a light shade at the top, deeper toward the icons
        public override Brush OverlayShade
        {
            get
            {
                var c = ((SolidColorBrush)Ink.Brush("SeleneScrimColor")).Color;
                return new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x66, c.R, c.G, c.B), 0),
                    new GradientStop(Color.FromArgb(0x99, c.R, c.G, c.B), 0.55),
                    new GradientStop(Color.FromArgb(0xEE, c.R, c.G, c.B), 1)
                }, 90);
            }
        }

        // the item last opened from the menu: coming back to the menu (B in that screen) puts the focus on it again
        private static string lastOpened;

        public MenuScreen(bool returning = false)
        {
            Root.Unloaded += (s, e) => StopSoundRefresh();
            var profile = Profiles.Active;
            var running = Features.On("menuclosegame") ? HomeNav.RunningGame() : null;
            if (running != null) items.Add(new Item { Icon = Ui.Icon(0xe5cd), Label = "Close " + running.Name, Sub = "Close game", Run = () => Later(HomeNav.CloseRunningGame) });
            items.Add(new Item { Icon = Ui.Icon(0xe853), Label = profile != null ? profile.Name : "Switch profile", Sub = profile != null ? "Switch profile" : null, Picture = profile != null ? WhoScreen.Avatar(profile, 52) : null, Run = () => LoginScene.Show(new WhoScreen(null), 1) });
            items.Add(new Item { Icon = Ui.Icon(0xe3c9), Label = "Edit profile", Run = EditProfile });
            items.Add(new Item { Icon = Ui.Icon(0xe40a), Label = "Theme", Run = ChangeTheme });
            items.Add(new Item { Icon = Ui.Icon(0xe429), Label = "Selene settings", Badge = Background.Any, Run = () => LoginScene.Show(new SettingsScreen(true), 1) });   // (the dot: something still running behind the scenes)
            if (Features.On("soundmenu")) items.Add(new Item { Icon = Ui.Icon(0xe050), Label = "Sound", Sub = SystemAudio.Summary() ?? (Features.On("livevolume") ? "\u2014" : null), Run = OpenSound });   // Windows' volume and output
            items.Add(new Item { Icon = Ui.Icon(0xe8ac), Label = "Power", Run = OpenPower });
            // the add-ons' own menus (Playnite's Extensions menu): audio switchers, metadata tools...
            items.Add(new Item { Icon = Ui.Icon(0xe87b), Picture = Ui.Glyph(Ui.Icon(0xe87b), Ui.Icons, 34, Ink.Brush("SeleneInkColor"), true), Label = "Extensions", Run = () => LoginScene.Show(GameOptionsScreen.Extensions(), 1) });   // (the piece centred by its body, not its knobs)
            items.Add(new Item { Icon = Ui.Icon(0xe8b8), Label = "Playnite settings", Run = () => { if (Features.On("playnitesettings")) LoginScene.Show(new PlayniteSettingsScreen(), 1); else Later(PlayniteMenu.OpenSettings); } });
            if (!Features.On("startrefresh")) items.Add(new Item { Icon = Ui.Icon(0xe627), Label = "Update library", Run = UpdateLibrary });   // (it updates at every start otherwise)
            items.Add(new Item { Icon = Ui.Icon(0xe30c), Label = "Desktop mode", Run = () => Later(() => PlayniteMenu.Main("SwitchToDesktopCommand")) });
            // Update, where Setup check was (that one is in Selene settings > Advanced now): a newer Selene found opens it, else it looks
            items.Add(new Item { Icon = Ui.Icon(0xe923), Label = "Update", Sub = UpdateCheck.Available != null ? "Selene " + UpdateCheck.Available : null, Badge = UpdateCheck.Available != null,
                Run = () => { if (UpdateCheck.Available != null) LoginScene.Show(new UpdateScreen(() => new MenuScreen(true)), 1); else UpdateCheck.CheckNow(() => new MenuScreen(true)); } });

            index = items.FindIndex(i => i.Label == "Power");   // opens on Power, in the middle
            if (returning && lastOpened != null) index = Math.Max(0, items.FindIndex(i => i.Label == lastOpened));
            foreach (var item in items)
            {
                var cell = new Grid { Width = Cell, Height = Ring + 74 };
                // the name floats above the icon, wider than the cell; the profile's has its action under it
                var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                words.Children.Add(new TextBlock { Text = item.Label, FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center });
                if (item.Sub != null)
                {
                    var summary = new TextBlock { Text = item.Sub, FontSize = 14, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
                    words.Children.Add(summary);
                    if (item.Label == "Sound") soundSummary = summary;
                }
                var pill = new Border { Child = words, Padding = new Thickness(16, 7, 16, 8), HorizontalAlignment = HorizontalAlignment.Center };
                var label = new Grid
                {
                    Opacity = 0, Width = 300, Margin = new Thickness(-102, 0, -102, 0), VerticalAlignment = VerticalAlignment.Bottom,
                    RenderTransform = new TranslateTransform(0, 6)
                };
                label.Children.Add(new Border { Child = pill, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, Ring + 12) });
                var dot = new Grid { Width = Ring, Height = Ring, VerticalAlignment = VerticalAlignment.Bottom, RenderTransformOrigin = new Point(0.5, 0.5) };
                var scale = new ScaleTransform(1, 1);
                dot.RenderTransform = scale;
                var ring = new Ellipse { StrokeThickness = 3, Stroke = Ink.Brush("SeleneInkColor"), Fill = Ink.Brush("SeleneInkColor", 0.12), Opacity = 0 };
                dot.Children.Add(ring);
                if (item.Picture != null) { item.Picture.HorizontalAlignment = HorizontalAlignment.Center; item.Picture.VerticalAlignment = VerticalAlignment.Center; dot.Children.Add(item.Picture); }
                else dot.Children.Add(Ui.Glyph(item.Icon, Ui.Icons, 34, Ink.Brush("SeleneInkColor")));
                if (item.Badge)
                {
                    // the red of a console's notice dot (the same in every theme, as the trophies' metals), ringed in the dark so it
                    // reads over any wallpaper; still
                    var badge = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)), Stroke = Ink.Brush("SeleneScrimColor"), StrokeThickness = 2.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0) };
                    dot.Children.Add(badge);
                }
                cell.Children.Add(label);
                cell.Children.Add(dot);
                bar.Children.Add(cell);
                int selectedCell = cells.Count;
                cell.Background = Brushes.Transparent;
                cell.MouseLeftButtonUp += (s, e) =>
                {
                    if (leaving || ask != null || powerList != null || soundList != null) return;
                    index = selectedCell; Handle("A"); e.Handled = true;
                };
                cells.Add(cell); rings.Add(ring); labels.Add(label); scales.Add(scale);
            }
            Root.Children.Add(bar);
            status.Foreground = Ink.Brush("SeleneInkColor", 0.75);
            Root.Children.Add(status);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Close")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Select")));
        }

        public override void Shown()
        {
            if (soundList != null) StartSoundRefresh();
            // the icons rise in from the bottom one after another
            for (int i = 0; i < cells.Count; i++) Ui.Rise(cells[i], 40 + i * 25, 24, 260);
            Ui.Play("OpenPanel.wav", 0.8);
            ShowFocus(false);
        }

        public override void Hidden() { StopSoundRefresh(); }

        private void ShowFocus(bool animate)
        {
            var ms = TimeSpan.FromMilliseconds(animate ? 180 : 0);
            for (int i = 0; i < cells.Count; i++)
            {
                bool on = i == index;
                rings[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 1 : 0, ms));
                labels[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 1 : 0, ms));
                ((TranslateTransform)labels[i].RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? 0 : 6, ms) { EasingFunction = Ui.Out() });
                var grow = new DoubleAnimation(on ? 1.1 : 1, ms) { EasingFunction = Ui.Out() };
                scales[i].BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
        }

        public override void Handle(string b)
        {
            if (leaving) return;
            if (ask != null) { ask.Handle(b); return; }   // the power question is up
            if (powerList != null) { HandlePower(b); return; }
            if (soundList != null) { HandleSound(b); return; }
            if (b == "B" || b == "Start") { leaving = true; Ui.Back(); if (!LoginScene.CloseMenuOver()) LoginScene.Finish(); return; }
            if (b == "A")
            {
                var item = items[index];
                Ui.Confirm();
                lastOpened = item.Label;
                try { item.Run(); }
                catch (Exception ex) { HomeNav.Log("menu: " + item.Label + " failed: " + ex); Ui.Shake(cells[index]); }
                return;
            }
            int next = index + (b == "DPadLeft" ? -1 : b == "DPadRight" ? 1 : 0);
            if (next == index || next < 0 || next >= items.Count) return;
            index = next;
            status.Text = "";
            Ui.Tick();
            ShowFocus(true);
        }

        // ---- Power: a short list above its icon ----
        private void OpenPower()
        {
            var list = new StackPanel();
            powerRows.Clear();
            foreach (var p in PowerItems)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = Ui.Icon((int)p[0]), FontFamily = Ui.Icons, FontSize = 24, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(new TextBlock { Text = (string)p[1], FontSize = 18, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 1) });
                var row = new Border { Child = line, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10, 24, 10), Margin = new Thickness(0, 2, 0, 2) };
                list.Children.Add(row);
                powerRows.Add(row);
            }
            powerList = new Border
            {
                Child = list, CornerRadius = new CornerRadius(18), Padding = new Thickness(8), MinWidth = 240,
                Background = Ink.Brush("SeleneGlassColor", 0.96), BorderBrush = Ink.Brush("SeleneInkColor", 0.16), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 40, Opacity = 0.5, Color = Colors.Black }
            };
            // over the Power icon, its right edge lined up with the icon's
            // centred over the Power button
            var at = cells[index].TranslatePoint(new Point(Cell / 2, 0), Root);
            powerList.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            powerList.Margin = new Thickness(Math.Max(20, at.X - powerList.DesiredSize.Width / 2), 0, 0, Root.ActualHeight - at.Y + 16);
            Root.Children.Add(powerList);
            Ui.Rise(powerList, 0, 12, 200);
            Ui.Play("OpenPanel.wav", 0.8);
            powerIndex = 0;
            ShowPowerFocus();
        }

        private void ShowPowerFocus()
        {
            for (int i = 0; i < powerRows.Count; i++)
            {
                bool on = i == powerIndex;
                powerRows[i].Background = on ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
                foreach (var t in ((StackPanel)powerRows[i].Child).Children.OfType<TextBlock>())
                    t.Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor");
            }
        }

        private void HandlePower(string b)
        {
            if (b == "B") { Ui.Back(); ClosePower(); return; }
            if (b == "Start") { leaving = true; Ui.Back(); if (!LoginScene.CloseMenuOver()) LoginScene.Finish(); return; }
            if (b == "A")
            {
                Ui.Confirm();
                int what = (int)PowerItems[powerIndex][2];
                var label = (string)PowerItems[powerIndex][1];
                Action run = () =>
                {
                    try
                    {
                        // (not for Sleep: Playnite carries on after it)
                        if (what < 0) HomeNav.BeginQuit(); else if (what > 0) HomeNav.Quitting = true;
                        // the same way out as Alt+F4 (Playnite's Exit command closes its own menu first and hangs on a menu never opened: #27, #26)
                        if (what < 0) Later(() => { HomeNav.Log("menu: quit"); CloseToTray.QuitNow("power menu"); });
                        else Power.Run(what);
                    }
                    catch (Exception ex) { HomeNav.Log("menu: power failed: " + ex); Ui.Shake(powerRows[powerIndex]); }
                };
                // Ask before power actions: a question first (easy to hit by accident on a controller)
                if (what >= 0 && Features.On("powerconfirm"))
                {
                    ask = new Dialog(Ui.Icon((int)PowerItems[powerIndex][0]), label + "?", what == 0 ? "The PC goes to sleep." : what == 1 ? "The PC restarts. Anything unsaved is lost." : "The PC shuts down. Anything unsaved is lost.",
                        label, "Cancel", () => { ask = null; run(); }, () => ask = null, what != 0, true);
                    ask.Open(Root);
                    return;
                }
                run();
                return;
            }
            int next = powerIndex + (b == "DPadUp" ? -1 : b == "DPadDown" ? 1 : 0);
            if (next == powerIndex || next < 0 || next >= powerRows.Count) return;
            powerIndex = next;
            Ui.Tick();
            ShowPowerFocus();
        }

        private void ClosePower()
        {
            var list = powerList;
            powerList = null;
            Ui.Fade(list, 0, 150, 0, () => Root.Children.Remove(list));
        }

        // ---- Sound: Windows' master volume (Left / Right, X mutes) and which playback device is the default (Left / Right) ----
        private void OpenSound()
        {
            var list = new StackPanel { Width = 440 };
            soundRows.Clear();
            // the volume: the settings' slider look, its figure and line
            soundSlider = new SliderBar();
            shownVolume = double.NaN;   // a fresh control needs its initial value even when Windows volume is unchanged
            soundRows.Add(SoundRow(list, "Volume", soundSlider.Root));
            // the output: the device playing, the arrows either side say Left / Right change it
            var pick = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 6, 0) };
            pick.Children.Add(new TextBlock { Text = "\u25C2", FontSize = 14, Foreground = Ink.Brush("SeleneInkColor", 0.55), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            soundOut = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis };
            pick.Children.Add(soundOut);
            pick.Children.Add(new TextBlock { Text = "\u25B8", FontSize = 14, Foreground = Ink.Brush("SeleneInkColor", 0.55), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            soundRows.Add(SoundRow(list, "Output", pick));
            soundList = new Border
            {
                Child = list, CornerRadius = new CornerRadius(18), Padding = new Thickness(8),
                Background = Ink.Brush("SeleneGlassColor", 0.96), BorderBrush = Ink.Brush("SeleneInkColor", 0.16), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 40, Opacity = 0.5, Color = Colors.Black }
            };
            // centred over the Sound button, as the Power list is
            var at = cells[index].TranslatePoint(new Point(Cell / 2, 0), Root);
            soundList.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            soundList.Margin = new Thickness(Math.Max(20, Math.Min(Root.ActualWidth - soundList.DesiredSize.Width - 20, at.X - soundList.DesiredSize.Width / 2)), 0, 0, Root.ActualHeight - at.Y + 16);
            Root.Children.Add(soundList);
            Ui.Rise(soundList, 0, 12, 200);
            Ui.Play("OpenPanel.wav", 0.8);
            soundIndex = 0;
            outputs = SystemAudio.Outputs();
            ShowSound(false);
            ShowSoundFocus();
            StartSoundRefresh();
        }

        private static Border SoundRow(StackPanel list, string label, UIElement control)
        {
            var line = new DockPanel();
            DockPanel.SetDock(control, Dock.Right);
            line.Children.Add(control);
            line.Children.Add(new TextBlock { Text = label, FontSize = 18, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 1) });
            var row = new Border { Child = line, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 2, 0, 2) };
            list.Children.Add(row);
            return row;
        }

        private void ShowSound(bool animate)
        {
            soundRevision++;   // a local volume change takes precedence over an older background read
            double v = SystemAudio.Volume;
            bool muted = v >= 0 && SystemAudio.Muted;
            ApplySoundVolume(v, muted, animate);
            var now = outputs.Find(d => d.Default);
            if (soundOut != null) soundOut.Text = now != null ? now.Name : outputs.Count > 0 ? outputs[0].Name : "No playback device";
        }

        private void ApplySoundVolume(double volume, bool muted, bool animate)
        {
            if (volume == shownVolume && muted == shownMute) return;
            shownVolume = volume; shownMute = muted;
            if (Features.On("livevolume") && soundSummary != null) soundSummary.Text = volume < 0 ? "\u2014" : muted ? "Muted" : (int)Math.Round(volume * 100) + "%";
            if (soundSlider != null) soundSlider.Set(volume < 0 ? 0 : (int)Math.Round(volume * 20), 21,
                volume < 0 ? "\u2014" : muted ? "Muted" : (int)Math.Round(volume * 100) + "%", animate);
        }

        private void StartSoundRefresh()
        {
            if (!Features.On("livevolume") || soundList == null || leaving) return;
            if (soundRefresh == null)
            {
                soundRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
                soundRefresh.Tick += (s, e) => RefreshSound();
            }
            soundRefresh.Start();
        }

        private void StopSoundRefresh()
        {
            soundGeneration++;
            if (soundRefresh != null) soundRefresh.Stop();
        }

        private void RefreshSound()
        {
            if (soundList == null || leaving || !Root.IsVisible || !Features.On("livevolume")) { StopSoundRefresh(); return; }
            if (!ScreenPresence.InView) return;
            if (readingSound) return;
            readingSound = true;
            int generation = soundGeneration, revision = soundRevision;
            var dispatcher = Root.Dispatcher;
            // Core Audio lookup runs on a worker; no device enumeration, UI-thread polling or overlapping reads.
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                double volume = SystemAudio.Volume;
                bool muted = volume >= 0 && SystemAudio.Muted;
                if (dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    readingSound = false;
                    if (generation != soundGeneration || revision != soundRevision || soundList == null || leaving || !Root.IsVisible || !ScreenPresence.InView || !Features.On("livevolume")) return;
                    ApplySoundVolume(volume, muted, false);
                }), DispatcherPriority.Background);
            });
        }

        private void ShowSoundFocus()
        {
            for (int i = 0; i < soundRows.Count; i++)
            {
                bool on = i == soundIndex;
                soundRows[i].Background = on ? Ink.Brush("SeleneInkColor", 0.1) : Brushes.Transparent;
                soundRows[i].BorderBrush = on ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
                soundRows[i].BorderThickness = new Thickness(2);
            }
        }

        private void HandleSound(string b)
        {
            if (soundPicker != null && soundPicker.IsOpen) { soundPicker.Handle(b); return; }   // the devices' list has the buttons
            if (b == "B") { Ui.Back(); CloseSound(); return; }
            if (b == "Start") { leaving = true; StopSoundRefresh(); Ui.Back(); if (!LoginScene.CloseMenuOver()) LoginScene.Finish(); return; }
            if (b == "DPadUp" || b == "DPadDown")
            {
                int next = soundIndex + (b == "DPadUp" ? -1 : 1);
                if (next < 0 || next >= soundRows.Count) return;
                soundIndex = next;
                Ui.Tick();
                ShowSoundFocus();
                return;
            }
            if (b == "X" || (b == "A" && soundIndex == 0))
            {
                if (SystemAudio.Volume < 0) return;
                SystemAudio.Muted = !SystemAudio.Muted;   // the mute, either way
                Ui.Key();
                ShowSound(true);
                return;
            }
            if (b == "A" && soundIndex == 1)
            {
                // the devices as a list over the row; picking one makes it the default
                if (outputs.Count == 0) { Ui.Shake(soundRows[1]); return; }
                Ui.Confirm();
                var names = new List<string>();
                foreach (var d in outputs) names.Add(d.Name);
                int now = Math.Max(0, outputs.FindIndex(d => d.Default));
                soundPicker = ChoiceList.Open(Root, soundRows[1], names, now, k =>
                {
                    if (k == now || k < 0 || k >= outputs.Count) return;
                    if (SystemAudio.SetDefault(outputs[k].Id)) { foreach (var d in outputs) d.Default = false; outputs[k].Default = true; HomeNav.Log("audio: output -> " + outputs[k].Name); }
                    else Ui.Shake(soundRows[1]);
                    ShowSound(true);
                }, true);   // (over the card's middle)
                return;
            }
            if (b == "DPadLeft" || b == "DPadRight")
            {
                int dir = b == "DPadLeft" ? -1 : 1;
                if (soundIndex == 0)
                {
                    double v = SystemAudio.Volume;
                    if (v < 0) return;
                    double to = Math.Max(0, Math.Min(1, Math.Round(v * 20) / 20 + dir * 0.05));   // 5% a step
                    if (Math.Abs(to - v) < 0.001 && !SystemAudio.Muted) return;   // the end of the line
                    SystemAudio.Volume = to;
                    if (SystemAudio.Muted) SystemAudio.Muted = false;   // a turn of the volume unmutes, as Windows does
                    Ui.Tick();
                    ShowSound(true);
                }
                else
                {
                    if (outputs.Count < 2) { Ui.Shake(soundRows[1]); return; }
                    int at = outputs.FindIndex(d => d.Default);
                    int next = ((at < 0 ? 0 : at) + dir + outputs.Count) % outputs.Count;
                    if (SystemAudio.SetDefault(outputs[next].Id))
                    {
                        foreach (var d in outputs) d.Default = false;
                        outputs[next].Default = true;
                        Ui.Tick();
                        ShowSound(true);
                        HomeNav.Log("audio: output -> " + outputs[next].Name);
                    }
                    else Ui.Shake(soundRows[1]);
                }
            }
        }

        private void CloseSound()
        {
            StopSoundRefresh();
            var list = soundList;
            soundList = null;
            Ui.Fade(list, 0, 150, 0, () => Root.Children.Remove(list));
        }

        // Playnite's own windows open once the menu has faded away
        // the tail of Selene's and Playnite's logs to the clipboard, for a bug report
        internal static void CopyLog()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Selene " + (HomeNav.Api != null ? typeof(MenuScreen).Assembly.GetName().Version.ToString() : "") + " on Playnite " + (HomeNav.Api != null ? HomeNav.Api.ApplicationInfo.ApplicationVersion.ToString() : "") + ", Windows " + Environment.OSVersion.Version);
                foreach (var pair in new[] { new[] { "selene.log", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene.log"), "500" },
                                             new[] { "playnite.log", System.IO.Path.Combine(HomeNav.Api != null ? HomeNav.Api.Paths.ConfigurationPath : "", "playnite.log"), "200" } })
                {
                    sb.AppendLine().AppendLine("==== " + pair[0] + " (last " + pair[2] + " lines) ====");
                    if (!System.IO.File.Exists(pair[1])) { sb.AppendLine("(not found)"); continue; }
                    string[] lines;
                    using (var fs = new System.IO.FileStream(pair[1], System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    using (var sr = new System.IO.StreamReader(fs)) lines = sr.ReadToEnd().Split('\n');
                    int n = int.Parse(pair[2]);
                    for (int i = Math.Max(0, lines.Length - n); i < lines.Length; i++) sb.AppendLine(lines[i].TrimEnd('\r'));
                }
                Clipboard.SetText(sb.ToString());
                Ui.Confirm();
                Ui.Notice("Log copied. Paste it into a GitHub issue.");
            }
            catch (Exception ex) { HomeNav.Log("copy log: " + ex.Message); Ui.Notice("Couldn't copy the log."); }
        }

        private void Later(Action run)
        {
            leaving = true;
            LoginScene.Finish();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try { run(); }
                catch (Exception ex) { HomeNav.Log("menu action failed: " + ex); }
            };
            t.Start();
        }

        // starts Playnite's library update in the background (from the hidden Playnite menu it waited behind one already
        // running); says what happens under the icons, then closes
        private void UpdateLibrary()
        {
            string busy;
            bool started = HomeNav.UpdateLibraryNow(out busy);
            status.Text = started ? "Updating your library in the background." : "Already updating: " + busy;
            if (!started) { Ui.Shake(cells[index]); return; }
            leaving = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (s, e) => { t.Stop(); LoginScene.Finish(); };
            t.Start();
        }

        // straight to the signed-in profile's theme (then its background), saved at the end
        private static void ChangeTheme()
        {
            var p = Profiles.Active;
            if (p == null) { LoginScene.Show(new WhoScreen(null), 1); return; }
            LoginScene.Show(new ProfileSteps(p, () => new MenuScreen(true), q => LoginScene.Finish(), 2), 1);
        }

        private static void EditProfile()
        {
            var p = Profiles.Active;
            if (p == null) { LoginScene.Show(new WhoScreen(null), 1); return; }
            LoginScene.Show(new ProfileSteps(p, () => new MenuScreen(true), q => LoginScene.Finish()), 1);
        }
    }

    // Playnite's own actions, reached through its view models (no public API for these)
    public static class PlayniteMenu
    {
        private static object menuModel;
        // Playnite's own "open main menu", kept when Selene's menu takes its place (Selene doesn't open it)
        public static ICommand Original;

        private static object MainModel { get { return Application.Current.MainWindow != null ? Application.Current.MainWindow.DataContext : null; } }

        private static Assembly App { get { var vm = MainModel; return vm != null ? vm.GetType().Assembly : null; } }

        // a command on the main view model (FullscreenAppViewModel)
        public static void Main(string command)
        {
            var vm = MainModel;
            var c = vm != null ? vm.GetType().GetProperty(command).GetValue(vm, null) as ICommand : null;
            if (c == null) throw new InvalidOperationException("Playnite has no " + command);
            c.Execute(null);
        }

        // a command of Playnite's main menu (MainMenuViewModel, made with its real window factory but never shown)
        public static void Run(string command)
        {
            if (menuModel == null)
            {
                var factory = Activator.CreateInstance(App.GetType("Playnite.FullscreenApp.Windows.MainMenuWindowFactory"));
                menuModel = Activator.CreateInstance(App.GetType("Playnite.FullscreenApp.ViewModels.MainMenuViewModel"), factory, MainModel);
            }
            var c = menuModel.GetType().GetProperty(command).GetValue(menuModel, null) as ICommand;
            if (c == null) throw new InvalidOperationException("Playnite's menu has no " + command);
            try { c.Execute(null); }
            catch (NullReferenceException ex) { HomeNav.Log("menu: " + command + " (closing the hidden menu): " + ex.Message); }
        }

        public static void OpenSettings()
        {
            var vmType = App.GetType("Playnite.FullscreenApp.ViewModels.SettingsViewModel");
            var factory = Activator.CreateInstance(App.GetType("Playnite.FullscreenApp.Windows.SettingsWindowFactory"));
            var settings = Activator.CreateInstance(vmType, factory, MainModel);
            vmType.GetMethod("OpenView").Invoke(settings, null);
        }
    }

    // Sleep / restart / shut down: the optional companion helper does it its own way (e.g. TV off first), else Windows
    public static class Power
    {
        private static readonly string[][] Items = {
            new[] { "power-sleep", "rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0" },
            new[] { "power-restart", "shutdown.exe", "/r /t 0" },
            new[] { "power-off", "shutdown.exe", "/s /t 0" } };

        [System.Runtime.InteropServices.DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        public static void Run(int i)
        {
            var item = Items[i];
            HomeNav.Log("power: " + item[0]);
            if (HomeNav.Isolated) { HomeNav.Log("dev isolated: " + item[0] + " NOT done (a test copy never sleeps, restarts or shuts down the PC)"); HomeNav.Quitting = false; return; }
            var helper = SelenePaths.Helper;
            bool viaHelper = !string.IsNullOrEmpty(helper) && System.IO.File.Exists(helper);
            // sleep straight through Windows: "rundll32 powrprof.dll,SetSuspendState" hibernates instead on a PC with
            // hibernation on (rundll32 hands the function the wrong arguments)
            if (i == 0 && !viaHelper) { System.Threading.Tasks.Task.Run(() => SetSuspendState(false, false, false)); return; }
            var start = viaHelper
                ? new System.Diagnostics.ProcessStartInfo(helper, item[0])
                : new System.Diagnostics.ProcessStartInfo(item[1], item[2]);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            System.Diagnostics.Process.Start(start);
        }
    }
}
