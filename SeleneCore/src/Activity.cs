using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Selene
{
    // What Selene has running in the background, for a red dot on the menu's Selene settings and on the settings group that owns
    // the work: songs (Sound & music), trailers and art (Spotlight), the achievements check (Games).
    public static class Background
    {
        public static bool Songs { get { try { return SongSetup.Busy; } catch { return false; } } }
        public static bool Art { get { try { return TrailerFetch.Busy; } catch { return false; } } }
        public static bool Achievements
        {
            get
            {
                try { int n, all; double part; Guid? on; return AchievementsData.Checking(out n, out all, out part, out on); }
                catch { return false; }
            }
        }

        // old administrator tasks still on the PC: the dot stays until they are removed
        public static bool OldTasks { get { try { return LegacyTasks.Any; } catch { return false; } } }

        public static bool Any { get { return Songs || Art || Achievements || OldTasks; } }

        public static bool ForGroup(string group)
        {
            if (group == "Sound & music") return Songs;
            if (group == "Spotlight") return Art;
            if (group == "Games") return Achievements;
            if (group == "Advanced") return OldTasks;
            return false;
        }

        // the setting that owns the running work
        public static bool ForOption(string key)
        {
            if (key == "songsetup") return Songs;
            if (key == "trailerdownload" || key == "artdownload") return Art;
            if (key == "achievements") return Achievements;
            if (key == "legacytasks") return OldTasks;
            return false;
        }

        // the dot itself: the red of a console's notice dot, the same in every theme
        public static UIElement Dot()
        {
            return new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 2, 0, 0) };
        }

        // how far each is, 0..1 (below 0: not running), for the bar under the setting
        public static double ArtProgress() { try { return TrailerFetch.Progress; } catch { return -1; } }
        public static double SongsProgress() { try { return SongSetup.Progress; } catch { return -1; } }
        public static double AchievementsProgress()
        {
            try { int n, all; double part; Guid? on; return AchievementsData.Checking(out n, out all, out part, out on) && all > 0 ? (double)n / all : -1; }
            catch { return -1; }
        }

        // a line under the setting while its work runs: where it's at
        public static string ArtNote()
        {
            try { int n = TrailerFetch.Pending; return n > 0 ? n + (n == 1 ? " game to go" : " games to go") : null; } catch { return null; }
        }
        public static string AchievementsNote()
        {
            try { int n, all; double part; Guid? on; return AchievementsData.Checking(out n, out all, out part, out on) ? "Checking " + n + " of " + all : null; }
            catch { return null; }
        }
    }
}
