using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Selene;

public class FakeUps { public string DefaultMusicPath { get; set; } public int DefaultMusicSourceOption { get; set; } public string SelectedBundledPreset { get; set; } public FakeUps() { DefaultMusicPath = ""; DefaultMusicSourceOption = 2; SelectedBundledPreset = "tunetank-dark-ambient-soundscape-music.mp3"; } }
class BugsCheck
{
    public class FocusSoundProbe { public static IntPtr NavigateSound { get; set; } }
    public class Named { public string Name { get; set; } }
    public class GameEntry { public Named Source { get; set; } public ArrayList Platforms { get; set; } }
    public class Wrapped { public GameEntry Game { get; set; } }
    static int checks;
    static void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args); }
    static void Settle(int ms) { var until = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < until) { Pump(); System.Threading.Thread.Sleep(15); } }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => {})); }
    [STAThread] static int Main(string[] args)
    {
        var scratch = Path.Combine(args[0], "runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("TEMP", scratch); Environment.SetEnvironmentVariable("TMP", scratch);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var key in new[] { "SeleneInkColor", "SeleneFocusColor", "GlyphColor" }) app.Resources[key] = Colors.White;
        foreach (var key in new[] { "SeleneGlassColor", "SeleneScrimColor", "SeleneOnFocusColor" }) app.Resources[key] = Colors.Black;
        Features.Init(new SeleneSettings(), () => {}); Features.Set("sounds", false);
        var window = new Window { Width = 1920, Height = 1080, Left = -32000, Top = -32000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Features.Set("actualplatform", true);
            var game = new GameEntry { Source = new Named { Name = "Steam" }, Platforms = new ArrayList { new Named { Name = "Nintendo Switch" } } };
            PlatformIcons.PublishGame(game);
            Assert(Equals(app.Resources["SelenePlatformName"], "Nintendo Switch"), "#49 platform metadata wins over store/source");
            PlatformIcons.PublishGame(new Wrapped { Game = game });
            Assert(Equals(app.Resources["SelenePlatformName"], "Nintendo Switch"), "Wrapped Playnite entries resolve their game metadata");
            game.Platforms.Add(new Named { Name = "PC (Windows)" }); game.Platforms.Add(new Named { Name = "Nintendo Switch" }); game.Platforms.Add(null);
            PlatformIcons.PublishGame(game);
            Assert(Equals(app.Resources["SelenePlatformName"], "Nintendo Switch / PC (Windows)"), "Multiple platforms are listed without duplicate or null entries");
            game.Platforms.Clear(); PlatformIcons.PublishGame(game);
            Assert(Equals(app.Resources["SelenePlatformName"], "Unknown"), "Missing platform does not invent a platform from its store");
            Features.Set("actualplatform", false); PlatformIcons.PublishGame(game);
            Assert(Equals(app.Resources["SelenePlatformName"], "Steam"), "Platform switch off preserves previous source display");
            game.Source = null; PlatformIcons.PublishGame(game);
            Assert(Equals(app.Resources["SelenePlatformName"], "Local"), "Previous local-source fallback is preserved");

            // Square cover has red edges and blue center: the original Fill mode crops the left/right red edges.
            var pixels = new byte[32 * 32 * 4];
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            {
                int i = (y * 32 + x) * 4; bool edge = x < 4 || x >= 28 || y < 4 || y >= 28;
                pixels[i] = edge ? (byte)0 : (byte)255; pixels[i + 2] = edge ? (byte)255 : (byte)0; pixels[i + 3] = 255;
            }
            var picture = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128); picture.Freeze();
            var coverFace = typeof(MenuScreen).Assembly.GetType("Selene.GameFacts").GetMethod("CoverFace", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (bool whole in new[] { true, false })
            {
                var face = (Border)coverFace.Invoke(null, new object[] { picture, whole }); face.Opacity = 1;
                face.Measure(new Size(150, 200)); face.Arrange(new Rect(0, 0, 150, 200));
                var rendered = new RenderTargetBitmap(150, 200, 96, 96, PixelFormats.Pbgra32); rendered.Render(face);
                var sample = new byte[4]; rendered.CopyPixels(new Int32Rect(6, 100, 1, 1), sample, 4, 0);
                Assert(whole ? sample[2] > 240 : sample[0] > 240, whole ? "#58 whole-cover mode preserves square cover edges" : "Crop fallback retains previous behavior");
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered));
                using (var file = File.Create(Path.Combine(args[0], whole ? "cover-fit.png" : "cover-crop.png"))) png.Save(file);
            }

            Features.Set("livevolume", true);
            var menu = new MenuScreen(); window.Content = menu.Root; window.Show(); Pump();
            Call(menu, "OpenSound");
            var refresh = (DispatcherTimer)Field(menu, "soundRefresh");
            Assert(refresh != null && refresh.IsEnabled, "#60 live volume monitoring belongs to the open Sound panel");
            Call(menu, "ApplySoundVolume", 0.3, false, false);
            Assert(((SliderBar)Field(menu, "soundSlider")).Value.Text == "30%", "External volume snapshot updates the slider");
            Assert(((TextBlock)Field(menu, "soundSummary")).Text == "30%", "Sound summary and slider show the same current value");
            Call(menu, "ApplySoundVolume", 0.3, true, false);
            Assert(((TextBlock)Field(menu, "soundSummary")).Text == "Muted", "External mute state updates the menu summary");
            Call(menu, "CloseSound"); Assert(!refresh.IsEnabled, "Closing Sound stops monitoring");
            Call(menu, "OpenSound"); Assert(((SliderBar)Field(menu, "soundSlider")).Value.Text.Length > 0, "Reopening creates a populated slider even at unchanged volume");
            menu.Hidden(); Assert(!refresh.IsEnabled, "Hiding the menu stops monitoring");
            menu.Shown(); Assert(refresh.IsEnabled, "Showing the same open menu resumes monitoring");
            window.Content = new Grid(); Pump(); Assert(!refresh.IsEnabled, "Unloading the menu stops monitoring");
            Features.Set("livevolume", false);
            var previous = new MenuScreen(); window.Content = previous.Root; Pump(); Call(previous, "OpenSound");
            Assert(Field(previous, "soundRefresh") == null, "Setting off preserves non-monitoring behavior");
            previous.Hidden();

            // #94: exercise the production size-change binding at wide and narrow viewport sizes.
            Features.Set("captureplayerfit", true);
            var player = new Grid { Width = 2560, Height = 1080 };
            var track = new Border(); player.Children.Add(track);
            typeof(CapturesScreen).GetMethod("BindPlayerTrack", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { track, player });
            window.Content = player; Pump();
            Assert(Math.Abs(track.Width - (player.ActualWidth - 342)) < 0.1, "#94 progress track follows an ultrawide player viewport");
            player.Width = 1200; Pump(); Assert(Math.Abs(track.Width - 858) < 0.1, "Capture controls reflow on resize");
            player.Width = 200; Pump(); Assert(track.Width == 0, "Very narrow player clamps track width safely");
            Features.Set("captureplayerfit", false); player.Width = 2560; Pump();
            Assert(track.Width == 1578, "Capture width switch off restores the original fixed width");

            // #95/#46: production logo used a negative top margin that let it paint above its own row.
            var xml = new System.Xml.XmlDocument(); xml.Load(args[1]);
            var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
            ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"); ns.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var logoXml = (System.Xml.XmlElement)xml.SelectSingleNode("//p:Image[@x:Name='SeleneLogo']", ns);
            logoXml.SetAttribute("xmlns:x", "http://schemas.microsoft.com/winfx/2006/xaml");
            logoXml.RemoveAttribute("Source"); logoXml.RemoveChild(logoXml.SelectSingleNode("p:Image.Triggers", ns));
            var logo = (Image)System.Windows.Markup.XamlReader.Parse(logoXml.OuterXml); logo.Source = picture;
            var logoRow = new Grid { Height = 170, Width = 620 }; logoRow.Children.Add(logo);
            window.Content = logoRow;
            var spacing = typeof(HomeNav).GetMethod("SetPreviewLogoSpacing", BindingFlags.Static | BindingFlags.NonPublic);
            Features.Set("trailerlogogap", true); spacing.Invoke(null, new object[] { true }); Settle(600);
            Assert(logo.TranslatePoint(new Point(0, 0), logoRow).Y >= -0.1, "#95 logo stays inside its own row while trailer plays");
            spacing.Invoke(null, new object[] { false }); Settle(600);   // (the logo glides there over 420 ms)
            Assert(logo.TranslatePoint(new Point(0, 0), logoRow).Y < -40, "Closing the trailer restores original logo layout");
            Features.Set("trailerlogogap", false); spacing.Invoke(null, new object[] { true }); Pump();
            Assert(logo.Margin.Top == -45, "Logo-spacing switch off preserves the previous overlap-prone layout");

            // #93: load the production single-button badge, not a duplicate template.
            xml.Load(args[2]);
            var badgeXml = (System.Xml.XmlElement)xml.SelectSingleNode("//p:Grid[@MinWidth='{DynamicResource SeleneDetailsBadgeWidth}']", ns);
            var badge = (Grid)System.Windows.Markup.XamlReader.Parse(badgeXml.OuterXml);
            app.Resources["SelenePromptB"] = "B"; app.Resources["SelenePromptA"] = "A"; app.Resources["SelenePromptY"] = "Y";
            Features.Set("detailsbadges", true); Ui.PublishPrompts(); window.Content = badge; Pump();
            Assert(badge.MinWidth == 32 && badge.Height == 32, "#93 single-button badges use equal minimum width and height");
            Features.Set("detailsbadges", false); Ui.PublishPrompts(); Pump();
            Assert(badge.MinWidth == 48, "Badge switch off restores the wider previous badge");
            // #96: the Scroll badge is round with a controller label too, and still widens for the longer keyboard and mouse labels
            var scrollXml = (System.Xml.XmlElement)xml.SelectSingleNode("//p:Grid[@MinWidth='{DynamicResource SeleneScrollBadgeWidth}']", ns);
            Assert(scrollXml != null, "#96 the production Scroll badge uses its own width resource");
            var scrollBadge = (Grid)System.Windows.Markup.XamlReader.Parse(scrollXml.OuterXml);
            app.Resources["SeleneScrollPrompt"] = "RS"; InputHints.Set(InputKind.Controller);
            Features.Set("detailsbadges", true); Ui.PublishPrompts(); window.Content = scrollBadge; Pump();
            Assert(scrollBadge.MinWidth == 32 && scrollBadge.Height == 32, "#96 controller Scroll badge is an equal-sided circle");
            InputHints.Set(InputKind.Keyboard); Ui.PublishPrompts(); Pump();
            Assert(scrollBadge.MinWidth == 48, "#96 keyboard label keeps room to widen");
            InputHints.Set(InputKind.Controller); Features.Set("detailsbadges", false); Ui.PublishPrompts(); Pump();
            Assert(scrollBadge.MinWidth == 48, "#96 badge switch off restores the previous Scroll badge");
            Features.Set("detailsbadges", true); Ui.PublishPrompts();
            // "Get art for your games?": yes is the consent for art, screenshots, selected-game trailers and missing details
            var allow = typeof(GamesSetup).GetMethod("Allow", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var key in new[] { "artdownload", "screenshotsdownload", "trailerdownload", "metadatacomplete" }) { if (key != "trailerdownload") Features.Set(key, false); DownloadConsent.Revoke(key); }
            Features.SetValue("trailerdownload", "off");
            Features.Remember("gamesask-done", "");
            allow.Invoke(null, null);
            foreach (var key in new[] { "artdownload", "screenshotsdownload", "trailerdownload", "metadatacomplete" }) Assert(DownloadConsent.Allowed(key), "Yes to the games question consents to " + key);
            Assert(Features.Value("trailerdownload") == "browse", "Trailers are for the games you select, not the whole library");
            Assert(Features.Remembered("gamesask-done") == "yes" && !GamesSetup.Pending && !Background.ForOption("gamesget"), "The question is asked once, and a yes leaves no dot");
            Assert(!DownloadConsent.Allowed("songsetup"), "The games question never covers the song tools");
            foreach (var key in new[] { "artdownload", "screenshotsdownload", "metadatacomplete" }) { Features.Set(key, false); DownloadConsent.Revoke(key); }
            Features.SetValue("trailerdownload", "off"); DownloadConsent.Revoke("trailerdownload"); Features.Remember("gamesask-done", "");
            // Not now: remembered, a red dot in the menu, the Games group and the row, and the row reopens the question
            GamesSetup.Decline();
            Assert(GamesSetup.Pending && Background.GetArt && Background.Any && Background.ForGroup("Games") && Background.ForOption("gamesget"), "Not now leaves a red dot on the settings entry, the Games group and the row");
            Features.Set("gamesget", true);
            var getArt = new SettingsScreen(false, "Games", "gamesget");
            window.Content = getArt.Root; Pump(); getArt.Shown(); Pump();
            getArt.Handle("A");
            Assert(Field(getArt, "ask") is Dialog, "The settings row asks the question again");
            getArt.Handle("B"); Pump();
            Assert(Field(getArt, "ask") == null && GamesSetup.Pending, "Not now again keeps the dot");
            allow.Invoke(null, null);
            Assert(!GamesSetup.Pending && !Background.ForOption("gamesget"), "Once the art is allowed the dot goes");
            foreach (var key in new[] { "artdownload", "screenshotsdownload", "metadatacomplete" }) { Features.Set(key, false); DownloadConsent.Revoke(key); }
            Features.SetValue("trailerdownload", "off"); DownloadConsent.Revoke("trailerdownload"); Features.Remember("gamesask-done", "");
            Assert(GamesSetup.Body().Contains("Steam") && GamesSetup.Body().Contains("Saved in your Playnite folder"), "The question says where things come from and where they are saved");

            // Selene's own background default music in UniPlaySong's default slot
            var useDefault = typeof(SongSetup).GetMethod("UseSeleneDefault", BindingFlags.Static | BindingFlags.NonPublic);
            var musicDir = Path.Combine(Path.GetTempPath(), "selene-default-music-test"); Directory.CreateDirectory(musicDir);
            var musicSource = Path.Combine(musicDir, "shipped.mp3"); File.WriteAllBytes(musicSource, new byte[] { 1, 2, 3, 4 });
            var musicTarget = Path.Combine(musicDir, "user", "Selene OST.mp3");
            Features.Remember("selene-default-music", "");
            var emptyUps = new FakeUps();   // a new install: its own bundled tracks (source 2), nothing chosen
            Assert((bool)useDefault.Invoke(null, new object[] { emptyUps, musicSource, musicTarget }) && emptyUps.DefaultMusicPath == musicTarget && emptyUps.DefaultMusicSourceOption == 0 && File.Exists(musicTarget), "New install: Selene OST is set AND the source is switched to a file (a path alone is ignored on source 2)");
            Assert(!(bool)useDefault.Invoke(null, new object[] { emptyUps, musicSource, musicTarget }), "Already Selene OST: nothing to save again");
            var ownUps = new FakeUps { DefaultMusicPath = @"D:\my music\mine.mp3", DefaultMusicSourceOption = 0 };
            Assert(!(bool)useDefault.Invoke(null, new object[] { ownUps, musicSource, musicTarget }) && ownUps.DefaultMusicPath == @"D:\my music\mine.mp3", "A default file the user chose stays");
            var folderUps = new FakeUps { DefaultMusicSourceOption = 1 };
            Assert(!(bool)useDefault.Invoke(null, new object[] { folderUps, musicSource, musicTarget }) && folderUps.DefaultMusicSourceOption == 1 && folderUps.DefaultMusicPath == "", "A music folder the user chose stays");
            var presetUps = new FakeUps { SelectedBundledPreset = "PS2 Menu - Ambience.mp3" };
            Assert(!(bool)useDefault.Invoke(null, new object[] { presetUps, musicSource, musicTarget }) && presetUps.DefaultMusicSourceOption == 2, "A bundled track the user picked stays");
            var clearedUps = new FakeUps { DefaultMusicSourceOption = 0 };
            Assert(!(bool)useDefault.Invoke(null, new object[] { clearedUps, musicSource, musicTarget }) && clearedUps.DefaultMusicPath == "", "A default the user cleared afterwards is not put back");
            var backUps = new FakeUps { DefaultMusicPath = musicTarget, DefaultMusicSourceOption = 2 };
            Assert(!(bool)useDefault.Invoke(null, new object[] { backUps, musicSource, musicTarget }) && backUps.DefaultMusicSourceOption == 2, "Switching back to the bundled tracks afterwards is respected");
            Features.Remember("selene-default-music", "1");   // set by an earlier build that left the source on 2: repaired once
            var staleUps = new FakeUps { DefaultMusicPath = musicTarget, DefaultMusicSourceOption = 2 };
            Assert((bool)useDefault.Invoke(null, new object[] { staleUps, musicSource, musicTarget }) && staleUps.DefaultMusicSourceOption == 0 && Features.Remembered("selene-default-music") == "2", "An earlier build's path that UniPlaySong ignored is repaired");
            Features.Remember("selene-default-music", "");
            var oldNameUps = new FakeUps { DefaultMusicPath = Path.Combine(musicDir, "user", "Selene background default.mp3"), DefaultMusicSourceOption = 0 }; File.WriteAllBytes(oldNameUps.DefaultMusicPath, new byte[] { 9 });
            Assert((bool)useDefault.Invoke(null, new object[] { oldNameUps, musicSource, musicTarget }) && oldNameUps.DefaultMusicPath == musicTarget && !File.Exists(Path.Combine(musicDir, "user", "Selene background default.mp3")), "The old file name moves over to Selene OST");
            Features.Remember("selene-default-music", "");
            var presetVisible = typeof(HomeNav).GetMethod("PresetVisible", BindingFlags.Static | BindingFlags.NonPublic);
            var preset = new Playnite.SDK.Models.FilterPreset { Name = "Desktop-only", ShowInFullscreeQuickSelection = false };
            Features.Set("fullscreenpresets", true);
            Assert(!(bool)presetVisible.Invoke(null, new object[] { preset }), "#71 desktop-only preset is excluded from fullscreen tabs");
            preset.ShowInFullscreeQuickSelection = true;
            Assert((bool)presetVisible.Invoke(null, new object[] { preset }), "Fullscreen quick-selection preset remains visible");
            preset.ShowInFullscreeQuickSelection = false; Features.Set("fullscreenpresets", false);
            Assert((bool)presetVisible.Invoke(null, new object[] { preset }), "Preset setting off restores all-presets behavior");
            // #39: use the production choice with isolated profile/shared persistence callbacks.
            var favoriteChoice = typeof(HomeNav).Assembly.GetType("Selene.FavoriteChoice");
            var readFavorite = favoriteChoice.GetMethod("Read");
            var toggleFavorite = favoriteChoice.GetMethod("Toggle");
            var favoriteGame = new Playnite.SDK.Models.Game { Name = "Favorite regression" };
            var owner = new Profile(); var other = new Profile(); int sharedSaves = 0, profileSaves = 0;
            Func<bool> profileToggle = () => {
                profileSaves++; if (owner.Favorites.Contains(favoriteGame.Id)) owner.Favorites.Remove(favoriteGame.Id);
                else owner.Favorites.Add(favoriteGame.Id); return owner.Favorites.Contains(favoriteGame.Id);
            };
            Action sharedSave = () => { sharedSaves++; };
            Features.Set("consistentfavorites", true);
            Assert(!(bool)readFavorite.Invoke(null, new object[] { favoriteGame, owner }), "#39 profile favorite starts off");
            Assert((bool)toggleFavorite.Invoke(null, new object[] { favoriteGame, owner, profileToggle, sharedSave }), "Game options adds to the active profile");
            Assert((bool)readFavorite.Invoke(null, new object[] { favoriteGame, owner }), "Options reads the same profile favorite it writes");
            Assert(!favoriteGame.Favorite && sharedSaves == 0 && profileSaves == 1, "Profile favorite leaves shared metadata unchanged");
            Assert(!(bool)readFavorite.Invoke(null, new object[] { favoriteGame, other }), "Another profile stays independent");
            Assert(!(bool)toggleFavorite.Invoke(null, new object[] { favoriteGame, owner, profileToggle, sharedSave }), "Options can remove a profile favorite");
            toggleFavorite.Invoke(null, new object[] { favoriteGame, null, profileToggle, sharedSave });
            Assert(favoriteGame.Favorite && sharedSaves == 1 && profileSaves == 2, "Signed-out options saves shared Playnite favorite");
            Features.Set("consistentfavorites", false);
            Assert((bool)readFavorite.Invoke(null, new object[] { favoriteGame, owner }), "Off reads the previous shared favorite");
            toggleFavorite.Invoke(null, new object[] { favoriteGame, owner, profileToggle, sharedSave });
            Assert(!favoriteGame.Favorite && sharedSaves == 2 && profileSaves == 2, "Off writes shared metadata even when signed in");
            // #37/#47/#88: production override policy, with no add-on/audio device or downloads.
            var wantsDefault = typeof(HomeNav).GetProperty("WantDefault", BindingFlags.NonPublic | BindingFlags.Static);
            Features.Set("music", true); Features.Set("independentmusic", true);
            HomeNav.SetMusic("login"); Assert((bool)wantsDefault.GetValue(null, null), "Login retains default music");
            HomeNav.SetMusic("home"); Assert(!(bool)wantsDefault.GetValue(null, null), "#47 browsing does not force default music without a trailer");
            HomeNav.SetMusic("trailer"); Assert(!(bool)wantsDefault.GetValue(null, null), "Trailer keeps selected-game music policy");
            HomeNav.SetMusic("home"); Assert(!(bool)wantsDefault.GetValue(null, null), "Closing a trailer for game details does not restore default-only music");
            Features.Set("independentmusic", false); HomeNav.RefreshMusicPolicy();
            Assert((bool)wantsDefault.GetValue(null, null), "Off restores default-only browsing music immediately");
            HomeNav.SetMusic("trailer"); Assert(!(bool)wantsDefault.GetValue(null, null), "Off retains legacy game music with trailers");
            HomeNav.SetMusic("normal"); Assert(!(bool)wantsDefault.GetValue(null, null), "Normal mode leaves song selection to UniPlaySong");
            HomeNav.ReleaseMusic();
            var deferCover = typeof(HomeNav).Assembly.GetType("Selene.LaunchChoice").GetMethod("DeferCover");
            var launchGame = new Playnite.SDK.Models.Game("Launch choice regression");
            Features.Set("launchchoice", true);
            Assert(!(bool)deferCover.Invoke(null, new object[] { launchGame }), "No declared choices retains immediate cover");
            launchGame.GameActions = new System.Collections.ObjectModel.ObservableCollection<Playnite.SDK.Models.GameAction> {
                new Playnite.SDK.Models.GameAction { Name = "Play", IsPlayAction = true },
                new Playnite.SDK.Models.GameAction { Name = "Manual", IsPlayAction = false } };
            Assert(!(bool)deferCover.Invoke(null, new object[] { launchGame }), "Non-play actions do not cause unnecessary deferral");
            launchGame.GameActions.Add(new Playnite.SDK.Models.GameAction { Name = "Alternate", IsPlayAction = true });
            Assert((bool)deferCover.Invoke(null, new object[] { launchGame }), "#43/#80 multiple local play actions defer cover until OnGameStarting");
            launchGame.GameActions.RemoveAt(2); launchGame.PluginId = Guid.NewGuid(); launchGame.IncludeLibraryPluginAction = true;
            Assert((bool)deferCover.Invoke(null, new object[] { launchGame }), "Library action plus custom play action also needs a chooser");
            launchGame.IncludeLibraryPluginAction = false;
            Assert(!(bool)deferCover.Invoke(null, new object[] { launchGame }), "Excluded library action is not counted");
            launchGame.GameActions.Add(new Playnite.SDK.Models.GameAction { IsPlayAction = true });
            Features.Set("launchchoice", false);
            Assert(!(bool)deferCover.Invoke(null, new object[] { launchGame }), "Off restores immediate loading for multiple actions");
            // Render a circle in a wide card: the smart crop must preserve its proportions before video opens.
            var coverVisual = new DrawingVisual();
            using (var dc = coverVisual.RenderOpen()) { dc.DrawRectangle(Brushes.Blue, null, new Rect(0, 0, 100, 100)); dc.DrawEllipse(Brushes.Red, null, new Point(50, 50), 10, 10); }
            var coverBitmap = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32); coverBitmap.Render(coverVisual); coverBitmap.Freeze();
            var cropApply = typeof(CoverCrop).GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic);
            Features.Set("smartcrop", true);
            foreach (bool correct in new[] { true, false })
            {
                Features.Set("previewartfit", correct);
                var brush = new ImageBrush(coverBitmap); cropApply.Invoke(null, new object[] { brush });
                var shape = new DrawingVisual(); using (var dc = shape.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(0, 0, 320, 180));
                var rendered = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32); rendered.Render(shape);
                byte[] coverPixels = new byte[320 * 180 * 4]; rendered.CopyPixels(coverPixels, 320 * 4, 0);
                int left = 320, right = 0, top = 180, bottom = 0;
                for (int y = 0; y < 180; y++) for (int x = 0; x < 320; x++) if (coverPixels[(y * 320 + x) * 4 + 2] > 200) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                double ratio = (double)(right - left + 1) / (bottom - top + 1);
                Assert(correct ? Math.Abs(ratio - 1) < .06 : ratio > 1.6, correct ? "Expanded smart-cropped art preserves circle proportions" : "Off reproduces stretched artwork");
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered)); using (var output = File.Create(Path.Combine(args[0], correct ? "preview-art-fit.png" : "preview-art-stretch.png"))) png.Save(output);
            }
            Features.Set("previewartfit", true);
            var songCountRoot = Path.Combine(scratch, "song-count");
            var visibleSong = Guid.NewGuid(); var removedSong = Guid.NewGuid();
            foreach (var id in new[] { visibleSong, removedSong }) { var dir = Path.Combine(songCountRoot, id.ToString()); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "song.mp3"), "test"); }
            var countSongs = typeof(SongSetup).GetMethod("CountLibrarySongs", BindingFlags.Static | BindingFlags.NonPublic);
            Assert((int)countSongs.Invoke(null, new object[] { songCountRoot, new[] { visibleSong, visibleSong } }) == 1, "Song progress excludes removed/hidden games and duplicate IDs");
            Assert((int)countSongs.Invoke(null, new object[] { songCountRoot, new[] { Guid.NewGuid() } }) == 0, "Unrelated cached songs cannot make a missing-song library appear complete");
            var media = typeof(HomeNav).Assembly.GetType("Selene.MediaDownloads");
            media.GetMethod("Revoke", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null); Features.Set("downloadprogress", true);
            Assert(!(bool)media.GetProperty("Enabled").GetValue(null, null), "Combined UI alone never grants permissions");
            foreach (var key in new[] { "artdownload", "screenshotsdownload", "trailerdownload", "songsetup", "metadatacomplete" })
                Assert(!(bool)media.GetMethod("ShowSetting").Invoke(null, new object[] { key }), "Combined UI hides separate " + key);
            var combined = new SettingsScreen(false, "Games", "gamesget"); window.Content = combined.Root; Pump(); combined.Shown(); Pump();
            var combinedRows = (IList)Field(combined, "rows"); var selectedIndex = (int)Field(combined, "index");
            Assert(((UIElement)((IList)Field(combined, "tracks"))[selectedIndex]).Visibility == Visibility.Collapsed, "Action rows have no redundant right-hand label");
            var conciseConsent = (string)media.GetMethod("Details").Invoke(null, null);
            Assert(conciseConsent.Length < 150 && conciseConsent.Split('\n').Length == 2 && conciseConsent.Contains("130 MB") && conciseConsent.Contains("UniPlaySong") && conciseConsent.Contains("20 MB"), "Two-line consent retains scope and material requirements");
            ((UIElement)combinedRows[selectedIndex]).RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert(Field(combined, "ask") is Dialog && !DownloadConsent.Allowed("songsetup"), "Mouse click opens consent before granting downloads");
            Pump();
            Assert(((FrameworkElement)Field(Field(combined, "ask"), "panel")).ActualWidth == 900 && ((FrameworkElement)Field(Field(combined, "ask"), "panel")).ActualHeight < 500, "Download confirmation is wide and compact");
            combined.Handle("A"); Pump();
            Assert((bool)media.GetProperty("Enabled").GetValue(null, null) && Features.Value("trailerdownload") == "all" && DownloadConsent.Allowed("songsetup"), "Explicit Download grants all library scopes including songs");
            Assert(Field(combined, "ask") == null && typeof(HomeNav).Assembly.GetType("Selene.DownloadProgressModal") == null, "Download returns directly to settings without a progress modal");
            var totalField = typeof(TrailerFetch).GetField("libraryTotal", BindingFlags.NonPublic | BindingFlags.Static);
            var checkedField = typeof(TrailerFetch).GetField("libraryChecked", BindingFlags.NonPublic | BindingFlags.Static);
            totalField.SetValue(null, 4); checkedField.SetValue(null, 1);
            Call(combined, "Live", null, EventArgs.Empty);
            object inlineRow = null;
            foreach (var candidate in (IList)Field(combined, "live")) if (((Features.Option)candidate.GetType().GetField("Opt").GetValue(candidate)).Key == "gamesget") inlineRow = candidate;
            var sharedBar = inlineRow.GetType().GetField("Bar").GetValue(inlineRow);
            Assert(Math.Abs((double)Field(sharedBar, "share") - (double)media.GetMethod("Progress").Invoke(null, null)) < .001, "Inline bar follows the combined media progress");
            var overall = media.GetMethod("OverallProgress", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(Math.Abs((double)overall.Invoke(null, new object[] { 1.0, .4, 1.0, true }) - .8) < .001, "Songs still downloading hold the bar below finished art");
            Assert(Math.Abs((double)overall.Invoke(null, new object[] { 4 / 11.0, 0.0, 1.0, true }) - (4 / 11.0 + 1) / 3) < .001, "Finished details count while songs haven't started");
            Assert((double)overall.Invoke(null, new object[] { 1.0, 1.0, 1.0, true }) < 1, "Bar never shows 100% while any stage is busy");
            Assert((double)overall.Invoke(null, new object[] { 1.0, 1.0, 1.0, false }) == 1, "Bar reaches 100% once every stage is idle");
            Features.Remember("steamgriddb-key", ""); Features.Remember("squarekey-skipped", "yes");
            Assert(Background.ForOption("steamgridkey") && Background.ForGroup("Look") && Background.Any, "Skipped SteamGridDB key shows a red dot on its setting");
            Features.Remember("steamgriddb-key", new string('a', 32));
            Assert(!Background.ForOption("steamgridkey") && !Background.ForGroup("Look"), "Adding the key clears the dot");
            Features.Remember("steamgriddb-key", ""); Features.Remember("squarekey-skipped", "");
            var logoBox = typeof(HomeNav).Assembly.GetType("Selene.LibraryStage").GetMethod("LogoBox", BindingFlags.NonPublic | BindingFlags.Static);
            var tall = (Size)logoBox.Invoke(null, new object[] { 545.0, 360.0 });
            var mid = (Size)logoBox.Invoke(null, new object[] { 640.0, 268.0 });
            var wide = (Size)logoBox.Invoke(null, new object[] { 1280.0, 275.0 });
            Assert(tall.Height > 260 && tall.Height <= 320, "Tall logos grow past the old 240 cap");
            Assert(Math.Abs(mid.Height - 240) < 1 && Math.Abs(wide.Width - 600) < 1, "Wide and mid logos keep their old size");
            var smallHero = typeof(TrailerFetch).GetMethod("SmallSteamHero", BindingFlags.NonPublic | BindingFlags.Static);
            Func<int, int, string> heroFile = (w, h) =>
            {
                var file = Path.Combine(Path.GetTempPath(), "selene-hero-" + w + "x" + h + ".jpg");
                var enc = new JpegBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, new byte[w * h], w)));
                using (var f = File.Create(file)) enc.Save(f);
                return file;
            };
            Assert((bool)smallHero.Invoke(null, new object[] { heroFile(1102, 620) }), "Steam cache's small background is picked for a sharper copy");
            Assert(!(bool)smallHero.Invoke(null, new object[] { heroFile(2204, 1240) }) && !(bool)smallHero.Invoke(null, new object[] { heroFile(1920, 1080) }), "Sharp or chosen backgrounds stay");
            var strip = inlineRow.GetType().GetField("Stages").GetValue(inlineRow);
            var stripCols = (IList)Field(strip, "columns");
            Assert(((TextBlock)stripCols[0].GetType().GetField("Value").GetValue(stripCols[0])).Text == "1 / 4", "Games row's strip shows current queue counts");
            Assert(((TextBlock)stripCols[0].GetType().GetField("State").GetValue(stripCols[0])).Text == "Downloading", "Unfinished art reads as downloading");
            var timer = (DispatcherTimer)Field(combined, "liveTimer"); Assert(timer.IsEnabled, "Visible settings observes progress");
            combined.Hidden(); Assert(!timer.IsEnabled, "Leaving settings stops its observer");
            Assert((int)checkedField.GetValue(null) == 1 && (bool)media.GetProperty("Enabled").GetValue(null, null), "Leaving settings preserves workers and consent");
            combined.Shown(); Pump(); Assert(timer.IsEnabled, "Returning resumes inline updates");
            combined.Handle("A"); Assert(Field(combined, "ask") is Dialog, "Enabled download row offers explicit stop confirmation");
            combined.Handle("B"); Assert((bool)media.GetProperty("Enabled").GetValue(null, null) && Field(combined, "ask") == null, "B keeps downloads running and returns to inline progress");
            var songWorkers = typeof(SongSetup).GetField("songWorkers", BindingFlags.NonPublic | BindingFlags.Static);
            songWorkers.SetValue(null, 1);
            combined.Handle("A"); Assert(Field(combined, "ask") == null, "A does nothing on the download row while downloads run");
            songWorkers.SetValue(null, 0);
            combined.Handle("A"); combined.Handle("DPadRight"); combined.Handle("A");
            foreach (var key in new[] { "artdownload", "screenshotsdownload", "trailerdownload", "songsetup", "metadatacomplete" }) Assert(!DownloadConsent.Allowed(key), "Stop revokes " + key);
            combined.Hidden(); Features.Set("downloadprogress", false);
            Assert((bool)media.GetMethod("ShowSetting").Invoke(null, new object[] { "songsetup" }), "Fallback restores separate download settings");
            Features.Set("downloadprogress", true);
            Assert(Array.TrueForAll(Features.All, option => option.About == null || option.About.Length <= 140), "Setting captions stay concise");
            var filterOption = Array.Find(Features.All, option => option.Key == "filtertabs");
            Assert(filterOption.Choices.Length == 2 && Array.IndexOf(filterOption.Choices, "both") < 0, "Filter tabs offer only Selene or Playnite presets");
            Features.SetValue("filtertabs", "both");
            Assert(Features.Value("filtertabs") == "selene", "Old Both preference falls back to Selene tabs");
            Features.SetValue("filtertabs", "playnite");
            Assert(Features.Value("filtertabs") == "playnite", "Playnite preset preference remains valid");
            Features.SetValue("filtertabs", "selene");
            var themeDirField = typeof(SelenePaths).GetField("themeDir", BindingFlags.NonPublic | BindingFlags.Static);
            var oldThemeDir = themeDirField.GetValue(null);
            themeDirField.SetValue(null, Path.GetDirectoryName(Path.GetDirectoryName(args[1])));
            var fallback = typeof(HomeNav).Assembly.GetType("Selene.DefaultBackdrop").GetMethod("ForTheme");
            Features.Set("defaultbackdrop", true);
            var still = fallback.Invoke(null, new object[] { "selene" });
            Assert(still is BitmapSource && ((BitmapSource)still).PixelWidth == 1920, "Selene fallback decodes the bundled backdrop still");
            Assert(ReferenceEquals(still, fallback.Invoke(null, new object[] { "selene" })), "Fallback reuses one decoded image across selections");
            Assert(fallback.Invoke(null, new object[] { "catppuccin" }) == null, "Other themes retain their own backgrounds");
            Features.Set("defaultbackdrop", false);
            Assert(fallback.Invoke(null, new object[] { "selene" }) == null, "Off retains the previous gradient fallback");
            themeDirField.SetValue(null, oldThemeDir);
            var soundField = typeof(HomeNav).GetField("navigateSound", BindingFlags.NonPublic | BindingFlags.Static);
            var savedSoundField = typeof(HomeNav).GetField("savedNavigateSound", BindingFlags.NonPublic | BindingFlags.Static);
            var oldSound = soundField.GetValue(null); var oldSavedSound = savedSoundField.GetValue(null);
            soundField.SetValue(null, typeof(FocusSoundProbe).GetProperty("NavigateSound")); savedSoundField.SetValue(null, IntPtr.Zero);
            FocusSoundProbe.NavigateSound = new IntPtr(123);
            Features.Set("quietdetails", true);
            var quietDetails = typeof(HomeNav).GetMethod("QuietDetailsTransition", BindingFlags.NonPublic | BindingFlags.Static);
            quietDetails.Invoke(null, null);
            Assert(FocusSoundProbe.NavigateSound == IntPtr.Zero, "Details transition suppresses native automatic-focus ticks");
            HomeNav.EndQuietFocus(); Assert(FocusSoundProbe.NavigateSound == new IntPtr(123), "Real navigation can end the quiet interval immediately");
            Features.Set("quietdetails", false); quietDetails.Invoke(null, null);
            Assert(FocusSoundProbe.NavigateSound == new IntPtr(123), "Off retains native focus sounds");
            var quietTimerField = typeof(HomeNav).GetField("quietTimer", BindingFlags.NonPublic | BindingFlags.Static);
            var quietTimer = quietTimerField.GetValue(null) as DispatcherTimer; if (quietTimer != null) quietTimer.Stop();
            soundField.SetValue(null, oldSound); savedSoundField.SetValue(null, oldSavedSound);
            foreach (var option in new[] {
                new[] { "Spotlight", "actualplatform" }, new[] { "Sound & music", "livevolume" },
                new[] { "Look", "activitycoverfit" }, new[] { "Look", "trailerlogogap" },
                new[] { "Look", "detailsbadges" }, new[] { "Look", "captureplayerfit" },
                new[] { "Spotlight", "fullscreenpresets" }, new[] { "Spotlight", "consistentfavorites" },
                new[] { "Sound & music", "independentmusic" }, new[] { "Games", "launchchoice" },
                new[] { "Games", "gamesask" }, new[] { "Sound & music", "selenemusic" }, new[] { "Look", "languagerestart" }, new[] { "Advanced", "shortaddonask" },
                new[] { "Advanced", "calmmotion" }, new[] { "Advanced", "presence" },
                new[] { "Advanced", "downloadprogress" }, new[] { "Look", "previewartfit" },
                new[] { "Look", "defaultbackdrop" }, new[] { "Sound & music", "quietdetails" } })
            {
                Features.Set(option[1], true);
                var settings = new SettingsScreen(false, option[0], option[1]);
                window.Content = settings.Root; Pump(); settings.Shown(); Pump();
                settings.Handle("A"); Assert(!Features.On(option[1]), "Settings UI exposes the off path for " + option[1]);
                settings.Handle("A"); Assert(Features.On(option[1]), "Settings UI exposes the on path for " + option[1]);
                settings.Hidden();
            }
            Console.WriteLine("Passed " + checks + " bug regression checks. No Windows volume, mute or output was changed.");
            return 0;
        }
        finally { window.Close(); if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
}
