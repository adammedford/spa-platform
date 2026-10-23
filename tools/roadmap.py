#!/usr/bin/env python3
"""The roadmap's state, derived rather than written. Owner: integrator.

`docs/roadmap.md` cuts the plan along the gateway's own capability ladder, because the gateway is
the thing being proved and the plan is organised by workstream. The ladder itself is editorial and
hand-written. Where the work has got to is not: AGENTS.md says of itself that a copy of the state
goes stale and then misleads every session that loads it, and a roadmap earns that warning twice
over, since a stage table with hand-typed ticks is wrong one merge after it is written.

So this script derives the state from the three places that already hold it, and writes it between
markers in `docs/roadmap.md` and `README.md`:

    gates closed       `git tag`. docs/agents/landing-work.md makes the tag the record of a gate.
    rows landed        merge subjects, whose form that same page fixes:
                       "Merge task(s) <id...>: ..." and "Merge part of task <id>: ...".
    rows that exist    the task tables in docs/plan/03-workstreams-and-tasks.md, and the package
                       table in docs/plan/06-phase-2-and-risks.md.

It also checks what no reader can check by eye: that the ladder covers every plan row exactly once
and names none that does not exist. A roadmap that quietly drops a row is worse than no roadmap,
because it reads as coverage.

    tools/roadmap.py            rewrite the generated blocks (also --write)
    tools/roadmap.py --check    fail if they are not what the repository says they should be,
                                or if the commits between origin/main and HEAD merge one id both
                                whole and in part (#290)
    tools/roadmap.py --json     print the same derivation for a program, and write nothing

An unrecognised argument exits 2 and writes nothing, because the default action writes.

--json is for a program that needs to know how far the work has got, so it asks rather than keeping
a rule of its own (#70). It prints, from the reading the blocks are written from,

    {"closed_gates": [{"gate": "G1a", "tag": "skeleton-v1"}, ...],
     "rows": {"I-1": "landed", ..., "1.3": "part-landed", ..., "6.8": "ongoing", ...},
     "splits": {"1.4a": "landed", ..., "2.1b": "part-landed", ...},
     "tickets": {"issue-65": "landed", ..., "issue-67": "part-landed", ...}}

with every plan row in the plan's order: landed, part-landed, open, or ongoing for a row of a stage
with no gate. "splits" and "tickets" hold the ids a merge subject names that are not plan rows -- a
split of a row, 2.1b, and a ticket, issue-66 -- each landed or part-landed by its own subjects, and
an id no subject names is absent. They are there for tools/prune_branches.py, whose claim branches
carry those ids and whose rows cannot speak for them (#71). Where the answer would not be whole -- a
shallow clone, a git that answers with no history, a ladder that does not line up with the plan --
it exits 1 with nothing on stdout.

The generated text holds no date and no commit id on purpose. It is a function of the tags, the
merge subjects and the plan, so --check stays quiet until one of those changes, and the push that
lands a task is the push that updates the roadmap.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import subprocess
import sys

ROADMAP = "docs/roadmap.md"
PLAN_TASKS = "docs/plan/03-workstreams-and-tasks.md"
PLAN_PHASE2 = "docs/plan/06-phase-2-and-risks.md"
TARGETS = {"README.md": ("summary",), ROADMAP: ("stages",)}

# A plan row's id: I-4, 2.1, 1.6a, P2-7. The letter suffix is a plan row of its own (1.6a and 1.6b
# are separate rows); a suffix on an id the plan does not list is a split made while working, which
# `parent` below folds back into the row it came from.
ID = re.compile(r"^(?:I-\d+|\d+\.\d+[a-z]?|P2-\d+)$")

# The ladder's summary table: | S1 | what the gateway can do | G1a | `skeleton-v1` |
LADDER_ROW = re.compile(r"^\|\s*(S\d+|SX)\s*\|([^|]*)\|([^|]*)\|([^|]*)\|\s*$")
STAGE_HEADING = re.compile(r"^###\s+(S\d+|SX)\s+(.+?)\s*$")
STAGE_ROWS = re.compile(r"^\*\*Plan rows\.\*\*\s*(.*)$")
BACKTICKED = re.compile(r"`([^`]+)`")

# The first cell of a plan table row. The phase 2 table writes "P2-1 Durable sessions" in one cell,
# so the id is the first token rather than the whole cell.
TABLE_CELL = re.compile(r"^\|\s*([^|]+?)\s*\|")

MARKER = "<!-- {} GENERATED {} -->"


def run(args: list[str], root: pathlib.Path) -> str:
    done = subprocess.run(
        args, cwd=root, capture_output=True, text=True, check=False
    )
    return done.stdout if done.returncode == 0 else ""


# --- what the plan says exists -------------------------------------------------------------------


def plan_rows(root: pathlib.Path) -> list[str]:
    """Every task id the plan lists, in the order the plan lists them."""
    rows: list[str] = []
    for name in (PLAN_TASKS, PLAN_PHASE2):
        for line in (root / name).read_text().splitlines():
            cell = TABLE_CELL.match(line)
            if not cell:
                continue
            candidate = cell.group(1).split()[0] if cell.group(1).split() else ""
            if ID.match(candidate) and candidate not in rows:
                rows.append(candidate)
    return rows


# --- what the ladder claims ----------------------------------------------------------------------


class Stage:
    def __init__(self, id_: str, capability: str, gate: str, tag: str) -> None:
        self.id = id_
        self.capability = capability
        self.gate = gate
        self.tag = tag
        self.name = ""
        self.rows: list[str] = []

    @property
    def title(self) -> str:
        return f"{self.id} {self.name}" if self.name else self.id


def ladder(root: pathlib.Path) -> tuple[list[Stage], list[str]]:
    """The stages, from the summary table and the section headings. Returns (stages, problems)."""
    text = (root / ROADMAP).read_text()
    problems: list[str] = []

    stages: list[Stage] = []
    for line in text.splitlines():
        row = LADDER_ROW.match(line)
        if not row:
            continue
        id_, capability, gate, tag = (cell.strip() for cell in row.groups())
        tags = BACKTICKED.findall(tag)
        stages.append(Stage(id_, capability, gate, tags[0] if tags else ""))

    by_id = {stage.id: stage for stage in stages}
    if len(by_id) != len(stages):
        problems.append(f"{ROADMAP}: a stage id appears twice in the ladder table")

    # The sections, in file order, each ending at the next heading of the same level or above. A
    # "**Plan rows.**" line is read to the end of its paragraph, because a stage with two dozen rows
    # does not fit the 100 columns the rest of the file wraps at.
    heading_order: list[str] = []
    current: Stage | None = None
    collecting = False
    for number, line in enumerate(text.splitlines(), start=1):
        if collecting:
            if not line.strip():
                collecting = False
            elif current is not None:
                current.rows += [
                    id_ for id_ in BACKTICKED.findall(line) if ID.match(id_)
                ]
                continue
        heading = STAGE_HEADING.match(line)
        if heading:
            id_, name = heading.groups()
            heading_order.append(id_)
            current = by_id.get(id_)
            if current is None:
                problems.append(
                    f"{ROADMAP}:{number}: stage '{id_}' has a section but no row in the ladder table"
                )
                continue
            current.name = name
            continue
        if line.startswith(("## ", "### ")):
            current = None
        if current is None:
            continue
        claim = STAGE_ROWS.match(line)
        if claim:
            current.rows = [
                id_ for id_ in BACKTICKED.findall(claim.group(1)) if ID.match(id_)
            ]
            collecting = True

    if heading_order != [stage.id for stage in stages]:
        problems.append(
            f"{ROADMAP}: the ladder table and the stage sections list different stages, or list "
            f"them in a different order: {[s.id for s in stages]} against {heading_order}"
        )

    for stage in stages:
        if not stage.rows:
            problems.append(
                f"{ROADMAP}: stage {stage.id} has no '**Plan rows.**' line, so nothing says what "
                f"it covers"
            )

    return stages, problems


def coverage(stages: list[Stage], rows: list[str]) -> list[str]:
    """Every plan row belongs to exactly one stage, and no stage invents one."""
    problems: list[str] = []
    seen: dict[str, str] = {}

    for stage in stages:
        for id_ in stage.rows:
            if id_ not in rows:
                problems.append(
                    f"{ROADMAP}: stage {stage.id} names '{id_}', which is not a row in "
                    f"{PLAN_TASKS} or {PLAN_PHASE2}"
                )
            elif id_ in seen:
                problems.append(
                    f"{ROADMAP}: '{id_}' is in both stage {seen[id_]} and stage {stage.id}"
                )
            else:
                seen[id_] = stage.id

    for id_ in rows:
        if id_ not in seen:
            problems.append(
                f"{ROADMAP}: plan row '{id_}' is in no stage. Put it in one, so the ladder cannot "
                f"read as coverage it does not have."
            )

    return problems


def citations(root: pathlib.Path, rows: list[str]) -> list[str]:
    """Every id-shaped token in the roadmap's prose names a plan row.

    The prose cites rows outside the '**Plan rows.**' lines -- which row fills a pipeline stage,
    which row a gap would land in -- and a citation of a row that does not exist sends a reader
    looking for it. Only backticked tokens count, for the reason PATHLIKE gives in
    .github/steering.py: guessing at bare prose is how a check earns false positives.
    """
    problems = []
    for number, line in enumerate((root / ROADMAP).read_text().splitlines(), start=1):
        for token in BACKTICKED.findall(line):
            if ID.match(token) and token not in rows:
                problems.append(
                    f"{ROADMAP}:{number}: cites '{token}', which is not a plan row. A split made "
                    f"while working belongs in its ticket, not here."
                )
    return problems


# --- what git says happened ----------------------------------------------------------------------


def subjects(root: pathlib.Path) -> list[str]:
    """Commit subjects from the integration lines: this branch, and origin/main where it exists.

    The union, not this branch alone. A task branch forks from main and carries main's merges, but
    a branch that main has moved past does not, and deriving from HEAD alone would then rewrite the
    block to drop work that is in the trunk.
    """
    refs = ["HEAD"]
    for ref in ("origin/main", "main"):
        if run(["git", "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"], root):
            refs.append(ref)
            break
    return run(["git", "log", "--format=%s", *refs], root).splitlines()


def tags(root: pathlib.Path) -> set[str]:
    return set(run(["git", "tag"], root).split())


def shallow(root: pathlib.Path) -> bool:
    return (root / ".git" / "shallow").exists() or bool(
        run(["git", "rev-parse", "--is-shallow-repository"], root).strip() == "true"
    )


# The merge is the landing event, which is why "Merge" is required here. A branch carries the same
# ids in its own commits -- "Task 5.5: ..." beside "Merge task 5.5: ..." -- and reading those as
# landings would call a row done at the commit that wrote it, and would promote a row whose merge
# said "part of" to landed on the strength of the commit beneath it.
LANDED_SUBJECT = re.compile(r"^Merge\s+(part of\s+)?tasks?\s+(.+?):", re.IGNORECASE)
# GitHub's own merge subject, where the branch name carries the id: task/i-3-abstractions.
LANDED_BRANCH = re.compile(
    r"^Merge pull request #\d+ from \S+?/task/([Ii]-\d+|[Pp]2-\d+|\d+\.\d+[a-z]?)-"
)
# "I-5: CI, and check A-05" and "Import skeleton (I-1), plan export, ...". The integrator's own rows
# landed on the trunk directly, with no branch and no merge to name them; a workstream row never
# does, because landing-work.md gives it a branch and a merge. Restricted to I- ids for that reason.
# Only the prefix is read, so an id later in the subject ("I-7: decide change request 1.6a") lands
# nothing.
LANDED_INTEGRATOR = re.compile(r"^(I-\d+):\s")
LANDED_PARENTHESISED = re.compile(r"\((I-\d+)\)")


def parent(id_: str, rows: list[str]) -> str | None:
    """The plan row an id belongs to, or None.

    A row that outgrew one session gets split while working -- 3.0a, 2.1b -- and those ids are in
    the tickets, not the plan. Fold such an id into the row it came from, and treat it as part of
    it: the plan row is not done because one of its splits merged.
    """
    if id_ in rows:
        return id_
    stripped = id_[:-1]
    return stripped if stripped in rows else None


# A ticket's id where a merge subject names it: "Merge task issue-66: ...". Tickets are GitHub
# issues (ADR-0020), not plan rows, so no ticket reaches the blocks; --json reports them because a
# claim branch carries the id, and tools/prune_branches.py has to know when that claim is finished
# (#71). Written in lower case on the branch, and read in any case here.
TICKET = re.compile(r"^issue-\d+$", re.IGNORECASE)


def reading(subject: str) -> list[tuple[str, str]]:
    """Every (id, how) one commit subject lands: how is 'landed' or 'part'.

    The one reading of the subject forms. `landed` folds these into plan rows, `own_states` keeps
    splits and tickets by their own wording, and `merged_both_ways` compares them within a range, so
    no two of them can read one subject two ways.
    """
    found: list[tuple[str, str]] = []
    match = LANDED_SUBJECT.match(subject)
    if match:
        how = "part" if match.group(1) else "landed"
        for token in re.split(r"[,\s]+and[,\s]+|[,\s]+", match.group(2)):
            if ID.match(token):
                found.append((token, how))
            elif TICKET.match(token):
                found.append((token.lower(), how))
        return found
    match = LANDED_BRANCH.match(subject)
    if match:
        # Normalize named prefixes only: 1.6a and 1.6b are distinct lowercase plan rows.
        # Capture the complete I-/P2- id above, including its hyphen, before the branch slug.
        id_ = match.group(1)
        if id_.startswith(("i-", "p2-")):
            id_ = id_.upper()
        return [(id_, "landed")]
    match = LANDED_INTEGRATOR.match(subject) or LANDED_PARENTHESISED.search(subject)
    if match:
        return [(match.group(1), "landed")]
    return found


def landings(root: pathlib.Path) -> list[tuple[str, str]]:
    """Every (id, how) a merge subject lands, newest first."""
    return [found for subject in subjects(root) for found in reading(subject)]


def merged_both_ways(root: pathlib.Path) -> list[str]:
    """Each id the commits between the trunk and HEAD merge both whole and in part.

    `landed` lets one full landing anywhere in the ancestry win for good, which is right for the
    trunk's history and wrong for a branch that has not landed yet. #287's run branch was rebuilt so
    row 6.7's merge said "Merge part of task 6.7", but the old "Merge task 6.7" was still reachable
    through the second parents of the rebuilt lane merges, so --check passed on a roadmap that said
    S7 owed nothing (#290). Within one unlanded range the two subjects contradict each other, and
    no reading of them is safe, so the check refuses the range and names both commits.

    Only the range counts. A row the trunk already lists as landed can take a later "part of"
    follow-up (2.1, then 2.1b, #71), and a row the trunk lists as part-landed can be finished by
    the range. Ids are compared as the subjects write them: a split such as 2.1b is its own id here,
    and its row's whole landing beside it is no contradiction. With no trunk to compare against
    there is no range, and nothing is held.
    """
    trunk = next(
        (
            ref
            for ref in ("origin/main", "main")
            if run(["git", "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"], root)
        ),
        None,
    )
    if trunk is None:
        return []
    seen: dict[str, dict[str, list[str]]] = {}
    for line in run(["git", "log", "--format=%h %s", f"{trunk}..HEAD"], root).splitlines():
        sha, _, subject = line.partition(" ")
        for id_, how in reading(subject):
            seen.setdefault(id_, {}).setdefault(how, []).append(f"{sha} '{subject}'")
    problems = []
    for id_, by in seen.items():
        if "landed" in by and "part" in by:
            problems.append(
                f"'{id_}' is merged whole by {', '.join(by['landed'])} and in part by "
                f"{', '.join(by['part'])}, both between {trunk} and HEAD"
            )
    return problems


def landed(root: pathlib.Path, rows: list[str]) -> dict[str, str]:
    """Each plan row's state: 'landed', 'part', or absent."""
    state: dict[str, str] = {}
    for id_, how in landings(root):
        row = parent(id_, rows)
        if row is None:
            continue
        # A split id never lands its parent row, however its own merge was worded.
        how = "part" if row != id_ else how
        if state.get(row) != "landed":
            state[row] = how
    return state


def own_states(root: pathlib.Path, rows: list[str]) -> tuple[dict[str, str], dict[str, str]]:
    """(splits, tickets): each id that is not a plan row, by the subjects that name it.

    A split's row cannot speak for it. 2.1 landed whole, and later a follow-up split of it merged as
    "Merge part of task 2.1b", so 2.1b's claim is live under a landed row (#71). Each reads
    'landed' once any subject lands it whole and 'part-landed' otherwise; an id no subject names is
    absent. Splits come in the plan's order of their rows, tickets by number.
    """
    splits: dict[str, str] = {}
    tickets: dict[str, str] = {}
    for id_, how in landings(root):
        if TICKET.match(id_):
            into = tickets
        elif id_ not in rows and id_[-1:].isalpha() and parent(id_, rows):
            into = splits
        else:
            continue
        if into.get(id_) != "landed":
            into[id_] = "landed" if how == "landed" else "part-landed"
    return (
        {id_: splits[id_] for id_ in sorted(splits, key=lambda s: (rows.index(s[:-1]), s))},
        {id_: tickets[id_] for id_ in sorted(tickets, key=lambda t: int(t.split("-")[1]))},
    )


# --- what the blocks count -----------------------------------------------------------------------


def counted(stages: list[Stage], state: dict[str, str]) -> dict[str, str]:
    """Each plan row as the generated blocks count it: landed, part-landed, open or ongoing.

    The one reading of a row's state that the blocks and --json share, so the two cannot disagree
    (#70). A row of a stage with no gate is ongoing whatever its subjects say: "I-7: decide change
    request 1.6a" lands I-7 by LANDED_INTEGRATOR, yet SX's rows run for the project's whole length
    and the blocks never count one as landed.
    """
    rows: dict[str, str] = {}
    for stage in stages:
        for row in stage.rows:
            if not stage.tag:
                rows[row] = "ongoing"
            else:
                rows[row] = {"landed": "landed", "part": "part-landed"}.get(
                    state.get(row, ""), "open"
                )
    return rows


def closed_gates(stages: list[Stage], closed: set[str]) -> list[Stage]:
    """The stages whose gate tag exists, in ladder order."""
    return [stage for stage in stages if stage.tag and stage.tag in closed]


def report(
    rows: list[str],
    stages: list[Stage],
    counts: dict[str, str],
    closed: set[str],
    own: tuple[dict[str, str], dict[str, str]],
) -> dict:
    """What --json prints: the closed gates, every plan row's state in the plan's order, and the
    splits and tickets `own_states` reads."""
    return {
        "closed_gates": [
            {"gate": stage.gate, "tag": stage.tag} for stage in closed_gates(stages, closed)
        ],
        "rows": {row: counts[row] for row in rows},
        "splits": own[0],
        "tickets": own[1],
    }


# --- rendering -----------------------------------------------------------------------------------


def ids(values: list[str]) -> str:
    return ", ".join(f"`{value}`" for value in values) if values else "none"


def render(stages: list[Stage], counts: dict[str, str], closed: set[str]) -> dict[str, str]:
    """The two generated blocks, from `counted`'s reading of each row and nothing else."""
    first_open = next(
        (stage for stage in stages if stage.tag and stage.tag not in closed), None
    )

    lines = [
        "| Stage | Gate | Plan rows landed | What the stage still owes |",
        "| --- | --- | --- | --- |",
    ]
    for stage in stages:
        if stage.tag:
            gate = f"{stage.gate}, tagged `{stage.tag}`" if stage.tag in closed else f"{stage.gate}, not tagged"
        else:
            gate = stage.gate
        if not stage.tag:
            # A stage with no gate has no end, so counting how far through it is would be a number
            # about nothing. SX's rows run for the project's whole length.
            lines.append(f"| {stage.title} | {stage.gate} | ongoing | — |")
            continue
        done = [row for row in stage.rows if counts[row] == "landed"]
        part = [row for row in stage.rows if counts[row] == "part-landed"]
        open_ = [row for row in stage.rows if counts[row] == "open"]
        owes = "; ".join(
            filter(
                None,
                [
                    ids(open_) if open_ else "",
                    f"part-landed: {ids(part)}" if part else "",
                ],
            )
        )
        lines.append(
            f"| {stage.title} | {gate} | {len(done)} of {len(stage.rows)} | "
            f"{owes or 'nothing'} |"
        )

    # Over the gated stages only: a row that runs throughout is never landed, so counting it would
    # put a ceiling on the total that can never be reached.
    total = sum(1 for state in counts.values() if state != "ongoing")
    all_done = sum(1 for state in counts.values() if state == "landed")
    all_part = sum(1 for state in counts.values() if state == "part-landed")

    summary = (
        f"**Closed gates:** "
        + (
            ", ".join(
                f"{stage.gate} (`{stage.tag}`)" for stage in closed_gates(stages, closed)
            )
            or "none yet"
        )
        + ".\n"
        + (
            f"**Where the gateway is:** {first_open.title}, which closes at gate {first_open.gate}.\n"
            if first_open
            else "**Where the gateway is:** every gate on the ladder is tagged.\n"
        )
        + f"**Plan rows landed:** {all_done} of {total}"
        + (f", with {all_part} part-landed" if all_part else "")
        + "."
    )
    if first_open:
        owed = [row for row in first_open.rows if counts[row] != "landed"]
        if owed:
            summary += f" {first_open.id} still owes {ids(owed)}."

    return {"stages": "\n".join(lines), "summary": summary}


def replace(text: str, name: str, body: str) -> tuple[str, bool]:
    begin, end = MARKER.format("BEGIN", name), MARKER.format("END", name)
    # The guard must survive deletion of both markers, and reject duplicate or reversed pairs.
    if text.count(begin) != 1 or text.count(end) != 1 or text.index(begin) >= text.index(end):
        return text, False
    head, rest = text.split(begin, 1)
    _, tail = rest.split(end, 1)
    return f"{head}{begin}\n{body}\n{end}{tail}", True


def main(argv: list[str]) -> int:
    # Parsed rather than scanned for "--check", because the default action rewrites two tracked
    # files and a scan reads every typo as that default: `tools/roadmap.py --help` rewrote the
    # blocks and exited 0, which no session exploring the script expects. An unknown flag now
    # exits 2 and writes nothing.
    parser = argparse.ArgumentParser(
        prog="tools/roadmap.py",
        description=(
            "Rewrite the generated blocks in docs/roadmap.md and README.md from the tags, the "
            "merge subjects and the plan."
        ),
    )
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--check",
        action="store_true",
        help="fail if the blocks are not what the repository says, and write nothing",
    )
    mode.add_argument(
        "--write",
        action="store_true",
        help="rewrite the blocks: the default, named so a caller can say which it meant",
    )
    mode.add_argument(
        "--json",
        action="store_true",
        help=(
            "print the closed gates and each plan row's state (landed, part-landed, open or "
            "ongoing) as the blocks count them, then each split and ticket a merge subject names, "
            "and write nothing"
        ),
    )
    arguments = parser.parse_args(argv)
    check = arguments.check
    root = pathlib.Path(__file__).resolve().parent.parent

    # In JSON mode stdout is the document a program parses, so everything else goes to stderr.
    def say(line: str = "") -> None:
        print(line, file=sys.stderr if arguments.json else sys.stdout)

    rows = plan_rows(root)
    stages, problems = ladder(root)
    problems += coverage(stages, rows)
    problems += citations(root, rows)

    if problems:
        for line in problems:
            say(f"  {line}")
        say()
        say("ROADMAP FAILED: the ladder does not line up with the plan.")
        say("The plan is the frozen export and never moves to suit the roadmap, so fix the")
        say("roadmap: give every plan row exactly one stage, and name no row the plan lacks.")
        return 1

    if shallow(root):
        if arguments.json:
            # The blocks mode passes a shallow clone unchecked and writes nothing. A JSON answer is
            # read, and one derived from part of the history calls landed rows open.
            say(
                "roadmap: no state printed: this is a shallow clone, so the tags and merge "
                "subjects the state is derived from are not all here."
            )
            return 1
        say(
            "roadmap: not checked: this is a shallow clone, so the tags and merge subjects the "
            "state is derived from are not all here."
        )
        return 0

    if arguments.json and not subjects(root):
        # `run` turns a git that fails -- a wrong GIT_DIR, a repository git will not read -- into
        # empty output, and no subjects reads as no row landed: every row open, exit 0, which sends
        # finished rows back out for work (#70). A repository with a plan has commits.
        say(
            "roadmap: no state printed: git returned no commit subjects here, so every row would "
            "read as open."
        )
        return 1

    if check:
        contradictions = merged_both_ways(root)
        if contradictions:
            for line in contradictions:
                print(f"  {line}")
            print()
            print("ROADMAP FAILED: this branch both completes a row and leaves it unfinished, and")
            print("every subject in the ancestry counts, so the full merge would win (#290). Take the")
            print("one that is wrong off the branch: rebuild it so that commit is not an ancestor.")
            return 1

    closed = tags(root)
    counts = counted(stages, landed(root, rows))
    if arguments.json:
        print(json.dumps(report(rows, stages, counts, closed, own_states(root, rows)), indent=2))
        return 0
    blocks = render(stages, counts, closed)

    stale: list[str] = []
    for name in TARGETS:
        path = root / name
        before = path.read_text()
        after = before
        for block in TARGETS[name]:
            after, found = replace(after, block, blocks[block])
            if not found:
                print(f"  {name}: '{block}' requires exactly one ordered BEGIN/END marker pair")
                return 1
        if after == before:
            continue
        if check:
            stale.append(name)
        else:
            path.write_text(after)
            print(f"roadmap: rewrote the generated blocks in {name}.")

    if stale:
        print(f"  {', '.join(stale)}")
        print()
        print("ROADMAP FAILED: the generated blocks are not what the tags, the merge subjects and")
        print("the plan say they should be. Run tools/roadmap.py and commit what it writes.")
        print("If this is main just after a merge, the merge did not go through tools/land.sh.")
        return 1

    print(f"roadmap: {len(rows)} plan rows, each in one stage; the generated blocks are current.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
