using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace Selene
{
    // Host the add-on's public theme control. No dependency on its DLL, media folders or private database format.
    public sealed class BackgroundChangerBridge : ContentControl
    {
        private static readonly List<WeakReference> surfaces = new List<WeakReference>();
        private readonly Func<bool> active;
        private Control media;
        private PropertyInfo context;
        private Guid shown;
        private bool watching, failed;
        public static readonly DependencyProperty GameIdProperty = DependencyProperty.Register("GameId", typeof(object), typeof(BackgroundChangerBridge), new PropertyMetadata(null, (d, e) => ((BackgroundChangerBridge)d).Refresh()));

        private static Plugin Plugin()
        {
            return HomeNav.Api != null ? HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.GetType().FullName == "BackgroundChanger.BackgroundChanger") : null;
        }
        public static bool Available() { return Plugin() != null; }

        private BackgroundChangerBridge(string path, Func<bool> active)
        {
            this.active = active;
            IsHitTestVisible = false; Focusable = false;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            ClipToBounds = true;
            SetBinding(GameIdProperty, new Binding(path));
            Loaded += (s, e) => { if (!watching) { ScreenPresence.Changed += Presence; watching = true; } Refresh(); };
            Unloaded += (s, e) => { if (watching) ScreenPresence.Changed -= Presence; watching = false; ReleaseControl(); };
            IsVisibleChanged += (s, e) => Refresh();
            surfaces.Add(new WeakReference(this));
        }

        public static void AttachMain(Window window)
        {
            Attach(HomeNav.FindByName(window, "SeleneBackgroundChangerHome") as Grid, "SelectedGame.Id", () => !SeleneShell.CollectionActive && !DetailsStage.PageOpen && !Equals(Application.Current.Resources["SeleneArtOpacity"], 0.0));
            Attach(HomeNav.FindByName(window, "SeleneBackgroundChangerCollection") as Grid, "SelectedGame.Id", () => SeleneShell.CollectionActive && !DetailsStage.PageOpen);
            var layer = HomeNav.FindByName(window, "AcceuilSettings");
            if (layer != null) layer.IsVisibleChanged += (s, e) => RefreshAll();
        }

        public static void AttachDetails(DependencyObject root)
        {
            Attach(HomeNav.FindByName(root, "SeleneBackgroundChangerDetails") as Grid, "Id", () => DetailsStage.PageOpen);
            RefreshAll();
        }

        private static void Attach(Grid host, string path, Func<bool> active)
        {
            if (host == null || host.Children.OfType<BackgroundChangerBridge>().Any()) return;
            host.Children.Add(new BackgroundChangerBridge(path, active));
        }

        public static void RefreshAll()
        {
            for (int i = surfaces.Count - 1; i >= 0; i--)
            {
                var surface = surfaces[i].Target as BackgroundChangerBridge;
                if (surface == null) surfaces.RemoveAt(i);
                else surface.Refresh();
            }
        }

        private void Presence(bool visible) { Refresh(); }

        private void Refresh()
        {
            if (!IsLoaded || !IsVisible || !active() || !ScreenPresence.InView || LoginScene.IsActive || !Features.On("backgroundchanger")) { ReleaseControl(); return; }
            var id = GetValue(GameIdProperty);
            var game = id is Guid && HomeNav.Api != null ? HomeNav.Api.Database.Games.Get((Guid)id) : null;
            if (game == null) { ReleaseControl(); return; }
            if (shown == game.Id && media != null) return;
            try
            {
                if (media == null)
                {
                    var plugin = Plugin();
                    if (plugin == null) return;
                    media = plugin.GetGameViewControl(new GetGameViewControlArgs { Name = "PluginBackgroundImage" });
                    if (media == null) return;
                    context = media.GetType().GetProperty("GameContext");
                    if (context == null || !context.CanWrite || !context.PropertyType.IsAssignableFrom(typeof(Game))) { ReleaseControl(); return; }
                    Content = media;
                    Opacity = 0;
                    BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
                context.SetValue(media, game, null);
                shown = game.Id; failed = false;
                HomeNav.Log("Background Changer: showing " + game.Name);
            }
            catch (Exception ex)
            {
                ReleaseControl();
                if (!failed) HomeNav.Log("Background Changer: using normal background (" + (ex.InnerException ?? ex).Message + ")");
                failed = true;
            }
        }

        private void ReleaseControl()
        {
            var old = media;
            media = null; context = null; shown = Guid.Empty;
            if (old == null) return;
            Content = null; // Unloaded lets the add-on stop its rotation timers and media playback.
            var disposable = old as IDisposable;
            if (disposable != null)
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { HomeNav.Log("Background Changer: disposal failed (" + ex.Message + ")"); }
            }
            HomeNav.Log("Background Changer: surface unloaded");
        }
    }
}
