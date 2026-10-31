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
        // Search and Changed come first, then Selene's groups
        private const string SearchGroup = "Search", ChangedGroup = "Changed";
        private static readonly string[] Listed = new[] { SearchGroup, ChangedGroup }.Concat(Features.Groups).ToArray();
        private static readonly int[] GroupIcons = { 0xE8B6 /* search */, 0xE889 /* history */, 0xEB9B /* power */, 0xE88A /* home */, 0xE8F1 /* grid */, 0xE7F4 /* notifications */, 0xE050 /* volume */, 0xE40A /* palette */, 0xE338 /* games */, 0xE869 /* build */ };

        private readonly List<Border> groupRows = new List<Border>();
        private readonly StackPanel optionList = new StackPanel();
        private ScrollViewer scroller;
        private readonly TextBlock groupTitle;
        private readonly List<Border> rows = new List<Border>();
        // the rows' live parts: the dot, the note and the bar follow the work while the screen is up (people wait here for it)
        private class LiveRow { public Features.Option Opt; public UIElement Dot; public TextBlock Note; public WorkBar Bar; public MediaStages Stages; }
        private readonly List<LiveRow> live = new List<LiveRow>();
        private readonly List<UIElement> groupDots = new List<UIElement>();
        private System.Windows.Threading.DispatcherTimer liveTimer;
        private readonly List<Border> knobs = new List<Border>();
        private readonly List<Border> tracks = new List<Border>();
        private readonly List<TextBlock> values = new List<TextBlock>();
        private readonly List<SliderBar> sliders = new List<SliderBar>();   // a number row's line (null on the others)
        private ChoiceList picker;   // a choice's list, open over its value   // a choice row's current value (null on switch rows)
        private List<Features.Option> options = new List<Features.Option>();
        private int group, index;
        private bool inOptions;
        private string GroupName { get { return Listed[group]; } }

        // the big groups in sections, in this order (a row not listed goes under "More")
        private static readonly Dictionary<string, string[][]> Sections = new Dictionary<string, string[][]>
        {
            { "Spotlight", new[] {
                new[] { "Row", "steadyhold", "pickscroll", "rowedges", "filtertabs", "fullscreenpresets", "consistentfavorites", "shade", "idle" },
                new[] { "Cards", "cardactivity", "actualplatform", "activitysessions", "cardcaptures", "capturesfolders", "cardachievements", "cardsurprise", "cardnews", "newsfeeds", "cardpictures" },
                new[] { "Trailers and downloads", "trailerdelay", "trailersound", "trailerdownload", "artdownload", "screenshotsdownload" } } },
            { "Games", new[] {
                new[] { "Your library", "gamesget", "gamesask" },
                new[] { "Launching", "launchstyle", "launchmotion", "launchshade", "loadingscreen", "launchchoice", "aftergame", "guidefocus" },
                new[] { "While playing", "freeze", "powerconfirm", "tray", "quietbackups" },
                new[] { "Game pages", "gameoptions", "achievements", "achcelebrate", "achsetup", "hltb", "hltbauto", "metadatacomplete", "metasource" },
                new[] { "Store", "storepage", "storetrailers", "storetab", "storeregion" },
                new[] { "Surprise me", "surprisepool", "surprisenorepeat" } } },
            { "Look", new[] {
                new[] { "Background", "homebackground", "glow", "backgroundfit", "guidefade", "smoothswitch", "perfmode" },
                new[] { "Art", "trimlogos", "logosize", "squareart", "steamgridkey", "smartcrop", "previewartfit", "activitycoverfit", "trailerlogogap", "screenshotskeleton" },
                new[] { "Text and hints", "textshadow", "hints", "prompts", "detailsbadges", "captureplayerfit", "dialogs", "topprofile", "language", "languagerestart" } } },
            { "Advanced", new[] {
                new[] { "Help", "report", "setupcheck", "storelibraries", "xboxsignin", "gamesfolder", "shortaddonask", "reportchord", "perflog" },
                new[] { "Performance", "fullrate", "calmmotion", "presence", "asynccovers", "lightweight" },
                new[] { "Controls and Playnite", "viewswitch", "playnitesettings", "legacytasks" },
                new[] { "Language", "translate", "translatecollect" },
                new[] { "Reset", "reset" } } },
        };

        private static string SectionOf(Features.Option o)
        {
            string[][] list;
            if (!Sections.TryGetValue(o.Group, out list)) return null;
            foreach (var sec in list) if (Array.IndexOf(sec, o.Key, 1) > 0) return sec[0];
            return "More";
        }

        private static int SectionPlace(Features.Option o)
        {
            string[][] list;
            if (!Sections.TryGetValue(o.Group, out list)) return 0;
            for (int i = 0; i < list.Length; i++) if (Array.IndexOf(list[i], o.Key, 1) > 0) return i;
            return list.Length;
        }

        // changed from how Selene ships (another add-on's own setting and the rows that do something don't count)
        private static bool IsChanged(Features.Option o)
        {
            if (o.Action != null || o.Get != null) return false;
            return o.IsChoice ? Features.Value(o.Key) != o.Default : Features.On(o.Key) != o.DefaultOn;
        }

        // where the settings were left: opened again (from the menu or Start), they come back there
        private static string lastGroup, lastKey;

        public override bool Overlay { get { return true; } }

        // a deep, even shade: the settings read clearly over anything
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.94); } }

        // opened from the menu: B goes back to it (on this item); held Start from the home: B goes back to the home
        private readonly bool fromMenu;

        // startKey: the row to land on (back from what that row opened: Setup check, Report a problem...), else the group list
        private string startKey;

        public SettingsScreen(bool fromMenu = false, string startGroup = null, string startKey = null)
        {
            this.fromMenu = fromMenu;
            if (startGroup == null && startKey == null) { startGroup = lastGroup; startKey = lastKey; }
            this.startKey = startKey;
            Root.UseLayoutRounding = true;   // whole pixels: at a fractional scroll offset some lines went soft
            group = Array.IndexOf(Listed, startGroup ?? "");
            if (group < 0) group = Array.IndexOf(Listed, Features.Groups[0]);
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
            for (int i = 0; i < Listed.Length; i++)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = Ui.Icon(GroupIcons[i]), FontFamily = Ui.Icons, FontSize = 24, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
                line.Children.Add(new TextBlock { Text = Listed[i], FontSize = 21, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
                // the dot: this group's work still running behind the scenes (songs, art, the achievements check)
                var groupDot = Background.Dot();
                groupDot.Visibility = i >= 2 && Background.ForGroup(Listed[i]) ? Visibility.Visible : Visibility.Collapsed;
                line.Children.Add(groupDot);
                groupDots.Add(groupDot);
                var row = new Border { Child = line, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 14, 20, 14), Margin = new Thickness(0, 0, 24, 6) };
                left.Children.Add(row);
                groupRows.Add(row);
                // a thin line under Changed: Search and Changed apart from the groups
                if (i == 1) left.Children.Add(new Border { Height = 1, Background = Ink.Brush("SeleneInkColor", 0.16), Margin = new Thickness(20, 8, 44, 14), IsHitTestVisible = false });
                int selectedGroup = i;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    if (ask != null || (picker != null && picker.IsOpen)) return;
                    group = selectedGroup; index = 0; inOptions = false;
                    if (GroupName == SearchGroup) { OpenSearch(); e.Handled = true; return; }
                    ShowGroup(false); scroller.ScrollToVerticalOffset(0); ShowFocus(); e.Handled = true;
                };
            }
            layout.Children.Add(left);

            // right: the chosen group's switches
            var right = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
            groupTitle = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), Margin = new Thickness(8, 14, 0, 16) };
            right.Children.Add(groupTitle);
            // the rows scroll (a long group ran off the screen, and the focus went on into rows you couldn't see)
            scroller = new ScrollViewer { Content = optionList, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 715, Focusable = false, CanContentScroll = false };
            right.Children.Add(PeekScroller.Wrap(scroller, optionList));   // the rows fade into the dark at the edges
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
            if (startKey != null)
            {
                int k = options.FindIndex(o => o.Key == startKey);
                if (k >= 0) { index = k; inOptions = true; }
                startKey = null;
            }
            ShowFocus();
            // landing on a row far down (back from Adjust): the list has no size yet here, so the scroll to it is done again once laid out
            if (inOptions) Root.Dispatcher.BeginInvoke(new Action(ShowFocus), System.Windows.Threading.DispatcherPriority.Loaded);
            if (liveTimer == null) { liveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; liveTimer.Tick += Live; }
            liveTimer.Start();
        }

        // the switches of the chosen group, rising in
        // a row's button, when it has one: its own (Setup check...), or Shade over the art's "Adjust" when the home is behind the
        // settings (set with the home in view, in ShadePreviewScreen); elsewhere that row keeps its slider
        // the key is copied from steamgriddb.com (on this PC, or sent from a phone) and pasted: no typing 32 letters with a pad
        private void AskSteamGridKey()
        {
            ask = new Dialog("\uE8D7", "SteamGridDB key", "Get a free key at steamgriddb.com > Preferences > API.\nCopy it, then choose Paste.", "Paste", "Not now", () =>
            {
                ask = null;
                SquareFetch.PasteKey(ok => { if (ok) try { ShowGroup(false); ShowFocus(); } catch (Exception ex) { HomeNav.Log("settings: refresh after the key: " + ex.Message); } });
            }, () => ask = null, false, false);
            ask.Open(Root);
        }

        private string ActionOf(Features.Option o)
        {
            return o.Action ?? (o.Key == "shade" && LoginScene.KeepHome ? "Adjust" : null);
        }

        private void ShowGroup(bool animate)
        {
            var name = GroupName;
            var available = Features.All.Where(o => MediaDownloads.ShowSetting(o.Key) && (o.Available == null || o.Available()));
            if (name == ChangedGroup) options = available.Where(IsChanged).OrderBy(o => Array.IndexOf(Features.Groups, o.Group)).ThenBy(SectionPlace).ToList();
            else if (name == SearchGroup) options = new List<Features.Option>();
            else options = available.Where(o => o.Group == name).OrderBy(SectionPlace).ToList();   // (stable: each section keeps its order)
            groupTitle.Text = name.ToUpperInvariant();
            optionList.Children.Clear(); rows.Clear(); knobs.Clear(); tracks.Clear(); values.Clear(); sliders.Clear(); live.Clear();
            if (options.Count == 0)
            {
                var note = name == SearchGroup ? "Press A to search every setting by name." : name == ChangedGroup ? "Nothing changed yet. Settings you change show up here." : "";
                var empty = new TextBlock { Text = note, FontSize = 19, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), Margin = new Thickness(8, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
                optionList.Children.Add(empty);
                if (animate) Ui.Rise(empty, 0, 10, 220);
            }
            string section = null;
            for (int i = 0; i < options.Count; i++)
            {
                var f = options[i];
                // a section's heading over its first row (Changed: the group each row is in)
                var sectionNow = name == ChangedGroup ? f.Group : SectionOf(f);
                if (sectionNow != null && sectionNow != section)
                {
                    var heading = new TextBlock { Text = sectionNow, FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("GlyphColor"), Margin = new Thickness(8, section == null ? 0 : 18, 0, 10) };
                    optionList.Children.Add(heading);
                    if (animate) Ui.Rise(heading, i * 40, 10, 220);
                }
                section = sectionNow;
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = f.Label, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor") });
                var dot = Background.Dot();   // this one's work still running
                head.Children.Add(dot);
                text.Children.Add(head);
                text.Children.Add(new TextBlock { Text = f.About, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                // the note (a count, a state) and, while the work runs, its bar: a thin line filled as far as it has got, with the percentage
                var noteBlock = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("GlyphColor"), Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
                text.Children.Add(noteBlock);
                MediaStages stages = null;
                if (f.Key == "gamesget" && MediaDownloads.Unified && MediaDownloads.Enabled) { stages = new MediaStages(); text.Children.Add(stages.Root); }
                WorkBar bar = null;
                if (f.Progress != null) { bar = new WorkBar(); text.Children.Add(bar.Root); }
                var liveRow = new LiveRow { Opt = f, Dot = dot, Note = noteBlock, Bar = bar, Stages = stages };
                Refresh(liveRow);
                live.Add(liveRow);
                var knob = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0), Background = Brushes.White, RenderTransform = new TranslateTransform() };
                var track = new Border { Width = 56, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center, Child = knob, Margin = new Thickness(24, 0, 0, 0) };
                TextBlock value = null;
                SliderBar slider = null;
                var action = ActionOf(f);
                if (action != null)
                {
                    // The row itself is the action; only settings with a value need a right-hand control.
                    track = new Border { Width = 0, Visibility = Visibility.Collapsed };
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
                int selectedRow = i;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    if (ask != null || (picker != null && picker.IsOpen)) return;
                    index = selectedRow; inOptions = true; ShowFocus();
                    if (f.IsChoice && f.Slider) { Features.Cycle(f.Key, 1); ShowSwitch(index, true); AfterChoice(f); }
                    else Handle("A");
                    e.Handled = true;
                };
                rows.Add(row); knobs.Add(knob); tracks.Add(track); values.Add(value); sliders.Add(slider);
                ShowSwitch(i, false);
                if (animate) Ui.Rise(row, i * 40, 10, 220);
            }
        }

        private Dialog ask;
        private void ConfirmStopDownloads()
        {
            ask = new Dialog("\uE896", "Stop downloads?", "Stops new requests. Active transfers and queued songs may finish. Downloaded files stay.",
                "Stop", "Keep running", () => { ask = null; MediaDownloads.Stop(); ShowGroup(false); ShowFocus(); },
                () => { ask = null; ShowFocus(); }, false);
            ask.Open(Root);
        }

        private void ShowSwitch(int i, bool animate)
        {
            if (ActionOf(options[i]) != null) return;
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
            lastGroup = GroupName == SearchGroup ? null : GroupName;
            lastKey = inOptions && index < options.Count ? options[index].Key : null;
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
                    if (Math.Abs(want - scroller.VerticalOffset) > 0.5) scroller.ScrollToVerticalOffset(Math.Round(want));
                }
                catch { }
            }
        }

        // a row's live parts brought up to date: the dot while its work runs, the note, the bar and its percentage
        private static void Refresh(LiveRow r)
        {
            string note = null;
            if (r.Stages != null) r.Stages.Update();   // (the strip says it all: no note lines)
            else if (r.Opt.Note != null) { try { note = r.Opt.Note(); } catch { } }
            r.Note.Text = note ?? "";
            r.Note.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
            double share = -1;
            if (r.Opt.Progress != null) { try { share = r.Opt.Progress(); } catch { } }
            if (r.Bar != null) r.Bar.Set(share);
            r.Dot.Visibility = Background.ForOption(r.Opt.Key) ? Visibility.Visible : Visibility.Collapsed;
        }

        // once a second while the screen is up: the work moves on, the rows and the groups' dots with it
        private void Live(object sender, EventArgs e)
        {
            try
            {
                foreach (var r in live) Refresh(r);
                for (int i = 2; i < groupDots.Count && i < Listed.Length; i++)   // (not Search and Changed)
                    groupDots[i].Visibility = Background.ForGroup(Listed[i]) ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { HomeNav.Log("settings: live: " + ex.Message); }
        }

        public override void Hidden()
        {
            if (liveTimer != null) liveTimer.Stop();
        }

        public override void Handle(string b)
        {
            if (picker != null && picker.IsOpen) { picker.Handle(b); return; }   // a choice's list has the buttons
            if (ask != null) { ask.Handle(b); return; }   // the Reset question is up
            if (b == "Y") { MenuScreen.CopyLog(); return; }
            if (!inOptions)
            {
                if (b == "B") { Ui.Back(); if (fromMenu) LoginScene.Show(new MenuScreen(true), -1); else LoginScene.Finish(); return; }
                if (b == "A" && GroupName == SearchGroup) { Ui.Confirm(); OpenSearch(); return; }
                if ((b == "A" || b == "DPadRight") && options.Count > 0) { inOptions = true; index = Math.Min(index, options.Count - 1); Ui.Tick(); ShowFocus(); return; }   // (back on the row left)
                int next = group + (b == "DPadDown" ? 1 : b == "DPadUp" ? -1 : 0);
                if (next == group) return;
                next = (next + groupRows.Count) % groupRows.Count;   // round from the first group to the last and back
                group = next;
                index = 0;
                Ui.Tick();
                ShowGroup(true);
                if (scroller != null) scroller.ScrollToVerticalOffset(0);
                ShowFocus();
                return;
            }
            var opt = options[index];
            bool acts = ActionOf(opt) != null;
            if (b == "B" || (b == "DPadLeft" && !(opt.IsChoice && opt.Slider && !acts))) { inOptions = false; Ui.Back(); ShowFocus(); return; }
            if (opt.IsChoice && opt.Slider && !acts && (b == "DPadLeft" || b == "DPadRight"))
            {
                int at = Math.Max(0, Array.IndexOf(opt.Choices, Features.Value(opt.Key))) + (b == "DPadLeft" ? -1 : 1);
                if (at < 0 || at >= opt.Choices.Length) return;   // the end of the line
                Features.SetValue(opt.Key, opt.Choices[at]);
                Ui.Tick();
                ShowSwitch(index, true);
                AfterChoice(opt);
                return;
            }
            if (opt.IsChoice && b == "A" && !opt.Slider && !acts)
            {
                int row = index;
                var names = opt.Names ?? opt.Choices;
                picker = ChoiceList.Open(Root, tracks[row], names, Math.Max(0, Array.IndexOf(opt.Choices, Features.Value(opt.Key))), k =>
                {
                    bool changed = Features.Value(opt.Key) != opt.Choices[k];
                    if (changed && DownloadConsent.IsControlled(opt.Key)) DownloadConsent.Revoke(opt.Key);
                    Features.SetValue(opt.Key, opt.Choices[k]);
                    ShowSwitch(row, true);
                    if (changed) AfterChoice(opt);
                });
                return;
            }
            if (opt.IsChoice && !acts && (b == "DPadRight" || b == "A")) return;   // (A on a number row: nothing; Right: nothing)
            if (acts)
            {
                if (b != "A") { MoveRow(b); return; }
                if (opt.Key == "gamesget" && MediaDownloads.Unified && MediaDownloads.Enabled && Background.ForOption("gamesget")) return;   // (downloading: the row only shows progress)
                Ui.Confirm();
                if (opt.Key == "shade") LoginScene.Show(new ShadePreviewScreen(fromMenu), 1);   // set with the home in view
                else if (opt.Key == "setupcheck") { bool back = fromMenu; LoginScene.Show(new SetupScreen(false) { Back = () => new SettingsScreen(back, "Advanced", "setupcheck") }, 1); }
                else if (opt.Key == "report") { bool back = fromMenu; LoginScene.Show(new ReportScreen(() => new SettingsScreen(back, "Advanced", "report")), 1); }
                else if (opt.Key == "newsfeeds") LoginScene.Show(new NewsFeedsScreen(() => new SettingsScreen(fromMenu, "Spotlight", "newsfeeds")), 1);
                else if (opt.Key == "storeregion") LoginScene.Show(new StoreCurrencyScreen(false, () => new SettingsScreen(true, "Games", "storeregion")), 1);
                else if (opt.Key == "steamgridkey") AskSteamGridKey();
                else if (opt.Key == "capturesfolders")
                {
                    // Windows' folder picker (Playnite's own call): mouse or keyboard
                    var folder = HomeNav.Api != null ? HomeNav.Api.Dialogs.SelectFolder() : null;
                    if (!string.IsNullOrEmpty(folder))
                    {
                        bool added = Features.ToggleCapturesFolder(folder);
                        CapturesScreen.Known = new List<CapturesScreen.Capture>();   // (looked through again next time)
                        Ui.Notice(added ? "Folder added" : "Folder removed", 3000);
                        try { ShowGroup(false); ShowFocus(); } catch (Exception ex) { HomeNav.Log("settings: refresh after a captures folder: " + ex.Message); }
                    }
                }
                else if (opt.Key == "gamesget")
                {
                    if (MediaDownloads.Unified && MediaDownloads.Enabled)
                    {
                        ConfirmStopDownloads(); return;
                    }
                    ask = GamesSetup.Question(() => { ask = null; try { ShowGroup(false); ShowFocus(); } catch (Exception ex) { HomeNav.Log("settings: refresh after the games question: " + ex.Message); } });
                    ask.Open(Root);
                }
                else if (opt.Key == "legacytasks")
                {
                    ask = new Dialog("\uE002", "Remove old administrator tasks?", "Windows will ask for permission for each task.",
                        "Remove", "Not now", () =>
                        {
                            ask = null;
                            LegacyTasks.Remove((removed, left) =>
                            {
                                if (left == 0) Ui.Play("Success.wav", 0.8);
                                Ui.Notice(left == 0 ? "Old administrator tasks removed." : left + (left == 1 ? " task" : " tasks") + " couldn't be removed. Remove them in Task Scheduler.", 7000);
                                try { ShowGroup(false); ShowFocus(); } catch (Exception ex) { HomeNav.Log("settings: refresh after removing old tasks: " + ex.Message); }
                            });
                        }, () => ask = null, true, true);
                    ask.Open(Root);
                }
                else if (opt.Key == "reset")
                {
                    ask = new Dialog("\uE5D5", "Reset Selene settings?", "Every switch and choice goes back to how Selene ships. Your profiles, theme and wallpaper stay.",
                        "Reset", "Keep them", () => { ask = null; Features.ResetAll(); Ui.Play("Success.wav", 0.8); ApplyEverything(); ShowGroup(false); ShowFocus(); }, () => ask = null, true, true);
                    ask.Open(Root);
                }
                return;
            }
            if (b == "A")
            {
                var key = options[index].Key;
                if (DownloadConsent.IsControlled(key) && !Features.On(key))
                {
                    ConfirmDownload(key, () => { Features.Set(key, true); DownloadConsent.Grant(key); DownloadConsent.Changed(key); ShowSwitch(index, true); });
                    return;
                }
                Features.Set(key, !Features.On(key));
                if (DownloadConsent.IsControlled(key) && !Features.On(key)) DownloadConsent.Revoke(key);
                if (DownloadConsent.IsControlled(key)) DownloadConsent.Changed(key);
                if (key == "brand") Brand.Apply();
                if (key == "downloadprogress") { ShowGroup(false); ShowFocus(); }
                if (key == "topprofile") TopProfile.Refresh();
                if (key == "hltb" || key == "achievements" || key == "rowedges" || key == "collectioninfo" || key == "collectionhints") SeleneShell.ApplyNowPlaying();
                if (key == "translatecollect") { Lang.Collect(Features.On(key)); if (Features.On(key)) Ui.Notice("Phrases are saved to \"Selene phrases.txt\" on your Desktop."); }
                Ui.Key();
                ShowSwitch(index, true);
                if (key == "hints") HomeHints.Update();
                if (key == "detailsbadges") Ui.PublishPrompts();
                if (key == "fullscreenpresets") HomeNav.ApplyValueSettings();
                if (key == "fullrate") Ui.FullRateChanged();
                if (key == "presence") ScreenPresence.Evaluate();
                if (key == "calmmotion") Ui.Notice("Restart Playnite to apply.");
                if (key == "lightweight") HomeNav.ApplyPerformanceMode();
                if (key == "perflog") { PerformanceRecorder.SetRecording(Features.On(key)); Ui.Notice(Features.On(key) ? "Performance recording started. Use Selene normally, then turn this off." : "Performance recording stopped.", 6000); }
                if (key == "collectionglide") LibraryStage.ApplyNavigationSettings();
                if (key == "glow") LoginScene.RefreshBackdrop();
                if (key == "songsettle") SongSetup.ApplySongSettle();
                if (key == "selenemusic") { SongSetup.ApplyDefaultMusic(); Ui.Notice("Restart Playnite to hear it.", 5000); }
                if (key == "independentmusic") HomeNav.RefreshMusicPolicy();
                if (key == "perfmode") Ui.Notice("Restart Playnite to apply.");
                if (key == "music")
                {
                    if (Features.On(key)) { HomeNav.LetMusicGo(); HomeNav.SetMusic(LoginScene.CoversHome ? "login" : "home"); }
                    else HomeNav.ReleaseMusicQuietly();
                }
                if (key == "cardpictures") { GameFacts.CardStats(); CapturesScreen.CountInBackground(); }
                if (key.StartsWith("card") && key != "cardpictures") HomeNav.RefreshCards();   // (the row behind settings follows at once)
                HomeNav.Log("settings: " + key + " " + (Features.On(key) ? "on" : "off"));
                return;
            }
            MoveRow(b);
        }

        // every setting by name, description, group or section: A on one opens its group with it focused
        private void OpenSearch()
        {
            bool back = fromMenu;
            var finder = new SearchScreen.Finder
            {
                Title = "Find a setting", Placeholder = "Type a setting's name", EmptyNote = "Settings show here as you type.", NoneNote = "No setting matches that.",
                Back = () => LoginScene.Show(new SettingsScreen(back, SearchGroup), -1),
                Max = 4, Shade = Ink.Brush("SeleneScrimColor", 0.94),   // four cards fit the screen's width; as dark as the settings
                Find = q => Features.All
                    .Where(o => MediaDownloads.ShowSetting(o.Key) && (o.Available == null || o.Available()) && Matches(o, q))
                    .OrderBy(o => o.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : o.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2)
                    .Select(o => new SearchScreen.Hit { Build = () => SettingTile(o), Open = () => LoginScene.Show(new SettingsScreen(back, o.Group, o.Key), 1) })
                    .ToList()
            };
            LoginScene.Show(new SearchScreen(finder), 1);
        }

        // every word typed is somewhere in the name, the description, the group or the section
        private static bool Matches(Features.Option o, string q)
        {
            var hay = (o.Label + " " + o.About + " " + o.Group + " " + (SectionOf(o) ?? "")).ToLowerInvariant();
            return q.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w));
        }

        private static Tile SettingTile(Features.Option o)
        {
            var t = new Tile(276, 132, 16, 4) { FocusScale = 1.06 };
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 20, 0) };
            words.Children.Add(new TextBlock { Text = o.Label, FontSize = 19, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap });
            var where = SectionOf(o);
            words.Children.Add(new TextBlock { Text = o.Group + (where != null ? " · " + where : ""), FontSize = 14, FontFamily = Ui.Body, Foreground = Ink.Brush("GlyphColor"), Margin = new Thickness(0, 6, 0, 0) });
            t.Face.Background = Ink.Brush("SeleneGlassColor", 0.85);
            t.Face.Child = words;
            return t;
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
                Brand.Apply(); TopProfile.Refresh(); SeleneShell.ApplyNowPlaying(); HomeHints.Update(); LoginScene.RefreshBackdrop(); Contrast.Apply();
                HomeNav.ApplyValueSettings(); Ui.FullRateChanged(); Profiles.ApplyLook(); IdleScreen.Restart(); BackgroundFit.Apply(); LibraryStage.ApplyNavigationSettings();
                GameFacts.CardStats(); CapturesScreen.CountInBackground();
            }
            catch (Exception ex) { HomeNav.Log("settings reset: " + ex.Message); }
        }

        // what a changed choice sets going at once
        private void ConfirmDownload(string key, Action accepted, Action declined = null)
        {
            ask = new Dialog("\uE2C4", "Allow these downloads?", DownloadConsent.Details(key), "Allow downloads", "Not now",
                () => { ask = null; accepted(); }, () => { ask = null; if (declined != null) declined(); }, false, true, 360);
            ask.Open(Root);
        }

        private void AfterChoice(Features.Option opt)
        {
            if (opt.Key == "trailerdownload")
            {
                if (Features.Value(opt.Key) == "off") { DownloadConsent.Changed(opt.Key); return; }
                if (!DownloadConsent.Allowed(opt.Key)) ConfirmDownload(opt.Key, () => { DownloadConsent.Grant(opt.Key); DownloadConsent.Changed(opt.Key); },
                    () => { Features.SetValue(opt.Key, "off"); ShowSwitch(index, true); DownloadConsent.Changed(opt.Key); });
                return;
            }
            if (opt.Key == "language" && Lang.WouldChange(Features.Value("language"))) ask = LanguageSetup.Restart(Root, () => ask = null, () => { });
            if (opt.Key == "trailersound") HomeNav.ApplyValueSettings();
            if (opt.Key == "shade") Contrast.Apply();
            if (opt.Key == "prompts") Ui.RefreshPrompts();   // the hints show the new button names
            if (opt.Key == "homebackground") Profiles.ApplyLook();
            if (opt.Key == "backgroundfit") BackgroundFit.Apply();
            if (opt.Key == "idle") IdleScreen.Restart();
            if (opt.Key == "clipvolume") CapturesScreen.ClipVolumeChanged();
            if (opt.Key == "starttab" || opt.Key == "filtertabs") HomeNav.ApplyValueSettings();
            HomeNav.Log("settings: " + opt.Key + " = " + Features.Value(opt.Key));
        }
    }
}
