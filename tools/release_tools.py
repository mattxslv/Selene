"""Release planning and verification. No network calls or publishing."""
import argparse
import datetime
import hashlib
import re
import subprocess
import tempfile
from pathlib import Path
from zipfile import BadZipFile, ZipFile

ROOT = Path(__file__).resolve().parent.parent
REPO = "https://github.com/mattxslv/Selene"


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def version(text):
    match = re.search(r"^Version: (\d+\.\d+\.\d+)\s*$", text, re.M)
    if not match:
        raise ValueError("Missing or invalid Version field")
    return match[1]


def metadata(root):
    current = version((root / "theme/theme.yaml").read_text(encoding="utf-8-sig"))
    if version((root / "SeleneCore/extension.yaml").read_text(encoding="utf-8-sig")) != current:
        raise ValueError("Theme and Core versions differ")
    if not re.search(rf"^## {re.escape(current)}\s+-", (root / 'CHANGELOG.md').read_text(encoding='utf-8-sig'), re.M):
        raise ValueError("Current version is missing from changelog")
    for name in ("Selene", "SeleneCore"):
        text = (root / f"manifests/{name}.yaml").read_text(encoding="utf-8-sig")
        versions = re.findall(r"^  - Version: (\d+\.\d+\.\d+)\s*$", text, re.M)
        if not versions or versions[0] != current or len(versions) != len(set(versions)):
            raise ValueError(f"{name} manifest is out of sync or has duplicates")
        first = re.search(r"^Packages:\n  - Version: [^\n]+\n(.*?)(?=^  - Version:|\Z)", text, re.M | re.S)
        if not first or f"/v{current}/{name}." not in first[1]:
            raise ValueError(f"{name} manifest has no current package URL")
    return current


def replace(text, pattern, value):
    result, count = re.subn(pattern, lambda _: value, text, count=1, flags=re.M)
    if count != 1:
        raise ValueError(f"Missing release marker: {pattern}")
    return result


def plan(root, target):
    if not re.fullmatch(r"\d+\.\d+\.\d+", target):
        raise ValueError("Use a version like 1.5.7")
    current = metadata(root)
    if tuple(map(int, target.split('.'))) <= tuple(map(int, current.split('.'))):
        raise ValueError("New version must be greater than the current version")
    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8-sig")
    match = re.search(r"^## Unreleased\n(.*?)(?=^## )", changelog, re.M | re.S)
    if not match or not match[1].strip():
        raise ValueError("No Unreleased notes to publish")
    if re.search(rf"^## {re.escape(target)}\b", changelog, re.M):
        raise ValueError("Version already exists in changelog")
    today = datetime.date.today().isoformat()
    notes = [n.strip() for n in re.split(r"^- ", match[1], flags=re.M) if n.strip()]
    changes = {"CHANGELOG.md": replace(changelog, r"^## Unreleased\n", f"## Unreleased\n\n## {target} - {today}\n")}
    for path in ("theme/theme.yaml", "SeleneCore/extension.yaml"):
        changes[path] = replace((root / path).read_text(encoding="utf-8-sig"), r"^Version: .*$", f"Version: {target}")
    lines = '\n'.join("      - >-\n        " + ' '.join(n.split()) for n in notes)
    for name, ext, api in (("Selene", "pthm", "2.10.0"), ("SeleneCore", "pext", "6.0.0")):
        path = f"manifests/{name}.yaml"
        text = (root / path).read_text(encoding="utf-8-sig")
        entry = f"Packages:\n  - Version: {target}\n    RequiredApiVersion: {api}\n    ReleaseDate: {today}\n    PackageUrl: {REPO}/releases/download/v{target}/{name}.{ext}\n    Changelog:\n{lines}\n"
        changes[path] = replace(text, r"^Packages:\n", entry)
    return changes


def write_plan(root, changes):
    originals = {name: (root / name).read_bytes() for name in changes}
    staged = {}
    try:
        for name, text in changes.items():
            with tempfile.NamedTemporaryFile(dir=(root / name).parent, delete=False) as f:
                staged[name] = Path(f.name)
                f.write(text.encode('utf-8'))
        try:
            for name, path in staged.items():
                path.replace(root / name)
        except OSError:
            for name, data in originals.items():
                (root / name).write_bytes(data)
            raise
    finally:
        for path in staged.values():
            path.unlink(missing_ok=True)


def packages(root):
    current = metadata(root)
    dist = root / 'dist'
    info = (dist / 'BUILDINFO.txt').read_text(encoding='utf-8-sig')
    source = re.search(r"^Source commit: ([0-9a-f]{40})\s*$", info, re.M)
    if not source or 'Local test mode: True' in info:
        raise ValueError("Packages need a clean recorded source commit")
    if git(root, 'diff', '--name-only', source[1], 'HEAD', '--', 'SeleneCore', 'theme', 'tools/build.ps1', 'tools/package.ps1', 'tools/verify-package.ps1'):
        raise ValueError("dist is older than the code; run tools/package.ps1")
    sums = {}
    for line in (dist / 'SHA256SUMS').read_text().splitlines():
        digest, name = line.split()
        if name not in ('Selene.pthm', 'SeleneCore.pext') or name in sums:
            raise ValueError("Unexpected or duplicate checksum entry")
        sums[name] = digest
    if set(sums) != {'Selene.pthm', 'SeleneCore.pext'}:
        raise ValueError("Missing package checksum")
    for name, entry in (('Selene.pthm', 'theme.yaml'), ('SeleneCore.pext', 'extension.yaml')):
        path = dist / name
        if hashlib.sha256(path.read_bytes()).hexdigest() != sums[name]:
            raise ValueError(f"Checksum mismatch: {name}")
        with ZipFile(path) as archive:
            if version(archive.read(entry).decode('utf-8-sig')) != current:
                raise ValueError(f"Package version mismatch: {name}")
    return current


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('version', nargs='?')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--dry-run', action='store_true', help='validate and list proposed edits without writing')
    mode.add_argument('--check', action='store_true', help='verify current metadata and packaged artifacts')
    args = parser.parse_args()
    try:
        if git(ROOT, 'status', '--porcelain'):
            raise ValueError("Working tree is dirty; commit or stash before release checks")
        if args.check:
            if args.version:
                raise ValueError("--check takes no version")
            print(f"Verified {packages(ROOT)}: versions, package hashes and source freshness")
            return
        branch = git(ROOT, 'branch', '--show-current')
        if not branch or branch in ('main', 'master'):
            raise ValueError("Prepare releases on a dedicated branch")
        if not args.version:
            raise ValueError("Supply the next version, or use --check")
        changes = plan(ROOT, args.version)
        if f"refs/tags/v{args.version}" in git(ROOT, 'tag', '--list', f'v{args.version}', '--format=%(refname)'):
            raise ValueError("Release tag already exists")
        for path in changes:
            print(('Would update: ' if args.dry_run else 'Update: ') + path)
        if not args.dry_run:
            write_plan(ROOT, changes)
        print("No release was published. Follow docs/releasing.md.")
    except (ValueError, OSError, KeyError, BadZipFile, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Release preflight failed: {error}\n")
