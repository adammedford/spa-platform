#!/usr/bin/env python3
"""Checks the documents that steer a session: a claim here misleads before any code is read.

Owner: integrator.

Two of the documents are loaded into every context window -- AGENTS.md and CLAUDE.md -- one is
read by every workstream before it builds, the contracts index, and the last rule widens the scope
again: a ticket citation is a pointer, and any tracked file may carry one. Each rule below was a
prose paragraph first, and each paragraph was broken by the session after the one that wrote it.

**No progress.** AGENTS.md says of itself that it "does not track progress, because a copy of the
state goes stale and then misleads every session that loads it". The skeleton import wrote three
progress lines; a retrospective removed two, wrote that paragraph, and left the third sitting
directly beneath it, where it stayed for another session. The file is loaded from the top of every
agent's context window, so a stale claim there misleads from the first token. A paragraph asking
for the rule is not the same as a check enforcing it.

**No dangling pointer.** These files are mostly context pointers, and a pointer at something that
does not exist costs a session the search before it finds nothing. `docs/gates/` was named twice
while absent, and was later dropped rather than written. A path that is promised rather than broken
goes in PLANNED below, which fails once the path appears, so the list cannot outlive its entries.

**No pointer that contradicts its target.** The same defect one step on: a claim about the
repository that the repository denies. `contracts/README.md` carries a freeze gate per row and each
contract carries its own freeze header, and the Pipeline row once landed naming a gate its target
never froze at, contradicting `contracts/pipeline.md` two directories away. A cell may say more
than the header -- `G1a; stage order changed at G1b` -- so the comparison is on the leading gate
token, and a cell naming no gate at all -- `With the registry document` -- makes no claim of its
own and is checked through the row it defers to. A row this rule cannot read fails as well, and
the line it passes prints how many headers it compared: a rule that quietly compares nothing is
the defect it was written to catch, one level up.

**No citation that resolves to nothing.** The dangling-pointer rule again, for the one pointer
written as a number rather than a path. `docs/agents/issue-tracker.md` says a document citing a
ticket "is citing a file every other session and CI can open, which is the whole point of citing
it", and merged code once cited overnight tickets 63 and 64 while `origin/main` stopped at 61. A
number alone does not say where, so where it resolves is this rule's decision: against the tracker
directory the citing file sits in, because numbering restarts per tracker, and against the
plan-wide tracker for a file outside `.scratch/`, which is what every ADR and contract here means
by an unqualified citation. A document meaning another tracker names it -- `overnight ticket 53`.
One global number-to-file map would mis-resolve both readings. A ticket named before it is filed
goes in UNFILED below, on PLANNED's terms, and the line this rule passes prints how many citations
it followed, for the reason the freeze count is printed.

Run: python3 -I .github/steering_tests.py && python3 -I .github/steering.py
"""

from __future__ import annotations

import json
import pathlib
import re
import subprocess
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

# The contracts index, whose "Freezes at" column makes a claim about a file two directories away.
INDEX = "contracts/README.md"
CONTRACT_COLUMN = "Contract"
FILE_COLUMN = "File"
FREEZE_COLUMN = "Freezes at"

# A table row, and the cells in it. Every cell this rule reads is found by its column's heading, so
# a column moved or added changes nothing here and the file's other tables are not mistaken for
# this one. A pipe inside a cell is escaped, and splitting on it would shift every cell after it.
ROW = re.compile(r"^\s*\|(.*)\|\s*$")
CELL_BOUNDARY = re.compile(r"(?<!\\)\|")
DIVIDER = re.compile(r"^[\s:|-]+$")

# A gate is G0, G1a, G2. The leading one in a cell is the freeze it claims; anything after it is
# allowed to say more, as in `G1a; stage order changed at G1b`.
GATE = re.compile(r"\bG\d[a-z]?\b")

