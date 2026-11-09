# Issue verification, 9 November 2026

The original reports and comments for #176, #180 and #185 were read, including
their attached logs and #185's screenshot. None has been established as fixed.

- #176: the supplied log records startup jobs up to 744 ms and the initial shell
  attachment up to 1,339 ms. It also contains later details frame gaps up to 180 ms.
  This does not establish one cause for stuttering across every reported screen.
  No animation timing change is included on that evidence alone.
- #180: the log confirms both hold actions open their intended screens. No
  vibration call was found in Selene's hold paths or Playnite's controller manager.
  Only virtual controller buses were detected locally, not a physical DualSense.
  Restored physical feedback is not verified; keep the report open.
- #185: the report has Background Changer 3.4 and ImageRotater 1.6.1 together, and
  logs their conflict checks. Own backgrounds first is enabled, but it selects
  Playnite's stored background, not an add-on's live renderer. The existing
  Look > Background Changer backgrounds option hosts that add-on's public control;
  it is off by default. ImageRotater targets the standard Playnite background
  control, whereas Spotlight uses separate art layers. Neither ImageRotater
  compatibility nor the reported Collection delay is established as fixed.
  Logo alignment remains an unimplemented feature request.

The trailer-logo and ambient-state regression failures were timing-sensitive.
The tests now wait up to four seconds for observable animation state, retaining
the original final-state assertions. Existing presence checks hide and show the owner. The tile-template check uses
a locally scoped ambient resource, so the live presence timer cannot overwrite
the fixture while it verifies that ambient clocks stop without losing selection. No production animation
behavior was changed to make those checks pass.

Earlier corrections for #178, #179, #182, #183 and #184 were exercised with a
packaged build in isolated portable Playnite. #177's defensive correction removes
the missing-tab selection path, but its original crash was not reproduced: do not
automatically close that report. French phrase-collector coverage is not proof
that externally supplied artwork, article text or date abbreviations are French.

Keep unresolved reports out of the release notification mapping. No merge,
publication, issue comment or issue closure is authorized by this verification.
