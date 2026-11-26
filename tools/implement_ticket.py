#!/usr/bin/env python3
"""The deterministic half of the foreground implement harness (#1563). Owner: integrator.

A foreground session building one ticket runs these, from the main checkout, in this order:

    python3 -I tools/implement_ticket.py read <n> --wt <path>   gate the ticket and record it
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

start reads the issue again and applies the same gates to what it finds now, so a ticket
relabelled or claimed since read is refused before anything is written. With no task branch it
assigns the issue (`gh issue edit --add-assignee @me`) and adds the worktree on a new branch at
read's base, undoing the assignment when the worktree cannot be added. With a branch it adds the
worktree on it, or reuses the one that has it checked out, and assigns nobody anew. Its state is
`new`, `continued`, or `finished` when check.json records a pass on the branch's head; it records
the fork point (`git merge-base` of the branch and origin/main) and the head.

check runs `tools/check.sh --full --base <fork> --port <p>` in the worktree, detached, by the
recipe in .claude/workflows/overnight.js's GATE: SIGINT reset to its default before bash starts
(#1532), stdout and stderr to one log, check.log, whose first line is `HEAD=<sha>`, and the pid in
check.pid beside it. One check runs per issue: the pid file is claimed with O_EXCL under a lock on
check.lock, and a call that
finds a check running waits on it rather than starting a second; one running for an earlier head is
waited out, and then this head's starts. A pid file naming a pid that is gone is stale and removed. Every call returns within
WAIT_BOUND seconds, with state `running` when there is no verdict yet; IMPLEMENT_TICKET_WAIT may
shorten the bound, never lengthen it. A log for the worktree's head that already has a verdict is
judged without a new run. The verdict passes only when the log's last line starts `check: pass:`,
its `HEAD=` equals the worktree's HEAD, the worktree has no uncommitted or untracked change, and
`.github/path-ownership.py <fork> <head>`, the main checkout's copy, names the ticket's owner and
no other, so a branch that edits A-05 does not judge itself. The record lists
the changed files outside the Touches paths without failing on them, and on a red check carries what
`tools/test_reach.py` prints for the log.

release removes the assignee and records the branch and worktree it left in place.
"""
from __future__ import annotations

import argparse
import contextlib
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
# The Bash tool's limit is 600 s. A call returns within WAIT_BOUND, so its wait ends JUDGING_RESERVE
# earlier, leaving room for A-05 and test_reach.py after it.
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

def run(args: list[str], cwd: pathlib.Path = ROOT, stdin: str | None = None,
        check: bool = True) -> subprocess.CompletedProcess:
    try:
        done = subprocess.run(args, cwd=cwd, capture_output=True, text=True, input=stdin,
                              check=False)
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


def do_read(number: int, args) -> tuple[int, dict, str]:
    if not args.wt:
        raise Unjudged("read needs --wt <path>, the worktree to build in")
    base = fetch()
    record = judged(fetch_issue(number), str(pathlib.Path(args.wt).resolve()))
    record["base"] = base
    code, why = gate_verdict(record, "read")
    return code, record, why


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


def bound() -> float:
    """How long a call waits for a verdict: IMPLEMENT_TICKET_WAIT seconds when it is shorter, which
    the companion uses, never longer."""
    longest = WAIT_BOUND - JUDGING_RESERVE
    try:
        return min(longest, max(0.0, float(os.environ.get("IMPLEMENT_TICKET_WAIT", longest))))
    except ValueError:
        return longest


def launch(wt: pathlib.Path, fork: str, port: int, log: pathlib.Path,
           pid_file: pathlib.Path) -> subprocess.Popen | None:
    """Start the check detached, as GATE does, once the pid file is ours; None when another call
    holds it. The claim names this process until the check's pid replaces it, so a call that reads
    the file in between sees a live pid and waits; a launch that fails removes its claim."""
    try:
        claim = os.open(pid_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o644)
    except FileExistsError:
        return None
    try:
        os.write(claim, f"{os.getpid()}\n".encode())
        log.unlink(missing_ok=True)
        script = ('{ echo "HEAD=$(git -C "$1" rev-parse HEAD)"; '
                  'tools/check.sh --full --base "$2" --port "$3"; } > "$4" 2>&1')
        process = subprocess.Popen(
            [sys.executable, "-I", "-c",
             "import os, signal, sys; signal.signal(signal.SIGINT, signal.SIG_DFL); "
             "os.execvp(sys.argv[1], sys.argv[1:])",
             "bash", "-c", script, "bash", str(wt), fork, str(port), str(log)],
            cwd=wt, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, start_new_session=True)
        os.lseek(claim, 0, os.SEEK_SET)
        os.ftruncate(claim, 0)
        os.write(claim, f"{process.pid}\n".encode())
    except BaseException:
        pid_file.unlink(missing_ok=True)
        raise
    finally:
        os.close(claim)
    return process


