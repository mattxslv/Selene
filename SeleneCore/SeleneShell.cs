using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Selene
{
    // The parts of the main screen Selene Core owns: home <-> library, the login layer, values the theme binds to.
    public static class SeleneShell
    {
        internal class Relay : ICommand
        {
            private readonly Action run;
            public Relay(Action run) { this.run = run; }
            public bool CanExecute(object parameter) { return true; }
            public void Execute(object parameter) { run(); }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }

        private static Window main;
        private static ICommand seleneMenu;
        private static System.Windows.Threading.DispatcherTimer attachTimer;
        internal static bool Ready { get; private set; }

        // the media folder for the theme's Square / Hero art, or "" when that folder has none (a new install never has
        // any: without this every tile and every move asked Playnite for a missing file, a warning each in its log)
        public static void ApplyArtRoots()
        {
            var app = Application.Current;
            if (app == null) return;
            foreach (var sub in new[] { "Square", "Hero" })
            {
                var dir = SelenePaths.Media(sub);
                bool any = false;
                try { any = System.IO.Directory.EnumerateFiles(dir, "*.jpg").Any(); } catch { }
                app.Resources["Selene" + sub + "Root"] = any ? System.IO.Path.GetDirectoryName(dir) : "";
            }
        }

        public static void Start()
        {
            var app = Application.Current;
            if (app == null) return;
            // the theme reads the media folder from here (tile squares, hero art)
            app.Resources["SeleneMediaRoot"] = System.IO.Path.GetDirectoryName(SelenePaths.Media("Hero"));
            ApplyArtRoots();
            app.Resources["SeleneIconFont"] = Ui.Icons;
            app.Resources["FontMaterialIcons"] = Ui.Icons;   // the game menu's and settings menus' icons (the theme's relative font path may not load there)
            app.Resources["FontFamily"] = Ui.TextFont;   // Playnite's font setting, for every text that names none   // solid icons for the theme (a font path in the theme's XAML didn't load)
            if (!app.Resources.Contains("SeleneRowMode")) app.Resources["SeleneRowMode"] = "tv";
            // the main screen's template is applied a moment after start: retry until its parts exist
            attachTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            int tries = 0;
            attachTimer.Tick += (s, e) => { if (TryAttach()) { Ready = true; attachTimer.Stop(); } else if (++tries > 120) attachTimer.Stop(); };
            attachTimer.Start();
        }

        private static bool TryAttach()
        {
            main = Application.Current.MainWindow;
            if (main == null) return false;
            var library = HomeNav.FindByName(main, "SeleneNav_library") as ButtonBase;
            var hub = HomeNav.FindByName(main, "SeleneNav_hub") as ButtonBase;
            if (library == null || hub == null) return false;
            library.Command = new Relay(ShowLibrary);
            LoginScene.AttachHomeGlow(main);   // the login's glow behind the home's cards
            LibraryStage.Attach(main);         // Collection's background trailer
            TopProfile.Attach(main);
            StoreData.AttachCard();            // the Store card's free games
            Contrast.Apply();                  // the home's shades as deep as the wallpaper needs           // the signed-in profile beside the clock
            Brand.Attach(main);                // Selene's mark on empty screens
            // "last played" as people say it (Today, Yesterday, 3 days ago), for Spotlight and Collection
            var npc = main.DataContext as System.ComponentModel.INotifyPropertyChanged;
            if (npc != null) npc.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != "SelectedGame") return;
                HomeNav.GuardSpotlightSelection();   // Spotlight's selection stays Spotlight's (no stray game art)
                UpdateLastPlayed();
            };
            UpdateLastPlayed();
            // no login this start (intro and Who's playing? both off): Collection right away if that's the start screen
            var loginLayer = Part("AcceuilSettings");
            // Collection's "no games" message stays readable over a bright wallpaper
            AdaptiveText.Watch(Part("SeleneLibraryEmpty") as FrameworkElement, () => Contrast.WallpaperImage);
            var coreNote = Part("SeleneCoreNotice");
            if (coreNote != null) coreNote.Visibility = Visibility.Collapsed;   // the theme's note for when Core isn't running
            if (loginLayer != null && !loginLayer.IsVisible) LoginScene.AllowBackdropVideo();
            if (loginLayer != null && !loginLayer.IsVisible && StartOnCollection()) ShowLibrary();
            // Playnite's "game is running" screen (switching back to Playnite while a game runs): the home instead
            var status = HomeNav.FindByName(main, "PART_ElemGameStatus") as UIElement;
            if (status != null) status.IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) HomeNav.GameStatusShown(); };
            hub.Command = new Relay(ShowHome);
            AddonHintLater();
            var menuButton = HomeNav.FindByName(main, "PART_ButtonMainMenu") as ButtonBase;
            if (menuButton != null) menuButton.Command = new Relay(OpenMenu);
            // every way Playnite opens its main menu (Start, keys, the button) opens Selene's; Playnite's own stays reachable
            // from it under More
            var vm = main.DataContext;
            var openMenu = vm != null ? vm.GetType().GetProperty("OpenMainMenuCommand") : null;
            try
            {
                var current = openMenu != null ? openMenu.GetValue(vm, null) as ICommand : null;
                if (openMenu != null && openMenu.CanWrite && current != null && current != seleneMenu)
                {
                    PlayniteMenu.Original = current;
                    // Playnite's own command type (the property only takes that)
                    seleneMenu = new Playnite.SDK.RelayCommand<object>(o => OpenMenu());
                    openMenu.SetValue(vm, seleneMenu, null);
                    HomeNav.Log("shell: main menu is Selene's");
                }
            }
            catch (Exception ex) { HomeNav.Log("shell: couldn't take over the main menu: " + ex.Message); }
            var store = HomeNav.FindByName(main, "SeleneNav_store") as ButtonBase;
            if (store != null) store.Command = new Relay(() => LoginScene.OpenWith(() => !Features.On("storepage") ? new StoreScreen() : StoreData.ChosenCountry == null ? (Screen)new StoreCurrencyScreen(true) : new StorePage()));
            // the other cards: their own screens over the home
            Wire("SeleneNav_activity", () => new ActivityScreen());
            Wire("SeleneNav_captures", () => new CapturesScreen());
            Wire("SeleneNav_surprise", () => new SurpriseScreen());
            Wire("SeleneNav_news", () => new NewsPage(null));
            Wire("SeleneNav_achievements", () => new AchievementsHub());
            HomeNav.ApplyValueSettings();
            Ui.WatchFullRate(main);            // the screen's own refresh rate while Playnite is in front
            DetailsStage.Attach(main);         // the details page's lower half
            ApplyNowPlaying();
            ProgressToast.WatchLibraryStatus(main);   // the library update's words as a notification card
            GameFacts.CardStats();
            NewsCard.Refresh(false);
            CapturesScreen.CountInBackground();
            HomeNav.Log("shell: home/library wired");
            DevView();
            return true;
        }

        private static void Wire(string proxy, Func<Screen> make)
        {
            var b = HomeNav.FindByName(main, proxy) as ButtonBase;
            if (b != null) b.Command = new Relay(() => LoginScene.OpenWith(make));
        }

        // developer switch: %TEMP%\selene-dev-view holding "details", "library" or "menu" opens that screen a few seconds
        // after start (to look at each screen while building Selene, without a controller)
        private static void DevView()
        {
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-view");
            if (!System.IO.File.Exists(file)) return;
            var view = System.IO.File.ReadAllText(file).Trim().ToLowerInvariant();
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            later.Tick += (s, e) =>
            {
                later.Stop();
                try
                {
                    var vm = main.DataContext;
                    if (view == "library") ShowLibrary();
                    else if (view == "details" && vm != null) vm.GetType().GetProperty("GameDetailsVisible").SetValue(vm, true, null);
                    else if (view == "menu" && vm != null) { var c = vm.GetType().GetProperty("OpenMainMenuCommand").GetValue(vm, null) as ICommand; if (c != null) c.Execute(null); }
                    HomeNav.Log("shell: developer view " + view);
                }
                catch (Exception ex) { HomeNav.Log("shell: developer view failed " + ex.Message); }
            };
            later.Start();
        }

        private static void UpdateLastPlayed()
        {
            try
            {
                var vm = main != null ? main.DataContext : null;
                var game = vm != null ? vm.GetType().GetProperty("SelectedGame").GetValue(vm, null) : null;
                var prop = game != null ? game.GetType().GetProperty("LastActivity") : null;
                var when = prop != null ? prop.GetValue(game, null) as DateTime? : null;
                string text;
                if (when == null) text = "Never";
                else
                {
                    int days = (DateTime.Today - when.Value.ToLocalTime().Date).Days;
                    text = days <= 0 ? "Today" : days == 1 ? "Yesterday" : days < 7 ? days + " days ago" : when.Value.ToLocalTime().ToString("MMM d, yyyy");
                }
                Application.Current.Resources["SeleneLastPlayed"] = text;
                // the store's flat logo by "platform"
                var source = game != null ? game.GetType().GetProperty("Source") : null;
                var src = source != null ? source.GetValue(game, null) : null;
                var name = src != null ? src.GetType().GetProperty("Name").GetValue(src, null) as string : null;
                PlatformIcons.Publish(name);
            }
            catch (Exception ex) { HomeNav.Log("last played: " + ex.Message); }
        }

        private static UIElement Part(string name) { return main != null ? HomeNav.FindByName(main, name) as UIElement : null; }

        // a part the theme shows or hides by itself (its style): switched off, it stays hidden; on, the theme decides again
        public static void Switch(UIElement part, string key)
        {
            if (part == null) return;
            if (Features.On(key)) part.ClearValue(UIElement.VisibilityProperty);
            else part.Visibility = Visibility.Collapsed;
        }

        public static void ApplyNowPlaying()
        {
            Switch(Part("SeleneHltbStatHub"), "hltb");
            Switch(Part("SeleneHltbStatLibrary"), "hltb");
            Switch(Part("SeleneAchievementsStatHub"), "achievements");
            Switch(Part("SeleneLibraryStats"), "collectioninfo");
            Switch(Part("SeleneLibraryGenres"), "collectioninfo");
            Switch(Part("SeleneLibraryHints"), "collectionhints");
        }

        // Switching between Spotlight and Collection is a crossfade: the screen you leave stays up while the other one gets
        // its picture, so you never see an empty background. (Spotlight sits above Collection in the theme.)
        private static int switchCount;

        // Collection is showing (or being switched to): Spotlight's selection guard stays out of its way
        public static bool CollectionActive { get; private set; }

        // the screen you were on last, for Start on: Where you left it (a word in a file, like the filter tab)
        private static readonly string StartScreenFile = System.IO.Path.Combine(SelenePaths.PlayniteConfig, "selene-start-screen.txt");

        private static void RememberScreen(string screen)
        {
            if (Features.Value("starton") != "last") return;
            try { System.IO.File.WriteAllText(StartScreenFile, screen); } catch { }
        }

        // the Start on setting: Collection, or whichever screen was up when Playnite last closed
        public static bool StartOnCollection()
        {
            var v = Features.Value("starton");
            if (v != "last") return v == "collection";
            try { return System.IO.File.Exists(StartScreenFile) && System.IO.File.ReadAllText(StartScreenFile).Trim() == "collection"; }
            catch { return false; }
        }

        public static void ShowLibrary()
        {
            var home = Part("WelcomeHubOverlay");
            var library = Part("SeleneLibrary");
            if (home == null || library == null) return;
            int mine = ++switchCount;
            CollectionActive = true;
            RememberScreen("collection");
            HomeNav.UpdateFilterTabs();
            library.BeginAnimation(UIElement.OpacityProperty, null);
            library.Visibility = Visibility.Visible;   // under Spotlight, loading its art meanwhile
            HomeNav.SetMusic("home");   // Collection like Spotlight: the default track, a game's song with its trailer
            HomeNav.ApplyCollectionFilter();   // the same tab as Spotlight's
            var list = Part("PART_ListGameItems") as ListBox;
            if (list != null) main.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
                var item = list.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) as UIElement;
                if (item != null) item.Focus(); else list.Focus();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            // a crossfade of the live screens: Collection is already there under Spotlight, which melts away over it (a still
            // of Spotlight was drawn by WPF in software: 2-4 s on a 4K TV)
            library.BeginAnimation(UIElement.OpacityProperty, null);
            library.Opacity = 1;
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fadeOut.Completed += (s, e) =>
            {
                if (mine != switchCount) return;
                home.Visibility = Visibility.Collapsed;
                home.BeginAnimation(UIElement.OpacityProperty, null);
            };
            home.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            HomeNav.Log("shell: library");
        }

        // a note on the home when the Extra Metadata add-on (logos, trailers) isn't installed: Spotlight looks bare without it and
        // nothing says why; the setup check installs it
        private static void AddonHintLater()
        {
            if (!Features.On("setuphint")) return;
            int waited = 0;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (s, e) =>
            {
                if (++waited > 40) { t.Stop(); return; }
                if (LoginScene.IsActive || CollectionActive) return;
                var home = Part("WelcomeHubOverlay");
                if (home == null || !home.IsVisible) return;
                t.Stop();
                try
                {
                    var api = HomeNav.Api;
                    if (api == null) return;
                    // worked out off the UI thread (asking Playnite which add-ons are installed took ~120 ms: a hitch on the home)
                    var ui = Application.Current.Dispatcher;
                    System.Threading.Tasks.Task.Factory.StartNew(() =>
                    {
                        bool missing = false;
                        try { foreach (var a in SetupCheck.MissingIncluded(api)) if (a.Id.StartsWith("ExtraMetadataLoader")) missing = true; }
                        catch (Exception ex) { HomeNav.Log("add-on hint: " + ex.Message); }
                        if (missing) ui.BeginInvoke(new Action(() => Ui.Notice("Logos and trailers need the Extra Metadata add-on. Install it from Setup check in the menu.", 9000)));
                    });
                }
                catch (Exception ex) { HomeNav.Log("add-on hint: " + ex.Message); }
            };
            t.Start();
        }

        public static void ShowHome() { ShowHome(false); }

        // instant: the home is back in the same frame, no still and no fade (the game details closing over it)
        public static void ShowHome(bool instant)
        {
            var home = Part("WelcomeHubOverlay");
            var library = Part("SeleneLibrary");
            if (home == null) return;
            int mine = ++switchCount;
            CollectionActive = false;
            RememberScreen("spotlight");
            HomeNav.UpdateFilterTabs();
            if (instant)
            {
                if (library != null) { library.BeginAnimation(UIElement.OpacityProperty, null); library.Visibility = Visibility.Collapsed; }
                HomeNav.SyncSelectionForHome(null);
                home.BeginAnimation(UIElement.OpacityProperty, null);
                home.Opacity = 1;
                home.Visibility = Visibility.Visible;
                HomeNav.Log("shell: home (instant)");
                return;
            }
            // Spotlight comes back unseen over Collection, gets its own selection and art (setting its "no game" made its list
            // jump to its first game: the 007 flash), then fades in over Collection, which goes once it's covered (at most
            // 400 ms wait). Live screens, no still: WPF draws a still in software, seconds on a 4K TV.
            bool over = library != null && library.IsVisible && !LibraryStage.Playing;
            if (!over && library != null) { library.BeginAnimation(UIElement.OpacityProperty, null); library.Visibility = Visibility.Collapsed; }
            home.BeginAnimation(UIElement.OpacityProperty, null);
            home.Opacity = 0;
            home.Visibility = Visibility.Visible;
            bool revealed = false;
            System.Windows.Threading.DispatcherTimer cap = null;
            Action reveal = () =>
            {
                if (revealed || mine != switchCount) return;
                revealed = true;
                if (cap != null) cap.Stop();
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(over ? 220 : 280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                fade.Completed += (s, e) =>
                {
                    if (mine != switchCount) return;
                    home.BeginAnimation(UIElement.OpacityProperty, null);
                    home.Opacity = 1;
                    if (library != null) { library.BeginAnimation(UIElement.OpacityProperty, null); library.Visibility = Visibility.Collapsed; }
                };
                home.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            cap = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            cap.Tick += (s, e) => reveal();
            cap.Start();
            HomeNav.SyncSelectionForHome(reveal);   // Spotlight's own selection
            HomeNav.Log("shell: home");
        }

        // lays a still picture of the screen being left over everything and fades it out (false when one can't be taken)
        private static Image crossfadeStill;

        private static bool Crossfade(UIElement leaving, int ms, int holdMs)
        {
            var el = leaving as FrameworkElement;
            var host = Part("PART_ViewHost") as Panel;
            if (el == null || host == null || !el.IsVisible || el.ActualWidth < 1 || el.ActualHeight < 1) return false;
            try
            {
                if (crossfadeStill != null) host.Children.Remove(crossfadeStill);
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth), (int)Math.Ceiling(el.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(el);
                rtb.Freeze();
                var img = new Image { Source = rtb, Stretch = Stretch.Fill, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Grid.SetRowSpan(img, 99); Grid.SetColumnSpan(img, 99);
                Panel.SetZIndex(img, 1000);
                host.Children.Add(img);
                crossfadeStill = img;
                img.Tag = ms;
                if (holdMs >= 0) ReleaseStill(img, holdMs);   // -1: the caller lets it go (ReleaseStill)
                return true;
            }
            catch (Exception ex) { HomeNav.Log("crossfade still: " + ex.Message); return false; }
        }

        // B on a game's page opened from Spotlight: a still of the page lies over everything while Spotlight comes back under
        // it, then melts away (the page cut straight to Spotlight)
        public static void FadeFromDetails(UIElement details, int ms)
        {
            if (details != null && Features.On("smoothswitch") && Crossfade(details, ms, 40)) HomeNav.Log("shell: details fade out over " + ms + " ms");
        }

        private static void ReleaseStill(Image img, int holdMs = 0)
        {
            if (img == null || !(img.Tag is int)) return;
            int ms = (int)img.Tag;
            img.Tag = null;   // once
            var host = VisualTreeHelper.GetParent(img) as Panel;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(holdMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            fade.Completed += (s, e) => { if (host != null) host.Children.Remove(img); if (crossfadeStill == img) crossfadeStill = null; };
            img.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        public static void OpenMenu()
        {
            if (LoginScene.IsActive) return;
            LoginScene.OpenWith(() => new MenuScreen());
        }

        // brings the login layer back (switch profile, setup check, edit profile from the menu)
        public static void ShowLogin()
        {
            var login = Part("AcceuilSettings");
            if (login == null) return;
            HomeNav.QuietFocus(800);   // focus moves into the login layer: not a move
            Raise(login, true);
            login.BeginAnimation(UIElement.OpacityProperty, null);
            login.Opacity = 0;
            login.Visibility = Visibility.Visible;
            // over the details page (a capture): quicker, it's one picture opening
            login.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(raised != null ? 160 : 320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // the game details page sits after the login layer's grid in Main.xaml: opened from there (a capture), that grid goes
        // over the page, and everything in it but the layer is masked out, so the page stays underneath as the layer fades in
        private static readonly List<Tuple<UIElement, Brush>> masked = new List<Tuple<UIElement, Brush>>();
        private static UIElement raised;
        private static Brush raisedBackground;   // the raised grid's own dark fill, lifted meanwhile (it covered the page: black under the menu)

        private static void Raise(UIElement login, bool on)
        {
            if (!on)
            {
                foreach (var m in masked) m.Item1.OpacityMask = m.Item2;
                masked.Clear();
                if (raised != null) Panel.SetZIndex(raised, 0);
                var lowered = raised as Panel;
                if (lowered != null && raisedBackground != null) lowered.Background = raisedBackground;
                raisedBackground = null;
                raised = null;
                return;
            }
            DependencyObject child = login, parent = VisualTreeHelper.GetParent(login);
            FrameworkElement details = null;
            while (parent != null)
            {
                var panel = parent as Panel;
                details = panel != null ? panel.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Name == "PART_ElemGameDetails") : null;
                if (details != null) break;
                child = parent;
                parent = VisualTreeHelper.GetParent(parent);
            }
            if (details == null || !details.IsVisible || !(child is UIElement) || raised != null) return;
            raised = (UIElement)child;
            Panel.SetZIndex(raised, 1);
            var grid = raised as Panel;
            if (grid != null && grid.Background != null) { raisedBackground = grid.Background; grid.Background = Brushes.Transparent; }
            for (DependencyObject at = login; at != child; )
            {
                var up = VisualTreeHelper.GetParent(at);
                var panel = up as Panel;
                if (panel != null)
                    foreach (UIElement c in panel.Children)
                        if (!ReferenceEquals(c, at)) { masked.Add(Tuple.Create(c, c.OpacityMask)); c.OpacityMask = Brushes.Transparent; }
                at = up;
            }
        }

        // the login layer goes when the profile is picked (it was the theme plugin's job before)
        public static void HideLogin()
        {
            var login = Part("AcceuilSettings");
            if (login == null) return;
            HomeNav.QuietFocus(1200);   // focus goes back to the home
            if (!LoginScene.KeepHome && StartOnCollection()) { ShowLibrary(); HomeNav.Log("start on Collection"); }
            else if (!LoginScene.KeepHome) HomeNav.HoldHomeForIntro();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(raised != null ? 200 : 420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (s, e) => { login.Visibility = Visibility.Collapsed; login.BeginAnimation(UIElement.OpacityProperty, null); login.Opacity = 1; Raise(login, false); };
            login.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
