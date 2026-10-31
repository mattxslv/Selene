using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // The Captures card: your screenshots and clips, newest first, from Steam's screenshot folders, Xbox Game Bar's
    // Videos\Captures, the NVIDIA app's Videos\NVIDIA and any folders added in Selene Core's settings. A opens one full
    // screen (clips play), Left / Right go through them there, B comes back to the grid, then to the home.
    public class CapturesScreen : Screen
    {
        public class Capture
        {
            public string Path, Game;
            public DateTime When;
            public bool IsVideo;
        }

        private const double Side = 96, TileW = 330, TileH = 186, PitchX = TileW + 22, PitchY = TileH + 64, Top = 190;
        private const int Columns = 5, Visible = 3, MaxItems = 150;
        private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp" };
        private static readonly string[] VideoExt = { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };
        // the date and what follows it: "2026-10-23 15-39-06", "2026.10.23 - 15.39.06.47", and Game Bar's short date "10_25_2026 9_45_56 PM"
        private static readonly Regex DateTail = new Regex(@"\s*(\d{4}[.\-_]\d{2}[.\-_]\d{2}|\d{1,2}[.\-_/]\d{1,2}[.\-_/]\d{4}).*$");
        private static readonly Regex FileNameInJson = new Regex(@"""FileName""\s*:\s*""((?:[^""\\]|\\.)*)""");

        private static readonly string[] Filters = { "All", "Screenshots", "Clips" };
        private static int filter;   // remembered while Playnite runs
        private readonly List<Capture> all = new List<Capture>();
        private readonly List<Border> tabs = new List<Border>();
        private readonly List<Capture> items = new List<Capture>();
        private readonly List<Tile> tiles = new List<Tile>();
        private readonly Grid page = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly TranslateTransform pageShift = new TranslateTransform();
        private readonly TextBlock note, caption;
        private readonly Grid viewer = new Grid { Visibility = Visibility.Collapsed, Opacity = 0 };
        private readonly Image picture = new Image { Stretch = Stretch.Uniform };
        private readonly TextBlock viewerName, viewerDate, timeText;
        private readonly Grid controls = new Grid { IsHitTestVisible = false };   // the words, the bar and the hints over a clip (hide after a while)
        private readonly Border progress = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        private readonly Border progressTrack;
        private readonly StackPanel clipHints, imageHints;
        private readonly DispatcherTimer hideControls = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private readonly DispatcherTimer playerClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private MediaElement video;
        private WebTrailer clip;
        private ClipAudio clipSound;   // its sound, played alongside (the browser can't decode a clip's AAC sound)   // the browser playing a clip Playnite's own player couldn't (AV1, VP9)
        private int index, firstRow;
        private bool viewing, closed, paused, controlsShown;
        private double clipAt, clipLength;
        private StackPanel words;   // the game's name and date over the viewer
        private readonly string openPath;   // opened from the card's pictures: straight to this one
        private readonly Image hero = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
        private readonly Image heroBack = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
        private readonly DispatcherTimer heroLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        private string heroShown;

        // opened on one capture from a game's details page: over the page (no backdrop), the capture fading in over it
        public override bool Overlay { get { return openPath != null; } }
        public override int ShowMs { get { return openPath != null ? 180 : 320; } }

        public CapturesScreen(string openPath = null)
        {
            this.openPath = openPath;
            var dark = new Border();
            dark.SetResourceReference(Border.BackgroundProperty, "SeleneScrimBrush");
            Panel.SetZIndex(dark, -10);
            Root.Children.Add(dark);
            // the focused capture's picture, soft and faint, behind everything; a vignette keeps the edges dark
            RenderOptions.SetBitmapScalingMode(hero, BitmapScalingMode.LowQuality);
            // two layers: the new picture fades in over the old one (a crossfade; it went to black and back), each blurred once
            // into a half-size cache, so a fade doesn't redraw the blur every frame
            foreach (var layer in new[] { heroBack, hero })
            {
                RenderOptions.SetBitmapScalingMode(layer, BitmapScalingMode.LowQuality);
                layer.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 40, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
                layer.CacheMode = new BitmapCache(0.5);
                Panel.SetZIndex(layer, -6);
                Root.Children.Add(layer);
            }
            var vignette = new Border { IsHitTestVisible = false, Background = new RadialGradientBrush(new GradientStopCollection { Ink.Stop("SeleneScrimColor", 0.35, 0), Ink.Stop("SeleneScrimColor", 0.6, 0.6), Ink.Stop("SeleneScrimColor", 0.92, 1) }) { RadiusX = 0.78, RadiusY = 0.85 } };
            Panel.SetZIndex(vignette, -5);
            Root.Children.Add(vignette);
            heroLater.Tick += (s, e) => { heroLater.Stop(); ShowHero(); };

            // all / screenshots / clips: pills up top (LB / RB)
            var tabBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 36, 0, 0) };
            tabBar.Children.Add(HomeNav.FilterBadge("LB"));   // the same badges as Spotlight's and Collection's tabs
            foreach (var f in Filters)
            {
                var pill = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 6, 18, 8), Margin = new Thickness(6, 0, 6, 0), Child = new TextBlock { Text = f, FontSize = 19, FontFamily = Ui.Body } };
                tabs.Add(pill);
                tabBar.Children.Add(pill);
            }
            tabBar.Children.Add(HomeNav.FilterBadge("RB"));
            Root.Children.Add(tabBar);
            ShowTabs();

            var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side, 56, 0, 0) };
            head.Children.Add(new TextBlock { Text = "Captures", FontSize = 44, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold, Foreground = Ink.Brush("SeleneInkColor") });
            caption = new TextBlock { Text = "Looking for your screenshots and clips…", FontSize = 20, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.65), Margin = new Thickness(2, 6, 0, 0) };
            head.Children.Add(caption);
            Root.Children.Add(head);
            Ui.Rise(head, 40, 14, 320);

            // the grid on a clipped area: the focused row glides into view
            var area = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Top - 24, 0, 0), Height = Visible * PitchY + 24, ClipToBounds = true };
            double h = Visible * PitchY + 24;
            var fade = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, h) };
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            fade.GradientStops.Add(new GradientStop(Colors.Black, 24 / h));
            fade.GradientStops.Add(new GradientStop(Colors.Black, (h - 70) / h));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
            area.OpacityMask = fade;
            page.RenderTransform = pageShift;
            page.Margin = new Thickness(Side, 24, 0, 0);
            area.Children.Add(new Canvas { Children = { page } });
            Root.Children.Add(area);

            note = new TextBlock { FontSize = 21, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Side, 240, 0, 0), MaxWidth = 1100, TextWrapping = TextWrapping.Wrap };
            Root.Children.Add(note);

            // the viewer: one capture on the whole screen; over it, the game and date at the bottom left, a clip's bar with its
            // time, and the hints, all fading away after a moment (any press brings them back)
            viewer.SetResourceReference(Panel.BackgroundProperty, "SeleneScrimBrush");
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            viewer.Children.Add(picture);
            var shade = new Border { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, Height = 300, Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0xB8, 0, 0, 0), 1) }, 90) };
            controls.Children.Add(shade);
            words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(Side, 0, 0, 150) };
            viewerName = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Brushes.White, MaxWidth = 1200, TextTrimming = TextTrimming.CharacterEllipsis };
            viewerDate = new TextBlock { FontSize = 17, FontFamily = Ui.Body, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(1, 4, 0, 0) };
            words.Children.Add(viewerName);
            words.Children.Add(viewerDate);
            controls.Children.Add(words);
            // the bar: where the clip is, with the time at its right end
            progressTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(Side, 0, 0, BarBottom), Width = 1920 - 2 * Side - 150 };
            progress.Background = Ink.Brush("SeleneFocusColor");
            progressTrack.Child = progress;
            BindPlayerTrack(progressTrack, viewer);
            controls.Children.Add(progressTrack);
            timeText = new TextBlock { FontSize = 17, FontFamily = Ui.Body, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, Side, BarBottom - 8) };
            controls.Children.Add(timeText);
            // while holding Left / Right: the time being gone to, over the bar under the preview
            scrubTime = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Brushes.White, Width = 90, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
            controls.Children.Add(scrubTime);
            jumpHint = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
            jumpHint.Children.Add(new Border
            {
                MinWidth = 24, Height = 24, CornerRadius = new CornerRadius(12), Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), Child = Ui.Letter(Ui.PromptLabel("Y"), 12, FontWeights.Bold, new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)))
            });
            jumpHint.Children.Add(new TextBlock { Text = "Jump here", FontSize = 16, FontFamily = Ui.Body, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 1) });
            controls.Children.Add(jumpHint);
            clipHints = PlayerButtons();
            imageHints = Ui.Prompts(true, Ui.Prompt("\u25C0 \u25B6", "Previous / next"));
            controls.Children.Add(clipHints);
            controls.Children.Add(imageHints);
            controls.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            viewer.Children.Add(controls);
            // paused: a big see-through pause sign in the middle (pops in on pause, gone at once on play)
            pauseBadge = new Border
            {
                Width = 132, Height = 132, CornerRadius = new CornerRadius(66), Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(0.5, 0.5),
                Child = new TextBlock { Text = Ui.Icon(0xE034 /* pause */), FontFamily = Ui.Icons, FontSize = 72, Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            viewer.Children.Add(pauseBadge);
            viewer.Children.Add(VolumePanel());
            Panel.SetZIndex(viewer, 20);
            Root.Children.Add(viewer);
            // opened on one capture (a game's details page): the viewer is up from the start, so the grid never shows under it
            if (openPath != null) { viewer.Visibility = Visibility.Visible; viewer.Opacity = 1; }
            hideControls.Tick += (s, e) => { hideControls.Stop(); if (!paused && !pending && !scrubbing) ShowControls(false); };   // (kept while a spot waits)
            playerClock.Tick += (s, e) => PollPlayer();

            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));   // (the tabs show LB / RB themselves)
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Open")));
        }

        public override void Shown()
        {
            var dispatcher = Root.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                var found = Scan(MaxItems);
                dispatcher.BeginInvoke(new Action(() => { if (closed) return; all.Clear(); all.AddRange(found); Apply(); }), DispatcherPriority.Background);
            });
        }

        public override void Hidden() { closed = true; if (releaseHooked) Ui.ButtonReleased -= OnReleased; HomeNav.ClipPlaying(false); StopVideo(); hideControls.Stop(); playerClock.Stop(); heroLater.Stop(); }

        // ---- the words, bar and hints over the viewer ----
        private void ShowControls(bool on)
        {
            controlsShown = on;
            Ui.Fade(controls, on ? 1 : 0, on ? 160 : 400);
            hideControls.Stop();
            if (on) hideControls.Start();
        }

        private static string Clock(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
        }

        // ---- a clip's player: its buttons (icons with the controller button under each), volume, holding to scrub ----
        private const double BarBottom = 122;
        private static void BindPlayerTrack(Border track, Grid player)
        {
            Action resize = () => track.Width = Features.On("captureplayerfit")
                ? Math.Max(0, player.ActualWidth - 2 * Side - 150) : 1920 - 2 * Side - 150;
            player.SizeChanged += (s, e) => resize();
            resize();
        }
        private static double clipVolume = Features.Number("clipvolume") / 100.0;   // the Clip volume setting (Up / Down change it too)
        public static void ClipVolumeChanged() { clipVolume = Features.Number("clipvolume") / 100.0; }
        private TextBlock playGlyph, scrubTime, volumeGlyph;
        private StackPanel jumpHint;
        private Border volumePanel, volumeFill;
        private readonly DispatcherTimer volumeHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };

        private StackPanel PlayerButtons()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 22) };
            row.Children.Add(PlayerButton(0xE045 /* skip_previous */, Ui.PromptLabel("LB"), 26));
            row.Children.Add(PlayerButton(0xE059 /* replay_10 */, "\u25C0", 30));
            var play = PlayerButton(0xE034 /* pause */, Ui.PromptLabel("A"), 36);
            playGlyph = (TextBlock)((Border)play.Children[0]).Child;
            ((Border)play.Children[0]).Width = ((Border)play.Children[0]).Height = 66;
            ((Border)play.Children[0]).CornerRadius = new CornerRadius(33);
            row.Children.Add(play);
            row.Children.Add(PlayerButton(0xE056 /* forward_10 */, "\u25B6", 30));
            row.Children.Add(PlayerButton(0xE050 /* volume_up */, "\u25B2\u25BC", 28));
            row.Children.Add(PlayerButton(0xE044 /* skip_next */, Ui.PromptLabel("RB"), 26));
            return row;
        }

        private static StackPanel PlayerButton(int glyph, string button, double size)
        {
            var item = new StackPanel { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Bottom };
            item.Children.Add(new Border
            {
                Width = 54, Height = 54, CornerRadius = new CornerRadius(27), HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = Ui.Icon(glyph), FontFamily = Ui.Icons, FontSize = size, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            item.Children.Add(new Border
            {
                MinWidth = 26, Height = 22, CornerRadius = new CornerRadius(11), Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(0, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                Child = Ui.Letter(button, 11, FontWeights.Bold, new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)))
            });
            return item;
        }

        // Up / Down: the clip's volume, shown on a slim upright bar at the right for a moment
        private FrameworkElement VolumePanel()
        {
            volumeFill = new Border { CornerRadius = new CornerRadius(4), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Bottom };
            var track = new Border { Width = 8, Height = 220, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)), Child = volumeFill, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            volumeGlyph = new TextBlock { FontFamily = Ui.Icons, FontSize = 30, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
            var stack = new StackPanel();
            stack.Children.Add(volumeGlyph);
            stack.Children.Add(track);
            volumePanel = new Border
            {
                Child = stack, Padding = new Thickness(16, 18, 16, 20), CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 70, 0), Opacity = 0, IsHitTestVisible = false
            };
            volumeHide.Tick += (s, e) => { volumeHide.Stop(); Ui.Fade(volumePanel, 0, 300); };
            return volumePanel;
        }

        private void ChangeVolume(int dir)
        {
            clipVolume = Math.Max(0, Math.Min(1, Math.Round((clipVolume + 0.1 * dir) * 10) / 10));
            Features.SetValue("clipvolume", ((int)Math.Round(clipVolume * 100)).ToString());
            if (clipSound != null) clipSound.Volume = clipVolume;
            if (video != null) try { video.Volume = clipVolume; } catch { }
            volumeFill.Height = 220 * clipVolume;
            volumeGlyph.Text = Ui.Icon(clipVolume <= 0 ? 0xE04F /* volume_off */ : clipVolume < 0.5 ? 0xE04D /* volume_down */ : 0xE050 /* volume_up */);
            Ui.Fade(volumePanel, 1, 120);
            volumeHide.Stop();
            volumeHide.Start();
        }

        // Left / Right: a tap jumps 10 s; held, the position glides (faster the longer it's held) with the frame there shown
        // above the bar, and letting go jumps to it
        private DateTime lastSeekPress = DateTime.MinValue, scrubStarted, scrubLastTick;
        private int lastSeekDir;
        private bool scrubbing;
        private double scrubPos;
        private DispatcherTimer scrubClock;

        // with the controller, Selene sees the button let go: a short press is a tap (10 s), still down after a moment it's a
        // hold and the glide starts there (Playnite only repeats a held button after 700 ms: the first press counted as a tap)
        private bool padHeld, releaseHooked;
        private int padDir;
        private DispatcherTimer holdWait;

        private void PadSeekPress(int dir)
        {
            if (!releaseHooked) { releaseHooked = true; Ui.ButtonReleased += OnReleased; }
            if (padHeld) return;   // Playnite's repeats of the held button
            padHeld = true;
            padDir = dir;
            if (holdWait == null)
            {
                holdWait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                holdWait.Tick += (s, e) =>
                {
                    holdWait.Stop();
                    if (!padHeld || closed || !viewing) return;
                    lastSeekDir = padDir;
                    scrubbing = true;
                    if (pendingWait != null) pendingWait.Stop();
                    scrubPos = ScrubClamp(pending ? scrubPos : clipAt);
                    pending = false;
                    scrubStarted = scrubLastTick = DateTime.Now;
                    StartScrubClock();
                };
            }
            holdWait.Stop();
            holdWait.Start();
        }

        private void OnReleased(string button)
        {
            if (closed || (button != "DPadLeft" && button != "DPadRight") || !padHeld) return;
            padHeld = false;
            if (scrubbing) { WaitAtSpot(); return; }
            if (holdWait != null && holdWait.IsEnabled)
            {
                holdWait.Stop();
                Ui.Tick();
                if (pending) { scrubPos = ScrubClamp(scrubPos + padDir * 10); ShowScrub(scrubPos); WaitAtSpot(); }   // a tap moves the waiting spot
                else Seek(padDir * 10);
            }
        }

        // the glide let go: the spot stays shown, with "Y Jump here"; it's gone to after 5 s, or at once on Y; held again, the
        // glide goes on from it; B leaves it
        private bool pending;
        private DispatcherTimer pendingWait;

        private void WaitAtSpot()
        {
            scrubbing = false;
            if (scrubClock != null) scrubClock.Stop();
            pending = true;
            ShowScrub(scrubPos);
            if (pendingWait == null)
            {
                pendingWait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                pendingWait.Tick += (s, e) => { pendingWait.Stop(); if (pending) JumpToSpot(); };
            }
            pendingWait.Stop();
            pendingWait.Start();
        }

        private void JumpToSpot()
        {
            if (!pending) return;
            pending = false;
            if (pendingWait != null) pendingWait.Stop();
            HideScrub();
            SeekTo(scrubPos);
        }

        private void LeaveSpot()
        {
            pending = false;
            if (pendingWait != null) pendingWait.Stop();
            HideScrub();
            ShowProgress(clipAt, clipLength, paused);
        }

        private void HideScrub()
        {
            scrubTime.Visibility = Visibility.Collapsed;
            jumpHint.Visibility = Visibility.Collapsed;
            if (clip != null) clip.HidePreview();
            if (words.Opacity < 1) Ui.Fade(words, 1, 160);
        }

        private void StartScrubClock()
        {
            if (scrubClock == null)
            {
                scrubClock = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
                scrubClock.Tick += (s, e) => ScrubStep();
            }
            scrubClock.Start();
            Ui.Tick();
        }

        // the keyboard: no "let go" to hear, so a held key is told by its repeats
        private void SeekPress(int dir)
        {
            if (Ui.PressFromPad) { PadSeekPress(dir); return; }
            var now = DateTime.Now;
            bool repeat = dir == lastSeekDir && (now - lastSeekPress).TotalMilliseconds < 600;   // (a held button repeats after a short wait)
            lastSeekPress = now;
            lastSeekDir = dir;
            if (!repeat && !scrubbing) { Ui.Tick(); Seek(dir * 10); return; }
            if (scrubbing) return;
            scrubbing = true;
            scrubPos = ScrubClamp(clipAt + dir * 10);
            scrubStarted = scrubLastTick = now;
            StartScrubClock();
        }

        private double ScrubClamp(double t) { return Math.Max(0, Math.Min(Math.Max(0, clipLength - 0.5), t)); }

        private void ScrubStep()
        {
            var now = DateTime.Now;
            double dt = (now - scrubLastTick).TotalSeconds;
            scrubLastTick = now;
            if (!padHeld && (now - lastSeekPress).TotalMilliseconds > 330) { EndScrub(true); return; }   // (keyboard) let go
            double held = (now - scrubStarted).TotalSeconds;
            double speed = Math.Max(8, clipLength / 25) * (1 + held);   // seconds of clip per second held
            scrubPos = ScrubClamp(scrubPos + lastSeekDir * speed * dt);
            ShowScrub(scrubPos);
        }

        private void ShowScrub(double t)
        {
            double w = Math.Max(0, progressTrack.Width);
            double frac = clipLength > 0 ? t / clipLength : 0;
            progress.Width = Math.Max(0, Math.Min(w, w * frac));
            timeText.Text = Clock(t) + (clipLength > 0 ? " / " + Clock(clipLength) : "");
            double x = Side + w * frac;
            scrubTime.Text = Clock(t);
            scrubTime.Margin = new Thickness(x - 45, 0, 0, BarBottom + 14);
            scrubTime.Visibility = Visibility.Visible;
            jumpHint.Margin = new Thickness(x + 52, 0, 0, BarBottom + 12);
            jumpHint.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            if (clip != null)
            {
                double h = viewer.ActualHeight > 0 ? viewer.ActualHeight : 1080, vw = viewer.ActualWidth > 0 ? viewer.ActualWidth : 1920;
                clip.Preview(t, x / vw, (h - BarBottom - 6 - 40) / h);   // its bottom just over the time above the bar
                // the preview is drawn in the player, under the game's name and date: those step aside while it shows
                if (words.Opacity > 0.99) Ui.Fade(words, 0, 120);
            }
            ShowControls(true);
        }

        private void EndScrub(bool jump)
        {
            if (pending && !jump) { LeaveSpot(); return; }
            if (!scrubbing) return;
            scrubbing = false;
            if (scrubClock != null) scrubClock.Stop();
            HideScrub();
            if (jump) SeekTo(scrubPos);
        }

        private void SeekTo(double at)
        {
            clipAt = at;
            if (clip != null) { clip.SeekTo(at); return; }
            var me = video;
            if (me == null) return;
            try { me.Position = TimeSpan.FromSeconds(at); ShowProgress(at, clipLength, paused); } catch { }
        }

        private Border pauseBadge;
        private DateTime pressDecides = DateTime.MinValue;

        private void PausedLook(bool on)
        {
            if (playGlyph != null) playGlyph.Text = Ui.Icon(on ? 0xE037 /* play_arrow */ : 0xE034 /* pause */);
            if (pauseBadge == null) return;
            if (!on) { pauseBadge.BeginAnimation(UIElement.OpacityProperty, null); pauseBadge.Visibility = Visibility.Collapsed; return; }   // play: gone at once
            if (pauseBadge.Visibility == Visibility.Visible) return;
            pauseBadge.Visibility = Visibility.Visible;
            pauseBadge.Opacity = 0;
            pauseBadge.BeginAnimation(UIElement.OpacityProperty, Ui.Fast(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)) { EasingFunction = Ui.Out() }));
            var pop = new ScaleTransform(0.94, 0.94);
            pauseBadge.RenderTransform = pop;
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, Ui.Fast(new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(110)) { EasingFunction = Ui.Out() }));
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, Ui.Fast(new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(110)) { EasingFunction = Ui.Out() }));
        }

        private void ShowProgress(double at, double length, bool isPaused)
        {
            clipAt = at; clipLength = length;
            // (just after a press the player's report can be from before it: the press decides for a moment)
            if (paused != isPaused && DateTime.Now > pressDecides) { paused = isPaused; ShowControls(true); PausedLook(paused); }
            if (scrubbing || pending) return;
            double w = Math.Max(0, progressTrack.Width);
            progress.Width = length > 0 ? Math.Max(0, Math.Min(w, w * at / length)) : 0;
            timeText.Text = Clock(at) + (length > 0 ? " / " + Clock(length) : "");
        }

        // Playnite's own player has no events for where it is: asked a few times a second
        private void PollPlayer()
        {
            var me = video;
            if (me == null) return;
            try { ShowProgress(me.Position.TotalSeconds, me.NaturalDuration.HasTimeSpan ? me.NaturalDuration.TimeSpan.TotalSeconds : 0, paused); } catch { }
        }

        private void TogglePause()
        {
            if (clip != null)
            {
                // the sign follows the press at once (it waited for the player's next report, half a second away)
                clip.TogglePause();
                if (clipSound != null) clipSound.Toggle();
                paused = !paused;
                pressDecides = DateTime.Now.AddMilliseconds(700);
                PausedLook(paused);
                ShowControls(true);
                return;
            }
            var me = video;
            if (me == null) return;
            try { if (paused) me.Play(); else me.Pause(); } catch { }
            paused = !paused;
            PausedLook(paused);
            ShowControls(true);
        }

        private void Seek(double seconds)
        {
            if (clip != null) { clip.Seek(seconds); return; }
            var me = video;
            if (me == null) return;
            try
            {
                double length = me.NaturalDuration.HasTimeSpan ? me.NaturalDuration.TimeSpan.TotalSeconds : 0;
                double to = Math.Max(0, me.Position.TotalSeconds + seconds);
                if (length > 0) to = Math.Min(length - 0.3, to);
                me.Position = TimeSpan.FromSeconds(to);
                ShowProgress(to, length, paused);
            }
            catch { }
        }

        private void ShowTabs()
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                bool on = i == filter;
                tabs[i].Background = on ? (Brush)Ink.Brush("SeleneFocusColor", 0.95) : Brushes.Transparent;
                var label = (TextBlock)tabs[i].Child;
                label.Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Ink.Brush("SeleneInkColor", 0.7);
                label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // the grid as the filter wants it
        private void Apply()
        {
            Fill(filter == 1 ? all.Where(c => !c.IsVideo).ToList() : filter == 2 ? all.Where(c => c.IsVideo).ToList() : all.ToList());
        }

        private void Fill(List<Capture> found)
        {
            items.Clear();
            items.AddRange(found);
            page.Children.Clear();
            tiles.Clear();
            int shots = items.Count(c => !c.IsVideo), clips = items.Count - shots;
            caption.Text = items.Count == 0 ? "Screenshots and clips from your games" : Count(shots, "screenshot") + (clips > 0 ? " · " + Count(clips, "clip") : "") + ", newest first";
            if (items.Count == 0)
            {
                note.Text = all.Count > 0 ? (filter == 1 ? "No screenshots here. LB / RB show the clips, or everything." : "No clips here. LB / RB show the screenshots, or everything.")
                    : "No screenshots or clips found. Selene looks in Steam's screenshot folders, Videos\\Captures (Xbox Game Bar), Videos\\NVIDIA (the NVIDIA app) and the folders you add under Captures in Selene Core's settings.";
                return;
            }
            note.Text = "";
            var files = new List<string>();
            var at = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                var t = new Tile(TileW, TileH, 12, 5) { FocusScale = 1.05 };
                var item = new StackPanel { Width = TileW, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness((i % Columns) * PitchX, (i / Columns) * PitchY, 0, 0) };
                item.Children.Add(t.Root);
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 0) };
                if (c.IsVideo) line.Children.Add(new TextBlock { Text = "\uE714", FontFamily = Ui.Glyphs, FontSize = 13, Foreground = Ink.Brush("SeleneInkColor", 0.8), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
                line.Children.Add(new TextBlock { Text = c.Game, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = TileW - 90 });
                line.Children.Add(new TextBlock { Text = "  " + GameFacts.Ago(c.When), FontSize = 14, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });
                item.Children.Add(line);
                if (c.IsVideo)
                {
                    // a clip: a film mark on the glass face until its still is made (Windows' thumbnail, or ffmpeg's)
                    var g = new Grid();
                    g.Children.Add(new TextBlock { Text = "\uE768", FontFamily = Ui.Glyphs, FontSize = 40, Foreground = Ink.Brush("SeleneInkColor", 0.85), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                    g.Children.Add(new TextBlock { Text = System.IO.Path.GetExtension(c.Path).TrimStart('.').ToUpperInvariant() + " clip", FontSize = 14, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 14) });
                    t.Face.Child = g;
                    var tile = t;
                    Thumbnails.Request(c.Path, still =>
                    {
                        if (closed || still == null) return;
                        Ui.LoadInOrder(new List<string> { still }, 420, (k, pic) =>
                        {
                            if (closed) return;
                            var face = new Grid();
                            face.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } });
                            face.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Padding = new Thickness(7, 3, 7, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0), Child = new TextBlock { Text = "\uE768", FontFamily = Ui.Glyphs, FontSize = 13, Foreground = Brushes.White } });
                            face.Opacity = 0;
                            tile.Face.Child = face;
                            Ui.Fade(face, 1, 240);
                        });
                    });
                }
                else { files.Add(c.Path); at.Add(i); }
                page.Children.Add(item);
                tiles.Add(t);
                if (i < Columns * Visible) Ui.Rise(item, 60 + i * 35, 14, 300);
            }
            Ui.LoadInOrder(files, 420, (i, pic) =>
            {
                if (closed || at[i] >= tiles.Count) return;
                var fill = new Border { CornerRadius = new CornerRadius(11), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill }, Opacity = 0 };
                tiles[at[i]].Face.Child = fill;
                Ui.Fade(fill, 1, 240);
            });
            index = 0;
            if (openPath != null) { int at0 = items.FindIndex(c => string.Equals(c.Path, openPath, StringComparison.OrdinalIgnoreCase)); if (at0 >= 0) index = at0; }
            ShowFocus(false);
            if (openPath != null && index < items.Count && string.Equals(items[index].Path, openPath, StringComparison.OrdinalIgnoreCase)) Open(index);
            else if (openPath != null && !viewing) { viewer.Opacity = 0; viewer.Visibility = Visibility.Collapsed; }   // (gone since: the grid)
        }

        private static string Count(int n, string what) { return n + " " + what + (n == 1 ? "" : "s"); }

        // the focused capture behind, once the focus rests on it (a still for a clip, the picture itself for a screenshot)
        private void ShowHero()
        {
            if (index >= items.Count || viewing) return;
            var c = items[index];
            var path = c.IsVideo ? Thumbnails.Cached(c.Path) : c.Path;
            if (path == heroShown) return;
            heroShown = path;
            if (path == null) { Ui.Fade(hero, 0, 300); Ui.Fade(heroBack, 0, 300); return; }
            Ui.LoadInOrder(new List<string> { path }, 480, (k, pic) =>
            {
                if (closed || path != heroShown) return;
                // the picture showing now stays underneath while the new one fades in over it
                heroBack.BeginAnimation(UIElement.OpacityProperty, null);
                heroBack.Source = hero.Source;
                heroBack.Opacity = hero.Source != null ? hero.Opacity : 0;
                hero.BeginAnimation(UIElement.OpacityProperty, null);
                hero.Source = pic;
                hero.Opacity = 0;
                Ui.Fade(hero, 0.55, 420);
                Ui.Fade(heroBack, 0, 520, 200);
            });
        }

        private void ShowFocus(bool animate)
        {
            heroLater.Stop();
            heroLater.Start();
            for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(i == index);
            int row = index / Columns, rows = (tiles.Count + Columns - 1) / Columns;
            // the focused row stays in the middle of the three on screen, within the grid's ends
            int first = Math.Max(0, Math.Min(row - 1, rows - Visible));
            if (first == firstRow && animate) return;
            firstRow = first;
            pageShift.BeginAnimation(TranslateTransform.YProperty, Ui.Fast(new DoubleAnimation(-first * PitchY, TimeSpan.FromMilliseconds(animate ? 260 : 0)) { EasingFunction = Ui.Out() }));   // at the screen's rate (WPF's default is 60)
        }

        // ---- the viewer ----
        private void Open(int i)
        {
            if (i < 0 || i >= items.Count) return;
            index = i;
            var c = items[i];
            StopVideo();
            picture.Source = null;
            viewerName.Text = c.Game;
            viewerDate.Text = c.When.ToString("MMMM d, yyyy  ·  HH:mm");
            paused = false;
            PausedLook(false);
            clipAt = 0; clipLength = 0;
            progress.Width = 0;
            timeText.Text = "";
            progressTrack.Visibility = timeText.Visibility = clipHints.Visibility = c.IsVideo ? Visibility.Visible : Visibility.Collapsed;
            imageHints.Visibility = c.IsVideo ? Visibility.Collapsed : Visibility.Visible;
            HomeNav.ClipPlaying(c.IsVideo);   // the music waits while a clip plays
            ShowControls(true);
            if (c.IsVideo)
            {
                var ext = System.IO.Path.GetExtension(c.Path).ToLowerInvariant();
                if (ext == ".mkv" || ext == ".avi")
                {
                    // Playnite's player can't show these: the PC's own player, Selene waits here
                    try { HomeNav.OpenOutside(c.Path); } catch (Exception ex) { HomeNav.Log("captures: open failed: " + ex.Message); }
                    return;
                }
                // Playnite's own player first (H.264 from Xbox Game Bar); a clip it can't decode (AV1 from the NVIDIA app) goes to
                // the browser Playnite ships with, as the Store trailers do
                var me = new MediaElement { Source = new Uri(c.Path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Volume = clipVolume, Opacity = 0 };
                video = me;
                me.MediaOpened += (s, e) => Ui.Fade(me, 1, openPath != null ? 200 : 360);   // fades in with its first frame
                me.MediaEnded += (s, e) => { try { me.Position = TimeSpan.Zero; me.Play(); } catch { } };
                me.MediaFailed += (s, e) =>
                {
                    HomeNav.Log("captures: clip failed in the player (" + e.ErrorException.Message + "): the browser");
                    if (!ReferenceEquals(video, me) || !viewing) return;
                    StopVideo();
                    var path = c.Path;
                    clip = WebTrailer.CreateFile(path, null);
                    if (clip == null) { viewerDate.Text = "This clip can't be played here."; return; }
                    clipSound = ClipAudio.For(path, clipVolume);
                    clip.Progress = (at, length, isPaused) => { if (viewing && clip != null) { ShowProgress(at, length, isPaused); if (clipSound != null) clipSound.Sync(at, isPaused); } };
                    clip.View.Opacity = 0;
                    viewer.Children.Insert(1, clip.View);
                    Ui.Fade(clip.View, 1, openPath != null ? 200 : 360, openPath != null ? 150 : 250);   // (the page draws its first frame meanwhile)
                };
                viewer.Children.Insert(1, me);
                me.Play();
                playerClock.Start();
            }
            else
            {
                Ui.LoadInOrder(new List<string> { c.Path }, 1920, (k, pic) => { if (viewing && index == i) { picture.Source = pic; picture.Opacity = 0; Ui.Fade(picture, 1, openPath != null ? 180 : 320); } });
            }
            if (!viewing)
            {
                viewing = true;
                viewer.Visibility = Visibility.Visible;
                Ui.Fade(viewer, 1, 220);
                for (int k = 0; k < tiles.Count; k++) tiles[k].Focus(false);
            }
        }

        private void CloseViewer()
        {
            viewing = false;
            HomeNav.ClipPlaying(false);   // the music comes back
            StopVideo();
            Ui.Fade(viewer, 0, 200, 0, () => { viewer.Visibility = Visibility.Collapsed; picture.Source = null; });
            ShowFocus(true);
        }

        private void StopVideo()
        {
            playerClock.Stop();
            EndScrub(false);
            if (clip != null) { clip.Stop(); clip = null; }
            if (clipSound != null) { clipSound.Stop(); clipSound = null; }
            if (video == null) return;
            try { video.Stop(); video.Close(); } catch { }
            viewer.Children.Remove(video);
            video = null;
        }

        public override void Handle(string b)
        {
            if (viewing)
            {
                if (b == "B" && pending) { Ui.Back(); LeaveSpot(); return; }
                if (b == "Y" && pending) { Ui.Confirm(); JumpToSpot(); return; }
                if (b == "B") { Ui.Back(); if (openPath != null) LoginScene.Finish(); else CloseViewer(); return; }   // from the details page: back there
                bool isClip = index < items.Count && items[index].IsVideo;
                if (!controlsShown) ShowControls(true); else hideControls.Stop();
                hideControls.Start();
                if (b == "A" && isClip) { Ui.Tick(); TogglePause(); return; }
                if ((b == "DPadLeft" || b == "DPadRight") && isClip) { SeekPress(b == "DPadRight" ? 1 : -1); return; }
                if ((b == "DPadUp" || b == "DPadDown") && isClip) { Ui.Tick(); ChangeVolume(b == "DPadUp" ? 1 : -1); return; }
                if (b == "DPadLeft" || b == "DPadRight" || b == "LeftShoulder" || b == "RightShoulder")
                {
                    int next = index + (b == "DPadRight" || b == "RightShoulder" ? 1 : -1);
                    if (next < 0 || next >= items.Count) return;
                    Ui.Tick();
                    Open(next);
                }
                return;
            }
            if (b == "B") { Ui.Back(); LoginScene.Finish(); return; }
            if (b == "LeftShoulder" || b == "RightShoulder")
            {
                filter = (filter + (b == "RightShoulder" ? 1 : Filters.Length - 1)) % Filters.Length;
                Ui.Tick();
                ShowTabs();
                Ui.Pop(tabs[filter]);
                firstRow = -1;
                Apply();
                page.Opacity = 0;
                Ui.Fade(page, 1, 220);
                return;
            }
            if (tiles.Count == 0) return;
            if (b == "A") { Ui.Confirm(); Open(index); return; }
            if (!b.StartsWith("DPad")) return;
            int to = Ui.GridMove(index, b, tiles.Count, Columns);
            if (to < 0) { Ui.Shake(tiles[index].Root); return; }
            index = to;
            Ui.Tick();
            ShowFocus(true);
        }

        // ---------------- finding them ----------------
        private const string SteamPlugin = "cb91dfc9-b977-43bf-8e70-55f46e410fab";

        // the last full scan (the card's count makes one at start): what the details page shows for a game
        public static List<Capture> Known = new List<Capture>();

        public static List<Capture> Scan(int max)
        {
            var list = new List<Capture>();
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            // ScreenshotsVisualizer's index when it's installed: its files by game, whatever they're named (first, so its word on
            // a file's game wins): ExtensionsData\<its id>\ScreenshotsVisualizer\<game id>.json -> Items[].FileName
            try
            {
                var index = HomeNav.Api != null ? Path.Combine(HomeNav.Api.Paths.ExtensionsDataPath, "c6c8276f-91bf-48e5-a1d1-4bee0b493488", "ScreenshotsVisualizer") : null;
                if (index != null && Directory.Exists(index))
                {
                    int found = 0;
                    foreach (var file in Directory.GetFiles(index, "*.json"))
                    {
                        Guid id;
                        if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out id)) continue;
                        var g = HomeNav.Api.Database.Games.Get(id);
                        if (g == null) continue;
                        foreach (Match m in FileNameInJson.Matches(File.ReadAllText(file)))
                        {
                            var path = m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\/", "/");
                            if (!Path.IsPathRooted(path) || !File.Exists(path)) continue;
                            var ext = Path.GetExtension(path).ToLowerInvariant();
                            bool video = VideoExt.Contains(ext);
                            if (!video && !ImageExt.Contains(ext)) continue;
                            DateTime when;
                            try { when = File.GetLastWriteTime(path); } catch { when = DateTime.MinValue; }
                            list.Add(new Capture { Path = path, Game = g.Name, When = when, IsVideo = video });
                            found++;
                        }
                    }
                    if (found > 0) HomeNav.Log("captures: " + found + " from ScreenshotsVisualizer's index");
                }
            }
            catch (Exception ex) { HomeNav.Log("captures: ScreenshotsVisualizer: " + ex.Message); }
            try
            {
                // Steam: userdata\<account>\760\remote\<app>\screenshots
                var steamNames = SteamNames();
                var userdata = SelenePaths.SteamUserdata;
                if (Directory.Exists(userdata))
                    foreach (var account in Directory.GetDirectories(userdata))
                    {
                        var remote = Path.Combine(account, "760", "remote");
                        if (!Directory.Exists(remote)) continue;
                        foreach (var app in Directory.GetDirectories(remote))
                        {
                            var shots = Path.Combine(app, "screenshots");
                            if (!Directory.Exists(shots)) continue;
                            string name;
                            if (!steamNames.TryGetValue(Path.GetFileName(app), out name)) name = "Steam";
                            Add(list, Directory.GetFiles(shots), name);
                        }
                    }
            }
            catch (Exception ex) { HomeNav.Log("captures: steam: " + ex.Message); }
            // Xbox Game Bar: Videos\Captures ("Game Name 2026-10-23 15-39-06.png")
            Folder(list, Path.Combine(videos, "Captures"), null, false);
            // the NVIDIA app: Videos\NVIDIA\<game>\, and the older Videos\<game>\
            Folder(list, Path.Combine(videos, "NVIDIA"), null, true);
            try
            {
                if (Directory.Exists(videos))
                    foreach (var dir in Directory.GetDirectories(videos))
                    {
                        var n = Path.GetFileName(dir);
                        if (n.Equals("Captures", StringComparison.OrdinalIgnoreCase) || n.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) || SystemFolder(n, dir)) continue;
                        Add(list, Directory.GetFiles(dir), n);
                    }
            }
            catch (Exception ex) { HomeNav.Log("captures: videos: " + ex.Message); }
            // folders added in Selene Core's settings (files in them and one level of subfolders)
            foreach (var extra in SelenePaths.CapturesFolders) Folder(list, extra, null, true);
            Canon(list);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return list.Where(c => seen.Add(c.Path)).OrderByDescending(c => c.When).Take(max).ToList();
        }

        // a drive's own and hidden folders (a Videos library on a network share or another drive shows them)
        private static bool SystemFolder(string name)
        {
            return name.StartsWith("$") || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
        }
        private static bool SystemFolder(string name, string dir)
        {
            if (SystemFolder(name)) return true;
            try { return (File.GetAttributes(dir) & FileAttributes.Hidden) != 0; } catch { return true; }
        }

        // a name as a filename carries it: lower-case, the characters Windows forbids (and the "_" Game Bar writes for them)
        // and other punctuation as one space, marks like (TM) dropped; so "Onimusha_ Way of the Sword" is "Onimusha: Way of the Sword"
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            bool gap = false;
            foreach (var ch in s.ToLowerInvariant())
            {
                if (char.IsWhiteSpace(ch) || "_:\"/\\|?*<>-.',!".IndexOf(ch) >= 0) { gap = true; continue; }
                if (ch == '\u2122' || ch == '\u00AE' || ch == '\u00A9') continue;
                if (gap && sb.Length > 0) sb.Append(' ');
                gap = false;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        // captures named by a game's file-safe name take the library's name (so they group with it, and its page finds them)
        private static void Canon(List<Capture> list)
        {
            try
            {
                if (HomeNav.Api == null) return;
                var names = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var g in HomeNav.Api.Database.Games) { var k = Fold(g.Name); if (k.Length > 0 && !names.ContainsKey(k)) names[k] = g.Name; }
                foreach (var c in list) { string n; if (c.Game != null && names.TryGetValue(Fold(c.Game), out n)) c.Game = n; }
            }
            catch (Exception ex) { HomeNav.Log("captures: names: " + ex.Message); }
        }

        private static void Folder(List<Capture> list, string folder, string name, bool subfolders)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
                Add(list, Directory.GetFiles(folder), name);
                if (!subfolders) return;
                foreach (var dir in Directory.GetDirectories(folder)) { var n = Path.GetFileName(dir); if (!SystemFolder(n, dir)) Add(list, Directory.GetFiles(dir), n); }
            }
            catch (Exception ex) { HomeNav.Log("captures: " + folder + ": " + ex.Message); }
        }

        private static void Add(List<Capture> list, string[] files, string name)
        {
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                bool video = VideoExt.Contains(ext);
                if (!video && !ImageExt.Contains(ext)) continue;
                var stem = Path.GetFileNameWithoutExtension(f);
                // "Game Name 2026-10-23 15-39-06" / "Desktop 2026.10.23 - 15.39.06.47": the game is the part before the date
                var game = name ?? DateTail.Replace(stem, "").Trim();
                if (string.IsNullOrEmpty(game)) game = "Capture";
                DateTime when;
                try { when = File.GetLastWriteTime(f); } catch { when = DateTime.MinValue; }
                list.Add(new Capture { Path = f, Game = game, When = when, IsVideo = video });
            }
        }

        // Steam app ids -> names, from the games Playnite knows
        private static Dictionary<string, string> SteamNames()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (HomeNav.Api == null) return d;
                foreach (var g in HomeNav.Api.Database.Games)
                    if (g.PluginId.ToString() == SteamPlugin && !string.IsNullOrEmpty(g.GameId) && !d.ContainsKey(g.GameId)) d[g.GameId] = g.Name;
            }
            catch { }
            return d;
        }

        // the Captures card: the numbers ("128 screenshots · 12 clips"), what's newest, and the latest four as pictures
        // (Down goes into them, A opens one); found off the UI thread
        public static void CountInBackground()
        {
            var dispatcher = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                string text, headline, about;
                var latest = new List<Capture>();
                try
                {
                    var all = Scan(int.MaxValue);
                    Known = all;
                    int clips = all.Count(c => c.IsVideo), shots = all.Count - clips;
                    text = all.Count == 0 ? "None found yet" : Count(shots, "screenshot") + (clips > 0 ? " · " + Count(clips, "clip") : "");
                    latest = all.Take(4).ToList();
                    foreach (var c in latest) if (c.IsVideo) Thumbnails.Get(c.Path, 12000);   // stills for the card's clips (made here, off the UI thread)
                    if (all.Count == 0)
                    {
                        headline = "No captures yet";
                        about = "Screenshots and clips from Steam, Xbox Game Bar, the NVIDIA app and your own folders show here.";
                    }
                    else
                    {
                        // the newest capture from a game (desktop recordings and unnamed ones aside)
                        var fromGame = all.FirstOrDefault(c => !c.Game.Equals("Desktop", StringComparison.OrdinalIgnoreCase) && c.Game != "Capture");
                        headline = fromGame != null ? "New from " + fromGame.Game : "Your captures";
                        about = text + ", newest first. Down for the latest ones.";
                    }
                }
                catch { text = ""; headline = "Captures"; about = ""; }
                dispatcher.BeginInvoke(new Action(() =>
                {
                    Application.Current.Resources["SeleneStatCaptures"] = text;
                    var entries = latest.Select(c =>
                    {
                        var path = c.Path;
                        return new CardShowcase.Entry { Image = c.IsVideo ? Thumbnails.Cached(c.Path) : c.Path, Title = c.Game, Sub = GameFacts.Ago(c.When), Glyph = "\uE714", Open = () => LoginScene.OpenWith(() => new CapturesScreen(path)) };
                    }).ToList();
                    CardShowcase.Set("nav:captures", "Captures", headline, about, entries);
                }));
            });
        }
    }
}
