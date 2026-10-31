using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Selene
{
    // Report a problem, no account needed, in three steps: what went wrong, where you were, anything to add (on-screen keys
    // or a keyboard). The versions and the logs go along (names, folders and emails taken out) to Selene's report service,
    // which opens a GitHub issue; a code to scan then follows it. If sending fails, the code opens GitHub's own form.
    public class ReportScreen : Screen
    {
        public override bool MenuOnStart { get { return false; } }   // (Start is its own here)
        private const string Service = "https://selene-report.mattxslv.workers.dev";
        private const string Form = "https://github.com/mattxslv/Selene/issues/new?template=bug_report.yml";

        private static readonly string[] Kinds = { "Something doesn't work", "Something looks wrong", "Selene froze or closed", "Controller or navigation", "Sound or music", "Other" };
        private static readonly int[] KindIcons = { 0xe868 /* bug */, 0xe3ad /* broken image */, 0xeb3b /* frozen */, 0xea28 /* controller */, 0xe050 /* sound */, 0xe5d3 /* more */ };
        private static readonly string[] Places = { "Starting Playnite", "Spotlight", "Collection", "A game's page", "Store", "Captures", "Settings or menu", "Starting or playing a game", "Quitting", "Somewhere else" };
        private static readonly int[] PlaceIcons = { 0xeb9b, 0xe88a, 0xe8f1, 0xe88e, 0xea12, 0xe412, 0xe8b8, 0xe037, 0xe879, 0xe5d3 };

        private readonly Func<Screen> back;
        private readonly Grid stage = new Grid();
        private readonly TextBlock stepLine;
        private readonly FrameworkElement header;
        private int step;          // 0 what, 1 where, 2 note, 3 result
        private int kind, place;
        private Grid stepRoot;
        private List<Tile> tiles = new List<Tile>();
        private int columns;
        private KeyPad pad;
        private TextBlock noteText, noteHint;
        private Border noteBox;
        private string note = "";
        private bool sending;
        private readonly byte[] shot;   // the screen as it was (both sticks), JPEG
        private bool includeShot;
        private int onTop = -1;   // Up from the keys: the chips you can press (the picture, typing on a phone)
        private readonly List<Border> tops = new List<Border>();
        private Border shotChip;
        private TextBlock shotLabel;

        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.94); } }
        public override bool WantsText { get { return step == 2 && !sending; } }

        // both sticks anywhere: a picture of the screen first, then the report over it
        public static void OpenFromShortcut()
        {
            var picture = Capture();
            Ui.Confirm();
            HomeNav.Log("report: opened with both sticks" + (picture != null ? " (picture " + picture.Length / 1024 + " KB)" : " (no picture)"));
            if (LoginScene.IsActive) LoginScene.Show(new ReportScreen(null, picture), 1);
            else LoginScene.OpenWith(() => new ReportScreen(null, picture));
        }

        private static byte[] Capture()
        {
            try
            {
                var w = Application.Current.MainWindow;
                var root = w != null ? w.Content as FrameworkElement : null;
                if (root == null || root.ActualWidth < 10) return null;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                System.Windows.Media.Imaging.BitmapSource image = rtb;
                double scale = Math.Min(1, 1600 / root.ActualWidth);
                if (scale < 1) image = new System.Windows.Media.Imaging.TransformedBitmap(rtb, new ScaleTransform(scale, scale));
                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 82 };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using (var ms = new MemoryStream()) { encoder.Save(ms); return ms.ToArray(); }
            }
            catch (Exception ex) { HomeNav.Log("report: picture failed: " + ex.Message); return null; }
        }

        public ReportScreen(Func<Screen> back, byte[] shot = null)
        {
            this.back = back;
            this.shot = shot;
            includeShot = shot != null;
            var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 80, 0, 0) };   // (centered over the choices)
            // Selene's mark, glowing, beside the title (as on Selene settings)
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var mark = Brand.Mark(64, true);
            mark.Margin = new Thickness(0, 0, 16, 0);
            mark.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(mark);
            titleRow.Children.Add(new TextBlock { Text = "Report a problem", FontSize = 42, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(titleRow);
            stepLine = new TextBlock { FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            head.Children.Add(stepLine);
            if (shot == null && Features.On("reportchord"))
                head.Children.Add(new TextBlock { Text = "Tip: click both sticks in together anywhere to report with a picture of the screen.", FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.45), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            header = head;
            Root.Children.Add(head);
            Root.Children.Add(stage);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Select")));
        }

        public override void Shown()
        {
            Ui.Rise(header, 40, 14, 300);
            Ui.Play("OpenPanel.wav", 0.8);
            Go(0, 0);
        }

        // ---- the steps ----
        private void Go(int to, int dir)
        {
            step = to;
            var old = stepRoot;
            stepRoot = to == 0 ? Choices(Kinds, KindIcons, 3, 300, 150, kind)
                     : to == 1 ? Choices(Places, PlaceIcons, 5, 236, 132, place)
                     : to == 2 ? NotePage()
                     : stepRoot;
            stepLine.Text = to == 0 ? "Step 1 of 3  \u00B7  What went wrong?" : to == 1 ? "Step 2 of 3  \u00B7  Where were you?" : to == 2 ? "Step 3 of 3  \u00B7  Anything to add?" : "";
            if (ReferenceEquals(old, stepRoot)) return;
            stage.Children.Add(stepRoot);
            if (old != null) { var leaving = old; Ui.Slide(leaving, 0, -dir * 90, 1, 0, 220, () => stage.Children.Remove(leaving)); }
            Ui.Slide(stepRoot, dir * 90, 0, 0, 1, 300);
            ShowFocus();
        }

        private Grid Choices(string[] labels, int[] icons, int cols, double w, double h, int at)
        {
            columns = cols;
            tiles = new List<Tile>();
            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
            var wrap = new WrapPanel { Width = cols * (w + 24) };
            for (int i = 0; i < labels.Length; i++)
            {
                var t = new Tile(w, h, 18);
                var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new TextBlock { Text = Ui.Icon(icons[i]), FontFamily = Ui.Icons, FontSize = 40, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center });
                content.Children.Add(new TextBlock { Text = labels[i], FontSize = 19, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 12, 14, 0) });
                t.Face.Child = content;
                t.Root.Margin = new Thickness(12);
                wrap.Children.Add(t.Root);
                tiles.Add(t);
                Ui.Rise(t.Root, 60 + i * 40, 12, 280);
            }
            grid.Children.Add(wrap);
            return grid;
        }

        private Grid NotePage()
        {
            tiles = new List<Tile>();
            var page = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 70, 0, 0) };
            page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(900) });
            page.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var col = new StackPanel();
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            chips.Children.Add(Chip(KindIcons[kind], Kinds[kind]));
            chips.Children.Add(Chip(PlaceIcons[place], Places[place]));
            tops.Clear();
            if (shot != null) { chips.Children.Add(ShotChip()); tops.Add(shotChip); }
            col.Children.Add(chips);
            noteText = new TextBlock { FontSize = 21, FontFamily = Ui.Body, TextWrapping = TextWrapping.Wrap };
            noteBox = new Border { Child = noteText, MinHeight = 96, CornerRadius = new CornerRadius(18), Padding = new Thickness(24, 18, 24, 18), Background = Ink.Brush("SeleneInkColor", 0.08), BorderBrush = Ink.Brush("SeleneFocusColor", 0.7), BorderThickness = new Thickness(2) };
            col.Children.Add(noteBox);
            noteHint = new TextBlock { Text = "Nothing to add? Press Send as it is, with A on it or the Start button.", FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            col.Children.Add(noteHint);
            // what goes with it: the full logs are public on GitHub with the issue
            col.Children.Add(new TextBlock { Text = "Sent with it, public on GitHub: Selene's and Playnite's logs, your add-ons, settings and displays (your user name removed).", FontSize = 14, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.45), HorizontalAlignment = noteHint.HorizontalAlignment, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            pad = new KeyPad("Send");
            pad.Root.Margin = new Thickness(0, 20, 0, 0);
            pad.Typed = Type;
            pad.Erased = Erase;
            pad.Done = Send;
            col.Children.Add(pad.Root);
            page.Children.Add(col);

            // or type on a phone: its code, ready as the page opens
            var side = new StackPanel { Width = 300, Margin = new Thickness(56, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            head.Children.Add(new TextBlock { Text = Ui.Icon(0xe32c), FontFamily = Ui.Icons, FontSize = 26, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            head.Children.Add(new TextBlock { Text = "Or type on your phone", FontSize = 22, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
            side.Children.Add(head);
            phoneCode = new Image { Width = 220, Height = 220 };
            RenderOptions.SetEdgeMode(phoneCode, EdgeMode.Aliased);
            phoneTile = new Border { Child = phoneCode, Background = Brushes.White, CornerRadius = new CornerRadius(18), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0), Opacity = 0 };
            side.Children.Add(phoneTile);
            phoneLine = new TextBlock { FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
            side.Children.Add(phoneLine);
            Grid.SetColumn(side, 1);
            page.Children.Add(side);
            Ui.Rise(side, 160, 14, 300);
            ShowNote();
            PreparePhone();
            return page;
        }

        private static Border Chip(int icon, string text)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = Ui.Icon(icon), FontFamily = Ui.Icons, FontSize = 20, Foreground = Ink.Brush("SeleneInkColor", 0.85), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            line.Children.Add(new TextBlock { Text = text, FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.85), VerticalAlignment = VerticalAlignment.Center });
            return new Border { Child = line, CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 7, 16, 8), Margin = new Thickness(0, 0, 10, 0), Background = Ink.Brush("SeleneInkColor", 0.1) };
        }

        // the picture going with it, which Up from the keys reaches: A leaves it out or puts it back
        private Border ShotChip()
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(shot);
            image.DecodePixelWidth = 160;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new Border { Width = 80, Height = 45, CornerRadius = new CornerRadius(6), Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill }, Margin = new Thickness(0, 0, 10, 0) });
            shotLabel = new TextBlock { FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.85), VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(shotLabel);
            shotChip = new Border { Child = line, CornerRadius = new CornerRadius(14), Padding = new Thickness(6, 6, 16, 6), Background = Ink.Brush("SeleneInkColor", 0.1), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent };
            shotLabel.Text = "Picture of the screen included";
            return shotChip;
        }

        private void ShowTops()
        {
            for (int i = 0; i < tops.Count; i++) tops[i].BorderBrush = i == onTop ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
            if (shotChip == null) return;
            shotLabel.Text = includeShot ? "Picture of the screen included" : "Picture left out";
            shotChip.Opacity = includeShot ? 1 : 0.55;
        }

        private void ShowNote()
        {
            bool empty = note.Length == 0;
            noteText.Text = empty ? "What happened? A few words help a lot (optional)." : note + "\u258F";
            noteText.Foreground = Ink.Brush("SeleneInkColor", empty ? 0.45 : 0.95);
            if (noteHint != null) noteHint.Visibility = empty ? Visibility.Visible : Visibility.Hidden;
            if (pad != null) pad.After(note);
        }

        private void ShowFocus()
        {
            int at = step == 0 ? kind : place;
            for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(i == at);
            if (step == 2 && pad != null) pad.Focus(true);
        }

        public override void Type(string text)
        {
            if (step != 2 || sending) return;
            if (note.Length + text.Length > 500) { Ui.Shake(noteBox); return; }
            note += text;
            Ui.Key();
            ShowNote();
        }

        public override void Erase()
        {
            if (step != 2 || sending) return;
            if (note.Length == 0) { Ui.Shake(noteBox); return; }
            note = note.Substring(0, note.Length - 1);
            Ui.Key();
            ShowNote();
        }

        public override void Handle(string b)
        {
            if (sending) return;
            if (step == 3) { if (b == "A" || b == "B") { Ui.Back(); Leave(); } return; }
            if (b == "B")
            {
                Ui.Back();
                if (step == 0) Leave(); else Go(step - 1, -1);
                return;
            }
            if (step == 2)
            {
                if (onTop >= 0)
                {
                    var chip = tops[onTop];
                    if (b == "A" && ReferenceEquals(chip, shotChip)) { includeShot = !includeShot; Ui.Key(); Ui.Pop(chip); ShowTops(); PreparePhone(); }
                    else if (b == "DPadLeft" || b == "DPadRight")
                    {
                        int next = onTop + (b == "DPadRight" ? 1 : -1);
                        if (next < 0 || next >= tops.Count) { Ui.Shake(chip); return; }
                        onTop = next; Ui.Tick(); ShowTops();
                    }
                    else if (b == "DPadDown") { onTop = -1; Ui.Tick(); ShowTops(); pad.Focus(true); }
                    else if (b == "Start") pad.Press(b);
                    return;
                }
                if (b.StartsWith("DPad"))
                {
                    if (pad.Move(b)) return;
                    if (tops.Count == 0) { Ui.Shake(noteBox); return; }
                    onTop = 0; Ui.Tick(); pad.Focus(false); ShowTops();
                    return;
                }
                pad.Press(b);
                return;
            }
            int at = step == 0 ? kind : place;
            if (b.StartsWith("DPad"))
            {
                int next = at + (b == "DPadRight" ? 1 : b == "DPadLeft" ? -1 : b == "DPadDown" ? columns : -columns);
                bool sameRow = b == "DPadUp" || b == "DPadDown" || next / columns == at / columns;
                if (next < 0 || next >= tiles.Count || !sameRow) { Ui.Shake(tiles[at].Root); return; }
                if (step == 0) kind = next; else place = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b == "A") { Ui.Confirm(); Go(step + 1, 1); }
        }

        private void Leave()
        {
            if (back == null) LoginScene.Finish(); else LoginScene.Show(back(), -1);
        }

        // ---- sending ----
        private void Send()
        {
            if (sending) return;
            sending = true;
            Ui.Confirm();
            pad.DoneText = "Sending...";
            Post("/report", Scrub(note.Trim()), (result, error) => Done(result != null ? result["url"] as string : null, error));
        }

        // the report as it stands (the logs read off the UI thread) to the service; done gets its answer (or an error)
        private void Post(string path, string text, Action<Dictionary<string, object>, string> done)
        {
            var report = new Dictionary<string, object>
            {
                { "category", Kinds[kind] }, { "place", Places[place] }, { "note", text },
                { "selene", Brand.Version }, { "playnite", PlayniteVersion() }, { "windows", WindowsName() },
            };
            if (shot != null && includeShot) report["screenshot"] = Convert.ToBase64String(shot);
            var dispatcher = Root.Dispatcher;
            Task.Factory.StartNew(() =>
            {
                Dictionary<string, object> answer = null;
                string error = null;
                try
                {
                    var seleneLog = ReadLines(Path.Combine(Path.GetTempPath(), "selene.log"));
                    var playniteLog = ReadLines(HomeNav.Api != null ? Path.Combine(HomeNav.Api.Paths.ConfigurationPath, "playnite.log") : "");
                    // this session only (from Selene's start), the problems once each with how often
                    int start = seleneLog.FindLastIndex(l => l.Contains("login: scene attached"));
                    var session = start > 0 ? seleneLog.Skip(start - 5).ToList() : seleneLog;
                    report["seleneLog"] = Scrub(string.Join("\n", Last(session.Where(l => !Noise(l)), 350)));
                    report["seleneProblems"] = Scrub(string.Join("\n", Grouped(session.Where(Problem))));
                    report["playniteLog"] = Scrub(string.Join("\n", Last(playniteLog.Where(l => l.Contains("|ERROR") || l.Contains("|WARN") || l.StartsWith("   ") || l.StartsWith("System.")), 120)));
                    // everything else that helps: whole logs, add-ons, settings, displays, compressed; the service keeps it and
                    // links it from the issue ("Full logs"), the issue itself keeps the short version above
                    try { report["logBundle"] = Convert.ToBase64String(Gzip(Scrub(ReportBundle.Build(seleneLog, playniteLog)))); }
                    catch (Exception ex) { HomeNav.Log("report: full logs left out: " + ex.Message); }
                    var result = Call(path, report);
                    object ok, err;
                    if (result.TryGetValue("ok", out ok) && ok is bool && (bool)ok) answer = result;
                    else error = result.TryGetValue("error", out err) ? err as string : "unknown";
                }
                catch (Exception ex) { error = ex.Message; }
                dispatcher.BeginInvoke(new Action(() => done(answer, error)));
            });
        }

        // one request to the service (null body: a GET)
        private static Dictionary<string, object> Call(string path, Dictionary<string, object> body)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var request = (HttpWebRequest)WebRequest.Create(Service + path);
            request.UserAgent = "Selene/" + Brand.Version;
            request.Timeout = 20000;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(js.Serialize(body));
                request.Method = "POST";
                request.ContentType = "application/json";
                using (var s = request.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            string text;
            try { using (var response = request.GetResponse()) using (var r = new StreamReader(response.GetResponseStream())) text = r.ReadToEnd(); }
            catch (WebException ex) { if (ex.Response == null) throw; using (var r = new StreamReader(ex.Response.GetResponseStream())) text = r.ReadToEnd(); }
            return js.Deserialize<Dictionary<string, object>>(text);
        }

        // ---- finishing on a phone: the report goes up as a draft, the phone's page adds the words and sends it ----
        private Image phoneCode;
        private Border phoneTile;
        private TextBlock phoneLine;
        private DispatcherTimer phoneWatch;
        private string draftId;
        private int draftTurn;

        private void PreparePhone()
        {
            int turn = ++draftTurn;
            draftId = null;
            if (phoneWatch != null) phoneWatch.Stop();
            Ui.Fade(phoneTile, 0.15, 160);
            phoneLine.Text = "Getting the code ready...";
            Post("/draft", "", (result, error) =>
            {
                if (turn != draftTurn || step != 2 || sending) return;
                if (result == null)
                {
                    HomeNav.Log("report: phone draft failed: " + error);
                    phoneLine.Text = error == "slow down" ? "Too many reports in a row for now. Send it from here." : "Couldn't reach the report service. Send it from here, or check the internet.";
                    return;
                }
                draftId = result["id"] as string;
                try { phoneCode.Source = new QrCode(result["url"] as string).Picture(); } catch (Exception ex) { HomeNav.Log("report: code: " + ex.Message); return; }
                Ui.Fade(phoneTile, 1, 260);
                phoneLine.Text = "Scan with your phone's camera, type there and send. This screen follows.";
                Watch(draftId, turn);
            });
        }

        // the phone sent it: this screen shows it sent too
        private void Watch(string id, int turn)
        {
            phoneWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            phoneWatch.Tick += (s, e) =>
            {
                if (turn != draftTurn || step != 2 || sending) { ((DispatcherTimer)s).Stop(); return; }
                var dispatcher = Root.Dispatcher;
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        var answer = Call("/r/" + id + "/status", null);
                        object ok;
                        if (answer.TryGetValue("ok", out ok) && ok is bool && (bool)ok)
                            dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (turn != draftTurn || step != 2 || sending) return;
                                if (phoneWatch != null) phoneWatch.Stop();
                                HomeNav.Log("report: sent from the phone");
                                Done(answer["url"] as string, null);
                            }));
                    }
                    catch { }
                });
            };
            phoneWatch.Start();
        }

        // the frame timings and polling lines fill a log without saying what happened
        private static bool Noise(string line)
        {
            foreach (var n in new[] { "slow job", "frame gap", "frames: ", "UPS ", "fade image", "watch: ", "details view found", "screenshots show", "full refresh rate" })
                if (line.IndexOf(n, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool Problem(string line)
        {
            if (!Regex.IsMatch(line, @"fail|error|exception|couldn't|not found|crash", RegexOptions.IgnoreCase) || Noise(line)) return false;
            // known and harmless: a clip handed to the browser player, a list looked for before it's built
            return line.IndexOf("clip failed in the player", StringComparison.Ordinal) < 0 && line.IndexOf("list not found/visible", StringComparison.Ordinal) < 0;
        }

        // the same message over and over: once, with its last time and how often
        private static IEnumerable<string> Grouped(IEnumerable<string> lines)
        {
            var order = new List<string>();
            var last = new Dictionary<string, string>();
            var count = new Dictionary<string, int>();
            foreach (var line in lines)
            {
                var text = line.Length > 13 && line[2] == ':' ? line.Substring(13) : line;
                if (!count.ContainsKey(text)) { order.Add(text); count[text] = 0; }
                count[text]++;
                last[text] = line.Length > 12 ? line.Substring(0, 12) : "";
            }
            return order.Select(t => last[t] + " " + t + (count[t] > 1 ? "  (x" + count[t] + ")" : "")).Skip(Math.Max(0, order.Count - 40));
        }

        private void Done(string url, string error)
        {
            sending = false;
            bool ok = url != null;
            if (ok) { Ui.Play("Success.wav", 0.8); HomeNav.Log("report: sent, " + url); }
            else HomeNav.Log("report: sending failed: " + error);
            var number = ok ? url.Substring(url.LastIndexOf('/') + 1) : null;
            var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(48), HorizontalAlignment = HorizontalAlignment.Center, Background = ok ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.14) };
            badge.Child = Ui.Glyph(Ui.Icon(ok ? 0xe876 : 0xe000), Ui.Icons, 52, ok ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor"));
            card.Children.Add(badge);
            card.Children.Add(new TextBlock { Text = ok ? "Thanks, report #" + number + " is sent" : "Couldn't send it", FontSize = 38, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0) });
            card.Children.Add(new TextBlock
            {
                Text = ok ? "Scan the code to see it on GitHub. Adding screenshots or comments there needs a free GitHub account."
                     : error == "slow down" ? "That's a few reports in a row. Try again in an hour, or scan the code to report on GitHub."
                     : "No internet, maybe. Scan the code to report on GitHub instead.",
                FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.65), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 700, Margin = new Thickness(0, 10, 0, 0)
            });
            try
            {
                var link = ok ? url : Form + "&selene=" + Uri.EscapeDataString(Brand.Version) + "&playnite=" + Uri.EscapeDataString(PlayniteVersion());
                var code = new Image { Source = new QrCode(link).Picture(), Width = 230, Height = 230 };
                RenderOptions.SetEdgeMode(code, EdgeMode.Aliased);
                card.Children.Add(new Border { Child = code, Background = Brushes.White, CornerRadius = new CornerRadius(18), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) });
            }
            catch (Exception ex) { HomeNav.Log("report: code: " + ex.Message); }
            var result = new Grid();
            result.Children.Add(card);
            var page = stepRoot;
            step = 3;
            stepRoot = result;
            stage.Children.Add(result);
            if (page != null) Ui.Fade(page, 0, 200, 0, () => stage.Children.Remove(page));
            Ui.Fade(header, 0, 200);
            for (int i = 0; i < card.Children.Count; i++) Ui.Rise((UIElement)card.Children[i], 40 + i * 60, 14, 320);
        }

        private static byte[] Gzip(string text)
        {
            var raw = Encoding.UTF8.GetBytes(text ?? "");
            using (var ms = new MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        private static List<string> ReadLines(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new List<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            }
            catch (Exception ex) { return new List<string> { "(couldn't read: " + ex.Message + ")" }; }
        }

        private static IEnumerable<string> Last(IEnumerable<string> lines, int n)
        {
            var list = lines.ToList();
            return list.Skip(Math.Max(0, list.Count - n));
        }

        // the service takes out home folders and emails too; the Windows user and PC names go here as well
        private static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (var name in new[] { Environment.UserName, Environment.MachineName })
                if (!string.IsNullOrEmpty(name) && name.Length >= 3)
                    text = Regex.Replace(text, Regex.Escape(name), "<user>", RegexOptions.IgnoreCase);
            return text;
        }

        private static string PlayniteVersion()
        {
            try { var v = HomeNav.Api.ApplicationInfo.ApplicationVersion; return v.Major + "." + v.Minor; } catch { return ""; }
        }

        private static string WindowsName()
        {
            var v = Environment.OSVersion.Version;
            if (v.Major != 10) return "Windows " + v;
            return (v.Build >= 22000 ? "Windows 11" : "Windows 10") + " (" + v.Build + ")";
        }
    }
}
