#!/usr/bin/env python3
"""Whether a full-landing merge names a ticket that is still unfinished. Owner: integrator.

    python3 -I tools/landing_subjects.py --base <ref>

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

When gh is missing, not signed in, cannot reach GitHub or has no issue under the number, the
commit is reported unchecked with gh's own reason and nothing fails, as the steering check's issue
rule does offline (.github/steering.py): an offline push is not held up on it. A git that cannot
read the range judges nothing and fails (CODING_STANDARDS.md, "A check fails closed").

Each finding is a line `landing-subjects: held: ...`, `landing-subjects: unchecked: ...` or
`landing-subjects: note: ...`, and the last line is the verdict: `landing-subjects: pass: ...` with
exit 0, `landing-subjects: fail: ...` with exit 1 when a merge was held, and exit 2 when nothing was
judged. tools/check.sh runs it over the range it already judges, so the pre-push hook and the
overnight integrate gate, which runs the full check, refuse the subject before it is written into
history to stay.
"""
from __future__ import annotations

import argparse
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

# How gh reads one issue's body. gh fills the repository from the remote of the directory it runs
# in, and signs in from its keyring or from GH_TOKEN.
GH_ISSUE_BODY = ("gh", "issue", "view", "{number}", "--json", "body", "--jq", ".body")

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


def issue_body(number: int) -> tuple[str | None, str]:
    """Issue `number`'s body, or None with the reason gh could not give it."""
    command = [part.replace("{number}", str(number)) for part in GH_ISSUE_BODY]
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
    clear = unchecked = unticketed = held = 0
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
        if verdict == "held":
            held += 1
        elif verdict == "unchecked":
            unchecked += 1
        else:
            clear += 1
    if held:
        names = "merge names" if held == 1 else "merges name"
        it = "it" if held == 1 else "each"
        say(f"fail: {held} full-landing {names} a ticket with an open box; land {it} as "
            f'"Merge part of task" with its Outstanding lines, or finish the boxes.')
        return 1
    say(f"pass: {plural(len(landings), 'full-landing merge', 'full-landing merges')} from {base} to HEAD: {clear} clear, "
        f"{unchecked} unchecked, {unticketed} with no Ticket line.")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Hold a full-landing merge whose ticket has an open box.")
    parser.add_argument("--base", required=True, help="judge the commits from this ref to HEAD")
    arguments = parser.parse_args(argv)
    try:
        return judge(arguments.base)
    except Broken as error:
        say(f"fail: nothing judged: {error}.")
        return 2
    except Exception as error:  # Python's own exit for an uncaught error is 1, a held merge's code.
        say(f"fail: nothing judged: {type(error).__name__}: {error}.")
        return 2


if __name__ == "__main__":
    sys.exit(main())
