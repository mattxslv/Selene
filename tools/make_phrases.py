"""Every English phrase Selene shows, gathered from the source into theme/Localization/phrases.txt, one per line with
" = " after it, ready to translate (#106). Translators no longer have to walk every screen with "Collect words to
translate" on to find them; that switch still catches the few phrases built at run time.

    py -3 tools/make_phrases.py          writes theme/Localization/phrases.txt
    py -3 tools/make_phrases.py check    says how much of each bundled language is translated

Phrases are found where Selene puts words on screen: Text / Label / About / Sub / Hint values, button hints
(Prompt("A", "...")), notices, dialogs, setting choices (Names = new[] { ... }), and Text= in the theme's views.
"""
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "theme", "Localization", "phrases.txt")
STR = r'"((?:[^"\\]|\\.)*)"'

CS_PATTERNS = [
    r'\b(?:Text|Label|About|Sub|Hint|Title|Headline|Note|Placeholder)\s*=\s*' + STR,
    r'\bPrompt\(\s*"[^"]*"\s*,\s*' + STR,
    r'\bActionPill\(\s*"[^"]*"\s*,\s*' + STR,
    r'\bNotice\(\s*' + STR,
    r'\bLang\.T\(\s*' + STR,
    r'\bnew Dialog\([^;]*?,\s*' + STR + r'\s*,\s*' + STR,
    r'\bShowEmpty\(\s*' + STR,
    r'\btitle\s*=\s*' + STR,
    r'\bhint\s*=\s*' + STR,
    r'\b(?:Switch|Slider|Choice)\(\s*"[^"]*"\s*,\s*' + STR + r'\s*,\s*' + STR,   # Selene's Playnite settings rows
    r'\bSection\s*\{\s*Name\s*=\s*' + STR,
    r'new object\[\]\s*\{\s*0x[0-9a-fA-F]+\s*,\s*' + STR,                       # icon + label lists (the power menu)
    r'\.Text\s*=\s*[^;"]*\?\s*' + STR + r'\s*:\s*' + STR,                     # x.Text = cond ? "Yes" : "Not yet"
    r'\b(?:SearchGroup|ChangedGroup)\s*=\s*' + STR,                            # the settings sidebar's first two
    r'new\s*\[\]\s*\{\s*' + STR + r'\s*,\s*"[a-z][a-z0-9]+"',                 # a settings section's title before its keys
]
NAMES = re.compile(r'\bNames\s*=\s*new\s*\[\]\s*\{([^}]*)\}')
XAML = re.compile(r'\b(?:Text|Value|Content)="([^"{][^"]*)"')


def unescape(s):
    return s.replace('\\"', '"').replace("\\n", " ").replace("\\u2014", "—").replace("\\u2019", "’").replace("\\u00b7", "·").replace("\\\\", "\\")


def wanted(s):
    s = s.strip()
    if len(s) < 2 or len(s) > 400 or not re.search(r"[A-Za-z]{2}", s):
        return False
    if re.match(r"^[a-z0-9_\-:./]+$", s):      # keys, ids, paths, file names
        return False
    if re.search(r"\\u[0-9a-fA-F]{4}", s) or "&#x" in s or s.startswith(("http", "pack:", "#", "Segoe", "/")):
        return False
    if re.search(r"\.(png|jpg|wav|mp4|json|txt|xaml|dll|exe|ttf)\b", s):
        return False
    if s in XAML_WORDS or "??" in s or re.match(r"^\+ ", s):   # property values and code, not words on screen
        return False
    if re.match(r"^Selene[A-Z][A-Za-z]+$", s):   # window and element names
        return False
    return True


XAML_WORDS = {"True", "False", "Center", "Visible", "Collapsed", "SemiBold", "Bold", "Normal", "Horizontal", "Vertical",
              "Top", "Bottom", "Left", "Right", "Wrap", "NoWrap", "Transparent", "Auto", "Uniform", "UniformToFill",
              "Aa", "Selene", "Playnite", "Xbox", "PlayStation", "Game Pass", "HDR", "English", "Deutsch", "Español", "Français",
              "Português (Brasil)", "Filipino", "日本語", "简体中文"}


def gather():
    found = []
    seen = set()

    def add(s):
        s = unescape(s).strip()
        if wanted(s) and s not in seen:
            seen.add(s)
            found.append(s)

    files = sorted(glob.glob(os.path.join(ROOT, "SeleneCore", "*.cs")) + glob.glob(os.path.join(ROOT, "SeleneCore", "src", "*.cs")))
    for f in files:
        src = open(f, encoding="utf-8").read()
        for p in CS_PATTERNS:
            for m in re.finditer(p, src):
                for g in m.groups():
                    if g is not None:
                        add(g)
        for m in NAMES.finditer(src):
            for g in re.findall(STR, m.group(1)):
                add(g)
    for f in sorted(glob.glob(os.path.join(ROOT, "theme", "Views", "*.xaml"))):
        for m in XAML.finditer(open(f, encoding="utf-8").read()):
            add(m.group(1))
    return found


def read_language(path):
    table = {}
    for line in open(path, encoding="utf-8-sig"):
        line = line.strip()
        if not line or line.startswith("#") or " = " not in line:
            continue
        k, v = line.split(" = ", 1)
        if v.strip():
            table[k.strip()] = v.strip()
    return table


def main():
    phrases = gather()
    if len(sys.argv) > 1 and sys.argv[1] == "check":
        for f in sorted(glob.glob(os.path.join(ROOT, "theme", "Localization", "*.txt"))):
            name = os.path.basename(f)
            if name in ("phrases.txt", "README.txt"):
                continue
            t = read_language(f)
            done = sum(1 for p in phrases if p in t)
            print("%-10s %4d of %d (%d%%)" % (name, done, len(phrases), 100 * done // max(1, len(phrases))))
        return
    with open(OUT, "w", encoding="utf-8", newline="\n") as out:
        out.write("# Selene's phrases (made by tools/make_phrases.py). To translate: copy this file, name it after your language\n")
        out.write("# (de.txt, pt_BR.txt...), write your words after each \" = \" and save it as UTF-8. Lines left empty stay English.\n")
        out.write("# Put it in Selene Core's data folder (see README.txt) so a theme update leaves it alone.\n")
        for p in phrases:
            out.write(p + " = \n")
    print("%d phrases -> %s" % (len(phrases), os.path.relpath(OUT, ROOT)))


if __name__ == "__main__":
    main()
