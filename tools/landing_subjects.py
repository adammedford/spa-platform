#!/usr/bin/env python3
"""Whether a full-landing merge names a ticket or a plan row that is still unfinished, or a `closing`
clause closes a row an open issue still names. Owner: integrator.

    python3 -I tools/landing_subjects.py --base <ref>
    python3 -I tools/landing_subjects.py --subject-for <id> --ticket <n>
    python3 -I tools/landing_subjects.py --closing <row>[,<row>...]

A merge whose subject reads "Merge task <id>: ..." or "Merge tasks <ids>: ..." says its row landed
whole, and tools/roadmap.py reads that subject anywhere in the history as landed and never takes it
back. On 2026-10-22 the 6.7 lane reported itself complete while #62 still held two open boxes, so
the overnight integrate step wrote "Merge task 6.7"; the roadmap then said S7 owed nothing.
Correcting it took two review rounds, two rebuilds of the run branch and a force-push, and once on
main it could not have been corrected at all (#287, #296).

This reads every commit from the base to HEAD, whatever its parents, and judges each whose subject
is a full landing, by the same reading as roadmap.py's LANDED_SUBJECT less its "part of" branch:
for each line of its message that starts `Ticket: #<n>`, it reads issue n's body through gh and
holds the commit when that body has an open `- [ ]` box, naming the commit, the issue and each open
box by its place among the body's boxes. That line is written by overnight.js's integrate step, into
the body of each task merge it makes on the run branch; docs/agents/landing-work.md calls that
merge the ticket's merge body in a run-branch landing. A foreground landing carries no such line in
its merge: the guide puts its tickets in the pull request description, and tools/land.sh merges it
on the server, so that merge is never in a range this reads (#897). A "Merge part of task" subject
is never judged: it says the row is unfinished, and tools/check.sh holds it to its Outstanding lines
instead (#291). A full-landing subject with no Ticket line is never judged either; it is named in a
note, since the merges from before the tracker moved to GitHub carry none. A line inside a fenced
block the body quotes is no box of the ticket and takes no place (#1402): on 2026-11-18
--subject-for made #1215 a part on the two boxes of a template it quotes, and the session wrote the
full subject by hand.

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
each open part is named on an `open part:` line, for the merge body's Outstanding line. The ticket's
own open boxes make the subject a part too, as the range check would hold a full landing over them
(#660): each is named on an `open box: <place> <its first line>` line, for the same Outstanding
line, and a ticket whose every box is ticked answers as it did. A part ticket with no open box
names, beside its own id, each row it declares that HEAD's history has landed in part and never
whole, when no other part of it is open: the merge of a row's last open part names the row too
(#388 Q6), "Merge tasks issue-417 and P2-1". A row already landed whole is never named again, so the
rule runs forward only. Anything else gets "Merge task <id>", as before. When the ticket cannot be
read, its boxes are reported unchecked and the answer is the one its rows alone give.

A ticket body gh cannot read, or a gh that is missing, not signed in or cannot reach GitHub, is
reported unchecked with gh's own reason and nothing fails, as the steering check's issue rule does
offline (.github/steering.py): an offline push is not held up on it, and --subject-for answers as
the lane's completeness alone would. --fail-unchecked, which tools/check.sh --full passes and the
pre-push hook does not, makes an unchecked commit fail the range check instead (#894): the full
check is what a lane and the overnight integrate gate run, and there a pass over a ticket it could
not read would let 2026-10-22's open-box "Merge task" through whenever gh is signed out or GitHub is
down, where a red gate only waits for GitHub. The open-issue listing has a different obligation: it
establishes that no other part ticket holds a row, so it must be complete (#871). gh reads every
GraphQL page, and the reader checks the pagination, distinct issue numbers and stable totalCount.
A failed, incomplete or malformed listing cannot clear a row or choose its full-landing subject;
it exits 2. A git that cannot read the range likewise fails (CODING_STANDARDS.md, "A check fails
closed").

A row can end in a sitting, whose last part is records on a ticket, so no merge is left to name it
whole, and the next merge names it after its own ids, behind the word `closing`, under either head:
"Merge task issue-1219, closing rows 1.3 and 6.9: ...", "Merge part of task 2.4, closing row 6.9:
..." (#1219). tools/roadmap.py reads every row after `closing` as landed whole, and reads no
tracker, so the tracker's evidence is read here, before the merge and over the range. A row may be
closed only when no open issue names it: by a title whose leading token is the row id ("6.7
sitting: ..."), by a `**Plan row:**` line whose value is or lists it, read up to the first `(` or
`;`, or by a part declaration as above. --closing <row>[,<row>...] answers that for the session about
to write the subject: a `held:` line per row and naming issue and exit 1, or exit 0 when none names
any. The range check holds every commit whose subject carries the clause on the same ground, whatever
its head and whether or not it has a Ticket line: unlike a part ticket, no open issue is excused,
since a row the merge's own ticket names is one the merge lands under its head, not by the clause.
The listing that answers both is the complete one above, and one it cannot prove complete exits 2.

Each finding is a line `landing-subjects: held: ...`, `landing-subjects: unchecked: ...` or
`landing-subjects: note: ...`, and the last line is the verdict: `landing-subjects: pass: ...` with
exit 0, `landing-subjects: fail: ...` with exit 1 when a merge was held or, under --fail-unchecked,
went unchecked, and exit 2 when nothing was judged. --subject-for's last line is
`landing-subjects: subject: <head>`, with exit 0, or exit 2 when it could not read the history or
the complete open-issue listing. --closing's last line is a pass with exit 0, a fail with exit 1, or
exit 2 when the listing could not be read whole. tools/check.sh runs the range check over the range it already
judges, so the pre-push hook and the overnight integrate gate, which runs the full check, refuse
the subject before it is written into history to stay.
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
# tools/ticket_shape.py's OPEN_BOX: `- [ ]` starting a line, indented or not. It is matched against
# one line at a time, since open_boxes reads the body by its lines to leave out the fenced ones.
BOX = re.compile(r"^[ \t]*- \[(?P<mark>[ xX])\][ \t]*(?P<text>[^\n]*)$")

# A line that opens or shuts a fenced block, from tools/ticket_shape.py's FENCE for the reason the
# forms below are copied, with three changes, each as CommonMark reads a fence (#1402). The whole run
# of the marker is read, not its first three characters, so a longer fence can hold a shorter one:
# ticket_shape.py's three-character reading shuts a five-backtick block on the three-backtick line
# inside it, which is how #1215's body is written. A backtick run with another backtick later on its
# line is an inline code span, "```x``` reads", and opens nothing. And only a run with nothing but
# white space after it shuts a fence, so "```x" inside one is a quoted line. Without the last two a
# real box after such a line was hidden, and --base passed a full landing over it.
FENCE = re.compile(r"^[ \t]*(`{3,}|~{3,})(.*)$")

# Either subject form, as tools/roadmap.py's LANDED_SUBJECT reads it; group 1 is set on a part. This
# script runs under `python3 -I`, which leaves its own directory off sys.path, so roadmap.py's forms
# are copied here rather than imported: ID, TICKET_ID, the token split and CLOSING below are its ID,
# TICKET, reading()'s split and CLOSING. A closed id is read as reading() reads one, by ID alone, so
# the clause's own words, `row` and `rows`, need no copy here.
ANY_LANDING = re.compile(r"^Merge\s+(part of\s+)?tasks?\s+(.+?):", re.IGNORECASE)
ID = re.compile(r"^(?:I-\d+|\d+\.\d+[a-z]?|P2-\d+)$")
TICKET_ID = re.compile(r"^issue-(\d+)$", re.IGNORECASE)
TOKENS = re.compile(r"[,\s]+and[,\s]+|[,\s]+")
CLOSING = re.compile(r"[,\s]+closing\b\s*", re.IGNORECASE)

# How an open issue names a row it holds against a `closing` clause (#1219), beside PART_OF: its
# title's leading token, and a Plan row line's value up to the first `(` or `;`. The plural "Plan
# rows" is read too, since a live ticket writes it.
TITLE_ROW = re.compile(r"^\s*([^\s:,;]+)")
PLAN_ROW = re.compile(r"(?m)^\*\*Plan rows?:\*\*[ \t]*([^\n(;]*)")

# A part ticket's declaration (#388 Q5): its Plan row line, `none (part of <ids>[; prose])`, the ids
# read up to the first `;` or `)`.
PART_OF = re.compile(r"(?m)^\*\*Plan row:\*\*[ \t]*none[ \t]*\([ \t]*part of[ \t]+([^;)\n]*)")

# gh fills {owner}/{repo} from the current remote and signs in from its keyring or GH_TOKEN.
# --paginate follows endCursor until hasNextPage is false; --slurp preserves every page's receipt,
# so a short listing is not mistaken for all open issues (#871). The paging and the placeholders are
# gh's (https://cli.github.com/manual/gh_api).
GH_ISSUE_BODY = ("gh", "issue", "view", "{number}", "--json", "body", "--jq", ".body")
GH_OPEN_ISSUES = (
    "gh", "api", "graphql", "--paginate", "--slurp", "-F", "owner={owner}", "-F", "name={repo}",
    "-f", "query=query($owner:String!,$name:String!,$endCursor:String){"
    "repository(owner:$owner,name:$name){issues(states:OPEN,first:100,after:$endCursor){"
    "totalCount pageInfo{hasNextPage endCursor} nodes{number title body}}}}",
)

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


def ids(subject: str) -> tuple[bool, list[str], list[str]] | None:
    """(whether its head is a part, the ids its head names, the plan-row-shaped ids its `closing`
    clause closes) for a landing subject, or None for any other."""
    match = ANY_LANDING.match(subject)
    if not match:
        return None
    parts = CLOSING.split(match.group(2), maxsplit=1)
    named = [token for token in TOKENS.split(parts[0]) if ID.match(token) or TICKET_ID.match(token)]
    closed = [token for token in TOKENS.split(parts[1].strip()) if ID.match(token)] if len(parts) > 1 else []
    return (bool(match.group(1)), [name.lower() if TICKET_ID.match(name) else name for name in named],
            closed)


def states(subjects: list[str]) -> dict[str, str]:
    """Each id these subjects land: 'landed' when one names it whole or closes it, else 'part'."""
    found: dict[str, str] = {}
    for subject in subjects:
        read = ids(subject)
        if read is None:
            continue
        part, named, closed = read
        for name in named:
            if not part:
                found[name] = "landed"
            else:
                found.setdefault(name, "part")
        for name in closed:
            found[name] = "landed"
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


def rows_named(title: str, body: str) -> dict[str, str]:
    """Each plan row an issue names, as a `closing` clause reads it (#1219), with how it names it."""
    found: dict[str, str] = {}
    leading = TITLE_ROW.match(title)
    if leading and ID.match(leading.group(1)):
        found[leading.group(1)] = "by its title"
    # A `none` line names no row by its value: "none (row 6.7's runbooks)" reads as "none", since
    # the value stops at the `(`; a part declaration is read by `declared` below.
    for line in PLAN_ROW.finditer(body):
        for row in rows([token.strip("`") for token in TOKENS.split(line.group(1).strip())]):
            found.setdefault(row, "by its Plan row line")
    for row in declared(body):
        found.setdefault(row, "by its part declaration")
    return found


class Parts:
    """The complete open-issue listing, read once when a plan row is first asked about: the rows each
    issue declares itself part of (#388), and the rows each names by title, Plan row line or part
    declaration, which a `closing` clause is held on (#1219)."""

    def __init__(self):
        self.listed: dict[int, list[str]] | None = None
        self.names: dict[int, dict[str, str]] = {}

    def of(self, row: str) -> list[int]:
        """Open issues declaring `row`; an unproven listing raises Broken instead of clearing it."""
        self.read()
        return sorted(number for number, declares in self.listed.items() if row in declares)

    def naming(self, row: str) -> list[tuple[int, str]]:
        """Open issues naming `row` as a `closing` clause reads it, each with how (#1219)."""
        self.read()
        return [(number, self.names[number][row]) for number in sorted(self.names) if row in self.names[number]]

    def read(self):
        """List the open issues once, whole, or raise Broken."""
        if self.listed is None:
            text, why = gh(list(GH_OPEN_ISSUES))
            if text is None:
                raise Broken(f"the open issues could not be listed completely: {why}")
            try:
                pages = json.loads(text)
                if not isinstance(pages, list) or not pages:
                    raise ValueError("no paginated listing receipt")
                total = None
                bodies: dict[int, str] = {}
                titles: dict[int, str] = {}
                cursors = set()
                for index, page in enumerate(pages):
                    if not isinstance(page, dict) or page.get("errors"):
                        raise ValueError("a page has GraphQL errors or is not an object")
                    listing = page["data"]["repository"]["issues"]
                    count = listing["totalCount"]
                    if type(count) is not int or count < 0:
                        raise ValueError("totalCount is not a nonnegative integer")
                    if total is not None and total != count:
                        raise ValueError("totalCount changed between pages")
                    total = count
                    nodes = listing["nodes"]
                    info = listing["pageInfo"]
                    more, cursor = info["hasNextPage"], info["endCursor"]
                    if not isinstance(nodes, list) or type(more) is not bool:
                        raise ValueError("nodes or pageInfo has an unreadable shape")
                    if index == len(pages) - 1 and more:
                        raise ValueError("pagination ended while hasNextPage is true")
                    if index < len(pages) - 1 and not more:
                        raise ValueError("a page follows a terminal page")
                    if nodes:
                        if not isinstance(cursor, str) or not cursor or cursor in cursors:
                            raise ValueError("a page has an absent or repeated endCursor")
                        cursors.add(cursor)
                    elif len(pages) != 1 or count != 0 or cursor is not None:
                        raise ValueError("an empty page does not describe an empty listing")
                    for issue in nodes:
                        number, body = issue["number"], issue["body"]
                        if type(number) is not int or number <= 0 or not isinstance(body, str):
                            raise ValueError("an issue has no readable number or body")
                        title = issue.get("title")
                        if not isinstance(title, str):
                            raise ValueError("an issue has no readable title")
                        if number in bodies:
                            raise ValueError("an issue number is repeated in the listing")
                        bodies[number] = body
                        titles[number] = title
                if len(bodies) != total:
                    raise ValueError(f"the listing returned {len(bodies)} distinct issues of {total}")
                self.names = {number: rows_named(titles[number], body) for number, body in bodies.items()}
                self.listed = {number: declared(body) for number, body in bodies.items()}
            except (ValueError, TypeError, KeyError) as error:
                raise Broken(f"the open issues could not be listed completely: {error}") from error


def open_parts(parts: Parts, row: str, own: set[int], landed: dict[str, str]) -> list[int]:
    """The part tickets still owed; an incomplete listing cannot prove that none is left."""
    found = parts.of(row)
    return [number for number in found
            if number not in own and landed.get(f"issue-{number}") != "landed"]


def open_boxes(body: str) -> list[tuple[int, str]]:
    """Each open box in `body`, as (its place among the boxes outside fenced blocks, from 1; its
    text). A line inside a fenced block the body quotes is no box, as tools/ticket_shape.py's
    headings() reads a heading there as none (#1402), so "box 2" is the second box a reader of the
    rendered ticket sees. A fence is shut by a run of its own marker at least as long as the one
    that opened it with nothing but white space after it; a shorter run, the other marker, or a run
    with text after it, inside it is a quoted line. A backtick run with a backtick after it on its
    line opens no fence."""
    found: list[tuple[int, str]] = []
    fence = ""
    place = 0
    for line in body.split("\n"):
        marked = FENCE.match(line)
        if marked:
            run, rest = marked.group(1), marked.group(2)
            if not fence and not (run[0] == "`" and "`" in rest):
                fence = run
                continue
            if fence:
                if run[0] == fence[0] and len(run) >= len(fence) and not rest.strip():
                    fence = ""
                continue
        box = None if fence else BOX.match(line)
        if box:
            place += 1
            if box.group("mark") == " ":
                found.append((place, box.group("text").strip()))
    return found


def plural(count: int, one: str, many: str) -> str:
    return f"{count} {one if count == 1 else many}"


def held_closings(parts: Parts, closings: list[tuple[str, str, list[str]]]) -> int:
    """Say each `closing` clause that closes a row an open issue names; return how many commits."""
    held = 0
    for sha, subject, closed in closings:
        named = [(row, number, how) for row in closed for number, how in parts.naming(row)]
        for row, number, how in named:
            say(f'held: {sha} "{subject}" closes {row}, but #{number} is open and names {row} {how}')
        held += bool(named)
    return held


def judge(base: str, fail_unchecked: bool = False) -> int:
    listed = commits(base)
    landings = [(sha, subject, message) for sha, subject, message in listed
                if FULL_LANDING.match(subject)]
    read = [(sha, subject, ids(subject)) for sha, subject, _ in listed]
    closings = [(sha, subject, named[2]) for sha, subject, named in read if named is not None and named[2]]
    parts = Parts()
    if not landings:
        closed_held = held_closings(parts, closings)
        if closed_held:
            say(closing_fail(closed_held))
            return 1
        cleared = f"; {plural(len(closings), 'closing clause', 'closing clauses')} clear" if closings else ""
        say(f"pass: no full-landing merge from {base} to HEAD{cleared}.")
        return 0
    bodies: dict[int, tuple[str | None, str]] = {}
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
        named_rows = rows(ids(subject)[1])  # the head's rows: a closed row is judged below
        landed = states(history(sha)) if named_rows else {}
        for row in named_rows:
            owed = open_parts(parts, row, set(numbers), landed)
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
    closed_held = held_closings(parts, closings)
    if held and closed_held:
        say(f"fail: {held} full-landing {'merge' if held == 1 else 'merges'} held, and "
            f"{plural(closed_held, 'closing clause', 'closing clauses')} held on a row an open issue names; "
            "see each held line above.")
        return 1
    if closed_held:
        say(closing_fail(closed_held))
        return 1
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
    if fail_unchecked and unchecked:
        merges = "merge" if unchecked == 1 else "merges"
        say(f"fail: {unchecked} full-landing {merges} went unchecked, and --fail-unchecked holds what it could "
            "not read; sign gh in or reach GitHub, then run it again.")
        return 1
    cleared = f" {plural(len(closings), 'closing clause', 'closing clauses')} clear." if closings else ""
    say(f"pass: {plural(len(landings), 'full-landing merge', 'full-landing merges')} from {base} to HEAD: {clear} clear, "
        f"{unchecked} unchecked, {unticketed} with no Ticket line.{cleared}")
    return 0


def closing_fail(held: int) -> str:
    merges = "merge's clause closes" if held == 1 else "merges' clauses close"
    return (f"fail: {held} {merges} a row an open issue names; leave the row out of the clause until no "
            "open issue names it (#1219).")


def closing_mode(listed: str) -> int:
    """Whether rows may be closed by a `closing` clause: no open issue names any of them (#1219)."""
    asked = [row.strip() for row in listed.split(",") if row.strip()]
    if not asked or any(not ID.match(row) for row in asked):
        raise Broken(f"--closing takes plan row ids separated by commas, not {listed!r}")
    parts = Parts()
    held = [(row, number, how) for row in asked for number, how in parts.naming(row)]
    for row, number, how in held:
        say(f"held: {row}: #{number} is open and names it {how}")
    if held:
        rows_held = sorted({row for row, _, _ in held}, key=asked.index)
        say(f"fail: {plural(len(rows_held), 'row', 'rows')} of {len(asked)} named by an open issue "
            f"({', '.join(rows_held)}); leave {'it' if len(rows_held) == 1 else 'them'} out of the `closing` clause.")
        return 1
    say(f"pass: no open issue names {', '.join(asked)}; the `closing` clause may close "
        f"{'it' if len(asked) == 1 else 'them'}.")
    return 0


def subject_for(id_: str, number: int) -> int:
    """The head of a complete lane's merge subject, judged on HEAD's history and the open issues."""
    own = id_.lower() if TICKET_ID.match(id_) else id_
    landed = states(history("HEAD"))
    parts = Parts()
    named = [own]
    body, why = issue_body(number)
    if body is None:
        unnamed = ", and no row it is part of is named" if TICKET_ID.match(own) else ""
        say(f"unchecked: #{number} could not be read, so its open boxes are not counted{unnamed}: {why}")
    # The ticket's own open boxes make the merge a part, as the range check holds a full landing
    # over them (#660); a part, it names no row, since the row is owed until its last part is whole.
    boxes = open_boxes(body or "")
    if TICKET_ID.match(own) and not boxes:
        # A part ticket names each row it declares that is landed in part, never whole, once no
        # other part of it is open (#388 Q6).
        for row in declared(body or ""):
            if landed.get(row) != "part":
                continue
            owed = open_parts(parts, row, {number}, landed)
            if not owed:
                named.append(row)
    owed_by = {}
    for row in rows(named[:1]):
        owed = open_parts(parts, row, {number}, landed)
        if owed:
            owed_by[row] = owed
    if owed_by or boxes:
        for row, owed in owed_by.items():
            for part in owed:
                say(f"open part: #{part} declares itself part of {row}")
        for place, text in boxes:
            say(f"open box: {place} {text}")
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
                    "part ticket, or a closing clause over a row an open issue names; or say which subject a "
                    "complete lane's merge takes, or whether rows may be closed.")
    asked = parser.add_mutually_exclusive_group(required=True)
    asked.add_argument("--base", help="judge the commits from this ref to HEAD")
    asked.add_argument("--subject-for", metavar="ID", help="the id a complete lane landed: a plan row or issue-<n>")
    asked.add_argument("--closing", metavar="ROWS",
                       help="plan rows, separated by commas, a merge subject's closing clause is to close")
    parser.add_argument("--ticket", type=int, help="with --subject-for: the lane's issue number")
    parser.add_argument("--fail-unchecked", action="store_true",
                        help="with --base: fail a merge whose ticket or part tickets could not be read")
    arguments = parser.parse_args(argv)
    if arguments.subject_for is not None and arguments.ticket is None:
        parser.error("--subject-for needs --ticket")
    if arguments.subject_for is not None and arguments.fail_unchecked:
        parser.error("--fail-unchecked is an option of --base, not of --subject-for")
    if arguments.closing is not None and (arguments.fail_unchecked or arguments.ticket is not None):
        parser.error("--closing takes no other option")
    try:
        if arguments.closing is not None:
            return closing_mode(arguments.closing)
        if arguments.subject_for is not None:
            return subject_for(arguments.subject_for, arguments.ticket)
        return judge(arguments.base, arguments.fail_unchecked)
    except Broken as error:
        say(f"fail: nothing judged: {error}.")
        return 2
    except Exception as error:  # Python's own exit for an uncaught error is 1, a held merge's code.
        say(f"fail: nothing judged: {type(error).__name__}: {error}.")
        return 2


if __name__ == "__main__":
    sys.exit(main())
