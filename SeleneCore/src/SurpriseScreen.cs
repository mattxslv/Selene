using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace Selene
{
    // The Surprise me card: not sure what to play? Covers flick past and slow down on one of your games, its art glowing
    // behind. A plays it (or installs it), X shows it on Spotlight, Y spins again, LB / RB pick where it draws from
    // (installed games, games you haven't played yet, all of them), B goes back.
    public class SurpriseScreen : Screen
    {
        private static readonly string[] Pools = { "Installed", "Not played yet", "All games" };
        private static readonly int[] Steps = { 55, 55, 60, 65, 70, 80, 90, 105, 125, 150, 185, 230, 290 };
        private static int pool = -1;   // remembered while Playnite runs
        private readonly List<Game> all;
        private readonly List<Border> tabs = new List<Border>();
        private readonly Tile tile = new Tile(300, 400, 18, 8) { FocusScale = 1 };
        private readonly TextBlock name, facts, hint;
        private readonly StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0), Opacity = 0 };
        private readonly Image hero = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
        private readonly DispatcherTimer step = new DispatcherTimer();
        private readonly Random random = new Random();
        private readonly Dictionary<Guid, ImageSource> covers = new Dictionary<Guid, ImageSource>();
        private List<Game> candidates = new List<Game>();
        private Game pick, preset;   // preset: the game the spin lands on first (picked from the card's pictures)
        private int stepAt;
        private bool spinning, closed;

        public SurpriseScreen(Game preset = null)
        {
            this.preset = preset;
            all = GameFacts.Games();
            if (preset != null && pool == 0 && !preset.IsInstalled) pool = 2;
            if (pool < 0)
            {
                var from = Features.Value("surprisepool");   // the Surprise me games setting
                pool = from == "fresh" ? 1 : from == "all" ? 2 : all.Any(g => g.IsInstalled) ? 0 : 2;
            }
            var dark = new Border();
            dark.SetResourceReference(Border.BackgroundProperty, "SeleneScrimBrush");
            Panel.SetZIndex(dark, -10);
            Root.Children.Add(dark);
            RenderOptions.SetBitmapScalingMode(hero, BitmapScalingMode.HighQuality);
            Panel.SetZIndex(hero, -5);
            Root.Children.Add(hero);
            var shade = new Border { IsHitTestVisible = false, Background = new RadialGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0.55, 0), Ink.Stop("SeleneScrimColor", 0.92, 1) }) { RadiusX = 0.75, RadiusY = 0.9 } };
            Panel.SetZIndex(shade, -4);
            Root.Children.Add(shade);

            // where it draws from: three pills up top (LB / RB)
            var tabBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 36, 0, 0) };
            tabBar.Children.Add(HomeNav.FilterBadge("LB"));   // the same badges as Spotlight's and Collection's tabs
            foreach (var p in Pools)
            {
                var pill = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 6, 18, 8), Margin = new Thickness(6, 0, 6, 0), Child = new TextBlock { Text = p, FontSize = 19, FontFamily = Ui.Body } };
                tabs.Add(pill);
                tabBar.Children.Add(pill);
            }
            tabBar.Children.Add(HomeNav.FilterBadge("RB"));
            Root.Children.Add(tabBar);

            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            col.Children.Add(new TextBlock { Text = "Surprise me", FontSize = 44, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center });
            hint = new TextBlock { Text = "Not sure what to play? Selene picks one.", FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.65), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 30) };
            col.Children.Add(hint);
            tile.Root.HorizontalAlignment = HorizontalAlignment.Center;
            tile.Face.ClipToBounds = true;
            col.Children.Add(tile.Root);
            name = new TextBlock { FontSize = 36, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0), MaxWidth = 1100, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0 };
            col.Children.Add(name);
            facts = new TextBlock { FontSize = 19, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.7), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Opacity = 0 };
            col.Children.Add(facts);
            col.Children.Add(actions);
            Root.Children.Add(col);

            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("Y", "Spin again"), Ui.Prompt("X", "Show on Spotlight")));   // (A: the Play pill under the card says it)
            step.Tick += (s, e) => Step();
            ShowTabs();
        }

        // the Surprise me card on Spotlight: a few ideas as pictures (Down goes into them, A spins onto that one)
        private static readonly Random cardRandom = new Random();
        public static void FillCard(List<Game> games)
        {
            int installed = games.Count(g => g.IsInstalled);
            int fresh = games.Count(g => GameFacts.Seconds(g) == 0 && !GameFacts.LastPlayed(g).HasValue);
            var pool = (installed > 0 ? games.Where(g => g.IsInstalled) : games).Where(g => GameFacts.BackgroundPath(g) != null || GameFacts.CoverPath(g) != null).ToList();
            var ideas = new List<Game>();
            while (ideas.Count < 4 && pool.Count > 0) { var g = pool[cardRandom.Next(pool.Count)]; pool.Remove(g); ideas.Add(g); }
            string about = games.Count == 0 ? "Add games and Selene picks one for you." : "Selene picks from your " + (installed > 0 ? installed + " installed games" : games.Count + " games") + (fresh > 0 ? ", or the " + fresh + " you haven't played yet." : ".");
            if (ideas.Count > 0) about += " Down for a few ideas.";
            var entries = ideas.Select(g =>
            {
                var game = g;
                return new CardShowcase.Entry { Image = GameFacts.BackgroundPath(g) ?? GameFacts.CoverPath(g), Title = g.Name, Sub = g.IsInstalled ? "Installed" : "", Glyph = "\uE8B1", Open = () => LoginScene.OpenWith(() => new SurpriseScreen(game)) };
            }).ToList();
            CardShowcase.Set("nav:surprise", "Surprise", "Not sure what to play?", about, entries);
        }

        private void ShowTabs()
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                bool on = i == pool;
                tabs[i].Background = on ? (Brush)Ink.Brush("SeleneFocusColor", 0.95) : Brushes.Transparent;
                var label = (TextBlock)tabs[i].Child;
                label.Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.7);
                label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
            candidates = pool == 0 ? all.Where(g => g.IsInstalled).ToList() : pool == 1 ? all.Where(g => GameFacts.Seconds(g) == 0 && !GameFacts.LastPlayed(g).HasValue).ToList() : all.ToList();
            // without repeats: the ones already picked wait until every game has come up (then it starts over)
            if (Features.On("surprisenorepeat") && candidates.Count > 1)
            {
                var fresh = candidates.Where(g => !seen.Contains(g.Id)).ToList();
                if (fresh.Count == 0) { seen.Clear(); fresh = candidates; }
                candidates = fresh;
            }
        }

        private static readonly HashSet<Guid> seen = new HashSet<Guid>();   // picked already (while Playnite runs)

        public override void Shown() { Spin(); }
        public override void Hidden() { closed = true; step.Stop(); }

        // the covers flick past, slower and slower, and stop on the pick
        private void Spin()
        {
            step.Stop();
            Ui.Fade(name, 0, 120); Ui.Fade(facts, 0, 120); Ui.Fade(actions, 0, 120);
            Ui.Fade(hero, 0, 300);
            tile.Focus(false);
            if (candidates.Count == 0)
            {
                pick = null;
                tile.Face.Child = new TextBlock { Text = pool == 0 ? "No installed games" : pool == 1 ? "You've played them all" : "No games yet", FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.7), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Center };
                hint.Text = "Try another pool with LB / RB.";
                return;
            }
            hint.Text = "Not sure what to play? Selene picks one.";
            pick = preset != null && candidates.Contains(preset) ? preset : candidates[random.Next(candidates.Count)];
            seen.Add(pick.Id);
            preset = null;   // Y spins for real next time
            // a handful of other covers to flick past (decoded ahead, off the UI thread), the pick's last
            var pass = new List<Game>();
            for (int i = 0; i < Steps.Length - 1 && candidates.Count > 1; i++) pass.Add(candidates[random.Next(candidates.Count)]);
            pass.Add(pick);
            var files = pass.Select(GameFacts.CoverPath).Where(f => f != null).Distinct().ToList();
            var byFile = pass.Where(g => GameFacts.CoverPath(g) != null).GroupBy(GameFacts.CoverPath).ToDictionary(x => x.Key, x => x.First().Id);
            // the spin starts once its covers are in (they flicked past as bare names, the pick's an empty card, on a first
            // open), or after a second at most; one arriving late still fills the card it's on
            spinning = true;
            stepAt = 0;
            queue = pass;
            int want = files.Count(f => { Guid id; return byFile.TryGetValue(f, out id) && !covers.ContainsKey(id); }), got = 0;
            bool started = false;
            Action go = () => { if (started || closed) return; started = true; step.Interval = TimeSpan.FromMilliseconds(Steps[0]); step.Start(); };
            var spinPick = pick;
            Ui.LoadInOrder(files, 600, (i, pic) =>
            {
                Guid id;
                if (!byFile.TryGetValue(files[i], out id)) return;
                covers[id] = pic;
                if (++got >= want) go();
                if (!spinning && pick != null && pick == spinPick && id == pick.Id) ShowCover(pick, true);   // landed before it came
            });
            if (want == 0) go();
            else
            {
                var cap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                cap.Tick += (s2, e2) => { cap.Stop(); go(); };
                cap.Start();
            }
        }

        private List<Game> queue = new List<Game>();

        private void Step()
        {
            if (closed) { step.Stop(); return; }
            var g = stepAt < queue.Count ? queue[stepAt] : pick;
            ShowCover(g, stepAt >= queue.Count - 1);
            Ui.Tick();
            stepAt++;
            if (stepAt >= queue.Count) { step.Stop(); Land(); return; }
            step.Interval = TimeSpan.FromMilliseconds(Steps[Math.Min(stepAt, Steps.Length - 1)]);
        }

        private void ShowCover(Game g, bool last)
        {
            ImageSource pic;
            FrameworkElement face;
            if (covers.TryGetValue(g.Id, out pic)) face = new Border { CornerRadius = new CornerRadius(17), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } };
            else face = new TextBlock { Text = g.Name, FontSize = 22, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(18), VerticalAlignment = VerticalAlignment.Center };
            tile.Face.Child = face;
            if (!last) Ui.Rise(face, 0, 10, 90);
        }

        // landed: the ring lights up, the name and facts come in, the art behind
        private void Land()
        {
            spinning = false;
            if (pick == null) return;
            Ui.Confirm();
            tile.Focus(true);
            Ui.Pop(tile.Root);
            name.Text = pick.Name;
            var parts = new List<string>();
            parts.Add(pick.IsInstalled ? "Installed" : "Not installed");
            var secs = GameFacts.Seconds(pick);
            parts.Add(secs == 0 ? "Never played" : GameFacts.Hours(secs) + " played");
            var when = GameFacts.LastPlayed(pick);
            if (when.HasValue) parts.Add("Last " + GameFacts.Ago(when).ToLowerInvariant());
            facts.Text = string.Join("  ·  ", parts);
            actions.Children.Clear();
            actions.Children.Add(Ui.ActionPill("A", pick.IsInstalled ? "Play" : "Install", true));
            actions.Children.Add(Ui.ActionPill("X", "Show on Spotlight", false));
            Ui.Rise(name, 60, 10, 300); Ui.Rise(facts, 120, 10, 300); Ui.Rise(actions, 180, 10, 300);
            var bg = GameFacts.BackgroundPath(pick);
            if (bg == null) return;
            var want = pick.Id;
            Ui.LoadInOrder(new List<string> { bg }, 1920, (i, pic) =>
            {
                if (closed || pick == null || pick.Id != want) return;
                hero.Source = pic;
                hero.Opacity = 0;
                Ui.Fade(hero, 0.55, 600);
            });
        }

        public override void Handle(string b)
        {
            if (b == "B") { Ui.Back(); LoginScene.Finish(); return; }
            switch (b)
            {
                case "LeftShoulder":
                case "RightShoulder":
                    pool = (pool + (b == "RightShoulder" ? 1 : Pools.Length - 1)) % Pools.Length;
                    Ui.Tick();
                    ShowTabs();
                    Ui.Pop(tabs[pool]);
                    Spin();
                    return;
                case "Y":
                    if (spinning) return;
                    Spin();
                    return;
                case "A":
                    if (spinning || pick == null) return;
                    Ui.Confirm();
                    {
                        var id = pick.Id; bool installed = pick.IsInstalled;
                        LoginScene.Finish();
                        if (installed) HomeNav.StartGameNow(id);
                        else if (HomeNav.Api != null) HomeNav.Api.InstallGame(id);
                    }
                    return;
                case "X":
                    if (spinning || pick == null) return;
                    Ui.Confirm();
                    LoginScene.ForgetReturnFocus();
                    var pickId = pick.Id;
                    LoginScene.FinishThen(() => HomeNav.ShowGame(pickId));
                    return;
            }
        }
    }
}