# A pid file left empty this long is a claim whose writer died between creating and writing it;
# one younger is read as CLAIMING, a check being started.
EMPTY_CLAIM = 10.0
CLAIMING = -1


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
    """The live pid the issue's pid file names, or None, removing a stale file: one naming a pid
    that is gone, one that is no pid, or one left empty past EMPTY_CLAIM. A file still empty
    within it is a claim being written, CLAIMING."""
    try:
        text = pid_file.read_text().strip()
        age = time.time() - pid_file.stat().st_mtime
    except FileNotFoundError:
        return None
    if not text and age < EMPTY_CLAIM:
        return CLAIMING
    pid = int(text) if text.isdigit() else 0
    if pid > 0 and alive(pid):
        return pid
    drop_claim(pid_file, text)
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
    started_at = time.monotonic()
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
    limit = bound()
    head = git("rev-parse", "HEAD", cwd=wt)
    fork = git("merge-base", head, "origin/main", cwd=wt)
    record = {"issue": number, "worktree": str(wt), "head": head, "fork": fork, "port": port,
              "log": str(log), "started": False}

    changes = dirty(wt)
    if changes and running_pid(pid_file) is None:
        record.update(state="fail", clean=False, uncommitted=changes)
        return 1, record, (f"the worktree has {len(changes)} uncommitted change(s), so no check "
                           "is started: commit them first")

    # Every look at the pid file and the log, and every claim or drop of it, happens under one
    # lock, so two calls never both read a claim as stale and both start a check.
    lock = directory / "check.lock"
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
                process = launch(wt, fork, port, log, pid_file)
                if process is not None:
                    record.update(started=True, pid=process.pid)
                continue
            record["pid"] = pid
            ours = process is not None and process.pid == pid
            if not (process.poll() is None if ours else (pid == CLAIMING or alive(pid))):
                drop_claim(pid_file, str(pid))   # it ended; the next pass reads its log
                continue
        if time.monotonic() - started_at >= limit:
            whose = "this head" if logged in (head, current) else f"{(logged or '?')[:12]}"
            record.update(state="running", logHead=logged, tail=last_lines(log, 5))
            return 2, record, (f"no verdict yet: the check for {whose} is still running "
                               f"(pid {pid}); run check again")
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
    branch = begun.get("branch") or ticket["branch"]
    wt = begun.get("worktree") or ticket["worktree"]
    kept = run(["git", "rev-parse", "--verify", "-q", f"refs/heads/{branch}"],
               check=False).returncode == 0
    record = {"issue": number, "branch": branch, "worktree": wt, "branchKept": kept,
              "worktreeKept": pathlib.Path(wt).is_dir()}
    return 0, record, (f"#{number} is unassigned; branch {branch} "
                       f"{'kept' if kept else 'absent'}, worktree {wt} "
                       f"{'kept' if record['worktreeKept'] else 'absent'}")


# --- the command line ------------------------------------------------------------------------------

STEPS = {"read": do_read, "start": do_start, "check": do_check, "release": do_release}


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise Unjudged(message)


def main(argv: list[str]) -> int:
    step = argv[0] if argv and argv[0] in STEPS else "usage"
    number = None
    try:
        parser = Parser(prog="implement_ticket.py", add_help=False)
        parser.add_argument("step", choices=sorted(STEPS))
        parser.add_argument("issue", type=int)
        parser.add_argument("--wt")
        parser.add_argument("--port", type=int)
        args = parser.parse_args(argv)
        number = args.issue
        if args.wt and step != "read":
            raise Unjudged("--wt is read's alone; later steps take it from read's record")
        if args.port and step != "check":
            raise Unjudged("--port is check's alone")
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
