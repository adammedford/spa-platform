#!/usr/bin/env python3
"""Whether a full-landing merge names a ticket or a plan row that is still unfinished. Owner: integrator.

    python3 -I tools/landing_subjects.py --base <ref>
    python3 -I tools/landing_subjects.py --subject-for <id> --ticket <n>

A merge whose subject reads "Merge task <id>: ..." or "Merge tasks <ids>: ..." says its row landed
whole, and tools/roadmap.py reads that subject anywhere in the history as landed and never takes it
back. On 2026-10-22 the 6.7 lane reported itself complete while #62 still held two open boxes, so
the overnight integrate step wrote "Merge task 6.7"; the roadmap then said S7 owed nothing.
Correcting it took two review rounds, two rebuilds of the run branch and a force-push, and once on
main it could not have been corrected at all (#287, #296).

This reads every commit from the base to HEAD, whatever its parents, and judges each whose subject
is a full landing, by the same reading as roadmap.py's LANDED_SUBJECT less its "part of" branch:
for each line of its message that starts `Ticket: #<n>` (the line the overnight run and
docs/agents/landing-work.md put in a merge body), it reads issue n's body through gh and holds the
commit when that body has an open `- [ ]` box, naming the commit, the issue and each open box by
its place among the body's boxes. A "Merge part of task" subject is never judged: it says the row is
unfinished, and tools/check.sh holds it to its Outstanding lines instead (#291). A full-landing
subject with no Ticket line is never judged either; it is named in a note, since the merges from
before the tracker moved to GitHub carry none.

A full landing is held on a second ground too (#388). On 2026-10-24 the run wrote "Merge task P2-2"
over #340, all its boxes ticked, directly after "Merge part of task issue-338", whose ticket and
#342 both declared themselves part of P2-2 and were open; "Merge task P2-1" over #344 landed the
same way through #385. #332 D7 says a carrier merges whole only when it is its row's last, but
nothing read the relation, which lived in an issue body. So, for each plan row a full-landing
subject names (an id in tools/roadmap.py's ID form, never `issue-<n>`), it lists the open issues
through gh, once for the run, and holds the commit while one, other than an issue on the commit's
own Ticket lines, declares itself part of that row and has not itself landed whole below the commit
("Merge task issue-<n>" in the commit's history; within one run a part's issue stays open until the
morning's pull request reaches main). A part ticket declares itself in its Plan row line alone, in
the form #388 Q5 fixed from the live tickets:

    **Plan row:** none (part of <row id>[, <row id> ... and <row id>][; prose])

with the row ids read up to the first `;` or `)`; any other `none` line declares nothing.

--subject-for asks the same question before a merge is written, for overnight.js's integrate step,
whose lane is complete: given the id the lane landed and its issue, it prints the subject's head,
judged on HEAD's history. A plan row with an open part ticket gets "Merge part of task <id>", and
each open part is named on an `open part:` line, for the merge body's Outstanding line. A part ticket
names, beside its own id, each row it declares that HEAD's history has landed in part and never
whole, when no other part of it is open: the merge of a row's last open part names the row too
(#388 Q6), "Merge tasks issue-417 and P2-1". A row already landed whole is never named again, so the
rule runs forward only. Anything else gets "Merge task <id>", as before.

When gh is missing, not signed in, cannot reach GitHub or has no issue under the number, the
commit is reported unchecked with gh's own reason and nothing fails, as the steering check's issue
rule does offline (.github/steering.py): an offline push is not held up on it, and --subject-for
answers as the lane's completeness alone would. A git that cannot read the range judges nothing and
fails (CODING_STANDARDS.md, "A check fails closed").

Each finding is a line `landing-subjects: held: ...`, `landing-subjects: unchecked: ...` or
`landing-subjects: note: ...`, and the last line is the verdict: `landing-subjects: pass: ...` with
exit 0, `landing-subjects: fail: ...` with exit 1 when a merge was held, and exit 2 when nothing was
judged. --subject-for's last line is `landing-subjects: subject: <head>`, with exit 0, or exit 2
when it could not read the history. tools/check.sh runs the range check over the range it already
judges, so the pre-push hook and the overnight integrate gate, which runs the full check, refuse the
subject before it is written into history to stay.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys

# A full landing, read as tools/roadmap.py's LANDED_SUBJECT reads one, without its "part of" branch:
# "Merge task 6.7: ...", "Merge tasks 1.9 and issue-238: ...", in any case.
FULL_LANDING = re.compile(r"^Merge\s+tasks?\s+(.+?):", re.IGNORECASE)

# The line that names the issue a merge came from (ADR-0020 decision 4). It must start a line; a
# mention mid-sentence is not the line.
TICKET = re.compile(r"(?m)^Ticket:[ \t]*#(\d+)[ \t]*$")

# Every box, ticked or not, so an open one can be named by its place: "box 2". An open box is
# tools/ticket_shape.py's OPEN_BOX: `- [ ]` starting a line, indented or not.
BOX = re.compile(r"(?m)^[ \t]*- \[(?P<mark>[ xX])\][ \t]*(?P<text>[^\n]*)$")

# Either subject form, as tools/roadmap.py's LANDED_SUBJECT reads it; group 1 is set on a part. This
# script runs under `python3 -I`, which leaves its own directory off sys.path, so roadmap.py's forms
# are copied here rather than imported: ID, TICKET_ID and the token split below are its ID, TICKET
# and reading()'s split.
ANY_LANDING = re.compile(r"^Merge\s+(part of\s+)?tasks?\s+(.+?):", re.IGNORECASE)
ID = re.compile(r"^(?:I-\d+|\d+\.\d+[a-z]?|P2-\d+)$")
TICKET_ID = re.compile(r"^issue-(\d+)$", re.IGNORECASE)
TOKENS = re.compile(r"[,\s]+and[,\s]+|[,\s]+")

# A part ticket's declaration (#388 Q5): its Plan row line, `none (part of <ids>[; prose])`, the ids
# read up to the first `;` or `)`.
PART_OF = re.compile(r"(?m)^\*\*Plan row:\*\*[ \t]*none[ \t]*\([ \t]*part of[ \t]+([^;)\n]*)")

# How gh reads one issue's body, and lists the open issues with theirs. gh fills the repository from
# the remote of the directory it runs in, and signs in from its keyring or from GH_TOKEN. The limit is
# above the repository's issue count, open or closed, by a margin.
GH_ISSUE_BODY = ("gh", "issue", "view", "{number}", "--json", "body", "--jq", ".body")
GH_OPEN_ISSUES = ("gh", "issue", "list", "--state", "open", "--limit", "2000", "--json", "number,body")

# How long to wait for gh, or for git's listing of the range, before saying so, as
# .github/steering.py waits for gh: the hook runs this on every push.
TIMEOUT = 60

PREFIX = "landing-subjects:"


class Broken(Exception):
    """The check could not judge the range."""


def say(line: str) -> None:
    print(f"{PREFIX} {line}", flush=True)


def commits(base: str) -> list[tuple[str, str, str]]:
    """Each commit from `base` to HEAD, oldest first, as (short sha, subject, message)."""
    try:
        listed = subprocess.run(
            ["git", "log", "--reverse", "--format=%h%x1f%s%x1f%B%x1e", f"{base}..HEAD", "--"],
            capture_output=True, text=True, timeout=TIMEOUT)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise Broken(f"git log could not run: {error}") from error
    if listed.returncode != 0:
        said = listed.stderr.strip().splitlines()
        raise Broken(f"git log failed on {base}..HEAD: {said[0] if said else listed.returncode}")
    found = []
    for record in listed.stdout.split("\x1e"):
        record = record.strip("\n")
        if not record:
            continue
        sha, subject, message = record.split("\x1f", 2)
        found.append((sha, subject, message))
    return found


def history(rev: str) -> list[str]:
    """The subject of every commit reachable from `rev`."""
    try:
        listed = subprocess.run(["git", "log", "--format=%s", rev, "--"],
                                capture_output=True, text=True, timeout=TIMEOUT)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise Broken(f"git log could not run: {error}") from error
    if listed.returncode != 0:
        said = listed.stderr.strip().splitlines()
        raise Broken(f"git log failed on {rev}: {said[0] if said else listed.returncode}")
    return listed.stdout.splitlines()


def ids(subject: str) -> tuple[bool, list[str]] | None:
    """(whether it is a part, the ids it names) for a landing subject, or None for any other."""
    match = ANY_LANDING.match(subject)
    if not match:
        return None
    named = [token for token in TOKENS.split(match.group(2)) if ID.match(token) or TICKET_ID.match(token)]
    return bool(match.group(1)), [name.lower() if TICKET_ID.match(name) else name for name in named]


def states(subjects: list[str]) -> dict[str, str]:
    """Each id these subjects land: 'landed' when one names it whole, else 'part'."""
    found: dict[str, str] = {}
    for subject in subjects:
        read = ids(subject)
        if read is None:
            continue
        part, named = read
        for name in named:
            if not part:
                found[name] = "landed"
            else:
                found.setdefault(name, "part")
    return found


def rows(named: list[str]) -> list[str]:
    return [name for name in named if ID.match(name)]


def gh(command: list[str]) -> tuple[str | None, str]:
    """gh's standard output, or None with the reason gh could not give it."""
    try:
        asked = subprocess.run(command, capture_output=True, text=True, timeout=TIMEOUT)
    except FileNotFoundError:
        return None, "gh is not on PATH"
    except subprocess.TimeoutExpired:
        return None, f"gh did not answer within {TIMEOUT} seconds"
    except OSError as error:
        return None, f"gh could not run: {error}"
    if asked.returncode != 0:
        said = [line for line in asked.stderr.splitlines() if line.strip()]
        return None, said[0].strip() if said else f"gh exited {asked.returncode} and said nothing"
    return asked.stdout, ""


