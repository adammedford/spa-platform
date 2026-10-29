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
double that answers the two listings the script asks for, from the case's canned output, records
every call with the directory it ran in, and exits 97 on anything else, so a new gh call, or any
write to the tracker, turns a case red. tools/overnight_collect.py is the case's own stub committed
on origin/main: the script runs origin/main's copy, extracted outside the checkout, so the stub
prints a canned summary line and answer and records how it was called and from where. A case whose
origin/main moves commits a stub that says so, which is how a case tells origin's copy from the
checkout's.

**The launch arguments meet the workflow's own validation.** The arguments case runs
.claude/workflows/overnight.js, this checkout's, under node, with the arguments the script printed
and every agent call answered by a sentinel, as .claude/workflows/overnight_tests.mjs runs it: the
script either halts on a bad launch before its first agent or reaches Collect. node is required:
without it the case fails, naming it.

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
WORKFLOW = ROOT / ".claude" / "workflows" / "overnight.js"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"
PYTHON = os.path.realpath(sys.executable)
DATE = "2026-10-26"

# The real tools the script runs, besides gh, which gets a double. Each is reached from the case's
# bin directory, which is the whole of the case's PATH.
TOOLS = ("git", "ps", "tar", "mktemp", "mkdir", "rm")

# The lines, by name, in the order the ticket gives them.
LINES = ["fetch", "tracked", "workflow", "run", "shell", "forecast", "demoted", "claims"]

LIST_TRIAGE = ["issue", "list", "--state", "open", "--label", "needs-triage", "--limit", "500",
               "--json", "number"]


def timeline_call(number: int) -> list[str]:
    return ["api", "--paginate", "--slurp", f"repos/{{owner}}/{{repo}}/issues/{number}/timeline?per_page=100"]


def ticket(id: str, issue: int, owner: str, status: str, open_boxes: int = 2) -> dict:
    return {"id": id, "issue": issue, "owner": owner, "status": status, "openBoxes": open_boxes}


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
# what collect prints: the summary on stderr and the answer on stdout.
COLLECT_STUB = """import json, pathlib, sys
with open({calls!r}, "a") as calls:
    calls.write(json.dumps({{"argv": sys.argv[1:], "file": str(pathlib.Path(__file__).resolve())}}) + "\\n")
print({summary!r}, file=sys.stderr)
print(json.dumps({answer!r}))
sys.exit({status!r})
"""

# The gh double: the two reads, each from the case's canned output, and 97 for anything else.
GH_DOUBLE = """#!{python}
import json, os, pathlib, sys
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
    for number in json.loads((canned / "triage.json").read_text()):
        if argv[3] == "repos/{{owner}}/{{repo}}/issues/%d/timeline?per_page=100" % number["number"]:
            page = canned / ("timeline-%d.json" % number["number"])
            if not page.exists():
                print("HTTP 502", file=sys.stderr)
                sys.exit(1)
            print(page.read_text())
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
                                       list_triage=LIST_TRIAGE))
        gh.chmod(0o755)
        self.triage([])

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
        self.assertEqual(10, len(result.lines), result.stdout)
        repo = str(case.checkout)
        wt = str(case.home / ".worktrees" / f"overnight-{DATE}")
        self.assertEqual({"repo": repo, "date": DATE, "wt": wt, "maxParallel": 2, "skip": []},
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
                self.assertIn("usage: tools/overnight_preflight.sh <date>", completed.stderr)
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
        self.assertOnlyFails(result, "forecast", "claims")
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
        self.assertOnlyFails(result, "forecast", "claims")
        self.assertEqual("origin/main has no tools/overnight_collect.py", result.row("forecast")[1])

    def test_max_parallel_is_the_owners_with_ready_tickets_and_at_least_one(self):
        for tickets, expected, owners in (
            ([ticket("issue-1", 1, "integrator", "ready-for-agent")], 1, "integrator 1"),
            ([ticket("issue-1", 1, "integrator", "blocked")], 1, "none"),
            ([ticket(f"issue-{n}", n, owner, "ready-for-agent")
              for n, owner in enumerate(("integrator", "ws1-infra", "ws2-hosting", "ws6-verification"), 1)],
             4, "integrator 1, ws1-infra 1, ws2-hosting 1, ws6-verification 1"),
        ):
            with self.subTest(expected=expected, owners=owners):
                case = Case(self, tickets=tickets)
                result = case.run()
                self.assertOnlyFails(result)
                self.assertEqual(expected, result.arguments()["maxParallel"])
                self.assertTrue(result.row("forecast")[1].endswith(f"ready-for-agent by owner: {owners}"))


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
        self.assertEqual([LIST_TRIAGE] + [timeline_call(n) for n in (358, 361, 362, 363)],
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
        self.assertOnlyFails(result, "forecast", "claims")
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
                     f"claimed; free each by renaming its branch: issue-10 (#10, 2 open boxes): "
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
        self.assertIn("2.1 (#11, 2 open boxes): git -C", evidence)
        self.assertIn("issue-12 (#12, 2 open boxes): git -C", evidence)
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

    def test_a_failed_forecast_fails_the_line_for_want_of_the_open_boxes(self):
        case = Case(self, summary="overnight_collect: no tickets printed: broken", collect_status=1)
        result = case.run()
        self.assertOnlyFails(result, "forecast", "claims")
        self.assertEqual("the forecast failed, so no ticket's open boxes are known and no claim was judged",
                         result.row("claims")[1])


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
                # Every gh call is one of the two reads: a write, or any other call, would be
                # recorded here and exit 97.
                for call in case.gh():
                    self.assertTrue(call["argv"] == LIST_TRIAGE or call["argv"][:3] == ["api", "--paginate", "--slurp"],
                                    call)
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
