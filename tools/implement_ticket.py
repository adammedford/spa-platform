#!/usr/bin/env python3
"""The deterministic half of the foreground implement harness (#1563). Owner: integrator.

A foreground session building one ticket runs these, from the main checkout, in this order:

    python3 -I tools/implement_ticket.py read <n> --wt <path> [--port <p>]
                                                                gate the ticket and record it
    python3 -I tools/implement_ticket.py launch <n> --seams <file>|implementer
                                                                check the confirmed seams
    python3 -I tools/implement_ticket.py start <n>              claim it and make its worktree
    python3 -I tools/implement_ticket.py check <n> [--port <p>] the full check, detached, judged
    python3 -I tools/implement_ticket.py release <n>            unclaim a run that failed

Each command writes its record as one JSON object to `.scratch/implement/log/<n>/<step>.json` in
the main checkout, prints that object on one line, and ends on a verdict line:
`implement-ticket: <step>: pass: ...` with exit 0, `implement-ticket: <step>: fail: ...` with exit 1,
or the same `fail:` with exit 2 when it could not judge: gh or git did not answer, a record it needs
is missing or unreadable, an argument is missing, or the check has no verdict yet. A later command
reads the earlier records and git itself, never what a caller says they held.

read fetches origin, reads the issue through gh and records it: its title, state, labels,
assignees and open blockers; the open acceptance boxes' texts in order, by tools/ticket_shape.py's
OPEN_BOX; the Touches paths, the owner, the task id and the slug, by tools/overnight_collect.py's
functions; the branch `task/<id>-<slug>`, the worktree path --wt names, the base (origin/main's
sha), whether the branch exists, locally or on origin, and the kinds (KINDS). It passes only when
every gate holds, and fails naming each that does not:
- open: the issue is open;
- ready-for-agent: it carries the label;
- no-open-blocker: no blocked-by edge to an open issue;
- ticket-shape: tools/ticket_shape.py's judge() finds no failure;
- unclaimed: it has no assignee, or its task branch already exists (a run to continue);
- boxes: the open boxes OPEN_BOX reads are as many as overnight_collect.py's open_boxes() counts,
  so no box is built that the overnight run would not see, or the reverse.

A passing read's record ends on the first launch of .claude/workflows/implement-ticket.js (#1565):
`"launch": {"scriptPath": <the script in this checkout>, "args": {"repo", "issue", "wt", "port"}}`,
port being --port, default DEFAULT_PORT. That launch runs the Plan agent alone and returns proposed
seams. A refused read has no launch.

launch checks the seams Adam confirmed against read's record and, when they hold, ends its record on
the second launch: the first's args with "seams" added, the file's JSON unchanged. --seams is a JSON
file `{"seams": [{"name", "interface", "boxes"}], "noTest": [{"box", "why"}]}`, where a box is the
1-based index of an open box in read's record ("boxes"), or the one word `implementer`, which lets
the build agent choose and list its own seams. It fails naming each box that is in no seam and not in
noTest, each box in two seams (or a seam and noTest), each index the ticket has no box for, each
field missing or of the wrong kind, and a field the file should not have; and it fails when read did
not pass. --port, read's and check's alone, is an integer from 1024 to 65535, the range the script
takes, so the tool never prints a launch the script refuses.

start reads the issue again and applies the same gates to what it finds now, so a ticket
relabelled or claimed since read is refused before anything is written. It is refused, too, when
the open boxes it reads are not read's, in the same order: the confirmed seams number read's boxes. With no task branch it
assigns the issue (`gh issue edit --add-assignee @me`) and adds the worktree on a new branch at
read's base, undoing the assignment when the worktree cannot be added. With a branch it adds the
worktree on it, or reuses the one that has it checked out, and assigns nobody anew. Its state is
`new`, `continued`, or `finished` when check.json records a pass on the branch's head; it records
the fork point (`git merge-base` of the branch and origin/main) and the head.

check runs `tools/check.sh --full --base <fork> --port <p>` in the worktree, detached, by the
recipe in .claude/workflows/overnight.js's GATE: SIGINT reset to its default before bash starts
(#1532), stdout and stderr to one log, check.log, whose first line is `HEAD=<sha>`, and the pid in
check.pid beside it. One check runs per issue: the pid file is claimed with O_EXCL under a lock on
check.lock as `claim <the tool's pid>`, and the check itself, under the same lock, replaces the
claim with its own pid before it execs bash, so a tool killed once the check is launched leaves the
file naming the check (#1564); a check that finds its starter's claim already cleared exits unrun.
A call that finds a check running, or a claim being started, waits on it rather than starting a
second; one running for an earlier head is waited out, and then this head's starts. A pid file
naming a pid that is gone is stale and removed, and so is a claim once its writer is gone and
EMPTY_CLAIM seconds have passed; a pid file whose text is not a pid as str() writes it (`0123`)
is removed as no pid. A launch that ends without naming itself fails. Every call returns within
WAIT_BOUND seconds: its wait for a verdict ends JUDGING_RESERVE seconds before that, with state
`running` when there is no verdict yet, and every git, gh and helper it runs is given only the time
left, so one that hangs ends the call unjudged (exit 2), never passed. IMPLEMENT_TICKET_WAIT and
IMPLEMENT_TICKET_RESERVE may shorten the two, never lengthen them. A log for the worktree's head that already has a verdict is
judged without a new run. The verdict passes only when the log's last line starts `check: pass:`,
its `HEAD=` equals the worktree's HEAD, the worktree has no uncommitted or untracked change, and
`.github/path-ownership.py <fork> <head>`, the main checkout's copy, names the ticket's owner and
no other, so a branch that edits A-05 does not judge itself. The record lists
the changed files outside the Touches paths without failing on them, and on a red check carries what
`tools/test_reach.py` prints for the log.

release removes the assignee, reads the assignees again, and records who still holds the issue,
with the branch and worktree it left in place: it says the issue is unassigned only when nobody
does, and names whoever still does (another login, which start may have continued under) without
failing, since its own removal went through.
"""
from __future__ import annotations

