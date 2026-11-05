# Maintainer release checklist

Release fixes and requested features together when the batch is verified. Do not add unrelated Ideas during release preparation.

## Before preparing a version

- Start with a clean dedicated branch based on public `main`. Private development history is separate; bring over only the intended source, tests and docs. Keep public automation and the 1.4.0 security protections.
- Add user-visible changes under `CHANGELOG.md` > Unreleased. Keep uncertain community reports distinct from verified fixes.
- Add the issue numbers fixed by the new version to `.github/release-fixes.json`. Include already-closed issues; do not list discussion or PR numbers. Use an explicit empty list for a release with no bug fixes.
- Run `tools/test.ps1`: builds on Windows and runs all six local suites, Python release-tool tests and Node notification tests. Python and Node are needed for maintainer tooling tests. `-SkipBuild` is for checking an existing build. Exit code is nonzero if any check fails; the final table shows every suite.
- Run `python tools/release.py <version> --dry-run`. It must leave files unchanged. Commit all source work first; preparation refuses dirty trees, main/detached branches, old versions, duplicate tags and inconsistent metadata.

## Prepare and verify

1. Run `python tools/release.py <version>`. It validates the complete edit plan before writing and restores original files if replacing one fails. It does not publish.
2. Review and commit the five changed metadata files. Run `tools/package.ps1` from the clean tree.
3. Run `python tools/release.py --check`. This verifies both package versions, SHA256SUMS and whether shipped code changed since BUILDINFO's source commit. Old packages are never silently accepted.
4. Run `tools/public-test.ps1 -Root "$env:USERPROFILE/selene-public-check" -PrepareOnly`, followed by `tools/hidden-test.ps1 -Root "$env:USERPROFILE/selene-public-check" -Login -Wait 24 -Keys '17000 shot:release-first-run'`. Read `temp/selene.log` and inspect the screenshot. Keep `selene-public-test` for the maintainer's own testing.
5. Test feature-on and fallback paths with the packaged build in that isolated copy. Record hardware or add-on checks that could not be reproduced. A build alone is not evidence a report is fixed.
6. Open the public release PR; require the compile and maintenance checks to pass. Stage a draft release with `Selene.pthm`, `SeleneCore.pext`, `SHA256SUMS` and `BUILDINFO.txt`. Use a branch name or full commit SHA for its target, not an abbreviated SHA.

## Approval and publishing

Announcements, including release notes, must be short and plain, like one developer talking. Show the exact draft and post only after maintainer approval. Approval for a previous announcement does not approve new wording. Do not publish a release or change `main` without the maintainer's authorization.

After approval, merge the release PR and verify that the merged source tree matches the tested tree. Publish the draft with an Announcements discussion. Download the public packages, check their hashes and versions, verify both public manifests and the latest-release link, and inspect the release workflow result. Sync version metadata and public automation back to the private development branch.

## Fix notifications

The release workflow uses the explicit version-to-issue mapping, not current issue state or labels. It comments on mapped closed issues too. A hidden per-version marker prevents duplicate bot comments on retry; retries still finish labelling and closing open issues. It never reopens a closed issue, and leaves explicitly reopened regression reports open.

Manual workflow runs default to **dry run**. Inspect that output before disabling dry run to backfill a release. Adding the 1.5.6 mapping does not itself post historical comments. Do not backfill comments without maintainer authorization. Keep mappings for past releases so retries remain possible.

## Ideas

See [Ideas status](ideas-status.md). An open discussion is not an approved feature or necessarily unfinished work.
