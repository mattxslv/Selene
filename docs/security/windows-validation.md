# Windows migration and validation

The proposed 1.3.2 patch retires elevated game controls until a protected broker is designed and validated.
It does not establish a Defender false positive. The [original investigation](v1.3.1-review.md) and
[original scan log](v1.3.1-clamav.log) describe published 1.3.1, not the patched build. See the
[current remediation review](remediation-review.md) for confirmed changes and remaining risks.

## Safely migrate an existing installation

1. Return to paused games, save, and close them normally. Close Playnite. Replacing a helper file does not stop
   an already-running old helper or resume a process it suspended. The script reports still-running SeleneHelper
   processes and refuses file cleanup until they exit; do not force-stop one while a game is suspended.
2. From a **normal, unelevated PowerShell**, run `tools\remove-legacy-elevation.ps1` without switches. It reports
   task principals/actions/working directories and helper diagnostics. Save the output, export relevant tasks,
   and record every affected game's original executable, arguments and working directory. Old Selene replaced
   Play actions without saving a backup. The add-on now recovers single executable actions from remaining tasks,
   saving the previous Play action and task XML under its user-data LauncherBackups folder first. Missing tasks,
   missing executables and ambiguous definitions still need manual restoration.
3. Install the freshly built add-on and restart Playnite. Verify the installed SeleneHelper.exe was replaced
   with the inert stub (every invocation exits 2). Inspect custom/portable/old installation paths too. When the
   Selene theme is active, its startup check offers a notice and settings action for remaining legacy tasks.
