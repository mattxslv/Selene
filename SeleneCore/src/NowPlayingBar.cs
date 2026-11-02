using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Selene
{
    // What UniPlaySong is playing, in the top bar left of the profile and the clock: a note and "title · artist", cut with dots
    // when it is long. It fades in and out, and a new song crossfades in. One switch: "nowplaying".
    // Idea and first version: amogh-w (Selene pull request #99).
    public static class NowPlayingBar
    {
        private static FrameworkElement slot;
        private static TextBlock text;
        private static DispatcherTimer watch;
        private static bool shown;

        public static void Attach(Window w)
        {
            slot = w != null ? HomeNav.FindByName(w, "SeleneNowPlaying") : null;
            text = w != null ? HomeNav.FindByName(w, "SeleneNowPlayingText") as TextBlock : null;
            if (slot == null || text == null) { HomeNav.Log("now playing: no slot"); slot = null; return; }
            shown = false;
            // the song changes on its own: looked at twice a second (one timer, if the view is attached again)
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
                string title = null, artist = null;
                // only while a song really plays (MusicLevel.Playing): not the default music, not paused by a trailer or a game
                bool on = Features.On("nowplaying") && MusicLevel.Playing();
                if (on) MusicLevel.Song(out title, out artist);
                if (on && !Fit()) on = false;
                if (!on || string.IsNullOrEmpty(title))
                {
                    if (shown) { shown = false; Ui.Fade(slot, 0, 200, 0, () => { if (!shown) slot.Visibility = Visibility.Collapsed; }); }
                    return;
                }
                var line = string.IsNullOrEmpty(artist) ? title : title + " · " + artist;
                if (!shown)
                {
                    shown = true;
                    text.Text = line;
                    slot.Opacity = 0;
                    slot.Visibility = Visibility.Visible;
                    Ui.Fade(slot, 1, 250);
                }
                else if (text.Text != line)
                {
                    // a new song: the old name fades out, the new one in
                    Ui.Fade(text, 0, 150, 0, () => { text.Text = line; Ui.Fade(text, 1, 200); });
                }
            }
            catch (Exception ex) { HomeNav.Log("now playing: " + ex.Message); }
        }

        // The line sits in the right part of the top bar and the filter tabs (All games, Not played yet...) are centered in the
        // same bar: the song is cut short before the tabs (it ran over them), and left out when not even a short name fits.
        private const double Gap = 32, NoteWidth = 34, MaxText = 320, MinText = 90;

        private static bool Fit()
        {
            var bar = slot.Parent as Panel;   // the right-side row
            var top = bar != null ? bar.Parent as FrameworkElement : null;
            var tabs = top != null ? HomeNav.FindByName(top, "SeleneFilterTabs") : null;
            if (tabs == null || bar.ActualWidth <= 0 || tabs.ActualWidth <= 0 || tabs.Visibility != Visibility.Visible) { text.MaxWidth = MaxText; return true; }
            // the slot's right edge: where the next shown part of the row (profile, clock) starts, shown or not (the row is
            // right-aligned, so that doesn't move as the song changes)
            double right = bar.TranslatePoint(new Point(bar.ActualWidth, 0), top).X;
            for (int i = bar.Children.IndexOf(slot) + 1; i < bar.Children.Count; i++)
            {
                var next = bar.Children[i] as FrameworkElement;
                if (next == null || next.Visibility != Visibility.Visible) continue;
                right = next.TranslatePoint(new Point(0, 0), top).X - next.Margin.Left;
                break;
            }
            right -= slot.Margin.Right;
            var tabsRight = tabs.TranslatePoint(new Point(tabs.ActualWidth, 0), top).X;
            var room = right - tabsRight - Gap - NoteWidth;
            if (room < MinText) return false;
            text.MaxWidth = Math.Min(MaxText, room);
            return true;
        }
    }
}
