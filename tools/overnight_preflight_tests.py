#!/usr/bin/env python3
"""Test companion for tools/overnight_preflight.sh: one verdict per launch precondition of the
overnight workflow, in order, and the launch arguments last.

Owner: integrator. Built in #378.

**What runs is the script itself.** Each case copies the tracked file's current bytes, whole, into
the tools/ directory of a throwaway main checkout, and runs that copy with `/bin/bash`, so the
script finds the checkout the way it does on a machine, from where it sits, and a Mac with
Homebrew's bash first on PATH still tests the 3.2 macOS ships. When SCRIPT_TESTS_REQUIRE_BASH is
set (CI's macOS job sets it to 3.2) a different version fails the run rather than skipping it.

**The shell that runs it.** Line 5 judges the script's parent process, which on a launch is the
Bash tool's shell. A case runs it as `/bin/bash -c '/bin/bash "$0" "$1"; exit $?'`, whose parent is
that outer bash: a `-c` string whose last command is the script would let bash exec the script in
its own place, and the parent would be this companion. The case that wants a parent other than bash
runs the script straight from Python, so the parent is this process, on every machine alike.

**A constructed environment, inheriting nothing.** PATH is the case's own bin directory; HOME is
inside the case's throwaway directory, so `~/.worktrees/overnight-<date>` is the case's own;
TMPDIR too, so a case can see the script's temporary directory removed; GIT_CONFIG_NOSYSTEM=1,
GIT_CONFIG_GLOBAL names an empty file, and LC_ALL=C. A real gh on a runner's PATH is never reached.

**Real git; a double for gh and for collect.** Every git call the script makes works offline, so
each case builds a bare origin, a clone of it standing in for whoever pushes to main, and the main
checkout, a second clone. gh is the call that leaves the machine, so the case's bin holds a strict gh
double that answers the three reads the script asks for (the needs-triage listing, a timeline,
and an issue's `gh issue view --json` for the shape line), from the case's canned output, records
every call with the directory it ran in, and exits 97 on anything else, so a new gh call, or any
write to the tracker, turns a case red. tools/overnight_collect.py is the case's own stub committed
on origin/main: the script runs origin/main's copy, extracted outside the checkout, so the stub
prints a canned summary line and answer and records how it was called and from where. A case whose
origin/main moves commits a stub that says so, which is how a case tells origin's copy from the
checkout's. Imported, as tools/ticket_shape.py imports it, the stub is the real collect's OWNER and
ROLES and does nothing else.

**The shape line runs the real check.** Each case commits this checkout's tools/ticket_shape.py,
.github/path-ownership.py and OWNERSHIP.md on origin/main, so the shape line judges a canned issue
with the rules the run will use, and a case proves what the check fails, not what a stub prints.

**The launch arguments meet the workflow's own validation.** The arguments case runs
.claude/workflows/overnight.js, this checkout's, under node, with the arguments the script printed
and every agent call answered by a sentinel, as .claude/workflows/overnight_tests.mjs runs it: the
script either halts on a bad launch before its first agent or reaches Collect. node is required:
without it the case fails, naming it.

**The harness fixture is read back whole (#485).** A case run with --fixture reads the file it
wrote and compares it with the arguments printed, the forecast's tickets and the ready-for-agent ids
the skip leaves. The harness itself, .claude/workflows/overnight_harness.mjs, is git-ignored and not
in the checkout, so no case runs it; the fixture's keys are the ones it reads.

The cases run at the same time on the threads of tools/companion_runner.py; each keeps to its own
throwaway directory and passes its own environment to what it runs.

Run: python3 -I tools/overnight_preflight_tests.py
"""

from __future__ import annotations

import datetime
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
SCRIPT = ROOT / "tools" / "overnight_preflight.sh"
spec = importlib.util.spec_from_file_location("overnight_collect", ROOT / "tools" / "overnight_collect.py")
real_collect = importlib.util.module_from_spec(spec)
spec.loader.exec_module(real_collect)
# The real files the shape line's tools/ticket_shape.py runs on, committed on each case's origin/main
# at the same paths: the check itself, A-05's ownership reader and the ownership it reads.
SHAPE_FILES = ("tools/ticket_shape.py", ".github/path-ownership.py", "OWNERSHIP.md")
# The real files the decision line's tools/decision_citation.py runs on (#663): the reader, and the
# guide whose "The human's account is" line names the account a decision is read from.
DECISION_FILES = ("tools/decision_citation.py", "docs/agents/tracker-policy.md")
spec = importlib.util.spec_from_file_location("decision_citation", ROOT / "tools" / "decision_citation.py")
decision_citation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(decision_citation)
LOGIN = decision_citation.named_account(ROOT / decision_citation.GUIDE)
# Two files every case's origin/main holds under docs/adr/, so the adr exemption has an existing ADR
# file and README to find, and docs/adr/0099-new.md is a path origin/main lacks.
ADR_FILES = {"docs/adr/README.md": "# Decisions\n", "docs/adr/0026-example.md": "# ADR-0026\n"}
# The fields the gh double answers for `gh issue view`, read from the check itself: the script
# copies them, so a FIELDS change the script misses turns a case red here.
spec = importlib.util.spec_from_file_location("ticket_shape", ROOT / "tools" / "ticket_shape.py")
ticket_shape = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ticket_shape)
SHAPE_FIELDS = ticket_shape.FIELDS
WORKFLOW = ROOT / ".claude" / "workflows" / "overnight.js"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"
PYTHON = os.path.realpath(sys.executable)
DATE = "2026-10-26"

# The real tools the script runs, besides gh, which gets a double. Each is reached from the case's
# bin directory, which is the whole of the case's PATH.
TOOLS = ("git", "ps", "tar", "mktemp", "mkdir", "rm")

# The lines, by name, in the order the ticket gives them.
LINES = ["fetch", "tracked", "workflow", "run", "shell", "forecast", "demoted", "claims", "shape",
         "adr", "decision", "unblocks"]
# The lines that fail with the forecast, since each reads collect's answer.
WITH_FORECAST = ("forecast", "claims", "shape", "adr", "decision", "unblocks")

LIST_TRIAGE = ["issue", "list", "--state", "open", "--label", "needs-triage", "--limit", "500",
               "--json", "number"]


def timeline_call(number: int) -> list[str]:
    return ["api", "--paginate", "--slurp", f"repos/{{owner}}/{{repo}}/issues/{number}/timeline?per_page=100"]


def view_call(number: int) -> list[str]:
    return ["issue", "view", str(number), "--json", SHAPE_FIELDS]


def comments_call(number: int) -> list[str]:
    return ["api", "--paginate", "--slurp", f"repos/{{owner}}/{{repo}}/issues/{number}/comments?per_page=100"]


def labels_call(number: int) -> list[str]:
    return ["issue", "view", str(number), "--json", "labels"]


def comment(body: str, *, login: str = LOGIN, hours: float = 1, edited: float | None = None,
            id: int = 1) -> dict:
    """An issue comment as GitHub's REST listing gives one, trimmed to the fields read."""
    return {"id": id, "body": body, "user": {"login": login},
            "html_url": f"https://github.com/o/r/issues/1#issuecomment-{id}",
            "created_at": ago(hours), "updated_at": ago(hours if edited is None else edited)}


ACCEPT_NO = "Decision: accept\nADR: no\n\nThe why."


def ticket(id: str, issue: int, owner: str, status: str, open_boxes: int = 2,
           blocked_by: tuple = ()) -> dict:
    return {"id": id, "issue": issue, "owner": owner, "status": status, "openBoxes": open_boxes,
            "blockedBy": list(blocked_by)}


def issue_json(number: int, label: str, body: str, also: tuple = ()) -> dict:
    """`gh issue view <n> --json` with ticket_shape.py's fields, as gh prints it."""
    return {"number": number, "body": body,
            "labels": [{"id": "LA_x", "name": name, "description": "", "color": "ededed"}
                       for name in (label, *also)],
            "subIssuesSummary": {"total": 0, "completed": 0, "percentCompleted": 0},
            "blockedBy": {"nodes": []}}


def clean_body(owner: str, box: str = "A test proves the behaviour") -> str:
    """A body ticket_shape.py passes under any label: one Owner role, a Touches line that writes
    nothing, so no path's owner is derived, and an open box no word list matches."""
    return f"**Owner:** {owner}\n**Touches:** none\n\n## Acceptance criteria\n\n- [ ] {box}\n"


# Collect's answer in the default case: three ready-for-agent tickets under two owners, one blocked
# ticket of a third owner, which must not count, and a needs-triage ticket with a plan-row id.
TICKETS = [
    ticket("issue-10", 10, "integrator", "ready-for-agent"),
    ticket("2.1", 11, "ws2-hosting", "ready-for-agent"),
    ticket("issue-12", 12, "integrator", "ready-for-agent"),
    ticket("issue-13", 13, "ws1-infra", "blocked"),
    ticket("6.9", 358, "ws6-verification", "needs-triage"),
]
SUMMARY = "overnight_collect: 5 tickets (1 blocked, 1 needs-triage, 3 ready-for-agent), 0 parents; 0 will end partial."

# The stub committed as origin/main's tools/overnight_collect.py. It records each call, then prints
# what collect prints: the summary on stderr and the answer on stdout. Imported, as
# tools/ticket_shape.py imports collect to read an Owner line, it is the real OWNER and ROLES and
# does nothing else.
COLLECT_STUB = """import json, pathlib, re, sys
OWNER = re.compile({owner!r})
ROLES = {roles!r}
if __name__ == "__main__":
    with open({calls!r}, "a") as calls:
        calls.write(json.dumps({{"argv": sys.argv[1:], "file": str(pathlib.Path(__file__).resolve())}}) + "\\n")
    print({summary!r}, file=sys.stderr)
    print(json.dumps({answer!r}))
    sys.exit({status!r})
"""

# The gh double: the two reads, each from the case's canned output, and 97 for anything else.
GH_DOUBLE = """#!{python}
import json, os, pathlib, re, sys
argv = sys.argv[1:]
with open({calls!r}, "a") as calls:
    calls.write(json.dumps({{"argv": argv, "cwd": os.getcwd()}}) + "\\n")
canned = pathlib.Path({canned!r})
if argv == {list_triage!r}:
    if (canned / "list-fails").exists():
        print("gh: could not resolve to a Repository", file=sys.stderr)
        sys.exit(1)
    print((canned / "triage.json").read_text())
    sys.exit(0)
if len(argv) == 4 and argv[:3] == ["api", "--paginate", "--slurp"]:
    found = re.fullmatch(r"repos/\\{{owner\\}}/\\{{repo\\}}/issues/([0-9]+)/comments\\?per_page=100", argv[3])
    if found:
        page = canned / ("comments-%s.json" % found.group(1))
        if not page.exists():
            print("HTTP 502", file=sys.stderr)
            sys.exit(1)
        print(page.read_text())
        sys.exit(0)
    for number in json.loads((canned / "triage.json").read_text()):
        if argv[3] == "repos/{{owner}}/{{repo}}/issues/%d/timeline?per_page=100" % number["number"]:
            page = canned / ("timeline-%d.json" % number["number"])
            if not page.exists():
                print("HTTP 502", file=sys.stderr)
                sys.exit(1)
            print(page.read_text())
            sys.exit(0)
if len(argv) == 5 and argv[:2] == ["issue", "view"] and argv[3:] == ["--json", {fields!r}]:
    issue = canned / ("issue-%s.json" % argv[2])
    if not issue.exists():
        print("GraphQL: Could not resolve to an issue or pull request", file=sys.stderr)
        sys.exit(1)
    print(issue.read_text())
    sys.exit(0)
if len(argv) == 5 and argv[:2] == ["issue", "view"] and argv[3:] == ["--json", "labels"]:
    labels = canned / ("labels-%s.json" % argv[2])
    if not labels.exists():
        print("GraphQL: Could not resolve to an issue or pull request", file=sys.stderr)
        sys.exit(1)
    print(labels.read_text())
    sys.exit(0)
print("gh double: unexpected call", argv, file=sys.stderr)
sys.exit(97)
"""