import argparse
import contextlib
import copy
import fcntl
import fnmatch
import importlib.util
import json
import os
import pathlib
import re
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
LOG_ROOT = ROOT / ".scratch" / "implement" / "log"
FIELDS = "number,title,state,body,labels,assignees,blockedBy,subIssuesSummary,url"
READY = "ready-for-agent"
DEFAULT_PORT = 5180
SCRIPT_PATH = ".claude/workflows/implement-ticket.js"
# The Bash tool's limit is 600 s. A call returns within WAIT_BOUND, so its wait ends JUDGING_RESERVE
# earlier, leaving room for A-05 and test_reach.py after it; run() gives every command only the time
# left before the call's deadline, so the reserve holds (#1569's review).
WAIT_BOUND = 540.0
JUDGING_RESERVE = 30.0
POLL = 0.2

# The kinds of change a Touches path is, in this order, and the test command each runs for a named
# project ("{project}"): #1564 asks for the table whole, the commands included, so that the
# mutation proof #1566 adds reads them here. A path may be of several kinds and of none. dotnet,
# terraform and workflow are the halves .github/touches.sh answers for, read from it so the two
# never disagree; script and docs are this table's own.
KINDS = (
    ("dotnet", ("dotnet", "test", "--project", "{project}")),
    ("terraform", ("terraform", "-chdir={project}", "test")),
    ("script", ("python3", "-I", "{project}")),
    ("workflow", ("node", "{project}")),
    ("docs", None),
)
HALVES = {"dotnet": "build", "terraform": "terraform", "workflow": "workflows"}
SCRIPT = re.compile(r"^(tools|\.github)/(?!.*\.(cs|csproj|props|targets|tf|json|md)$)")
DOCS = re.compile(r"(^docs/|\.md$)")

A05_WHOLE = re.compile(r"^A-05: \d+ file\(s\), all owned by (?P<owner>\S+)\.$", re.MULTILINE)
A05_COMMIT = re.compile(r"^  [0-9a-f]{9}  (?P<owner>\S+) ", re.MULTILINE)
PASS_LINE, FAIL_LINE = "check: pass:", "check: fail:"


class Unjudged(Exception):
    """Nothing could be judged: exit 2."""


class Failed(Exception):
    """Judged, and a gate or a step failed: exit 1."""


def test_command(kind: str, project: str) -> list[str] | None:
    """The command that runs a named project's tests, by its kind, or None for docs."""
    command = dict(KINDS)[kind]
    return None if command is None else [part.replace("{project}", project) for part in command]


# --- the world: git, gh and the records -----------------------------------------------------------

# When the call must have returned, by time.monotonic(): main() sets it from bound() and reserve().
DEADLINE = float("inf")


def run(args: list[str], cwd: pathlib.Path = ROOT, stdin: str | None = None,
        check: bool = True) -> subprocess.CompletedProcess:
    """args run to its end, given only the time left before DEADLINE: one that does not end in it
    is killed and nothing is judged."""
    left = DEADLINE - time.monotonic()
    if left <= 0:
        raise Unjudged(f"{' '.join(args[:4])} was not run: the call's time bound has passed")
    try:
        done = subprocess.run(args, cwd=cwd, capture_output=True, text=True, input=stdin,
                              check=False, timeout=left)
    except subprocess.TimeoutExpired as error:
        raise Unjudged(f"{' '.join(args[:4])} did not end within the call's time bound "
                       f"({left:.1f} s were left), so it was killed") from error
    except OSError as error:
        raise Unjudged(f"{args[0]} could not run: {error}") from error
    if check and done.returncode != 0:
        raise Unjudged(f"{' '.join(args[:4])} exited {done.returncode}: "
                       f"{(done.stderr or done.stdout).strip()[:300]}")
    return done


def git(*args: str, cwd: pathlib.Path = ROOT) -> str:
    return run(["git", *args], cwd=cwd).stdout.strip()


def gh(*args: str) -> str:
    return run(["gh", *args]).stdout


def load(name: str, path: pathlib.Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def collect():
    if not hasattr(collect, "module"):
        collect.module = load("overnight_collect", ROOT / "tools" / "overnight_collect.py")
    return collect.module


def record_path(number: int, step: str) -> pathlib.Path:
    return LOG_ROOT / str(number) / f"{step}.json"


def write_record(number: int, step: str, record: dict) -> None:
    path = record_path(number, step)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(record) + "\n")
    temporary.replace(path)


