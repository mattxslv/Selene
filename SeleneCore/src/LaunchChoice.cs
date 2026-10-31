using System;
using System.Linq;
using Playnite.SDK.Models;

namespace Selene
{
    internal static class LaunchChoice
    {
        // Do not ask a library plugin for its controllers twice. Count only choices declared in game metadata.
        public static bool DeferCover(Game game)
        {
            if (!Features.On("launchchoice") || game == null) return false;
            int count = game.GameActions == null ? 0 : game.GameActions.Count(a => a != null && a.IsPlayAction);
            if (game.PluginId != Guid.Empty && game.IncludeLibraryPluginAction) count++;
            return count > 1;
        }
    }
}