def issue_body(number: int) -> tuple[str | None, str]:
    """Issue `number`'s body, or None with the reason gh could not give it."""
    return gh([part.replace("{number}", str(number)) for part in GH_ISSUE_BODY])


def declared(body: str) -> list[str]:
    """The plan rows a body's Plan row line declares it part of (#388 Q5); none for any other line."""
    match = PART_OF.search(body)
    return rows(TOKENS.split(match.group(1).strip())) if match else []


class Parts:
    """The open issues that declare themselves part of a plan row, listed once, when first asked."""

    def __init__(self):
        self.listed: dict[int, list[str]] | None = None
        self.why = ""
        self.asked = False

    def of(self, row: str) -> list[int] | None:
        """The open issues declaring themselves part of `row`, or None when gh could not list them."""
        if not self.asked:
            self.asked = True
            text, self.why = gh(list(GH_OPEN_ISSUES))
            if text is not None:
                try:
                    self.listed = {int(issue["number"]): declared(issue.get("body") or "")
                                   for issue in json.loads(text)}
                except (ValueError, TypeError, KeyError) as error:
                    self.why = f"gh's listing could not be read: {error}"
        if self.listed is None:
            return None
        return sorted(number for number, declares in self.listed.items() if row in declares)


def open_parts(parts: Parts, row: str, own: set[int], landed: dict[str, str]) -> list[int] | None:
    """The part tickets of `row` still owed: open, not `own`, and not landed whole in `landed`."""
    found = parts.of(row)
    if found is None:
        return None
    return [number for number in found
            if number not in own and landed.get(f"issue-{number}") != "landed"]


