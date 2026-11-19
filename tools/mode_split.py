#!/usr/bin/env python3
"""The mode split: every tracked path has one value of the README's path table, so "what may an
adopter take?" has one answer and the tree keeps it true. Owner: integrator. Built in #1344, as the
first step of the check spec #1335 asks for; tools/check.sh runs it in both modes.

    python3 -I tools/mode_split.py

Why (#1335). The repository is used three ways: to develop the gateway, to prove it in this
project's own cell, and to adopt it. An adopter needs to know which paths it takes as they stand
(the unit of adoption), which it may copy and change (the references), and which it takes nothing
from (development, the proof cell and process). One table in README.md says so, and this check keeps
the table true of the tree, so a new path gets its value in the change that adds it.

It reads the tracked files of the repository it runs in, from the top level, through `git ls-files`,
and reads the table and the list below from the files at every run. It holds no path list of its own
(CODING_STANDARDS.md, "A guard reads the thing it guards").

**The table** is the rows between `<!-- BEGIN path values -->` and `<!-- END path values -->` in
README.md, under a `| Path | Value |` header. A row's Value cell is one of Development, Proof cell,
Unit of adoption, Reference or Process.

- A row's paths are the code spans in its Path cell, each read from the repository's root. A span
  that ends in `/`, or that names a tracked directory, covers every path under it; a `*` matches
  within one path segment and never across a `/`. The prose in a cell is not read.
- A row whose Path cell begins "The files of `contracts/`" names no path. Every path under
  `contracts/` takes its value from the list in contracts/README.md, and no row is read for it.
- In a row whose Path cell begins "Every other file directly in", its first span, a directory, is
  read as the files one segment deep in it: `tools/` is `tools/*`. A new directory under `tools/`
  therefore has no row until the table gives it one.
- The deeper row is the path's one row. One row is deeper than another when its span, up to its first
  `*`, extends the other's: `tools/scripts/poc/` is deeper than `tools/scripts/`, and
  `tools/aws-project*.env` than `tools/*`. When two rows match a path and neither is deeper, the path
  has two values.

**The list** is the lines between `<!-- BEGIN contracts list -->` and `<!-- END contracts list -->`
in contracts/README.md: two lead lines, "The unit's:" (Unit of adoption) and "The project's own:"
(Development), and under each one bullet per file or directory. A bullet's first code span is the
name, relative to `contracts/`, read as the table reads a span; a bullet that names a schema also
names its example ("with `….example.json`"), and the example takes the schema's value.

It asserts three clauses over the tree:

1. One value per tracked path: a path that no row matches fails, and so does a path that two rows
   match with neither deeper.
2. Every file of `contracts/` is listed: a tracked file under `contracts/` that the list does not
   name, directly or through a listed directory, fails, as does one it names under both lead lines.
3. A directory named `poc` holds only the proof cell's own: a tracked path with a segment named
   `poc` above its file name fails unless its value is Proof cell.

Each finding is a line on stderr, `mode-split: the tree: <path>: <what>`. The last line goes to stdout
and is the verdict, `mode-split: pass: <n> paths: <count per value>` or `mode-split: fail: ...`. Any
finding exits 1. A README.md or contracts/README.md with no marked table or list, a table row or a
list line the format above cannot read, or a listing that cannot be read or is empty exits 2: a check
that read nothing must not pass (CODING_STANDARDS.md, "A check fails closed").

Run its companion: python3 -I tools/mode_split_tests.py
"""
from __future__ import annotations

import pathlib
import re
import subprocess
import sys

ME = "mode-split"
TABLE_FILE = "README.md"
LIST_FILE = "contracts/README.md"
CONTRACTS = "contracts/"
TABLE_MARKERS = ("<!-- BEGIN path values -->", "<!-- END path values -->")
LIST_MARKERS = ("<!-- BEGIN contracts list -->", "<!-- END contracts list -->")
PROOF_CELL = "Proof cell"
VALUES = ("Development", PROOF_CELL, "Unit of adoption", "Reference", "Process")
LEADS = {"The unit's:": "Unit of adoption", "The project's own:": "Development"}
CONTRACTS_ROW = "The files of `contracts/`"
EVERY_OTHER = "Every other file directly in"
SPAN = re.compile(r"`([^`]+)`")


class Unreadable(Exception):
    """What the check could not read, so it judged nothing."""


class Span:
    """One code span of the table or the list, and the paths it covers."""

    def __init__(self, text: str, value: str) -> None:
        self.text = text
        self.value = value
        self.key = text.split("*", 1)[0]
        self.glob = None
        if "*" in text:
            self.glob = re.compile("".join("[^/]*" if c == "*" else re.escape(c) for c in text))

    def covers(self, path: str) -> bool:
        if self.glob is not None:
            return self.glob.fullmatch(path) is not None
        if self.text.endswith("/"):
            return path.startswith(self.text)
        return path == self.text or path.startswith(self.text + "/")

    def deeper(self, other: Span) -> bool:
        return len(self.key) > len(other.key) and self.key.startswith(other.key)


def git(*args: str) -> str:
    done = subprocess.run(["git", *args], capture_output=True, text=True, check=False)
    if done.returncode != 0:
        raise Unreadable(f"git {' '.join(args)} failed: {done.stderr.strip()}")
    return done.stdout


