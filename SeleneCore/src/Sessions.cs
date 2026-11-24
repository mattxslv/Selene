using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK.Data;

namespace Selene
{
    // Every play session, from the GameActivity add-on (it keeps one file a game: each session's start and length). Playnite
    // itself only keeps a game's total time and when it was last played; the Activity screen's week, streak, month and session
    // facts come from these. Read once a minute at most.
    internal static class Sessions
    {
        public class Session
        {
            public DateTime DateSession { get; set; }   // its start (UTC in the file)
            public ulong ElapsedSeconds { get; set; }
        }

        private class GameFile
        {
            public List<Session> Items { get; set; }
            public Guid Id { get; set; }
        }

        private const string AddonData = "afbb1a0d-04a1-4d0c-9afa-c6e42ca855b4";
        private static Dictionary<Guid, List<Session>> all;
        private static DateTime readAt;

        private static string Folder
        {
            get { return HomeNav.Api != null ? Path.Combine(HomeNav.Api.Paths.ExtensionsDataPath, AddonData, "GameActivity") : null; }
        }

        public static bool Available() { var f = Folder; return f != null && Directory.Exists(f); }

        // each game's sessions, starts in local time, oldest first; empty when the add-on isn't there or the switch is off
        public static Dictionary<Guid, List<Session>> All()
        {
            if (!Features.On("activitysessions") || !Available()) return new Dictionary<Guid, List<Session>>();
            if (all != null && (DateTime.Now - readAt).TotalSeconds < 60) return all;
            var read = new Dictionary<Guid, List<Session>>();
            foreach (var file in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    var g = Serialization.FromJsonFile<GameFile>(file);
                    if (g == null || g.Items == null) continue;
                    var list = g.Items.Where(s => s.ElapsedSeconds >= 60).ToList();   // (a launch closed again isn't play)
                    foreach (var s in list) s.DateSession = s.DateSession.Kind == DateTimeKind.Local ? s.DateSession : s.DateSession.ToLocalTime();
                    list.Sort((a, b) => a.DateSession.CompareTo(b.DateSession));
                    if (list.Count > 0) read[g.Id] = list;
                }
                catch (Exception ex) { HomeNav.Log("sessions: " + Path.GetFileName(file) + ": " + ex.Message); }
            }
            all = read;
            readAt = DateTime.Now;
            return all;
        }

        private static IEnumerable<Session> Every() { return All().Values.SelectMany(l => l); }

        // seconds played on each day of this week, Monday first
        public static ulong[] ThisWeek()
        {
            var days = new ulong[7];
            var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
            foreach (var s in Every())
            {
                int d = (int)(s.DateSession.Date - monday).TotalDays;
                if (d >= 0 && d < 7) days[d] += s.ElapsedSeconds;
            }
            return days;
        }

        // days in a row with play, up to today (or yesterday, today not played yet)
        public static int Streak()
        {
            var played = new HashSet<DateTime>(Every().Select(s => s.DateSession.Date));
            var day = played.Contains(DateTime.Today) ? DateTime.Today : DateTime.Today.AddDays(-1);
            int n = 0;
            while (played.Contains(day)) { n++; day = day.AddDays(-1); }
            return n;
        }

        // seconds in each game over a month (the month of `start`)
        public static Dictionary<Guid, ulong> Month(DateTime start)
        {
            var from = new DateTime(start.Year, start.Month, 1);
            var to = from.AddMonths(1);
            var per = new Dictionary<Guid, ulong>();
            foreach (var pair in All())
                foreach (var s in pair.Value)
                    if (s.DateSession >= from && s.DateSession < to)
                    {
                        ulong had; per.TryGetValue(pair.Key, out had);
                        per[pair.Key] = had + s.ElapsedSeconds;
                    }
            return per;
        }

        // a game's sessions: how many, the longest, the average (seconds); false with none
        public static bool Facts(Guid id, out int count, out ulong longest, out ulong average)
        {
            count = 0; longest = average = 0;
            List<Session> list;
            // asked on every focus rest in Activity: what was read when the screen opened (All() reads every game's file again
            // once a minute, which froze the screen in the middle of browsing)
            var known = all != null && Features.On("activitysessions") ? all : All();
            if (!known.TryGetValue(id, out list) || list.Count == 0) return false;
            count = list.Count;
            longest = list.Max(s => s.ElapsedSeconds);
            average = (ulong)list.Average(s => (double)s.ElapsedSeconds);
            return true;
        }
    }
}