def read_record(number: int, step: str) -> dict:
    path = record_path(number, step)
    try:
        record = json.loads(path.read_text())
    except (OSError, json.JSONDecodeError) as error:
        raise Unjudged(f"the {step} record {path} is missing or unreadable: {error}") from error
    if not isinstance(record, dict) or record.get("issue") != number:
        raise Unjudged(f"the {step} record {path} is not #{number}'s")
    return record


# --- read's judgement ------------------------------------------------------------------------------

def fetch_issue(number: int) -> dict:
    try:
        raw = json.loads(gh("issue", "view", str(number), "--json", FIELDS))
    except json.JSONDecodeError as error:
        raise Unjudged(f"gh gave #{number} in no JSON: {error}") from error
    if not isinstance(raw, dict):
        raise Unjudged(f"gh gave #{number} as something other than an issue")
    return raw


def branch_refs() -> list[str]:
    out = git("for-each-ref", "--format=%(refname)", "refs/heads/task/",
              "refs/remotes/origin/task/")
    names = []
    for ref in out.split():
        for prefix in ("refs/heads/", "refs/remotes/origin/"):
            if ref.startswith(prefix):
                names.append(ref[len(prefix):])
    return names


def kinds_of(paths: list[str]) -> list[str]:
    found = set()
    for path in paths:
        for kind, half in HALVES.items():
            # touches.sh exits 0 when the half is touched and 1 when not; anything else is no answer.
            done = run(["bash", str(ROOT / ".github" / "touches.sh"), half], stdin=path + "\n",
                       check=False)
            if done.returncode not in (0, 1):
                raise Unjudged(f".github/touches.sh {half} exited {done.returncode} on {path}")
            if done.returncode == 0:
                found.add(kind)
        if SCRIPT.match(path):
            found.add("script")
        if DOCS.search(path):
            found.add("docs")
    return [kind for kind, _ in KINDS if kind in found]


def judged(raw: dict, worktree: str) -> dict:
    """The record of the issue as gh gave it, with every gate judged."""
    c = collect()
    shape = c.ticket_shape()
    try:
        labels = [label["name"] for label in raw["labels"]]
        issue = c.issues_of({READY: [raw]})[0]
        assignees = [entry["login"] for entry in raw["assignees"]]
        state = raw["state"]
    except (KeyError, TypeError, ValueError) as error:
        raise Unjudged(f"gh gave #{raw.get('number')} with no readable {error}") from error

    rules = c.ownership_rules()
    tid = c.ticket_id(issue)
    owner, _ = c.owner_of(issue, tid, rules)
    touches = c.touches_of(issue, rules) or []
    slug = c.slug_of(issue.title, tid)
    branch = f"task/{tid}-{slug}"
    existing = sorted(set(branch_refs()))
    if branch not in existing:
        # A title edited since the branch was made changes the slug, not the claim (#187's rule:
        # a `task/<id>-` branch claims the ticket).
        claimed = [name for name in existing if name.startswith(f"task/{tid}-")]
        if len(claimed) == 1:
            branch = claimed[0]
    branch_exists = branch in existing

    boxes = [found.group("text").strip() for found in shape.OPEN_BOX.finditer(issue.body)]
    counted = c.open_boxes(issue.body)
    try:
        shaped = shape.issue_of(raw)
        failures = shape.judge(shaped, rules, shape.states_of(shaped))
    except (Exception, SystemExit) as error:
        raise Unjudged(f"the ticket-shape check could not judge #{issue.number}: "
                       f"{type(error).__name__}: {error}") from error

    others = sorted(set(existing) - {branch})
    clashing = [name for name in others if name.startswith(f"task/{tid}-")]
    gates = [
        {"gate": "open", "ok": state == "OPEN", "detail": f"the issue is {state}"},
        {"gate": "ready-for-agent", "ok": READY in labels,
         "detail": f"its labels are {', '.join(labels) or 'none'}"},
        {"gate": "no-open-blocker", "ok": not issue.blockers,
         "detail": "open blockers: " + (", ".join(f"#{n}" for n in issue.blockers) or "none")},
        {"gate": "ticket-shape", "ok": not failures,
         "detail": "; ".join(f"{f.rule}: {f.detail}" for f in failures) or "the shape holds"},
        {"gate": "unclaimed", "ok": (not assignees or branch_exists) and not clashing,
         "detail": (f"task branches {', '.join(clashing)} claim it" if clashing else
                    f"assigned to {', '.join(assignees)} with no branch {branch}"
                    if assignees and not branch_exists else
                    f"assignees: {', '.join(assignees) or 'none'}; branch {branch} "
                    f"{'exists' if branch_exists else 'is new'}")},
        {"gate": "boxes", "ok": len(boxes) == counted,
         "detail": f"{len(boxes)} open box text(s) read, and overnight_collect.py counts "
                   f"{counted}"},
    ]
    return {
        "issue": issue.number, "title": issue.title, "state": state, "labels": labels,
        "assignees": assignees, "blockers": list(issue.blockers), "boxes": boxes,
        "touches": touches, "owner": owner, "id": tid, "slug": slug, "branch": branch,
        "worktree": worktree, "branchExists": branch_exists,
        "kinds": kinds_of(touches), "gates": gates,
    }