# The two header shapes in the tree. A markdown contract carries a bullet under its title; a JSON
# schema carries a sentence inside its top-level description. Each is read down to the sentence
# that states the freeze, because the bullet runs on into its rationale over several lines and a
# message quoting half a clause is a message its reader has to open the file to finish. The
# bullet's continuation lines are read with it, so a claim is found wherever the author's line
# happened to run out; a gate that went unread would be a disagreement that went unreported.
MD_FREEZE = re.compile(
    r"^-\s+\*\*Freezes at:\*\*(?P<freeze>[^\n]*(?:\n[ \t]+[^\n]*)*)", re.MULTILINE
)
SCHEMA_FREEZE = re.compile(r"[Ff]reezes at\s+(?P<freeze>G\d[a-z]?[^.]*)\.")

# The file column names each target as a markdown link, or as a backticked path when there is
# nothing to link to, as `src/Gateway/Program.cs`.
TARGET = re.compile(r"\]\(([^)]+)\)|`([A-Za-z0-9_][A-Za-z0-9_.-]*(?:/[A-Za-z0-9_.-]+)+)`")

# The tracker: one directory per feature under .scratch/, each holding `issues/<NN>-<slug>.md` and
# each numbering from 01 of its own. `overnight` is the plan-wide one, and the one a document
# outside .scratch/ means when it names none: every ADR and contract that cites a ticket cites it.
TRACKER_ROOT = ".scratch"
ISSUES = "issues"
DEFAULT_TRACKER = "overnight"

# A ticket number is one or two digits, and two things are refused before it is read. A third digit,
# because no ticket has one and `ticket 100` would otherwise resolve as ticket 10. And a dot before
# a digit, because `workstream 3's tickets 3.3, 3.5 and 3.9` cites plan task ids: read as ticket 3
# that line is a dangling citation in a settled file, and a rule red on a settled file is a rule
# switched off within the week.
TICKET_NUMBER = r"\d{1,2}(?!\d)(?!\.\d)"

# The gap inside a citation: horizontal space, or horizontal space across one line break. The
# prose here wraps near column 100, so a citation split over two lines is the ordinary case and not
# an edge: eight in the tree today, `Ticket\n49` in docs/adr/0015 among them. A scan that read one
# line at a time saw none of them, which is this rule's own silent pass -- the shape it exists to
# catch, missed by the shape of the scan. One line break and no more, so a heading ending in the
# word and a list item starting with a number are not joined into a citation neither wrote.
GAP = r"(?:[ \t]+|[ \t]*\n[ \t]*)"

# The three shapes the tree writes, and no more: `ticket NN`; `tickets NN and NN`, with `,` and `/`
# enumerating the same way; and `<tracker> ticket NN`, where the word before the citation names a
# tracker directory. A range -- `tickets 62-65`, `tickets 01 to 08` -- is read at its first number
# only, because its interior is not cited and expanding it would assert tickets nobody named.
# `\b` is here and not in the greps that found these shapes: BSD grep -E does not support it, and
# a pattern that silently matched nothing is how the dangling citation went unnoticed.
CITATION = re.compile(
    rf"(?:(?P<tracker>[A-Za-z][A-Za-z0-9-]*){GAP})?"
    rf"(?P<word>\btickets?){GAP}"
    rf"(?P<numbers>{TICKET_NUMBER}(?:{GAP}?(?:,|and|/){GAP}?{TICKET_NUMBER})*)",
    re.IGNORECASE,
)

# Tickets named before they are filed, keyed by the tracker and the two-digit number. PLANNED's
# rule one level over: this check fails once the ticket appears, so landing it forces the entry out
# rather than leaving a stale exemption behind. Empty is the expected state.
UNFILED: dict[tuple[str, str], str] = {}


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


def dangling_pointers(root: pathlib.Path) -> tuple[list[str], list[str]]:
    """The backticked paths in the steering files that resolve to nothing, and the PLANNED paths
    that have landed. Named for its pointers since dangling_tickets joined it: both rules are
    about a reference that resolves to nothing, and one of them is not about paths.
    """
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


