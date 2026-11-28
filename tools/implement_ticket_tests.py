#!/usr/bin/env python3
"""Tests for tools/implement_ticket.py. Owner: integrator.

Run: python3 -I tools/implement_ticket_tests.py

Every case but the Launcher cases runs a command of the tool as a subprocess, from the main
checkout of a throwaway repository with a bare origin, in an environment that inherits nothing, as
tools/overnight_collect_tests.py runs its own: a fake `gh` on PATH answers from a world file and logs
every call, so no case reaches GitHub. The repository holds the tool and the rules it loads by path
(tools/overnight_collect.py, tools/ticket_shape.py, .github/path-ownership.py, .github/touches.sh,
OWNERSHIP.md), copied from this checkout, beside two stubs: a tools/check.sh that writes the log a
case chooses and counts its runs, and a tools/test_reach.py that prints one fixed judgement. Each of
those cases asserts on the record the command writes, its verdict line and its exit code, and on what
the fake gh and git show afterwards. A case may overwrite one of the copied rules in that checkout,
uncommitted, to make it misbehave. The Launcher cases run the tool's LAUNCHER alone, with no
command, and assert on the pid file, what the launched command wrote and the exit code.
"""
from __future__ import annotations

import ast
import fcntl
import importlib.util
import json
import os
import pathlib
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
TOOL = "tools/implement_ticket.py"
PARTS = (TOOL, "tools/overnight_collect.py", "tools/ticket_shape.py",
         ".github/path-ownership.py", ".github/touches.sh", "OWNERSHIP.md")


