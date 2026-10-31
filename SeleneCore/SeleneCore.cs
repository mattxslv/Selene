using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace Selene
{
    // Selene Core: the companion add-on of the Selene fullscreen theme. Does nothing with any other theme.
    public class SeleneCorePlugin : GenericPlugin
    {
        public const string ThemeId = "Selene_a98e0027-fa6a-4f5e-a087-bad8d69f2443";
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly SeleneSettingsViewModel settings;

        public override Guid Id { get { return Guid.Parse("cd424c29-97f4-4dd0-aa66-4d8e01cc8724"); } }

        public SeleneCorePlugin(IPlayniteAPI api) : base(api)
        {
            settings = new SeleneSettingsViewModel(this);
            SelenePaths.Init(this, PlayniteApi, settings.Settings);
            Features.Init(settings.Settings, () => SavePluginSettings(settings.Settings));
            Profiles.Init(GetPluginUserDataPath());
            Properties = new GenericPluginProperties { HasSettings = true };
            // Playnite selects its first elements while starting, and each played the move sound: quiet until the
            // login is up (or the user's first press)
            if (Active) HomeNav.QuietFocus(8000);
        }

        // Playnite's first selection comes before its start: UniPlaySong (loaded after Selene) plays that game's song on it
        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            if (!HomeNav.HushedAtStart && Active) HomeNav.HushAtStart(PlayniteApi);
            if (Active && !Features.On("music")) HomeNav.LetMusicGo();
        }

        private bool Active
        {
            get { return PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen && SelenePaths.ActiveThemeId == ThemeId; }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            LegacyTasks.RepairLaunchers(PlayniteApi);
            if (!Active)
            {
                logger.Info("Selene Core: the Selene theme isn't in use (fullscreen theme: " + SelenePaths.ActiveThemeId + "), staying off");
                HomeNav.CleanMusicLeftovers(PlayniteApi);   // what Selene left in the music add-on's saved settings must not silence another theme
                return;
            }
            HomeNav.HushAtStart(PlayniteApi);
            HomeNav.Log("paths: config " + SelenePaths.PlayniteConfig + " | theme " + SelenePaths.ThemeDir + (PlayniteApi.Paths.IsPortable ? " | portable" : ""));
            HomeNav.Api = PlayniteApi;
            SongSetup.DisableManagedAutomaticDownloads();
            SongSetup.ApplySongSettle();
            SongSetup.ApplyDefaultMusic();
            GamesSetup.AskLater();
            XboxSignIn.AskLater();
            if (Features.On("perflog")) PerformanceRecorder.SetRecording(true);
            StartupWork.Start(() => SeleneShell.Ready && Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible
                && !LoginScene.StartingUp && !LoginScene.InSetup && !HomeNav.IntroRunning && HomeNav.StartupReady);
            if (Features.On("translate")) Lang.Load(PlayniteApi);
            HomeNav.Install();
            BackgroundFit.Apply();
            Lang.CollectIfAsked();   // the words to translate, collected on the Desktop (Advanced switch)
            Lang.Refresh();
            StartupWork.AfterHome("save backup settings", () => { if (Features.On("quietbackups")) LudusaviApp.Quiet(PlayniteApi); });
            try { Profiles.ApplyLook(); }
            catch (Exception ex)
            {
                logger.Error(ex, "Selene Core: couldn't apply the profile's theme");
                PlayniteApi.Notifications.Add(new NotificationMessage("selene-theme", "Selene couldn't apply your theme, using the default look: " + ex.Message, NotificationType.Error));
            }
            CollectionPresets.Ensure(PlayniteApi);   // Collection's LB / RB tabs
            HomeNav.RefreshRowFilters();              // the five tabs, then your own filter presets
            Ui.RefreshPrompts();                      // Xbox or PlayStation names in the hints
            SeleneShell.Start();
            StartupWork.AfterHome("game songs", () => { if (!MediaDownloads.Unified || !MediaDownloads.Enabled) SongSetup.StartLater(); });
            StartupWork.AfterHome("library media", () => { if (MediaDownloads.Enabled) MediaDownloads.Start(); });
            StartupWork.AfterHome("Steam avatar", () => { if (string.IsNullOrWhiteSpace(settings.Settings.AvatarPath)) AnimatedAvatar.Fetch(SelenePaths.SteamId); });
            HomeNav.StartIsolatedMute();              // (dev: a test copy beside a PC in use stays silent)
            StartupWork.AfterHome("legacy tasks", LegacyTasks.CheckLater);
            HomeNav.Log("Selene Core started");
        }

        // developer switch %TEMP%\selene-dev-slowquit: Selene Core takes 6 s (or the seconds in the file) to close, inside Playnite's "Closing" step, to see
        // the closing screen that comes back when quitting takes long
        public override void Dispose()
        {
            var slow = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-slowquit");
            if (System.IO.File.Exists(slow))
            {
                int secs; if (!int.TryParse(System.IO.File.ReadAllText(slow).Trim(), out secs) || secs <= 0) secs = 6;   // (the file may hold the seconds)
                HomeNav.Log("dev slow quit: closing takes " + secs + " s");
                System.Threading.Thread.Sleep(secs * 1000);
            }
            base.Dispose();
        }

        // Playnite's close has really begun (its quit step tells every add-on): Selene's quit watch stops waiting for it
        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            PerformanceRecorder.Stop(true);
            HomeNav.ShutdownBegun = true;
            HomeNav.Log("quit: Playnite is closing");
            if (Active) { HomeNav.ReleaseMusic(); HomeNav.CleanMusicLeftovers(PlayniteApi); }   // the music add-on is saved as it should be
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            PerformanceRecorder.Mark("game_start_requested", args.Game != null ? args.Game.Name : "unknown");
            var action = args.SourceAction;
            if (action == null && args.Game != null && args.Game.GameActions != null)
                action = args.Game.GameActions.FirstOrDefault(a => a.IsPlayAction);
            if (action != null && action.Type == Playnite.SDK.Models.GameActionType.File && LegacyElevation.IsLauncher(action.Path, action.Arguments))
            {
                args.CancelStartup = true;
                PlayniteApi.Dialogs.ShowMessage("Selene couldn't recover this game's launcher from its old administrator task. Set its Play action to the game's executable in Playnite. If the task was already removed, use its saved details or launcher backup.", "Update game launcher");
                return;
            }
            if (Active && args.Game != null) HomeNav.GameStarting(args.Game.Id);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            PerformanceRecorder.Mark("game_stopped", args.Game != null ? args.Game.Name : "unknown");
            if (!Active || args.Game == null) return;
            HomeNav.GameEnded();   // the next time Selene is in front, its screen is renewed (#24)
            Profiles.RecordSession(args.Game.Id, args.ElapsedSeconds);
            // back from a game: a card says how long it was played (once Selene is in front again) (any session over 10 s: a short one showed nothing)
            if (Features.On("sessioncard") && args.ElapsedSeconds >= 10)
            {
                var game = args.Game; var secs = args.ElapsedSeconds;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
                    t.Tick += (s, e) => { t.Stop(); ProgressToast.ShowSession(game, secs); };
                    t.Start();
                }));
            }
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            // store imports mark deleted games installed again: folder gone = not installed
            if (Active) LocalGames.MarkGoneFolders();
        }

        // Extensions menu: back to Who's playing?, the profile steps for the signed-in profile, or the setup check
        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            if (!Active) yield break;
            yield return new MainMenuItem { MenuSection = "@Selene", Description = "Switch profile", Action = a => LoginScene.OpenWith(() => new WhoScreen(null)) };
            if (Profiles.Active != null)
                yield return new MainMenuItem
                {
                    MenuSection = "@Selene", Description = "Edit profile (name, avatar, theme, background)",
                    Action = a => { var p = Profiles.Active; if (p != null) LoginScene.OpenWith(() => new ProfileSteps(p, null, q => LoginScene.Finish())); }
                };
            yield return new MainMenuItem { MenuSection = "@Selene", Description = "Setup check", Action = a => LoginScene.OpenWith(() => new SetupScreen(false)) };
        }

        public override ISettings GetSettings(bool firstRunSettings) { return settings; }

        public override UserControl GetSettingsView(bool firstRunSettings) { return SeleneSettingsView.Build(settings); }
    }

    public class SeleneSettings
    {
        public string MediaFolder { get; set; }
        public List<string> GameFolders { get; set; }
        public string HelperPath { get; set; }
        public string StreamLogsFolder { get; set; }
        public string AvatarPath { get; set; }
        public string DisplayName { get; set; }
        public string StreamClientMatch { get; set; }
        public string StreamCredentialsFile { get; set; }
        // Retained only for settings deserialization; legacy task names are never used.
        public string HelperTask { get; set; }
        public string WallpapersFolder { get; set; }
        // more folders with screenshots and clips, for the Captures card
        public List<string> CapturesFolders { get; set; }
        // the settings that are a choice of values (Features.All with Choices), by key
        public Dictionary<string, string> Values { get; set; }
        // Selene features switched off (Features.All keys); everything else is on
        public List<string> TurnedOff { get; set; }
        // features that start off (Features.All with DefaultOn = false) and were switched on
        public List<string> TurnedOn { get; set; }

        public SeleneSettings()
        {
            MediaFolder = ""; GameFolders = new List<string>(); HelperPath = ""; StreamLogsFolder = ""; AvatarPath = ""; DisplayName = "";
            StreamClientMatch = ""; StreamCredentialsFile = ""; HelperTask = ""; WallpapersFolder = ""; CapturesFolders = new List<string>(); Values = new Dictionary<string, string>(); TurnedOff = new List<string>(); TurnedOn = new List<string>();
        }
    }

    public class SeleneSettingsViewModel : ObservableObject, ISettings
    {
        private readonly SeleneCorePlugin plugin;
        private SeleneSettings editing;
        public SeleneSettings Settings { get; private set; }

        public SeleneSettingsViewModel(SeleneCorePlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<SeleneSettings>() ?? new SeleneSettings();
        }

        public void BeginEdit() { editing = Playnite.SDK.Data.Serialization.GetClone(Settings); }

        public void CancelEdit()
        {
            if (editing == null) return;
            Settings.MediaFolder = editing.MediaFolder; Settings.GameFolders = editing.GameFolders;
            Settings.HelperPath = editing.HelperPath; Settings.StreamLogsFolder = editing.StreamLogsFolder;
            Settings.AvatarPath = editing.AvatarPath; Settings.DisplayName = editing.DisplayName;
            Settings.StreamClientMatch = editing.StreamClientMatch; Settings.StreamCredentialsFile = editing.StreamCredentialsFile; Settings.HelperTask = editing.HelperTask;
            string gridKey = null;   // (written straight to the live settings by the page's field: kept over the copy made before editing)
            bool hasGridKey = Settings.Values != null && Settings.Values.TryGetValue("steamgriddb-key", out gridKey);
            Settings.WallpapersFolder = editing.WallpapersFolder; Settings.CapturesFolders = editing.CapturesFolders; Settings.Values = editing.Values; Settings.TurnedOff = editing.TurnedOff; Settings.TurnedOn = editing.TurnedOn;
            if (hasGridKey) { if (Settings.Values == null) Settings.Values = new Dictionary<string, string>(); Settings.Values["steamgriddb-key"] = gridKey; }
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
            SelenePaths.Init(plugin, plugin.PlayniteApi, Settings);
            Features.Init(Settings, () => plugin.SavePluginSettings(Settings));
        }

        public bool VerifySettings(out List<string> errors) { errors = new List<string>(); return true; }
    }

    // settings page, built in code (no XAML compiler needed to build Selene Core)
    public static class SeleneSettingsView
    {
        public static UserControl Build(SeleneSettingsViewModel vm)
        {
            var s = vm.Settings;
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Features (also in Selene's menu under Selene settings):", FontWeight = FontWeights.SemiBold });
            foreach (var f in Features.All)
            {
                if (f.IsChoice || f.Action != null || f.Get != null || DownloadConsent.IsControlled(f.Key)) continue;
                var key = f.Key;
                var box = new CheckBox { Content = f.Label + "  -  " + f.About, IsChecked = Features.On(key), Margin = new Thickness(0, 6, 0, 0) };
                box.Checked += (o, e) => Features.Mark(s, key, true);
                box.Unchecked += (o, e) => Features.Mark(s, key, false);
                panel.Children.Add(box);
            }
            foreach (var f in Features.All)
            {
                if (!f.IsChoice || DownloadConsent.IsControlled(f.Key) || (f.Available != null && !f.Available())) continue;
                var key = f.Key; var opt = f;
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var combo = new ComboBox { ItemsSource = f.Names ?? f.Choices, SelectedIndex = Math.Max(0, Array.IndexOf(f.Choices, Features.Value(key))), Width = 190, Margin = new Thickness(0, 0, 10, 0) };
                combo.SelectionChanged += (o, e) =>
                {
                    if (combo.SelectedIndex < 0) return;
                    if (s.Values == null) s.Values = new Dictionary<string, string>();
                    s.Values[key] = opt.Choices[combo.SelectedIndex];
                };
                line.Children.Add(combo);
                line.Children.Add(new TextBlock { Text = f.Label + "  -  " + f.About, VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(line);
            }
            panel.Children.Add(new TextBlock { Text = "Optional media downloads start off. Choose their scope and review storage locations in Selene settings > Spotlight or Sound & music.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) });
            Func<string, string, Action<string>, TextBox> field = (label, value, set) =>
            {
                panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 4) });
                var box = new TextBox { Text = value ?? "" };
                box.TextChanged += (o, e) => set(box.Text);
                panel.Children.Add(box);
                return box;
            };
            field("Media folder (downloaded art, preview clips, screenshots). Empty = Selene Core's own data folder:", s.MediaFolder, v => s.MediaFolder = v);
            var games = field("Folders with games no store knows about (one per line):", string.Join(Environment.NewLine, s.GameFolders ?? new List<string>()),
                v => s.GameFolders = new List<string>(v.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)));
            games.AcceptsReturn = true; games.MinHeight = 60;
            var caps = field("Folders with your screenshots and clips, for the Captures card (one per line; Steam, Videos\\Captures and Videos\\NVIDIA are looked at anyway):", string.Join(Environment.NewLine, s.CapturesFolders ?? new List<string>()),
                v => s.CapturesFolders = new List<string>(v.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)));
            caps.AcceptsReturn = true; caps.MinHeight = 60;
            field("Your wallpapers folder (images in it and its subfolders show up in the background picker). Empty = only Selene's wallpapers:", s.WallpapersFolder, v => s.WallpapersFolder = v);
            var gridKey = Features.Remembered("steamgriddb-key");
            field("SteamGridDB API key, for square tile art (free at steamgriddb.com > Preferences > API). Empty = the SteamGridDB add-on's key, if any:", gridKey, v => SquareFetch.SetKey(v));
            field("Login name. Empty = your Steam name:", s.DisplayName, v => s.DisplayName = v);
            field("Login picture (image file). Empty = your Steam avatar:", s.AvatarPath, v => s.AvatarPath = v);
            field("Optional companion program (.exe) Selene starts for Sleep, Restart, Shut down and to restart Playnite. It runs with Playnite's own rights, so only choose a program you trust. Empty = off:", s.HelperPath, v => s.HelperPath = v);
            field("Optional game-streaming host log folder (a streaming client sees every game). Empty = off:", s.StreamLogsFolder, v => s.StreamLogsFolder = v);
            field("Streaming client name to react to (regular expression). Empty = any client:", s.StreamClientMatch, v => s.StreamClientMatch = v);
            field("Streaming host credentials file (disconnect clients before sleep). Empty = off:", s.StreamCredentialsFile, v => s.StreamCredentialsFile = v);
            return new UserControl { Content = panel };
        }
    }

    // Every Selene feature someone might not want, each with an on/off switch (add-on settings page and Selene settings
    // in the menu). A new feature gets a line here and a Features.On("key") check where it starts.
    public static class Features
    {
        // an add-on is installed (Playnite answers by reading every extension folder on disk: asked for each settings row as a
        // group was shown, it held up settings; looked up again at most every 10 s)
        private static string[] addons; private static DateTime addonsAt = DateTime.MinValue;
        public static bool HasAddon(string prefix)
        {
            if (HomeNav.Api == null) return false;
            if (addons == null || (DateTime.Now - addonsAt).TotalSeconds > 10) { addons = HomeNav.Api.Addons.Addons.ToArray(); addonsAt = DateTime.Now; }
            return addons.Any(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public class Option
        {
            public string Key, Label, About, Group = "Games";
            public bool DefaultOn = true;
            // a choice instead of a switch: the values, how they read, and the one it starts on
            public string[] Choices, Names;
            public string Default;
            public bool IsChoice { get { return Choices != null; } }
            public bool Slider;   // a number along a line (Left / Right) instead of a list to pick from
            public string Action; // a row that does something on A (its button's label) instead of a switch
            // a switch that is another add-on's setting: read and written there (not in Selene's settings), shown only when
            // that add-on is there
            public Func<bool> Get, Available;
            public Func<string> Note;   // a third line under the words, read when the row is built (a count, a state)
            public Func<double> Progress;   // a thin bar under that line while something runs: 0..1 (below 0: none), read when the row is built
            public Action<bool> Put;
        }

        // the groups Selene settings shows, in order (each option names its group)
        public static readonly string[] Groups = { "Start-up", "Spotlight", "Collection", "Notifications", "Sound & music", "Look", "Games", "Advanced" };

        public static readonly Option[] All =
        {
            // ---- Start-up ----
            new Option { Group = "Start-up", Key = "intro", Label = "Logo intro", About = "Show the Selene logo when Playnite starts." },
            new Option { Group = "Start-up", Key = "startsound", Label = "Start-up sound", About = "Play a chime with the start-up logo." },
            new Option { Group = "Start-up", Key = "login", Label = "Who's playing?", About = "Choose who's playing each time Playnite starts. When this is off, the last profile signs in automatically." },
            new Option { Group = "Start-up", Key = "starton", Label = "Start on", About = "Choose the screen Playnite opens on.", Choices = new[] { "spotlight", "collection", "last" }, Names = new[] { "Spotlight", "Collection", "Where you left it" }, Default = "spotlight" },
            new Option { Group = "Start-up", Key = "starttab", Label = "Starting tab", About = "Choose the tab Spotlight opens on.", Choices = new[] { "last", "all", "installed", "recent", "favorites", "unplayed" }, Names = new[] { "Where you left it", "All games", "Installed", "Recently played", "Favorites", "Not played yet" }, Default = "last" },
            new Option { Group = "Start-up", Key = "startrefresh", Label = "Update library at start-up", About = "Check for new and removed games after Playnite starts." },
            // ---- Spotlight ----
            new Option { Group = "Spotlight", Key = "cardactivity", Label = "Activity card", About = "Show what you've been playing and for how long." },
            new Option { Group = "Spotlight", Key = "actualplatform", Label = "Use game platform metadata", About = "Show the selected game's platform instead of its store." },
            new Option { Group = "Spotlight", Key = "fullscreenpresets", Label = "Honor fullscreen preset visibility", About = "Show presets enabled for fullscreen mode." },
            new Option { Group = "Spotlight", Key = "consistentfavorites", Label = "Use the same Favorites in game options", About = "Use profile Favorites in game options and Spotlight." },
            new Option { Group = "Spotlight", Key = "activitysessions", Label = "Play sessions in Activity", About = "Show play sessions and totals. Requires GameActivity.", Available = Sessions.Available },
            new Option { Group = "Spotlight", Key = "cardcaptures", Label = "Captures card", About = "Show your screenshots and video clips." },
            new Option { Group = "Spotlight", Key = "cardachievements", Label = "Achievements card", About = "Show achievements from all your games, newest first.", Available = AchievementsData.Available },
            new Option { Group = "Spotlight", Key = "capturesfolders", Label = "Captures folders", About = "Add a folder with your screenshots and clips. Pick one that's listed to remove it.", Action = "Add", Note = () => SelenePaths.CapturesFolders.Count == 0 ? "Steam, Videos and NVIDIA folders only" : string.Join(" · ", SelenePaths.CapturesFolders) },
            new Option { Group = "Spotlight", Key = "cardsurprise", Label = "Surprise me card", About = "Let Selene pick a game for you." },
            new Option { Group = "Spotlight", Key = "cardnews", Label = "News card", About = "Show headlines from the news feeds you follow." },
            new Option { Group = "Spotlight", Key = "cardpictures", Label = "Pictures under cards", About = "Show recent captures, recently played games and suggestions under the cards." },
            new Option { Group = "Spotlight", Key = "steadyhold", Label = "Steady hold scrolling", About = "Holding left or right moves through games at a steady pace the row can keep up with." },
            new Option { Group = "Spotlight", Key = "pickscroll", Label = "Scroll to a picked game", About = "Scroll along the row to a game you picked from search or another screen." },
            new Option { Group = "Spotlight", Key = "rowedges", Label = "Full-width row", About = "Let the game row reach the edges of the screen." },
            new Option { Group = "Spotlight", Key = "trailerdelay", Label = "Trailer delay", About = "Set how long to stay on a game before its trailer plays.", Choices = new[] { "1", "2.5", "5", "0" }, Names = new[] { "1 second", "2.5 seconds", "5 seconds", "Never" }, Default = "2.5" },
            new Option { Group = "Spotlight", Key = "trailerdownload", Label = "Download trailers", About = "Off until you choose a scope and confirm the save location. Each trailer is about 20 MB.", Choices = new[] { "off", "browse", "all" }, Names = new[] { "Off", "Games you select", "Whole library" }, Default = "off", Note = Background.ArtNote, Progress = Background.ArtProgress },
            new Option { Group = "Spotlight", Key = "artdownload", Label = "Download logos and backgrounds", About = "Get missing logos and backgrounds for selected games.", DefaultOn = false, Note = Background.ArtNote, Progress = Background.ArtProgress },
            new Option { Group = "Spotlight", Key = "screenshotsdownload", Label = "Download screenshots", About = "Download screenshots for games you select. Existing pictures work while downloads are off.", DefaultOn = false },
            new Option { Group = "Spotlight", Key = "trailersound", Label = "Trailer sound", About = "Play trailers with sound. Press R3 to turn the sound on or off.", DefaultOn = false },
            new Option { Group = "Spotlight", Key = "filtertabs", Label = "Filter tabs", About = "Choose Selene's tabs or Playnite presets.", Choices = new[] { "selene", "playnite" }, Names = new[] { "Selene's", "Playnite's presets" }, Default = "selene" },
            new Option { Group = "Spotlight", Key = "shade", Slider = true, Label = "Background shade", About = "Set how much the artwork darkens behind text.", Choices = new[] { "0", "0.25", "0.5", "0.75", "1" }, Names = new[] { "Off", "25%", "50%", "75%", "100%" }, Default = "1" },
            new Option { Group = "Spotlight", Key = "idle", Label = "Idle screen", About = "Show game artwork after a period of inactivity.", Choices = new[] { "0", "5", "10", "20" }, Names = new[] { "Never", "5 minutes", "10 minutes", "20 minutes" }, Default = "10" },
            // ---- Collection ----
            new Option { Group = "Collection", Key = "collectionglide", Label = "Smooth scrolling", About = "Glide between covers in Collection instead of jumping." },
            new Option { Group = "Look", Key = "logosize", Label = "Even logo sizes", About = "Size Collection logos by their shape, so tall logos aren't small." },
            new Option { Group = "Look", Key = "squareart", Label = "Square art from SteamGridDB", About = "Get square tile art from SteamGridDB with the art downloads. Needs a free key." },
            new Option { Group = "Look", Key = "steamgridkey", Label = "SteamGridDB key", About = "Paste a free key from steamgriddb.com.", Action = "Paste", Note = SquareFetch.Status },
            new Option { Group = "Collection", Key = "collection23", Label = "2:3 covers", About = "Shape Collection's tiles like the covers, so none are cut. Also sets Playnite's grid to 2:3." },
            new Option { Group = "Collection", Key = "collectionhero", Label = "Same art as Spotlight", About = "Show Spotlight's game art behind Collection too. Applies after restarting Playnite." },
            new Option { Group = "Collection", Key = "librarytrailer", Label = "Collection trailers", About = "Play a game's trailer when you stay on it. Press R3 for sound." },
            new Option { Group = "Collection", Key = "collectioninfo", Label = "Game details", About = "Show playtime, last played, release year and genres under the game's logo." },
            new Option { Group = "Collection", Key = "collectionhints", Label = "Button hints", About = "Show button hints at the bottom of Collection." },
            new Option { Group = "Collection", Key = "cardcollection", Label = "Collection card on Spotlight", About = "Show a card on Spotlight that opens Collection.", DefaultOn = false },
            // ---- Notifications ----
            new Option { Group = "Notifications", Key = "notifysound", Label = "Notification sound", About = "Play a sound when a notification appears." },
            new Option { Group = "Notifications", Key = "sessioncard", Label = "Session card", About = "Show how long you played after you close a game." },
            new Option { Group = "Notifications", Key = "taskcards", Label = "Progress cards", About = "Show cards while downloads and library updates run. Off: only when they finish.", DefaultOn = false },
            new Option { Group = "Notifications", Key = "progresstoast", Label = "Download notifications", About = "Show downloads in a small notification instead of a window." },
            new Option { Group = "Notifications", Key = "updatecheck", Label = "Update notifications", About = "Check for a new version of Selene once a day." },
            new Option { Group = "Notifications", Key = "setuphint", Label = "Add-on tips", About = "Let you know when logos and trailers need the Extra Metadata add-on." },
            // ---- Sound & music ----
            new Option { Group = "Sound & music", Key = "sounds", Label = "Interface sounds", About = "Play sounds when you move, select and go back." },
            new Option { Group = "Sound & music", Key = "quietdetails", Label = "Quiet details transitions", About = "Avoid extra navigation ticks when opening game details." },
            new Option { Group = "Look", Key = "defaultbackdrop", Label = "Selene background fallback", About = "Use Selene's background when no game art is showing. Restart Playnite to apply." },
            new Option { Group = "Sound & music", Key = "soundmenu", Label = "Sound in the menu", About = "Change the volume and audio output from the menu." },
            new Option { Group = "Sound & music", Key = "livevolume", Label = "Keep volume display in sync", About = "Keep the Sound panel in sync with Windows volume." },
            new Option { Group = "Sound & music", Key = "soundvolume", Slider = true, Label = "Interface sounds volume", About = "Set the volume of interface sounds.", Choices = new[] { "25", "50", "75", "100" }, Names = new[] { "25%", "50%", "75%", "100%" }, Default = "100" },
            new Option { Group = "Sound & music", Key = "clipvolume", Slider = true, Label = "Clip volume", About = "Set the volume of video clips in Captures.", Available = () => false /* set with Up / Down in Captures only */, Choices = new[] { "0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100" }, Names = new[] { "Off", "10%", "20%", "30%", "40%", "50%", "60%", "70%", "80%", "90%", "100%" }, Default = "90" },
            new Option { Group = "Sound & music", Key = "songsetup", Label = "Get game songs", About = "Get missing game songs. Required tools use about 130 MB.", DefaultOn = false, Note = SongSetup.Status, Progress = Background.SongsProgress, Available = () => HasAddon("UniPlaySong") },
            new Option { Group = "Sound & music", Key = "selenemusic", Label = "Selene OST as default music", About = "Use Selene OST when no default music file is selected." },
            new Option { Group = "Sound & music", Key = "songsettle", Label = "Songs wait until you stop", About = "Wait until scrolling stops before changing songs.", Available = () => HasAddon("UniPlaySong") },
            new Option { Group = "Sound & music", Key = "music", Label = "Background music", About = "Play music while you browse. Requires the UniPlaySong add-on.", Available = () => HasAddon("UniPlaySong") },
            new Option { Group = "Sound & music", Key = "independentmusic", Label = "Game music without trailers", About = "Play game songs without waiting for a trailer. Requires UniPlaySong.", DefaultOn = false },
            new Option { Group = "Sound & music", Key = "launchquiet", Label = "Quiet loading screen", About = "Keep the loading screen silent until the game appears." },
            new Option { Group = "Games", Key = "launchchoice", Label = "Keep launch choices above loading", About = "Show launch choices before the loading screen." },
            // ---- Look ----
            new Option { Group = "Games", Key = "metasource", Label = "Metadata source per game", About = "Choose where a game's screenshots come from, in Game options." },
            new Option { Group = "Look", Key = "trimlogos", Label = "Fit game logos", About = "Remove empty space around game logos so they appear the same size." },
            new Option { Group = "Look", Key = "smartcrop", Label = "Smart cover crop", About = "Crop covers around their most detailed area." },
            new Option { Group = "Look", Key = "activitycoverfit", Label = "Keep whole covers in Activity", About = "Show complete covers in Activity." },
            new Option { Group = "Look", Key = "trailerlogogap", Label = "Keep logos below trailers", About = "Keep game logos clear of expanded trailers." },
            new Option { Group = "Look", Key = "detailsbadges", Label = "Round game-page button badges", About = "Use round button badges on game pages." },
            new Option { Group = "Look", Key = "captureplayerfit", Label = "Fit capture controls to screen", About = "Fit playback controls to your screen." },
            new Option { Group = "Look", Key = "backgroundfit", Label = "Background sizing", About = "Choose how background images fill the screen.", Choices = new[] { "fill", "fit", "stretch" }, Names = new[] { "Fill", "Fit", "Stretch" }, Default = "fill" },
            new Option { Group = "Look", Key = "language", Label = "Language", About = "Choose the language for Selene.", Choices = new[] { "auto", "en", "de", "es", "fr", "pt_BR", "zh_CN", "ja", "fil" }, Names = new[] { "Follow Playnite", "English", "Deutsch", "Español", "Français", "Português (Brasil)", "简体中文", "日本語", "Filipino" }, Default = "auto" },
            new Option { Group = "Look", Key = "languagerestart", Label = "Ask to restart only when the language changes", About = "Selene asks to restart Playnite after you pick a language only if it would show different words. Off: it asks after every change." },
            new Option { Group = "Games", Key = "guidefocus", Label = "Focus on Guide", About = "Bring Selene to the front when you press the Guide button. Turn this off if another overlay uses the Guide button." },
            new Option { Group = "Look", Key = "homebackground", Label = "Spotlight background", About = "Choose what appears behind the selected game.", Choices = new[] { "art", "wallpaper" }, Names = new[] { "Game art", "Wallpaper only" }, Default = "art" },
            new Option { Group = "Look", Key = "glow", Label = "Animated background", About = "Show a moving background behind the profile screen and cards." },
            new Option { Group = "Look", Key = "guidefade", Label = "Fade in over a game", About = "Fade from your game into Selene when you press the Guide button." },
            new Option { Group = "Games", Key = "gameoptions", Label = "Selene game options", About = "Show a game's options in Selene's style." },
            new Option { Group = "Games", Key = "achievements", Label = "Achievements on a game's page", About = "Show achievements on Spotlight and on a game's page.", Available = AchievementsData.Available, Note = Background.AchievementsNote, Progress = Background.AchievementsProgress },
            new Option { Group = "Games", Key = "achcelebrate", Label = "Celebrate completed games", About = "Celebrate when you unlock every achievement in a game." },
            new Option { Group = "Games", Key = "achsetup", Label = "Set up achievements automatically", About = "Set up Playnite Achievements for you and keep your games up to date.", Available = AchievementsData.Available },
            new Option { Group = "Games", Key = "metadatacomplete", Label = "Complete missing game details", About = "Opt in to missing metadata, covers and backgrounds for your library. Existing details are kept.", DefaultOn = false },
            new Option { Group = "Games", Key = "hltb", Label = "HowLongToBeat times", About = "Show how long a game takes to beat. Requires the HowLongToBeat add-on.", Available = HltbFetch.Installed },
            new Option { Group = "Games", Key = "hltbauto", Label = "HowLongToBeat for new games", About = "Get HowLongToBeat times when games are added to your library.", Get = HltbFetch.AutoImport, Put = HltbFetch.SetAutoImport, Available = HltbFetch.Installed },
            new Option { Group = "Look", Key = "topprofile", Label = "Profile in the top bar", About = "Show your avatar next to the clock." },
            new Option { Group = "Look", Key = "textshadow", Label = "Text shadow", About = "Add a soft shadow behind text over bright images." },
            new Option { Group = "Look", Key = "smoothswitch", Label = "Fade back to Spotlight", About = "Fade back to Spotlight when you leave a game's page." },
            new Option { Group = "Look", Key = "screenshotskeleton", Label = "Screenshot loading cards", About = "Show a moving shine while screenshots load." },
            new Option { Group = "Look", Key = "dialogs", Label = "Selene dialogs", About = "Show Playnite's messages in Selene's style." },
            new Option { Group = "Look", Key = "hints", Label = "Button hints", About = "Show button hints at the bottom of the screen." },
            new Option { Group = "Look", Key = "prompts", Label = "Button names", About = "Choose which controller's buttons the hints show.", Choices = new[] { "playnite", "xbox", "playstation" }, Names = new[] { "As Playnite's setting", "Xbox", "PlayStation" }, Default = "playnite" },
            // ---- Games ----
            new Option { Group = "Spotlight", Key = "newsfeeds", Label = "News feeds", About = "Add, remove or turn off the feeds on the News card.", Action = "Manage" },
            new Option { Group = "Games", Key = "launchstyle", Label = "Launch screen style", About = "Choose what appears while a game starts.", Choices = new[] { "artlogo", "art", "logo", "minimal" }, Names = new[] { "Art and logo", "Art only", "Logo on black", "Minimal" }, Default = "artlogo" },
            new Option { Group = "Games", Key = "launchmotion", Label = "Launch screen motion", About = "Slowly zoom the artwork while a game starts." },
            new Option { Group = "Games", Key = "launchshade", Label = "Launch screen shade", About = "Darken the artwork so the game's logo stands out.", Choices = new[] { "0", "0.5", "1" }, Names = new[] { "None", "Soft", "Dark" }, Default = "0.5" },
            new Option { Group = "Games", Key = "gamesget", Label = "Download game media", About = "Download missing media for your library.", Action = "Open", Note = MediaDownloads.SettingStatus, Progress = () => MediaDownloads.Unified ? MediaDownloads.Progress() : Background.ArtProgress() },
            new Option { Group = "Advanced", Key = "downloadprogress", Label = "Combined media downloads", About = "Use one download control in Games. Turn off for individual controls." },
            new Option { Group = "Look", Key = "previewartfit", Label = "Keep trailer-card artwork proportional", About = "Prevent stretched artwork in trailer cards. Restart Playnite to apply." },
            new Option { Group = "Games", Key = "gamesask", Label = "Offer to get art for a new library", About = "Offer media downloads when setting up a new library." },
            new Option { Group = "Games", Key = "loadingscreen", Label = "Loading screen", About = "Show the game's artwork and logo while it starts." },
            new Option { Group = "Games", Key = "aftergame", Label = "After a game", About = "Choose where you return to after you close a game.", Choices = new[] { "spotlight", "where" }, Names = new[] { "Spotlight, on that game", "Where you were" }, Default = "spotlight" },
            new Option { Group = "Games", Key = "freeze", Label = "Pause games on Guide", About = "Pause offline games when you press the Guide button. When this is off, games are only muted." },
            new Option { Group = "Games", Key = "powerconfirm", Label = "Ask before power actions", About = "Ask before Sleep, Restart and Shut down." },
            new Option { Group = "Games", Key = "tray", Label = "Close to tray", About = "Keep Playnite running in the background when you exit, so it opens instantly. Alt+F4 still quits." },
            new Option { Group = "Games", Key = "storepage", Label = "Store page", About = "Open free games, sales and new releases from the Store card." },
            new Option { Group = "Games", Key = "storetrailers", Label = "Store trailers", About = "Play a game's trailer on its Store page." },
            new Option { Group = "Games", Key = "storetab", Label = "Store opens on", About = "Choose the tab the Store opens on.", Choices = new[] { "All", "Steam", "Epic Games", "GOG", "Xbox", "More stores" }, Default = "All" },
            new Option { Group = "Games", Key = "storeregion", Label = "Store currency", About = "Choose the currency for store prices.", Action = "Change" },
            new Option { Group = "Games", Key = "surprisepool", Label = "Surprise me games", About = "Choose which games Surprise me picks from.", Choices = new[] { "installed", "fresh", "all" }, Names = new[] { "Installed games", "Games not played yet", "All games" }, Default = "installed" },
            new Option { Group = "Games", Key = "surprisenorepeat", Label = "No repeats in Surprise me", About = "Show every game once before any comes up again." },
            new Option { Group = "Games", Key = "quietbackups", Label = "Automatic save backups", About = "Back up your saves with Ludusavi after you play, without asking." },
            // ---- Advanced ----
            new Option { Group = "Advanced", Key = "report", Label = "Report a problem", About = "Tell us about a problem in a few steps. No account needed.", Action = "Open" },
            new Option { Group = "Advanced", Key = "perflog", Available = () => System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-perflog")) || Features.On("perflog"), Label = "Record performance", About = "Record performance locally for up to 20 minutes.", DefaultOn = false, Note = PerformanceRecorder.Note },
            new Option { Group = "Games", Key = "menuclosegame", Label = "Close game in the menu", About = "Show Close for the running game at the top of the menu, hidden games too." },
            new Option { Group = "Advanced", Key = "xboxsignin", Label = "Xbox sign-in", About = "Offer to sign Playnite in to Xbox so Game Pass games show up." },
            new Option { Group = "Advanced", Key = "gamesfolder", Label = "Find games in Games folders", About = "With no game folder set, look in a Games folder on each drive for games no store knows about." },
            new Option { Group = "Advanced", Key = "storelibraries", Label = "Offer store libraries", About = "Offer Playnite's library add-ons for the stores on this PC in the setup check." },
            new Option { Group = "Advanced", Key = "setupcheck", Label = "Setup check", About = "Check Playnite's settings and add-ons, and fix what Selene can.", Action = "Open" },
            new Option { Group = "Advanced", Key = "shortaddonask", Label = "Short add-on question", About = "Use a shorter add-on installation prompt." },
            new Option { Group = "Advanced", Key = "reportchord", Label = "Report with both sticks", About = "Press both sticks at the same time to report a problem with a screenshot." },
            new Option { Group = "Advanced", Key = "legacytasks", Label = "Old administrator tasks", About = "Remove the administrator tasks that older versions of Selene left on this PC.", Action = "Remove", Available = () => LegacyTasks.Any, Note = LegacyTasks.Summary },
            new Option { Group = "Advanced", Key = "fullrate", Label = "Match the screen's refresh rate", About = "Move at 120 or 144 Hz on high refresh rate screens." },
            new Option { Group = "Advanced", Key = "calmmotion", Label = "Calm background motion", About = "Reduce background animation to 30 FPS. Restart Playnite to apply." },
            new Option { Group = "Advanced", Key = "presence", Label = "Pause when out of view", About = "Pause background motion and trailers while Selene is out of view." },
            new Option { Group = "Look", Key = "perfmode", Label = "Performance mode", About = "For slower PCs: no moving background or trailer previews. Motion and art stay. Applies after restarting Playnite.", DefaultOn = false },
            new Option { Group = "Advanced", Key = "asynccovers", Label = "Load covers in the background", About = "Covers load without holding up scrolling and fade in. Applies after restarting Playnite." },
            new Option { Group = "Advanced", Key = "lightweight", Label = "Reduce memory use", About = "Cache fewer images and preload fewer games.", DefaultOn = false },
            new Option { Group = "Advanced", Key = "viewswitch", Label = "View button", About = "Press to switch between Spotlight and Collection. Hold to search." },
            new Option { Group = "Advanced", Key = "playnitesettings", Label = "Selene Playnite settings", About = "Show Playnite's settings in Selene's style." },
            new Option { Group = "Advanced", Key = "translatecollect", Label = "Collect words to translate", About = "Save every phrase Selene shows to a file on your Desktop, ready to translate.", DefaultOn = false },
            new Option { Group = "Advanced", Key = "translate", Label = "Selene in Playnite's language", About = "Use Playnite's language for Selene when a translation is available." },
            new Option { Group = "Advanced", Key = "reset", Label = "Reset Selene settings", About = "Restore all Selene settings to their defaults. Your profiles are kept.", Action = "Reset" },
        };

        private static SeleneSettings settings;
        private static Action save;

        public static void Init(SeleneSettings s, Action saveAll)
        {
            settings = s; save = saveAll;
            if (s != null && DownloadConsent.Initialize(s) && save != null) save();
            // the Start on Collection switch (up to 1.0.0) is the Start on choice now
            if (s != null && s.TurnedOn != null && s.TurnedOn.Remove("startcollection"))
            {
                if (s.Values == null) s.Values = new Dictionary<string, string>();
                if (!s.Values.ContainsKey("starton")) s.Values["starton"] = "collection";
                if (save != null) save();
            }
            // Trailer sound was a choice (on / off, up to 1.0.0): a switch now
            string ts;
            if (s != null && s.Values != null && s.Values.TryGetValue("trailersound", out ts))
            {
                s.Values.Remove("trailersound");
                if (ts == "on") Mark(s, "trailersound", true);
                if (save != null) save();
            }
        }

        private static bool DefaultOn(string key) { var o = All.FirstOrDefault(f => f.Key == key); return o == null || o.DefaultOn; }

        public static bool On(string key)
        {
            var external = Find(key);
            if (external != null && external.Get != null) { try { return external.Get(); } catch { return false; } }
            if (DefaultOn(key)) return settings == null || settings.TurnedOff == null || !settings.TurnedOff.Contains(key);
            return settings != null && settings.TurnedOn != null && settings.TurnedOn.Contains(key);
        }

        // records a switch in the given settings (the add-on page edits a copy until it's saved)
        public static void Mark(SeleneSettings s, string key, bool on)
        {
            if (s.TurnedOff == null) s.TurnedOff = new List<string>();
            if (s.TurnedOn == null) s.TurnedOn = new List<string>();
            s.TurnedOff.Remove(key);
            s.TurnedOn.Remove(key);
            if (on && !DefaultOn(key)) s.TurnedOn.Add(key);
            if (!on && DefaultOn(key)) s.TurnedOff.Add(key);
        }

        public static void Set(string key, bool on)
        {
            var external = Find(key);
            if (external != null && external.Put != null) { external.Put(on); return; }
            if (settings == null) return;
            Mark(settings, key, on);
            if (save != null) save();
        }

        // ---- choices ----
        public static Option Find(string key) { return All.FirstOrDefault(f => f.Key == key); }

        // every switch and choice back to its default (the Reset row)
        public static void ResetAll()
        {
            if (settings == null) return;
            if (settings.TurnedOn != null) settings.TurnedOn.Clear();
            if (settings.TurnedOff != null) settings.TurnedOff.Clear();
            if (settings.Values != null) settings.Values.Clear();
            if (save != null) save();
        }

        // the chosen value (its default until it's changed; a value no longer offered falls back too)
        public static string Value(string key)
        {
            var o = Find(key);
            if (o == null || !o.IsChoice) return "";
            string v;
            if (settings != null && settings.Values != null && settings.Values.TryGetValue(key, out v) && o.Choices.Contains(v)) return v;
            return o.Default ?? o.Choices[0];
        }

        // the chosen value as a number (0 when it isn't one)
        public static double Number(string key)
        {
            double d;
            return double.TryParse(Value(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        // how the chosen value reads
        public static string ValueName(string key)
        {
            var o = Find(key);
            if (o == null || !o.IsChoice) return "";
            int i = Array.IndexOf(o.Choices, Value(key));
            return o.Names != null && i >= 0 && i < o.Names.Length ? o.Names[i] : Value(key);
        }

        // a small fact kept with the settings (when a notice was last shown); not one of the choices
        public static string Remembered(string name)
        {
            string v;
            return settings != null && settings.Values != null && settings.Values.TryGetValue(name, out v) ? v : null;
        }

        public static void Remember(string name, string value) { SetValue(name, value); }

        // a captures folder added, or taken out when it's in the list already (Selene settings > Spotlight > Captures folders)
        public static bool ToggleCapturesFolder(string folder)
        {
            if (settings == null || string.IsNullOrWhiteSpace(folder)) return false;
            if (settings.CapturesFolders == null) settings.CapturesFolders = new List<string>();
            var have = settings.CapturesFolders.FirstOrDefault(f => string.Equals(f.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            bool added = have == null;
            if (added) settings.CapturesFolders.Add(folder); else settings.CapturesFolders.Remove(have);
            if (save != null) save();
            return added;
        }

        public static void SetValue(string key, string value)
        {
            if (settings == null) return;
            if (settings.Values == null) settings.Values = new Dictionary<string, string>();
            settings.Values[key] = value;
            if (save != null) save();
        }

        // the next (or previous) value round
        public static void Cycle(string key, int dir)
        {
            var o = Find(key);
            if (o == null || !o.IsChoice) return;
            int i = Array.IndexOf(o.Choices, Value(key));
            SetValue(key, o.Choices[((i < 0 ? 0 : i) + dir + o.Choices.Length) % o.Choices.Length]);
        }
    }
}