4. Remove reviewed legacy tasks, using either:
   - Selene settings > Advanced > **Old administrator tasks** > Remove. Ordinary UAC consent is required per
     validated task. Declining stops further prompts. Unusual names and nested tasks require manual review.
   - An **elevated PowerShell** running `tools\remove-legacy-elevation.ps1 -Remove`. This mode only unregisters
     tasks directly in `\Selene\` and returns; it never deletes or scans user-profile files. Nested tasks are
     reported but left intact. Review names/actions first: not every custom task in this folder is necessarily
     Selene-owned. Export and remove any remaining affected nested/unusual tasks manually in Task Scheduler.
5. Restore direct Play actions using the recorded executable/arguments/working directory. Old Selene scheduled
   launchers are blocked even outside the theme. Restore direct actions before uninstalling the extension,
   which also removes its launch guard.
6. Back in the original user's **normal, unelevated PowerShell**, use
   `tools\remove-legacy-elevation.ps1 -CleanFiles` to remove old request/log files. Optionally add
   `-DeleteOldHelpers` after reviewing diagnostic results. The script refuses combined task/file removal,
   refuses elevated file cleanup, and skips files reached through reparse points. Helper classification is
   heuristic; compare hashes and inspect unknown files manually. Custom and portable paths may be missed.
   Old HelperTask settings are ignored. A HelperPath naming SeleneHelper.exe is ignored too.

**The exposure is not resolved until the affected elevated tasks are actually removed.** The stub does not
protect a writable path from future replacement. Removing a request file or scanning clean is insufficient.
No step disables Defender/UAC, restores quarantined files, or runs an old helper. The app automatically repairs recoverable Play actions at startup, with a backup. Task deletion still requires consent.

## Capture Defender evidence

Keep protection enabled and quarantined files quarantined. Capture detection name, affected path and SHA-256,
time, download/extraction/execution stage, engine/platform/intelligence versions and action taken. Compare
with [original release hashes](v1.3.1-hashes.json); do not conflate the archive with its individual binaries.

The read-only collector writes diagnostics locally; it does not execute samples, start tasks, change protection
or upload anything. Redact usernames/private paths before sharing. Denied operations are recorded, not bypassed.

```powershell
.\tools\collect-security-evidence.ps1 -OutputDirectory "$env:USERPROFILE\Desktop\Selene-security-evidence"
```

## Completed on Linux

Core compiles against Framework 4.6.2 with C# 5 (0 errors, two existing unused-field warnings). The security suite
covers command retirement, task-name/CSV parsing, scoped mock task removal, link-aware file cleanup and package
integrity. Final suite: 75 command/task checks, 781 source guards, and 85 cleanup/package checks, all passed.
Real Framework binaries are also used for a validation-only archive; this is not Toolbox packaging
or a distributable release.

## Windows release gates

Automated checks have been rerun on this Windows PC after deferring the broker: the build passes with three
existing compiler warnings; security checks pass on PowerShell 5.1 and 7 (81 command/task checks, 781 source guards,
85 cleanup/package checks each); check.ps1 passes 63 assertions; library.ps1 passes 20 checks; interaction.ps1
passes 15 checks. These results do not replace the remaining interactive, migration and final-artifact checks below.

- Run `tools/tests/security.ps1` on Windows PowerShell 5.1 and PowerShell 7, plus existing Playnite localization,
  library and interaction checks with their required runtime. Test Windows 10/11 and a fresh install as well as
  an upgrade from the affected releases.
- In an isolated VM with Defender/UAC enabled, use harmless test tasks (for example notepad) matching old helper
  and game definitions. Verify principal and task/file/ancestor ACLs. Include decoys in `\Other\`, a root task
  `SeleneBackup`, an unusual `\Selene\a&b`, and nested `\Selene\Sub\Task`.
- Test app cleanup approval/cancellation and failed/denied queries. A failure must not claim the PC is clean.
  Only validated direct-child names are removed through the app; unrelated/unsupported tasks survive.
- Test script default report mode, elevated `-Remove`, and unelevated `-CleanFiles`. Combined modes and elevated
  file deletion must fail. Script task removal uses structured parameters; nested/outside-folder tasks survive.
  Test profile mismatch when a standard user supplies another administrator's UAC credentials: only the intended
  tasks are removed, and no administrator-profile file cleanup occurs. Test junctions/symlinks and inaccessible
  paths. Link-checking is not claimed to be atomic; file cleanup must remain unelevated.
- Verify replacement of the old helper, obsolete copies, pending files, existing running helpers, custom
  settings, and portable installations. No old task is started. Restore and test direct Play actions. Task
  cleanup does not imply a maliciously replaced executable has been repaired; inspect it separately.
- Test ordinary offline/online games: launch, Guide, pause/resume, close/restart, cancellation, and game exit.
  Test elevated games: ordinary best-effort window switching only, no elevated freeze/close/restart,
  no new task setup and no command file writes. If Windows refuses focus, the game stays visible without a cover.
  Test companion Sleep/Restart/Shut down fallback with a retired-helper setting and a normal trusted companion.
- Run `tools/build.ps1` and `tools/package.ps1` from the clean committed patch source using the intended Playnite
  SDK and Toolbox. Verify binary/package versions, exact helper replacement, final package inventory and bytes,
  `SHA256SUMS`, and `BUILDINFO.txt` commit/tool hashes. `-AllowDirty` is for test builds only. Check rejection of
  stale helper binaries and unsafe/duplicate theme entries in a disposable test checkout; do not distribute them.
- Scan final archives and extracted files with updated Defender, then test normal install/use and cleanup.
  Record exact results and reproduce any detections. Linux/ClamAV results are not a Defender verdict. Submit
  the exact flagged artifact through the relevant vendor's developer process if warranted; no exclusions.
- Sign/timestamp with a genuine publisher certificate if available, and revalidate final bytes/hashes after
  signing. Current builds are unsigned; metadata and SHA-256 do not authenticate a publisher.

Do not publish/tag a release or upload private test artifacts until the Windows gates are met and release is
explicitly authorized. Prepared version/manifests do not indicate a release has been published.

## Future protected broker

The broker prototype is deferred outside the release sources. This release does not include a privileged replacement.

Require a signed administrator-managed installation/update path and protected dependencies, deliberate account/
ACL policy, narrowly authorized IPC, verified process-instance handles, bounded requests/acknowledgements,
independent suspension recovery, and uninstall/migration support. A same-user pipe ACL alone does not authenticate
Selene against other programs running under that user.

## Automatic launcher repair — 2026-10-28

Build passed on Windows (three existing warnings). Security suite passed on PowerShell 5.1 and 7:
81 command/task checks, 773 source guards, 85 cleanup/package checks each. Existing check.ps1 passed 54 assertions.
Recovery tests cover exact task names, helper rejection, XML argument decoding, multiple actions and prohibited XML entities.
No real task was executed or deleted for these checks. Interactive migration remains to be tested after restarting Playnite.

## 1.4.0 release files

Scanned with Microsoft Defender on Windows 11 (engine 1.1.26100.4, definitions 1.463.12.0) on 2026-10-29, both the
release files and everything inside them: no threats found.

- `Selene.pthm` sha256 `4bbbda4b3e00d5c99a825e10287b96f614e3d5c4a582531c0f86638bd83b9771`
- `SeleneCore.pext` sha256 `5d398a288e28c0420b08b0b4eab12cdffe23ca6c041020f8cfadcacc46350c08`

A clean scan today doesn't promise every antivirus will agree tomorrow. If yours flags 1.4.0, please open an issue
with the detection name.