COLUMNS = (CONTRACT_COLUMN, FILE_COLUMN, FREEZE_COLUMN)


def index_rows(text: str):
    """The rows of the one table carrying all three columns, as (line number, headers, cells).

    A row is recognised by its pipes, and which table it belongs to by the header row above it:
    contracts/README.md holds two other tables, and neither names these columns. A row whose cells
    do not line up with the headers is yielded too, because the caller reports it rather than
    passing over it in silence.
    """
    headers = None
    for number, line in enumerate(text.splitlines(), start=1):
        match = ROW.match(line)
        if not match:
            headers = None
            continue
        cells = [cell.strip() for cell in CELL_BOUNDARY.split(match.group(1))]
        if headers is None:
            if all(column in cells for column in COLUMNS):
                headers = cells
            continue
        if DIVIDER.match(line):
            continue
        yield number, headers, cells


def freeze_header(root: pathlib.Path, target: str) -> tuple[str, str] | None:
    """Where the target is and what it says about its own freeze, or None when it says nothing.

    A link in the index is relative to the index, and a backticked path is relative to the root,
    so both are tried. A row may point at source code or at a schema's example, and neither
    carries a header. Absence is not a disagreement: this rule compares two claims about a gate,
    and where there is no header there is only the index's.
    """
    # A link may carry a fragment -- `registry.schema.json#/$defs/oauth` -- which names a part of
    # the file, not another file.
    path = target.split("#", 1)[0]
    for candidate in (root / INDEX).parent / path, root / path:
        if not candidate.is_file():
            continue
        where = candidate.relative_to(root).as_posix()
        if candidate.suffix == ".md":
            found = MD_FREEZE.search(candidate.read_text())
            if not found:
                return None
            # The bullet's first sentence is the freeze; what follows it is the reason, and in
            # http-contract.md and request-gate.md it runs on past the end of the line.
            bullet = " ".join(found.group("freeze").split())
            sentence = bullet.split(". ")[0].strip().rstrip(".")
            return where, f"Freezes at: {sentence}"
        if candidate.suffix == ".json":
            try:
                document = json.loads(candidate.read_text())
            except ValueError:
                return None
            description = document.get("description", "") if isinstance(document, dict) else ""
            found = SCHEMA_FREEZE.search(description)
            return (where, found.group(0)) if found else None
    return None


def freeze_disagreements(root: pathlib.Path) -> tuple[list[str], list[str], int]:
    """The rows that contradict their target, the rows this rule cannot read, and how many
    headers it read to find out.

    The count is reported, so the line this check prints when it passes says how much it
    compared. A sentence that would be printed whether or not the comparison happened is a
    sentence no test can hold to anything. The unreadable rows are reported for the same reason:
    a row passed over in silence is a freeze claim nothing compares.
    """
    index = root / INDEX
    if not index.is_file():
        return [], [], 0

    found, unreadable, compared = [], [], 0
    for number, headers, cells in index_rows(index.read_text()):
        if len(cells) != len(headers):
            unreadable.append(
                f"{INDEX}:{number}: {len(cells)} cells in a {len(headers)}-column table: "
                f"{' | '.join(cells)}"
            )
            continue
        cells = dict(zip(headers, cells))
        name = cells[CONTRACT_COLUMN].strip("`")
        cell = cells[FREEZE_COLUMN]
        claimed = GATE.search(cell)
        if not claimed:
            # A relational cell -- "With the registry document" -- names no gate, so there is
            # nothing to disagree with. It defers to a row that this rule does check.
            continue
        for link, backticked in TARGET.findall(cells[FILE_COLUMN]):
            read = freeze_header(root, link or backticked)
            if read is None:
                continue
            where, header = read
            stated = GATE.search(header)
            if not stated:
                continue
            compared += 1
            if stated.group(0) != claimed.group(0):
                # Both sides in full, because the point of the message is that neither file has
                # to be opened to see which of the two is wrong.
                found.append(
                    f"{INDEX}:{number}: row '{name}': the index cell '{cell}' names "
                    f"{claimed.group(0)}, and {where}'s header '{header}' names "
                    f"{stated.group(0)}"
                )
    return found, unreadable, compared