def between(root: pathlib.Path, name: str, markers: tuple[str, str], what: str) -> list[str]:
    try:
        text = (root / name).read_text()
    except OSError as error:
        raise Unreadable(f"{name} could not be read: {error}") from error
    begin, end = markers
    if text.count(begin) != 1 or text.count(end) != 1 or text.index(begin) > text.index(end):
        raise Unreadable(f"{name} has no {what} between {begin} and {end}")
    return text[text.index(begin) + len(begin):text.index(end)].splitlines()


def read_table(root: pathlib.Path) -> list[list[Span]]:
    """Each row's spans, in the table's order."""
    lines = [line.strip() for line in between(root, TABLE_FILE, TABLE_MARKERS, "path table") if line.strip()]
    if len(lines) < 3 or not all(line.startswith("|") and line.endswith("|") for line in lines):
        raise Unreadable(f"{TABLE_FILE}'s path table has a line that is not a table row, or no rows")
    cells = [[cell.strip() for cell in line[1:-1].split("|")] for line in lines]
    if cells[0] != ["Path", "Value"] or not all(set(cell) <= set("-: ") for cell in cells[1]):
        raise Unreadable(f"{TABLE_FILE}'s path table does not open with a | Path | Value | header")
    rows = []
    for row in cells[2:]:
        if len(row) != 2 or row[1] not in VALUES:
            raise Unreadable(f"{TABLE_FILE}'s path table has a row it cannot read: | {' | '.join(row)} |")
        path, value = row
        if path.startswith(CONTRACTS_ROW):
            continue
        spans = SPAN.findall(path)
        if not spans:
            raise Unreadable(f"{TABLE_FILE}'s path table has a row that names no path: | {path} | {value} |")
        if path.startswith(EVERY_OTHER):
            if not spans[0].endswith("/"):
                raise Unreadable(f"{TABLE_FILE}'s row \"{EVERY_OTHER}\" does not begin with a directory")
            spans[0] += "*"
        rows.append([Span(span, value) for span in spans])
    return rows


def read_list(root: pathlib.Path) -> list[Span]:
    """Each name the list gives under `contracts/`, with its value."""
    names: list[Span] = []
    value = None
    leads = set()
    for line in between(root, LIST_FILE, LIST_MARKERS, "contracts list"):
        line = line.strip()
        if not line:
            continue
        if line in LEADS:
            value = LEADS[line]
            leads.add(line)
            continue
        spans = SPAN.findall(line)
        if not line.startswith("- ") or value is None or not spans:
            raise Unreadable(f"{LIST_FILE}'s contracts list has a line it cannot read: {line}")
        named = [spans[0]] + re.findall(r"\bwith `([^`]+)`", line)
        names.extend(Span(CONTRACTS + name, value) for name in dict.fromkeys(named))
    if leads != set(LEADS):
        raise Unreadable(f"{LIST_FILE}'s contracts list lacks a lead line: {' '.join(sorted(set(LEADS) - leads))}")
    return names


def judge(path: str, rows: list[list[Span]], names: list[Span]) -> tuple[str | None, str | None]:
    """The path's value, or the finding that it has none or two."""
    if path.startswith(CONTRACTS):
        values = {name.value for name in names if name.covers(path)}
        if not values:
            return None, f"the list in {LIST_FILE} does not name it"
        if len(values) > 1:
            return None, f"the list in {LIST_FILE} names it under both lead lines"
        return values.pop(), None
    matches = []
    for row in rows:
        covering = [span for span in row if span.covers(path)]
        if covering:
            matches.append(max(covering, key=lambda span: len(span.key)))
    if not matches:
        return None, "no row of the path table matches it"
    for span in matches:
        if all(span is other or span.deeper(other) for other in matches):
            return span.value, None
    named = " and ".join(f"`{span.text}` ({span.value})" for span in matches)
    return None, f"rows match it and neither is deeper: {named}"


def main() -> int:
    findings = 0
    counts = dict.fromkeys(VALUES, 0)
    try:
        top = git("rev-parse", "--show-toplevel").strip()
        root = pathlib.Path(top)
        rows = read_table(root)
        names = read_list(root)
        tracked = sorted(path for path in git("-C", top, "ls-files", "-z").split("\0") if path)
        if not tracked:
            raise Unreadable("git ls-files listed no tracked path")
    except Unreadable as error:
        print(f"{ME}: {error}", file=sys.stderr)
        print(f"{ME}: fail: nothing was judged")
        return 2
    for path in tracked:
        value, finding = judge(path, rows, names)
        if finding is None and "poc" in path.split("/")[:-1] and value != PROOF_CELL:
            finding = f"it sits under a directory named poc, and its value is {value}, not {PROOF_CELL}"
        if finding is not None:
            findings += 1
            print(f"{ME}: the tree: {path}: {finding}", file=sys.stderr)
            continue
        counts[value] += 1
    if findings:
        print(f"{ME}: fail: {findings} of {len(tracked)} paths have no one value of the path table, or break its clauses")
        return 1
    tally = ", ".join(f"{count} {value.lower()}" for value, count in counts.items())
    print(f"{ME}: pass: {len(tracked)} paths: {tally}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
