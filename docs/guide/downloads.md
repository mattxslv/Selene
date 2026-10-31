# Optional media downloads

Selene does not start optional game-media downloads simply because you installed it. New installations and
upgrades start with trailers, screenshots, logos/backgrounds, missing metadata and game-song setup off.
Previously downloaded files are kept. They can still be displayed or played while downloads are off.

When your library has games but no Selene art yet, Selene asks one question once, "Get art for your games?". Yes is the
consent for art, screenshots, trailers for the games you select and missing details together (it lists the sources and the save
folders); songs are separate. Not now leaves everything off and puts a red dot on Selene settings; Games > Get art for your games asks again, and the dot goes once the art is allowed. The same choices stay in Selene settings.

Choose what you want in Selene settings:

- **Spotlight > Download trailers:** Off, Games you select, or Whole library. Changing the enabled scope asks
  for confirmation again. Selected-game mode does not queue neighbouring games.
- **Spotlight > Download screenshots:** only fetch when you select a game; no whole-library startup pass.
- **Spotlight > Download logos and backgrounds:** selected-game art and appearance-preview samples.
- **Sound & music > Get game songs:** confirm one library batch through UniPlaySong. Missing yt-dlp/ffmpeg
  tools take about 130 MB before the songs themselves; this cost is disclosed first.
- **Games > Complete missing game details:** optional library metadata lookup, including missing covers and
  backgrounds, using the configured metadata add-ons and Steam.

Before any of these categories is enabled, its confirmation shows the scope and actual save directories,
including the configured Selene media folder or Playnite library location. A switch or old inherited default
without that confirmation cannot authorize a download. Playback switches do not grant download permission.
“Not now” leaves the category off. The native add-on settings page directs download choices to Selene's
fullscreen settings so it cannot skip the confirmation.

Turning a category off prevents further requests. A request already transferring can finish. UniPlaySong
owns its running download queue; use its own controls to stop that queue. For tools Selene configured,
automatic library-update/game-install music downloads are disabled; manually configured independent add-ons
are not reconfigured. Playnite, Extra Metadata Loader, UniPlaySong and other add-ons may have independent
automatic-download settings. These controls are for Selene's optional game-media work, not a global network block.

Main destinations, also shown before enabling:

- Trailers/logos: Playnite's `ExtraMetadata/games/<game ID>`.
- Screenshots: `Screenshots-v2/<game ID>` under the configured Selene media folder.
- Backgrounds/preview samples: `Hero` and `Sample` under that media folder.
- Songs: Playnite's `ExtraMetadata/UniPlaySong/Games/<game ID>`.
- Song tools: `Tools` under Selene Core's user-data directory.
- Metadata covers/backgrounds: the files directory of the configured Playnite library.

No download is silently opted back in after an upgrade or reset. No existing media files are deleted as
part of the consent change. Old screenshot caches with unverified media matches may be left unused rather
than displayed under the wrong game; their files remain on disk.
