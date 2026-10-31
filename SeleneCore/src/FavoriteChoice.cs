using System;
using Playnite.SDK.Models;

namespace Selene
{
    // Game Options must read/write the same favorite store as Spotlight. Shared Playnite flags remain shared.
    internal static class FavoriteChoice
    {
        public static bool Read(Game game, Profile profile)
        {
            return Features.On("consistentfavorites") && profile != null
                ? profile.Favorites.Contains(game.Id) : game.Favorite;
        }

        public static bool Toggle(Game game, Profile profile, Func<bool> toggleProfile, Action saveShared)
        {
            if (Features.On("consistentfavorites") && profile != null) return toggleProfile();
            game.Favorite = !game.Favorite;
            saveShared();
            return game.Favorite;
        }
    }
}