def open_boxes(body: str) -> list[tuple[int, str]]:
    """Each open box in `body`, as (its place among all the boxes, from 1; its text)."""
    return [(place, box.group("text").strip())
            for place, box in enumerate(BOX.finditer(body), start=1) if box.group("mark") == " "]


def plural(count: int, one: str, many: str) -> str:
    return f"{count} {one if count == 1 else many}"


def judge(base: str) -> int:
    landings = [(sha, subject, message) for sha, subject, message in commits(base)
                if FULL_LANDING.match(subject)]
    if not landings:
        say(f"pass: no full-landing merge from {base} to HEAD.")
        return 0
    bodies: dict[int, tuple[str | None, str]] = {}
    parts = Parts()
    clear = unchecked = unticketed = held = on_box = on_part = 0
    for sha, subject, message in landings:
        numbers = [int(found) for found in TICKET.findall(message)]
        if not numbers:
            say(f'note: {sha} "{subject}" has no "Ticket: #n" line, so it was not judged.')
            unticketed += 1
            continue
        verdict = "clear"
        for number in numbers:
            if number not in bodies:
                bodies[number] = issue_body(number)
            body, why = bodies[number]
            if body is None:
                say(f'unchecked: {sha} "{subject}": #{number} could not be read: {why}')
                if verdict == "clear":
                    verdict = "unchecked"
                continue
            boxes = open_boxes(body)
            if boxes:
                named = ", ".join(f'box {place} "{text}"' for place, text in boxes)
                say(f'held: {sha} "{subject}" lands #{number} whole, but #{number} has '
                    f'{plural(len(boxes), "open box", "open boxes")}: {named}')
                verdict = "held"
        boxed = verdict == "held"
        parted = False
        named_rows = rows(ids(subject)[1])
        landed = states(history(sha)) if named_rows else {}
        for row in named_rows:
            owed = open_parts(parts, row, set(numbers), landed)
            if owed is None:
                say(f'unchecked: {sha} "{subject}": the open part tickets of {row} could not be listed: '
                    f"{parts.why}")
                if verdict == "clear":
                    verdict = "unchecked"
                continue
            for number in owed:
                say(f'held: {sha} "{subject}" lands {row} whole, but #{number} is open and declares itself '
                    f"part of {row}")
                verdict = "held"
                parted = True
        if verdict == "held":
            held += 1
            on_box += boxed
            on_part += parted
        elif verdict == "unchecked":
            unchecked += 1
        else:
            clear += 1
    if held:
        names = "merge names" if held == 1 else "merges name"
        it = "it" if held == 1 else "each"
        if not on_part:
            say(f"fail: {held} full-landing {names} a ticket with an open box; land {it} as "
                f'"Merge part of task" with its Outstanding lines, or finish the boxes.')
        elif not on_box:
            say(f"fail: {held} full-landing {names} a plan row an open part ticket declares; land {it} as "
                f'"Merge part of task" with an Outstanding line naming the part tickets, or land them first.')
        else:
            merges = "merge" if held == 1 else "merges"
            say(f"fail: {held} full-landing {merges} held: {on_box} on an open box, {on_part} on an open part "
                f'ticket; land {it} as "Merge part of task" with its Outstanding lines.')
        return 1
    say(f"pass: {plural(len(landings), 'full-landing merge', 'full-landing merges')} from {base} to HEAD: {clear} clear, "
        f"{unchecked} unchecked, {unticketed} with no Ticket line.")
    return 0