def gate_verdict(record: dict, step: str) -> tuple[int, str]:
    failed = [entry for entry in record["gates"] if not entry["ok"]]
    if failed:
        return 1, (f"#{record['issue']} fails the gate(s) "
                   + "; ".join(f"{entry['gate']} ({entry['detail']})" for entry in failed))
    return 0, f"#{record['issue']} passes every gate{'' if step == 'read' else ' now'}"


def fetch() -> str:
    git("fetch", "-q", "origin")
    return git("rev-parse", "origin/main")


def first_launch(record: dict, port: int) -> dict:
    return {"scriptPath": str(ROOT / SCRIPT_PATH),
            "args": {"repo": str(ROOT), "issue": record["issue"], "wt": record["worktree"],
                     "port": port}}


def do_read(number: int, args) -> tuple[int, dict, str]:
    if not args.wt:
        raise Unjudged("read needs --wt <path>, the worktree to build in")
    base = fetch()
    record = judged(fetch_issue(number), str(pathlib.Path(args.wt).resolve()))
    record["base"] = base
    record["port"] = args.port or DEFAULT_PORT
    code, why = gate_verdict(record, "read")
    if code == 0:
        record["launch"] = first_launch(record, record["port"])
    return code, record, why


# --- launch ----------------------------------------------------------------------------------------

def seam_problems(seams, boxes: list[str]) -> list[str]:
    """What is wrong with the confirmed seams against the open boxes, each named; [] when every
    open box is in exactly one seam or in noTest."""
    if seams == "implementer":
        return []
    if not isinstance(seams, dict) or not isinstance(seams.get("seams"), list):
        return ['the seams file holds neither {"seams": [...], "noTest": [...]} nor the word '
                '"implementer"']
    unknown = sorted(set(seams) - {"seams", "noTest"})
    if unknown:
        return [f"{', '.join(unknown)}: not a field; a seams file has seams and noTest"]
    no_test = seams.get("noTest", [])
    if not isinstance(no_test, list):
        return ["noTest is not a list"]
    problems, holders = [], {}

    def index(value, where: str) -> int | None:
        if isinstance(value, bool) or not isinstance(value, int):
            problems.append(f"{where}: a box is a 1-based integer index, not {value!r}")
            return None
        if not 1 <= value <= len(boxes):
            problems.append(f"{where}: box {value} is not one of the ticket's {len(boxes)} open "
                            "box(es)")
            return None
        return value

    for at, seam in enumerate(seams["seams"], 1):
        if not isinstance(seam, dict):
            problems.append(f"seam {at} is not an object")
            continue
        name = seam.get("name") if isinstance(seam.get("name"), str) else f"seam {at}"
        for field in ("name", "interface"):
            if not (isinstance(seam.get(field), str) and seam[field].strip()):
                problems.append(f"{name}: {field} is missing or empty")
        if not (isinstance(seam.get("boxes"), list) and seam["boxes"]):
            problems.append(f"{name}: boxes is missing or empty")
            continue
        for value in seam["boxes"]:
            box = index(value, f"{name}'s boxes")
            if box is not None:
                holders.setdefault(box, []).append(name)
    for at, entry in enumerate(no_test, 1):
        if not isinstance(entry, dict):
            problems.append(f"noTest entry {at} is not an object")
            continue
        if not (isinstance(entry.get("why"), str) and entry["why"].strip()):
            problems.append(f"noTest entry {at}: why is missing or empty")
        box = index(entry.get("box"), f"noTest entry {at}")
        if box is not None:
            holders.setdefault(box, []).append("noTest")
    for box, text in enumerate(boxes, 1):
        held = holders.get(box, [])
        if not held:
            problems.append(f"box {box} ({text!r}) is in no seam and not in noTest")
        elif len(held) > 1:
            problems.append(f"box {box} is in {' and '.join(held)}; a box belongs to one")
    return problems


def do_launch(number: int, args) -> tuple[int, dict, str]:
    ticket = read_record(number, "read")
    if not args.seams:
        raise Unjudged("launch needs --seams <file>, the confirmed seams, or --seams implementer")
    if args.seams == "implementer":
        seams = "implementer"
    else:
        try:
            seams = json.loads(pathlib.Path(args.seams).read_text())
        except (OSError, json.JSONDecodeError) as error:
            raise Unjudged(f"the seams file {args.seams} is missing or unreadable: {error}") \
                from error
    record = {"issue": number, "seamsFile": args.seams, "seams": seams,
              "boxes": ticket.get("boxes", [])}
    if ticket.get("verdict") != "pass" or "launch" not in ticket:
        return 1, record, "read did not pass, so there is nothing to launch: run read again"
    problems = seam_problems(seams, ticket["boxes"])
    if problems:
        return 1, record, "; ".join(problems)
    second = copy.deepcopy(ticket["launch"])
    second["args"]["seams"] = seams
    record["launch"] = second
    held = ("the implementer's own" if seams == "implementer" else
            f"{len(seams['seams'])} seam(s) and {len(seams.get('noTest', []))} box(es) with no "
            "test")
    return 0, record, f"#{number}'s {len(ticket['boxes'])} open box(es) are covered: {held}"


