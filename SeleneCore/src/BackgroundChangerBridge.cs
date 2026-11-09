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
    // Host an add-on's background (#185): Background Changer's public theme control, or a place for ImageRotater. No dependency
    // on their DLLs, media folders or private database formats. ImageRotater draws only into a visible image part named
    // PART_ImageBackground (Playnite's own one shows in Collection); Spotlight and the game page paint their own art, so there
    // they carry an empty part of that name for it, above Selene's art: when it has nothing for a game, Selene's art shows.
    public sealed class BackgroundChangerBridge : ContentControl
    {
        private static readonly List<WeakReference> surfaces = new List<WeakReference>();
        private readonly Func<bool> active;
        private readonly bool collection;
        private string source;   // which add-on the hosted control came from
        private Image rotaterPart;   // ImageRotater's place
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

        private static Plugin RotaterPlugin()
        {
            return HomeNav.Api != null ? HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.GetType().FullName == "ImageRotater.ImageRotater") : null;
        }
        public static bool RotaterAvailable() { return RotaterPlugin() != null; }

        // the add-on this surface uses now: Background Changer when switched on, else ImageRotater (not in Collection)
        private string Wanted()
        {
            if (Features.On("backgroundchanger") && Plugin() != null) return "bc";
            if (!collection && Features.On("imagerotater") && RotaterPlugin() != null) return "ir";
            return null;
        }

        private BackgroundChangerBridge(string path, Func<bool> active, bool collection = false)
        {
            this.active = active;
            this.collection = collection;
            IsHitTestVisible = false; Focusable = false;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            ClipToBounds = true;
            SetBinding(GameIdProperty, new Binding(path));
            Loaded += (s, e) => { if (!watching) { ScreenPresence.Changed += Presence; watching = true; } Refresh(); };
            Unloaded += (s, e) => { if (watching) ScreenPresence.Changed -= Presence; watching = false; ReleaseControl(); ReleaseRotater(); };
            IsVisibleChanged += (s, e) => Refresh();
            surfaces.Add(new WeakReference(this));
        }

        public static void AttachMain(Window window)
        {
            Attach(HomeNav.FindByName(window, "SeleneBackgroundChangerHome") as Grid, "SelectedGame.Id", () => !SeleneShell.CollectionActive && !DetailsStage.PageOpen && !Equals(Application.Current.Resources["SeleneArtOpacity"], 0.0));
            Attach(HomeNav.FindByName(window, "SeleneBackgroundChangerCollection") as Grid, "SelectedGame.Id", () => SeleneShell.CollectionActive && !DetailsStage.PageOpen, true);
            var layer = HomeNav.FindByName(window, "AcceuilSettings");
            if (layer != null) layer.IsVisibleChanged += (s, e) => RefreshAll();
        }

        public static void AttachDetails(DependencyObject root)
        {
            Attach(HomeNav.FindByName(root, "SeleneBackgroundChangerDetails") as Grid, "Id", () => DetailsStage.PageOpen);
            RefreshAll();
        }

        private static void Attach(Grid host, string path, Func<bool> active, bool collection = false)
        {
            if (host == null || host.Children.OfType<BackgroundChangerBridge>().Any()) return;
            host.Children.Add(new BackgroundChangerBridge(path, active, collection));
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
            var want = IsLoaded && IsVisible && active() && ScreenPresence.InView && !LoginScene.IsActive ? Wanted() : null;
            if (want != "ir") ReleaseRotater();
            if (want == null || (media != null && want != source)) { ReleaseControl(); if (want == null) return; }
            if (want == "ir") { ShowRotater(); return; }
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
                    source = want;
                    if (media == null) return;
                    context = media.GetType().GetProperty("GameContext");
                    if (context == null || !context.CanWrite || !context.PropertyType.IsAssignableFrom(typeof(Game))) { ReleaseControl(); return; }
                    Content = media;
                    Opacity = 0;
                    BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
                context.SetValue(media, game, null);
                shown = game.Id; failed = false;
                HomeNav.Log(Name(source) + ": showing " + game.Name);
            }
            catch (Exception ex)
            {
                ReleaseControl();
                if (!failed) HomeNav.Log(Name(want) + ": using normal background (" + (ex.InnerException ?? ex).Message + ")");
                failed = true;
            }
        }

        private void ShowRotater()
        {
            var host = Parent as Grid;
            if (host == null || (rotaterPart != null && ReferenceEquals(rotaterPart.Parent, host))) return;
            rotaterPart = new Image { Name = "PART_ImageBackground", IsHitTestVisible = false, Stretch = System.Windows.Media.Stretch.UniformToFill };
            rotaterPart.SetResourceReference(Image.StretchProperty, "SeleneBackgroundStretch");
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(rotaterPart, System.Windows.Media.BitmapScalingMode.HighQuality);
            Grid.SetRowSpan(rotaterPart, Math.Max(1, host.RowDefinitions.Count));
            Grid.SetColumnSpan(rotaterPart, Math.Max(1, host.ColumnDefinitions.Count));
            host.Children.Insert(host.Children.IndexOf(this), rotaterPart);
            HomeNav.Log("ImageRotater: background place ready on " + host.Name);
        }

        private void ReleaseRotater()
        {
            var part = rotaterPart;
            rotaterPart = null;
            var host = part != null ? part.Parent as Panel : null;
            if (host == null) return;
            host.Children.Remove(part);   // its renderer, put beside it, is released by ImageRotater once the part is gone
            for (int i = host.Children.Count - 1; i >= 0; i--)
                if (host.Children[i].GetType().FullName == "ImageRotater.Controls.BackgroundImageControl") host.Children.RemoveAt(i);
            HomeNav.Log("ImageRotater: background place removed from " + host.Name);
        }

        private static string Name(string which) { return which == "ir" ? "ImageRotater" : "Background Changer"; }

        private void ReleaseControl()
        {
            var old = media;
            var from = source;
            media = null; context = null; shown = Guid.Empty; source = null;
            if (old == null) return;
            Content = null; // Unloaded lets the add-on stop its rotation timers and media playback.
            var disposable = old as IDisposable;
            if (disposable != null)
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { HomeNav.Log(Name(from) + ": disposal failed (" + ex.Message + ")"); }
            }
            HomeNav.Log(Name(from) + ": surface unloaded");
        }
    }
}
