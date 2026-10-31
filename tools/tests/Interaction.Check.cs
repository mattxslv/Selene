using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Selene;
class InteractionCheck
{
    static int checks;
    static void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
    static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    [STAThread] static int Main(string[] args)
    {
        string scratch = Path.Combine(args[0], "runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("TEMP",scratch); Environment.SetEnvironmentVariable("TMP",scratch);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["SeleneInkColor"] = Colors.White; app.Resources["SeleneOnFocusColor"] = Colors.Black;
        app.Resources["SeleneGlassColor"] = Color.FromRgb(25,30,40); app.Resources["GlyphColor"] = Color.FromRgb(150,190,250);
        app.Resources["SeleneFocusColor"] = Color.FromRgb(150,190,250); app.Resources["SeleneScrimColor"] = Color.FromRgb(8,12,18);
        app.Resources["SeleneStatStoresSet"] = true;
        Features.Init(new SeleneSettings(), () => {}); Features.Set("sounds",false);
        InputHints.Changed += Ui.PublishPrompts; Ui.RefreshPrompts();
        var window = new Window { Width=1920,Height=1080,Left=-32000,Top=-32000,Opacity=0,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None };
        try
        {
            Assert(!LanguageSetup.Complete, "First-time language selection must be pending");
            Assert(LoginScene.Start() is LanguageScreen,"New users must start on the language screen");
            var language = new LanguageScreen(); window.Content=language.Root; window.Show(); Pump();
            Render(language.Root,Path.Combine(args[0],"first-language.png"));
            var prompt=Ui.Prompt("B","Back"); var holder=new Grid(); holder.Children.Add(prompt); window.Content=holder; Pump();
            var badge=(Grid)prompt.Children[0];
            InputHints.Set(InputKind.Controller); Assert(badge.Width==32,"Controller Back uses a round badge");
            InputHints.Set(InputKind.Keyboard); Assert(badge.Width>32,"Existing Back badge expands for Escape without reopening screen");
            Assert((string)app.Resources["SelenePromptB"]=="Esc","Theme resources update to keyboard hints");
            InputHints.Set(InputKind.Mouse); Assert((string)app.Resources["SelenePromptA"]=="Click","Theme resources update to mouse hints");
            InputHints.Set(InputKind.Controller); Assert((string)app.Resources["SelenePromptB"]=="B","Controller input restores B");
            var settings=new SettingsScreen(false,"Look","language"); window.Content=settings.Root; Pump(); settings.Shown(); Pump();
            settings.Handle("A"); var picker=(ChoiceList)Field(settings,"picker"); picker.Handle("DPadDown"); picker.Handle("A"); Pump();
            Assert(Features.Value("language")=="en","Language choice saves immediately");
            Assert(Field(settings,"ask") is Dialog,"Changed language opens restart modal");
            Wait(250); Render(settings.Root,Path.Combine(args[0],"language-restart.png"));
            settings.Handle("B"); Assert(Field(settings,"ask")==null,"Later dismisses restart modal");
            Assert(Features.Value("language")=="en","Later keeps the chosen language saved");
            Features.Set("downloadprogress", false); // Explicitly exercise the retained individual/legacy flow below.
            var gamesAsk = new GamesAskScreen(); window.Content = gamesAsk.Root; Pump(); gamesAsk.Shown(); Wait(300); Render(gamesAsk.Root, Path.Combine(args[0], "games-question.png"));
            Assert(((FrameworkElement)Field((Dialog)Field(gamesAsk, "ask"), "panel")).ActualHeight < 560, "The games question is a short card, not a scrolling list");
            GamesSetup.Decline(); var gamesRow = new SettingsScreen(false, "Games", "gamesget"); window.Content = gamesRow.Root; Pump(); gamesRow.Shown(); Wait(350); Render(gamesRow.Root, Path.Combine(args[0], "games-row-dot.png"));
            Features.Remember("gamesask-done", "");
            var downloads = new SettingsScreen(false, "Spotlight", "screenshotsdownload");
            window.Content = downloads.Root; Pump(); downloads.Shown(); Pump();
            downloads.Handle("A");
            Assert(Field(downloads, "ask") is Dialog && !Features.On("screenshotsdownload") && !DownloadConsent.Allowed("screenshotsdownload"), "Download toggle asks before enabling or authorizing network work");
            Wait(280); Render(downloads.Root, Path.Combine(args[0], "download-consent.png"));
            var consent = (Dialog)Field(downloads, "ask");
            Assert(((FrameworkElement)Field(consent, "panel")).ActualHeight <= 1000, "Consent destinations stay inside the screen");
            downloads.Handle("A");
            Assert(!Features.On("screenshotsdownload") && Field(downloads, "ask") == null, "Permission question defaults to Not now");
            downloads.Handle("A"); downloads.Handle("DPadRight"); downloads.Handle("A");
            Assert(DownloadConsent.Allowed("screenshotsdownload"), "Explicit Allow grants screenshot permission");
            downloads.Handle("A");
            Assert(!DownloadConsent.Allowed("screenshotsdownload"), "Switching off revokes screenshot permission");
            downloads.Hidden(); window.Content = settings.Root; Pump();
            settings.Handle("A"); picker=(ChoiceList)Field(settings,"picker"); picker.Handle("A"); Assert(Field(settings,"ask")==null,"Selecting the same language does not ask for another restart"); settings.Hidden();
            language=new LanguageScreen(); window.Content=language.Root; Pump(); language.Handle("DPadDown"); language.Handle("A");
            Assert(LanguageSetup.Complete,"Completing language step records progress before restart");
            Assert(Field(language,"ask") is Dialog,"First-time language change also offers restart");
            var glide=typeof(HomeNav).GetMethod("Glide",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(TranslateTransform),typeof(double),typeof(bool),typeof(double)},null);
            var firstRow=new TranslateTransform();var secondRow=new TranslateTransform();
            glide.Invoke(null,new object[]{firstRow,256.0,false,20.0});glide.Invoke(null,new object[]{secondRow,256.0,false,0.0});
            Assert(firstRow.X==20,"Switching between Collection and Spotlight settles the previous row");
            // Repeated hide/restore must preserve the visual tree and publish ambient state exactly once per transition.
            Features.Set("presence", true);
            ScreenPresence.Watch(window);
            int presenceChanges = 0;
            Action<bool> presenceObserver = value => presenceChanges++;
            ScreenPresence.Changed += presenceObserver;
            var retainedContent = window.Content;
            for (int cycle = 0; cycle < 4; cycle++)
            {
                window.Hide(); Pump(); ScreenPresence.Evaluate();
                Assert(!ScreenPresence.InView && Equals(app.Resources["SeleneAmbientMotionEnabled"], false), "Hidden window stops ambient work");
                int beforeRepeat = presenceChanges; ScreenPresence.Evaluate();
                Assert(presenceChanges == beforeRepeat, "Unchanged visibility does not notify subscribers again");
                window.Show(); Pump(); ScreenPresence.Evaluate();
                Assert(ScreenPresence.InView && ReferenceEquals(window.Content, retainedContent), "Restore preserves the same content tree");
            }
            window.Hide(); ScreenPresence.Evaluate(); Features.Set("presence", false); ScreenPresence.Evaluate();
            Assert(ScreenPresence.InView, "Turning presence off resumes media even while hidden");
            Features.Set("presence", true); ScreenPresence.Evaluate();
            Assert(!ScreenPresence.InView, "Turning presence back on applies current visibility");
            ScreenPresence.Changed -= presenceObserver; window.Show(); ScreenPresence.Evaluate();
            Ui.Poke(5000); Assert(Ui.RecentlyActive, "Input wakes the active frame window");
            // Byte budget must evict high-DPI portraits as well as many small covers, without changing decode quality.
            var remember = typeof(TileCovers).GetMethod("Remember", BindingFlags.Static | BindingFlags.NonPublic);
            var covers = (System.Collections.IDictionary)typeof(TileCovers).GetField("cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var pixels = new byte[1024 * 1024 * 4];
            var bitmap = BitmapSource.Create(1024, 1024, 96, 96, PixelFormats.Bgra32, null, pixels, 4096); bitmap.Freeze();
            for (int i = 0; i < 30; i++) remember.Invoke(null, new object[] { "cover-" + i, bitmap });
            Assert(covers.Count == 20 && !covers.Contains("cover-0") && covers.Contains("cover-29"), "80 MiB pixel budget evicts least recent covers");
            remember.Invoke(null, new object[] { "cover-29", bitmap });
            Assert(covers.Count == 20, "Replacing a cached cover does not double-count its pixel storage");
            var small = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); small.Freeze();
            for (int i = 0; i < 100; i++) remember.Invoke(null, new object[] { "small-" + i, small });
            Assert(covers.Count == 80, "Entry count also bounds very small covers");
            // Load the production tile template: ambient clocks must stop without undoing selected scale/focus.
            var xml = new System.Xml.XmlDocument(); xml.Load(args[1]);
            var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
            ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
            ns.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var templateXml = (System.Xml.XmlElement)xml.SelectSingleNode("//p:Style[@x:Key='SeleneTileStyle']/p:Setter[@Property='Template']/p:Setter.Value/p:ControlTemplate", ns);
            templateXml.SetAttribute("xmlns:x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(templateXml.OuterXml);
            app.Resources["SeleneAmbientMotionEnabled"] = true;
            var selected = new ListBoxItem { Template = template, IsSelected = true };
            window.Content = selected; Wait(250);
            var ring = (RotateTransform)template.FindName("RingSpin", selected);
            var selectedScale = (ScaleTransform)template.FindName("TileScale", selected);
            Assert(ring.HasAnimatedProperties, "Visible selected tile runs its ambient ring");
            app.Resources["SeleneAmbientMotionEnabled"] = false; Wait(50);
            Assert(!ring.HasAnimatedProperties && selected.IsSelected && selectedScale.ScaleX > 1.29, "Hidden ambient state removes clocks and preserves selected scale");
            app.Resources["SeleneAmbientMotionEnabled"] = true; Wait(50);
            Assert(ring.HasAnimatedProperties, "Restoring ambient state restarts the selected ring");
            selected.Visibility = Visibility.Collapsed; Wait(50);
            Assert(!ring.HasAnimatedProperties, "An invisible selected tile has no ambient clocks");
            Console.WriteLine("Passed "+checks+" UI interaction checks; rendered first-language and restart screens."); return 0;
        }
        finally { window.Close(); if(Directory.Exists(scratch))Directory.Delete(scratch,true); }
    }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(()=>{})); }
    static void Wait(int ms) { var frame=new DispatcherFrame(); var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(ms) }; timer.Tick+=(s,e)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame); }
    static void Render(FrameworkElement root,string file)
    {
        root.Measure(new Size(1920,1080));root.Arrange(new Rect(0,0,1920,1080));root.UpdateLayout();
        var bmp=new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32);bmp.Render(root);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using(var stream=File.Create(file))png.Save(stream);
    }
}
