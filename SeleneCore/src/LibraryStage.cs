using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // The library's background: the selected game's art, and once you rest on a game its trailer takes over the whole
    // background (muted, looping) with the game's song. R3 swaps the song for the trailer's own sound. Moving on fades the
    // trailer away and the default music comes back.
    public static class LibraryStage
    {
        private static ListBox list;
        private static FrameworkElement library;
        private static Grid host;
        private static TextBlock rsHint;
        private static UIElement art, wallpaper, baseLayer;   // faded away while the trailer plays (black behind it)
        private static MediaElement video;
        private static bool videoHasSound, soundOn;
        private static DispatcherTimer arm;
        private static DispatcherTimer relatedWork;

        public static bool Playing { get { return video != null; } }

        public static void Attach(Window w)
        {
            if (list != null || w == null) return;
            list = HomeNav.FindByName(w, "PART_ListGameItems") as ListBox;
            library = HomeNav.FindByName(w, "SeleneLibrary");
            host = HomeNav.FindByName(w, "SeleneLibraryTrailerHost") as Grid;
            rsHint = HomeNav.FindByName(w, "SeleneLibraryRsHint") as TextBlock;
            // Collection's own (game details has a PART_ImageBackground of its own, which was the one found before)
            art = HomeNav.FindByName(library, "PART_ImageBackground") as UIElement;
            // Playnite doesn't keep that name on its image: it's the FadeImage sitting right in Collection's own grid
            var grid = library as Panel;
            if (art == null && grid != null)
                foreach (UIElement child in grid.Children)
                    if (child.GetType().Name == "FadeImage") { art = child; break; }
            wallpaper = HomeNav.FindByName(library, "SeleneLibraryWallpaper") as UIElement;
            baseLayer = HomeNav.FindByName(library, "SeleneLibraryBase") as UIElement;
            HomeNav.Log("library stage: art " + (art != null) + ", wallpaper " + (wallpaper != null));
            if (list == null || library == null || host == null) { HomeNav.Log("library stage: parts not found"); list = null; return; }
            // a held stick at Spotlight's pace: Playnite's controller repeats a held direction every 80 ms, but its list control
            // ignores repeats for 150 ms after each move (ListBoxEx.InputRepeatInterval), so Collection moved at half the pace.
            // Spotlight's row never meets that (Selene takes its keys first). Below 80 ms every repeat gets through
            try
            {
                var interval = list.GetType().GetProperty("InputRepeatInterval", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (interval != null && interval.PropertyType == typeof(int) && Features.On("collectionglide") && (int)interval.GetValue(null, null) > 60)
                {
                    interval.SetValue(null, 60, null);
                    HomeNav.Log("collection: held moves every repeat (input repeat interval 60 ms)");
                }
            }
            catch (Exception ex) { HomeNav.Log("collection: repeat interval: " + ex.Message); }
            arm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4000) };   // the art first, the trailer about 4 s in
            arm.Tick += (s, e) => { arm.Stop(); Play(); };
            list.SelectionChanged += (s, e) =>
            {
                Rearm(true);
                AttachGlide();   // (the list's parts aren't made yet when this is set up, and it may never load again)
                KeepNearLeft();
                QueueRelatedWork();
            };
            list.SizeChanged += (s, e) => measuredPitch = 0;
            library.IsVisibleChanged += (s, e) =>
            {
                if (!(bool)e.NewValue) { Stop(); if (relatedWork != null) relatedWork.Stop(); return; }
                Rearm(false);
                QueueRelatedWork();
                // the row's edges as soon as it shows (not from the first move)
                list.Dispatcher.BeginInvoke(new Action(() => { var sv = RowScroller(); if (sv != null) { HookScroll(sv); ShowFades(sv); } }), DispatcherPriority.Loaded);
            };
            HookArtCrossfade(grid);
            HookGlide();
            HookLogoSize(HomeNav.FindByName(library, "SeleneLibraryLogo") as Image);
        }

        // Logos sized by how much they cover, not by a wide box: a tall one (Monster Hunter Wilds, Path of Exile 2) hit the
        // box's height while still narrow and looked small next to the wide ones. A tall logo grows upward over the art.
        // Switch: Look > Even logo sizes.
        private const double LogoArea = 600 * 200, LogoMaxWidth = 600, LogoMaxHeight = 320, LogoSlot = 250, ThemeMaxHeight = 240;
        private static void HookLogoSize(Image logo)
        {
            if (logo == null) return;
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(logo, (s, e) => SizeLogo(logo));
            SizeLogo(logo);
        }

        internal static Size LogoBox(double pixelWidth, double pixelHeight)
        {
            double r = pixelWidth / pixelHeight;
            double before = Math.Min(LogoMaxWidth, ThemeMaxHeight * r) / r;   // the theme's 600 x 240 box: never smaller than that
            double h = Math.Max(before, Math.Min(LogoMaxHeight, Math.Sqrt(LogoArea / r)));
            double w = Math.Min(LogoMaxWidth, h * r);
            return new Size(w, w / r);
        }

        private static void SizeLogo(Image logo)
        {
            var pic = logo.Source as System.Windows.Media.Imaging.BitmapSource;
            if (!Features.On("logosize") || pic == null || pic.PixelHeight == 0)
            {
                logo.MaxWidth = LogoMaxWidth; logo.MaxHeight = ThemeMaxHeight; logo.Margin = new Thickness(0);
                return;
            }
            var box = LogoBox(pic.PixelWidth, pic.PixelHeight);
            logo.MaxWidth = box.Width;
            logo.MaxHeight = box.Height;
            logo.Margin = new Thickness(0, -Math.Max(0, box.Height - LogoSlot), 0, 0);   // (room above the slot: it grows up, the lines under it stay)
        }

        // Browsing past a cover should not start its downloads/metadata work in the input handler.
        // The latest selection is still fetched as soon as the user rests on it.
        private static void QueueRelatedWork()
        {
            if (relatedWork == null)
            {
                relatedWork = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
                relatedWork.Tick += (s, e) =>
                {
                    relatedWork.Stop();
                    if (library == null || !library.IsVisible) return;
                    var entry = list.SelectedItem;
                    TrailerFetch.Want(entry, null);
                    var property = entry != null ? entry.GetType().GetProperty("Game") : null;
                    var game = property != null ? property.GetValue(entry, null) as Playnite.SDK.Models.Game : entry as Playnite.SDK.Models.Game;
                    try { HltbFetch.ForPage(game); } catch (Exception ex) { HomeNav.Log("hltb: " + ex.Message); }
                };
            }
            relatedWork.Stop();
            if (library != null && library.IsVisible) relatedWork.Start();
        }

        // ---- the row glides: Playnite scrolls it a whole cover at a time (a jump); the covers are shifted back by that much
        // and slide into place on the same spring as Spotlight's row ----
        private static DateTime lastStep = DateTime.MinValue;

        private static void HookGlide()
        {
            list.Loaded += (s, e) => AttachGlide();
            AttachGlide();
        }

        private static bool glideAttached;

        private static void AttachGlide()
        {
            if (glideAttached || library == null || !library.IsVisible) return;
            var sv = RowScroller();
            if (sv == null) { HomeNav.Log("collection glide: no scroll viewer yet (list children " + VisualTreeHelper.GetChildrenCount(list) + ", template " + (list.Template != null) + ")"); return; }
            HookScroll(sv);
        }

        private static ScrollViewer hooked;
        private static ItemsPresenter rowPresenter;
        private static VirtualizingPanel rowPanel;
        private static DependencyProperty nativeSmooth;
        private static BindingBase nativeSmoothBinding;
        private static object nativeSmoothLocal;
        private static double measuredPitch;
        private static bool positionQueued;

        // Native pixel-by-pixel scrolling invalidates the panel's layout on every frame.
        // Let it make one logical step and use Spotlight's render-transform spring for motion.
        public static void ApplyNavigationSettings()
        {
            if (rowPanel == null || nativeSmooth == null) return;
            if (Features.On("collectionglide"))
            {
                double offset = hooked != null ? hooked.HorizontalOffset : 0;
                rowPanel.SetValue(nativeSmooth, false);
                var field = rowPanel.GetType().GetField("HorizontalOffsetProperty", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
                var animatedOffset = field != null ? field.GetValue(null) as DependencyProperty : null;
                if (animatedOffset != null && DependencyPropertyHelper.GetValueSource(rowPanel, animatedOffset).IsAnimated)
                {
                    rowPanel.BeginAnimation(animatedOffset, null);
                    var scroll = rowPanel as IScrollInfo; if (scroll != null) scroll.SetHorizontalOffset(offset);
                }
            }
            else
            {
                HomeNav.CancelGlide(rowPresenter != null ? rowPresenter.RenderTransform as TranslateTransform : null);
                if (nativeSmoothBinding != null) BindingOperations.SetBinding(rowPanel, nativeSmooth, nativeSmoothBinding);
                else if (nativeSmoothLocal == DependencyProperty.UnsetValue) rowPanel.ClearValue(nativeSmooth);
                else rowPanel.SetValue(nativeSmooth, nativeSmoothLocal);
            }
        }
        // the row's edges: a cover cut by the screen dissolves over the last 200 px (an opacity mask on the row, its end stops
        // animated clear or solid), so nothing is a black slab; a side with nothing cut off (the row's start or end) stays solid
        private static GradientStop maskLeft, maskRight, maskLeftIn, maskRightIn;
        private static bool? leftFade, rightFade;

        private const double EdgePx = 200;

        private static void EnsureMask()
        {
            if (!Features.On("collectionglide")) { if (list.OpacityMask != null) list.OpacityMask = null; return; }
            var brush = list.OpacityMask as LinearGradientBrush;
            if (brush == null)
            {
                maskLeft = new GradientStop(Colors.Black, 0);
                maskLeftIn = new GradientStop(Colors.Black, 0.1);
                maskRightIn = new GradientStop(Colors.Black, 0.9);
                maskRight = new GradientStop(Colors.Black, 1);
                brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                brush.GradientStops.Add(maskLeft); brush.GradientStops.Add(maskLeftIn); brush.GradientStops.Add(maskRightIn); brush.GradientStops.Add(maskRight);
                list.OpacityMask = brush;
                leftFade = rightFade = null;
            }
            double w = list.ActualWidth;
            if (w > EdgePx * 3) { maskLeftIn.Offset = EdgePx / w; maskRightIn.Offset = 1 - EdgePx / w; }
        }

        private static void SetEdge(GradientStop stop, bool dissolve)
        {
            var to = dissolve ? Color.FromArgb(0, 0, 0, 0) : Colors.Black;
            if (stop.Color == to && !stop.HasAnimatedProperties) return;
            stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        private static TranslateTransform endShift;

        // scrolled as far as it goes, the last cover (and its ring) still sat past the screen's edge: the whole row nudges left
        // by what's missing, on its own transform (the glide has the presenter's), and back when the row leaves the end
        private static void KeepEndsInside(ScrollViewer sv)
        {
            if (endShift == null || endShift.IsFrozen) { endShift = new TranslateTransform(); list.RenderTransform = endShift; }
            double need = 0;
            if (sv.HorizontalOffset >= sv.ScrollableWidth - 0.5 && list.Items.Count > 0)
            {
                var last = list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) as FrameworkElement;
                if (last != null && last.IsVisible && last.ActualWidth > 0)
                {
                    var presenter = FindChild<ItemsPresenter>(list);
                    var glide = presenter != null ? presenter.RenderTransform as TranslateTransform : null;
                    double right = last.TranslatePoint(new Point(0, 0), list).X - (glide != null ? glide.X : 0) + last.ActualWidth;
                    double edge = Math.Max(24, list.ActualWidth * 0.012);   // room for the ring and the zoom
                    if (right > list.ActualWidth - edge) need = -(right - (list.ActualWidth - edge));
                }
            }
            double now = endShift.X;
            if (Math.Abs(now - need) < 0.5) return;
            endShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(need, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // each edge dissolves only while a cover is cut off on that side
        private static void ShowFades(ScrollViewer sv)
        {
            EnsureMask();
            if (maskLeft == null || !Features.On("collectionglide")) return;
            bool left = sv.HorizontalOffset > 1, right = sv.HorizontalOffset < sv.ScrollableWidth - 1;
            if (leftFade != left) { leftFade = left; SetEdge(maskLeft, left); }
            if (rightFade != right) { rightFade = right; SetEdge(maskRight, right); }
        }

        // the row's scroll viewer watched for its one-cover steps (hooked again if the list got a new one)
        private static void HookScroll(ScrollViewer sv)
        {
            if (sv == null || ReferenceEquals(hooked, sv)) return;
            hooked = sv;
            rowPresenter = FindChild<ItemsPresenter>(list);
            // room after the last cover: selected it grows and gets its ring and glow, and the row's edge cut them off
            if (rowPresenter != null) rowPresenter.Margin = new Thickness(0, 0, 36, 0);
            rowPanel = FindChild<VirtualizingPanel>(list);
            measuredPitch = 0;
            if (rowPanel != null)
            {
                var property = DependencyPropertyDescriptor.FromName("SmoothScrollEnabled", rowPanel.GetType(), rowPanel.GetType());
                nativeSmooth = property != null ? property.DependencyProperty : null;
                if (nativeSmooth != null)
                {
                    nativeSmoothBinding = BindingOperations.GetBindingBase(rowPanel, nativeSmooth);
                    nativeSmoothLocal = rowPanel.ReadLocalValue(nativeSmooth);
                    ApplyNavigationSettings();
                }
            }
            glideAttached = true;
            HomeNav.Log("collection glide: attached (" + sv.GetType().Name + ")");
            int told = 0;
            sv.ScrollChanged += (s, e) =>
            {
                if (Math.Abs(e.HorizontalChange) < 0.001 || !library.IsVisible) return;
                ShowFades(sv);
                if (!Features.On("collectionglide")) return;
                if (!list.IsKeyboardFocusWithin) { if (told++ < 3) HomeNav.Log("collection glide: scrolled " + e.HorizontalChange + " without the focus in the row"); return; }
                var presenter = rowPresenter;
                double pitch = Pitch();
                if (presenter == null || pitch <= 0) return;
                // the scroll counts in pixels here (in covers only with item scrolling): either way, the distance the covers jumped
                bool itemUnits = Math.Abs(sv.ExtentWidth - list.Items.Count) < 2;
                double delta = itemUnits ? e.HorizontalChange * pitch : e.HorizontalChange;
                if (told++ < 3) HomeNav.Log("collection glide: scrolled " + delta.ToString("0") + " px (pitch " + pitch.ToString("0") + (itemUnits ? ", item units" : "") + ")");
                if (Math.Abs(delta) > pitch * 1.6) return;   // (a long jump, a pick from elsewhere: as it is)
                var shift = presenter.RenderTransform as TranslateTransform;
                if (shift == null || shift.IsFrozen) { shift = new TranslateTransform(); presenter.RenderTransform = shift; }
                double since = (DateTime.Now - lastStep).TotalMilliseconds;
                lastStep = DateTime.Now;
                HomeNav.Glide(shift, delta, since < 260);   // (a held stick glides on at one speed)
            };
        }

        // one cover of lookahead: the ring walks along the row, and once only one cover is left beyond it (either side) the row
        // moves instead, so what's next is always in view. Playnite only scrolled once the ring reached the very edge. Its own
        // scroll into view runs first, so this comes right after it
        private static void KeepNearLeft()
        {
            if (positionQueued || !Features.On("collectionglide") || !list.IsKeyboardFocusWithin) return;
            positionQueued = true;
            list.Dispatcher.BeginInvoke(new Action(() =>
            {
                positionQueued = false;
                try
                {
                    if (!library.IsVisible || !Features.On("collectionglide") || !list.IsKeyboardFocusWithin) return;
                    var sv = RowScroller();
                    int idx = list.SelectedIndex;
                    if (sv == null || idx < 0) return;
                    HookScroll(sv);   // (found here when the earlier looks came up empty)
                    bool itemUnits = Math.Abs(sv.ExtentWidth - list.Items.Count) < 2;   // (item scrolling counts in covers)
                    double pitch = itemUnits ? 1 : Pitch();
                    if (pitch <= 0) return;
                    int visible = Math.Max(3, (int)Math.Floor(sv.ViewportWidth / pitch + 0.01));   // whole covers across the row
                    int first = (int)Math.Round(sv.HorizontalOffset / pitch);
                    int lo = first + 1, hi = first + visible - 2;   // where the ring may be with a cover still beyond it
                    int firstWant = idx > hi ? idx - (visible - 2) : idx < lo ? idx - 1 : first;
                    double want = Math.Max(0, Math.Min(firstWant * pitch, sv.ScrollableWidth));
                    if (Math.Abs(sv.HorizontalOffset - want) > 0.01)
                    {
                        sv.ScrollToHorizontalOffset(want);
                        list.UpdateLayout();
                    }
                    KeepEndsInside(sv);
                    ShowFades(sv);
                }
                catch { }
            }), DispatcherPriority.Loaded);
        }

        // the row's scroll viewer: in the list's own parts, else up from a cover (the list's parts weren't found that way once)
        private static ScrollViewer RowScroller()
        {
            if (hooked != null && list.IsAncestorOf(hooked)) return hooked;
            var sv = FindChild<ScrollViewer>(list);
            if (sv != null) return sv;
            var item = list.SelectedIndex >= 0 ? list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as DependencyObject : null;
            for (var d = item; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var found = d as ScrollViewer;
                if (found != null) return found;
            }
            return null;
        }

        // the distance from one cover to the next (the scroll moves in whole covers)
        private static double Pitch()
        {
            var gen = list.ItemContainerGenerator;
            int selected = Math.Max(0, list.SelectedIndex);
            for (int i = Math.Max(0, selected - 2); i < Math.Min(list.Items.Count - 1, selected + 3); i++)
            {
                var a = gen.ContainerFromIndex(i) as FrameworkElement;
                var b = gen.ContainerFromIndex(i + 1) as FrameworkElement;
                if (a != null && b != null && a.IsVisible && b.IsVisible)
                {
                    double pitch = Math.Abs(b.TranslatePoint(new Point(0, 0), a).X);
                    if (pitch > 1) return measuredPitch = pitch;
                }
            }
            return measuredPitch;
        }

        private static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = child as T ?? FindChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        // ---- the art from game to game: a crossfade of Selene's own ----
        // Playnite's image swaps its picture with a blink of a fade. Here the picture as it was stays over it while the next
        // one comes in, then melts away over a good half second.
        private static Image artSnap;
        private static DispatcherTimer snapFallback;
        private static bool snapCovering;
        private static readonly HashSet<Image> watched = new HashSet<Image>();

        private static void HookArtCrossfade(Panel grid)
        {
            var fe = art as FrameworkElement;
            if (fe == null || grid == null) return;
            artSnap = new Image { Stretch = BackgroundFit.Current, IsHitTestVisible = false, Opacity = 0 };
            artSnap.SetResourceReference(Image.StretchProperty, "SeleneBackgroundStretch");
            RenderOptions.SetBitmapScalingMode(artSnap, BitmapScalingMode.Fant);
            Grid.SetRowSpan(artSnap, Grid.GetRowSpan(fe));
            Panel.SetZIndex(artSnap, Panel.GetZIndex(fe));
            int at = grid.Children.IndexOf(fe);
            grid.Children.Insert(at + 1, artSnap);   // right over the art
            snapFallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            snapFallback.Tick += (s, e) => { snapFallback.Stop(); MeltSnapshot(); };   // no new picture came (the same art): the still goes anyway
            list.SelectionChanged += (s, e) => TakeSnapshot();
            fe.Loaded += (s, e) => WatchInnerImages(fe);
            WatchInnerImages(fe);
        }

        // Playnite's image holds its pictures in Image elements inside: one of them getting a new picture means the next art is in
        private static void WatchInnerImages(FrameworkElement fe)
        {
            foreach (var img in Inner(fe).ToList())
            {
                if (!watched.Add(img)) continue;
                DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(img, (s, e) =>
                {
                    if (snapCovering && img.Source != null) fe.Dispatcher.BeginInvoke(new Action(MeltSnapshot), DispatcherPriority.Loaded);
                });
            }
        }

        private static IEnumerable<Image> Inner(DependencyObject d)
        {
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                var img = c as Image;
                if (img != null) yield return img;
                foreach (var deeper in Inner(c)) yield return deeper;
            }
        }

        // the picture showing now, kept over the art until the next one is in
        private static void TakeSnapshot()
        {
            var fe = art as FrameworkElement;
            if (fe == null || artSnap == null || video != null || !fe.IsVisible) return;
            if (snapCovering) { snapFallback.Stop(); snapFallback.Start(); return; }   // still covering from the last move: that still stays
            if (watched.Count == 0) WatchInnerImages(fe);
            var showing = Inner(fe).FirstOrDefault(i => i.Source != null && i.Opacity > 0.5 && i.IsVisible);
            if (showing == null) return;
            artSnap.Source = showing.Source;
            artSnap.Stretch = showing.Stretch;
            artSnap.RenderTransform = fe.RenderTransform;             // the same crop as the art's (the letterbox trim)
            artSnap.RenderTransformOrigin = fe.RenderTransformOrigin;
            artSnap.BeginAnimation(UIElement.OpacityProperty, null);
            artSnap.Opacity = fe.Opacity;
            snapCovering = true;
            snapFallback.Stop();
            snapFallback.Start();
        }

        private static void MeltSnapshot()
        {
            if (!snapCovering || artSnap == null) return;
            snapCovering = false;
            snapFallback.Stop();
            artSnap.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private static void Rearm(bool moved)
        {
            Stop(moved);
            if (arm == null || !library.IsVisible) return;
            arm.Stop();
            // the same wait as Spotlight's trailer cards (the Trailer delay setting; never = none here either)
            double after = Features.Number("trailerdelay");
            if (after <= 0) return;
            arm.Interval = TimeSpan.FromSeconds(after);
            arm.Start();
        }

        private static void Play()
        {
            try
            {
                if (!library.IsVisible || !Features.On("librarytrailer") || HomeNav.Light || !ScreenPresence.InView) return;
                var w = Window.GetWindow(library);
                var details = w != null ? HomeNav.FindByName(w, "PART_ElemGameDetails") : null;
                if (details != null && details.IsVisible) return;
                var game = list.SelectedItem;
                string path; bool hasSound;
                if (game == null || !HomeNav.TrailerFor(game, out path, out hasSound)) return;
                var player = new MediaElement
                {
                    Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
                    Stretch = Stretch.UniformToFill, IsMuted = true, IsHitTestVisible = false   // the game's song plays instead (R3: its own sound)
                };
                player.MediaEnded += (s, e) => { player.Position = TimeSpan.Zero; if (video == player && ScreenPresence.InView) player.Play(); };
                player.MediaOpened += (s, e) =>
                {
                    if (video != player) return;
                    // black cinema bars are measured while it's still hidden, so any crop is in place before it shows
                    CropBars(player, () =>
                    {
                        if (video != player) return;
                        Fade(true);
                        HomeNav.SetMusic("trailer");   // the game's song comes with its trailer
                        var gidProp = game.GetType().GetProperty("Id");
                        if (gidProp != null && gidProp.GetValue(game, null) is Guid) SongSetup.NoteMissing((Guid)gidProp.GetValue(game, null));
                        if (rsHint != null && videoHasSound) rsHint.Text = "Trailer sound";
                    });
                };
                // what Windows can't decode comes from the browser (#28); its picture up = the stage shows, as for the player's own
                TrailerFix fix = null;
                fix = TrailerFix.Watch(player, path, () =>
                {
                    if (video != player) return;
                    Fade(true);
                    HomeNav.SetMusic("trailer");
                    if (rsHint != null && videoHasSound) rsHint.Text = "Trailer sound";
                });
                player.MediaFailed += (s, e) => { HomeNav.Log("library trailer failed: " + e.ErrorException.Message); if (fix == null || !fix.TookOver) Stop(); };
                video = player;
                videoHasSound = hasSound;
                soundOn = false;
                host.Children.Add(player);
                player.Play();
            }
            catch (Exception ex) { HomeNav.Log("library trailer: " + ex.Message); }
        }

        // R3 while a trailer plays: its own sound instead of the game's song (and back)
        public static bool ToggleSound()
        {
            if (video == null || !videoHasSound) return false;
            soundOn = !soundOn;
            video.IsMuted = !soundOn;
            HomeNav.TrailerSound(soundOn);
            if (rsHint != null) rsHint.Text = soundOn ? "Trailer sound off" : "Trailer sound";
            Ui.Key();
            HomeNav.Log("library trailer sound " + (soundOn ? "on" : "off"));
            return true;
        }

        // Many trailers carry black cinema bars inside their 16:9 picture. Measured twice while the trailer is still hidden
        // (0.6 s and 0.9 s in); only when both agree is it zoomed, at once, just enough to push the bars off screen, so the
        // picture reaches the top edge. Then `show`. A trailer starting on black frames just isn't cropped (no zoom later).
        private static void CropBars(MediaElement player, Action show)
        {
            player.RenderTransformOrigin = new Point(0.5, 0.5);
            double first = 0;
            var look = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            look.Tick += (s, e) =>
            {
                if (video != player) { look.Stop(); return; }
                if (!ScreenPresence.InView) return;
                double scale = BarZoom(player);
                if (first == 0) { first = scale; look.Interval = TimeSpan.FromMilliseconds(300); return; }
                look.Stop();
                if (scale > 1.02 && Math.Abs(scale - first) < 0.03)
                {
                    player.RenderTransform = new ScaleTransform(scale, scale);
                    HomeNav.Log("library trailer: black bars, zoomed to " + scale.ToString("0.00"));
                }
                show();
            };
            look.Start();
        }

        // how much zoom hides the bars (1 = none): rows that are black right across, counted from the top and the bottom
        private static double BarZoom(MediaElement player)
        {
            try
            {
                const int w = 160, h = 90;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(player) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var px = new byte[w * h * 4];
                rtb.CopyPixels(px, w * 4, 0);
                Func<int, bool> dark = y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        if (px[i] > 28 || px[i + 1] > 28 || px[i + 2] > 28) return false;
                    }
                    return true;
                };
                int top = 0; while (top < h && dark(top)) top++;
                if (top >= h) return 1;   // a black frame (fading in): nothing to measure yet
                int bottom = 0; while (bottom < h && dark(h - 1 - bottom)) bottom++;
                int band = Math.Min(top, bottom);
                if (band < h * 0.05 || Math.Abs(top - bottom) > h * 0.05) return 1;   // no bars, or not even ones
                // a real bar ends on a sharp edge (a dark scene fades out instead): the first rows past it hold picture
                Func<int, double> mean = y => { double sum = 0; for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; sum += px[i] + px[i + 1] + px[i + 2]; } return sum / (w * 3); };
                if (mean(Math.Min(h - 1, top + 1)) < 40 || mean(Math.Max(0, h - 2 - bottom)) < 40) return 1;
                return Math.Min(1.45, h / (double)(h - 2 * band));
            }
            catch { return 1; }
        }

        // entering Collection: its art fades in on its own, a moment after the screen (Playnite's image pops in without
        // a fade here, mid-way through the screen's fade-in)
        public static void FadeArtIn()
        {
            if (art == null) return;
            var keys = new DoubleAnimationUsingKeyFrames();
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            keys.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            keys.Completed += (s, e) => { if (video == null) art.BeginAnimation(UIElement.OpacityProperty, null); };
            art.BeginAnimation(UIElement.OpacityProperty, keys);
        }

        public static void Stop() { Stop(false); }

        // Selene out of view (minimized, in the tray, behind a game): the trailer stops decoding where it is and no new one starts;
        // back in view it plays on, or the wait for the selected game's trailer begins again if that is what it was doing
        private static bool armedWhenLeft;
        public static void PresenceChanged(bool inView)
        {
            try
            {
                var playing = video;
                if (!inView) { armedWhenLeft = arm != null && arm.IsEnabled; if (arm != null) arm.Stop(); if (playing != null) playing.Pause(); return; }
                if (playing != null) playing.Play();
                else if (armedWhenLeft && library != null && library.IsVisible) Rearm(false);
            }
            catch (Exception ex) { HomeNav.Log("library trailer: " + ex.Message); }
        }

        // a trailer just downloaded for the game the art is showing: it plays as if the wait had just started
        public static void TrailerReady(Guid id)
        {
            if (list == null || video != null || !library.IsVisible) return;
            var g = list.SelectedItem;
            var gid = g != null ? g.GetType().GetProperty("Id") : null;
            if (gid != null && id.Equals(gid.GetValue(g, null))) Rearm(false);
        }

        // `moved`: another game was picked, so the art waits for its picture before it comes back (it showed the last
        // game's art for a moment)
        private static void Stop(bool moved)
        {
            if (arm != null) arm.Stop();
            var old = video;
            video = null;
            if (rsHint != null) rsHint.Text = "Filter";
            if (soundOn) { soundOn = false; HomeNav.TrailerSound(false); }
            if (old != null)
            {
                Fade(false, moved);
                var gone = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };   // after the fade, whatever came next
                gone.Tick += (s, e) => { gone.Stop(); old.Stop(); old.Source = null; host.Children.Remove(old); };
                gone.Start();
                if (library != null && library.IsVisible) HomeNav.SetMusic("home");
            }
        }

        // the art has a picture (it covers the wallpaper completely then)
        private static bool ArtShowing()
        {
            if (art == null) return false;
            var source = art.GetType().GetProperty("Source");
            return source != null && source.GetValue(art, null) != null;
        }

        private static void Fade(bool on, bool moved = false)
        {
            var fade = new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 700 : 250)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            // the game's art and the wallpaper step aside while the trailer plays (dark on the left), and come back after.
            // Under a picture the wallpaper goes and comes back unseen (fading it with the art let it show through)
            bool covered = ArtShowing();
            int artDelay = !on && moved ? 260 : 0;   // the next game's picture loads first (SourceUpdateDelay 120 + decoding)
            foreach (var el in new[] { art, wallpaper, baseLayer })
            {
                if (el == null) continue;
                bool under = el != art;
                if (on)
                {
                    var ms = under && covered ? 0 : 700;
                    el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
                else
                {
                    // fades back to its own opacity (1 for the art, the theme's for the wallpaper), then lets go; what's
                    // under the art comes back once the art is up
                    var target = el == wallpaper ? 0.6 : 1.0;
                    var back = new DoubleAnimation(target, TimeSpan.FromMilliseconds(under && covered ? 0 : 300))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(artDelay + (under && covered ? 300 : 0)),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    var who = el;
                    back.Completed += (s, e) => who.BeginAnimation(UIElement.OpacityProperty, null);
                    el.BeginAnimation(UIElement.OpacityProperty, back);
                }
            }
            host.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
