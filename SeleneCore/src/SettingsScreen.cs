using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Selene
{
    // Selene settings, like a console's settings screen: the groups on the left (Start-up, Navigation, Sound & music, Look,
    // Games), the chosen group's switches on the right. Up/Down picks a group; Right or A goes into its switches, where A
    // flips one (saved straight away); Left or B goes back to the groups; B there goes back to the menu.
    public class SettingsScreen : Screen
    {
        private static readonly int[] GroupIcons = { 0xEB9B /* power */, 0xE88A /* home */, 0xE8F1 /* grid */, 0xE7F4 /* notifications */, 0xE050 /* volume */, 0xE40A /* palette */, 0xE338 /* games */, 0xE869 /* build */ };

        private readonly List<Border> groupRows = new List<Border>();
        private readonly StackPanel optionList = new StackPanel();
        private ScrollViewer scroller;
        private readonly TextBlock groupTitle;
        private readonly List<Border> rows = new List<Border>();
        private readonly List<Border> knobs = new List<Border>();
        private readonly List<Border> tracks = new List<Border>();
        private readonly List<TextBlock> values = new List<TextBlock>();
        private readonly List<SliderBar> sliders = new List<SliderBar>();   // a number row's line (null on the others)
        private ChoiceList picker;   // a choice's list, open over its value   // a choice row's current value (null on switch rows)
        private List<Features.Option> options = new List<Features.Option>();
        private int group, index;
        private bool inOptions;

        public override bool Overlay { get { return true; } }

        // a deep, even shade: the settings read clearly over anything
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.94); } }

        // opened from the menu: B goes back to it (on this item); held Start from the home: B goes back to the home
        private readonly bool fromMenu;

        public SettingsScreen(bool fromMenu = false, string startGroup = null)
        {
            this.fromMenu = fromMenu;
            group = Math.Max(0, Array.IndexOf(Features.Groups, startGroup ?? ""));
            var layout = new Grid { Margin = new Thickness(120, 110, 120, 110) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // left: title and the groups
            var left = new StackPanel();
            {
                // Selene's own page: its mark, glowing, beside the title; the version under it
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 30) };
                var mark = Brand.Mark(76, true);
                mark.Margin = new Thickness(0, 0, 18, 0);
                head.Children.Add(mark);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(new TextBlock { Text = "Settings", FontSize = 40, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor") });
                words.Children.Add(new TextBlock { Text = "Selene " + Brand.Version, FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), Margin = new Thickness(2, 2, 0, 0) });
                head.Children.Add(words);
                left.Children.Add(head);
            }
            for (int i = 0; i < Features.Groups.Length; i++)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = Ui.Icon(GroupIcons[i]), FontFamily = Ui.Icons, FontSize = 24, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
                line.Children.Add(new TextBlock { Text = Features.Groups[i], FontSize = 21, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
                var row = new Border { Child = line, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 14, 20, 14), Margin = new Thickness(0, 0, 24, 6) };
                left.Children.Add(row);
                groupRows.Add(row);
            }
            layout.Children.Add(left);

            // right: the chosen group's switches
            var right = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
            groupTitle = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), Margin = new Thickness(8, 14, 0, 16) };
            right.Children.Add(groupTitle);
            // the rows scroll (a long group ran off the screen, and the focus went on into rows you couldn't see)
            scroller = new ScrollViewer { Content = optionList, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 715, Focusable = false, CanContentScroll = false };
            right.Children.Add(PeekScroller.Wrap(scroller, optionList));   // the rows past the edge peek in, blurred, fading into the dark
            Grid.SetColumn(right, 1);
            layout.Children.Add(right);

            Root.Children.Add(layout);
            // Y copies the logs for a bug report (it was an entry of its own in the menu)
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back"), Ui.Prompt("Y", "Copy log")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Select")));
        }

        public override void Shown()
        {
            for (int i = 0; i < groupRows.Count; i++) Ui.Rise(groupRows[i], 40 + i * 40, 12, 260);
            Ui.Play("OpenPanel.wav", 0.8);
            ShowGroup(false);
            ShowFocus();
        }

        // the switches of the chosen group, rising in
        private void ShowGroup(bool animate)
        {
            options = Features.All.Where(o => o.Group == Features.Groups[group]).ToList();
            groupTitle.Text = Features.Groups[group].ToUpperInvariant();
            optionList.Children.Clear(); rows.Clear(); knobs.Clear(); tracks.Clear(); values.Clear(); sliders.Clear();
            for (int i = 0; i < options.Count; i++)
            {
                var f = options[i];
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = f.Label, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor") });
                text.Children.Add(new TextBlock { Text = f.About, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                var knob = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0), Background = Brushes.White, RenderTransform = new TranslateTransform() };
                var track = new Border { Width = 56, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center, Child = knob, Margin = new Thickness(24, 0, 0, 0) };
                TextBlock value = null;
                SliderBar slider = null;
                if (f.Action != null)
                {
                    // a row that does something: its button's label in a pill
                    track = new Border { CornerRadius = new CornerRadius(17), Padding = new Thickness(16, 4, 16, 5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0), Background = Ink.Brush("SeleneInkColor", 0.12), Child = new TextBlock { Text = f.Action, FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor") } };
                    knob = null;
                }
                else if (f.IsChoice && f.Slider)
                {
                    // a number: a line with a knob (Left / Right)
                    slider = new SliderBar();
                    track = slider.Root;
                    value = slider.Value;
                    knob = null;
                }
                else if (f.IsChoice)
                {
                    // a choice: its value; A opens the list of them
                    track = ChoiceLook.Pill(out value);
                    knob = null;
                }
                var line = new DockPanel();
                DockPanel.SetDock(track, Dock.Right);
                line.Children.Add(track);
                line.Children.Add(text);
                var row = new Border { Child = line, CornerRadius = new CornerRadius(16), Padding = new Thickness(26, 18, 24, 18), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Ink.Brush("SeleneGlassColor", 0.7) };
                optionList.Children.Add(row);
                rows.Add(row); knobs.Add(knob); tracks.Add(track); values.Add(value); sliders.Add(slider);
                ShowSwitch(i, false);
                if (animate) Ui.Rise(row, i * 40, 10, 220);
            }
        }

        private Dialog ask;

        private void ShowSwitch(int i, bool animate)
        {
            if (options[i].Action != null) return;
            if (options[i].IsChoice)
            {
                var o = options[i];
                if (sliders[i] != null) { sliders[i].Set(Math.Max(0, Array.IndexOf(o.Choices, Features.Value(o.Key))), o.Choices.Length, Features.ValueName(o.Key), animate); return; }
                values[i].Text = Features.ValueName(o.Key);
                if (animate) Ui.Pop(tracks[i]);
                return;
            }
            bool on = Features.On(options[i].Key);
            var slide = new DoubleAnimation(on ? 26 : 0, TimeSpan.FromMilliseconds(animate ? 200 : 0)) { EasingFunction = Ui.Out() };
            ((TranslateTransform)knobs[i].RenderTransform).BeginAnimation(TranslateTransform.XProperty, slide);
            tracks[i].Background = on ? Ink.Brush("GlyphColor") : Ink.Brush("SeleneInkColor", 0.2);
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
            for (int i = 0; i < rows.Count; i++)
                rows[i].BorderBrush = inOptions && i == index ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
            // the focused row in view
            if (inOptions && index < rows.Count && scroller != null)
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
            else if (!inOptions && scroller != null) scroller.ScrollToVerticalOffset(0);
        }

        public override void Handle(string b)
        {
            if (picker != null && picker.IsOpen) { picker.Handle(b); return; }   // a choice's list has the buttons
            if (ask != null) { ask.Handle(b); return; }   // the Reset question is up
            if (b == "Y") { MenuScreen.CopyLog(); return; }
            if (!inOptions)
            {
                if (b == "B") { Ui.Back(); if (fromMenu) LoginScene.Show(new MenuScreen(true), -1); else LoginScene.Finish(); return; }
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
            if (opt.IsChoice && opt.Slider && (b == "DPadLeft" || b == "DPadRight"))
            {
                int at = Math.Max(0, Array.IndexOf(opt.Choices, Features.Value(opt.Key))) + (b == "DPadLeft" ? -1 : 1);
                if (at < 0 || at >= opt.Choices.Length) return;   // the end of the line
                Features.SetValue(opt.Key, opt.Choices[at]);
                Ui.Tick();
                ShowSwitch(index, true);
                AfterChoice(opt);
                return;
            }
            if (opt.IsChoice && b == "A" && !opt.Slider)
            {
                int row = index;
                var names = opt.Names ?? opt.Choices;
                picker = ChoiceList.Open(Root, tracks[row], names, Math.Max(0, Array.IndexOf(opt.Choices, Features.Value(opt.Key))), k =>
                {
                    Features.SetValue(opt.Key, opt.Choices[k]);
                    ShowSwitch(row, true);
                    AfterChoice(opt);
                });
                return;
            }
            if (opt.IsChoice && (b == "DPadRight" || b == "A")) return;   // (A on a number row: nothing; Right: nothing)
            if (opt.Action != null)
            {
                if (b != "A") { MoveRow(b); return; }
                Ui.Confirm();
                if (opt.Key == "storeregion") LoginScene.Show(new StoreCurrencyScreen(false, () => new SettingsScreen(true, "Games")), 1);
                else if (opt.Key == "reset")
                {
                    ask = new Dialog("\uE5D5", "Reset Selene settings?", "Every switch and choice goes back to how Selene ships. Your profiles, theme and wallpaper stay.",
                        "Reset", "Keep them", () => { ask = null; Features.ResetAll(); Ui.Play("Success.wav", 0.8); ApplyEverything(); ShowGroup(false); ShowFocus(); }, () => ask = null, true);
                    ask.Open(Root);
                }
                return;
            }
            if (b == "A")
            {
                var key = options[index].Key;
                Features.Set(key, !Features.On(key));
                if (key == "brand") Brand.Apply();
                if (key == "topprofile") TopProfile.Refresh();
                Ui.Key();
                ShowSwitch(index, true);
                if (key == "hints") HomeHints.Update();
                if (key == "fullrate") Ui.FullRateChanged();
                if (key == "glow") LoginScene.RefreshBackdrop();
                if (key == "music" && !Features.On(key)) HomeNav.ReleaseMusic();
                if (key == "cardpictures") { GameFacts.CardStats(); CapturesScreen.CountInBackground(); }
                HomeNav.Log("settings: " + key + " " + (Features.On(key) ? "on" : "off"));
                return;
            }
            MoveRow(b);
        }

        private void MoveRow(string b)
        {
            int n = index + (b == "DPadDown" ? 1 : b == "DPadUp" ? -1 : 0);
            if (n == index || n < 0 || n >= rows.Count) return;
            index = n;
            Ui.Tick();
            ShowFocus();
        }

        // after a reset: everything that reads a setting once is told
        private static void ApplyEverything()
        {
            try
            {
                Brand.Apply(); TopProfile.Refresh(); HomeHints.Update(); LoginScene.RefreshBackdrop(); Contrast.Apply();
                HomeNav.ApplyValueSettings(); Ui.FullRateChanged(); Profiles.ApplyLook(); IdleScreen.Restart();
                GameFacts.CardStats(); CapturesScreen.CountInBackground();
            }
            catch (Exception ex) { HomeNav.Log("settings reset: " + ex.Message); }
        }

        // what a changed choice sets going at once
        private void AfterChoice(Features.Option opt)
        {
            if (opt.Key == "trailersound") HomeNav.ApplyValueSettings();
            if (opt.Key == "shade") Contrast.Apply();
            if (opt.Key == "prompts") Ui.RefreshPrompts();   // the hints show the new button names
            if (opt.Key == "homebackground") Profiles.ApplyLook();
            if (opt.Key == "idle") IdleScreen.Restart();
            if (opt.Key == "clipvolume") CapturesScreen.ClipVolumeChanged();
            if (opt.Key == "starttab" || opt.Key == "filtertabs") HomeNav.ApplyValueSettings();
            HomeNav.Log("settings: " + opt.Key + " = " + Features.Value(opt.Key));
        }
    }
}
