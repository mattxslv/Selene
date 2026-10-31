using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Selene
{
    // Isolated collaborators: the production Lang.cs and BackgroundFit.cs are compiled unchanged.
    public static class Features { public static string Language = "auto", Fit = "fill"; public static string Value(string key) { return key == "language" ? Language : Fit; } public static bool RestartSwitch = true; public static bool On(string key) { return key == "languagerestart" && RestartSwitch; } }
    public static class HomeNav { public static object Api; public static void Log(string message) { } }
    public static class NewsCard { public static void Refresh(bool force) { } }
    public static class Ui { public static bool PadCopy; public static bool PressFromPad; public static bool KeyIsPadCopy() { return PadCopy; } }
    public class GameControllerInputEventArgs : System.Windows.Input.KeyEventArgs
    {
        public GameControllerInputEventArgs(PresentationSource source) : base(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, System.Windows.Input.Key.Enter) { }
    }
    public static class SelenePaths
    {
        public static string Root;
        public static string DataFolder { get { return Path.Combine(Root, "user"); } }
        public static string ThemeFile(string path) { return Path.Combine(Root, "theme", path); }
    }
    public class MockSettings { public string Language { get; set; } }
    public class MockApi { public MockSettings ApplicationSettings { get; set; } }
    public class Model { public string Title { get { return "Back"; } } }
    public class SortGame { public string Id, Name; public DateTime? Played; public int Preset; }
    public static class Check
    {
        private static int assertions;
        private static void Equal(object expected, object actual, string reason)
        {
            if (!object.Equals(expected, actual)) throw new Exception(reason + ": expected " + expected + ", got " + actual);
            assertions++;
        }
        private static void PumpUntil(Func<bool> finished)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (s, e) => { if (finished() || elapsed.ElapsedMilliseconds > 4000) { timer.Stop(); frame.Continue = false; } };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (!finished()) throw new Exception("Startup work did not complete within the test deadline");
        }
        [STAThread]
        public static int Main()
        {
            Func<string, string, System.Collections.Generic.KeyValuePair<string, string>> candidate = (id, title) => new System.Collections.Generic.KeyValuePair<string, string>(id, title);
            Equal("870780", GameTitleMatch.Select("Control", new[] { candidate("3669870", "CONTROL Resonant"), candidate("870780", "CONTROL Ultimate Edition") }), "Control resolves to its edition, never its sequel");
            Equal("base", GameTitleMatch.Select("Control", new[] { candidate("sequel", "Control Resonant"), candidate("edition", "Control Ultimate Edition"), candidate("base", "CONTROL") }), "Exact title wins over every edition regardless of result order");
            Equal(null, GameTitleMatch.Select("Control", new[] { candidate("sequel", "Control Resonant") }), "A prefix alone is never a match");
            Equal(null, GameTitleMatch.Select("Control Resonant", new[] { candidate("base", "Control") }), "A longer requested title never matches its prefix");
            Equal(null, GameTitleMatch.Select("Game", new[] { candidate("demo", "Game Demo"), candidate("dlc", "Game Soundtrack"), candidate("sequel", "Game 2") }), "Demos, soundtracks and numbered sequels are rejected");
            Equal(null, GameTitleMatch.Select("Game", new[] { candidate("one", "Game Complete Edition"), candidate("two", "Game Deluxe Edition") }), "Ambiguous editions are left unmatched");
            Equal(null, GameTitleMatch.Select("", new[] { candidate("one", "A Game") }), "Empty titles never match");
            Equal("one", GameTitleMatch.Select("大神", new[] { candidate("two", "別のゲーム"), candidate("one", "大神") }), "Non-Latin titles retain their identity");
            Equal("one", GameTitleMatch.Select("Game™: Title", new[] { candidate("one", "GAME - Title") }), "Case, trademark and punctuation differences are harmless");
            SelenePaths.Root = Path.Combine(Path.GetTempPath(), "selene-localization-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(SelenePaths.ThemeFile("Localization"));
                var user = Path.Combine(SelenePaths.DataFolder, "Localization"); Directory.CreateDirectory(user);
                File.WriteAllText(Path.Combine(SelenePaths.ThemeFile("Localization"), "de.txt"), "Back = Zurück\nSelect = Wählen\nMenu = Menü\n");
                File.WriteAllText(Path.Combine(SelenePaths.ThemeFile("Localization"), "de_DE.txt"), "Select = Auswählen\n");
                File.WriteAllText(Path.Combine(user, "de.txt"), "Back = Benutzer\n");
                var api = new MockApi { ApplicationSettings = new MockSettings { Language = "de-DE" } };
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var existingText = new TextBlock { Text = "Back" };
                var existingWindow = new Window { Content = existingText, Width = 100, Height = 100, Left = -32000, Top = -32000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
                existingWindow.Show();
                var inputSource = PresentationSource.FromVisual(existingWindow);
                var text = new TextBlock { Text = "Back" };
                Lang.Load(api); Lang.Track(text);
                Lang.Refresh();
                app.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Equal("Benutzer", existingText.Text, "Existing UI created before language loading is revisited");
                existingWindow.Close();
                Equal("de_DE", Lang.Language, "Hyphenated language normalized");
                Equal("Benutzer", text.Text, "User base language overrides shipped region");
                text.Text = "Select"; Equal("Auswählen", text.Text, "Text changes after load use regional phrases");
                text.Text = "Menu"; Equal("Menü", text.Text, "Partial user files retain shipped translations");
                text.Text = "Missing phrase"; Equal("Missing phrase", text.Text, "Missing translation stays English");
                Lang.Apply(text); Equal("Missing phrase", text.Text, "Repeated application is stable");
                text.Text = "Back"; Lang.Apply(text); Equal("Benutzer", text.Text, "Already translated text is stable");
                var bound = new TextBlock(); BindingOperations.SetBinding(bound, TextBlock.TextProperty, new Binding("Title") { Source = new Model() });
                Lang.Track(bound); Equal("Back", bound.Text, "Bound game data is preserved");
                Features.Language = "en"; Lang.Load(api); var english = new TextBlock { Text = "Back" }; Lang.Track(english);
                Equal("en", Lang.Language, "Explicit language overrides Playnite"); Equal("Back", english.Text, "English stays English");
                Features.Language = "auto"; api.ApplicationSettings.Language = "english"; Lang.Load(api); Equal("en_US", Lang.Language, "Playnite English does not follow OS language");
                // the restart question after picking a language: only when the words would change
                HomeNav.Api = api; Features.RestartSwitch = true;
                Equal("False", Lang.WouldChange("auto").ToString(), "First setup: following an English Playnite needs no restart");
                Equal("False", Lang.WouldChange("en").ToString(), "First setup: choosing English on an English start needs no restart");
                Equal("True", Lang.WouldChange("de").ToString(), "Choosing another language still asks");
                Features.RestartSwitch = false; Equal("True", Lang.WouldChange("en").ToString(), "Switch off: always asks, as before"); Features.RestartSwitch = true;
                Features.Language = "de"; Lang.Load(api); Equal("False", Lang.WouldChange("de").ToString(), "Going back to the language Selene started in needs no restart");
                Equal("True", Lang.WouldChange("en").ToString(), "Leaving the language Selene started in asks");
                Features.Language = "auto"; api.ApplicationSettings.Language = "english"; Lang.Load(api); HomeNav.Api = null;
                Features.Fit = "fill"; Equal(Stretch.UniformToFill, BackgroundFit.Current, "Fill crops proportionally");
                Features.Fit = "fit"; BackgroundFit.Apply(); Equal(Stretch.Uniform, app.Resources["SeleneBackgroundStretch"], "Fit updates resource");
                Features.Fit = "stretch"; BackgroundFit.Apply(); Equal(Stretch.Fill, app.Resources["SeleneBackgroundStretch"], "Stretch updates resource");
                Equal(false, NewsFeeds.WebUrl("javascript:alert(1)"), "Script URLs rejected");
                Equal(false, NewsFeeds.WebUrl("https://name:password@example.org/feed"), "Credentials in URLs rejected");
                Equal(true, NewsFeeds.WebUrl("https://example.org/feed"), "HTTPS feeds accepted");
                string rss = "<rss><channel><item><title>Patch &amp; update</title><link>/patch</link><pubDate>Tue, 27 Oct 2026 10:00:00 GMT</pubDate></item><item><title>Unsafe</title><link>javascript:alert(1)</link></item></channel></rss>";
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rss)))
                {
                    var parsed = NewsFeeds.Parse(stream, new Uri("https://example.org/feed"));
                    Equal(1, parsed.Count, "RSS rejects unsafe article links");
                    Equal("https://example.org/patch", parsed[0].Url, "Relative article links resolved");
                    Equal("Patch & update", parsed[0].Title, "RSS XML entities decoded");
                    Equal(2026, parsed[0].Published.Year, "RSS dates parsed");
                }
                string atom = "<feed xmlns='http://www.w3.org/2005/Atom'><entry><title>News</title><link rel='self' href='/feed/1'/><link rel='alternate' href='/story'/><updated>2026-10-27T11:00:00Z</updated></entry></feed>";
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(atom)))
                {
                    var parsed = NewsFeeds.Parse(stream, new Uri("https://example.org/feed"));
                    Equal(1, parsed.Count, "Namespaced Atom parsed");
                    Equal("https://example.org/story", parsed[0].Url, "Atom alternate link used");
                    Equal(11, parsed[0].Published.Hour, "Atom dates parsed");
                }
                bool dtdRejected = false;
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE rss [<!ENTITY x 'test'>]><rss>&x;</rss>")))
                    try { NewsFeeds.Parse(stream, new Uri("https://example.org")); } catch (System.Xml.XmlException) { dtdRejected = true; }
                Equal(true, dtdRejected, "XML DTDs prohibited");
                var feeds = new System.Collections.Generic.List<NewsFeed> { new NewsFeed { Url = "https://example.org/feed", Enabled = true } };
                NewsFeeds.Save(feeds); Equal(1, NewsFeeds.Load().Count, "Feed added and persisted");
                feeds[0].Enabled = false; NewsFeeds.Save(feeds); Equal(false, NewsFeeds.Load()[0].Enabled, "Feed disable persisted");
                feeds.Clear(); NewsFeeds.Save(feeds); Equal(0, NewsFeeds.Load().Count, "Feed removal persisted");
                bool homeReady = false, inputRan = false;
                int jobsRun = 0;
                var workTimes = new System.Collections.Generic.List<long>();
                var workClock = System.Diagnostics.Stopwatch.StartNew();
                StartupWork.Start(() => homeReady);
                StartupWork.AfterHome("first test job", () => { jobsRun++; workTimes.Add(workClock.ElapsedMilliseconds); }, 100);
                StartupWork.AfterHome("failing test job", () => { jobsRun++; workTimes.Add(workClock.ElapsedMilliseconds); throw new Exception("Expected test failure"); }, 100);
                StartupWork.AfterHome("last test job", () => { jobsRun++; workTimes.Add(workClock.ElapsedMilliseconds); }, 100);
                var wait = System.Diagnostics.Stopwatch.StartNew();
                PumpUntil(() => wait.ElapsedMilliseconds >= 250);
                Equal(0, jobsRun, "Background jobs wait while intro/profile setup is active");
                homeReady = true;
                PumpUntil(() => jobsRun == 3);
                Equal(3, jobsRun, "Every enabled job runs, including after an earlier job failed");
                Equal(true, workTimes[1] - workTimes[0] >= 90 && workTimes[2] - workTimes[1] >= 90, "Jobs are separated by quiet intervals");
                int setupSteps = 0;
                StartupWork.Yield(
                    () => { setupSteps++; app.Dispatcher.BeginInvoke(new Action(() => inputRan = true), System.Windows.Threading.DispatcherPriority.Input); },
                    () => { Equal(true, inputRan, "Input gets a dispatcher turn between required setup steps"); setupSteps++; });
                PumpUntil(() => setupSteps == 2);
                Equal(2, setupSteps, "All required setup steps finish");
                var day = new DateTime(2026, 10, 27);
                var sortGames = new object[] {
                    new SortGame { Id = "old", Name = "Old pinned", Played = day.AddDays(-10), Preset = 4 },
                    new SortGame { Id = "new", Name = "Newest", Played = day, Preset = 3 },
                    new SortGame { Id = "b", Name = "beta", Preset = 2 },
                    new SortGame { Id = "a", Name = "Alpha", Preset = 1 }
                };
                int idReads = 0, dateReads = 0, nameReads = 0;
                Func<object, string> gameId = g => { idReads++; return ((SortGame)g).Id; };
                Func<object, DateTime?> gameDate = g => { dateReads++; return ((SortGame)g).Played; };
                Func<object, string> gameName = g => { nameReads++; return ((SortGame)g).Name; };
                var sorted = GameRowOrder.Sort(sortGames, new[] { "old", "old" }, gameId, gameDate, gameName, null);
                Equal(sortGames[0], sorted[0], "Pinned order wins over last played, including duplicate pins");
                Equal(sortGames[1], sorted[1], "Unpinned games are sorted by last played");
                Equal(sortGames[3], sorted[2], "Games with no activity are sorted by name ignoring case");
                Equal(sortGames.Length, idReads, "Game IDs are read once per game, not once per comparison");
                Equal(sortGames.Length, dateReads, "Profile dates are read once per game");
                Equal(sortGames.Length, nameReads, "Names are read once per game");
                sorted = GameRowOrder.Sort(sortGames, new string[0], gameId, gameDate, gameName, g => ((SortGame)g).Preset);
                Equal(sortGames[3], sorted[0], "Playnite preset order wins over recent activity");
                Equal(sortGames[0], sorted[3], "Playnite preset order is fully preserved");
                int modeChanges = 0;
                InputHints.Changed += () => modeChanges++;
                InputHints.Set(InputKind.Controller);
                Equal(null, InputHints.Label("B"), "Controller leaves Xbox/PlayStation labeling to the theme");
                var keyboardEvent = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, inputSource, Environment.TickCount, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                InputHints.Observe(keyboardEvent);
                Equal(InputKind.Keyboard, InputHints.Kind, "A physical keyboard event switches hints");
                Equal("Esc", InputHints.Label("B"), "Keyboard Back uses Escape");
                Equal("Enter", InputHints.Label("A"), "Keyboard Select uses Enter");
                int before = modeChanges;
                InputHints.Observe(keyboardEvent); Equal(before, modeChanges, "Repeated keyboard input does not rebuild hints");
                InputHints.PointerMoved(new Point(0, 0)); InputHints.PointerMoved(new Point(1, 0)); InputHints.PointerMoved(new Point(2, 0));
                Equal(InputKind.Keyboard, InputHints.Kind, "Tiny pointer changes do not flicker device hints");
                InputHints.PointerMoved(new Point(4, 0)); Equal(InputKind.Mouse, InputHints.Kind, "Slow cumulative mouse movement switches hints");
                Equal("Click", InputHints.Label("A"), "Mouse Select shows Click");
                InputHints.Observe(new GameControllerInputEventArgs(inputSource)); Equal(InputKind.Controller, InputHints.Kind, "Controller input restores controller hints");
                Ui.PadCopy = true; InputHints.Observe(keyboardEvent); Equal(InputKind.Controller, InputHints.Kind, "Controller-generated keyboard copies cannot switch hints"); Ui.PadCopy = false;
                Ui.PressFromPad = true; InputHints.Observe(keyboardEvent); Equal(InputKind.Controller, InputHints.Kind, "Fresh controller presses suppress trailing keyboard input"); Ui.PressFromPad = false;
                InputHints.Observe(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left));
                Equal(InputKind.Mouse, InputHints.Kind, "Mouse clicks switch hints without movement");
                Console.WriteLine("Passed " + assertions + " localization, startup, game ordering and input-device assertions.");
                return 0;
            }
            finally { if (Directory.Exists(SelenePaths.Root)) Directory.Delete(SelenePaths.Root, true); }
        }
    }
}
