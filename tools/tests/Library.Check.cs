using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Selene;
class LibraryCheck
{
    public class MusicProbe { public double Level = 1; public void SetVolumeMultiplier(double value) { Level = value; } }
    public class DownloadTriggerProbe { public bool AutoDownloadOnLibraryUpdate { get; set; } public bool AutoDownloadOnGameInstall { get; set; } public bool AutoPlayOnFirstLaunchDesktop { get; set; } }
    static int checks;
    static void Assert(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
    static T Child<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var c=VisualTreeHelper.GetChild(parent,i);var found=c as T ?? Child<T>(c);if(found!=null)return found;}return null;
    }
    static void Wait(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(s,e)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    static string ReadShared(string path) { using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var reader = new StreamReader(input)) return reader.ReadToEnd(); }
    [STAThread]static int Main(string[] args)
    {
        string runtime=args[0];
        var temp=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"temp");Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP",temp);Environment.SetEnvironmentVariable("TMP",temp);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var name=new AssemblyName(e.Name).Name;foreach(var suffix in new[]{".dll",".exe"}){var path=Path.Combine(runtime,name+suffix);if(File.Exists(path))return Assembly.LoadFrom(path);}return null;};
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Features.Init(new SeleneSettings(),()=>{});Features.Set("sounds",false);Features.Set("collectionglide",false);
        foreach (var category in new[] { "artdownload", "songsetup", "screenshotsdownload", "metadatacomplete", "trailerdownload" })
            Assert(!DownloadConsent.Allowed(category), "Fresh installations do not consent to " + category);
        var legacySettings = new SeleneSettings { TurnedOn = new System.Collections.Generic.List<string> { "artdownload", "songsetup" },
            Values = new System.Collections.Generic.Dictionary<string, string> { { "trailerdownload", "all" }, { "language", "en_US" } } };
        Features.Init(legacySettings, () => {});
        Assert(!Features.On("artdownload") && !Features.On("songsetup") && Features.Value("trailerdownload") == "off", "Upgrade pauses inherited or old explicit download settings for fresh consent");
        Assert(legacySettings.Values["language"] == "en_US", "Download migration preserves unrelated preferences");
        Features.Set("artdownload", true);
        Assert(!DownloadConsent.Allowed("artdownload"), "Enabling a switch alone cannot bypass destination/scope confirmation");
        DownloadConsent.Grant("artdownload");
        Assert(DownloadConsent.Allowed("artdownload"), "Explicit category consent enables only that category");
        Features.Init(legacySettings, () => {});
        Assert(DownloadConsent.Allowed("artdownload"), "Confirmed consent survives an ordinary settings reload");
        Features.SetValue("trailerdownload", "browse"); DownloadConsent.Grant("trailerdownload");
        Assert(DownloadConsent.Allowed("trailerdownload"), "Selected-game scope can be explicitly consented");
        Features.SetValue("trailerdownload", "all");
        Assert(!DownloadConsent.Allowed("trailerdownload"), "Browse consent never authorizes a whole-library download");
        DownloadConsent.Revoke("artdownload");
        Assert(!DownloadConsent.Allowed("artdownload"), "Revocation immediately blocks further requests");
        Features.Init(new SeleneSettings(), () => {}); Features.Set("sounds", false); Features.Set("collectionglide", false);
        var cachedFiles = typeof(GameMedia).GetMethod("CachedFiles", BindingFlags.Static | BindingFlags.NonPublic);
        var uncachedId = Guid.NewGuid();
        var noFiles = (System.Collections.Generic.List<string>)cachedFiles.Invoke(null, new object[] { uncachedId, "No network test", "", "", true });
        Assert(noFiles.Count == 0, "Unconsented screenshot request returns without a network lookup");
        var screenshotDirectory = Path.Combine(SelenePaths.Media("Screenshots-v2"), uncachedId.ToString());
        Assert(!Directory.Exists(screenshotDirectory), "Opt-out creates no false no-screenshots marker");
        Directory.CreateDirectory(screenshotDirectory); File.WriteAllText(Path.Combine(screenshotDirectory, "00.jpg"), "cached fixture");
        var existingFiles = (System.Collections.Generic.List<string>)cachedFiles.Invoke(null, new object[] { uncachedId, "No network test", "", "", true });
        Assert(existingFiles.Count == 1 && File.Exists(existingFiles[0]), "Opt-out preserves and serves existing downloaded files");
        var triggers = new DownloadTriggerProbe { AutoDownloadOnLibraryUpdate = true, AutoDownloadOnGameInstall = true, AutoPlayOnFirstLaunchDesktop = true };
        var disableTriggers = typeof(SongSetup).GetMethod("DisableAutomaticTriggers", BindingFlags.Static | BindingFlags.NonPublic);
        Assert((bool)disableTriggers.Invoke(null, new object[] { triggers }) && !triggers.AutoDownloadOnLibraryUpdate && !triggers.AutoDownloadOnGameInstall, "Selene-managed music setup disables automatic download triggers");
        Assert(triggers.AutoPlayOnFirstLaunchDesktop, "Stopping automatic downloads preserves playback preferences");
        Assert(!PerformanceRecorder.Recording, "Performance recording is off by default");
        PerformanceRecorder.Mark("ignored", "not recording");
        PerformanceRecorder.SetRecording(true);
        var performancePath = PerformanceRecorder.LastPath;
        PerformanceRecorder.Mark("test_marker", "navigation test");
        Wait(2300);
        System.Threading.Thread.Sleep(420); // deliberate UI stall: the background recorder must survive it
        Wait(350);
        PerformanceRecorder.Stop(true);
        for (int attempt = 0; attempt < 30 && (!File.Exists(performancePath) || !ReadShared(performancePath).Contains("session_end")); attempt++) Wait(100);
        Assert(!PerformanceRecorder.Recording && File.Exists(performancePath), "Recorder stops and creates its local log");
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
        bool measured = false, marked = false, delayed = false, ended = false;
        foreach (var line in ReadShared(performancePath).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(line);
            var kind = Convert.ToString(entry["kind"]);
            if (kind == "sample")
            {
                measured = entry.ContainsKey("workingSetMB") && entry.ContainsKey("privateMB") && entry.ContainsKey("cpuPercent") && entry.ContainsKey("gpuBusiestEnginePercent");
                if (Convert.ToDouble(entry["maxUiDelayMs"]) > 100) delayed = true;
            }
            if (kind == "test_marker") marked = true;
            if (kind == "session_end") ended = true;
        }
        Assert(measured && marked && ended, "Diagnostic log contains parseable measurements, correlated events, and a clean ending");
        Assert(delayed, "The recorder detects a deliberately stalled UI");
        PerformanceRecorder.SetRecording(true);
        Assert(PerformanceRecorder.LastPath != performancePath, "Each recording uses its own file");
        PerformanceRecorder.Stop(true);
        Assert(!Features.On("lightweight"), "Reduced-memory mode is off by default");
        bool remoteLite = HomeNav.Lite;
        Features.Set("lightweight", true);
        Assert(HomeNav.Lite == remoteLite, "Memory setting preserves visual/animation mode independently of remote-session detection");
        var remember = typeof(GameMedia).GetMethod("Remember", BindingFlags.Static | BindingFlags.NonPublic);
        for (int i = 0; i < 12; i++) remember.Invoke(null, new object[] { Guid.NewGuid(), new System.Collections.Generic.List<ShotItem>() });
        var imageCache = (System.Collections.IDictionary)typeof(GameMedia).GetField("cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        Assert(imageCache.Count == 8, "Reduced-memory mode bounds screenshot memory to eight games");
        GameMedia.ApplyPerformanceMode();
        Assert(imageCache.Count == 0, "Changing performance mode releases retained screenshot cache references");
        Features.Set("lightweight", false);
        var playerField = typeof(HomeNav).GetField("upsPlayer", BindingFlags.Static | BindingFlags.NonPublic);
        var probe = new MusicProbe();
        playerField.SetValue(null, probe);
        foreach (var backgroundMusic in new[] { false, true })
        {
            Features.Set("music", backgroundMusic);
            HomeNav.HoldMusicForLaunch(true); Wait(120);
            Assert(probe.Level > 0 && probe.Level < 1, "Loading music fades rather than cuts, background music=" + backgroundMusic);
            Wait(300);
            Assert(probe.Level == 0, "Loading screen reaches silence, background music=" + backgroundMusic);
            HomeNav.HoldMusicForLaunch(false); Wait(320);
            Assert(probe.Level == 1, "Leaving loading restores the volume multiplier, background music=" + backgroundMusic);
        }
        HomeNav.HoldMusicForLaunch(true); Wait(80);
        HomeNav.HoldMusicForLaunch(false); Wait(400);
        Assert(probe.Level == 1, "Cancelling launch during fade cannot leave music muted");
        HomeNav.ReleaseMusic(); playerField.SetValue(null, null);
        var loadingHost = new Grid { Name = "HubBottomCards" };
        var loadingWindow = new Window { Content = loadingHost, Width = 1800, Height = 280, Left = -32000, Top = -32000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        app.MainWindow = loadingWindow;
        loadingWindow.Show();
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var showLoading = typeof(GameMedia).GetMethod("ShowLoading", flags);
        var hideLoading = typeof(GameMedia).GetMethod("HideLoading", flags);
        Features.Set("screenshotskeleton", false);
        showLoading.Invoke(null, null);
        Assert(loadingHost.Children.Count == 0, "Loading cards respect their switch");
        Features.Set("screenshotskeleton", true);
        showLoading.Invoke(null, null); Wait(220);
        Assert(loadingHost.Children.Count == 1, "Loading cards attach once");
        var skeleton = (Grid)loadingHost.Children[0];
        var cards = (StackPanel)skeleton.Children[0];
        Assert(cards.Children.Count == 4 && !skeleton.IsHitTestVisible, "Four placeholders cannot intercept navigation");
        var glint = ((Grid)cards.Children[0]).Children[1] as System.Windows.Shapes.Rectangle;
        Assert(glint != null && glint.RenderTransform.HasAnimatedProperties, "Placeholders shimmer while loading");
        hideLoading.Invoke(null, new object[] { true }); Wait(260);
        Assert(skeleton.Opacity < 0.01 && !glint.RenderTransform.HasAnimatedProperties, "Shimmer stops and placeholders fade after loading");
        showLoading.Invoke(null, null); Wait(220);
        Assert(skeleton.Opacity > 0.99 && glint.RenderTransform.HasAnimatedProperties && loadingHost.Children.Count == 1, "A new selection restarts loading without duplicate cards");
        hideLoading.Invoke(null, new object[] { false });
        loadingWindow.Close();
        var panelType=Assembly.LoadFrom(Path.Combine(runtime,"Playnite.FullscreenApp.exe")).GetType("Playnite.FullscreenApp.Controls.FullscreenTilePanel",true);
        var factory=new FrameworkElementFactory(panelType);
        foreach(var pair in new[]{new object[]{"ColumnsProperty",7},new object[]{"RowsProperty",1},new object[]{"UseHorizontalLayoutProperty",true},new object[]{"SmoothScrollEnabledProperty",true}})
            factory.SetValue((DependencyProperty)panelType.GetField((string)pair[0]).GetValue(null),pair[1]);
        var ratioType=panelType.GetProperty("ItemAspectRatio").PropertyType;
        var ratioConstructor=ratioType.GetConstructors().First(c=>c.GetParameters().Length==2);
        var parameters=ratioConstructor.GetParameters();var ratio=ratioConstructor.Invoke(new[]{Convert.ChangeType(600,parameters[0].ParameterType),Convert.ChangeType(900,parameters[1].ParameterType)});
        factory.SetValue((DependencyProperty)panelType.GetField("ItemAspectRatioProperty").GetValue(null),ratio);
        var list=new ListBox{Name="PART_ListGameItems",Width=1920,Height=400,ItemsPanel=new ItemsPanelTemplate(factory)};
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Hidden);ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Disabled);ScrollViewer.SetCanContentScroll(list,true);
        using(var input=File.OpenRead(args[1])){var resources=(ResourceDictionary)XamlReader.Load(input);list.ItemContainerStyle=(Style)resources["ListGameItemStyle"];}
        for(int i=0;i<367;i++)list.Items.Add("Game "+i);
        var library=new Grid{Name="SeleneLibrary"};library.Children.Add(list);library.Children.Add(new Grid{Name="SeleneLibraryTrailerHost"});
        var window=new Window{Content=library,Width=1920,Height=450,Left=-32000,Top=-32000,Opacity=0,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None};
        try
        {
            window.Show();Wait(200);LibraryStage.Attach(window);Wait(100);
            var panel=Child<VirtualizingPanel>(list);Assert(panel!=null && panel.GetType()==panelType,"Real Playnite fullscreen panel was instantiated");
            var smooth=(DependencyProperty)panelType.GetField("SmoothScrollEnabledProperty").GetValue(null);
            Assert((bool)panel.GetValue(smooth),"Native scrolling is preserved when Selene glide is off");
            var scroll=(IScrollInfo)panel;
            int layouts=0;panel.LayoutUpdated+=(s,e)=>layouts++;
            scroll.SetHorizontalOffset(0);Wait(150);layouts=0;scroll.LineRight();Wait(240);int before=layouts;
            Features.Set("collectionglide",true);LibraryStage.ApplyNavigationSettings();
            Assert(!(bool)panel.GetValue(smooth),"Selene spring replaces the native per-frame layout animation");
            scroll.SetHorizontalOffset(0);Wait(150);layouts=0;scroll.LineRight();Wait(240);int after=layouts;
            Assert(after<before,"One logical move uses fewer layout passes than native animated scrolling");
            Assert(scroll.HorizontalOffset>0,"The logical move still advances the row");
            var item=list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex>=0?list.SelectedIndex:1) as DependencyObject;
            Assert(item!=null && Child<Grid>(item).CacheMode is BitmapCache,"Real cover template caches the focus face and ring");
            Features.Set("collectionglide",false);LibraryStage.ApplyNavigationSettings();Assert((bool)panel.GetValue(smooth),"Turning off Selene glide restores the native setting");
            Console.WriteLine("Passed "+checks+" real Playnite panel checks. Layout passes for one move: "+before+" native animated, "+after+" with Selene logical step.");return 0;
        }
        finally{window.Close();}
    }
}