def tracked_files(root: pathlib.Path) -> list[str]:
    """The files git tracks, which is the set a citation can be held to.

    Not a walk of the tree: `.scratch/<slug>/log/` is ignored on purpose -- a transcript, not a
    spec -- and a rule reading transcripts would fail a push over a line nobody can correct.
    Build output would be read too, and neither is a document that cites anything.
    """
    found = subprocess.run(
        ("git", "ls-files", "-z"), cwd=root, capture_output=True, text=True, check=False
    )
    if found.returncode != 0:
        # Not a repository, so nothing is tracked. The count this rule prints is what catches
        # that: zero citations read means no sentence, and the case asserting a floor goes red.
        return []
    return [name for name in found.stdout.split("\0") if name]


def trackers(root: pathlib.Path) -> set[str]:
    """The tracker directories a number can resolve in, which is what makes a word a tracker name.

    A word is read as naming a tracker only when the directory is there, so `the overnight
    grilling of ticket 02` -- where the word before the citation is `of` -- is not mistaken for a
    tracker named `of`, and `overnight ticket 53` is not mistaken for prose.

    A directory counts only when it holds an `issues/` directory. Three under .scratch/ do not --
    they are working notes, not trackers -- and reading them as trackers would resolve every
    unqualified citation in them against a directory that cannot exist, so the only way to write
    one would be to name another tracker. They fall through to the plan-wide tracker instead,
    which is what a document with no tracker of its own means everywhere else in the tree.
    """
    directory = root / TRACKER_ROOT
    if not directory.is_dir():
        return set()
    return {child.name for child in directory.iterdir() if (child / ISSUES).is_dir()}


def citing_tracker(relative: str, available: set[str]) -> str:
    """The tracker a citation in this file resolves against when it names none itself.

    Its own, when the file sits in one: numbering restarts per tracker, so a document inside
    `.scratch/<slug>/` citing `ticket 06` means its sibling. Otherwise the plan-wide tracker,
    which is what every ADR and contract in the tree means by an unqualified citation.
    """
    parts = pathlib.PurePosixPath(relative).parts
    if len(parts) > 1 and parts[0] == TRACKER_ROOT and parts[1] in available:
        return parts[1]
    return DEFAULT_TRACKER


def dangling_tickets(root: pathlib.Path) -> tuple[list[str], list[str], int]:
    """The citations that resolve to no file, the UNFILED entries that have been filed, and how
    many citations resolved.

    The count is reported for the reason the freeze count is: a sentence printed whether or not
    anything was read is a sentence no test can hold to anything. A number matching two files
    resolves -- the citation lands on a spec, which is all this rule promises -- because two files
    sharing a number is a different defect with a rule of its own.
    """
    available = trackers(root)
    missing, filed, resolved = [], [], 0

    for relative in tracked_files(root):
        try:
            text = (root / relative).read_text()
        except (OSError, UnicodeDecodeError):
            continue
        own = citing_tracker(relative, available)
        # Over the whole text rather than line by line, because a citation wraps where its
        # author's line ran out and not where its claim ends. The line is counted back from the
        # offset of the word itself, so the message names the line the citation starts on.
        for citation in CITATION.finditer(text):
            line_number = text.count("\n", 0, citation.start("word")) + 1
            named = (citation.group("tracker") or "").lower()
            tracker = named if named in available else own
            issues = root / TRACKER_ROOT / tracker / ISSUES
            for digits in re.findall(TICKET_NUMBER, citation.group("numbers")):
                number = f"{int(digits):02d}"
                if any(issues.glob(f"{number}-*")):
                    resolved += 1
                    continue
                if (tracker, number) in UNFILED:
                    continue
                missing.append(
                    f"{relative}:{line_number}: cites ticket {number}, which resolves to no "
                    f"file under {TRACKER_ROOT}/{tracker}/{ISSUES}/"
                )

    for (tracker, number), why in UNFILED.items():
        issues = root / TRACKER_ROOT / tracker / ISSUES
        if any(issues.glob(f"{number}-*")):
            filed.append(
                f"ticket {number} is filed under {TRACKER_ROOT}/{tracker}/{ISSUES}/ now ({why})"
            )

    return missing, filed, resolved