def subject_for(id_: str, number: int) -> int:
    """The head of a complete lane's merge subject, judged on HEAD's history and the open issues."""
    own = id_.lower() if TICKET_ID.match(id_) else id_
    landed = states(history("HEAD"))
    parts = Parts()
    named = [own]
    if TICKET_ID.match(own):
        # A part ticket names each row it declares that is landed in part, never whole, once no
        # other part of it is open (#388 Q6).
        body, why = issue_body(number)
        if body is None:
            say(f"unchecked: #{number} could not be read, so no row it is part of is named: {why}")
        for row in declared(body or ""):
            if landed.get(row) != "part":
                continue
            owed = open_parts(parts, row, {number}, landed)
            if owed is None:
                say(f"unchecked: the open part tickets of {row} could not be listed, so it is not named: "
                    f"{parts.why}")
            elif not owed:
                named.append(row)
    owed_by = {}
    for row in rows(named[:1]):
        owed = open_parts(parts, row, {number}, landed)
        if owed is None:
            say(f"unchecked: the open part tickets of {row} could not be listed: {parts.why}")
        elif owed:
            owed_by[row] = owed
    if owed_by:
        for row, owed in owed_by.items():
            for part in owed:
                say(f"open part: #{part} declares itself part of {row}")
        say(f"subject: Merge part of task {own}")
        return 0
    if len(named) == 1:
        say(f"subject: Merge task {own}")
    else:
        say(f"subject: Merge tasks {', '.join(named[:-1])} and {named[-1]}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Hold a full-landing merge whose ticket has an open box or whose plan row has an open "
                    "part ticket; or say which subject a complete lane's merge takes.")
    asked = parser.add_mutually_exclusive_group(required=True)
    asked.add_argument("--base", help="judge the commits from this ref to HEAD")
    asked.add_argument("--subject-for", metavar="ID", help="the id a complete lane landed: a plan row or issue-<n>")
    parser.add_argument("--ticket", type=int, help="with --subject-for: the lane's issue number")
    arguments = parser.parse_args(argv)
    if arguments.subject_for is not None and arguments.ticket is None:
        parser.error("--subject-for needs --ticket")
    try:
        if arguments.subject_for is not None:
            return subject_for(arguments.subject_for, arguments.ticket)
        return judge(arguments.base)
    except Broken as error:
        say(f"fail: nothing judged: {error}.")
        return 2
    except Exception as error:  # Python's own exit for an uncaught error is 1, a held merge's code.
        say(f"fail: nothing judged: {type(error).__name__}: {error}.")
        return 2


if __name__ == "__main__":
    sys.exit(main())