# --- start -----------------------------------------------------------------------------------------

def worktrees() -> dict[str, str]:
    """Each worktree's path by the branch it has checked out."""
    found, path = {}, None
    for line in git("worktree", "list", "--porcelain").splitlines():
        if line.startswith("worktree "):
            path = line[len("worktree "):]
        elif line.startswith("branch refs/heads/") and path:
            found[line[len("branch refs/heads/"):]] = path
    return found


def passed_on(number: int, head: str) -> bool:
    try:
        record = read_record(number, "check")
    except Unjudged:
        return False
    return record.get("state") == "pass" and record.get("head") == head


def do_start(number: int, args) -> tuple[int, dict, str]:
    earlier = read_record(number, "read")
    fetch()
    record = judged(fetch_issue(number), earlier["worktree"])
    record["base"] = earlier["base"]
    code, why = gate_verdict(record, "start")
    if code:
        record["state"] = "refused"
        return code, record, why
    if record["boxes"] != earlier.get("boxes"):
        # The confirmed seams number read's boxes, and the build is handed these: when the two lists
        # differ, a seam's box number may point at another box (#1594's review).
        record["state"] = "refused"
        return 1, record, (f"#{number}'s open boxes are not the ones read recorded, so the seams "
                           "confirmed against them may name other boxes: run read and launch again")
    branch, wt = record["branch"], pathlib.Path(record["worktree"])
    local = run(["git", "rev-parse", "--verify", "-q", f"refs/heads/{branch}"],
                check=False).returncode == 0
    if record["branchExists"]:
        held = worktrees().get(branch)
        if held:
            wt = pathlib.Path(held)
        elif wt.exists():
            raise Failed(f"{wt} exists and does not have {branch} checked out")
        elif local:
            git("worktree", "add", "-q", str(wt), branch)
        else:
            git("worktree", "add", "-q", "--track", "-b", branch, str(wt), f"origin/{branch}")
        head = git("rev-parse", "HEAD", cwd=wt)
        record["state"] = "finished" if passed_on(number, head) else "continued"
    else:
        if wt.exists() and any(wt.iterdir()):
            raise Failed(f"{wt} exists and is not empty, so no worktree is added there")
        gh("issue", "edit", str(number), "--add-assignee", "@me")
        added = run(["git", "worktree", "add", "-q", "--no-track", "-b", branch, str(wt),
                     record["base"]], check=False)
        if added.returncode != 0:
            cause = f"git worktree add failed: {added.stderr.strip()[:300]}"
            try:
                gh("issue", "edit", str(number), "--remove-assignee", "@me")
            except Unjudged as undo:
                raise Unjudged(f"{cause}; and the assignment could not be undone, so #{number} "
                               f"is still assigned: {undo}") from undo
            raise Failed(f"{cause}; the assignment was undone")
        head = git("rev-parse", "HEAD", cwd=wt)
        record["state"] = "new"
    record["worktree"] = str(wt)
    record["head"] = head
    record["fork"] = git("merge-base", head, "origin/main")
    return 0, record, f"#{number} is {record['state']} on {branch} at {head[:12]} in {wt}"


# --- check -----------------------------------------------------------------------------------------

def alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def log_state(log: pathlib.Path) -> tuple[str | None, str | None]:
    """The log's HEAD= sha and its verdict line, each None when absent."""
    try:
        lines = log.read_text(errors="replace").splitlines()
    except OSError:
        return None, None
    logged = lines[0][len("HEAD="):] if lines and lines[0].startswith("HEAD=") else None
    last = next((line for line in reversed(lines) if line.strip()), "")
    verdict = last if last.startswith((PASS_LINE, FAIL_LINE)) else None
    return logged, verdict


def last_lines(log: pathlib.Path, count: int = 25) -> list[str]:
    try:
        return log.read_text(errors="replace").splitlines()[-count:]
    except OSError:
        return []


def shortened(name: str, longest: float) -> float:
    """The environment's value of name in seconds when it is shorter than longest, which the
    companion uses, never longer."""
    try:
        return min(longest, max(0.0, float(os.environ.get(name, longest))))
    except ValueError:
        return longest


def bound() -> float:
    """How long a call waits for a verdict."""
    return shortened("IMPLEMENT_TICKET_WAIT", WAIT_BOUND - JUDGING_RESERVE)


def reserve() -> float:
    """How long a call has, after its wait, to judge what it waited for."""
    return shortened("IMPLEMENT_TICKET_RESERVE", JUDGING_RESERVE)