def report(lines: list[str], headline: str, *advice: str) -> bool:
    """Print one rule's failures under its own headline, and say whether there were any.

    Every rule here reports the same way -- each failing line indented, then the headline, then
    what to do about it -- and before this was one function it was the same three statements
    written out per rule. The fourth rule made that seven copies, and a fifth would edit a count,
    a condition and a block in three places.
    """
    for line in lines:
        print(f"  {line}")
    if not lines:
        return False
    print()
    print(headline)
    for line in advice:
        print(line)
    return True


def main() -> int:
    root = pathlib.Path(__file__).resolve().parent.parent

    claims = progress_claims(root)
    missing, landed = dangling_pointers(root)
    disagreements, unreadable, compared = freeze_disagreements(root)
    unresolved, filed, resolved = dangling_tickets(root)

    # A list and not a chain of `or`, because every rule reports before any of them decides the
    # exit code: a session fixing one failure should see the others in the same run.
    failures = [
        report(
            claims,
            "STEERING FAILED: AGENTS.md and CLAUDE.md state progress.",
            "They are loaded into every session from the top of the window, so a stale claim",
            "misleads from the first token. Point at the source instead: git tag, git log, the",
            "dispatch table, the gates table in docs/plan/01-plan.md.",
        ),
        report(
            missing,
            "STEERING FAILED: a pointer names something that does not exist.",
            "Write the target, fix the pointer, or add the path to PLANNED in this script with",
            "the reason it is named early.",
        ),
        report(
            landed,
            "STEERING FAILED: a PLANNED path has landed.",
            "Remove it from PLANNED in this script, so the list never outlives its entries.",
        ),
        report(
            disagreements,
            "STEERING FAILED: the contracts index and a contract's own freeze header disagree.",
            "One of the two is wrong, and a freeze cell is frozen state: decide which side the",
            "gate actually froze at, and correct that side through a change request under",
            "contracts/changes/ rather than editing the other to match.",
        ),
        report(
            unreadable,
            "STEERING FAILED: the contracts index has a row this rule cannot read.",
            "Its cells do not line up with the columns above it, so nothing compared its freeze",
            "claim against the contract it points at. Fix the row, or the headings it sits",
            "under; a pipe inside a cell is written \\|.",
        ),
        report(
            unresolved,
            "STEERING FAILED: a cited ticket number resolves to no file.",
            "A citation resolves against one tracker directory: the one the citing file sits in,",
            f"or {TRACKER_ROOT}/{DEFAULT_TRACKER}/{ISSUES}/ for a file outside {TRACKER_ROOT}/.",
            "Write '<tracker> ticket NN' to cite across trackers, file the ticket, or add it to",
            "UNFILED in this script with the reason it is named early. The resolution rule is",
            "in docs/agents/issue-tracker.md.",
        ),
        report(
            filed,
            "STEERING FAILED: a ticket on UNFILED has been filed.",
            "Remove it from UNFILED in this script, so the list never outlives its entries.",
        ),
    ]

    if any(failures):
        return 1

    summary = f"steering: {', '.join(FILES)} claim no progress and every pointer resolves."
    if compared:
        headers = "freeze header" if compared == 1 else "freeze headers"
        summary += f" {INDEX} agrees with the {compared} {headers} it indexes."
    if resolved:
        citations = "citation resolves" if resolved == 1 else "citations resolve"
        summary += f" {resolved} ticket {citations} to a spec."
    print(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
