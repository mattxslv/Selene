"""Adds a batch of translations ("English = yours" lines) to a bundled language file in theme/Localization, keeping the
lines it already has (#106). Every English phrase must be one of theme/Localization/phrases.txt (run make_phrases.py
first); the ones that aren't are listed and left out, so a typo can't slip in.

    py -3 tools/merge_translation.py <batch.txt> <language file, e.g. fr.txt> [--dry]
"""
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOC = os.path.join(ROOT, "theme", "Localization")


def pairs(path):
    out = []
    for line in open(path, encoding="utf-8-sig"):
        line = line.rstrip("\r\n")
        if not line.strip() or line.lstrip().startswith("#") or " = " not in line:
            continue
        k, v = line.split(" = ", 1)
        out.append((k.strip(), v.strip()))
    return out


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    batch, target = sys.argv[1], os.path.join(LOC, sys.argv[2])
    dry = "--dry" in sys.argv
    known = {k for k, _ in pairs(os.path.join(LOC, "phrases.txt"))}
    lines = open(target, encoding="utf-8-sig").read().splitlines() if os.path.exists(target) else []
    have = {k for k, v in pairs(target) if v} if os.path.exists(target) else set()
    added, unknown = [], []
    for k, v in pairs(batch):
        if k not in known:
            unknown.append(k)
            continue
        if k in have or not v:
            continue
        added.append(k + " = " + v)
        have.add(k)
    for u in unknown:
        print("not a phrase: " + u)
    print("%d added to %s, %d not phrases" % (len(added), sys.argv[2], len(unknown)))
    if dry or not added:
        return 0
    if lines and lines[-1].strip():
        lines.append("")
    lines.append("# Added by tools/merge_translation.py")
    lines.extend(added)
    with open(target, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
