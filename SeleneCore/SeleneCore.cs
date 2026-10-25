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

        private bool Active
        {
            get { return PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen && SelenePaths.ActiveThemeId == ThemeId; }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            if (!Active) { logger.Info("Selene Core: the Selene theme isn't in use (fullscreen theme: " + SelenePaths.ActiveThemeId + "), staying off"); return; }
            HomeNav.Log("paths: config " + SelenePaths.PlayniteConfig + " | theme " + SelenePaths.ThemeDir + (PlayniteApi.Paths.IsPortable ? " | portable" : ""));
            HomeNav.Api = PlayniteApi;
            HomeNav.Install();
            if (Features.On("translate")) Lang.Load(PlayniteApi);
            try { if (Features.On("quietbackups")) LudusaviApp.Quiet(PlayniteApi); }
            catch (Exception ex) { logger.Warn("Selene Core: couldn't quieten Ludusavi: " + ex.Message); }
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
            if (string.IsNullOrWhiteSpace(settings.Settings.AvatarPath)) AnimatedAvatar.Fetch(SelenePaths.SteamId);
            HomeNav.Log("Selene Core started");
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            if (Active && args.Game != null) HomeNav.GameStarting(args.Game.Id);
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            // a game that runs as administrator gets a no-prompt launcher after it closes (automatic, any game)
            if (Active && args.Game != null) LocalGames.WatchForAdmin(args.Game);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (!Active || args.Game == null) return;
            LocalGames.AfterGameStopped(args.Game);
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
            Settings.WallpapersFolder = editing.WallpapersFolder; Settings.CapturesFolders = editing.CapturesFolders; Settings.Values = editing.Values; Settings.TurnedOff = editing.TurnedOff; Settings.TurnedOn = editing.TurnedOn;
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
                if (f.IsChoice) continue;
                var key = f.Key;
                var box = new CheckBox { Content = f.Label + "  -  " + f.About, IsChecked = Features.On(key), Margin = new Thickness(0, 6, 0, 0) };
                box.Checked += (o, e) => Features.Mark(s, key, true);
                box.Unchecked += (o, e) => Features.Mark(s, key, false);
                panel.Children.Add(box);
            }
            foreach (var f in Features.All)
            {
                if (!f.IsChoice) continue;
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
            field("Login name. Empty = your Steam name:", s.DisplayName, v => s.DisplayName = v);
            field("Login picture (image file). Empty = your Steam avatar:", s.AvatarPath, v => s.AvatarPath = v);
            field("Optional companion helper (.exe) with admin rights. Empty = off:", s.HelperPath, v => s.HelperPath = v);
            field("Optional admin helper scheduled task (focus/close games that run as administrator). Empty = off:", s.HelperTask, v => s.HelperTask = v);
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
        }

        // the groups Selene settings shows, in order (each option names its group)
        public static readonly string[] Groups = { "Start-up", "Spotlight", "Collection", "Notifications", "Sound & music", "Look", "Games", "Advanced" };

        public static readonly Option[] All =
        {
            // ---- Start-up ----
            new Option { Group = "Start-up", Key = "intro", Label = "Logo intro", About = "The Selene logo as Playnite starts." },
            new Option { Group = "Start-up", Key = "startsound", Label = "Start-up sound", About = "The chime with the logo." },
            new Option { Group = "Start-up", Key = "login", Label = "Who's playing?", About = "Pick a profile at start. Off: the last one signs in by itself." },
            new Option { Group = "Start-up", Key = "starton", Label = "Start on", About = "The screen Playnite opens on.", Choices = new[] { "spotlight", "collection", "last" }, Names = new[] { "Spotlight", "Collection", "Where you left it" }, Default = "spotlight" },
            new Option { Group = "Start-up", Key = "starttab", Label = "Starting tab", About = "The filter tab Spotlight opens on.", Choices = new[] { "last", "all", "installed", "recent", "favorites", "unplayed" }, Names = new[] { "Where you left it", "All games", "Installed", "Recently played", "Favorites", "Not played yet" }, Default = "last" },
            new Option { Group = "Start-up", Key = "startrefresh", Label = "Library update at start", About = "Looks for new and removed games shortly after start and when back from the tray." },
            // ---- Spotlight ----
            new Option { Group = "Spotlight", Key = "cardactivity", Label = "Activity card", About = "What you've been playing, with the numbers." },
            new Option { Group = "Spotlight", Key = "cardcaptures", Label = "Captures card", About = "Your screenshots and clips." },
            new Option { Group = "Spotlight", Key = "cardsurprise", Label = "Surprise me card", About = "Covers flick past and stop on a game to play." },
            new Option { Group = "Spotlight", Key = "cardpictures", Label = "Pictures under the cards", About = "Latest captures, last played and a few ideas. Down goes into them." },
            new Option { Group = "Spotlight", Key = "trailerdelay", Label = "Trailer cards after", About = "How long you rest on a game before its tile plays its trailer.", Choices = new[] { "1", "2.5", "5", "0" }, Names = new[] { "1 second", "2.5 seconds", "5 seconds", "Never" }, Default = "2.5" },
            new Option { Group = "Spotlight", Key = "trailersound", Label = "Trailer sound", About = "Trailer cards start with sound. R3 switches it either way.", DefaultOn = false },
            new Option { Group = "Spotlight", Key = "filtertabs", Label = "Filter tabs", About = "The tabs LB / RB go through, here and in Collection.", Choices = new[] { "selene", "playnite", "both" }, Names = new[] { "Selene's", "Playnite's presets", "Both" }, Default = "selene" },
            new Option { Group = "Spotlight", Key = "shade", Label = "Shade over the art", About = "How much the art darkens behind the words.", Choices = new[] { "0.7", "1", "1.3" }, Names = new[] { "Lighter", "As needed", "Deeper" }, Default = "1" },
            new Option { Group = "Spotlight", Key = "idle", Label = "Idle screen after", About = "Left alone, Spotlight dims and drifts through your games' art. Any button wakes it.", Choices = new[] { "0", "5", "10", "20" }, Names = new[] { "Never", "5 minutes", "10 minutes", "20 minutes" }, Default = "10" },
            // ---- Collection ----
            new Option { Group = "Collection", Key = "librarytrailer", Label = "Collection trailers", About = "Rest on a game and its trailer plays. R3 for its sound." },
            new Option { Group = "Collection", Key = "cardcollection", Label = "Collection card on Spotlight", About = "A card that opens Collection (the View button does too).", DefaultOn = false },
            // ---- Notifications ----
            new Option { Group = "Notifications", Key = "notifysound", Label = "Notification sound", About = "A soft sound as a card comes in." },
            new Option { Group = "Notifications", Key = "sessioncard", Label = "Session card", About = "After a game: how long you played, this time and in total." },
            new Option { Group = "Notifications", Key = "progresstoast", Label = "Downloads as a card", About = "Add-ons fetching logos or trailers show a small card instead of covering the screen." },
            new Option { Group = "Notifications", Key = "updatecheck", Label = "New version notice", About = "Once a day Selene checks for a newer version and says so. Nothing installs by itself." },
            new Option { Group = "Notifications", Key = "setuphint", Label = "Add-on hint", About = "A note when logos and trailers need the Extra Metadata add-on." },
            // ---- Sound & music ----
            new Option { Group = "Sound & music", Key = "sounds", Label = "Interface sounds", About = "Sounds when you move, select and go back." },
            new Option { Group = "Sound & music", Key = "soundvolume", Slider = true, Label = "Sound volume", About = "How loud Selene's own sounds are.", Choices = new[] { "25", "50", "75", "100" }, Names = new[] { "25%", "50%", "75%", "100%" }, Default = "100" },
            new Option { Group = "Sound & music", Key = "clipvolume", Slider = true, Label = "Clip volume", About = "Clips in Captures. Up / Down change it there too.", Choices = new[] { "0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100" }, Names = new[] { "Off", "10%", "20%", "30%", "40%", "50%", "60%", "70%", "80%", "90%", "100%" }, Default = "90" },
            new Option { Group = "Sound & music", Key = "music", Label = "Background music", About = "The default track while browsing, a game's song with its trailer." },
            new Option { Group = "Sound & music", Key = "launchquiet", Label = "Quiet loading screen", About = "A starting game stays quiet until it shows." },
            // ---- Look ----
            new Option { Group = "Look", Key = "homebackground", Label = "Spotlight background", About = "Behind the picked game: its own art, or always your wallpaper.", Choices = new[] { "art", "wallpaper" }, Names = new[] { "Game art", "Wallpaper only" }, Default = "art" },
            new Option { Group = "Look", Key = "glow", Label = "Animated background", About = "The moving backdrop behind the login and cards." },
            new Option { Group = "Look", Key = "topprofile", Label = "Profile in the top bar", About = "Your avatar beside the clock." },
            new Option { Group = "Look", Key = "textshadow", Label = "Readable text", About = "A soft halo behind text over bright pictures." },
            new Option { Group = "Look", Key = "dialogs", Label = "Selene dialogs", About = "Playnite's questions and errors as Selene cards." },
            new Option { Group = "Look", Key = "hints", Label = "Button hints", About = "What each button does, along the bottom." },
            new Option { Group = "Look", Key = "prompts", Label = "Button names", About = "Which controller's buttons the hints name.", Choices = new[] { "playnite", "xbox", "playstation" }, Names = new[] { "As Playnite's setting", "Xbox", "PlayStation" }, Default = "playnite" },
            // ---- Games ----
            new Option { Group = "Games", Key = "loadingscreen", Label = "Loading screen", About = "A game's art and logo while it starts. Off: Playnite's plain start." },
            new Option { Group = "Games", Key = "aftergame", Label = "After a game", About = "Where Selene comes back to.", Choices = new[] { "spotlight", "where" }, Names = new[] { "Spotlight, on that game", "Where you were" }, Default = "spotlight" },
            new Option { Group = "Games", Key = "freeze", Label = "Freeze on Guide", About = "Guide pauses offline games. Off: they're only muted." },
            new Option { Group = "Games", Key = "powerconfirm", Label = "Ask before power actions", About = "A question before Sleep, Restart and Shut down." },
            new Option { Group = "Games", Key = "tray", Label = "Close to tray", About = "Exit hides Playnite so it comes back instantly. Alt+F4 still quits." },
            new Option { Group = "Games", Key = "storepage", Label = "Store page", About = "The Store card opens free games, sales and new releases. Off: it picks a store app." },
            new Option { Group = "Games", Key = "storetrailers", Label = "Store trailers", About = "A game's trailer plays behind its Store page." },
            new Option { Group = "Games", Key = "storetab", Label = "Store opens on", About = "The tab the Store page starts on.", Choices = new[] { "All", "Steam", "Epic Games", "GOG", "Xbox", "More stores" }, Default = "All" },
            new Option { Group = "Games", Key = "storeregion", Label = "Store currency", About = "The currency the stores show their prices in.", Action = "Change" },
            new Option { Group = "Games", Key = "surprisepool", Label = "Surprise me picks from", About = "Where Surprise me draws from. LB / RB change it there too.", Choices = new[] { "installed", "fresh", "all" }, Names = new[] { "Installed games", "Games not played yet", "All games" }, Default = "installed" },
            new Option { Group = "Games", Key = "surprisenorepeat", Label = "Surprise me without repeats", About = "Every game comes up once before any comes round again." },
            new Option { Group = "Games", Key = "admin", Label = "No-prompt launchers", About = "Admin games skip the Windows prompt after their first run." },
            new Option { Group = "Games", Key = "quietbackups", Label = "Quiet save backups", About = "Ludusavi backs up saves after playing without asking." },
            // ---- Advanced ----
            new Option { Group = "Advanced", Key = "fullrate", Label = "Match the screen's refresh rate", About = "Selene moves at 120 / 144 Hz on such screens instead of 60. Only while Playnite is in front." },
            new Option { Group = "Advanced", Key = "viewswitch", Label = "View button", About = "Switches Spotlight and Collection. Hold it to search." },
            new Option { Group = "Advanced", Key = "playnitesettings", Label = "Playnite settings, Selene's way", About = "The menu's Playnite settings as a Selene screen. Off: Playnite's own pages." },
            new Option { Group = "Advanced", Key = "translate", Label = "Selene in Playnite's language", About = "Selene's words follow Playnite's language when a translation exists." },
            new Option { Group = "Advanced", Key = "reset", Label = "Reset Selene settings", About = "Every switch and choice back to how Selene ships. Profiles stay.", Action = "Reset" },
        };

        private static SeleneSettings settings;
        private static Action save;

        public static void Init(SeleneSettings s, Action saveAll)
        {
            settings = s; save = saveAll;
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