def ago(hours: float) -> str:
    """A GitHub timestamp `hours` before now."""
    at = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(hours=hours)
    return at.strftime("%Y-%m-%dT%H:%M:%SZ")


def labelled(event: str, label: str, hours: float) -> dict:
    """A timeline event as GitHub's timeline endpoint gives one, trimmed to the fields it needs."""
    return {"event": event, "label": {"name": label, "color": "ededed"}, "created_at": ago(hours),
            "actor": {"login": "adammedford"}}


def bash_version() -> str:
    """The major.minor of the bash every case runs, as that bash reports it."""
    result = subprocess.run(
        [BASH, "-c", 'printf "%s.%s" "${BASH_VERSINFO[0]}" "${BASH_VERSINFO[1]}"'],
        env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True, check=True)
    return result.stdout


def setUpModule():
    full = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout
    print(f"overnight_preflight_tests: tools/overnight_preflight.sh runs under {BASH} {full}",
          file=sys.stderr)


def header_lines() -> list[str]:
    """The script's header comment, line by line, each without its `#` and the space after it."""
    lines = []
    for line in SCRIPT.read_text().splitlines()[1:]:
        if not line.startswith("#"):
            break
        lines.append(line[2:] if line.startswith("# ") else line[1:])
    return lines


def header_text() -> str:
    """The header as one run of words, so a phrase reads the same wherever the lines wrap."""
    return " ".join(" ".join(header_lines()).split())


class Result:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout
        self.stderr = completed.stderr
        self.lines = completed.stdout.splitlines()

    def rows(self) -> list[tuple[str, str, str]]:
        """Each precondition line as (verdict, name, evidence)."""
        found = []
        for line in self.lines:
            match = re.match(r"^(pass|fail): ([a-z]+): (.*)$", line)
            if match:
                found.append(match.groups())
        return found

    def row(self, name: str) -> tuple[str, str]:
        for verdict, found, evidence in self.rows():
            if found == name:
                return verdict, evidence
        raise AssertionError(f"no {name} line in:\n{self.stdout}")

    def arguments(self) -> dict:
        return json.loads(self.lines[-1])


