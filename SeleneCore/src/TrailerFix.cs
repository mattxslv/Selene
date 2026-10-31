// A trailer whose picture or sound Windows' own player (the MediaElement of a trailer card or of Collection) can't decode
// (#28): videos people add themselves are often VP9 / AV1 with Opus sound (YouTube downloads, a .webm renamed .mp4), which
// Windows plays without picture, or without sound, unless extra Store extensions are installed. TrailerFix watches the
// player; whatever it couldn't decode comes from elsewhere:
//   picture: Playnite's browser (Chromium: VP8, VP9, AV1), laid exactly over the player
//   sound:   the browser (Opus, Vorbis, FLAC, MP3) or, with the browser's picture, Windows decoding just the sound (AAC, ClipAudio)
// The player's own mute is followed (whatever Selene does to it), and all of it ends when the player is taken away.
// Only HEVC on a PC without Windows' HEVC extension has no way to play: the log says so.
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Selene
{
    public class TrailerFix
    {
        private readonly MediaElement player;
        private readonly string path, name;
        private readonly MediaProbe probe;
        private readonly Action showing;
        private WebTrailer web;
        private ClipAudio audio;
        private bool webSound, stopped, decided;
        private DependencyPropertyDescriptor muteWatch, sourceWatch;
        private EventHandler onMute, onSource;

        // what the browser or the sound side took over (for the caller: false = nothing can play it)
        public bool TookOver { get; private set; }

        // showing: called when a picture from the browser is up (the caller shows its stage, as for the player's own)
        public static TrailerFix Watch(MediaElement player, string path, Action showing)
        {
            if (player == null || string.IsNullOrEmpty(path)) return null;
            try { return new TrailerFix(player, path, showing); }
            catch (Exception ex) { HomeNav.Log("trailer fix: " + ex.Message); return null; }
        }

        private TrailerFix(MediaElement player, string path, Action showing)
        {
            this.player = player;
            this.path = path;
            this.showing = showing;
            name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) + "\\" + System.IO.Path.GetFileName(path);
            probe = MediaProbe.Read(path);
            player.MediaOpened += OnOpened;
            player.MediaFailed += OnFailed;
            player.Unloaded += OnUnloaded;
            onMute = (s, e) => ApplySound();
            muteWatch = DependencyPropertyDescriptor.FromProperty(MediaElement.IsMutedProperty, typeof(MediaElement));
            muteWatch.AddValueChanged(player, onMute);
            onSource = (s, e) => { if (!swapping && (player.Source == null || player.Source.LocalPath != path)) Stop(); };
            sourceWatch = DependencyPropertyDescriptor.FromProperty(MediaElement.SourceProperty, typeof(MediaElement));
            sourceWatch.AddValueChanged(player, onSource);
            player.MediaEnded += OnEnded;   // the two loop together
            ScreenPresence.Changed += OnPresence;
        }

        private void OnOpened(object sender, RoutedEventArgs e)
        {
            if (stopped || decided) return;
            decided = true;
            bool picture = probe.HasVideo && (!player.HasVideo || player.NaturalVideoWidth == 0);
            bool sound = probe.HasAudio && !player.HasAudio;
            if (!picture && !sound) { HomeNav.Log("trailer: Windows plays " + name + " (" + probe + ")"); return; }
            Fill(picture, sound, "Windows played it without its " + (picture && sound ? "picture or sound" : picture ? "picture" : "sound"));
        }

        private void OnFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (stopped || decided) return;
            decided = true;
            bool known = probe.HasVideo || probe.HasAudio;
            Fill(known ? probe.HasVideo : true, known ? probe.HasAudio : true, "Windows couldn't open it (" + (e.ErrorException != null ? e.ErrorException.Message : "?") + ")");
        }

        private void Fill(bool picture, bool sound, string why)
        {
            var host = player.Parent as Panel;
            if (host == null) { HomeNav.Log("trailer fix: " + name + ": " + why + ", and no place for another player"); return; }
            var v = MediaProbe.Norm(probe.Video);
            bool mp4 = probe.Container == "mp4";
            string pictureFrom = null, soundFrom = null;
            if (picture)
            {
                if (probe.VideoBrowser || probe.Container == "unknown") pictureFrom = "the browser";
                // Windows decodes this picture but refused the file for its sound track: a copy without the sound plays
                else if (probe.VideoWindowsBasic && mp4 && probe.HasAudio) pictureFrom = "Windows (copy without the sound track)";
                else HomeNav.Log("trailer fix: " + name + ": its picture (" + v + ") has no decoder on this PC" + (v == "hevc" ? " (Windows' \"HEVC Video Extensions\" add one)" : ""));
            }
            if (sound)
            {
                bool browserPicture = pictureFrom == "the browser";
                if (browserPicture && probe.AudioWindows && !probe.AudioBrowser) soundFrom = "Windows (sound only)";
                else if (browserPicture) soundFrom = "the browser";
                // Windows couldn't decode it: the browser is tried whatever the label says (Vorbis in an MP4 is labelled like
                // AAC); it says in the log if it can't either
                else soundFrom = mp4 && probe.HasVideo ? "the browser (copy without the picture track)" : "the browser (sound only)";
            }
            HomeNav.Log("trailer fix: " + name + " (" + probe + "): " + why + " -> picture from " + (pictureFrom ?? (picture ? "nowhere" : "Windows"))
                + ", sound from " + (soundFrom ?? (sound ? "nowhere" : probe.HasAudio ? "Windows" : "none (silent file)")));
            if (pictureFrom == null && soundFrom == null) return;
            TookOver = true;
            var ui = player.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                // the copies (a few MB to copy, once: kept for next time) off the UI thread
                string pictureCopy = pictureFrom != null && pictureFrom.StartsWith("Windows") ? MediaProbe.WithoutTracks(path, "soun") : null;
                string soundCopy = soundFrom != null && soundFrom.Contains("without the picture") ? MediaProbe.WithoutTracks(path, "vide") : null;
                ui.BeginInvoke(new Action(() => Start(host, pictureFrom, soundFrom, pictureCopy, soundCopy)));
            });
        }

        private bool swapping;

        private void Start(Panel host, string pictureFrom, string soundFrom, string pictureCopy, string soundCopy)
        {
            if (stopped) return;
            if (pictureCopy != null)
            {
                // the player itself plays the copy (its own fade-in, crop and loop carry on as usual)
                swapping = true;
                player.Source = new Uri(pictureCopy);
                swapping = false;
                if (ScreenPresence.InView) player.Play();
                HomeNav.Log("trailer fix: playing the picture from " + System.IO.Path.GetFileName(pictureCopy));
            }
            else if (pictureFrom != null && pictureFrom.StartsWith("Windows")) { HomeNav.Log("trailer fix: the copy without sound couldn't be made"); pictureFrom = null; }
            bool browserPicture = pictureFrom == "the browser";
            webSound = soundFrom != null && soundFrom.StartsWith("the browser");
            string webFile = browserPicture ? path : webSound ? (soundCopy ?? path) : null;
            if (webFile != null)
            {
                web = WebTrailer.CreateLoop(webFile, browserPicture, () => { if (!stopped) { ApplySound(); if (browserPicture && showing != null) showing(); } }, null);
                if (web == null) { TookOver = pictureCopy != null; return; }
                var view = web.View;
                if (browserPicture)
                {
                    // exactly where the player is: same size, rounding, crop and place in its panel
                    foreach (var dp in new[] { FrameworkElement.WidthProperty, FrameworkElement.HeightProperty, UIElement.ClipProperty, UIElement.RenderTransformProperty,
                                               UIElement.RenderTransformOriginProperty, FrameworkElement.MarginProperty, FrameworkElement.HorizontalAlignmentProperty, FrameworkElement.VerticalAlignmentProperty })
                        BindingOperations.SetBinding(view, dp, new Binding(dp.Name) { Source = player });
                    Grid.SetRow(view, Grid.GetRow(player)); Grid.SetColumn(view, Grid.GetColumn(player));
                    Grid.SetRowSpan(view, Grid.GetRowSpan(player)); Grid.SetColumnSpan(view, Grid.GetColumnSpan(player));
                    Panel.SetZIndex(view, Panel.GetZIndex(player));
                    host.Children.Insert(host.Children.IndexOf(player) + 1, view);
                }
                else
                {
                    // sound only: a few pixels, out of sight (the browser still needs a size to run)
                    view.Width = 4; view.Height = 4; view.HorizontalAlignment = HorizontalAlignment.Left; view.VerticalAlignment = VerticalAlignment.Top;
                    host.Children.Add(view);
                }
            }
            if (soundFrom == "Windows (sound only)" && web != null)
            {
                audio = ClipAudio.For(path, 0);
                web.Progress = (at, length, paused) => { lastAudioAt = at; if (audio != null) audio.Sync(at, paused || !ScreenPresence.InView); };
            }
            ApplySound();
        }

        // the player's mute is the wish (R3, the trailer-sound setting); a test copy on a hidden desktop stays silent
        private void ApplySound()
        {
            if (stopped) return;
            bool on = !player.IsMuted;
            if (web != null && webSound) web.Mute(!on);
            if (audio != null) audio.Volume = on ? 0.9 : 0;
            if (!ScreenPresence.InView) OnPresence(false);
            if (web != null && webSound || audio != null) HomeNav.Log("trailer fix: sound " + (on ? "on" : "off") + " (" + (audio != null ? "Windows" : "browser") + ")");
        }

        private void OnEnded(object sender, RoutedEventArgs e) { if (web != null) web.SeekTo(0); }

        private double lastAudioAt;
        private void OnPresence(bool inView)
        {
            if (stopped) return;
            if (web != null) web.SetPaused(!inView);
            if (!inView && audio != null) audio.Sync(lastAudioAt, true);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) { Stop(); }

        public void Stop()
        {
            if (stopped) return;
            stopped = true;
            ScreenPresence.Changed -= OnPresence;
            player.MediaEnded -= OnEnded;
            player.MediaOpened -= OnOpened;
            player.MediaFailed -= OnFailed;
            player.Unloaded -= OnUnloaded;
            if (muteWatch != null) muteWatch.RemoveValueChanged(player, onMute);
            if (sourceWatch != null) sourceWatch.RemoveValueChanged(player, onSource);
            if (web != null) { web.Stop(); web = null; }
            if (audio != null) { audio.Stop(); audio = null; }
        }
    }
}