def load(name: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tools" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


collect = load("overnight_collect")

# The fake gh. `issue view` prints the world's issue, `issue edit` adds or removes the world's own
# login as an assignee, `api graphql` prints the world's answer, and anything else exits 97. Every
# call is logged as a JSON line of its arguments; GH_FAIL fails it as gh does offline.
GH = """#!{python} -I
import json, os, sys
args = sys.argv[1:]
with open(os.environ["GH_LOG"], "a") as log:
    log.write(json.dumps(args) + "\\n")
if os.environ.get("GH_FAIL"):
    sys.stderr.write("gh: could not reach GitHub\\n")
    sys.exit(1)
path = os.environ["GH_WORLD"]
world = json.load(open(path))
if args[:2] == ["issue", "view"]:
    print(json.dumps(world["issue"]))
elif args[:2] == ["issue", "edit"]:
    names = [entry["login"] for entry in world["issue"]["assignees"]]
    if "--add-assignee" in args and world["me"] not in names:
        names.append(world["me"])
    if "--remove-assignee" in args:
        names = [name for name in names if name != world["me"]]
    world["issue"]["assignees"] = [{{"login": name}} for name in names]
    json.dump(world, open(path, "w"))
    print(world["issue"]["url"])
elif args[:2] == ["api", "graphql"]:
    print(json.dumps(world.get("graphql", {{}})))
else:
    sys.exit(97)
"""

# The stub check: it counts its runs, then does what the case's behaviour file says, in the
# worktree it was started in. With no behaviour file it passes at once.
CHECK = """#!/bin/bash
echo "stub check: $*"
echo run >> "$STUB_DIR/runs"
if [ -f "$STUB_DIR/behaviour.sh" ]; then . "$STUB_DIR/behaviour.sh"; else echo "check: pass: stub"; fi
"""

REACH = """import json, sys
print(json.dumps({"failedAt": "tests", "verdict": "check: fail: the tests failed.",
                  "projects": [{"project": "tests/Stub.Tests", "reachedBy": ["stub (project)"]}],
                  "changed": sys.argv[1:]}))
"""

BODY = """**Plan row:** none
**Touches:** `tools/thing.py`, `tools/thing_tests.py`, `docs/thing.md`
**Vendor:** none

## Parent

Part of nothing in particular.

## What to build

A thing.

## Acceptance criteria

- [ ] The thing prints its name.
- [x] The thing has a home.
- [ ] Its companion fails when the name is wrong.

## Blocked by

None (can start immediately).
"""

TITLE = "The thing prints its name, and its companion holds it"


def issue(**changes) -> dict:
    raw = {
        "number": 7, "title": TITLE, "state": "OPEN", "body": BODY,
        "labels": [{"name": "ready-for-agent"}, {"name": "kind:ticket"}],
        "assignees": [], "url": "https://github.com/o/r/issues/7",
        "blockedBy": {"nodes": []},
        "subIssuesSummary": {"total": 0, "completed": 0, "percentCompleted": 0},
    }
    raw.update(changes)
    return raw


class Harness(unittest.TestCase):
    """A bare origin, a main checkout with the tool committed in it, a fake gh and a stub check."""

    def setUp(self):
        scratch = tempfile.TemporaryDirectory(prefix="implement-ticket-")
        self.addCleanup(scratch.cleanup)
        self.base = base = pathlib.Path(scratch.name).resolve()
        found = shutil.which("git")
        self.assertIsNotNone(found, "git is not on PATH, and the tool reads it")
        tools = base / "bin"
        tools.mkdir()
        (tools / "git").symlink_to(found)
        # prove runs a script project as `python3 -I <project>`: this interpreter, by that name.
        (tools / "python3").symlink_to(sys.executable)
        (base / "tmp").mkdir()
        gh = tools / "gh"
        gh.write_text(GH.format(python=sys.executable))
        gh.chmod(gh.stat().st_mode | stat.S_IXUSR)
        (base / "home").mkdir()
        config = base / "gitconfig"
        config.write_text("[user]\n\tname = Case\n\temail = case@example.invalid\n"
                          "[init]\n\tdefaultBranch = main\n"
                          "[gc]\n\tauto = 0\n[maintenance]\n\tauto = false\n")
        self.stub = base / "stub"
        self.stub.mkdir()
        self.world, self.gh_log = base / "world.json", base / "gh.log"
        self.env = {"PATH": f"{tools}:/usr/bin:/bin", "HOME": str(base / "home"),
                    "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": str(config), "LC_ALL": "C",
                    "GH_WORLD": str(self.world), "GH_LOG": str(self.gh_log),
                    "STUB_DIR": str(self.stub), "TMPDIR": str(base / "tmp")}
        self.set_issue(issue())

        self.origin, self.checkout = base / "origin.git", base / "checkout"
        self.git("init", "-q", "--bare", str(self.origin), cwd=base)
        self.git("clone", "-q", str(self.origin), str(self.checkout), cwd=base)
        for part in PARTS:
            (self.checkout / part).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / part, self.checkout / part)
        check = self.checkout / "tools" / "check.sh"
        check.write_text(CHECK)
        check.chmod(0o755)
        (self.checkout / "tools" / "test_reach.py").write_text(REACH)
        (self.checkout / ".gitignore").write_text(".scratch/*\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "The first commit")
        self.git("push", "-q", "origin", "HEAD:refs/heads/main")
        # origin/main moves on from another clone, so the checkout's main and its origin/main trail
        # it until the tool fetches: a base read from either, unfetched, is not origin's.
        other = base / "other"
        self.git("clone", "-q", str(self.origin), str(other), cwd=base)
        self.git("commit", "-q", "--allow-empty", "-m", "Landed elsewhere", cwd=other)
        self.git("push", "-q", "origin", "HEAD:main", cwd=other)
        self.base_sha = self.git("rev-parse", "HEAD", cwd=other)
        self.wt = base / "worktrees" / "issue-7"
        self.addCleanup(self.stop_checks)

    # --- the world ------------------------------------------------------------------------------

    def set_issue(self, raw: dict, graphql: dict | None = None) -> None:
        self.world.write_text(json.dumps({"issue": raw, "me": "case", "graphql": graphql or {}}))

    def assignees(self) -> list[str]:
        return [entry["login"] for entry in json.loads(self.world.read_text())["issue"]["assignees"]]

    def gh_calls(self) -> list[list[str]]:
        if not self.gh_log.exists():
            return []
        return [json.loads(line) for line in self.gh_log.read_text().splitlines()]

    def git(self, *args: str, cwd: pathlib.Path | None = None) -> str:
        done = subprocess.run(["git", *args], cwd=cwd or self.checkout, env=self.env,
                              capture_output=True, text=True, check=False)
        self.assertEqual(0, done.returncode, f"git {' '.join(args)}: {done.stderr}")
        return done.stdout.strip()

    def behave(self, script: str) -> None:
        (self.stub / "behaviour.sh").write_text(script)

    def runs(self) -> int:
        path = self.stub / "runs"
        return len(path.read_text().splitlines()) if path.exists() else 0

    def log_dir(self) -> pathlib.Path:
        return self.checkout / ".scratch" / "implement" / "log" / "7"

    def replace_rule(self, part: str, text: str) -> None:
        """Overwrite one of the copied rules in the main checkout, uncommitted: the tool runs the
        main checkout's copies."""
        path = self.checkout / part
        path.write_text(text)
        path.chmod(0o755)

    def stop_checks(self) -> None:
        pid_file = self.log_dir() / "check.pid"
        try:
            pid = int(pid_file.read_text().strip())
            os.killpg(pid, signal.SIGKILL)
        except (OSError, ValueError):
            pass

    # --- the tool -------------------------------------------------------------------------------

    def tool(self, *argv: str, env: dict | None = None, timeout: float = 120):
        """Run a command; return (exit code, record, verdict line, stdout)."""
        done = subprocess.run([sys.executable, "-I", str(self.checkout / TOOL), *argv],
                              cwd=self.checkout, env={**self.env, **(env or {})},
                              capture_output=True, text=True, check=False, timeout=timeout)
        lines = done.stdout.splitlines()
        self.assertTrue(lines, f"nothing on stdout; stderr: {done.stderr}")
        verdict = lines[-1]
        record = json.loads(lines[0]) if len(lines) == 2 else None
        return done.returncode, record, verdict, done.stdout + done.stderr

    def read(self, **kw):
        return self.tool("read", "7", "--wt", str(self.wt), **kw)

    def saved(self, step: str) -> dict:
        return json.loads((self.log_dir() / f"{step}.json").read_text())

    def assert_unjudged(self, code, record, verdict, step, *words):
        """Exit 2, its verdict line, and a record saying nothing was judged, as it was saved."""
        self.assert_verdict(code, verdict, step, 2, *words)
        self.assertEqual(("unjudged", 2), (record["state"], record["exit"]))
        self.assertEqual(record, self.saved(step))

    def assert_verdict(self, code, verdict, step, expected_code, *words):
        kind = "pass" if expected_code == 0 else "fail"
        self.assertEqual(expected_code, code, verdict)
        self.assertTrue(verdict.startswith(f"implement-ticket: {step}: {kind}: "), verdict)
        for word in words:
            self.assertIn(word, verdict)


class Read(Harness):
    def test_a_well_formed_ticket_is_recorded_whole(self):
        code, record, verdict, _ = self.read()
        self.assert_verdict(code, verdict, "read", 0)
        self.assertEqual(record, self.saved("read"))
        self.assertEqual(["The thing prints its name.",
                          "Its companion fails when the name is wrong."], record["boxes"])
        self.assertEqual(["tools/thing.py", "tools/thing_tests.py", "docs/thing.md"],
                         record["touches"])
        self.assertEqual(("integrator", "issue-7"), (record["owner"], record["id"]))
        self.assertEqual("the-thing-prints-its-name-and-its-companion", record["slug"])
        self.assertEqual("task/issue-7-the-thing-prints-its-name-and-its-companion",
                         record["branch"])
        self.assertEqual(str(self.wt), record["worktree"])
        self.assertEqual(self.base_sha, record["base"])
        self.assertFalse(record["branchExists"])
        self.assertEqual(["script", "docs"], record["kinds"])
        self.assertEqual(("OPEN", [], []), (record["state"], record["assignees"],
                                            record["blockers"]))

    def test_the_owner_id_and_slug_are_overnight_collects(self):
        """The same issue, resolved by tools/overnight_collect.py, gives the same three."""
        for row in ("none", "1.7"):
            with self.subTest(row=row):
                raw = issue(body=BODY.replace("**Plan row:** none", f"**Plan row:** {row}"))
                self.set_issue(raw)
                code, record, verdict, _ = self.read()
                self.assert_verdict(code, verdict, "read", 0)
                listed = {**raw, "labels": [], "assignees": []}
                answer = collect.resolve({"ready-for-agent": [listed]}, [], {"rows": {}},
                                         states={}, timelines={7: [{
                                             "event": "labeled", "actor": {"login": "o"},
                                             "label": {"name": "ready-for-agent"}}]})
                ticket = answer["tickets"][0]
                self.assertEqual((ticket["owner"], ticket["id"], ticket["slug"]),
                                 (record["owner"], record["id"], record["slug"]))

    def test_each_gate_fails_naming_itself(self):
        cases = (
            ("open", issue(state="CLOSED")),
            ("ready-for-agent", issue(labels=[{"name": "kind:ticket"}])),
            ("no-open-blocker", issue(blockedBy={"nodes": [{"number": 3, "state": "OPEN"}]})),
            ("ticket-shape", issue(body=BODY.replace("`docs/thing.md`", "`tools/bench/x.py`"))),
            ("unclaimed", issue(assignees=[{"login": "someone"}])),
        )
        for gate, raw in cases:
            with self.subTest(gate=gate):
                self.set_issue(raw)
                code, record, verdict, _ = self.read()
                self.assert_verdict(code, verdict, "read", 1, gate)
                failed = [entry["gate"] for entry in record["gates"] if not entry["ok"]]
                self.assertEqual([gate], failed)

    def test_an_assignee_with_the_task_branch_present_passes(self):
        self.git("branch", "task/issue-7-the-thing-prints-its-name-and-its-companion")
        self.set_issue(issue(assignees=[{"login": "someone"}]))
        code, record, verdict, _ = self.read()
        self.assert_verdict(code, verdict, "read", 0)
        self.assertTrue(record["branchExists"])

    def test_a_box_count_that_differs_from_overnight_collects_fails(self):
        """ticket_shape's OPEN_BOX wants a space or tab before the box; open_boxes takes any
        whitespace, so a box indented by a form feed is counted by one and not the other."""
        self.set_issue(issue(body=BODY.replace("- [ ] Its companion", "\f- [ ] Its companion")))
        code, record, verdict, _ = self.read()
        self.assert_verdict(code, verdict, "read", 1, "boxes")
        self.assertEqual(["boxes"], [entry["gate"] for entry in record["gates"] if not entry["ok"]])
        self.assertEqual(["The thing prints its name."], record["boxes"])

    def test_an_unreachable_gh_exits_2(self):
        code, record, verdict, _ = self.read(env={"GH_FAIL": "1"})
        self.assert_unjudged(code, record, verdict, "read", "could not reach GitHub")

    def test_a_touches_sh_that_gives_no_answer_exits_2(self):
        """touches.sh answers 0 or 1; any other exit is no answer, and read judges no kinds on it."""
        self.replace_rule(".github/touches.sh", "#!/bin/bash\nexit 2\n")
        code, record, verdict, _ = self.read()
        self.assert_unjudged(code, record, verdict, "read", ".github/touches.sh", "exited 2")

    def test_a_missing_argument_exits_2_with_a_verdict(self):
        code, record, verdict, _ = self.tool("read", "7")
        self.assert_unjudged(code, record, verdict, "read", "--wt")


BRANCH = "task/issue-7-the-thing-prints-its-name-and-its-companion"


class Start(Harness):
    def edits(self) -> list[list[str]]:
        return [call for call in self.gh_calls() if call[:2] == ["issue", "edit"]]

    def test_a_fresh_ticket_is_assigned_and_gets_its_worktree_at_the_base(self):
        self.read()
        code, record, verdict, _ = self.tool("start", "7")
        self.assert_verdict(code, verdict, "start", 0)
        self.assertEqual(record, self.saved("start"))
        self.assertEqual("new", record["state"])
        self.assertEqual(["case"], self.assignees())
        self.assertEqual([["issue", "edit", "7", "--add-assignee", "@me"]], self.edits())
        base = self.base_sha
        self.assertEqual(base, self.git("rev-parse", "HEAD", cwd=self.wt))
        self.assertEqual(BRANCH, self.git("rev-parse", "--abbrev-ref", "HEAD", cwd=self.wt))
        self.assertEqual((base, base, str(self.wt)), (record["head"], record["fork"],
                                                      record["worktree"]))

    def test_a_second_start_continues_and_assigns_nobody_anew(self):
        self.read()
        self.tool("start", "7")
        before = len(self.edits())
        code, record, verdict, _ = self.tool("start", "7")
        self.assert_verdict(code, verdict, "start", 0, "continued")
        self.assertEqual("continued", record["state"])
        self.assertEqual(before, len(self.edits()), "the second start called gh issue edit")

    def test_a_branch_whose_head_has_a_passing_check_is_finished(self):
        self.read()
        self.tool("start", "7")
        head = commit(self, "tools/thing.py")
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual(("pass", head), (record["state"], record["head"]))
        code, record, verdict, _ = self.tool("start", "7")
        self.assert_verdict(code, verdict, "start", 0, "finished")
        self.assertEqual(("finished", head), (record["state"], record["head"]))
        # A commit after the pass makes it a run to continue again.
        later = commit(self, "tools/thing_tests.py")
        code, record, verdict, _ = self.tool("start", "7")
        self.assert_verdict(code, verdict, "start", 0, "continued")
        self.assertEqual(("continued", later), (record["state"], record["head"]))

    def test_a_ticket_relabelled_since_read_is_refused_with_nothing_written(self):
        self.read()
        self.set_issue(issue(labels=[{"name": "ready-for-human"}]))
        code, record, verdict, _ = self.tool("start", "7")
        self.assert_verdict(code, verdict, "start", 1, "ready-for-agent")
        self.assertEqual("refused", record["state"])
        self.assertEqual([], self.assignees())
        self.assertEqual([], self.edits())
        self.assertFalse(self.wt.exists())
        self.assertEqual("", self.git("branch", "--list", BRANCH))

    def test_a_ticket_whose_open_boxes_changed_since_read_is_refused_with_nothing_written(self):
        self.read()
        first = "- [ ] The thing prints its name."
        last = "- [ ] Its companion fails when the name is wrong."
        self.set_issue(issue(body=BODY.replace(first, "@").replace(last, first).replace("@", last)))
        code, record, verdict, _ = self.tool("start", "7")
        self.assert_verdict(code, verdict, "start", 1, "open boxes", "read and launch again")
        self.assertEqual("refused", record["state"])
        self.assertEqual(list(reversed(self.saved("read")["boxes"])), record["boxes"])
        self.assertEqual([], self.assignees())
        self.assertEqual([], self.edits())
        self.assertFalse(self.wt.exists())
        self.assertEqual("", self.git("branch", "--list", BRANCH))

    def test_start_with_no_read_record_exits_2(self):
        code, record, verdict, _ = self.tool("start", "7")
        self.assert_unjudged(code, record, verdict, "start", "read record")


def commit(case: Harness, *paths: str) -> str:
    for path in paths:
        (case.wt / path).parent.mkdir(parents=True, exist_ok=True)
        (case.wt / path).write_text(f"{path}\n")
    case.git("add", "-A", cwd=case.wt)
    case.git("commit", "-q", "-m", f"Change {', '.join(paths)}", cwd=case.wt)
    return case.git("rev-parse", "HEAD", cwd=case.wt)


class Check(Harness):
    def setUp(self):
        super().setUp()
        self.read()
        self.tool("start", "7")

    def test_a_pass_line_on_the_current_head_passes_and_lists_what_lies_outside_touches(self):
        head = commit(self, "tools/thing.py", "tools/other.py")
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual(record, self.saved("check"))
        self.assertEqual(("pass", head, head), (record["state"], record["head"],
                                                record["logHead"]))
        self.assertEqual(["tools/other.py"], record["outsideTouches"])
        self.assertEqual(["integrator"], record["ownership"]["named"])
        log = (self.log_dir() / "check.log").read_text().splitlines()
        self.assertEqual(f"HEAD={head}", log[0])
        self.assertIn(f"stub check: --full --base {self.base_sha} --port 5180", log)
        self.assertNotIn("testReach", record)

    def test_the_port_is_passed_on(self):
        commit(self, "tools/thing.py")
        code, record, verdict, _ = self.tool("check", "7", "--port", "5191")
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual(5191, record["port"])
        self.assertIn("--port 5191", (self.log_dir() / "check.log").read_text())

    def test_a_fail_line_fails_and_carries_test_reach(self):
        commit(self, "tools/thing.py")
        self.behave('echo "check: fail: the tests failed."\n')
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 1, "check: fail: the tests failed.")
        self.assertEqual("tests/Stub.Tests", record["testReach"]["projects"][0]["project"])

    def test_a_log_of_an_earlier_head_is_no_pass(self):
        """The head moved while the check ran: its pass is the earlier commit's."""
        first = commit(self, "tools/thing.py")
        self.behave('git commit -q --allow-empty -m "Moved during the check"\n'
                    'echo "check: pass: stub"\n')
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 1, "ran on")
        self.assertEqual(first, record["logHead"])
        self.assertNotEqual(first, record["head"])

    def test_a_check_that_stops_with_no_verdict_line_fails_and_is_not_run_again(self):
        commit(self, "tools/thing.py")
        self.behave('echo "the check was cut off"\n')
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 1, "no verdict line")
        self.assertEqual(("fail", True, 1), (record["state"], record["started"], self.runs()))

    def test_an_uncommitted_change_is_no_pass(self):
        commit(self, "tools/thing.py")
        self.behave('echo during > tools/thing_tests.py\necho "check: pass: stub"\n')
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 1, "uncommitted")
        self.assertFalse(record["clean"])

    def test_an_uncommitted_change_before_the_check_starts_no_check(self):
        commit(self, "tools/thing.py")
        (self.wt / "tools" / "thing_tests.py").write_text("not committed\n")
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 1, "uncommitted")
        self.assertEqual(("fail", False, ["?? tools/thing_tests.py"]),
                         (record["state"], record["started"], record["uncommitted"]))
        self.assertEqual(0, self.runs())

    def test_another_owners_path_fails_naming_a05(self):
        for paths in (("tools/thing.py", "tools/bench/x.py"), ("tools/bench/x.py",)):
            with self.subTest(paths=paths):
                self.git("reset", "-q", "--hard", "origin/main", cwd=self.wt)
                commit(self, *paths)
                code, record, verdict, _ = self.tool("check", "7")
                self.assert_verdict(code, verdict, "check", 1, "A-05")
                self.assertIn("ws6-verification", record["ownership"]["output"])

    def test_a05_exiting_non_zero_fails_though_it_names_the_owner_alone(self):
        """A-05's verdict is its exit code and the owners it names, both: a run that names the
        right owner and still exits 1 is no pass."""
        commit(self, "tools/thing.py")
        self.replace_rule(".github/path-ownership.py",
                          'print("A-05: 1 file(s), all owned by integrator.")\n'
                          'raise SystemExit(1)\n')
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 1, "A-05", "exit 1")
        self.assertEqual((False, 1, ["integrator"]),
                         (record["ownership"]["ok"], record["ownership"]["exit"],
                          record["ownership"]["named"]))

    def test_a_helper_that_hangs_ends_the_call_unjudged_within_its_bound(self):
        """The judging after the wait has only the reserve left: an A-05 that never ends is
        killed at the call's deadline, and the call exits 2 rather than pass or hang."""
        commit(self, "tools/thing.py")
        self.replace_rule(".github/path-ownership.py", "import time\ntime.sleep(120)\n")
        began = time.monotonic()
        code, record, verdict, _ = self.tool(
            "check", "7", env={"IMPLEMENT_TICKET_WAIT": "3", "IMPLEMENT_TICKET_RESERVE": "2"},
            timeout=60)
        self.assertLess(time.monotonic() - began, 30)
        self.assert_unjudged(code, record, verdict, "check", "path-ownership.py",
                             "did not end within")

    def test_a_branch_that_rewrites_a05_does_not_judge_itself(self):
        """A-05 is the main checkout's copy: a branch whose own copy always passes still fails."""
        commit(self, "tools/bench/x.py")
        (self.wt / ".github" / "path-ownership.py").write_text(
            'print("A-05: 1 file(s), all owned by integrator.")\n')
        self.git("commit", "-q", "-am", "A-05 passes everything", cwd=self.wt)
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 1, "A-05")
        self.assertIn("ws6-verification", record["ownership"]["output"])

    def test_a_check_with_no_verdict_yet_returns_running_within_the_bound(self):
        commit(self, "tools/thing.py")
        self.behave('sleep 60\necho "check: pass: stub"\n')
        began = time.monotonic()
        code, record, verdict, _ = self.tool("check", "7", env={"IMPLEMENT_TICKET_WAIT": "1"})
        self.assertLess(time.monotonic() - began, 15)
        self.assert_verdict(code, verdict, "check", 2, "still running")
        self.assertEqual("running", record["state"])

    def test_a_call_while_a_check_runs_for_the_head_starts_no_second(self):
        head = commit(self, "tools/thing.py")
        self.behave('sleep 3\necho "check: pass: stub"\n')
        code, record, verdict, _ = self.tool("check", "7", env={"IMPLEMENT_TICKET_WAIT": "0.5"})
        self.assert_verdict(code, verdict, "check", 2, "still running")
        self.assertEqual((True, "running"), (record["started"], record["state"]))
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual((head, False), (record["head"], record["started"]))
        self.assertEqual(1, self.runs())
        # And a call after the verdict judges the same log again, with no new run.
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual(("pass", False, 1), (record["state"], record["started"], self.runs()))

    def test_the_check_starts_with_sigint_at_its_default_whatever_the_caller_ignores(self):
        """#1532: a job a non-interactive bash backgrounds starts with SIGINT ignored, and a
        companion that interrupts its own child then goes red. The tool is run here with SIGINT
        ignored, and the check must still start with it at its default."""
        commit(self, "tools/thing.py")
        self.behave('"$PYTHON" -I -c "import signal; print(\'SIGINT\', signal.getsignal('
                    'signal.SIGINT))"\necho "check: pass: stub"\n')
        done = subprocess.run(
            ["bash", "-c", 'trap "" INT; exec "$PYTHON" -I "$0" check 7', str(self.checkout / TOOL)],
            cwd=self.checkout, env={**self.env, "PYTHON": sys.executable},
            capture_output=True, text=True, check=False, timeout=120)
        self.assertEqual(0, done.returncode, done.stdout)
        record_line, verdict = done.stdout.splitlines()
        self.assertTrue(verdict.startswith("implement-ticket: check: pass: "), verdict)
        self.assertEqual(json.loads(record_line), self.saved("check"))
        log = (self.log_dir() / "check.log").read_text()
        self.assertIn("SIGINT <built-in function default_int_handler>", log)

    def test_check_with_no_start_record_exits_2(self):
        record_path = self.log_dir() / "start.json"
        record_path.unlink()
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_unjudged(code, record, verdict, "check", "start record")

    def test_a_pid_file_naming_a_pid_that_is_gone_starts_a_check(self):
        """A check killed with its pid file left behind holds nothing up."""
        commit(self, "tools/thing.py")
        gone = subprocess.Popen(["true"])
        gone.wait()
        (self.log_dir() / "check.pid").write_text(f"{gone.pid}\n")
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual((True, 1), (record["started"], self.runs()))

    def test_an_earlier_heads_check_that_dies_unfinished_is_waited_out_then_this_heads_runs(self):
        """A check another call started for an earlier head ends with no verdict while this call
        waits on it: that is no verdict on this head, which then gets its own check."""
        earlier = self.git("rev-parse", "HEAD", cwd=self.wt)
        commit(self, "tools/thing.py")
        # Started through bash so that init, not this process, reaps it: an unreaped child stays a
        # zombie, which a pid probe reads as alive.
        dying = int(subprocess.run(["bash", "-c", "sleep 1 > /dev/null 2>&1 & echo $!"],
                                   capture_output=True, text=True, check=True).stdout)
        (self.log_dir() / "check.log").write_text(f"HEAD={earlier}\nstub check: cut off\n")
        (self.log_dir() / "check.pid").write_text(f"{dying}\n")
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual((True, "pass"), (record["started"], record["state"]))
        self.assertEqual(1, self.runs())

    def test_a_claim_left_empty_by_a_call_that_died_is_cleared(self):
        commit(self, "tools/thing.py")
        claim = self.log_dir() / "check.pid"
        claim.write_text("")
        old = time.time() - 60
        os.utime(claim, (old, old))
        code, record, verdict, _ = self.tool("check", "7", env={"IMPLEMENT_TICKET_WAIT": "5"})
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual((True, "pass"), (record["started"], record["state"]))
        self.assertEqual(1, self.runs())

    def test_a_pid_file_naming_a_gone_pid_in_another_form_is_dropped_within_the_bound(self):
        """int() reads `0<pid>` and `<pid>` in full-width digits as the pid, but they are not the
        text str() writes: such a file is no pid, dropped, and a check starts. Each ends within
        its bound and does not spin on a file it cannot drop."""
        head = commit(self, "tools/thing.py")
        gone = str(self.gone_pid())
        full_width = "".join(chr(ord(digit) + 0xFEE0) for digit in gone)
        for text in (f"0{gone}", full_width):
            with self.subTest(text=text):
                runs = self.runs() + 1
                (self.log_dir() / "check.log").unlink(missing_ok=True)
                (self.log_dir() / "check.pid").write_text(f"{text}\n", encoding="utf-8")
                began = time.monotonic()
                code, record, verdict, _ = self.tool("check", "7",
                                                     env={"IMPLEMENT_TICKET_WAIT": "5"},
                                                     timeout=60)
                self.assertLess(time.monotonic() - began, 30)
                self.assert_verdict(code, verdict, "check", 0)
                self.assertEqual((head, True, "pass", runs),
                                 (record["head"], record["started"], record["state"],
                                  self.runs()))

    def gone_pid(self) -> int:
        gone = subprocess.Popen(["true"])
        gone.wait()
        return gone.pid

    def test_a_tool_killed_once_it_has_launched_the_check_leaves_the_check_named(self):
        """The check names itself in the pid file, so a tool killed after launching it leaves no
        claim naming the dead tool, which a later call would clear and start a second check over.
        The file is aged past EMPTY_CLAIM, so a claim left by the dead tool would read as stale.
        The kill lands once the check runs, after it has named itself: no case can time it inside
        the window between Popen and the naming, which the young-claim case stands in for."""
        head = commit(self, "tools/thing.py")
        self.behave('sleep 3\necho "check: pass: stub"\n')
        tool = subprocess.Popen([sys.executable, "-I", str(self.checkout / TOOL), "check", "7"],
                                cwd=self.checkout, env=self.env, stdout=subprocess.DEVNULL,
                                stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 30
        while self.runs() == 0 and time.monotonic() < deadline:
            time.sleep(0.05)
        tool.kill()
        tool.wait()
        self.assertEqual(1, self.runs())
        pid_file = self.log_dir() / "check.pid"
        old = time.time() - 60
        os.utime(pid_file, (old, old))
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual((head, False, "pass"),
                         (record["head"], record["started"], record["state"]))
        self.assertEqual(1, self.runs())

    def test_a_young_claim_whose_writer_is_gone_is_waited_on(self):
        """A claim whose writer died after launching the check, before the check named itself."""
        commit(self, "tools/thing.py")
        (self.log_dir() / "check.pid").write_text(f"claim {self.gone_pid()}\n")
        code, record, verdict, _ = self.tool("check", "7", env={"IMPLEMENT_TICKET_WAIT": "1"})
        self.assert_verdict(code, verdict, "check", 2, "still running")
        self.assertEqual(("running", False, 0), (record["state"], record["started"], self.runs()))

    def test_an_old_claim_whose_writer_lives_is_waited_on(self):
        """A call that claimed long ago and still lives is watching the check it started."""
        commit(self, "tools/thing.py")
        claim = self.log_dir() / "check.pid"
        claim.write_text(f"claim {os.getpid()}\n")
        old = time.time() - 60
        os.utime(claim, (old, old))
        code, record, verdict, _ = self.tool("check", "7", env={"IMPLEMENT_TICKET_WAIT": "1"})
        self.assert_verdict(code, verdict, "check", 2, "still running")
        self.assertEqual(("running", False, 0), (record["state"], record["started"], self.runs()))

    def test_an_old_claim_whose_writer_is_gone_is_cleared(self):
        commit(self, "tools/thing.py")
        claim = self.log_dir() / "check.pid"
        claim.write_text(f"claim {self.gone_pid()}\n")
        old = time.time() - 60
        os.utime(claim, (old, old))
        code, record, verdict, _ = self.tool("check", "7", env={"IMPLEMENT_TICKET_WAIT": "5"})
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual((True, "pass", 1), (record["started"], record["state"], self.runs()))

    def test_a_check_that_ends_before_naming_itself_fails_and_leaves_no_claim(self):
        """The launched check cannot write its name (a directory sits where it writes it), so it
        ends without running; the call that started it fails at once rather than wait on its own
        claim to the bound."""
        commit(self, "tools/thing.py")
        (self.log_dir() / "check.pid.named").mkdir()
        began = time.monotonic()
        code, record, verdict, _ = self.tool("check", "7", env={"IMPLEMENT_TICKET_WAIT": "8"})
        self.assertLess(time.monotonic() - began, 6)
        self.assert_verdict(code, verdict, "check", 1, "before it named itself")
        self.assertEqual(("fail", True, 0), (record["state"], record["started"], self.runs()))
        self.assertNotEqual(0, record["launchExit"])
        self.assertFalse((self.log_dir() / "check.pid").exists())


# The stub test project prove runs: it fails when tools/thing.py says "broken", as a test that
# guards the thing's name fails when the name is broken.
THING = "print('the thing')\n"
THING_TESTS = ('import pathlib, sys\n'
               'sys.exit(1 if "broken" in pathlib.Path("tools/thing.py").read_text() else 0)\n')
CONFIRMED = {"seams": [{"name": "the command line", "interface": "thing.py's stdout",
                        "boxes": [1]}],
             "noTest": [{"box": 2, "why": "the companion is the test"}]}


def write(case: Harness, files: dict) -> None:
    for path, text in files.items():
        (case.wt / path).parent.mkdir(parents=True, exist_ok=True)
        (case.wt / path).write_text(text)


def commit_files(case: Harness, files: dict, message: str = "Change the thing") -> str:
    write(case, files)
    case.git("add", "-A", cwd=case.wt)
    case.git("commit", "-q", "-m", message, cwd=case.wt)
    return case.git("rev-parse", "HEAD", cwd=case.wt)


def save_mutation(case: Harness, name: str, box: int, project: str, files: dict) -> None:
    """A break saved as the build prompt asks: the edit made, `git diff` saved as the patch beside
    its sidecar, and the files restored."""
    write(case, files)
    patch = case.git("diff", cwd=case.wt) + "\n"
    case.git("checkout", "--", ".", cwd=case.wt)
    directory = case.log_dir() / "mutations"
    directory.mkdir(parents=True, exist_ok=True)
    (directory / f"{name}.patch").write_text(patch)
    (directory / f"{name}.json").write_text(json.dumps({"box": box, "project": project}))


class Prove(Harness):
    """prove re-runs each saved mutation in a throwaway worktree at the checked head."""

    def setUp(self):
        super().setUp()
        self.read()
        path = self.base / "seams.json"
        path.write_text(json.dumps(CONFIRMED))
        self.tool("launch", "7", "--seams", str(path))
        self.tool("start", "7")
        commit_files(self, {"tools/thing.py": THING, "tools/thing_tests.py": THING_TESTS})

    def checked(self) -> str:
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        return record["head"]

    def prove(self, **kw):
        return self.tool("prove", "7", **kw)

    def assert_no_throwaway(self) -> None:
        """Only the main checkout and the task worktree are left, and nothing in TMPDIR."""
        listed = [line for line in self.git("worktree", "list", "--porcelain").splitlines()
                  if line.startswith("worktree ")]
        self.assertEqual([f"worktree {self.checkout}", f"worktree {self.wt}"], listed)
        self.assertEqual([], sorted(os.listdir(self.base / "tmp")))

    def rows(self, record) -> dict:
        return {row["patch"]: row["result"] for row in record["patches"]}

    def boxes(self, record) -> list:
        return [row["state"] for row in record["boxes"]]

    def test_a_patch_that_makes_its_test_fail_is_proved_and_a_no_test_box_is_not_unproved(self):
        save_mutation(self, "name", 1, "tools/thing_tests.py",
                      {"tools/thing.py": "print('broken')\n"})
        head = self.checked()
        code, record, verdict, _ = self.prove()
        self.assert_verdict(code, verdict, "prove", 0)
        self.assertEqual(record, self.saved("prove"))
        self.assertEqual(("proved", head), (record["state"], record["head"]))
        self.assertEqual({"name": "proved"}, self.rows(record))
        self.assertEqual(["proved", "no-test"], self.boxes(record))
        self.assertEqual([["name"], []], [row["patches"] for row in record["boxes"]])
        self.assertEqual([("tools/thing_tests.py", "script", 0)],
                         [(row["project"], row["kind"], row["exit"])
                          for row in record["baselines"]])
        self.assert_no_throwaway()
        # The patch ran in the throwaway worktree: the task worktree is untouched.
        self.assertEqual(THING, (self.wt / "tools" / "thing.py").read_text())

    def test_a_patch_that_leaves_its_test_passing_did_not_fail(self):
        save_mutation(self, "harmless", 1, "tools/thing_tests.py",
                      {"tools/thing.py": "print('the thing!')\n"})
        self.checked()
        code, record, verdict, _ = self.prove()
        self.assert_verdict(code, verdict, "prove", 1, "box 1", "did-not-fail")
        self.assertEqual("gaps", record["state"])
        self.assertEqual({"harmless": "did-not-fail"}, self.rows(record))
        self.assertEqual(["did-not-fail", "no-test"], self.boxes(record))
        self.assert_no_throwaway()

    def test_a_patch_that_no_longer_applies_did_not_apply(self):
        save_mutation(self, "name", 1, "tools/thing_tests.py",
                      {"tools/thing.py": "print('broken')\n"})
        commit_files(self, {"tools/thing.py": "print('the thing, renamed')\n"})
        self.checked()
        code, record, verdict, _ = self.prove()
        self.assert_verdict(code, verdict, "prove", 1, "box 1", "did-not-apply")
        self.assertEqual({"name": "did-not-apply"}, self.rows(record))
        self.assertEqual(["did-not-apply", "no-test"], self.boxes(record))
        self.assert_no_throwaway()

    def test_a_failing_baseline_is_baseline_red_for_every_patch_of_that_project(self):
        commit_files(self, {"tools/red_tests.py": "import sys\nsys.exit(1)\n"})
        save_mutation(self, "a", 1, "tools/red_tests.py", {"tools/thing.py": "print('broken')\n"})
        save_mutation(self, "b", 1, "tools/red_tests.py", {"tools/thing.py": "print('x')\n"})
        save_mutation(self, "c", 1, "tools/thing_tests.py",
                      {"tools/thing.py": "print('broken')\n"})
        self.checked()
        code, record, verdict, _ = self.prove()
        self.assert_verdict(code, verdict, "prove", 1, "baseline-red")
        self.assertEqual({"a": "baseline-red", "b": "baseline-red", "c": "proved"},
                         self.rows(record))
        self.assertEqual({"tools/red_tests.py": 1, "tools/thing_tests.py": 0},
                         {row["project"]: row["exit"] for row in record["baselines"]})
        self.assert_no_throwaway()

    def test_an_open_box_with_no_patch_is_unproved_unless_no_test_holds_it(self):
        path = self.base / "seams.json"
        path.write_text(json.dumps({"seams": [{"name": "cli", "interface": "stdout",
                                               "boxes": [1, 2]}], "noTest": []}))
        self.tool("launch", "7", "--seams", str(path))
        save_mutation(self, "name", 1, "tools/thing_tests.py",
                      {"tools/thing.py": "print('broken')\n"})
        self.checked()
        code, record, verdict, _ = self.prove()
        self.assert_verdict(code, verdict, "prove", 1, "box 2", "unproved")
        self.assertEqual(["proved", "unproved"], self.boxes(record))

    def test_prove_refuses_without_a_passing_check_on_the_head(self):
        save_mutation(self, "name", 1, "tools/thing_tests.py",
                      {"tools/thing.py": "print('broken')\n"})
        code, record, verdict, _ = self.prove()
        self.assert_unjudged(code, record, verdict, "prove", "check record")
        self.behave('echo "check: fail: the tests failed."\n')
        self.tool("check", "7")
        code, record, verdict, _ = self.prove()
        self.assert_verdict(code, verdict, "prove", 1, "did not pass")
        self.assertEqual(("refused", None), (record["state"], record.get("patches")))
        (self.stub / "behaviour.sh").unlink()
        commit_files(self, {"tools/thing_tests.py": THING_TESTS + "# mended\n"})
        self.checked()
        commit_files(self, {"docs/thing.md": "later\n"})
        code, record, verdict, _ = self.prove()
        self.assert_verdict(code, verdict, "prove", 1, "earlier head")
        self.assertEqual(("refused", None), (record["state"], record.get("patches")))
        self.assert_no_throwaway()

    def test_a_sidecar_naming_a_project_outside_the_worktree_or_no_kinds_fails_the_step(self):
        outside = self.base / "outside_tests.py"
        outside.write_text("import sys\nsys.exit(0)\n")
        commit_files(self, {"docs/thing.md": "the thing\n"})
        self.checked()
        for project, word in (("../outside_tests.py", "outside the worktree"),
                              (str(outside), "outside the worktree"),
                              ("docs/thing.md", "no kind's command"),
                              ("tools/none_tests.py", "does not hold")):
            with self.subTest(project=project):
                save_mutation(self, "name", 1, project, {"tools/thing.py": "print('broken')\n"})
                code, record, verdict, _ = self.prove()
                self.assert_verdict(code, verdict, "prove", 1, "name", word)
                self.assertEqual("fail", record["state"])
                self.assert_no_throwaway()

    def test_a_project_that_hangs_ends_the_call_unjudged_and_the_throwaway_is_gone(self):
        commit_files(self, {"tools/slow_tests.py": "import time\ntime.sleep(120)\n"})
        save_mutation(self, "slow", 1, "tools/slow_tests.py",
                      {"tools/thing.py": "print('broken')\n"})
        self.checked()
        began = time.monotonic()
        code, record, verdict, _ = self.prove(
            env={"IMPLEMENT_TICKET_WAIT": "3", "IMPLEMENT_TICKET_RESERVE": "2"}, timeout=60)
        self.assertLess(time.monotonic() - began, 30)
        self.assert_unjudged(code, record, verdict, "prove", "did not end within")
        self.assert_no_throwaway()

    def test_a_docs_only_ticket_passes_with_nothing_to_prove(self):
        touches = BODY.split("\n")[1]
        self.set_issue(issue(body=BODY.replace(touches, "**Touches:** `docs/thing.md`")))
        self.read()
        self.tool("start", "7")
        self.checked()
        code, record, verdict, _ = self.prove()
        self.assert_verdict(code, verdict, "prove", 0, "docs-only")
        self.assertEqual(("proved", [], []), (record["state"], record["patches"],
                                              record["boxes"]))
        self.assert_no_throwaway()


class Release(Harness):
    def test_release_unassigns_and_keeps_the_branch_and_the_worktree(self):
        self.read()
        self.tool("start", "7")
        code, record, verdict, _ = self.tool("release", "7")
        self.assert_verdict(code, verdict, "release", 0, "#7 is unassigned")
        self.assertEqual(record, self.saved("release"))
        self.assertEqual([], self.assignees())
        self.assertEqual([], record["assignees"])
        self.assertIn(["issue", "edit", "7", "--remove-assignee", "@me"], self.gh_calls())
        self.assertEqual((BRANCH, str(self.wt), True, True),
                         (record["branch"], record["worktree"], record["branchKept"],
                          record["worktreeKept"]))
        self.assertEqual(BRANCH, self.git("rev-parse", "--abbrev-ref", "HEAD", cwd=self.wt))

    def test_release_names_another_login_that_still_holds_the_issue(self):
        """start continues an issue another login holds when its task branch exists; removing @me
        leaves that login, and release says so rather than that the issue is unassigned."""
        self.git("branch", BRANCH)
        self.set_issue(issue(assignees=[{"login": "someone"}]))
        self.read()
        self.tool("start", "7")
        code, record, verdict, _ = self.tool("release", "7")
        self.assert_verdict(code, verdict, "release", 0, "#7 is still assigned to someone")
        self.assertNotIn("unassigned", verdict)
        self.assertEqual(record, self.saved("release"))
        self.assertEqual((["someone"], ["someone"]), (record["assignees"], self.assignees()))

    def test_an_unreachable_gh_exits_2(self):
        self.read()
        self.tool("start", "7")
        code, record, verdict, _ = self.tool("release", "7", env={"GH_FAIL": "1"})
        self.assert_unjudged(code, record, verdict, "release", "could not reach GitHub")
        self.assertEqual(["case"], self.assignees())


SCRIPT_PATH = ".claude/workflows/implement-ticket.js"


class LaunchLines(Harness):
    """read's record ends on the first launch, and launch prints the second once the confirmed
    seams cover every open box exactly once. BODY has two open boxes, 1 and 2."""

    def first(self, port: int = 5180) -> dict:
        return {"scriptPath": str(self.checkout / SCRIPT_PATH),
                "args": {"repo": str(self.checkout), "issue": 7, "wt": str(self.wt), "port": port}}

    def seams(self, content) -> str:
        path = self.base / "seams.json"
        path.write_text(json.dumps(content))
        return str(path)

    def launch(self, content):
        argument = content if content == "implementer" else self.seams(content)
        return self.tool("launch", "7", "--seams", argument)

    def test_read_ends_its_record_with_the_first_launch(self):
        code, record, verdict, _ = self.read()
        self.assert_verdict(code, verdict, "read", 0)
        self.assertEqual("launch", list(record)[-1])
        self.assertEqual(self.first(), record["launch"])
        code, record, verdict, _ = self.tool("read", "7", "--wt", str(self.wt), "--port", "5191")
        self.assert_verdict(code, verdict, "read", 0)
        self.assertEqual(self.first(5191), record["launch"])
        self.assertEqual(record, self.saved("read"))

    def test_a_refused_read_prints_no_launch(self):
        self.set_issue(issue(state="CLOSED"))
        code, record, verdict, _ = self.read()
        self.assert_verdict(code, verdict, "read", 1, "open")
        self.assertNotIn("launch", record)

    def test_launch_prints_the_second_for_seams_covering_every_box(self):
        self.read()
        confirmed = {"seams": [{"name": "the command line", "interface": "thing.py's stdout",
                                "boxes": [1]}],
                     "noTest": [{"box": 2, "why": "the companion is the test"}]}
        code, record, verdict, _ = self.launch(confirmed)
        self.assert_verdict(code, verdict, "launch", 0)
        expected = self.first()
        expected["args"]["seams"] = confirmed
        self.assertEqual(expected, record["launch"])
        self.assertEqual("launch", list(record)[-1])
        self.assertEqual(record, self.saved("launch"))

    def test_launch_takes_the_word_implementer_for_seams(self):
        self.read()
        code, record, verdict, _ = self.launch("implementer")
        self.assert_verdict(code, verdict, "launch", 0)
        self.assertEqual("implementer", record["launch"]["args"]["seams"])

    def test_launch_fails_naming_a_box_left_out(self):
        self.read()
        code, record, verdict, _ = self.launch(
            {"seams": [{"name": "cli", "interface": "stdout", "boxes": [1]}], "noTest": []})
        self.assert_verdict(code, verdict, "launch", 1, "box 2", "Its companion fails")
        self.assertNotIn("box 1", verdict)
        self.assertNotIn("launch", record)

    def test_launch_fails_naming_a_box_in_two_seams(self):
        self.read()
        code, record, verdict, _ = self.launch(
            {"seams": [{"name": "cli", "interface": "stdout", "boxes": [1, 2]},
                       {"name": "api", "interface": "thing()", "boxes": [1]}], "noTest": []})
        self.assert_verdict(code, verdict, "launch", 1, "box 1", "cli", "api")
        self.assertNotIn("box 2", verdict)
        self.assertNotIn("launch", record)

    def test_launch_fails_naming_a_box_in_a_seam_and_in_no_test(self):
        self.read()
        code, record, verdict, _ = self.launch(
            {"seams": [{"name": "cli", "interface": "stdout", "boxes": [1, 2]}],
             "noTest": [{"box": 2, "why": "none needed"}]})
        self.assert_verdict(code, verdict, "launch", 1, "box 2", "cli", "noTest")
        self.assertNotIn("launch", record)

    def test_launch_fails_on_a_box_the_ticket_does_not_have(self):
        self.read()
        code, record, verdict, _ = self.launch(
            {"seams": [{"name": "cli", "interface": "stdout", "boxes": [1, 2, 3]}], "noTest": []})
        self.assert_verdict(code, verdict, "launch", 1, "box 3")
        self.assertNotIn("launch", record)

    def test_a_misshapen_seams_file_fails_naming_the_field(self):
        self.read()
        for content, word in (({"seams": [{"name": "cli", "boxes": [1, 2]}]}, "interface"),
                              ({"seams": [{"name": "cli", "interface": "x", "boxes": ["1"]}]},
                               "boxes"),
                              ({"seams": [], "noTest": [{"box": 1}, {"box": 2}]}, "why"),
                              ("everything", "implementer"),
                              ({"seams": [], "noTest": [{"box": 1, "why": "a"}, {"box": 2, "why": "b"}],
                                "extra": 1}, "extra")):
            with self.subTest(word=word):
                code, record, verdict, _ = self.tool("launch", "7", "--seams",
                                                     self.seams(content))
                self.assert_verdict(code, verdict, "launch", 1, word)
                self.assertNotIn("launch", record)

    def test_launch_after_a_refused_read_fails(self):
        self.set_issue(issue(state="CLOSED"))
        self.read()
        code, record, verdict, _ = self.launch("implementer")
        self.assert_verdict(code, verdict, "launch", 1, "read did not pass")
        self.assertNotIn("launch", record)

    def test_launch_with_no_seams_or_no_read_record_exits_2(self):
        code, record, verdict, _ = self.launch("implementer")
        self.assert_unjudged(code, record, verdict, "launch", "read record")
        self.read()
        code, record, verdict, _ = self.tool("launch", "7")
        self.assert_unjudged(code, record, verdict, "launch", "--seams")
        code, record, verdict, _ = self.tool("launch", "7", "--seams", str(self.base / "none"))
        self.assert_unjudged(code, record, verdict, "launch", "seams file")
        code, record, verdict, _ = self.tool("start", "7", "--seams", "implementer")
        self.assert_unjudged(code, record, verdict, "start", "--seams is launch's alone")
        code, record, verdict, _ = self.tool("read", "7", "--wt", str(self.wt), "--port", "80")
        self.assert_unjudged(code, record, verdict, "read", "1024 to 65535")


# --- the shared fixture -------------------------------------------------------------------------
# .claude/workflows/fixtures/implement-ticket.json holds the tool's records, which the script's
# companion serves as its couriers' returns. Each scenario below makes the records of one entry, and
# FixtureKeys fails when a record the tool writes no longer has the fixture's keys, so the two layers
# change together. The same scenarios wrote the fixture (its note says how).

FIXTURE = ROOT / ".claude" / "workflows" / "fixtures" / "implement-ticket.json"

# A Touches line for one ticket of each kind of tools/implement_ticket.py's KINDS.
KIND_TOUCHES = {
    "dotnet": "`Directory.Build.props`, `tools/thing.py`",
    "terraform": "`infra/modules/thing/main.tf`, `infra/modules/thing/tests/thing.tftest.hcl`",
    "script": "`tools/thing.py`, `tools/thing_tests.py`",
    "workflow": "`.claude/workflows/thing.js`, `.claude/workflows/thing_tests.mjs`",
    "docs": "`docs/thing.md`",
}


def kind_records(case: Harness, kind: str) -> dict:
    """read's record for a ticket whose Touches are of this kind."""
    touches = BODY.split("\n")[1]
    case.set_issue(issue(body=BODY.replace(touches, f"**Touches:** {KIND_TOUCHES[kind]}")))
    return {"read": case.read()[1]}


def run_records(case: Harness) -> dict:
    """Every record one run of the script routes on, by name, from read to release."""
    out = {}
    out["read"] = case.read()[1]
    seams = case.base / "seams.json"
    seams.write_text(json.dumps(CONFIRMED))
    out["launch"] = case.tool("launch", "7", "--seams", str(seams))[1]
    out["startNew"] = case.tool("start", "7")[1]
    commit(case, "tools/thing.py")
    case.behave('echo "check: fail: the tests failed."\n')
    out["checkFail"] = case.tool("check", "7")[1]
    commit(case, "tools/thing_tests.py")
    case.behave("sleep 30\n")
    out["checkRunning"] = case.tool("check", "7", env={"IMPLEMENT_TICKET_WAIT": "1"})[1]
    case.stop_checks()
    (case.stub / "behaviour.sh").unlink()
    (case.log_dir() / "check.pid").unlink(missing_ok=True)
    commit_files(case, {"docs/thing.md": "the thing\n", "tools/thing.py": THING,
                        "tools/thing_tests.py": THING_TESTS})
    save_mutation(case, "name", 1, "tools/thing_tests.py", {"tools/thing.py": "print('broken')\n"})
    out["checkPass"] = case.tool("check", "7")[1]
    out["prove"] = case.tool("prove", "7")[1]
    out["startFinished"] = case.tool("start", "7")[1]
    out["release"] = case.tool("release", "7")[1]
    case.set_issue(issue(labels=[{"name": "ready-for-human"}]))
    out["startRefused"] = case.tool("start", "7")[1]
    return out


class FixtureKeys(Harness):
    def setUp(self):
        super().setUp()
        self.fixture = json.loads(FIXTURE.read_text())

    def assert_keys(self, name: str, fixture: dict, written: dict) -> None:
        missing = sorted(set(fixture) - set(written))
        self.assertEqual([], missing, f"the tool's {name} record no longer has the fixture's keys "
                                      f"{missing}: change {FIXTURE.name} and the script together")

    def test_the_fixture_says_where_it_came_from_and_holds_each_kind(self):
        self.assertIn("implement_ticket_tests.py", self.fixture["note"])
        self.assertEqual([kind for kind, _ in load("implement_ticket").KINDS],
                         [ticket["kind"] for ticket in self.fixture["tickets"]])
        for ticket in self.fixture["tickets"]:
            self.assertIn(ticket["kind"], ticket["read"]["kinds"])

    def test_each_kinds_read_record_has_the_fixtures_keys(self):
        for ticket in self.fixture["tickets"]:
            with self.subTest(kind=ticket["kind"]):
                written = kind_records(self, ticket["kind"])["read"]
                self.assertIn(ticket["kind"], written["kinds"])
                self.assert_keys(f"{ticket['kind']} read", ticket["read"], written)

    def test_each_record_of_a_run_has_the_fixtures_keys(self):
        written = run_records(self)
        self.assertEqual(sorted(self.fixture["run"]), sorted(written))
        for name, record in self.fixture["run"].items():
            with self.subTest(record=name):
                self.assert_keys(name, record, written[name])
                self.assertEqual((record["step"], record["state"] if "state" in record else None),
                                 (written[name]["step"], written[name].get("state")))


def launcher_source() -> str:
    """The tool's LAUNCHER, read from its source rather than by importing the tool."""
    tree = ast.parse((ROOT / TOOL).read_text())
    for node in tree.body:
        names = [getattr(target, "id", None) for target in getattr(node, "targets", [])]
        if isinstance(node, ast.Assign) and names == ["LAUNCHER"]:
            return ast.literal_eval(node.value)
    raise AssertionError("tools/implement_ticket.py defines no LAUNCHER")


class Launcher(unittest.TestCase):
    """The check's first act, run alone: the one step of the claim that a case driving the tool
    cannot time, since it races the tool's own polling."""

    def setUp(self):
        scratch = tempfile.TemporaryDirectory(prefix="implement-launcher-")
        self.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name)
        self.pid_file, self.lock = self.dir / "check.pid", self.dir / "check.lock"
        self.out = self.dir / "out"

    def launch(self, claim: str) -> subprocess.Popen:
        return subprocess.Popen([sys.executable, "-I", "-c", launcher_source(), str(self.pid_file),
                                 str(self.lock), claim, "sh", "-c", f'echo $$ > "{self.out}"'],
                                env={"PATH": "/usr/bin:/bin"})

    def test_the_check_names_itself_under_the_lock_and_then_runs_as_that_pid(self):
        self.pid_file.write_text("claim 1\n")
        with open(self.lock, "a") as held:
            fcntl.flock(held, fcntl.LOCK_EX)
            process = self.launch("claim 1")
            time.sleep(0.5)
            self.assertEqual("claim 1\n", self.pid_file.read_text())
            self.assertFalse(self.out.exists())
        self.assertEqual(0, process.wait(timeout=30))
        self.assertEqual(f"{process.pid}\n", self.pid_file.read_text())
        self.assertEqual(f"{process.pid}\n", self.out.read_text())

    def test_a_check_whose_claim_was_cleared_exits_unrun(self):
        """A call cleared the starter's claim as stale and may have started its own check."""
        for held in ("claim 2\n", None):
            if held is None:
                self.pid_file.unlink()
            else:
                self.pid_file.write_text(held)
            self.assertEqual(3, self.launch("claim 1").wait(timeout=30))
            self.assertFalse(self.out.exists())
            self.assertEqual(held, self.pid_file.read_text() if held else None)
            self.assertEqual(held is None, not self.pid_file.exists())


if __name__ == "__main__":
    unittest.main(verbosity=1)
