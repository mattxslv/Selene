<p align="center">
  <img src="docs/readme/intro.gif" alt="Selene" width="100%">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-1.1.0-9CCBFF?style=flat-square" alt="Version 0.9.0">
  <img src="https://img.shields.io/badge/Playnite-10%2B-1A2A4A?style=flat-square" alt="Playnite 10+">
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-1A2A4A?style=flat-square" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/controller-first-1A2A4A?style=flat-square" alt="Controller first">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-1A2A4A?style=flat-square" alt="MIT license"></a>
</p>

<p align="center">
  <b>A calm, console-style fullscreen theme for <a href="https://playnite.link">Playnite</a></b>, named after the Greek goddess of the moon.<br>
  The theme plus <b>Selene Core</b>, the add-on that brings it to life: Spotlight, profiles, the store, your captures, the launch experience.
</p>

<p align="center">
  <a href="#install">Install</a> ·
  <a href="docs/guide/README.md">Guide</a> ·
  <a href="docs/guide/settings.md">Settings</a> ·
  <a href="#themes">Themes</a> ·
  <a href="CHANGELOG.md">Changelog</a>
</p>

## Install

1. Download **both** `Selene.pthm` and `SeleneCore.pext` from the [latest release](../../releases/latest). The theme alone shows only a dark screen with a note.
2. Open each file (or drag it onto Playnite) and confirm. Restart Playnite.
3. Fullscreen mode → **Menu → Playnite settings → Appearance → Theme → Selene**. Selene walks you through the rest.

Playnite 10 or newer, Windows 10 / 11, a controller recommended. Everything Selene adds can be switched off in
**Selene settings**. The **[guide](docs/guide/README.md)** has every screen and button.

## In motion

<table>
  <tr>
    <td width="50%"><img src="docs/readme/spotlight.gif" alt="Spotlight"><p align="center"><sub><b>Spotlight</b> One row: your cards, then your games</sub></p></td>
    <td width="50%"><img src="docs/readme/launch.gif" alt="Play"><p align="center"><sub><b>Play</b> The game's art fades in at once and stays until the game is really on screen</sub></p></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/readme/store.gif" alt="Store"><p align="center"><sub><b>Store</b> Free games, sales, new releases; a game's page plays its trailer</sub></p></td>
    <td width="50%"><img src="docs/readme/find.gif" alt="Find a game"><p align="center"><sub><b>Find a game</b> Hold View, type, and the pick lands on Spotlight</sub></p></td>
  </tr>
</table>

## Screens

![Collection](docs/screenshots/library.png)

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/details.png" alt="Game details"><p align="center"><sub><b>Details</b></sub></p></td>
    <td width="50%"><img src="docs/screenshots/store.png" alt="Store"><p align="center"><sub><b>Store</b></sub></p></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/screenshots/captures.png" alt="Captures"><p align="center"><sub><b>Captures</b></sub></p></td>
    <td width="50%"><img src="docs/screenshots/activity.png" alt="Activity"><p align="center"><sub><b>Activity</b></sub></p></td>
  </tr>
</table>

## Themes

**23 color themes**, each with its own wallpaper, dark and light. Pick one per profile; every screen follows it.

![All themes](docs/readme/themes-strip.jpg)

![Avatars](docs/readme/avatars.jpg)

## Building from source

```powershell
tools\build.ps1          # builds SeleneCore\bin\SeleneCore.dll with the C# compiler that ships with Windows
tools\package.ps1        # Selene.pthm + SeleneCore.pext in dist\
```

More in the guide: [building](docs/guide/building.md).

## Credits

Built on [Playnite](https://github.com/JosefNemec/Playnite)'s fullscreen theme template (MIT). Menu icons:
Google's [Material Icons](https://github.com/google/material-design-icons) (Apache 2.0). Sounds: Selene's own and
menu sounds from SQUARE ENIX games, (c) SQUARE ENIX. Avatars: Xbox gamer pictures, property of Microsoft and the
respective game publishers. Wallpapers: from the theme collection of [Omarchy](https://github.com/basecamp/omarchy)
and its contributors, each the property of its artist. The crescent crown logo and default cover are Selene's own;
the logo lettering is [Cinzel](https://github.com/NDISCOVER/Cinzel) (SIL OFL). The pictures and clips on this page
show games from the author's own library; their art belongs to their publishers. Full notices in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Selene is not affiliated with any console maker.

## License

The code is [MIT](LICENSE). The Selene name and the crescent crown logo are not covered by the MIT license:
forks and derived themes are welcome, but please give them your own name and logo.
