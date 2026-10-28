using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Selene
{
    // Never treat a sequel, subtitle, demo or DLC as the base game merely because its name starts the same.
    public static class GameTitleMatch
    {
        private static string Normalize(string name)
        {
            return Regex.Replace((name ?? "").ToLowerInvariant(), @"[\p{P}\p{S}\s]+", " ").Trim();
        }

        private static string BaseTitle(string title)
        {
            return Regex.Replace(title, @"\s+(?:(?:ultimate|game of the year|goty|complete|definitive|deluxe|standard|collector s|collectors) edition)$", "");
        }

        public static string Select(string name, IEnumerable<KeyValuePair<string, string>> candidates)
        {
            var want = Normalize(name);
            if (want.Length == 0) return null;
            var list = candidates.Where(c => !string.IsNullOrEmpty(c.Key)).ToList();
            var exact = list.Where(c => Normalize(c.Value) == want).Select(c => c.Key).Distinct().ToList();
            if (exact.Count > 0) return exact.Count == 1 ? exact[0] : null;
            var editions = list.Where(c => BaseTitle(Normalize(c.Value)) == BaseTitle(want)).Select(c => c.Key).Distinct().ToList();
            return editions.Count == 1 ? editions[0] : null;
        }
    }
}
