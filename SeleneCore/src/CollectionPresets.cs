using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace Selene
{
    // Collection filters with the same five tabs as Spotlight (All games, Installed, Recently played, Favorites, Not played
    // yet): each is a Playnite filter preset Selene keeps (made once, found again by name), applied when its tab is chosen.
    public static class CollectionPresets
    {
        private static readonly Dictionary<string, Guid> ids = new Dictionary<string, Guid>();

        public static void Ensure(IPlayniteAPI api)
        {
            try
            {
                Keep(api, "all", "All games", new FilterPresetSettings(), SortOrder.Name, SortOrderDirection.Ascending);
                Keep(api, "installed", "Installed", new FilterPresetSettings { IsInstalled = true }, SortOrder.Name, SortOrderDirection.Ascending);
                Keep(api, "recent", "Recently played", new FilterPresetSettings { LastActivity = RecentMonth() }, SortOrder.LastActivity, SortOrderDirection.Descending);
                Keep(api, "favorites", "Favorites", new FilterPresetSettings { Favorite = true }, SortOrder.Name, SortOrderDirection.Ascending);
                Keep(api, "unplayed", "Not played yet", new FilterPresetSettings { PlayTime = new EnumFilterItemProperties((int)PlaytimeCategory.NotPlayed) }, SortOrder.Name, SortOrderDirection.Ascending);
            }
            catch (Exception ex) { HomeNav.Log("collection presets: " + ex.Message); }
        }

        // played in the last month, as Spotlight's tab (it listed every game, never played ones too)
        private static EnumFilterItemProperties RecentMonth()
        {
            return new EnumFilterItemProperties(new List<int> { (int)PastTimeSegment.Today, (int)PastTimeSegment.Yesterday, (int)PastTimeSegment.PastWeek, (int)PastTimeSegment.PastMonth });
        }

        public static Guid For(string key) { Guid id; return ids.TryGetValue(key, out id) ? id : Guid.Empty; }

        private static void Keep(IPlayniteAPI api, string key, string name, FilterPresetSettings settings, SortOrder order, SortOrderDirection direction)
        {
            var existing = api.Database.FilterPresets.FirstOrDefault(p => p.Name == name);
            if (existing == null)
            {
                existing = new FilterPreset { Name = name, Settings = settings, SortingOrder = order, SortingOrderDirection = direction, ShowInFullscreeQuickSelection = false };
                api.Database.FilterPresets.Add(existing);
                HomeNav.Log("collection presets: added " + name);
            }
            else if (key == "recent" && existing.Settings != null && existing.Settings.LastActivity == null)
            {
                // made by an older Selene with no filter: given it once (one the user set up stays as it is)
                existing.Settings.LastActivity = RecentMonth();
                api.Database.FilterPresets.Update(existing);
                HomeNav.Log("collection presets: Recently played now keeps the last month");
            }
            ids[key] = existing.Id;
        }
    }
}
