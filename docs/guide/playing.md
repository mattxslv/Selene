<p align="center"><sub><a href="README.md">Guide</a> · <a href="getting-started.md">Getting started</a> · <a href="spotlight.md">Spotlight</a> · <a href="cards.md">Cards</a> · <a href="store.md">Store</a> · <a href="details-collection.md">Details &amp; Collection</a> · <a href="playing.md">Playing</a> · <a href="menu.md">Menu</a> · <a href="profiles.md">Profiles</a> · <a href="settings.md">Settings</a> · <a href="themes.md">Themes</a> · <a href="troubleshooting.md">Help</a></sub></p>

# Playing a game

![Launching](../screenshots/loading.png)

**Play.** A loading screen fades in at once with the game's art and logo, drifting slowly. It stays until the game
is really showing something (its window is up *and* it's making sound), then fades through black into the game, and
the game fades in out of the black. Nothing plays under it (*Quiet loading screen*). Games that aren't installed are
installed instead.

**Guide** takes you back to the home while a game runs. Offline games pause there (*Pause games on Guide*); games with
anti-cheat or an internet connection are only muted. Guide again goes back into the game. **Close Game** on the home
ends it, with a closing screen.

**Administrator games.** Elevated Guide, freeze, close and restart controls are unavailable until a protected
Windows broker is implemented. Close those games from their own menus. Selene settings > Advanced > Old administrator tasks offers removal of legacy tasks with Windows consent.
Selene no longer creates no-prompt
administrator launchers or uses custom elevated helper tasks. If an older Selene launcher is blocked, edit the
game's Play action to launch its original executable directly. See [Windows migration and validation](../security/windows-validation.md).

**Save backups.** With Ludusavi installed, a game's saves are backed up after playing, quietly (*Automatic save backups*).

## On its own, for every game
- A wrong or generic game folder (Downloads, Desktop...) is corrected from where the game actually runs.
- Games in your own folders (set on the add-on page) are added; installers and downloads are ignored; games whose
  folder is gone are marked not installed.
- The library is looked over 20 s after Playnite starts and when it comes back from the tray (*Library update at
  start*). Only the small mark in the top bar shows it.
- **Close to tray** hides Playnite on Exit so it comes back instantly.
