<p align="center"><sub><a href="README.md">Guide</a> · <a href="getting-started.md">Getting started</a> · <a href="spotlight.md">Spotlight</a> · <a href="cards.md">Cards</a> · <a href="store.md">Store</a> · <a href="details-collection.md">Details &amp; Collection</a> · <a href="playing.md">Playing</a> · <a href="menu.md">Menu</a> · <a href="profiles.md">Profiles</a> · <a href="settings.md">Settings</a> · <a href="themes.md">Themes</a> · <a href="troubleshooting.md">Help</a></sub></p>

# Building from source

No Visual Studio needed: Selene Core builds with the C# compiler that ships with Windows.

```powershell
tools\build.ps1          # builds SeleneCore\bin\SeleneCore.dll (and installs it for the running Playnite)
tools\dev.ps1 new        # restart Playnite on the Selene in this repo (dev.ps1 back = previous theme)
tools\package.ps1        # Selene.pthm + SeleneCore.pext in dist\
```

For development, add `SeleneCore\bin` to Playnite's developer add-ons (Settings → For developers) and link `theme\`
into `%APPDATA%\Playnite\Themes\Fullscreen\Selene_a98e0027-fa6a-4f5e-a087-bad8d69f2443`.

- Color themes: `tools/palettes/*.toml` → `python tools/make_palettes.py` → `theme/Palettes`.
- Artwork: `tools/art` → `python tools/make_art.py`. Sounds: `python tools/make_sounds.py`, then
  `python tools/level_sounds.py`.
- README pictures: `python tools/make_readme_art.py` (theme strip, avatars) and `python tools/make_readme_motion.py`
  (the animated banner).
- `dotnet build tools/linux-check` checks that Selene Core compiles anywhere (C# 5).

**Releasing:** `python tools/release.py <version>` moves the Unreleased notes to the version and sets it everywhere;
commit and push; `tools\package.ps1`; then
`gh release create v<version> dist\Selene.pthm dist\SeleneCore.pext dist\SHA256SUMS dist\BUILDINFO.txt --title "Selene <version>"`.

`tools\package.ps1` requires a clean committed tree, checks package contents against the fresh stage and rejects
known old-helper content. It writes SHA256SUMS and BUILDINFO.txt with the source commit and tool/input hashes.
The legacy compiler is not deterministic; this records provenance rather than promising reproducible bytes.
For disposable local tests only, `-AllowDirty` marks the source as modified.
Run `pwsh -NoProfile -File tools/tests/security.ps1` on Linux or Windows for the helper retirement and package
regression checks. The Windows-only build, runtime and Defender validation are described in
[Windows migration and validation](../security/windows-validation.md). Version metadata does not replace
Authenticode signing; a publisher certificate is still needed before signed releases can be produced.

## Translating

Selene's words follow Playnite's language when `theme/Localization` has a file for it: `de_DE.txt`, or `de.txt` for
every German. One phrase per line, English on the left, the translation on the right, separated by ` = `. To collect
every English phrase, create an empty file `%TEMP%\selene-strings`, use Selene for a while, and read
`%TEMP%\selene-strings.txt`. Details in `theme/Localization/README.txt`. Pull requests with translations are welcome.
