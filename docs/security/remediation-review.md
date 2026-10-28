# Private remediation review — 2026-10-28

This reviews `security/harden-elevated-helper` for the proposed 1.3.2 patch. No public push, release, Defender
exception, antivirus upload or protection change is part of this work. Findings below describe source review
and Linux tests, not a Windows exploitation test or a Defender verdict.

## Confirmed findings and changes

- **The command interpreter is retired (`6cb6576`).** The helper's only method returns 2. The task installer,
  elevated game controls and request-file execution paths are gone. Old task-based Play actions are blocked,
  including outside the Selene theme. No privileged game-control replacement has been introduced.
- **Legacy tasks survive updates.** `\Selene\Helper` and old game tasks may still point to writable executables.
  Replacing the interpreter with a stub does not protect that path against later replacement. Exploitability
  depends on the installed task principal/run permissions and executable/ancestor ACLs; those need Windows
  inspection. Tasks must actually be removed before this residual exposure is considered resolved.
- **Task removal is explicit (`609a679`, tightened here).** The settings action invokes only the Windows system
  `schtasks.exe` with a validated `/delete` command, with ordinary UAC consent per task. No task is run, created
  or changed. Names outside the exact `\Selene\` namespace, unusual names and nested tasks are left for manual
  review in the app. This is privileged cleanup, not a new elevated command broker.
- **The unfinished cleanup script deleted profile files while elevated.** That could follow a mutable path or
  use the credential-provider administrator's profile instead of the original user's profile. This follow-up
  separates modes: elevated `-Remove` only unregisters tasks directly inside `\Selene\` and returns; normal
  `-CleanFiles` handles profile files. `-DeleteOldHelpers` now requires `-CleanFiles`. File cleanup refuses
  elevation, rejects reparse points in the file and every ancestor, and does not recurse during deletion.
  Link checking is defense in depth; it is not an atomic race-proof filesystem primitive. No elevated token
  is used for file deletion. The add-on no longer automatically deletes a pending request file either. Running old-helper processes are
  reported, and file cleanup refuses to proceed until they exit normally. Unregistering a task is not proof
  that an already-running helper stopped.
- **Task parsing could truncate a CSV-escaped quoted name.** The first field is now decoded without truncating
  doubled quotes. Malformed fields are ignored, and quoted unusual names are reported intact for manual work.
  Task-name validation uses absolute regex anchors so a trailing newline cannot become a deletion target.
- **The task query's timeout was ineffective.** Reading a stream synchronously before waiting could block
  forever; ignoring the query exit code could look like an empty task list. Both streams are drained
  concurrently, with bounded waits and exit-code checks. Failed verification preserves known state and cannot
  produce a successful removal count. Windows behavior still requires testing.
- **Companion-program compatibility (`609a679`).** A HelperPath naming SeleneHelper.exe is ignored so the inert
  stub cannot swallow Sleep/Restart/Shut down requests. Other companion programs retain Playnite's own rights.
- **Package/release checks (`2e03dbd`, tightened here).** The package must match its clean stage by inventory and
  SHA-256. Known old-helper markers and an oversize helper are rejected even when stage/package hashes match.
  Theme paths reject traversal, absolute paths and case-insensitive duplicates as well as executable/script/
  archive extensions. Git identity/clean-tree failures abort packaging, stale provenance is cleared, and
  BUILDINFO records source identity and compiler/SDK/Toolbox versions and hashes. The Framework path is obtained
  from Windows rather than assuming C:.

The size/marker check is a regression sanity check for the known old implementation; **it does not prove
arbitrary supplied bytes are inert**. A clean source build, review of the minimal stub, final packaged-byte
comparison, and Windows artifact validation are all needed. Diagnostic `stub-like` results and writable-ACL
warnings are heuristics, not attestations or effective-access proofs.

## Verified on Linux

- Core builds against .NET Framework 4.6.2 reference assemblies with C# 5, with no errors (two existing unused-field warnings).
- Security regressions exercise legacy command rejection, task recognition, strict names, escaped CSV, file/link
  cleanup, and an isolated real removal block using a mock unregister cmdlet. No Windows task was touched.
- Package regressions cover known legacy markers, modified/missing/extra files, duplicate names, path traversal,
  Windows separators and a stale helper matching its staged bytes.
- Final suite: **75** command/task checks, **781** source guards, **85** cleanup/package checks, all passed.
- PowerShell scripts parse. The actual old published helper and a freshly compiled Framework stub are checked
  separately; a Linux-built validation archive exercises the package verifier with real compiled binaries.

See [windows-validation.md](windows-validation.md) for the final checklist and exact test results.

## Remaining risks and release blockers

- **Windows migration is unverified.** Test real task discovery, denied enumeration, UAC approval/cancellation,
  cross-account credentials, legacy executable ACLs, junctions, custom/portable installations, and correct Play
  action restoration. Script removal deliberately leaves nested tasks for manual inspection. Review all task
  actions first; the folder name alone cannot establish who created a task.
- **Task removal remains necessary.** A stub update, deleting elevated.txt, or a clean AV scan alone does not
  remove the retained elevated launch entitlement. Existing tasks and old running helper instances must be handled.
- **Windows packaging, Playnite runtime compatibility and Defender scans are not established by Linux checks.**
  No patched release should be published before those checks. No claim of a false positive is justified yet.
- **Unsigned/non-reproducible builds.** Build information improves traceability but does not pin local build
  tools or make the old Framework compiler deterministic. A real signing certificate and trusted release
  process are still needed. Hashes are integrity records, not publisher authentication.
- **Other existing functionality is not fully audited.** Runtime program downloads in SongSetup, predictable
  queued update locations and missing download-digest checks warrant separate integrity work. Ordinary process
  suspension and folder-wide termination remain potential reliability/heuristic concerns. They do not restore
  the retired elevated helper, but this review does not certify every DLL method safe.
- Affected helper presence was assessed in 1.2.3, 1.3.0 and 1.3.1; earlier releases remain unassessed. No end-to-end
  privilege escalation or Defender classification has been reproduced here.
