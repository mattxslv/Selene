<p align="center"><sub><a href="README.md">Guide</a> · <a href="getting-started.md">Getting started</a> · <a href="spotlight.md">Spotlight</a> · <a href="cards.md">Cards</a> · <a href="store.md">Store</a> · <a href="details-collection.md">Details &amp; Collection</a> · <a href="playing.md">Playing</a> · <a href="menu.md">Menu</a> · <a href="profiles.md">Profiles</a> · <a href="settings.md">Settings</a> · <a href="themes.md">Themes</a> · <a href="troubleshooting.md">Help</a></sub></p>

# Help

**Something looks like plain Playnite.** Selene Core only works while the Selene theme is the fullscreen theme, and
the theme needs the add-on: check both are installed (Add-ons → Installed) and restart Playnite.

**A change in Selene settings didn't take.** Most take at once; the theme's own pieces (the intro, dialogs) need a
Playnite restart.

**No trailers, logos or music.** Those come from add-ons: ExtraMetadata (logos, trailers), UniPlaySong (music).
Run the setup check from the menu; it installs what's missing.

**Captures don't show.** Selene looks in Steam's screenshot folders, `Videos\Captures`, `Videos\NVIDIA` and
`Videos\<game>`. Other folders go on the add-on page. Clip stills need Windows thumbnails or ffmpeg on the PATH.

**A game's loading screen stays too long.** Selene waits until the game's window is up and it's making sound. Games
that start silent get a time limit; tell us which game and it can be tuned.

**Logs.** Menu → Copy log puts both logs on the clipboard. By hand: Selene writes `%TEMP%\selene.log`; Playnite writes `%APPDATA%\Playnite\playnite.log`. Both help a lot in an
issue.

**Known limits.** Windows only (WPF). The clips the browser plays (AV1) start a moment later than the others. Store
prices are the stores' own pages; buying happens in the store's app.

## Updating from 1.2.3, 1.3.0 or 1.3.1

Update to 1.4.0, it removes the old admin helper. Then go to
**Selene settings > Advanced > Old administrator tasks** to clean up what it left behind. Details in
[#74](https://github.com/mattxslv/Selene/issues/74).
