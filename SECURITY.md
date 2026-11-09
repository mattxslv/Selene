# Security

## Supported versions

Only the [latest release](https://github.com/mattxslv/Selene/releases/latest) gets fixes. Update both Selene and Selene Core before reporting.

## Reporting a problem

Please don't open a public issue for a security problem. Use **[Report a vulnerability](https://github.com/mattxslv/Selene/security/advisories/new)** (the Security tab) instead; only the maintainer can see it.

Say what you found, which version, and how to reproduce it. I'll reply there, and once a fix is out the advisory is published with credit if you want it.

## What counts

Selene Core runs inside Playnite with your user's rights. Things like running code from a downloaded file, writing outside Selene's own folders, leaking a key or path, or getting around the download consent are security problems. A crash or a visual bug is a normal bug: open an [issue](https://github.com/mattxslv/Selene/issues).

Download Selene only from this repository's releases. Each release lists SHA-256 hashes of both files in `SHA256SUMS`.
