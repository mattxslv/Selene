using System;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace Selene
{
    // "Set up my games": the one question that makes a new library look right. Games added in Playnite's Desktop mode (or
    // imported from Steam) have only what Playnite itself downloaded. Selene's art, screenshots and trailers are optional
    // downloads that ask first (DownloadConsent); this asks once for all of them together, saying where they come from and where
    // they are saved, instead of making a new user find five separate switches. Yes is the consent for those categories; Not now
    // leaves everything off, and each one stays in Selene settings. Songs are not part of it (they need about 130 MB of tools).
    // Asked once, only when there are games, none of these downloads is allowed yet and no Selene art is on disk. Switch: Games >
    // Offer to get art for a new library.
    public static class GamesSetup
    {
        private static readonly string[] Categories = { "artdownload", "screenshotsdownload", "trailerdownload", "metadatacomplete" };
        public static bool Allowed { get { return Categories.All(DownloadConsent.Allowed); } }

        public static void AskLater() { StartupWork.AfterHome("games setup question", () => Ask(0)); }

        private static bool Needed()
        {
            if (!Features.On("gamesask") || !string.IsNullOrEmpty(Features.Remembered("gamesask-done"))) return false;
            var api = HomeNav.Api;
            if (api == null || !api.Database.Games.Any(g => !g.Hidden)) return false;
            if (Categories.Any(DownloadConsent.Allowed)) return false;
            try { if (Directory.Exists(SelenePaths.Media("Hero")) && Directory.EnumerateFiles(SelenePaths.Media("Hero")).Any()) return false; } catch { }
            return true;
        }

        // the home is up, but a profile screen, a menu or a game's loading screen may still be in front: try again a few times
        private static void Ask(int attempt)
        {
            try
            {
                if (!Needed()) return;
                if (HomeNav.Launching || HomeNav.GameRunning() || LoginScene.IsActive || !ScreenPresence.InView)
                {
                    if (attempt >= 8) return;
                    var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                    again.Tick += (s, e) => { again.Stop(); Ask(attempt + 1); };
                    again.Start();
                    return;
                }
                HomeNav.Log("games setup: asking whether to get art for the library");
                LoginScene.OpenWith(() => new GamesAskScreen());
            }
            catch (Exception ex) { HomeNav.Log("games setup: " + ex.Message); }
        }

        // short on purpose: what, from where, and one place it is all kept (every save folder is under Playnite's own folder)
        public static string Body()
        {
            var config = HomeNav.Api != null ? HomeNav.Api.Paths.ConfigurationPath : SelenePaths.PlayniteConfig;
            return "Covers, backgrounds, logos, screenshots and trailers for the games you look at, from Steam and your Playnite add-ons.\n\nSaved in your Playnite folder:\n" + config + MediaElsewhere(config);
        }
        // a media folder chosen in the add-on's settings, outside Playnite's folder, is named too
        private static string MediaElsewhere(string config)
        {
            try { var media = Path.GetDirectoryName(SelenePaths.Media("Hero")); return media != null && !media.StartsWith(config, StringComparison.OrdinalIgnoreCase) ? "\n" + media : ""; }
            catch { return ""; }
        }

        // "not now" is remembered as "later": the settings row and a red dot stay until the art is allowed
        public static bool Pending { get { return Features.Remembered("gamesask-done") == "later" && !Categories.Any(DownloadConsent.Allowed); } }

        // the question itself; closed runs after either answer (the first-run screen leaves, the settings row refreshes)
        public static Dialog Question(Action closed, Action accepted = null, Action declined = null)
        {
            if (MediaDownloads.Unified)
                return new Dialog("\uE896", "Download game media?", MediaDownloads.Details(), "Download", "Not now",
                    () => { MediaDownloads.Accept(); closed(); if (accepted != null) accepted(); },
                    () => { Decline(); closed(); if (declined != null) declined(); }, false, false, 0, 900).StartOnYes();
            return new Dialog("\uE896", "Get art for your games?", Body(), "Get it", "Not now",
                () => { Accept(); Ui.Chime(); closed(); if (accepted != null) accepted(); },
                () => { Decline(); closed(); if (declined != null) declined(); }, false).StartOnYes();
        }

        // yes: the consent for each category, with its switch on, then the work starts
        public static void Accept() { Allow(); Start(); }

        internal static void Allow()
        {
            Features.Remember("gamesask-done", "yes");
            foreach (var key in Categories)
            {
                if (key == "trailerdownload") Features.SetValue(key, "browse"); else Features.Set(key, true);
                DownloadConsent.Grant(key);
            }

            HomeNav.Log("games setup: art, screenshots, trailers and missing details allowed");
        }

        private static void Start()
        {
            foreach (var key in Categories) DownloadConsent.Changed(key);
        }

        public static void Decline()
        {
            Features.Remember("gamesask-done", "later");
            HomeNav.Log("games setup: not now");
        }
    }

    public class GamesAskScreen : Screen
    {
        private Dialog ask;
        private bool accepted;
        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.94); } }
        public override bool MenuOnStart { get { return false; } }

        public GamesAskScreen()
        {
            ask = GamesSetup.Question(() => { if (!MediaDownloads.Unified) LoginScene.Finish(); },
                () => { if (MediaDownloads.Unified) Next(); },
                () => { if (MediaDownloads.Unified) LoginScene.Finish(); });
        }

        private void Next() { accepted = true; LoginScene.Show(new SettingsScreen(false, "Games", "gamesget"), 1); }

        public override void Shown() { ask.Open(Root); }
        public override void Handle(string button) { if (!accepted) ask.Handle(button); }
    }

    // first-run setup, after the setup check: a SteamGridDB key for square tile art (Steam has no square pictures). Pasted
    // from the clipboard, checked before it's kept; Skip goes on without it (the tall covers, cropped). Not shown when a key
    // is there already (the SteamGridDB add-on's counts) or square art is switched off.
    public class SquareKeyScreen : Screen
    {
        private Dialog ask;
        private readonly Action next;
        private bool done;
        public static bool Wanted { get { return Features.On("squareart") && SquareFetch.Key == null; } }
        public static bool Skipped { get { return Features.Remembered("squarekey-skipped") == "yes" && Wanted; } }

        public SquareKeyScreen(Action next) { this.next = next; }

        private void Open()
        {
            ask = new Dialog("\uE8D7", "Square tile art", "Add a free SteamGridDB key for square tiles: steamgriddb.com > Preferences > API.\nCopy it, then choose Paste. You can add it later in Look.",
                "Paste", "Skip", () => SquareFetch.PasteKey(ok => { if (ok) Next(); else Open(); }), () => { Features.Remember("squarekey-skipped", "yes"); Next(); }, false, false, 0, 900).StartOnYes();
            ask.Open(Root);
        }

        private void Next() { if (done) return; done = true; next(); }

        public override void Shown() { if (ask == null) Open(); }
        public override void Handle(string button) { if (!done && ask != null) ask.Handle(button); }
    }
}
