using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace Selene
{
    public enum SetupState { Ready, Missing, Optional, Warning }

    public class SetupItem
    {
        public string Key { get; set; }
        public string Title { get; set; }
        // what it is, in one line, so people know what they're installing
        public string About { get; set; }
        public string Detail { get; set; }
        public SetupState State { get; set; }
        // set when picking the card installs a Playnite add-on
        public string AddonId { get; set; }
    }

    public class AddonInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string About { get; set; }
    }

    // What the first-launch setup checks: the add-ons that come with Selene (installed or not),
    // and add-ons that do the same job as Selene Core (they would fight it)
    public static class SetupCheck
    {
        // Playnite 10 ships no store libraries: a fresh copy started in fullscreen imported nothing until someone found "Steam
        // library" in Desktop's add-on browser. The ones for launchers on this PC come first (installed with the rest; Playnite
        // imports the installed games on its next start). Switch: Advanced > Offer store libraries.
        private class Store { public string Id, Name; public Func<bool> Here; }
        private static readonly Store[] Stores =
        {
            new Store { Id = "SteamLibrary_Builtin", Name = "Steam", Here = () => Reg(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath") },
            new Store { Id = "EpicGamesLibrary_Builtin", Name = "Epic Games", Here = () => Dir(@"%ProgramData%\Epic\EpicGamesLauncher") },
            new Store { Id = "GogLibrary_Builtin", Name = "GOG", Here = () => Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", "client") || Dir(@"%ProgramData%\GOG.com\Galaxy") },
            new Store { Id = "XboxLibrary_Builtin", Name = "Xbox", Here = () => Dir(@"%LOCALAPPDATA%\Packages\Microsoft.GamingApp_8wekyb3d8bbwe") },
            new Store { Id = "OriginLibrary_Builtin", Name = "EA app", Here = () => Dir(@"%ProgramData%\EA Desktop") || Dir(@"%ProgramData%\Origin") },
            new Store { Id = "UplayLibrary_Builtin", Name = "Ubisoft Connect", Here = () => Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir") },
            new Store { Id = "BattlenetLibrary_Builtin", Name = "Battle.net", Here = () => Dir(@"%ProgramData%\Battle.net") },
            new Store { Id = "ItchioLibrary_Builtin", Name = "itch.io", Here = () => Dir(@"%APPDATA%\itch") },
        };
        private static bool Reg(string key, string value) { try { return !string.IsNullOrEmpty(Microsoft.Win32.Registry.GetValue(key, value, null) as string); } catch { return false; } }
        private static bool Dir(string path) { try { return System.IO.Directory.Exists(Environment.ExpandEnvironmentVariables(path)); } catch { return false; } }
        private static AddonInfo[] stores;   // (looked for once a session)
        private static AddonInfo[] StoresHere
        {
            get
            {
                if (!Features.On("storelibraries")) return new AddonInfo[0];
                if (stores == null) stores = Stores.Where(st => st.Here()).Select(st => new AddonInfo { Id = st.Id, Name = st.Name + " library", About = "Brings your " + st.Name + " games into Playnite." }).ToArray();
                return stores;
            }
        }

        public static AddonInfo[] Included { get { return StoresHere.Concat(Extras).ToArray(); } }

        private static readonly AddonInfo[] Extras =
        {
            new AddonInfo { Id = "ExtraMetadataLoader_705fdbca-e1fc-4004-b839-1d040b8b4429", Name = "Extra Metadata Loader",
                About = "Game logos and trailers for the home and game details." },
            new AddonInfo { Id = "mtkennerly.ludusavi", Name = "Ludusavi",
                About = "Backs up your game saves every time you close a game, so you never lose progress." },
            new AddonInfo { Id = "playnite-howlongtobeat-plugin", Name = "HowLongToBeat",
                About = "Shows how long a game takes to beat, next to how long you've played." },
            new AddonInfo { Id = "PlayniteAchievements", Name = "Playnite Achievements",
                About = "Your achievements from Steam, Xbox, Epic, GOG, RetroAchievements and more." },
            new AddonInfo { Id = "felixkmh_DuplicateHider_Plugin", Name = "DuplicateHider",
                About = "Shows each game once, even if you own it on several stores." },
            new AddonInfo { Id = "playnite-gameactivity-plugin", Name = "GameActivity",
                About = "A history of when and how long you played each game." },
            new AddonInfo { Id = "UniPlaySong.a1b2c3d4-e5f6-7890-abcd-ef1234567890", Name = "UniPlaySong",
                About = "Plays each game's music while you browse." },
            new AddonInfo { Id = "PlayniteAudioSwitcher_708b6ec4-bf96-4c0d-bd9d-fe0aa04d6bf1", Name = "Audio Switcher",
                About = "Switch between TV speakers and headset with the controller." },
        };

        public static readonly AddonInfo[] Clashes =
        {
            new AddonInfo { Id = "PlayState", Name = "PlayState", About = "pauses games, which Selene already does" },
            new AddonInfo { Id = "SplashScreen_d8c4f435-2bd2-49d8-98f6-87b1d415934a", Name = "Splash Screen", About = "shows a launch screen, which Selene already does" },
            new AddonInfo { Id = "NowPlaying_db4e7ade-57fb-426c-8392-60e2347a0209", Name = "Now Playing", About = "handles the running game, which Selene already does" },
            new AddonInfo { Id = "playnite-backgroundchanger-plugin", Name = "BackgroundChanger", About = "changes backgrounds, which Selene's wallpapers already do" },
            // made for the Aniki ReMake theme: with Selene it still runs its own start-up work, sounds and music control
            new AddonInfo { Id = "AnikiHelper_96a983a3-3f13-4dce-a474-4052b718bb52", Name = "Aniki Helper", About = "belongs to the Aniki ReMake theme; with Selene it adds start-up work, plays its own alert sounds and fights over the music" },
        };

        public static List<SetupItem> Run(IPlayniteAPI api)
        {
            // only the add-ons: installed or not (the library, game folders and controller live elsewhere)
            var items = new List<SetupItem>();
            items.AddRange(Included.Select(a => IncludedAddon(api, a)));
            if (Enabled(api, "mtkennerly.ludusavi")) items.Add(LudusaviProgram(api));
            if (Features.On("xboxsignin") && Enabled(api, "XboxLibrary_Builtin"))
                items.Add(new SetupItem
                {
                    Key = "xbox-signin", Title = "Xbox sign-in",
                    About = "Xbox and Game Pass games show up once Playnite is signed in to your Xbox account.",
                    State = XboxSignIn.SignedIn ? SetupState.Ready : SetupState.Missing,
                    Detail = XboxSignIn.SignedIn ? "Signed in" : "Not signed in"
                });
            items.AddRange(Clashes.Where(c => Enabled(api, c.Id)).Select(c => new SetupItem
            {
                Key = "clash:" + c.Id,
                Title = c.Name,
                State = SetupState.Warning,
                About = c.Name + " " + c.About + ".",
                Detail = "Turn it off in Add-ons so the two don't fight"
            }));
            return items;
        }

        // add-ons that come with Selene and aren't installed yet (turned-off ones need turning on in Add-ons instead)
        public static List<AddonInfo> MissingIncluded(IPlayniteAPI api)
        {
            var installed = new HashSet<string>(api.Addons.Addons ?? new List<string>(), StringComparer.OrdinalIgnoreCase);   // read once (Playnite works it out on each ask)
            return Included.Where(a => !installed.Contains(a.Id)).ToList();
        }

        private static bool Enabled(IPlayniteAPI api, string addonId)
        {
            return api.Addons.Addons.Contains(addonId) && !api.Addons.DisabledAddons.Contains(addonId);
        }

        private static SetupItem IncludedAddon(IPlayniteAPI api, AddonInfo addon)
        {
            bool installed = api.Addons.Addons.Contains(addon.Id);
            bool on = installed && !api.Addons.DisabledAddons.Contains(addon.Id);
            if (!installed && Accepted.Contains(addon.Id))
                return new SetupItem { Key = "addon:" + addon.Id, Title = addon.Name, About = addon.About, State = SetupState.Optional, Detail = PendingRestart };
            return new SetupItem
            {
                Key = "addon:" + addon.Id,
                Title = addon.Name,
                About = addon.About,
                AddonId = installed ? null : addon.Id,
                State = on ? SetupState.Ready : SetupState.Missing,
                Detail = on ? "Installed" : installed ? "Turned off · turn it on in Add-ons" : "Comes with Selene · not installed yet"
            };
        }

        private static SetupItem LudusaviProgram(IPlayniteAPI api)
        {
            bool found = LudusaviApp.Ready(api);
            return new SetupItem
            {
                Key = "ludusavi-program",
                Title = "Ludusavi program",
                About = "The free program the Ludusavi add-on uses to make the backups. Selene sets it up for you.",
                State = found ? SetupState.Ready : SetupState.Missing,
                Detail = found ? "Ready" : "Not set up yet"
            };
        }

        // opens Playnite's own add-on installer for that add-on
        public static void Install(string addonId)
        {
            LastAsked = addonId;
            HomeNav.OpenOutside("playnite://playnite/installaddon/" + addonId);
        }

        // add-ons said yes to in Playnite's install question this run: Playnite puts them in on its next start, so until the
        // restart they count as done (not offered again, and the restart question can say how many are left)
        public const string PendingRestart = "Installs when Playnite restarts";
        public static string LastAsked;
        public static readonly HashSet<string> Accepted = new HashSet<string>();

        public static void Accept(string addonId)
        {
            if (string.IsNullOrEmpty(addonId)) return;
            Accepted.Add(addonId);
            HomeNav.Log("setup: " + addonId + " installs on the next restart");
        }

        // what's still to install (missing and not said yes to yet)
        public static List<AddonInfo> LeftToInstall(IPlayniteAPI api)
        {
            return MissingIncluded(api).Where(a => !Accepted.Contains(a.Id)).ToList();
        }
    }
}
