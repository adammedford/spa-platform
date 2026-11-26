#!/usr/bin/env python3
"""Tests for tools/implement_ticket.py. Owner: integrator.

Run: python3 -I tools/implement_ticket_tests.py

Every case runs a command of the tool as a subprocess, from the main checkout of a throwaway
repository with a bare origin, in an environment that inherits nothing, as
tools/overnight_collect_tests.py runs its own: a fake `gh` on PATH answers from a world file and logs
every call, so no case reaches GitHub. The repository holds the tool and the rules it loads by path
(tools/overnight_collect.py, tools/ticket_shape.py, .github/path-ownership.py, .github/touches.sh,
OWNERSHIP.md), copied from this checkout, beside two stubs: a tools/check.sh that writes the log a
case chooses and counts its runs, and a tools/test_reach.py that prints one fixed judgement. Each case
asserts on the record the command writes, its verdict line and its exit code, and on what the fake
gh and git show afterwards.
"""
from __future__ import annotations

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
                    "STUB_DIR": str(self.stub)}
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

    def stop_checks(self) -> None:
        pid_file = self.log_dir() / "check.pid"
        try:
            pid = int(pid_file.read_text().strip())
            os.killpg(pid, signal.SIGKILL)
        except (OSError, ValueError):
            pass

    # --- the tool -------------------------------------------------------------------------------

    def tool(self, *argv: str, env: dict | None = None):
        """Run a command; return (exit code, record, verdict line, stdout)."""
        done = subprocess.run([sys.executable, "-I", str(self.checkout / TOOL), *argv],
                              cwd=self.checkout, env={**self.env, **(env or {})},
                              capture_output=True, text=True, check=False, timeout=120)
        lines = done.stdout.splitlines()
        self.assertTrue(lines, f"nothing on stdout; stderr: {done.stderr}")
        verdict = lines[-1]
        record = json.loads(lines[0]) if len(lines) == 2 else None
        return done.returncode, record, verdict, done.stdout + done.stderr

    def read(self, **kw):
        return self.tool("read", "7", "--wt", str(self.wt), **kw)

    def saved(self, step: str) -> dict:
        return json.loads((self.log_dir() / f"{step}.json").read_text())

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
        self.assertEqual(self.git("rev-parse", "origin/main"), record["base"])
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

    def test_an_unreachable_gh_exits_2(self):
        code, _, verdict, _ = self.read(env={"GH_FAIL": "1"})
        self.assert_verdict(code, verdict, "read", 2)

    def test_a_missing_argument_exits_2_with_a_verdict(self):
        code, _, verdict, _ = self.tool("read", "7")
        self.assert_verdict(code, verdict, "read", 2, "--wt")


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
        base = self.git("rev-parse", "origin/main")
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
        commit(self, "tools/thing.py")
        code, _, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        code, record, verdict, _ = self.tool("start", "7")
        self.assert_verdict(code, verdict, "start", 0, "finished")
        self.assertEqual("finished", record["state"])
        # A commit after the pass makes it a run to continue again.
        commit(self, "tools/thing_tests.py")
        _, record, _, _ = self.tool("start", "7")
        self.assertEqual("continued", record["state"])

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

    def test_start_with_no_read_record_exits_2(self):
        code, _, verdict, _ = self.tool("start", "7")
        self.assert_verdict(code, verdict, "start", 2, "read record")


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
        fork = self.git("rev-parse", "origin/main")
        self.assertIn(f"stub check: --full --base {fork} --port 5180", log)
        self.assertNotIn("testReach", record)

    def test_the_port_is_passed_on(self):
        commit(self, "tools/thing.py")
        code, _, verdict, _ = self.tool("check", "7", "--port", "5191")
        self.assert_verdict(code, verdict, "check", 0)
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

    def test_an_uncommitted_change_is_no_pass(self):
        commit(self, "tools/thing.py")
        self.behave('echo during > tools/thing_tests.py\necho "check: pass: stub"\n')
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 1, "uncommitted")
        self.assertFalse(record["clean"])

    def test_an_uncommitted_change_before_the_check_starts_no_check(self):
        commit(self, "tools/thing.py")
        (self.wt / "tools" / "thing_tests.py").write_text("not committed\n")
        code, _, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 1, "uncommitted")
        self.assertEqual(0, self.runs())

    def test_another_owners_path_fails_naming_a05(self):
        for paths in (("tools/thing.py", "tools/bench/x.py"), ("tools/bench/x.py",)):
            with self.subTest(paths=paths):
                self.git("reset", "-q", "--hard", "origin/main", cwd=self.wt)
                commit(self, *paths)
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
        code, record, _, _ = self.tool("check", "7", env={"IMPLEMENT_TICKET_WAIT": "0.5"})
        self.assertEqual((2, True), (code, record["started"]))
        code, record, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 0)
        self.assertEqual((head, False), (record["head"], record["started"]))
        self.assertEqual(1, self.runs())
        # And a call after the verdict judges the same log again, with no new run.
        code, _, _, _ = self.tool("check", "7")
        self.assertEqual((0, 1), (code, self.runs()))

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
        log = (self.log_dir() / "check.log").read_text()
        self.assertIn("SIGINT <built-in function default_int_handler>", log)

    def test_check_with_no_start_record_exits_2(self):
        record_path = self.log_dir() / "start.json"
        record_path.unlink()
        code, _, verdict, _ = self.tool("check", "7")
        self.assert_verdict(code, verdict, "check", 2, "start record")


class Release(Harness):
    def test_release_unassigns_and_keeps_the_branch_and_the_worktree(self):
        self.read()
        self.tool("start", "7")
        code, record, verdict, _ = self.tool("release", "7")
        self.assert_verdict(code, verdict, "release", 0)
        self.assertEqual(record, self.saved("release"))
        self.assertEqual([], self.assignees())
        self.assertIn(["issue", "edit", "7", "--remove-assignee", "@me"], self.gh_calls())
        self.assertEqual((BRANCH, str(self.wt), True, True),
                         (record["branch"], record["worktree"], record["branchKept"],
                          record["worktreeKept"]))
        self.assertEqual(BRANCH, self.git("rev-parse", "--abbrev-ref", "HEAD", cwd=self.wt))

    def test_an_unreachable_gh_exits_2(self):
        self.read()
        self.tool("start", "7")
        code, _, verdict, _ = self.tool("release", "7", env={"GH_FAIL": "1"})
        self.assert_verdict(code, verdict, "release", 2)
        self.assertEqual(["case"], self.assignees())


if __name__ == "__main__":
    unittest.main(verbosity=1)