class Case:
    """One throwaway directory: a bare origin, a clone that pushes to it, the main checkout (a
    second clone, holding a copy of the script), and a bin directory holding the gh double and a way
    to each real tool, which is the case's whole PATH."""

    def __init__(self, test: unittest.TestCase, *, tickets: list | None = None,
                 summary: str = SUMMARY, collect_status: int = 0):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        # Resolved, because bash's pwd reports the physical path, and macOS's temporary directory
        # sits behind a symbolic link.
        self.dir = pathlib.Path(scratch.name).resolve()
        self.bin = self.dir / "bin"
        self.home = self.dir / "home"
        self.tmp = self.dir / "tmp"
        self.canned = self.dir / "canned"
        self.gh_calls = self.dir / "gh-calls"
        self.collect_calls = self.dir / "collect-calls"
        self.gitconfig = self.dir / "gitconfig"
        for directory in (self.bin, self.home, self.tmp, self.canned):
            directory.mkdir()
        self.gitconfig.write_bytes(b"")
        for tool in TOOLS:
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            runner.link_tool(self.bin / tool, os.path.realpath(found))
        runner.link_tool(self.bin / "python3", PYTHON)
        gh = self.bin / "gh"
        gh.write_text(GH_DOUBLE.format(python=PYTHON, calls=str(self.gh_calls), canned=str(self.canned),
                                       list_triage=LIST_TRIAGE, fields=SHAPE_FIELDS))
        gh.chmod(0o755)
        self.triage([])
        # Each forecast ticket's issue, clean by default, so the shape line passes listing none.
        for each in TICKETS if tickets is None else tickets:
            label = each["status"] if each["status"] in ("needs-triage", "ready-for-human") else "ready-for-agent"
            self.issue(each["issue"], label, clean_body(each["owner"]))

        self.landed = 0
        self.origin = self.dir / "origin.git"
        self.pusher = self.dir / "pusher"
        self.checkout = self.dir / "spa-platform"
        self.git("init", "-q", "--bare", "-b", "main", str(self.origin), cwd=self.dir)
        self.git("config", "gc.auto", "0", cwd=self.origin)
        self.clone(self.pusher)
        (self.pusher / "README.md").write_text("The platform.\n")
        (self.pusher / ".claude" / "workflows").mkdir(parents=True)
        (self.pusher / ".claude" / "workflows" / "overnight.js").write_text("// the workflow\n")
        (self.pusher / "tools").mkdir()
        (self.pusher / ".github").mkdir()
        (self.pusher / "docs" / "agents").mkdir(parents=True)
        (self.pusher / "docs" / "adr").mkdir()
        for path in SHAPE_FILES + DECISION_FILES:
            (self.pusher / path).write_bytes((ROOT / path).read_bytes())
        for path, text in ADR_FILES.items():
            (self.pusher / path).write_text(text)
        self.push_collect("Lay out the repository", tickets=tickets, summary=summary,
                          status=collect_status)
        self.clone(self.checkout)
        copy = self.checkout / "tools" / "overnight_preflight.sh"
        copy.write_bytes(SCRIPT.read_bytes())
        copy.chmod(0o755)

    # --- setup ---

    def env(self) -> dict[str, str]:
        return {"PATH": str(self.bin), "HOME": str(self.home), "TMPDIR": str(self.tmp),
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": str(self.gitconfig), "LC_ALL": "C"}

    def git(self, *args: str, cwd: pathlib.Path) -> str:
        return subprocess.run(
            ["git", "-c", "user.name=Preflight test", "-c", "user.email=test@example.test",
             "-c", "maintenance.auto=false", "-c", "gc.auto=0", *args],
            cwd=cwd, env=self.env(), check=True, capture_output=True, text=True).stdout.strip()

    def clone(self, target: pathlib.Path):
        self.git("clone", "-q", "-c", "gc.auto=0", "-c", "maintenance.auto=false", str(self.origin),
                 str(target), cwd=self.dir)

    def push_collect(self, message: str, *, tickets: list | None = None, summary: str = SUMMARY,
                     status: int = 0, also: dict[str, str] | None = None):
        """Commits a collect stub on origin/main, and any other files `also` names."""
        answer = {"tickets": TICKETS if tickets is None else tickets, "parents": []}
        (self.pusher / "tools" / "overnight_collect.py").write_text(COLLECT_STUB.format(
            owner=real_collect.OWNER.pattern, roles=real_collect.ROLES,
            calls=str(self.collect_calls), summary=summary, answer=answer, status=status))
        for path, text in (also or {}).items():
            (self.pusher / path).write_text(text)
        self.git("add", "-A", cwd=self.pusher)
        self.git("commit", "-q", "-m", message, cwd=self.pusher)
        self.git("push", "-q", "origin", "main", cwd=self.pusher)

    def advance_origin(self, summary: str = SUMMARY, also: dict[str, str] | None = None):
        """Moves origin/main on by one commit the main checkout has not fetched: a landing's
        record of its own, so every call makes a commit, and the files `also` names."""
        self.landed += 1
        self.push_collect("Land a ticket", summary=summary,
                          also={f"landed-{self.landed}.md": "landed\n", **(also or {})})

    def triage(self, numbers: list[int], timelines: dict[int, list] | None = None):
        (self.canned / "triage.json").write_text(json.dumps([{"number": n} for n in numbers]))
        for number, events in (timelines or {}).items():
            # One page, as `gh api --paginate --slurp` wraps them.
            (self.canned / f"timeline-{number}.json").write_text(json.dumps([events]))

    def issue(self, number: int, label: str, body: str, also: tuple = ()):
        """What `gh issue view <number> --json` answers, with the label the issue carries now and
        any others `also` names."""
        (self.canned / f"issue-{number}.json").write_text(json.dumps(issue_json(number, label, body, also)))

    def comments(self, number: int, comments: list):
        """An issue's comments, one page, as `gh api --paginate --slurp` wraps them."""
        (self.canned / f"comments-{number}.json").write_text(json.dumps([comments]))

    def labels(self, number: int, *names: str):
        """What `gh issue view <number> --json labels` answers."""
        (self.canned / f"labels-{number}.json").write_text(json.dumps(
            {"labels": [{"id": "LA_x", "name": name, "description": "", "color": "ededed"}
                        for name in names]}))

    # --- running ---

    def run(self, *argv: str, parent: str = "bash") -> Result:
        script = str(self.checkout / "tools" / "overnight_preflight.sh")
        args = list(argv) if argv else [DATE]
        if parent == "bash":
            command = [BASH, "-c", '/bin/bash "$0" "$@"; exit $?', script, *args]
        else:
            command = [BASH, script, *args]
        return Result(subprocess.run(command, cwd=self.dir, env=self.env(), capture_output=True,
                                     text=True, timeout=120))

    def gh(self) -> list[dict]:
        if not self.gh_calls.exists():
            return []
        return [json.loads(line) for line in self.gh_calls.read_text().splitlines()]

    def collect(self) -> list[dict]:
        if not self.collect_calls.exists():
            return []
        return [json.loads(line) for line in self.collect_calls.read_text().splitlines()]

    def snapshot(self) -> dict:
        """What the script must leave as it was: HEAD, every ref but origin's remote-tracking ones,
        which line 1's fetch moves, the index's bytes, every file of the checkout and the worktrees."""
        files = {}
        for path in sorted(self.checkout.rglob("*")):
            relative = path.relative_to(self.checkout)
            if relative.parts[0] == ".git" or not path.is_file():
                continue
            files[str(relative)] = hashlib.sha256(path.read_bytes()).hexdigest()
        refs = [line for line in self.git("for-each-ref", "--format=%(refname) %(objectname)",
                                          cwd=self.checkout).splitlines()
                if not line.startswith(("refs/remotes/", "refs/tags/"))]
        return {
            "HEAD": (self.checkout / ".git" / "HEAD").read_text(),
            "head": self.git("rev-parse", "HEAD", cwd=self.checkout),
            "refs": refs,
            "index": hashlib.sha256((self.checkout / ".git" / "index").read_bytes()).hexdigest(),
            "files": files,
            "worktrees": self.git("worktree", "list", "--porcelain", cwd=self.checkout),
            # Not the rest of HOME: the Python Xcode ships caches its bytecode under ~/Library.
            "worktree root": (self.home / ".worktrees").exists(),
            "origin": self.git("for-each-ref", "--format=%(refname) %(objectname)", cwd=self.origin),
        }


class PreflightTestCase(unittest.TestCase):
    def assertOrder(self, result: Result):
        self.assertEqual(LINES, [name for _, name, _ in result.rows()], result.stdout)

    def assertOnlyFails(self, result: Result, *names: str):
        failing = [name for verdict, name, _ in result.rows() if verdict == "fail"]
        self.assertEqual(list(names), failing, result.stdout)
        if names:
            self.assertEqual(1, result.status, result.stdout + result.stderr)
            self.assertTrue(result.lines[-2].startswith("overnight_preflight: fail: " + " ".join(names) + "."),
                            result.lines[-2])
        else:
            self.assertEqual(0, result.status, result.stdout + result.stderr)
            self.assertTrue(result.lines[-2].startswith("overnight_preflight: pass:"), result.lines[-2])


class CleanStartTests(PreflightTestCase):
    def test_a_clean_start_passes_every_line_in_order_and_ends_on_the_arguments(self):
        case = Case(self)
        result = case.run()
        self.assertOrder(result)
        self.assertOnlyFails(result)
        self.assertEqual(14, len(result.lines), result.stdout)
        repo = str(case.checkout)
        wt = str(case.home / ".worktrees" / f"overnight-{DATE}")
        self.assertEqual({"repo": repo, "date": DATE, "wt": wt, "maxParallel": 4, "skip": []},
                         result.arguments())
        self.assertIn("standingAnswer", result.lines[-2])
        self.assertIn("ADR-0021 decision 3", result.lines[-2])
        self.assertNotIn("standingAnswer", result.lines[-1])

    def test_each_line_names_its_evidence(self):
        case = Case(self)
        result = case.run()
        main = case.git("rev-parse", "HEAD", cwd=case.checkout)
        blob = case.git("rev-parse", "HEAD:.claude/workflows/overnight.js", cwd=case.checkout)
        self.assertEqual(("pass", f"git fetch origin --tags succeeded; main trails origin/main by 0 "
                                  f"commits and leads it by 0 (main {main[:7]}, origin/main {main[:7]})"),
                         result.row("fetch"))
        self.assertEqual(("pass", f"no tracked change in {case.checkout}"), result.row("tracked"))
        self.assertEqual(("pass", f".claude/workflows/overnight.js equals origin/main's blob {blob[:7]}"),
                         result.row("workflow"))
        self.assertEqual(("pass", f"overnight/{DATE} exists neither locally nor on origin, and "
                                  f"{case.home}/.worktrees/overnight-{DATE} is absent"),
                         result.row("run"))
        verdict, evidence = result.row("shell")
        self.assertEqual("pass", verdict)
        self.assertRegex(evidence, r"^the shell that ran this is (/bin/)?bash \(pid \d+\)$")
        self.assertEqual(("pass", f"{SUMMARY}; ready-for-agent by owner: integrator 2, ws2-hosting 1"),
                         result.row("forecast"))
        self.assertEqual(("pass", "0 needs-triage tickets; none lost ready-for-agent in the last 24 hours"),
                         result.row("demoted"))
        self.assertEqual(("pass", "no ticket with an open box is held by a local task branch whose id a "
                                  "`Merge part of task <id>` subject on origin/main names"),
                         result.row("claims"))

    def test_the_forecast_runs_origins_collect_from_outside_the_checkout_against_it(self):
        case = Case(self)
        case.run()
        calls = case.collect()
        self.assertEqual(1, len(calls), calls)
        self.assertEqual(["--json", "--repo", str(case.checkout)], calls[0]["argv"])
        ran = pathlib.Path(calls[0]["file"])
        self.assertFalse(ran.is_relative_to(case.checkout), ran)
        self.assertTrue(ran.is_relative_to(case.tmp), ran)
        # The copy is removed on exit: the temporary directory is left as it was found.
        self.assertEqual([], list(case.tmp.iterdir()))

    def test_a_bad_date_is_refused_before_anything_runs(self):
        case = Case(self)
        for argv in (["2026-10"], ["tomorrow"], [], [DATE, DATE]):
            with self.subTest(argv=argv):
                script = str(case.checkout / "tools" / "overnight_preflight.sh")
                completed = subprocess.run([BASH, script, *argv], cwd=case.dir, env=case.env(),
                                           capture_output=True, text=True)
                self.assertEqual(2, completed.returncode)
                self.assertEqual("", completed.stdout)
                self.assertIn("usage: tools/overnight_preflight.sh [--fixture <path>] [--skip <id,...>] <date>",
                              completed.stderr)
        self.assertEqual([], case.gh())
        self.assertEqual([], case.collect())


class StaleMainTests(PreflightTestCase):
    def test_a_main_that_trails_origin_fails_the_fetch_line_naming_the_count_and_the_fast_forward(self):
        case = Case(self)
        case.advance_origin()
        case.advance_origin()
        result = case.run()
        self.assertOrder(result)
        self.assertOnlyFails(result, "fetch")
        verdict, evidence = result.row("fetch")
        self.assertIn("git fetch origin --tags succeeded; main trails origin/main by 2 commits and leads it by 0", evidence)
        self.assertIn(f"the fast-forward is: git -C {case.checkout} merge --ff-only origin/main", evidence)

    def test_the_forecast_is_origins_once_main_trails(self):
        case = Case(self)
        moved = "overnight_collect: origin's own copy, one commit on."
        case.advance_origin(summary=moved)
        result = case.run()
        self.assertEqual(("pass", f"{moved}; ready-for-agent by owner: integrator 2, ws2-hosting 1"),
                         result.row("forecast"))

    def test_a_main_ahead_of_origin_or_off_main_fails(self):
        case = Case(self)
        (case.checkout / "LOCAL.md").write_text("local\n")
        case.git("add", "LOCAL.md", cwd=case.checkout)
        case.git("commit", "-q", "-m", "A local commit", cwd=case.checkout)
        result = case.run()
        self.assertOnlyFails(result, "fetch")
        self.assertIn("trails origin/main by 0 commits and leads it by 1", result.row("fetch")[1])
        self.assertNotIn("fast-forward", result.row("fetch")[1])

        case = Case(self)
        case.git("switch", "-q", "-c", "task/elsewhere", cwd=case.checkout)
        result = case.run()
        self.assertOnlyFails(result, "fetch")
        self.assertIn("the checkout is on task/elsewhere, not main", result.row("fetch")[1])

    def test_a_fetch_that_fails_fails_the_line_with_gits_last_line(self):
        case = Case(self)
        case.origin.rename(case.dir / "origin-gone.git")
        result = case.run()
        self.assertOrder(result)
        verdict, evidence = result.row("fetch")
        self.assertEqual("fail", verdict)
        self.assertRegex(evidence, r"^git fetch origin --tags failed \(.+\), so origin/main is the last one fetched")
        # The run line asks origin too, and a failed ask is a fail, not an absent branch.
        self.assertEqual("fail", result.row("run")[0])
        self.assertIn("origin could not be asked", result.row("run")[1])
        self.assertEqual(1, result.status)


class TrackedChangeTests(PreflightTestCase):
    def test_a_tracked_edit_equal_to_origins_blob_fails_and_says_it_is_origins(self):
        case = Case(self)
        case.advance_origin(also={"README.md": "The platform, as origin has it.\n"})
        (case.checkout / "README.md").write_text("The platform, as origin has it.\n")
        result = case.run()
        self.assertOrder(result)
        self.assertOnlyFails(result, "fetch", "tracked")
        self.assertEqual(
            f"1 tracked changes in {case.checkout}; equal to origin/main's blob: README.md (put HEAD's "
            "copy back with git show HEAD:<path> > <path>, and the fast-forward restores this content)",
            result.row("tracked")[1])

    def test_a_tracked_edit_that_differs_from_origin_fails_and_says_so(self):
        case = Case(self)
        (case.checkout / "README.md").write_text("A local draft.\n")
        result = case.run()
        self.assertOnlyFails(result, "tracked")
        self.assertEqual(
            f"1 tracked changes in {case.checkout}; differing from origin/main's blob: README.md (the "
            "human's to keep or drop: collect refuses a dirty start)",
            result.row("tracked")[1])

    def test_a_staged_change_a_deletion_and_both_kinds_at_once(self):
        case = Case(self)
        case.advance_origin(also={"README.md": "Origin's README.\n"})
        (case.checkout / "README.md").write_text("Origin's README.\n")
        (case.checkout / "NEW.md").write_text("staged\n")
        case.git("add", "NEW.md", cwd=case.checkout)
        (case.checkout / "tools" / "overnight_collect.py").unlink()
        evidence = case.run().row("tracked")[1]
        self.assertIn("3 tracked changes", evidence)
        self.assertIn("equal to origin/main's blob: README.md (", evidence)
        self.assertIn("differing from origin/main's blob: NEW.md tools/overnight_collect.py (", evidence)

    def test_untracked_files_are_not_tracked_changes(self):
        case = Case(self)
        (case.checkout / "notes.txt").write_text("Adam's\n")
        result = case.run()
        self.assertOnlyFails(result)


class WorkflowFileTests(PreflightTestCase):
    def test_a_workflow_script_that_differs_from_origins_fails(self):
        case = Case(self)
        case.advance_origin(also={".claude/workflows/overnight.js": "// origin's newer workflow\n"})
        result = case.run()
        self.assertOnlyFails(result, "fetch", "workflow")
        self.assertIn("a scriptPath launch would run the checkout's", result.row("workflow")[1])

    def test_a_missing_workflow_script_fails(self):
        case = Case(self)
        (case.checkout / ".claude" / "workflows" / "overnight.js").unlink()
        result = case.run()
        self.assertOnlyFails(result, "tracked", "workflow")
        self.assertIn("overnight.js is missing", result.row("workflow")[1])


class RunBranchTests(PreflightTestCase):
    def test_an_existing_run_branch_or_worktree_fails_the_run_line(self):
        branch = f"overnight/{DATE}"
        for where in ("locally", "on origin", "worktree"):
            with self.subTest(where=where):
                case = Case(self)
                wt = case.home / ".worktrees" / f"overnight-{DATE}"
                if where == "locally":
                    case.git("branch", branch, cwd=case.checkout)
                    expected = f"{branch} exists locally"
                elif where == "on origin":
                    case.git("push", "-q", "origin", f"main:refs/heads/{branch}", cwd=case.pusher)
                    expected = f"{branch} exists on origin"
                else:
                    (wt / "overnight").mkdir(parents=True)
                    expected = f"{wt} exists"
                result = case.run()
                self.assertOrder(result)
                self.assertOnlyFails(result, "run")
                self.assertEqual(expected, result.row("run")[1])

    def test_another_dates_branch_is_not_this_runs(self):
        case = Case(self)
        case.git("branch", "overnight/2026-10-25", cwd=case.checkout)
        case.git("push", "-q", "origin", "main:refs/heads/overnight/2026-10-25", cwd=case.pusher)
        (case.home / ".worktrees" / "overnight-2026-10-25").mkdir(parents=True)
        self.assertOnlyFails(case.run())


class ShellTests(PreflightTestCase):
    def test_a_parent_that_is_not_bash_fails_the_shell_line(self):
        case = Case(self)
        result = case.run(parent="python")
        self.assertOrder(result)
        self.assertOnlyFails(result, "shell")
        evidence = result.row("shell")[1]
        self.assertIn(", not bash:", evidence)
        self.assertIn("restart Claude Code so CLAUDE_CODE_SHELL takes effect (#129)", evidence)
        # The parent is this companion's own process, whatever its executable is called.
        self.assertIn(f"(pid {os.getpid()}), not bash:", evidence)


class ForecastTests(PreflightTestCase):
    def test_a_collect_that_fails_fails_the_line_and_leaves_max_parallel_to_the_default(self):
        case = Case(self, summary="overnight_collect: no tickets printed: #56 and #180 share 1.4",
                    collect_status=1)
        result = case.run()
        self.assertOnlyFails(result, *WITH_FORECAST)
        self.assertEqual("origin/main's overnight_collect.py --repo exited 1: overnight_collect: no "
                         "tickets printed: #56 and #180 share 1.4", result.row("forecast")[1])
        self.assertNotIn("maxParallel", result.arguments())

    def test_origin_without_collect_fails_the_line(self):
        case = Case(self)
        case.git("rm", "-q", "tools/overnight_collect.py", cwd=case.pusher)
        case.git("commit", "-q", "-m", "Drop collect", cwd=case.pusher)
        case.git("push", "-q", "origin", "main", cwd=case.pusher)
        case.git("pull", "-q", "--ff-only", cwd=case.checkout)
        result = case.run()
        self.assertOnlyFails(result, *WITH_FORECAST)
        self.assertEqual("origin/main has no tools/overnight_collect.py", result.row("forecast")[1])

    def test_max_parallel_is_twice_the_lanes_capped_at_seven_and_a_heuristic(self):
        # #663: lanes is the owners with ready-for-agent tickets, at least 1; the value is
        # min(2 x lanes, 7), worked by hand: 1 lane 2, 3 lanes 6, 5 lanes 10 capped at 7.
        def ready(*owners: str) -> list:
            return [ticket(f"issue-{n}", n, owner, "ready-for-agent") for n, owner in enumerate(owners, 1)]
        for tickets, expected, owners in (
            (ready("integrator"), 2, "integrator 1"),
            ([ticket("issue-1", 1, "integrator", "blocked")], 2, "none"),
            (ready("integrator", "ws1-infra", "integrator", "ws2-hosting"), 6,
             "integrator 2, ws1-infra 1, ws2-hosting 1"),
            (ready("integrator", "ws1-infra", "ws2-hosting", "ws3-identity", "ws6-verification"), 7,
             "integrator 1, ws1-infra 1, ws2-hosting 1, ws3-identity 1, ws6-verification 1"),
        ):
            with self.subTest(expected=expected, owners=owners):
                case = Case(self, tickets=tickets)
                result = case.run()
                self.assertOnlyFails(result)
                self.assertEqual(expected, result.arguments()["maxParallel"])
                self.assertTrue(result.row("forecast")[1].endswith(f"ready-for-agent by owner: {owners}"))
                verdict = result.lines[-2]
                self.assertIn(f"maxParallel is {expected}, min(2 x lanes, 7), a launch heuristic", verdict)
                self.assertIn("7 bounds the slot-counted work (implement, review, the serialised merge and "
                              "resolve), not every agent", verdict)

    def test_two_owners_print_a_max_parallel_above_the_lane_count_and_the_header_says_why(self):
        # #711: launches overrode a printed lane count of 2 or 3 to 6 to 8. Two owners, integrator
        # with two ready tickets and ws2-hosting with one: 2 lanes, and 4 by min(2 x 2, 7).
        case = Case(self)
        result = case.run()
        self.assertOnlyFails(result)
        self.assertTrue(result.row("forecast")[1].endswith(
            "ready-for-agent by owner: integrator 2, ws2-hosting 1"))
        self.assertGreater(result.arguments()["maxParallel"], 2)
        self.assertEqual(4, result.arguments()["maxParallel"])
        self.assertIn("So it exceeds the lane count", header_text())
        self.assertIn("one slot per lane leaves a lane's review waiting on another lane's implement",
                      header_text())

    def test_the_header_calls_max_parallel_a_heuristic_and_names_what_seven_bounds(self):
        header = header_text()
        self.assertIn("min(2 x lanes, 7)", header)
        self.assertIn("heuristic", header)
        self.assertIn("7 bounds the slot-counted work", header)
        self.assertIn("withSlot", header)

    def test_a_failed_forecast_names_no_max_parallel_in_the_verdict(self):
        case = Case(self, summary="overnight_collect: no tickets printed: broken", collect_status=1)
        result = case.run()
        self.assertNotIn("maxParallel", result.arguments())
        self.assertNotIn("maxParallel is", result.lines[-2])


class DemotedTests(PreflightTestCase):
    def test_a_ticket_demoted_in_the_last_day_is_a_skip_candidate_and_older_ones_are_not(self):
        case = Case(self)
        case.triage([358, 361, 362, 363], {
            # Lost ready-for-agent two hours ago, and carries a plan-row id in collect's answer.
            358: [labelled("labeled", "ready-for-agent", 50), labelled("unlabeled", "ready-for-agent", 2),
                  labelled("labeled", "needs-triage", 2)],
            # Lost it 30 hours ago: a demotion the last launch plan saw.
            361: [labelled("unlabeled", "ready-for-agent", 30)],
            # Lost another label an hour ago.
            362: [labelled("unlabeled", "ready-for-human", 1), {"event": "commented", "created_at": ago(1)}],
            # Lost it an hour ago, and collect's answer does not list it.
            363: [labelled("unlabeled", "ready-for-agent", 1)],
        })
        result = case.run()
        self.assertOrder(result)
        self.assertOnlyFails(result)
        self.assertEqual(["6.9", "issue-363"], result.arguments()["skip"])
        evidence = result.row("demoted")[1]
        self.assertRegex(evidence, r"^4 needs-triage tickets; these lost ready-for-agent in the last 24 "
                                   r"hours and are skip candidates: 6\.9 \(#358, removed \S+Z\); "
                                   r"issue-363 \(#363, removed \S+Z\)$")
        self.assertEqual([LIST_TRIAGE] + [timeline_call(n) for n in (358, 361, 362, 363)]
                         + [view_call(n) for n in (10, 11, 12, 358)],
                         [call["argv"] for call in case.gh()])
        self.assertEqual({str(case.checkout)}, {call["cwd"] for call in case.gh()})

    def test_a_gh_listing_that_fails_fails_the_line(self):
        case = Case(self)
        (case.canned / "list-fails").write_text("")
        result = case.run()
        self.assertOnlyFails(result, "demoted")
        self.assertEqual("gh could not list the needs-triage issues, so no demotion was read",
                         result.row("demoted")[1])
        self.assertEqual([], result.arguments()["skip"])

    def test_a_timeline_that_cannot_be_read_fails_the_line(self):
        case = Case(self)
        case.triage([358, 361], {358: [labelled("unlabeled", "ready-for-agent", 2)]})
        result = case.run()
        self.assertOnlyFails(result, "demoted")
        self.assertEqual("gh could not read the timeline of #361, so its demotions are unread",
                         result.row("demoted")[1])
        self.assertEqual([], result.arguments()["skip"])

    def test_a_failed_forecast_names_each_candidate_by_its_issue(self):
        case = Case(self, summary="overnight_collect: no tickets printed: broken", collect_status=1)
        case.triage([358], {358: [labelled("unlabeled", "ready-for-agent", 2)]})
        result = case.run()
        self.assertOnlyFails(result, *WITH_FORECAST)
        self.assertEqual(["issue-358"], result.arguments()["skip"])


class ClaimsTests(PreflightTestCase):
    """#484: collect reads a local `task/<id>-` branch as the ticket's claim, and a part-landed
    ticket's branch stays after its merge, which the integration rewrote, so its open boxes never get
    a lane. The claims line names each such ticket with the rename that frees it."""

    def part_merged(self, case: Case, *subjects: str):
        """Commits on origin/main with the given subjects, and fast-forwards the checkout to it, so
        the fetch line still passes."""
        for subject in subjects:
            case.git("commit", "-q", "--allow-empty", "-m", subject, cwd=case.pusher)
            case.git("push", "-q", "origin", "main", cwd=case.pusher)
        case.git("pull", "-q", "--ff-only", cwd=case.checkout)

    def test_a_ticket_with_open_boxes_held_by_a_part_merged_branch_is_listed_with_its_rename(self):
        case = Case(self)
        self.part_merged(case, "Merge part of task issue-10: the first half")
        branch = "task/issue-10-preflight-claims"
        case.git("branch", branch, cwd=case.checkout)
        tip = case.git("rev-parse", branch, cwd=case.checkout)
        result = case.run()
        self.assertOrder(result)
        self.assertOnlyFails(result)
        self.assertEqual(
            ("pass", "these tickets have open boxes and are held by a local task branch whose id a "
                     "`Merge part of task <id>` subject on origin/main names, so collect reads them as "
                     "claimed; free each whose open blockers are all met tonight by renaming its "
                     "branch, and leave held each with an open blocker that is not (#663): "
                     "issue-10 (#10, 2 open boxes; no open blocker): "
                     f"git -C {case.checkout} branch -m {branch} hold/issue-10-{tip[:7]}"),
            result.row("claims"))
        # The line names; it renames nothing, and adds no skip.
        self.assertIn(branch, case.git("branch", "--list", "task/*", cwd=case.checkout))
        self.assertEqual([], result.arguments()["skip"])

    def test_each_held_ticket_is_listed_and_a_plan_row_id_matches_too(self):
        case = Case(self)
        self.part_merged(case, "Merge part of task issue-12: one half", "Merge part of task 2.1: the other")
        case.git("branch", "task/issue-12-a", cwd=case.checkout)
        case.git("branch", "task/2.1-b", cwd=case.checkout)
        evidence = case.run().row("claims")[1]
        self.assertIn("2.1 (#11, 2 open boxes; no open blocker): git -C", evidence)
        self.assertIn("issue-12 (#12, 2 open boxes; no open blocker): git -C", evidence)
        self.assertIn("branch -m task/2.1-b hold/2.1-", evidence)

    def test_a_ticket_without_the_subject_or_without_an_open_box_is_not_listed(self):
        cases = {
            # The branch is a claim, but no part of its task merged: a lane in flight, or abandoned.
            "no subject": ([ticket("issue-10", 10, "integrator", "claimed")],
                           "Merge the review records", "task/issue-10-x"),
            # The subject names another id that shares the branch's prefix.
            "another id": ([ticket("issue-10", 10, "integrator", "claimed")],
                           "Merge part of task issue-1: something else", "task/issue-10-x"),
            "a longer id": ([ticket("issue-1", 1, "integrator", "claimed")],
                            "Merge part of task issue-10: something else", "task/issue-1-x"),
            # The subject names the ticket, but the only branch is another ticket's whose id it prefixes.
            "a branch with a longer id": ([ticket("issue-1", 1, "integrator", "claimed")],
                                          "Merge part of task issue-1: half", "task/issue-10-x"),
            # Every box ticked: close-in-morning, not a held ticket.
            "no open box": ([ticket("issue-10", 10, "integrator", "close-in-morning", open_boxes=0)],
                            "Merge part of task issue-10: the whole of it", "task/issue-10-x"),
            # No checkbox at all, which collect gives as -1.
            "no box at all": ([ticket("issue-10", 10, "integrator", "claimed", open_boxes=-1)],
                              "Merge part of task issue-10: the whole of it", "task/issue-10-x"),
        }
        for name, (tickets, subject, branch) in cases.items():
            with self.subTest(name):
                case = Case(self, tickets=tickets)
                self.part_merged(case, subject)
                case.git("branch", branch, cwd=case.checkout)
                result = case.run()
                self.assertOnlyFails(result)
                self.assertTrue(result.row("claims")[1].startswith("no ticket with an open box"),
                                result.row("claims"))

    def test_a_branch_on_origin_alone_is_not_listed(self):
        # git branch -m renames a local branch; a branch only on origin is not this checkout's to move.
        case = Case(self)
        self.part_merged(case, "Merge part of task issue-10: the first half")
        case.git("push", "-q", "origin", "main:refs/heads/task/issue-10-x", cwd=case.pusher)
        case.git("fetch", "-q", "origin", cwd=case.checkout)
        result = case.run()
        self.assertOnlyFails(result)
        self.assertTrue(result.row("claims")[1].startswith("no ticket with an open box"))

    def test_a_held_branch_origin_also_has_says_the_rename_does_not_free_it(self):
        # Collect reads origin's task branches too, so the local rename alone leaves the claim.
        case = Case(self)
        self.part_merged(case, "Merge part of task issue-10: the first half")
        case.git("push", "-q", "origin", "main:refs/heads/task/issue-10-x", cwd=case.pusher)
        case.git("fetch", "-q", "origin", cwd=case.checkout)
        case.git("branch", "task/issue-10-x", "origin/task/issue-10-x", cwd=case.checkout)
        tip = case.git("rev-parse", "task/issue-10-x", cwd=case.checkout)
        evidence = case.run().row("claims")[1]
        self.assertIn(f"branch -m task/issue-10-x hold/issue-10-{tip[:7]} (origin/task/issue-10-x claims "
                      "it too, which the rename does not free)", evidence)

    def test_a_failed_forecast_fails_the_line_for_want_of_the_open_boxes(self):
        case = Case(self, summary="overnight_collect: no tickets printed: broken", collect_status=1)
        result = case.run()
        self.assertOnlyFails(result, *WITH_FORECAST)
        self.assertEqual("the forecast failed, so no ticket's open boxes are known and no claim was judged",
                         result.row("claims")[1])


class ShapeTests(PreflightTestCase):
    """The shape line judges each ready-for-agent and needs-triage ticket of the forecast as
    ready-for-agent, with origin/main's real tools/ticket_shape.py (#483)."""

    def test_clean_tickets_are_not_listed_and_the_line_passes_with_none_listed(self):
        case = Case(self)
        result = case.run()
        self.assertOrder(result)
        self.assertOnlyFails(result)
        self.assertEqual(("pass", "4 ready-for-agent and needs-triage tickets judged as ready-for-agent; "
                                  "none fails"), result.row("shape"))
        self.assertEqual([], result.arguments()["skip"])
        # The blocked ticket of the forecast is not read; the four others are, from the checkout.
        views = [call for call in case.gh() if call["argv"][:2] == ["issue", "view"]]
        self.assertEqual([view_call(n) for n in (10, 11, 12, 358)], [call["argv"] for call in views])
        self.assertEqual({str(case.checkout)}, {call["cwd"] for call in views})

    def test_a_needs_triage_ticket_with_no_owner_line_is_listed_with_owner_line_and_skipped(self):
        case = Case(self)
        case.issue(358, "needs-triage", "**Touches:** `tools/overnight_collect.py`\n\n"
                                        "## Acceptance criteria\n\n- [ ] A test proves it\n")
        result = case.run()
        self.assertOnlyFails(result)
        self.assertEqual(("pass", "4 ready-for-agent and needs-triage tickets judged as ready-for-agent; "
                                  "these fail and are skip candidates: 6.9 (#358: owner-line)"),
                         result.row("shape"))
        self.assertEqual(["6.9"], result.arguments()["skip"])

    def test_a_needs_triage_ticket_is_judged_under_ready_for_agent_so_its_box_words_count(self):
        # Under its own label the word list does not fire; under ready-for-agent it does.
        body = clean_body("integrator", "The stack is deployed and answers")
        case = Case(self)
        case.issue(358, "needs-triage", body)
        result = case.run()
        self.assertOnlyFails(result)
        self.assertIn("these fail and are skip candidates: 6.9 (#358: sitting-or-landing)",
                      result.row("shape")[1])
        self.assertEqual(["6.9"], result.arguments()["skip"])

    def test_a_ready_ticket_that_fails_is_listed_by_its_id_with_each_rule_once(self):
        case = Case(self)
        case.issue(12, "ready-for-agent", "**Owner:** ws2-hosting and integrator\n**Touches:** none\n\n"
                                          "- [ ] It is deployed\n- [ ] It is checked by hand\n")
        result = case.run()
        self.assertOnlyFails(result)
        self.assertIn("skip candidates: issue-12 (#12: owner-line, sitting-or-landing)",
                      result.row("shape")[1])
        self.assertEqual(["issue-12"], result.arguments()["skip"])

    def test_a_ticket_both_demoted_and_failing_is_skipped_once(self):
        case = Case(self)
        case.triage([358], {358: [labelled("unlabeled", "ready-for-agent", 2)]})
        case.issue(358, "needs-triage", "**Touches:** none\n")
        result = case.run()
        self.assertOnlyFails(result)
        self.assertEqual(["6.9"], result.arguments()["skip"])

    def test_a_ticket_gh_cannot_read_fails_the_line(self):
        case = Case(self)
        (case.canned / "issue-11.json").unlink()
        case.issue(358, "needs-triage", "**Touches:** none\n")
        result = case.run()
        # The adr and decision lines read the same issues, so #11 is unread on each (#663).
        self.assertOnlyFails(result, "shape", "adr", "decision")
        self.assertEqual("4 ready-for-agent and needs-triage tickets; gh could not read #11, so those "
                         "are unjudged; of the rest, these fail and are skip candidates: 6.9 (#358: "
                         "owner-line)", result.row("shape")[1])
        self.assertEqual(["6.9"], result.arguments()["skip"])

    def test_a_ticket_the_check_cannot_judge_fails_the_line(self):
        case = Case(self)
        # No blockedBy field: ticket_shape.py reads nothing and exits 2.
        (case.canned / "issue-10.json").write_text(json.dumps({"number": 10, "body": "", "labels": []}))
        result = case.run()
        self.assertOnlyFails(result, "shape")
        self.assertIn("ticket_shape.py judged nothing for #10, so those are unjudged; of the rest, none fails",
                      result.row("shape")[1])

    def test_a_failed_forecast_or_no_check_on_origin_fails_the_line(self):
        case = Case(self, summary="overnight_collect: no tickets printed: broken", collect_status=1)
        result = case.run()
        self.assertOnlyFails(result, *WITH_FORECAST)
        self.assertEqual("the forecast failed, so no ticket was judged", result.row("shape")[1])

        case = Case(self)
        case.git("rm", "-q", "tools/ticket_shape.py", cwd=case.pusher)
        case.git("commit", "-q", "-m", "Drop the shape check", cwd=case.pusher)
        case.git("push", "-q", "origin", "main", cwd=case.pusher)
        case.git("pull", "-q", "--ff-only", cwd=case.checkout)
        result = case.run()
        # The adr and decision lines import the same check, so they cannot judge either (#663).
        self.assertOnlyFails(result, "shape", "adr", "decision")
        self.assertEqual("origin/main has no tools/ticket_shape.py, so no ticket was judged",
                         result.row("shape")[1])


def touching(paths: str, owner: str = "integrator") -> str:
    """A body ticket_shape.py passes whose Touches line is `paths`."""
    return f"**Owner:** {owner}\n**Touches:** {paths}\n\n## Acceptance criteria\n\n- [ ] A test proves it\n"


# The Touches lines under docs/adr/ the adr line flags, each with what its evidence says (#663).
ADR_TOUCHES = {
    "a bare docs/adr/": ("`docs/adr/`", "docs/adr/; every path is under docs/adr/"),
    "an existing ADR file": ("`docs/adr/0026-example.md`", "docs/adr/0026-example.md; every path is under docs/adr/"),
    "docs/adr/README.md": ("`docs/adr/README.md`", "docs/adr/README.md; every path is under docs/adr/"),
    "a mixed line": ("`docs/adr/0026-example.md`, `tools/overnight_preflight.sh`",
                     "docs/adr/0026-example.md, tools/overnight_preflight.sh; not every path is under docs/adr/"),
}


class AdrTests(PreflightTestCase):
    """#663: the adr line flags a ticket whose Touches line names a path under docs/adr/, which a
    scripted session only drafts (ADR-0021 decision 4), exempting only the authorised edit."""

    def test_each_touches_line_under_docs_adr_is_a_skip_candidate_saying_whether_all_its_paths_are(self):
        for name, (paths, said) in ADR_TOUCHES.items():
            with self.subTest(name):
                case = Case(self)
                case.issue(12, "ready-for-agent", touching(paths))
                result = case.run()
                self.assertOrder(result)
                self.assertOnlyFails(result)
                verdict, evidence = result.row("adr")
                self.assertIn("these name a path under docs/adr/, which a lane only drafts (ADR-0021 "
                              f"decision 4), and are skip candidates: issue-12 (#12: {said})", evidence)
                self.assertEqual(["issue-12"], result.arguments()["skip"])

    def test_tickets_with_no_such_path_pass_listing_none(self):
        case = Case(self)
        case.issue(12, "ready-for-agent", touching("`tools/overnight_preflight.sh`, `docs/agents/review.md`"))
        result = case.run()
        self.assertOnlyFails(result)
        self.assertEqual(("pass", "4 ready-for-agent and needs-triage tickets read; none names a path "
                                  "under docs/adr/ on its Touches line. A pass means the screen completed "
                                  "its checks, not that it authorised implementation"),
                         result.row("adr"))
        self.assertEqual([], result.arguments()["skip"])

    def change_request(self, case: Case, number: int, paths: str, comments: list):
        case.issue(number, "ready-for-agent", touching(paths), also=("change-request",))
        case.comments(number, comments)

    def test_an_accepted_adr_no_request_naming_one_existing_adr_file_is_exempt(self):
        case = Case(self)
        decided = comment(ACCEPT_NO, id=77)
        self.change_request(case, 12, "`docs/adr/0026-example.md`", [decided])
        result = case.run()
        self.assertOnlyFails(result)
        evidence = result.row("adr")[1]
        self.assertIn(f"exempt: issue-12 (#12: docs/adr/0026-example.md, decided at {decided['html_url']}; "
                      "the exemption covers that authorised edit only, which the lane validates)", evidence)
        self.assertNotIn("skip candidates", evidence)
        self.assertEqual([], result.arguments()["skip"])

    def test_the_same_decision_with_a_bare_a_new_or_a_mixed_path_stays_flagged(self):
        for paths in ("`docs/adr/`", "`docs/adr/0099-new.md`",
                      "`docs/adr/0026-example.md`, `tools/overnight_preflight.sh`"):
            with self.subTest(paths=paths):
                case = Case(self)
                self.change_request(case, 12, paths, [comment(ACCEPT_NO)])
                result = case.run()
                self.assertOnlyFails(result)
                evidence = result.row("adr")[1]
                self.assertIn("skip candidates: issue-12 (#12: ", evidence)
                self.assertNotIn("exempt:", evidence)
                # The decision line passes it: only the adr line names it.
                self.assertNotIn("issue-12 (#12: #12", result.row("decision")[1])
                self.assertEqual(["issue-12"], result.arguments()["skip"])

    def test_a_ticket_that_is_not_a_change_request_is_never_exempt(self):
        case = Case(self)
        case.issue(12, "ready-for-agent", touching("`docs/adr/0026-example.md`"))
        case.comments(12, [comment(ACCEPT_NO)])
        result = case.run()
        self.assertOnlyFails(result)
        self.assertIn("skip candidates: issue-12 (#12: docs/adr/0026-example.md; every path is under docs/adr/)",
                      result.row("adr")[1])
        # Its comments are not even read: only a change request's decision can exempt it.
        self.assertNotIn(comments_call(12), [call["argv"] for call in case.gh()])

    def test_a_request_the_decision_line_flags_is_named_there_and_not_on_the_adr_line(self):
        case = Case(self)
        self.change_request(case, 12, "`docs/adr/0026-example.md`", [])
        result = case.run()
        self.assertOnlyFails(result)
        self.assertNotIn("issue-12", result.row("adr")[1])
        self.assertIn("issue-12 (#12: ", result.row("decision")[1])
        self.assertEqual(["issue-12"], result.arguments()["skip"])

    def test_a_request_whose_decision_cannot_be_read_leaves_its_exemption_unjudged(self):
        case = Case(self)
        case.issue(12, "ready-for-agent", touching("`docs/adr/0026-example.md`"), also=("change-request",))
        result = case.run()
        self.assertOnlyFails(result, "adr", "decision")
        self.assertIn("the decision of #12, a change request naming a path under docs/adr/, could not be "
                      "read, so its exemption is unjudged", result.row("adr")[1])


class DecisionTests(PreflightTestCase):
    """#663: the decision line screens each ready-for-agent and needs-triage change request's latest
    decision with origin/main's decision_citation.judge(), flagging all but an accept with ADR: no."""

    def test_no_decision_a_decline_and_adr_yes_are_flagged_naming_the_reason_and_adr_no_passes(self):
        tickets = TICKETS + [ticket(f"issue-{n}", n, "integrator", "ready-for-agent") for n in (20, 21, 22, 23)]
        case = Case(self, tickets=tickets)
        for number in (20, 21, 22, 23):
            case.issue(number, "ready-for-agent", clean_body("integrator"), also=("change-request",))
        # #20 has a comment in the form from another account, which judge() passes over.
        case.comments(20, [comment(ACCEPT_NO, login="someone-else", id=1)])
        case.comments(21, [comment(ACCEPT_NO, hours=3, id=2), comment("Decision: decline\nADR: no", hours=1, id=3)])
        case.comments(22, [comment("Decision: accept\nADR: yes", id=4)])
        accepted = comment("Decision: accept with changes\nADR: no", id=5)
        case.comments(23, [accepted])
        result = case.run()
        self.assertOrder(result)
        self.assertOnlyFails(result)
        evidence = result.row("decision")[1]
        self.assertIn("4 change requests among the 8 ready-for-agent and needs-triage tickets", evidence)
        self.assertIn("issue-20 (#20: a comment in the decision form by someone-else, not by "
                      f"{LOGIN}, is not read as a decision; #20 carries no decision comment by {LOGIN}", evidence)
        self.assertIn("issue-21 (#21: #21 is declined by its latest decision, "
                      "https://github.com/o/r/issues/1#issuecomment-3", evidence)
        self.assertIn("issue-22 (#22: #22's latest decision, https://github.com/o/r/issues/1#issuecomment-4, "
                      "says ADR: yes", evidence)
        self.assertIn(f"these pass: issue-23 (#23: accepts with ADR: no at {accepted['html_url']})", evidence)
        self.assertEqual(["issue-20", "issue-21", "issue-22"], result.arguments()["skip"])
        # Only the change requests' comments are read, each once, from the checkout.
        reads = [call for call in case.gh() if call["argv"][3:4] and "/comments" in call["argv"][3]]
        self.assertEqual([comments_call(n) for n in (20, 21, 22, 23)], [call["argv"] for call in reads])
        self.assertEqual({str(case.checkout)}, {call["cwd"] for call in reads})

    def test_an_accept_as_drafted_whose_draft_was_edited_after_passes_and_the_screen_says_what_it_leaves(self):
        case = Case(self)
        case.issue(12, "ready-for-agent", clean_body("integrator"), also=("change-request",))
        draft = comment("## Draft\n\nThe change, as drafted.", login="claude-bot", hours=5, edited=0.5, id=10)
        decided = comment(f"Decision: accept as drafted {draft['html_url']}\nADR: no", hours=2, id=11)
        case.comments(12, [draft, decided])
        result = case.run()
        self.assertOnlyFails(result)
        evidence = result.row("decision")[1]
        self.assertIn(f"these pass: issue-12 (#12: accepts with ADR: no at {decided['html_url']})", evidence)
        for said in ("the screen reads the latest decision's form and author only",
                     "the accepted draft's identity and freshness (an `accept as drafted` whose draft was "
                     "edited after the decision passes here)",
                     "the frozen-contract restrictions and the edit authority are the lane's to validate",
                     "a pass does not authorise implementation"):
            self.assertIn(said, evidence)
        header = header_text()
        self.assertIn("reads the decision's form and author only", header)
        self.assertIn("the draft's identity and freshness, the frozen-contract restrictions and the edit "
                      "authority to the lane", header)

    def test_no_change_request_passes_naming_none_and_reads_no_comments(self):
        case = Case(self)
        result = case.run()
        self.assertOnlyFails(result)
        self.assertTrue(result.row("decision")[1].startswith(
            "0 change requests among the 4 ready-for-agent and needs-triage tickets; none is flagged;"),
            result.row("decision"))
        self.assertEqual([], [call for call in case.gh() if "/comments" in " ".join(call["argv"])])

    def test_comments_gh_cannot_read_or_judge_cannot_read_fail_the_line(self):
        case = Case(self)
        case.issue(12, "ready-for-agent", clean_body("integrator"), also=("change-request",))
        result = case.run()
        self.assertOnlyFails(result, "decision")
        self.assertIn("gh could not read the comments of #12, so its decision is unjudged",
                      result.row("decision")[1])
        self.assertEqual([], result.arguments()["skip"])

        case = Case(self)
        case.issue(12, "ready-for-agent", clean_body("integrator"), also=("change-request",))
        broken = comment(ACCEPT_NO)
        broken["created_at"] = "yesterday"
        case.comments(12, [broken])
        result = case.run()
        self.assertOnlyFails(result, "decision")
        self.assertIn("judge() could not read #12 (a decision comment, https://github.com/o/r/issues/1"
                      "#issuecomment-1, has no created_at in GitHub's form)", result.row("decision")[1])

    def test_origin_without_the_reader_fails_the_line(self):
        case = Case(self)
        case.git("rm", "-q", "tools/decision_citation.py", cwd=case.pusher)
        case.git("commit", "-q", "-m", "Drop the reader", cwd=case.pusher)
        case.git("push", "-q", "origin", "main", cwd=case.pusher)
        case.git("pull", "-q", "--ff-only", cwd=case.checkout)
        result = case.run()
        self.assertOnlyFails(result, "adr", "decision")
        self.assertIn("origin/main's tools/decision_citation.py or tools/ticket_shape.py could not be "
                      "loaded", result.row("decision")[1])


class UnblocksTests(PreflightTestCase):
    """#663: the unblocks line names the five open blockers with the most downstream tickets over the
    forecast's known blockedBy edges, and adds no skip candidate."""

    def test_a_chain_counts_every_downstream_ticket_once_ties_go_low_and_five_are_named(self):
        tickets = [
            # 20 <- 21 <- 22, and 23 waits on both 20 and 21: 20 reaches 21, 22, 23 (23 by two paths).
            ticket("issue-20", 20, "integrator", "ready-for-agent"),
            ticket("issue-21", 21, "integrator", "blocked", blocked_by=("issue-20",)),
            ticket("issue-22", 22, "integrator", "blocked", blocked_by=("issue-21",)),
            ticket("issue-23", 23, "integrator", "blocked", blocked_by=("issue-20", "issue-21")),
            # issue-99 is outside the forecast and holds 30 and 31; 32 to 35 each hold one ticket.
            ticket("issue-30", 30, "integrator", "blocked", blocked_by=("issue-99",)),
            ticket("issue-31", 31, "ws2-hosting", "blocked", blocked_by=("issue-99",)),
            ticket("issue-32", 32, "integrator", "ready-for-human"),
            ticket("issue-33", 33, "integrator", "needs-triage"),
            ticket("issue-34", 34, "integrator", "close-in-morning", open_boxes=0),
            ticket("issue-35", 35, "integrator", "claimed"),
            ticket("issue-40", 40, "integrator", "blocked", blocked_by=("issue-35",)),
            ticket("issue-41", 41, "integrator", "blocked", blocked_by=("issue-34",)),
            ticket("issue-42", 42, "integrator", "blocked", blocked_by=("issue-33",)),
            ticket("issue-43", 43, "integrator", "blocked", blocked_by=("issue-32",)),
        ]
        case = Case(self, tickets=tickets)
        case.labels(99, "change-request", "ready-for-human")
        result = case.run()
        self.assertOrder(result)
        self.assertOnlyFails(result)
        # By hand: issue-20 3, issue-21 2, issue-99 2, then the four holding one each, 32 to 35, of
        # which the lowest two make five.
        self.assertEqual(("pass", "counted over the forecast's known blockedBy edges in collect's answer, "
                                  "not over every issue in the repository; the open blockers with the most "
                                  "downstream tickets: issue-20 (#20, ready-for-agent): 3 downstream; "
                                  "issue-21 (#21, blocked): 2 downstream; issue-99 (#99, outside the "
                                  "forecast; labels: change-request, ready-for-human): 2 downstream; "
                                  "issue-32 (#32, ready-for-human): 1 downstream; issue-33 (#33, "
                                  "needs-triage): 1 downstream. It is informational and adds no skip "
                                  "candidate"),
                         result.row("unblocks"))
        self.assertEqual([], result.arguments()["skip"])
        # One labels read, for the one blocker outside the forecast.
        self.assertEqual([labels_call(99)], [call["argv"] for call in case.gh()
                                             if call["argv"][-2:] == ["--json", "labels"]])

    def test_fewer_blockers_are_named_when_fewer_exist_and_none_when_none(self):
        tickets = [ticket("issue-20", 20, "integrator", "close-in-morning", open_boxes=0),
                   ticket("issue-21", 21, "integrator", "ready-for-agent", blocked_by=("issue-20",))]
        result = Case(self, tickets=tickets).run()
        self.assertOnlyFails(result)
        self.assertIn("downstream tickets: issue-20 (#20, close-in-morning): 1 downstream. It is informational",
                      result.row("unblocks")[1])

        result = Case(self).run()
        self.assertEqual(("pass", "counted over the forecast's known blockedBy edges in collect's answer, "
                                  "not over every issue in the repository; no forecast ticket has an open "
                                  "blocker. It is informational and adds no skip candidate"),
                         result.row("unblocks"))

    def test_a_cycle_among_the_downstream_tickets_is_walked_once(self):
        # 20 holds 21, and 21 and 22 hold each other: the walk from 20 meets 21 again through 22,
        # and only its seen guard stops it, so 20 counts 21 and 22 once each and the line ends.
        tickets = [ticket("issue-20", 20, "integrator", "ready-for-agent"),
                   ticket("issue-21", 21, "integrator", "blocked", blocked_by=("issue-20", "issue-22")),
                   ticket("issue-22", 22, "integrator", "blocked", blocked_by=("issue-21",))]
        result = Case(self, tickets=tickets).run()
        self.assertOnlyFails(result)
        self.assertIn("downstream tickets: issue-20 (#20, ready-for-agent): 2 downstream; issue-21 (#21, "
                      "blocked): 1 downstream; issue-22 (#22, blocked): 1 downstream. It is informational",
                      result.row("unblocks")[1])

    def test_a_blocker_whose_labels_gh_cannot_read_fails_the_line(self):
        tickets = [ticket("issue-21", 21, "integrator", "blocked", blocked_by=("issue-99",))]
        result = Case(self, tickets=tickets).run()
        self.assertOnlyFails(result, "unblocks")
        self.assertIn("gh could not read the labels of #99, outside the forecast", result.row("unblocks")[1])


class NewLinesSkipAndFailureTests(PreflightTestCase):
    """#663: the adr and decision candidates join the printed skip and the fixture's; unblocks adds
    none; each new line fails only when it could not judge."""

    def test_adr_and_decision_candidates_are_in_both_skips_and_unblocks_adds_none(self):
        tickets = TICKETS + [ticket("issue-20", 20, "integrator", "ready-for-agent"),
                             ticket("issue-21", 21, "integrator", "ready-for-agent", blocked_by=("issue-10",))]
        case = Case(self, tickets=tickets)
        case.issue(12, "ready-for-agent", touching("`docs/adr/`"))
        case.issue(20, "ready-for-agent", clean_body("integrator"), also=("change-request",))
        case.comments(20, [])
        fixture = case.dir / "fixture.json"
        result = case.run("--fixture", str(fixture), DATE)
        self.assertOnlyFails(result)
        self.assertEqual(["issue-12", "issue-20"], result.arguments()["skip"])
        written = json.loads(fixture.read_text())
        self.assertEqual(["issue-12", "issue-20"], written["args"]["skip"])
        self.assertEqual(["issue-10", "2.1", "6.9", "issue-21"], written["expect"]["integrated"])
        # issue-10 blocks issue-21 and is named by unblocks, and stays out of both skips.
        self.assertIn("issue-10 (#10, ready-for-agent): 1 downstream", result.row("unblocks")[1])

    def test_a_failed_forecast_fails_each_new_line_saying_so(self):
        case = Case(self, summary="overnight_collect: no tickets printed: broken", collect_status=1)
        result = case.run()
        self.assertOnlyFails(result, *WITH_FORECAST)
        self.assertEqual("the forecast failed, so no ticket's Touches line was read", result.row("adr")[1])
        self.assertEqual("the forecast failed, so no change request's decision was read",
                         result.row("decision")[1])
        self.assertEqual("the forecast failed, so no blocker was counted", result.row("unblocks")[1])


class OrderTests(PreflightTestCase):
    def test_the_header_lists_the_lines_in_the_printed_order_and_cites_663(self):
        named = [match.group(1) for line in header_lines()
                 if (match := re.match(r"^  ([a-z]+)  +\S", line))]
        self.assertEqual(LINES, named)
        self.assertIn("#663", header_text())
        result = Case(self).run()
        self.assertOrder(result)
        # Then the verdict and the arguments, last.
        self.assertTrue(result.lines[-2].startswith("overnight_preflight: pass:"), result.lines[-2])
        self.assertEqual(len(LINES) + 2, len(result.lines), result.stdout)


class ClaimsBlockerTests(PreflightTestCase):
    """#663: a held ticket's waits are its open blocked-by edges only, as collect gives them."""

    def held(self, case: Case, tid: str = "issue-10"):
        case.git("commit", "-q", "--allow-empty", "-m", f"Merge part of task {tid}: half", cwd=case.pusher)
        case.git("push", "-q", "origin", "main", cwd=case.pusher)
        case.git("pull", "-q", "--ff-only", cwd=case.checkout)
        case.git("branch", f"task/{tid}-x", cwd=case.checkout)
        return case.git("rev-parse", f"task/{tid}-x", cwd=case.checkout)

    def test_a_blocker_met_tonight_gets_the_rename_and_one_not_met_is_left_held(self):
        # Both statuses the body counts as met tonight, ready-for-agent and close-in-morning, are
        # held by #10's two blockers, so dropping either from the met set turns this red.
        tickets = [ticket("issue-10", 10, "integrator", "claimed", blocked_by=("issue-20", "issue-22")),
                   ticket("issue-11", 11, "integrator", "claimed", blocked_by=("issue-20", "issue-21")),
                   ticket("issue-20", 20, "integrator", "ready-for-agent"),
                   ticket("issue-21", 21, "integrator", "ready-for-human"),
                   ticket("issue-22", 22, "integrator", "close-in-morning", open_boxes=0)]
        case = Case(self, tickets=tickets)
        tip = self.held(case, "issue-10")
        self.held(case, "issue-11")
        fixture = case.dir / "fixture.json"
        result = case.run("--fixture", str(fixture), DATE)
        self.assertOnlyFails(result)
        evidence = result.row("claims")[1]
        self.assertIn("issue-10 (#10, 2 open boxes; open blockers: issue-20, met tonight, issue-22, met "
                      "tonight): "
                      f"git -C {case.checkout} branch -m task/issue-10-x hold/issue-10-{tip[:7]}", evidence)
        self.assertIn("issue-11 (#11, 2 open boxes; open blockers: issue-20, met tonight, issue-21, not met "
                      "tonight): leave held", evidence)
        self.assertNotIn("task/issue-11-x hold/", evidence)
        self.assertEqual(["issue-10", "issue-11"], json.loads(fixture.read_text())["args"]["skip"])
        self.assertEqual([], result.arguments()["skip"])

    def test_a_blocker_outside_the_forecast_is_not_met_tonight(self):
        tickets = [ticket("issue-10", 10, "integrator", "claimed", blocked_by=("issue-99",))]
        case = Case(self, tickets=tickets)
        case.labels(99, "ready-for-agent")
        self.held(case)
        self.assertIn("issue-10 (#10, 2 open boxes; open blockers: issue-99, not met tonight): leave held",
                      case.run().row("claims")[1])

    def test_prose_is_no_wait_and_no_line_adds_an_edge_on_the_347_shape(self):
        # #10 is held and its body names the open #20 in prose; #20, an open change request, names
        # #10; no edge exists either way, as between #583 and #347.
        tickets = [ticket("issue-10", 10, "integrator", "claimed"),
                   ticket("issue-20", 20, "integrator", "ready-for-human")]
        case = Case(self, tickets=tickets)
        case.issue(10, "ready-for-agent", clean_body("integrator") + "\nWaits for #20 to land.\n")
        case.issue(20, "ready-for-human", clean_body("integrator") + "\nThis holds #10.\n",
                   also=("change-request",))
        tip = self.held(case)
        result = case.run()
        self.assertOnlyFails(result)
        self.assertIn(f"issue-10 (#10, 2 open boxes; no open blocker): git -C {case.checkout} branch -m "
                      f"task/issue-10-x hold/issue-10-{tip[:7]}", result.row("claims")[1])
        self.assertIsNone(re.search(r"add(ing)?[- ](the )?(an )?(blocked-by|edge)|--add-blocked-by",
                                    result.stdout, re.IGNORECASE), result.stdout)
        header = header_text()
        self.assertIn("claims reads edges only", header)
        self.assertIsNone(re.search(r"--add-blocked-by", header))


class FixtureTests(PreflightTestCase):
    """#485: --fixture writes the overnight harness's input, `{args, tickets, expect}`, from the
    forecast, and --skip names what the launch plan skips besides the lines' candidates."""

    def test_the_fixture_holds_the_arguments_the_forecasts_tickets_and_what_integrates(self):
        case = Case(self)
        fixture = case.dir / "fixture.json"
        result = case.run("--fixture", str(fixture), "--skip", "issue-12", DATE)
        self.assertOnlyFails(result)
        repo = str(case.checkout)
        wt = str(case.home / ".worktrees" / f"overnight-{DATE}")
        printed = {"repo": repo, "date": DATE, "wt": wt, "maxParallel": 4, "skip": ["issue-12"]}
        self.assertEqual(printed, result.arguments())
        self.assertEqual({
            "args": printed,
            "tickets": TICKETS,
            # The ready-for-agent and needs-triage tickets outside the skip, in the forecast's order:
            # not the blocked issue-13, not the skipped issue-12; the needs-triage 6.9 with its note
            # (#711).
            "expect": {"integrated": ["issue-10", "2.1", "6.9"], "skippedForBlocker": [], "startsBefore": [],
                       "note": "6.9 is needs-triage and integrates only if Route sends it to an agent, as "
                               "the mock route of the harness does; on the night a route to a human "
                               "leaves it out"},
        }, json.loads(fixture.read_text()))
        self.assertNotIn("standingAnswer", fixture.read_text())
        # The stdout contract holds: the verdict, then the arguments, last.
        self.assertEqual(14, len(result.lines), result.stdout)
        self.assertIn(f"overnight_preflight: wrote the harness fixture {fixture}", result.stderr)

    def test_the_shape_and_claims_lines_candidates_are_in_the_written_skip(self):
        case = Case(self)
        # issue-12 fails the shape check; issue-10 is held by a part-merged branch.
        case.issue(12, "ready-for-agent", "**Touches:** none\n\n- [ ] A test proves it\n")
        case.git("commit", "-q", "--allow-empty", "-m", "Merge part of task issue-10: half", cwd=case.pusher)
        case.git("push", "-q", "origin", "main", cwd=case.pusher)
        case.git("pull", "-q", "--ff-only", cwd=case.checkout)
        case.git("branch", "task/issue-10-x", cwd=case.checkout)
        fixture = case.dir / "fixture.json"
        result = case.run("--fixture", str(fixture), "--skip", "2.1", DATE)
        self.assertOnlyFails(result)
        written = json.loads(fixture.read_text())
        self.assertEqual(["issue-12", "issue-10", "2.1"], written["args"]["skip"])
        self.assertEqual(["6.9"], written["expect"]["integrated"])
        # The printed arguments leave the held ticket out: a rename frees it before launch (#484).
        self.assertEqual(["issue-12", "2.1"], result.arguments()["skip"])

    def test_a_demoted_candidate_is_in_the_written_skip_too(self):
        case = Case(self, tickets=TICKETS + [ticket("issue-14", 14, "integrator", "ready-for-agent")])
        case.triage([14], {14: [labelled("unlabeled", "ready-for-agent", 2)]})
        fixture = case.dir / "fixture.json"
        case.run("--fixture", str(fixture), DATE)
        written = json.loads(fixture.read_text())
        self.assertEqual(["issue-14"], written["args"]["skip"])
        self.assertEqual(["issue-10", "2.1", "issue-12", "6.9"], written["expect"]["integrated"])

    def test_a_ready_ticket_a_skipped_one_blocks_is_skipped_for_the_blocker(self):
        # overnight.js gives a skipped ticket no lane, so issue-15, which waits on issue-12, and
        # issue-16, which waits on issue-15, skip on the blocker; 2.1 waits on issue-10, which runs.
        tickets = [ticket("issue-10", 10, "integrator", "ready-for-agent"),
                   ticket("2.1", 11, "ws2-hosting", "ready-for-agent", blocked_by=("issue-10",)),
                   ticket("issue-12", 12, "integrator", "ready-for-agent"),
                   ticket("issue-16", 16, "integrator", "ready-for-agent", blocked_by=("issue-15",)),
                   ticket("issue-15", 15, "integrator", "ready-for-agent", blocked_by=("issue-12",))]
        case = Case(self, tickets=tickets)
        fixture = case.dir / "fixture.json"
        case.run("--fixture", str(fixture), "--skip", "issue-12", DATE)
        expect = json.loads(fixture.read_text())["expect"]
        self.assertEqual(["issue-10", "2.1"], expect["integrated"])
        self.assertEqual(["issue-16", "issue-15"], expect["skippedForBlocker"])

    def test_a_needs_triage_ticket_outside_the_skip_is_expected_to_integrate_with_the_note(self):
        # #711: the harness's mock routes every needs-triage ticket to an agent, so 6.9 and 6.11
        # integrate under it; 6.10 is in the skip and is not written; and the note says the routing
        # is Route's.
        tickets = TICKETS + [ticket("6.10", 359, "ws6-verification", "needs-triage"),
                             ticket("6.11", 360, "ws6-verification", "needs-triage")]
        case = Case(self, tickets=tickets)
        fixture = case.dir / "fixture.json"
        result = case.run("--fixture", str(fixture), "--skip", "6.10", DATE)
        self.assertOnlyFails(result)
        expect = json.loads(fixture.read_text())["expect"]
        self.assertEqual(["issue-10", "2.1", "issue-12", "6.9", "6.11"], expect["integrated"])
        self.assertEqual("6.9, 6.11 are needs-triage and integrate only if Route sends them to an agent, "
                         "as the mock route of the harness does; on the night a route to a human leaves "
                         "that one out", expect["note"])

    def test_a_needs_triage_ticket_a_ready_for_human_blocker_holds_goes_to_a_human(self):
        # overnight.js routes a needs-triage ticket with a ready-for-human blocker to a human whatever
        # its route agent returns (#379), so 6.9 is not expected, and issue-14, which waits on it,
        # skips on the blocker; 6.8, waiting on a ready-for-agent ticket, is expected.
        tickets = [ticket("issue-10", 10, "integrator", "ready-for-agent"),
                   ticket("issue-11", 11, "integrator", "ready-for-human"),
                   ticket("6.9", 358, "ws6-verification", "needs-triage", blocked_by=("issue-11",)),
                   ticket("6.8", 357, "ws6-verification", "needs-triage", blocked_by=("issue-10",)),
                   ticket("issue-14", 14, "integrator", "ready-for-agent", blocked_by=("6.9",))]
        case = Case(self, tickets=tickets)
        fixture = case.dir / "fixture.json"
        case.run("--fixture", str(fixture), DATE)
        expect = json.loads(fixture.read_text())["expect"]
        self.assertEqual(["issue-10", "6.8"], expect["integrated"])
        self.assertEqual(["issue-14"], expect["skippedForBlocker"])
        self.assertEqual("6.8 is needs-triage and integrates only if Route sends it to an agent, as the "
                         "mock route of the harness does; on the night a route to a human leaves it out",
                         expect["note"])

    def test_a_needs_triage_ticket_waiting_on_a_ticket_no_lane_runs_skips_on_the_blocker(self):
        # #711 review: overnight.js's implement counts a blocker collect lists as met only when a lane
        # integrates it or it is close-in-morning. Collect makes a ready-for-agent ticket with any
        # other blocker "blocked", but a needs-triage one keeps its status, so 6.9 (waiting on the
        # blocked issue-13) and 6.7 (on the claimed issue-15) skip on the blocker, and 6.8 (on the
        # close-in-morning issue-16) integrates.
        tickets = [ticket("issue-10", 10, "integrator", "ready-for-agent"),
                   ticket("issue-13", 13, "ws1-infra", "blocked"),
                   ticket("issue-15", 15, "integrator", "claimed"),
                   ticket("issue-16", 16, "integrator", "close-in-morning", open_boxes=0),
                   ticket("6.9", 358, "ws6-verification", "needs-triage", blocked_by=("issue-13",)),
                   ticket("6.7", 356, "ws6-verification", "needs-triage", blocked_by=("issue-15",)),
                   ticket("6.8", 357, "ws6-verification", "needs-triage", blocked_by=("issue-16",))]
        case = Case(self, tickets=tickets)
        fixture = case.dir / "fixture.json"
        case.run("--fixture", str(fixture), DATE)
        expect = json.loads(fixture.read_text())["expect"]
        self.assertEqual(["issue-10", "6.8"], expect["integrated"])
        self.assertEqual(["6.9", "6.7"], expect["skippedForBlocker"])

    def test_without_the_flag_no_fixture_is_written(self):
        case = Case(self)
        # The directory it runs in, besides the call records its doubles keep there, and the checkout.
        records = {"gh-calls", "collect-calls"}
        before = (sorted(p.name for p in case.dir.iterdir() if p.name not in records), case.snapshot())
        result = case.run("--skip", "issue-12", DATE)
        self.assertOnlyFails(result)
        self.assertEqual(["issue-12"], result.arguments()["skip"])
        self.assertNotIn("fixture", result.stderr)
        after = (sorted(p.name for p in case.dir.iterdir() if p.name not in records), case.snapshot())
        self.assertEqual(before, after)

    def test_a_failed_forecast_writes_no_fixture(self):
        case = Case(self, summary="overnight_collect: no tickets printed: broken", collect_status=1)
        fixture = case.dir / "fixture.json"
        result = case.run("--fixture", str(fixture), DATE)
        self.assertEqual(1, result.status)
        self.assertFalse(fixture.exists())
        self.assertIn("overnight_preflight: no harness fixture written: the forecast failed", result.stderr)

    def test_a_bad_invocation_is_refused_before_anything_runs(self):
        case = Case(self)
        script = str(case.checkout / "tools" / "overnight_preflight.sh")
        for argv in (["--fixture"], ["--fixture", str(case.dir / "nowhere" / "f.json"), DATE],
                     ["--skip", DATE], ["--skip", ",", DATE], ["--skip", "a b", DATE], ["--bogus", DATE]):
            with self.subTest(argv=argv):
                completed = subprocess.run([BASH, script, *argv], cwd=case.dir, env=case.env(),
                                           capture_output=True, text=True)
                self.assertEqual(2, completed.returncode, completed.stdout + completed.stderr)
                self.assertEqual("", completed.stdout)
                self.assertIn("usage: tools/overnight_preflight.sh", completed.stderr)
        self.assertEqual([], case.gh())
        self.assertEqual([], case.collect())


class WritesNothingTests(PreflightTestCase):
    def test_the_tool_writes_nothing_to_the_checkout_its_refs_or_the_tracker(self):
        for setup in ("clean", "stale and dirty", "demoted", "held"):
            with self.subTest(setup=setup):
                case = Case(self)
                if setup == "stale and dirty":
                    case.advance_origin(also={"README.md": "Origin's.\n"})
                    (case.checkout / "README.md").write_text("Origin's.\n")
                    (case.checkout / "NEW.md").write_text("staged\n")
                    case.git("add", "NEW.md", cwd=case.checkout)
                    # Its mtime moved a minute back after the index was written, so a status that
                    # refreshed the index's stat cache would rewrite it. A touch to "now" can land in
                    # the second the clone wrote the index, which git's stat check, comparing whole
                    # seconds, does not see: a `git status` in the script then went uncaught.
                    collect = case.checkout / "tools" / "overnight_collect.py"
                    moved = collect.stat().st_mtime - 60
                    os.utime(collect, (moved, moved))
                if setup == "demoted":
                    case.triage([358], {358: [labelled("unlabeled", "ready-for-agent", 2)]})
                if setup == "held":
                    case.git("commit", "-q", "--allow-empty", "-m", "Merge part of task issue-10: half",
                             cwd=case.pusher)
                    case.git("push", "-q", "origin", "main", cwd=case.pusher)
                    case.git("branch", "task/issue-10-x", cwd=case.checkout)
                before = case.snapshot()
                result = case.run()
                self.assertEqual(before, case.snapshot(), result.stdout)
                if setup == "stale and dirty":
                    # The file whose mtime alone moved is no tracked change: its content is HEAD's.
                    self.assertIn("2 tracked changes", result.row("tracked")[1])
                    self.assertNotIn("overnight_collect.py", result.row("tracked")[1])
                # Every gh call is one of the three reads: a write, or any other call, would be
                # recorded here and exit 97.
                for call in case.gh():
                    self.assertTrue(call["argv"] == LIST_TRIAGE or call["argv"][:3] == ["api", "--paginate", "--slurp"]
                                    or call["argv"][:2] == ["issue", "view"], call)
                self.assertEqual([], list(case.tmp.iterdir()))


class LaunchValidationTests(PreflightTestCase):
    NODE_RUN = r"""
const fs = require('fs')
// With -e, the first argument after the code is process.argv[1].
const [script, given] = process.argv.slice(1)
const src = fs.readFileSync(script, 'utf8').replace(/^export const meta/m, 'const meta')
const COLLECT = 'reached collect'
const agent = async () => { throw new Error(COLLECT) }
const run = new Function('args', 'agent', 'phase', 'log', 'parallel',
  'return (async () => {' + src + '\n})()')
run(JSON.parse(given), agent, () => {}, () => {}, async (xs) => Promise.all(xs.map((f) => f())))
  .then((value) => { console.log(JSON.stringify({ halted: (value && value.halted) || '' })) },
        (e) => { console.log(JSON.stringify({ reached: e.message === COLLECT, error: String(e) })) })
"""

    def launch(self, arguments: dict) -> dict:
        node = shutil.which("node")
        self.assertIsNotNone(node, "node is required to run the overnight workflow's launch validation")
        completed = subprocess.run([node, "-e", self.NODE_RUN, str(WORKFLOW), json.dumps(arguments)],
                                   capture_output=True, text=True, timeout=60)
        self.assertEqual(0, completed.returncode, completed.stderr)
        return json.loads(completed.stdout)

    def test_the_arguments_pass_the_workflows_validation_once_standing_answer_is_set(self):
        for tickets, triage in ((None, None), ([ticket("issue-1", 1, "integrator", "blocked")], None),
                                (None, 358)):
            case = Case(self, tickets=tickets)
            if triage:
                case.triage([triage], {triage: [labelled("unlabeled", "ready-for-agent", 2)]})
            arguments = case.run().arguments()
            for granted in (True, False):
                with self.subTest(arguments=arguments, standingAnswer=granted):
                    self.assertEqual({"reached": True, "error": "Error: reached collect"},
                                     self.launch({**arguments, "standingAnswer": granted}))
            with self.subTest(arguments=arguments, standingAnswer="unset"):
                halted = self.launch(arguments)["halted"]
                self.assertIn("bad launch", halted)
                self.assertIn("standingAnswer:", halted)

    def test_the_harness_sees_a_bad_argument(self):
        # The validation this companion relies on refuses what it should, so a pass above is the
        # validation passing and not the harness missing it.
        halted = self.launch({"repo": "relative", "date": DATE, "wt": "/x", "standingAnswer": True})
        self.assertIn("repo:", halted["halted"])


class InterpreterTests(PreflightTestCase):
    def test_bash_is_the_version_this_run_requires(self):
        required = os.environ.get(REQUIRE_BASH, "")
        version = bash_version()
        self.assertRegex(version, r"^\d+\.\d+$")
        if required:
            self.assertEqual(required, version, f"{REQUIRE_BASH}={required}, but {BASH} is {version}")

    def test_the_script_is_executable_and_names_bash(self):
        self.assertTrue(os.access(SCRIPT, os.X_OK), f"{SCRIPT} is not executable")
        self.assertEqual("#!/usr/bin/env bash", SCRIPT.read_text().splitlines()[0])


if __name__ == "__main__":
    runner.main()