# The check's first act, run as `python3 -I -c LAUNCHER <pid file> <lock> <claim> <command...>`
# (#1564): SIGINT back to its default (#1532), then, under the lock every call's look at the pid
# file takes, its own pid in place of the claim, written to a side file and renamed so no reader
# sees half of it, then exec, which keeps that pid. It names itself only while the file still holds
# the claim of the call that started it: a call that has since cleared that claim as stale may have
# started a check of its own, so this one exits 3 without running. The lock's file is closed before
# the exec, so the check holds no lock while it runs.
LAUNCHER = """\
import fcntl, os, signal, sys
signal.signal(signal.SIGINT, signal.SIG_DFL)
pid_file, lock, claim, command = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
with open(lock, "a") as held:
    fcntl.flock(held, fcntl.LOCK_EX)
    try:
        with open(pid_file) as current:
            mine = current.read().strip() == claim
    except FileNotFoundError:
        mine = False
    if not mine:
        sys.exit(3)
    named = pid_file + ".named"
    with open(named, "w") as handle:
        handle.write(f"{os.getpid()}\\n")
    os.replace(named, pid_file)
os.execvp(command[0], command)
"""


def launch(wt: pathlib.Path, fork: str, port: int, log: pathlib.Path,
           pid_file: pathlib.Path, lock: pathlib.Path) -> subprocess.Popen | None:
    """Start the check detached, as GATE does, once the pid file is ours; None when another call
    holds it. The claim reads claim_for(this process) until the check replaces it with its own pid
    by LAUNCHER: the tool never writes the check's pid, so a tool killed after Popen still leaves
    the file naming the check (#1564). A launch that fails removes its claim."""
    try:
        claim = os.open(pid_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o644)
    except FileExistsError:
        return None
    try:
        os.write(claim, f"{claim_for(os.getpid())}\n".encode())
        log.unlink(missing_ok=True)
        script = ('{ echo "HEAD=$(git -C "$1" rev-parse HEAD)"; '
                  'tools/check.sh --full --base "$2" --port "$3"; } > "$4" 2>&1')
        process = subprocess.Popen(
            [sys.executable, "-I", "-c", LAUNCHER, str(pid_file), str(lock),
             claim_for(os.getpid()),
             "bash", "-c", script, "bash", str(wt), fork, str(port), str(log)],
            cwd=wt, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, start_new_session=True)
    except BaseException:
        pid_file.unlink(missing_ok=True)
        raise
    finally:
        os.close(claim)
    return process


# A pid file left empty, or holding a claim whose writer is gone, this long is a claim whose check
# never named itself; one younger is read as CLAIMING, a check being started. A claim whose writer
# lives is CLAIMING at any age: that call is starting a check, or watching one it started (#1564).
EMPTY_CLAIM = 10.0
CLAIMING = -1
CLAIM_PREFIX = "claim "


def claim_for(pid: int) -> str:
    """The pid file's text while the call with this pid starts a check, before the check names
    itself."""
    return f"{CLAIM_PREFIX}{pid}"


def as_pid(text: str) -> int:
    """The pid text names, or 0 when it names none. Only the form str() writes is a pid: `0123` or
    a non-ASCII digit, which int() reads, would make the pid and the text drop_claim compares
    disagree, and the file would never be dropped (#1569's review)."""
    return int(text) if text.isascii() and text.isdigit() and str(int(text)) == text else 0


def claim_writer(text: str) -> int | None:
    """The pid of the call whose claim text is, or None when text is no claim; 0 for a claim that
    names no pid."""
    if not text.startswith(CLAIM_PREFIX):
        return None
    return as_pid(text[len(CLAIM_PREFIX):])


