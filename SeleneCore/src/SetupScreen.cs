using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // "Let's get Selene ready": a card per thing Selene checks (SetupCheck.Run). Cards rise in one after another;
    // A on a card installs or explains it; when something becomes ready its status pops to a check. First launch
    // goes on to the profile steps; from the menu, B or Continue goes back to the home.
    public class SetupScreen : Screen
    {
        public override bool MenuOnStart { get { return false; } }   // (Start is its own here)
        private DateTime bottomArrivedAt = DateTime.MinValue;   // the last move onto the bottom row of cards
        private Dialog ask;            // Install all's question, then the restart question
        private bool batchRunning;     // Install all is downloading
        private const int Columns = 6;
        private readonly bool firstRun;
        // where each card sits: its row and its x from the centre (rows are centred, so a short last row sits in the middle)
        private readonly List<int> cardRow = new List<int>();
        private readonly List<double> cardX = new List<double>();
        private const double CardCell = 258;
        private readonly List<Tile> cards = new List<Tile>();
        private readonly List<TextBlock> statuses = new List<TextBlock>();
        private readonly DetailPanel info;
        private readonly Pill installAll, carryOn;
        private List<SetupItem> items;
        private int index;
        private DispatcherTimer recheck;
        private readonly Queue<string> installQueue = new Queue<string>();
        private bool settingUpLudusavi;

        private readonly bool fromMenu;   // B goes back to the menu (on Setup check) instead of the home
        public Func<Screen> Back;   // or to this (Selene settings)

        public SetupScreen(bool firstRun, bool fromMenu = false)
        {
            this.firstRun = firstRun;
            this.fromMenu = fromMenu;
            items = SetupCheck.Run(HomeNav.Api);
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -20, 0, 0) };
            col.Children.Add(Ui.Title("Let's get Selene ready"));
            col.Children.Add(Ui.Sub("Selene checks what it needs. You can come back to this anytime from the menu: Extensions, Selene."));
            // rows as even as possible (11 cards: 6 + 5), each centred
            var grid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 46, 0, 0) };
            int rows = (items.Count + Columns - 1) / Columns, at = 0;
            for (int r = 0; r < rows; r++)
            {
                int inRow = items.Count / rows + (r < items.Count % rows ? 1 : 0);
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                for (int i = 0; i < inRow; i++, at++)
                {
                    row.Children.Add(Card(items[at]));
                    cardRow.Add(r);
                    cardX.Add((i - (inRow - 1) / 2.0) * CardCell);
                }
                grid.Children.Add(row);
            }
            col.Children.Add(grid);
            info = new DetailPanel(960);
            info.Root.Margin = new Thickness(0, 34, 0, 0);
            info.Root.Height = 156;   // fixed: a taller or shorter text must not shift the centred screen
            col.Children.Add(info.Root);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };
            installAll = new Pill("Install all", 240);
            carryOn = new Pill(firstRun ? "Continue" : "Done", 200);
            buttons.Children.Add(installAll.Root);
            buttons.Children.Add(carryOn.Root);
            col.Children.Add(buttons);
            Root.Children.Add(col);
            if (!firstRun) Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            UpdateInstallAll();
            // the first card that needs something, else Continue
            index = items.FindIndex(i => i.State == SetupState.Missing || i.State == SetupState.Warning);
            if (index < 0) index = ButtonsStart + 1;
        }

        private int ButtonsStart { get { return items.Count; } }

        private FrameworkElement Card(SetupItem item)
        {
            var tile = new Tile(240, 140, 18, 6) { FocusScale = 1.06 };
            // the same layout in every card: icon, title and status always at the same height (status keeps room for 2 lines)
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 20, 16, 0) };
            sp.Children.Add(new TextBlock { Text = Icon(item.Key), FontFamily = Ui.Glyphs, FontSize = 26, Height = 30, Foreground = Ink.Brush("GlyphColor"), HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = item.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            // the state as a short chip; the full detail is in the panel below when the card is focused
            var status = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = Ui.Body, TextTrimming = TextTrimming.CharacterEllipsis };
            var chip = new Border { Child = status, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 200 };
            SetStatus(status, item);
            sp.Children.Add(chip);
            tile.Face.Child = sp;
            tile.Root.Margin = new Thickness(9);
            cards.Add(tile);
            statuses.Add(status);
            var holder = new Grid();
            holder.Children.Add(tile.Root);
            return holder;
        }

        private static string Icon(string key)
        {
            if (key == "ludusavi-program") return "\uE896";
            if (key.StartsWith("clash:")) return "\uE7BA";
            return "\uE74C";   // an add-on
        }

        private static void SetStatus(TextBlock t, SetupItem item)
        {
            var chip = t.Parent as Border;
            string key;
            switch (item.State)
            {
                case SetupState.Ready: key = "SeleneSuccessColor"; break;
                case SetupState.Missing: key = "GlyphColor"; break;
                case SetupState.Optional: key = "SeleneInkColor"; break;
                default: key = null; break;
            }
            t.Text = (item.State == SetupState.Ready ? "\u2713 " : "") + Short(item).ToUpperInvariant();
            if (key != null)
            {
                t.Foreground = Ink.Brush(key, key == "SeleneInkColor" ? 0.7 : 1);
                if (chip != null) chip.Background = Ink.Brush(key, 0.14);
            }
            else
            {
                t.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                if (chip != null) chip.Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0x6B, 0x6B));
            }
        }

        // a card's state in a word or two ("392 games", "Installed", "Not installed"...)
        private static string Short(SetupItem item)
        {
            if (item.Key == "ludusavi-program") return item.State == SetupState.Ready ? "Ready" : "Not set up";
            if (item.Detail == SetupCheck.PendingRestart) return "Restart to finish";
            if (item.Key.StartsWith("clash:")) return "Clashes";
            if (item.State == SetupState.Ready) return "Installed";
            return item.AddonId != null ? "Not installed" : "Turned off";
        }

        public override void Shown()
        {
            for (int i = 0; i < cards.Count; i++) Ui.Rise(cards[i].Root, 140 + i * 45);
            ShowFocus();
            // things change behind the screen (an add-on installed or turned on): check again every few seconds
            recheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            recheck.Tick += (s, e) => Recheck();
            recheck.Start();
            var w = Application.Current.MainWindow;
            if (w != null) w.Activated += OnActivated;
        }

        public override void Hidden()
        {
            if (recheck != null) recheck.Stop();
            if (openingWait != null) { openingWait.Stop(); openingWait = null; }
            var w = Application.Current.MainWindow;
            if (w != null) w.Activated -= OnActivated;
        }

        private bool checking;

        // the check reads the whole library: it runs in the background (on the UI thread it made the screen hitch every
        // few seconds) and only the result comes back here
        private void Recheck()
        {
            if (checking) return;
            checking = true;
            var api = HomeNav.Api;
            var dispatcher = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                List<SetupItem> result = null;
                try { result = SetupCheck.Run(api); }
                catch (Exception ex) { HomeNav.Log("setup check failed: " + ex.Message); }
                dispatcher.BeginInvoke(new Action(() => { checking = false; if (result != null) Apply(result); }), System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        private void Apply(List<SetupItem> fresh)
        {
            if (fresh.Count != items.Count) return;   // the list itself changed (an add-on that clashes appeared): next time the screen opens
            for (int i = 0; i < fresh.Count; i++)
            {
                if (fresh[i].Key != items[i].Key || (fresh[i].State == items[i].State && fresh[i].Detail == items[i].Detail)) continue;
                bool nowReady = fresh[i].State == SetupState.Ready && items[i].State != SetupState.Ready;
                items[i] = fresh[i];
                if (i == openingAt) continue;   // OPENING... stays until Playnite's question is up
                SetStatus(statuses[i], fresh[i]);
                Ui.Pop(statuses[i]);
                if (nowReady) Ui.Tick();
            }
            missingCache = null;
            UpdateInstallAll();
            ShowFocus();
        }

        private void UpdateInstallAll()
        {
            int missing = Missing().Count;
            installAll.Text = missing > 0 ? "Install all (" + missing + ")" : "All add-ons installed";
            installAll.Root.Opacity = missing > 0 ? 1 : 0.5;
        }

        private void ShowFocus()
        {
            for (int i = 0; i < cards.Count; i++) cards[i].Focus(i == index);
            installAll.Focus(index == ButtonsStart);
            carryOn.Focus(index == ButtonsStart + 1);
            if (index < items.Count)
            {
                var item = items[index];
                Describe(item);
            }
            else if (index == ButtonsStart)
            {
                // which add-ons the button installs, by name (the button itself is right below)
                var missing = Missing();
                if (missing.Count > 0) info.Show("\uE74C", Tone.Accent, "Install all", missing.Count + " to go", string.Join(" \u00B7 ", missing.Select(m => m.Name)), "Playnite asks for each one. They work after a restart.");
                else info.Show("\uE74C", Tone.Success, "Install all", "Done", "Every add-on that comes with Selene is installed.", "");
            }
            else
            {
                // how far setup is, and what's left
                int ready = items.Count(i => i.State == SetupState.Ready || i.State == SetupState.Optional);
                var left = items.Where(i => i.State == SetupState.Missing || i.State == SetupState.Warning).Select(i => i.Title).ToList();
                if (left.Count == 0) info.Show("\uE73E", Tone.Success, "All set", "Ready", firstRun ? "Everything Selene needs is here. Next: your profile." : "Everything Selene needs is here.", "");
                else info.Show("\uE72A", Tone.Accent, ready + " of " + items.Count + " ready", left.Count + " left", "Still to do: " + string.Join(", ", left) + ".", "You can finish this later from the menu: Setup check.");
            }
        }

        // the focused card in the panel below: what it is, its state, and what A does
        private void Describe(SetupItem item)
        {
            var icon = Icon(item.Key);
            switch (item.State)
            {
                case SetupState.Ready:
                    info.Show(icon, Tone.Success, item.Title, "Ready", item.About, item.Detail);
                    return;
                case SetupState.Warning:
                    info.Show(icon, Tone.Warning, item.Title, "Clashes", item.About, "Turn it off in Playnite's Add-ons so the two don't fight.");
                    return;
            }
            if (item.Detail == SetupCheck.PendingRestart) { info.Show(icon, Tone.Success, item.Title, "Restart to finish", item.About, "Installed. It starts working after Playnite restarts; install the others first to restart only once."); return; }
            if (item.AddonId != null) { info.Show(icon, Tone.Accent, item.Title, "Not installed", item.About, "Playnite asks you to confirm. It works after a restart.", "A", "Install"); return; }
            if (item.Key == "ludusavi-program") { info.Show(icon, Tone.Accent, item.Title, "Not set up", item.About, "Downloads the free Ludusavi program and connects it.", "A", "Set up"); return; }
            info.Show(icon, Tone.Warning, item.Title, "Turned off", item.About, "Turn it on in Playnite's Add-ons.");
        }

        public override void Handle(string b)
        {
            if (ask != null) { ask.Handle(b); return; }   // a question is up: the press is its own
            if (b == "B")
            {
                if (firstRun) { Ui.Shake(Root.Children[0]); return; }
                Ui.Back();
                if (Back != null) LoginScene.Show(Back(), -1);
                else if (fromMenu) LoginScene.Show(new MenuScreen(true), -1);
                else LoginScene.Finish();
                return;
            }
            if (b == "A") { Activate(); return; }
            if (!b.StartsWith("DPad")) return;
            int next;
            if (index < items.Count)
            {
                next = CardMove(index, b);
                if (next < 0 && b == "DPadDown")
                {
                    // onto the buttons, the nearer one. Only a Down that comes right after landing on the bottom row waits (a held
                    // stick went row, row, buttons in one go); resting there, any Down goes at once, however fast the taps
                    if ((DateTime.Now - bottomArrivedAt).TotalMilliseconds > 300) next = cardX[index] < 0 ? ButtonsStart : ButtonsStart + 1;
                }
                else if (next >= 0 && cardRow[next] == cardRow[items.Count - 1] && cardRow[index] != cardRow[next]) bottomArrivedAt = DateTime.Now;
            }
            else
            {
                next = -1;
                if (b == "DPadLeft" && index == ButtonsStart + 1) next = ButtonsStart;
                else if (b == "DPadRight" && index == ButtonsStart) next = ButtonsStart + 1;
                else if (b == "DPadUp") next = Nearest(cardRow[items.Count - 1], index == ButtonsStart ? -CardCell / 2 : CardCell / 2);
            }
            if (next < 0) return;
            index = next;
            Ui.Tick();
            ShowFocus();
        }

        // left/right along the row; up/down to the card in the next row that sits closest above or below
        private int CardMove(int from, string dir)
        {
            if (dir == "DPadLeft") return from > 0 && cardRow[from - 1] == cardRow[from] ? from - 1 : -1;
            if (dir == "DPadRight") return from + 1 < items.Count && cardRow[from + 1] == cardRow[from] ? from + 1 : -1;
            int row = cardRow[from] + (dir == "DPadUp" ? -1 : 1);
            return row < 0 || row > cardRow[items.Count - 1] ? -1 : Nearest(row, cardX[from], dir == "DPadDown");
        }

        // a card exactly between two: going down picks the right one, going up the left one (so up then down comes back)
        private int Nearest(int row, double x, bool tieRight = true)
        {
            int best = -1;
            for (int i = 0; i < items.Count; i++)
                if (cardRow[i] == row && (best < 0 || (tieRight ? Math.Abs(cardX[i] - x) <= Math.Abs(cardX[best] - x) + 0.5 : Math.Abs(cardX[i] - x) < Math.Abs(cardX[best] - x) - 0.5))) best = i;
            return best;
        }

        private void Activate()
        {
            if (index == ButtonsStart + 1)
            {
                Ui.Confirm();
                if (firstRun) LoginScene.Show(new ProfileSteps(null, () => new SetupScreen(true), p => LoginScene.Show(new WhoScreen(p.Id), 1)), 1);
                else LoginScene.Finish();
                return;
            }
            if (index == ButtonsStart)
            {
                var missing = Missing();
                if (missing.Count == 0 || batchRunning) { Ui.Shake(installAll.Root); return; }
                Ui.Confirm();
                // one question for all of them (Playnite's own link asked once per add-on, with a restart question each time)
                ask = new Dialog("\uE896", "Install " + missing.Count + (missing.Count == 1 ? " add-on?" : " add-ons?"),
                    string.Join(" \u00B7 ", missing.Select(m => m.Name)) + "\n\nSelene downloads them now. Playnite installs them all on its next restart.",
                    "Install all", "Not now", () => { ask = null; StartBatch(missing); }, () => ask = null, false).StartOnYes();
                ask.Open(Root);
                return;
            }
            var item = items[index];
            if (item.AddonId != null) { Ui.Confirm(); ShowOpening(item.AddonId); SetupCheck.Install(item.AddonId); return; }
            if (item.Key == "ludusavi-program" && item.State != SetupState.Ready) { SetUpLudusavi(index); return; }
            if (item.State == SetupState.Ready) { Ui.Pop(cards[index].Root); Ui.Tick(); return; }
            Ui.Shake(cards[index].Root);   // nothing to do from here; the hint says where
        }

        // which of Selene's add-ons are missing: asked of Playnite once in a while, not on every move (its list of add-ons is
        // read from disk each time it's asked, which made Left / Right between the buttons lag)
        private List<AddonInfo> missingCache;
        private DateTime missingAt;
        private List<AddonInfo> Missing()
        {
            if (missingCache == null || (DateTime.Now - missingAt).TotalSeconds > 4) { missingCache = SetupCheck.LeftToInstall(HomeNav.Api); missingAt = DateTime.Now; }
            return missingCache;
        }

        // Playnite takes a moment (it fetches the add-on's details) before its install question comes up: meanwhile the card
        // says OPENING... and breathes, and the panel says what's happening, so the press doesn't look like it did nothing
        private DispatcherTimer openingWait;
        private int openingAt = -1;
        private DateTime openingSince;

        private void ShowOpening(string addonId)
        {
            EndOpening();
            openingAt = items.FindIndex(i => i.AddonId == addonId);
            openingSince = DateTime.Now;
            if (openingAt >= 0)
            {
                var chip = statuses[openingAt].Parent as UIElement ?? statuses[openingAt];
                statuses[openingAt].Text = "OPENING…";
                statuses[openingAt].Foreground = Ink.Brush("SeleneInkColor", 0.85);
                chip.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                var item = items[openingAt];
                info.Show(Icon(item.Key), Tone.Accent, item.Title, "Opening", item.About, "Playnite is getting the add-on. Its question comes up in a moment.");
            }
            openingWait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            openingWait.Tick += (s, e) =>
            {
                // the question is up (or Playnite never answered): back to normal
                if (HomeNav.DialogUp() || (DateTime.Now - openingSince).TotalSeconds > 25) EndOpening();
            };
            openingWait.Start();
        }

        private void EndOpening()
        {
            if (openingWait != null) { openingWait.Stop(); openingWait = null; }
            if (openingAt < 0 || openingAt >= statuses.Count) { openingAt = -1; return; }
            var chip = statuses[openingAt].Parent as UIElement ?? statuses[openingAt];
            chip.BeginAnimation(UIElement.OpacityProperty, null);
            chip.Opacity = 1;
            SetStatus(statuses[openingAt], items[openingAt]);
            openingAt = -1;
            ShowFocus();
        }

        // Install all: every package downloaded and queued for Playnite's next start. The button counts along and breathes, each
        // card says DOWNLOADING... in turn and RESTART TO FINISH once it's queued, then one question: restart now?
        private void StartBatch(List<AddonInfo> addons)
        {
            batchRunning = true;
            installAll.Text = "Downloading\u2026";
            Breathe(installAll.Root, true);
            int busyCard = -1;
            AddonBatch.Run(addons,
                (i, count, id) =>
                {
                    installAll.Text = "Downloading " + (i + 1) + " of " + count + "\u2026";
                    busyCard = items.FindIndex(it => it.Key == "addon:" + id);
                    if (busyCard >= 0)
                    {
                        statuses[busyCard].Text = "DOWNLOADING\u2026";
                        statuses[busyCard].Foreground = Ink.Brush("SeleneInkColor", 0.85);
                        Breathe(statuses[busyCard].Parent as UIElement ?? statuses[busyCard], true);
                    }
                    var name = addons[i].Name;
                    info.Show("\uE896", Tone.Accent, "Installing add-ons", (i + 1) + " of " + count, "Downloading " + name + "\u2026", "Playnite installs them all on its next restart.");
                },
                (id, ok) =>
                {
                    int at = items.FindIndex(it => it.Key == "addon:" + id);
                    if (at < 0) return;
                    Breathe(statuses[at].Parent as UIElement ?? statuses[at], false);
                    if (ok)
                    {
                        SetupCheck.Accept(id);
                        var was = items[at];
                        items[at] = new SetupItem { Key = was.Key, Title = was.Title, About = was.About, State = SetupState.Optional, Detail = SetupCheck.PendingRestart };
                        SetStatus(statuses[at], items[at]);
                        Ui.Pop(statuses[at]);
                        Ui.Tick();
                    }
                    else
                    {
                        statuses[at].Text = "COULDN'T DOWNLOAD";
                        statuses[at].SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                        Ui.Shake(cards[at].Root);
                    }
                },
                (queued, failed) =>
                {
                    batchRunning = false;
                    Breathe(installAll.Root, false);
                    missingCache = null;
                    UpdateInstallAll();
                    ShowFocus();
                    if (queued == 0)
                    {
                        Ui.Notice("Couldn't download the add-ons. Check your connection and try again.", 7000);
                        return;
                    }
                    Ui.Chime();
                    var text = queued + (queued == 1 ? " add-on is" : " add-ons are") + " ready" + (failed > 0 ? ", " + failed + " couldn't be downloaded" : "") + ". Restart Playnite to start using " + (queued == 1 ? "it" : "them") + ".";
                    ask = new Dialog("\uE72C", "Restart Playnite?", text, "Restart now", "Later",
                        () => { ask = null; if (!AddonBatch.RestartPlaynite()) Ui.Notice("Restart Playnite to finish installing.", 7000); },
                        () => ask = null, false).StartOnYes();
                    ask.Open(Root);
                });
        }

        private static void Breathe(UIElement el, bool on)
        {
            if (el == null) return;
            if (!on) { el.BeginAnimation(UIElement.OpacityProperty, null); el.Opacity = 1; return; }
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }

        // Playnite shows one install prompt at a time: the next add-on is asked for once Playnite is back in front
        private void InstallNext()
        {
            if (installQueue.Count == 0) return;
            missingCache = null;
            var id = installQueue.Dequeue();
            HomeNav.Log("setup: installing " + id + " (" + installQueue.Count + " more)");
            ShowOpening(id);
            try { SetupCheck.Install(id); }
            catch (Exception ex) { HomeNav.Log("setup: install " + id + " failed: " + ex.Message); EndOpening(); }
        }

        private DispatcherTimer installWait;

        private void OnActivated(object sender, EventArgs e)
        {
            if (installQueue.Count == 0 || installWait != null) return;
            // Playnite asks "Restart now?" after each install, a moment after its window comes back: the next add-on is asked
            // for only once no dialog has been up for a while (the two asked at once, one over the other)
            int quiet = 0;
            installWait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            installWait.Tick += (s, a) =>
            {
                if (HomeNav.DialogUp()) { quiet = 0; return; }
                if (++quiet < 3) return;
                installWait.Stop();
                installWait = null;
                InstallNext();
            };
            installWait.Start();
        }

        private void SetUpLudusavi(int at)
        {
            if (settingUpLudusavi) return;
            settingUpLudusavi = true;
            Ui.Confirm();
            statuses[at].Text = "DOWNLOADING\u2026";
            statuses[at].Foreground = Ink.Brush("SeleneInkColor", 0.7);
            var api = HomeNav.Api;
            var dispatcher = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                Exception failed = null;
                try { LudusaviApp.DownloadIfNeeded(api); } catch (Exception ex) { failed = ex; }
                dispatcher.BeginInvoke(new Action(() =>
                {
                    settingUpLudusavi = false;
                    try
                    {
                        if (failed != null) throw failed;
                        LudusaviApp.Setup(api);
                        if (Features.On("quietbackups")) LudusaviApp.Quiet(api);
                        Ui.Chime();
                    }
                    catch (Exception ex)
                    {
                        HomeNav.Log("setup: Ludusavi failed: " + ex);
                        statuses[at].Text = "SETUP FAILED";
                        statuses[at].SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                        return;
                    }
                    Recheck();
                }));
            });
        }
    }
}
