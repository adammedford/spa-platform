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
    tools/roadmap.py --check    fail if they are not what the repository says they should be

An unrecognised argument exits 2 and writes nothing, because the default action writes.

The generated text holds no date and no commit id on purpose. It is a function of the tags, the
merge subjects and the plan, so --check stays quiet until one of those changes, and the push that
lands a task is the push that updates the roadmap.
"""

from __future__ import annotations

import argparse
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


def landed(root: pathlib.Path, rows: list[str]) -> dict[str, str]:
    """Each plan row's state: 'landed', 'part', or absent."""
    state: dict[str, str] = {}

    def record(id_: str, how: str) -> None:
        row = parent(id_, rows)
        if row is None:
            return
        # A split id never lands its parent row, however its own merge was worded.
        how = "part" if row != id_ else how
        if state.get(row) != "landed":
            state[row] = how

    for subject in subjects(root):
        match = LANDED_SUBJECT.match(subject)
        if match:
            how = "part" if match.group(1) else "landed"
            for token in re.split(r"[,\s]+and[,\s]+|[,\s]+", match.group(2)):
                if ID.match(token):
                    record(token, how)
            continue
        match = LANDED_BRANCH.match(subject)
        if match:
            # Normalize named prefixes only: 1.6a and 1.6b are distinct lowercase plan rows.
            # Capture the complete I-/P2- id above, including its hyphen, before the branch slug.
            id_ = match.group(1)
            if id_.startswith(("i-", "p2-")):
                id_ = id_.upper()
            record(id_, "landed")
            continue
        match = LANDED_INTEGRATOR.match(subject) or LANDED_PARENTHESISED.search(subject)
        if match:
            record(match.group(1), "landed")

    return state


# --- rendering -----------------------------------------------------------------------------------


def ids(values: list[str]) -> str:
    return ", ".join(f"`{value}`" for value in values) if values else "none"


def render(stages: list[Stage], state: dict[str, str], closed: set[str]) -> dict[str, str]:
    ladder_stages = [stage for stage in stages if stage.id != "SX"]
    first_open = next(
        (stage for stage in ladder_stages if stage.tag not in closed), None
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
        done = [row for row in stage.rows if state.get(row) == "landed"]
        part = [row for row in stage.rows if state.get(row) == "part"]
        open_ = [row for row in stage.rows if row not in state]
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
    gated = [row for stage in ladder_stages for row in stage.rows]
    total = len(gated)
    all_done = sum(1 for row in gated if state.get(row) == "landed")
    all_part = sum(1 for row in gated if state.get(row) == "part")

    summary = (
        f"**Closed gates:** "
        + (
            ", ".join(
                f"{stage.gate} (`{stage.tag}`)"
                for stage in ladder_stages
                if stage.tag in closed
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
        owed = [row for row in first_open.rows if state.get(row) != "landed"]
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
    check = parser.parse_args(argv).check
    root = pathlib.Path(__file__).resolve().parent.parent

    rows = plan_rows(root)
    stages, problems = ladder(root)
    problems += coverage(stages, rows)
    problems += citations(root, rows)

    if problems:
        for line in problems:
            print(f"  {line}")
        print()
        print("ROADMAP FAILED: the ladder does not line up with the plan.")
        print("The plan is the frozen export and never moves to suit the roadmap, so fix the")
        print("roadmap: give every plan row exactly one stage, and name no row the plan lacks.")
        return 1

    if shallow(root):
        print(
            "roadmap: not checked: this is a shallow clone, so the tags and merge subjects the "
            "state is derived from are not all here."
        )
        return 0

    blocks = render(stages, landed(root, rows), tags(root))

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