@contextlib.contextmanager
def locked(path: pathlib.Path):
    """An exclusive lock on path, held for the block."""
    with open(path, "a", encoding="utf-8") as handle:
        fcntl.flock(handle, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(handle, fcntl.LOCK_UN)


def running_pid(pid_file: pathlib.Path) -> int | None:
    """The pid the issue's pid file names; CLAIMING for a file still empty within EMPTY_CLAIM, or a
    claim whose writer lives or that is younger than EMPTY_CLAIM; or None when there is none,
    removing a file that is no pid, or an empty file or claim past EMPTY_CLAIM whose writer is gone.
    Whether the pid still runs is live()'s to say, or the Popen's for this call's own check."""
    try:
        text = pid_file.read_text().strip()
        age = time.time() - pid_file.stat().st_mtime
    except FileNotFoundError:
        return None
    writer = claim_writer(text)
    if writer is not None:
        if age < EMPTY_CLAIM or (writer > 0 and alive(writer)):
            return CLAIMING
        drop_claim(pid_file, text)
        return None
    if not text and age < EMPTY_CLAIM:
        return CLAIMING
    pid = as_pid(text)
    if pid > 0:
        return pid
    drop_claim(pid_file, text)
    return None


def live(pid: int) -> bool:
    """Whether a check another call started still runs: a claim being written, or a pid alive."""
    return pid == CLAIMING or alive(pid)


def claim_text(pid_file: pathlib.Path) -> str | None:
    """The pid file's text, or None when there is no file: no call holds the issue."""
    try:
        return pid_file.read_text().strip()
    except FileNotFoundError:
        return None


def drop_claim(pid_file: pathlib.Path, text: str) -> None:
    """Remove the pid file only while it still holds text, so another call's fresh claim stays."""
    try:
        if pid_file.read_text().strip() == text:
            pid_file.unlink()
    except FileNotFoundError:
        pass


def dirty(wt: pathlib.Path) -> list[str]:
    return [line for line in git("status", "--porcelain", "--untracked-files=all",
                                 cwd=wt).splitlines() if line]


def covered(path: str, touches: list[str]) -> bool:
    for entry in touches:
        entry = entry.strip("/")
        if path == entry or path.startswith(entry + "/") or fnmatch.fnmatch(path, entry):
            return True
    return False


def ownership(fork: str, head: str, owner: str) -> dict:
    """A-05 on the fork and head by sha, run by the main checkout's copy in the main checkout, which
    shares the worktree's objects: the branch under judgement never judges itself."""
    done = run([sys.executable, "-I", str(ROOT / ".github" / "path-ownership.py"), fork, head],
               check=False)
    output = done.stdout.strip()
    named = [found.group("owner") for found in A05_WHOLE.finditer(output)]
    named += [found.group("owner") for found in A05_COMMIT.finditer(output)]
    named = list(dict.fromkeys(named))
    ok = done.returncode == 0 and named == [owner]
    return {"ok": ok, "exit": done.returncode, "named": named, "output": output[-2000:]}


def do_check(number: int, args) -> tuple[int, dict, str]:
    ticket = read_record(number, "read")
    begun = read_record(number, "start")
    if begun.get("state") not in ("new", "continued", "finished"):
        raise Unjudged(f"the start record made no worktree: its state is {begun.get('state')}")
    wt = pathlib.Path(begun["worktree"])
    if not wt.is_dir():
        raise Unjudged(f"the worktree {wt} that start made is gone")
    port = args.port or DEFAULT_PORT
    directory = LOG_ROOT / str(number)
    directory.mkdir(parents=True, exist_ok=True)
    log, pid_file = directory / "check.log", directory / "check.pid"
    # The wait ends reserve() before the call's deadline, so the judging after it has that long.
    wait_until = DEADLINE - reserve()
    head = git("rev-parse", "HEAD", cwd=wt)
    fork = git("merge-base", head, "origin/main", cwd=wt)
    record = {"issue": number, "worktree": str(wt), "head": head, "fork": fork, "port": port,
              "log": str(log), "started": False}

    # Every look at the pid file and the log, and every claim or drop of it, happens under one
    # lock, which LAUNCHER also takes to name the check, so two calls never both read a claim as
    # stale and both start a check, and no call clears a claim while its check names itself.
    lock = directory / "check.lock"
    changes = dirty(wt)
    with locked(lock):
        claimed = running_pid(pid_file)
    if changes and (claimed is None or not live(claimed)):
        record.update(state="fail", clean=False, uncommitted=changes)
        return 1, record, (f"the worktree has {len(changes)} uncommitted change(s), so no check "
                           "is started: commit them first")

    process = None
    while True:
        with locked(lock):
            pid = running_pid(pid_file)
            logged, verdict = log_state(log)
            current = git("rev-parse", "HEAD", cwd=wt)
            if pid is None:
                # head is the head this call began on, current the worktree's now: a log of either
                # is judged, and the judgement below fails one that is not current's.
                if logged in (head, current) and verdict:
                    break
                if logged in (head, current) and record["started"]:
                    record.update(state="fail", logHead=logged, tail=last_lines(log))
                    return 1, record, f"the check stopped with no verdict line; its log is {log}"
                if current != head:
                    head, fork = current, git("merge-base", current, "origin/main", cwd=wt)
                    record.update(head=head, fork=fork)
                process = launch(wt, fork, port, log, pid_file, lock)
                if process is not None:
                    record.update(started=True, pid=process.pid)
            else:
                own_claim = claim_for(os.getpid())
                if (pid == CLAIMING and process is not None and process.poll() is not None
                        and claim_text(pid_file) == own_claim):
                    drop_claim(pid_file, own_claim)
                    record.update(state="fail", launchExit=process.returncode,
                                  tail=last_lines(log))
                    return 1, record, (f"the check ended (exit {process.returncode}) before it "
                                       f"named itself in {pid_file}, so it never ran")
                record["pid"] = pid
                ours = process is not None and process.pid == pid
                if not (process.poll() is None if ours else live(pid)):
                    drop_claim(pid_file, str(pid))   # it ended; a later pass reads its log
        # Every pass, one that launched or dropped included, reaches the bound and the sleep, so a
        # pid file a pass cannot drop holds the call to its bound rather than spinning it (#1569's
        # review).
        if time.monotonic() >= wait_until:
            whose = "this head" if logged in (head, current) else f"{(logged or '?')[:12]}"
            record.update(state="running", logHead=logged, tail=last_lines(log, 5))
            held = "being started" if pid in (None, CLAIMING) else f"pid {pid}"
            return 2, record, (f"no verdict yet: the check for {whose} is still running "
                               f"({held}); run check again")
        time.sleep(POLL)

    head = git("rev-parse", "HEAD", cwd=wt)
    fork = git("merge-base", head, "origin/main", cwd=wt)
    changes = dirty(wt)
    owned = ownership(fork, head, ticket["owner"])
    changed = [path for path in git("diff", "--no-renames", "--name-only", fork, head,
                                    cwd=wt).splitlines() if path]
    record.update(head=head, fork=fork, logHead=logged, lastLine=verdict, clean=not changes,
                  uncommitted=changes, ownership=owned,
                  outsideTouches=[path for path in changed
                                  if not covered(path, ticket["touches"])],
                  tail=last_lines(log))
    reasons = []
    if not verdict.startswith(PASS_LINE):
        reasons.append(f"the log's last line is {verdict!r}")
        # The main checkout's copy, run in the worktree the log was written in.
        reach = run([sys.executable, "-I", str(ROOT / "tools" / "test_reach.py"), "--log",
                     str(log), "--base", fork, "--head", head], cwd=wt, check=False)
        try:
            record["testReach"] = json.loads(reach.stdout)
        except json.JSONDecodeError:
            record["testReach"] = {"exit": reach.returncode,
                                   "output": (reach.stdout + reach.stderr).strip()[-2000:]}
    if logged != head:
        reasons.append(f"the log ran on {(logged or 'no HEAD= line')[:12]}, and the worktree is "
                       f"at {head[:12]}")
    if changes:
        reasons.append(f"the worktree has {len(changes)} uncommitted change(s)")
    if not owned["ok"]:
        reasons.append(f"A-05 names {', '.join(owned['named']) or 'no owner'} (exit "
                       f"{owned['exit']}), not {ticket['owner']} alone")
    if reasons:
        record["state"] = "fail"
        return 1, record, "; ".join(reasons)
    record["state"] = "pass"
    return 0, record, f"the full check passed on {head[:12]}, all {ticket['owner']}'s"


# --- release ---------------------------------------------------------------------------------------

def do_release(number: int, args) -> tuple[int, dict, str]:
    ticket = read_record(number, "read")
    try:
        begun = read_record(number, "start")
    except Unjudged:
        begun = ticket
    gh("issue", "edit", str(number), "--remove-assignee", "@me")
    # Read back, not assumed: start continues an issue another login holds when its task branch
    # exists, and removing @me leaves that login assigned (#1569's review).
    try:
        remaining = [entry["login"] for entry in fetch_issue(number)["assignees"]]
    except (KeyError, TypeError) as error:
        raise Unjudged(f"the removal was sent, but gh gave #{number}'s assignees back unreadable: "
                       f"{error}") from error
    except Unjudged as error:
        raise Unjudged(f"the removal was sent, but #{number}'s assignees could not be read back: "
                       f"{error}") from error
    branch = begun.get("branch") or ticket["branch"]
    wt = begun.get("worktree") or ticket["worktree"]
    kept = run(["git", "rev-parse", "--verify", "-q", f"refs/heads/{branch}"],
               check=False).returncode == 0
    record = {"issue": number, "assignees": remaining, "branch": branch, "worktree": wt,
              "branchKept": kept, "worktreeKept": pathlib.Path(wt).is_dir()}
    held = (f"still assigned to {', '.join(remaining)}" if remaining else "unassigned")
    return 0, record, (f"#{number} is {held}; branch {branch} "
                       f"{'kept' if kept else 'absent'}, worktree {wt} "
                       f"{'kept' if record['worktreeKept'] else 'absent'}")


# --- the command line ------------------------------------------------------------------------------

STEPS = {"read": do_read, "launch": do_launch, "start": do_start, "check": do_check,
         "release": do_release}


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise Unjudged(message)


def main(argv: list[str]) -> int:
    global DEADLINE
    DEADLINE = time.monotonic() + bound() + reserve()
    step = argv[0] if argv and argv[0] in STEPS else "usage"
    number = None
    try:
        parser = Parser(prog="implement_ticket.py", add_help=False)
        parser.add_argument("step", choices=sorted(STEPS))
        parser.add_argument("issue", type=int)
        parser.add_argument("--wt")
        parser.add_argument("--port", type=int)
        parser.add_argument("--seams")
        args = parser.parse_args(argv)
        number = args.issue
        if args.wt and step != "read":
            raise Unjudged("--wt is read's alone; later steps take it from read's record")
        if args.port is not None and not 1024 <= args.port <= 65535:
            raise Unjudged("--port is an integer from 1024 to 65535, as the script takes it")
        if args.port and step not in ("read", "check"):
            raise Unjudged("--port is read's and check's alone")
        if args.seams and step != "launch":
            raise Unjudged("--seams is launch's alone")
        os.chdir(ROOT)
        try:
            code, record, why = STEPS[step](number, args)
        except Failed as failure:
            code, record, why = 1, {"issue": number, "state": "fail"}, str(failure)
    except (Exception, SystemExit) as error:
        why = str(error) if isinstance(error, Unjudged) else f"{type(error).__name__}: {error}"
        code, record = 2, ({"issue": number, "state": "unjudged"} if number else None)
    if record is not None:
        record = {"step": step, **record, "verdict": "pass" if code == 0 else "fail",
                  "exit": code, "why": why}
        if "launch" in record:
            # The launch line ends the record, as the session copies it from there.
            record["launch"] = record.pop("launch")
        if number is not None:
            try:
                write_record(number, step, record)
            except OSError as error:
                code, why = 2, f"the record could not be written: {error}"
        print(json.dumps(record))
    print(f"implement-ticket: {step}: {'pass' if code == 0 else 'fail'}: {why}")
    return code


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
