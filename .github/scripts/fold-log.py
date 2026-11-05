#!/usr/bin/env python3
"""Folds the log pasted into the bug form, so a report doesn't turn into a page thousands of lines long.

Reads the issue text from stdin. Prints the new text, or nothing when it should stay as it is.
"""
import re
import sys

HEADING = "### Selene's log (required)"
MIN_CHARS = 1500       # shorter than this reads fine as it is (a link to an attached file, "_No response_")
MAX_CHARS = 65000      # what GitHub takes for an issue, with some room


def fold(body):
    start = body.find(HEADING)
    if start < 0 or "<details" in body:
        return None
    text_start = start + len(HEADING)
    following = re.search(r"^### ", body[text_start:], re.M)
    text_end = text_start + following.start() if following else len(body)
    log = body[text_start:text_end].strip()
    if len(log) < MIN_CHARS:
        return None
    longest = max((len(m) for m in re.findall(r"`+", log)), default=0)
    fence = "`" * max(4, longest + 1)
    lines = log.count("\n") + 1
    folded = (
        f"{HEADING}\n\n<details><summary>Show the log ({lines} lines)</summary>\n\n"
        f"{fence}text\n{log}\n{fence}\n\n</details>\n\n"
    )
    result = body[:start] + folded + body[text_end:]
    return result if len(result) <= MAX_CHARS else None


if __name__ == "__main__":
    new = fold(sys.stdin.read())
    if new is not None:
        sys.stdout.write(new)