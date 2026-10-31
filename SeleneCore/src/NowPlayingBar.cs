using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Selene
{
    // What UniPlaySong is playing, in the top bar left of the profile and the clock: a note and "title · artist",
    // cut with dots when it is long. One switch: "nowplaying".
    public static class NowPlayingBar
    {
        private static FrameworkElement slot;
        private static TextBlock text;
        private static DispatcherTimer watch;

        public static void Attach(Window w)
        {
            slot = w != null ? HomeNav.FindByName(w, "SeleneNowPlaying") : null;
            text = w != null ? HomeNav.FindByName(w, "SeleneNowPlayingText") as TextBlock : null;
            if (slot == null || text == null) { HomeNav.Log("now playing: no slot"); slot = null; return; }
            // the song changes on its own: looked at twice a second (one timer only, if the view is attached again)
            if (watch != null) watch.Stop();
            watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            watch.Tick += (s, e) => Refresh();
            watch.Start();
            Refresh();
        }

        public static void Refresh()
        {
            if (slot == null) return;
            try
            {
                string title, artist;
                MusicLevel.Song(out title, out artist);
                if (!Features.On("nowplaying") || !MusicLevel.Installed() || string.IsNullOrEmpty(title)) { slot.Visibility = Visibility.Collapsed; return; }
                var line = string.IsNullOrEmpty(artist) ? title : title + " · " + artist;
                if (text.Text != line) text.Text = line;
                slot.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { HomeNav.Log("now playing: " + ex.Message); }
        }
    }
}
