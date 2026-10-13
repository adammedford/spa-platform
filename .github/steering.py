#!/usr/bin/env python3
"""Checks the two files every session loads: AGENTS.md and CLAUDE.md. Owner: integrator.

Two rules, both of which a prose paragraph has already failed to hold.

**No progress.** AGENTS.md says of itself that it "does not track progress, because a copy of the
state goes stale and then misleads every session that loads it". The skeleton import wrote three
progress lines; a retrospective removed two, wrote that paragraph, and left the third sitting
directly beneath it, where it stayed for another session. The file is loaded from the top of every
agent's context window, so a stale claim there misleads from the first token. A paragraph asking
for the rule is not the same as a check enforcing it.

**No dangling pointer.** These files are mostly context pointers, and a pointer at something that
does not exist costs a session the search before it finds nothing. `docs/gates/` was named twice
while absent, and was later dropped rather than written. A path that is promised rather than broken goes in PLANNED below, which fails once
the path appears, so the list cannot outlive its entries.

Run: python3 -I .github/steering.py
"""

from __future__ import annotations

import pathlib
import re
import sys

FILES = ["AGENTS.md", "CLAUDE.md"]

# Paths named before they exist. Each is a planned artifact, and this check fails when one appears,
# so landing it forces the entry out rather than leaving a stale exemption behind.
PLANNED = {
    "docs/gaps.md": "workstream 6 owns it and has not started; the row declares ownership",
}

# Deliberately small, and aimed at the shapes the drift actually took rather than at every way a
# sentence could carry state. A false negative here is a missed line; a false positive is a check
# nobody trusts. Add a pattern when a new shape gets through, not in anticipation.
PROGRESS = [
    (re.compile(r"\b\d{4}-\d{2}-\d{2}\b"), "a date"),
    (
        re.compile(
            r"\b\d{1,2}\s+(January|February|March|April|May|June|July|August|September|October"
            r"|November|December)\s+\d{4}\b"
        ),
        "a date",
    ),
    (re.compile(r"^\s*-?\s*\*{0,2}Done:", re.MULTILINE), "a done list"),
    (re.compile(r"\bOpen on day one\b"), "a done list"),
    (re.compile(r"\bitems?\s+passed\b"), "a count of what passed"),
    (
        re.compile(
            r"\b(one|two|three|four|five|six|seven|eight|nine|ten|\d+)\s+of\s+"
            r"(its\s+|the\s+)?(one|two|three|four|five|six|seven|eight|nine|ten|\d+)\b"
        ),
        "a count of how far something got",
    ),
]

# A backticked token counts as a pointer only when it carries a slash, so it is relative to the
# repository root and resolvable. A bare name such as `Program.cs` or `GLOSSARY.md` is prose naming
# a file, not a path to it, and guessing where those live is how this check earns false positives.
PATHLIKE = re.compile(r"`([A-Za-z0-9_][A-Za-z0-9_.-]*(?:/[A-Za-z0-9_.-]*)+)`")


def progress_claims(root: pathlib.Path) -> list[str]:
    found = []
    for name in FILES:
        text = (root / name).read_text()
        for line_number, line in enumerate(text.splitlines(), start=1):
            for pattern, what in PROGRESS:
                if pattern.search(line):
                    found.append(f"{name}:{line_number}: {what}: {line.strip()}")
                    break
    return found


def dangling(root: pathlib.Path) -> tuple[list[str], list[str]]:
    missing, landed = [], []
    seen = set()

    for name in FILES:
        text = (root / name).read_text()
        for line_number, line in enumerate(text.splitlines(), start=1):
            for path in PATHLIKE.findall(line):
                seen.add(path)
                if (root / path).exists():
                    continue
                if path in PLANNED:
                    continue
                missing.append(f"{name}:{line_number}: points at '{path}', which does not exist")

    for path, why in PLANNED.items():
        if (root / path).exists():
            landed.append(f"'{path}' exists now ({why})")

    return missing, landed


def main() -> int:
    root = pathlib.Path(__file__).resolve().parent.parent

    claims = progress_claims(root)
    missing, landed = dangling(root)

    for line in claims:
        print(f"  {line}")
    if claims:
        print()
        print("STEERING FAILED: AGENTS.md and CLAUDE.md state progress.")
        print("They are loaded into every session from the top of the window, so a stale claim")
        print("misleads from the first token. Point at the source instead: git tag, git log, the")
        print("dispatch table, the gates table in docs/plan/01-plan.md.")

    for line in missing:
        print(f"  {line}")
    if missing:
        print()
        print("STEERING FAILED: a pointer names something that does not exist.")
        print("Write the target, fix the pointer, or add the path to PLANNED in this script with")
        print("the reason it is named early.")

    for line in landed:
        print(f"  {line}")
    if landed:
        print()
        print("STEERING FAILED: a PLANNED path has landed.")
        print("Remove it from PLANNED in this script, so the list never outlives its entries.")

    if claims or missing or landed:
        return 1

    print(f"steering: {', '.join(FILES)} claim no progress and every pointer resolves.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
