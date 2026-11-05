using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Selene;

// A public-API-compatible add-on fixture; no installed Background Changer or user media is needed.
namespace BackgroundChanger
{
    public class BackgroundChanger : GenericPlugin
    {
        public override Guid Id { get { return Guid.Parse("3afdd02b-db6c-4b60-8faa-2971d6dfad2a"); } }
        public int Created;
        public bool Throw;
        public TestMedia Last;
        public BackgroundChanger(IPlayniteAPI api) : base(api) { }
        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (Throw) throw new InvalidOperationException("Fixture unavailable");
            if (args.Name != "PluginBackgroundImage") throw new Exception("Wrong custom element requested");
            Created++; Last = new TestMedia(); return Last;
        }
    }
    public class TestMedia : Control, IDisposable
    {
        public Game GameContext { get; set; }
        public bool Disposed, Running;
        public TestMedia() { Loaded += (s, e) => Running = true; Unloaded += (s, e) => Running = false; }
        public void Dispose() { Disposed = true; Running = false; }
    }
}

class BackgroundChangerCheck
{
    class Stub : RealProxy
    {
        readonly Func<IMethodCallMessage, object> call;
        public Stub(Type type, Func<IMethodCallMessage, object> call) : base(type) { this.call = call; }
        public override IMessage Invoke(IMessage message)
        {
            var input = (IMethodCallMessage)message;
            try { return new ReturnMessage(call(input), null, 0, input.LogicalCallContext, input); }
            catch (Exception ex) { return new ReturnMessage(ex, input); }
        }
    }
    public class Model { public Game SelectedGame { get; set; } }
    static int count;
    static void Assert(bool ok, string text) { if (!ok) throw new Exception(text); count++; }
    static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    static Type Result(IMethodCallMessage m) { return ((MethodInfo)m.MethodBase).ReturnType; }
    [STAThread] static int Main(string[] args)
    {
        Directory.CreateDirectory(Path.Combine(args[0], "temp"));
        Environment.SetEnvironmentVariable("TEMP", Path.Combine(args[0], "temp"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var presenceEvent = typeof(ScreenPresence).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic);
        var before = presenceEvent.GetValue(null) as Delegate;
        int subscriptions = before == null ? 0 : before.GetInvocationList().Length;
        var game = new Game("Fixture") { Id = Guid.NewGuid() };
        var other = new Game("Other") { Id = Guid.NewGuid() };
        var plugins = new List<Plugin>();
        var api = (IPlayniteAPI)new Stub(typeof(IPlayniteAPI), m =>
        {
            if (m.MethodName == "get_Addons") return new Stub(Result(m), a => a.MethodName == "get_Plugins" ? plugins : null).GetTransparentProxy();
            if (m.MethodName == "get_Database") return new Stub(Result(m), d => d.MethodName == "get_Games" ? new Stub(Result(d), g => g.MethodName == "Get" ? ((Guid)g.Args[0] == game.Id ? game : other) : null).GetTransparentProxy() : null).GetTransparentProxy();
            return null;
        }).GetTransparentProxy();
        HomeNav.Api = api;
        Features.Init(new SeleneSettings(), () => { });
        var plugin = new BackgroundChanger.BackgroundChanger(api); plugins.Add(plugin);
        var window = new Window { Width = 640, Height = 360, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        var root = new Grid(); var host = new Grid { Name = "SeleneBackgroundChangerHome" }; var fallback = new Border();
        root.Children.Add(fallback); root.Children.Add(host); window.Content = root; window.DataContext = new Model { SelectedGame = game }; app.MainWindow = window;
        try
        {
            window.Show(); Pump(); BackgroundChangerBridge.AttachMain(window); Pump();
            Assert(plugin.Created == 0, "Integration must default off");
            Assert(BackgroundChangerBridge.Available(), "Installed public API fixture is detected");
            Features.Set("backgroundchanger", true); BackgroundChangerBridge.RefreshAll(); Pump();
            Assert(plugin.Created == 1 && plugin.Last.GameContext == game, "Selected game reaches the add-on control");
            Assert(plugin.Last.Running, "Mounted control is loaded");
            var first = plugin.Last;
            BackgroundChangerBridge.RefreshAll(); Pump(); Assert(plugin.Created == 1, "Refresh keeps the existing selected-game control");
            window.DataContext = new Model { SelectedGame = other }; Pump();
            Assert(plugin.Created == 1 && first.GameContext == other, "Game switch updates context without another player");
            Features.Set("backgroundchanger", false); BackgroundChangerBridge.RefreshAll(); Pump();
            Assert(first.Disposed && !first.Running && !first.IsLoaded, "Off unloads and disposes the player");
            Assert(root.Children.Contains(fallback), "Off preserves the theme fallback");
            Features.Set("backgroundchanger", true); BackgroundChangerBridge.RefreshAll(); Pump();
            var resumed = plugin.Last; Assert(plugin.Created == 2 && resumed.Running, "Turning on again restores the player");
            window.Hide(); Pump(); Assert(resumed.Disposed && !resumed.Running, "Hidden owner stops the player");
            window.Show(); Pump(); var reloaded = plugin.Last;
            Assert(plugin.Created == 3 && reloaded.Running, "Showing the owner recreates the player");
            window.DataContext = new Model(); Pump(); Assert(reloaded.Disposed, "A card without a game unloads the player");
            plugins.Clear(); window.DataContext = new Model { SelectedGame = game }; Pump(); BackgroundChangerBridge.RefreshAll();
            Assert(!BackgroundChangerBridge.Available() && plugin.Created == 3, "Missing add-on falls back even when enabled");
            plugins.Add(plugin); plugin.Throw = true; BackgroundChangerBridge.RefreshAll(); Pump();
            Assert(root.Children.Contains(fallback) && host.IsVisible, "An unavailable custom control preserves fallback without throwing");
            plugin.Throw = false; BackgroundChangerBridge.RefreshAll(); Pump(); var final = plugin.Last;
            window.Close(); Pump(); Assert(final.Disposed && !final.Running, "Closing owner stops and disposes the player");
            var after = presenceEvent.GetValue(null) as Delegate;
            Assert((after == null ? 0 : after.GetInvocationList().Length) == subscriptions, "Closing owner removes presence subscriptions");
            Console.WriteLine("Passed " + count + " Background Changer integration and lifecycle checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window.Close(); app.Shutdown(); }
    }
}
