# Background Changer

Install Background Changer, then turn on **Look > Background Changer backgrounds** in Selene settings. It starts off and applies when returning to browsing; no restart is needed.

On Spotlight, Collection and game details, Selene hosts Background Changer's own background control above its normal artwork. Choose curated images, favorites, rotation and video options in Background Changer. Its format support and any conversion-tool requirements still apply. This setting does not download media or install the add-on.

Turning the switch off restores Selene's normal backgrounds. Without Background Changer, without usable add-on media, or if the custom control cannot be created, Selene keeps its normal artwork. The existing **Own backgrounds first** switch still controls that fallback. Loading and closing screens use static game artwork.

The active browsing screen owns its add-on control. Moving to another screen, hiding Playnite, opening Selene's menu, disabling the setting or unloading the screen removes that control so the add-on can stop its timers and media. Selene adds no polling timer.

## Verification

`tools/tests/backgroundchanger.ps1` checks the public custom-control API with a local fixture: default off, missing add-on, load failure, selected-game changes, fallback preservation, hide/show, disposal and subscription cleanup. It needs the Windows build and Playnite SDK, like the existing UI suites.

The real Background Changer 3.4 release was also checked in the isolated portable Playnite with generated images and a silent MP4. Curated images and timed rotation rendered on Spotlight, Collection and game details. Video position advanced with volume zero. Disabling the setting removed the mounted control and restored the normal image; the old control was unloaded and its rotation timer was stopped. The test fixture and its media live only in the isolated test copy, not in the shipped package.
