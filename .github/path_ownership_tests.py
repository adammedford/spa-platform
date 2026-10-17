#!/usr/bin/env python3
"""Regressions for A-05's two rules: whole diff, and per commit on an integration branch.

Owner: integrator. Run: python3 -I .github/path_ownership_tests.py

Each case builds a throwaway repository, because the rules are about shapes of history -- a lane
branch, two lanes merged, a commit that crosses owners -- and a shape is cheaper to build than to
find. The module resolves its root from __file__ and runs git there, so both are patched. Each
repository has git's automatic housekeeping off, which ThrowawayRepositoryTests explains and holds.
"""

import contextlib
import importlib.util
import io
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("path_ownership", ROOT / ".github/path-ownership.py")
ownership = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ownership)

OWNERSHIP_BLOCK = """# Ownership

```
*                                     integrator
/src/Platform.Hosting/                ws2-hosting
/src/Platform.Proxy/                  ws4-proxy
/infra/                               ws1-infra
```
"""


class Repo:
    """A throwaway git repository, built commit by commit."""

    def __init__(self, path: pathlib.Path):
        self.path = path
        self.git("init", "-q", "-b", "main")
        # No housekeeping: every commit and merge would otherwise start a detached
        # `git maintenance run --auto` that can still be writing into .git while the case's
        # cleanup removes it. ThrowawayRepositoryTests has the detail. The switch they read:
        # https://git-scm.com/docs/git-config#Documentation/git-config.txt-maintenanceauto
        self.git("config", "maintenance.auto", "false")
        self.git("config", "user.email", "test@example.test")
        self.git("config", "user.name", "Test")
        self.write("OWNERSHIP.md", OWNERSHIP_BLOCK)
        self.commit("the ownership block")

    def git(self, *args: str) -> str:
        done = subprocess.run(["git", *args], cwd=self.path, capture_output=True, text=True,
                              check=True)
        return done.stdout.strip()

    def write(self, relative: str, body: str = "x\n") -> None:
        target = self.path / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(body)

    def commit(self, subject: str, *paths: str) -> str:
        self.write_all(paths)
        self.git("add", "-A")
        self.git("commit", "-q", "-m", subject)
        return self.git("rev-parse", "HEAD")

    def write_all(self, paths: tuple[str, ...]) -> None:
        for path in paths:
            self.write(path)

    def branch(self, name: str, at: str = "main") -> None:
        self.git("checkout", "-q", "-b", name, at)

    def checkout(self, name: str) -> None:
        self.git("checkout", "-q", name)

    def merge(self, name: str, subject: str) -> None:
        self.git("merge", "-q", "--no-ff", "-m", subject, name)


class RuleTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        path = pathlib.Path(self.tmp.name)
        self.repo = Repo(path)
        ownership.REPOSITORY_ROOT = path
        ownership.OWNERSHIP = path / "OWNERSHIP.md"
        self.addCleanup(self.tmp.cleanup)

    def check(self, head: str) -> tuple[int, str]:
        """The exit code and output of checking main..head, as the hook would."""
        buffer = io.StringIO()
        argv = sys.argv
        sys.argv = ["path-ownership.py", "main", head]
        try:
            with contextlib.redirect_stdout(buffer):
                code = ownership.main()
        finally:
            sys.argv = argv
        return code, buffer.getvalue()

    def test_a_lane_branch_of_one_owner_passes(self):
        self.repo.branch("task/2.3b-lane")
        self.repo.commit("Task 2.3b", "src/Platform.Hosting/RuleIds.cs")
        code, out = self.check("task/2.3b-lane")
        self.assertEqual(0, code, out)
        self.assertIn("all owned by ws2-hosting", out)

    def test_a_lane_branch_crossing_owners_fails_and_is_judged_whole(self):
        self.repo.branch("task/bad-lane")
        self.repo.commit("one commit, two owners",
                         "src/Platform.Hosting/A.cs", "src/Platform.Proxy/B.cs")
        code, out = self.check("task/bad-lane")
        self.assertEqual(1, code, out)
        self.assertIn("more than one owner's paths", out)
        self.assertIn("is read per commit instead", out)

    def test_an_integration_branch_of_single_owner_lanes_passes_per_commit(self):
        self.repo.branch("lane-a")
        self.repo.commit("Task 2.3b", "src/Platform.Hosting/A.cs")
        self.repo.checkout("main")
        self.repo.branch("lane-b")
        self.repo.commit("Task 4.3b", "src/Platform.Proxy/B.cs")
        self.repo.checkout("main")
        self.repo.branch("overnight/today")
        self.repo.merge("lane-a", "Merge task 2.3b")
        self.repo.merge("lane-b", "Merge task 4.3b")

        code, out = self.check("overnight/today")
        self.assertEqual(0, code, out)
        self.assertIn("every commit is", out)
        self.assertIn("ws2-hosting", out)
        self.assertIn("ws4-proxy", out)

    def test_an_integration_branch_with_one_crossing_commit_fails_and_names_it(self):
        self.repo.branch("lane-a")
        self.repo.commit("Task 2.3b", "src/Platform.Hosting/A.cs")
        self.repo.checkout("main")
        self.repo.branch("overnight/today")
        self.repo.merge("lane-a", "Merge task 2.3b")
        self.repo.commit("a stray fix across two owners",
                         "src/Platform.Hosting/C.cs", "src/Platform.Proxy/D.cs")

        code, out = self.check("overnight/today")
        self.assertEqual(1, code, out)
        self.assertIn("a commit crosses owners", out.lower())
        self.assertIn("a stray fix across two owners", out)

    def test_split_task_without_merges_still_fails(self):
        self.repo.branch("task/split")
        self.repo.commit("hosting", "src/Platform.Hosting/A.cs")
        self.repo.commit("proxy", "src/Platform.Proxy/B.cs")
        code, out = self.check("HEAD")
        self.assertEqual(1, code, out)

    def test_nested_integration_merge_passes(self):
        self.repo.branch("lane-a")
        self.repo.commit("hosting", "src/Platform.Hosting/A.cs")
        self.repo.checkout("main")
        self.repo.branch("lane-b")
        self.repo.commit("proxy", "src/Platform.Proxy/B.cs")
        self.repo.checkout("main")
        self.repo.branch("integration/inner")
        self.repo.merge("lane-a", "hosting lane")
        self.repo.merge("lane-b", "proxy lane")
        self.repo.checkout("main")
        self.repo.branch("integration/outer")
        self.repo.merge("integration/inner", "merge the integrated lanes")
        code, out = self.check("HEAD")
        self.assertEqual(0, code, out)

    def test_reverted_crossing_commit_still_fails(self):
        for empty in (False, True):
            with self.subTest(empty=empty):
                self.repo.checkout("main")
                self.repo.branch(f"lane-{empty}")
                bad = self.repo.commit("crossing before revert",
                                       "src/Platform.Hosting/A.cs", "src/Platform.Proxy/B.cs")
                self.repo.git("revert", "--no-edit", bad)
                if not empty:
                    self.repo.commit("keep hosting", "src/Platform.Hosting/C.cs")
                self.repo.checkout("main")
                self.repo.branch(f"integration-{empty}")
                self.repo.merge(f"lane-{empty}", "integrate the lane")
                code, out = self.check("HEAD")
                self.assertEqual(1, code, out)
                self.assertIn(bad[:9], out)

    def merge_with_edits(self, *paths):
        self.repo.branch("lane")
        self.repo.commit("hosting", "src/Platform.Hosting/A.cs")
        self.repo.checkout("main")
        self.repo.branch("integration")
        self.repo.git("merge", "--no-ff", "--no-commit", "lane")
        self.repo.commit("merge with edits", *paths)

    def test_single_owner_merge_edit_passes(self):
        self.merge_with_edits("src/Platform.Proxy/B.cs")
        code, out = self.check("HEAD")
        self.assertEqual(0, code, out)

    def test_crossing_merge_edit_fails(self):
        self.merge_with_edits("src/Platform.Proxy/B.cs", "infra/main.tf")
        code, out = self.check("HEAD")
        self.assertEqual(1, code, out)
        self.assertIn("merge with edits", out)

    def test_rename_across_owners_fails(self):
        self.repo.commit("initial hosting", "src/Platform.Hosting/A.cs")
        self.repo.branch("task/rename")
        self.repo.git("mv", "src/Platform.Hosting", "src/Platform.Proxy")
        self.repo.commit("move between owners")
        code, out = self.check("HEAD")
        self.assertEqual(1, code, out)
        self.assertIn("ws2-hosting", out)
        self.assertIn("ws4-proxy", out)

    def test_octopus_merge_fails_with_instruction(self):
        for name, path in (("hosting", "src/Platform.Hosting/A.cs"),
                           ("proxy", "src/Platform.Proxy/B.cs"),
                           ("infra", "infra/main.tf")):
            self.repo.branch(name)
            self.repo.commit(name, path)
        self.repo.checkout("main")
        self.repo.branch("integration/octopus")
        self.repo.git("merge", "--no-ff", "-m", "octopus", "hosting", "proxy", "infra")
        code, out = self.check("HEAD")
        self.assertEqual(1, code, out)
        self.assertIn("use two-parent merges", out)

    def test_unsupported_remerge_diff_fails_closed(self):
        self.merge_with_edits("src/Platform.Proxy/B.cs")
        original = ownership.run

        def old_git(command):
            if "--remerge-diff" in command:
                raise subprocess.CalledProcessError(129, command, stderr="unknown option")
            return original(command)

        with mock.patch.object(ownership, "run", side_effect=old_git):
            code, out = self.check("HEAD")
        self.assertEqual(1, code, out)
        self.assertIn("unknown option", out)
        self.assertIn("Git with --remerge-diff support", out)

    def test_quoted_paths_keep_their_actual_owner(self):
        self.repo.branch("task/quoted")
        self.repo.commit("tab in hosting path", "src/Platform.Hosting/A\tB.cs")
        code, out = self.check("HEAD")
        self.assertEqual(0, code, out)
        self.assertIn("all owned by ws2-hosting", out)

    def test_conflict_resolution_cannot_cross_owners(self):
        paths = ("src/Platform.Hosting/A.cs", "src/Platform.Proxy/B.cs")
        self.repo.commit("initial hosting", paths[0])
        self.repo.commit("initial proxy", paths[1])
        for lane in ("left", "right"):
            self.repo.branch(lane)
            for path in paths:
                self.repo.write(path, lane + "\n")
                self.repo.commit("edit one owned path")
        self.repo.checkout("left")
        self.repo.git("merge", "--no-ff", "--no-commit", "-s", "ours", "right")
        # Commit hand-authored resolutions in both owners' paths.
        for path in paths:
            self.repo.write(path, "resolved\n")
        bad = self.repo.commit("crossing resolution")
        code, out = self.check("HEAD")
        self.assertEqual(1, code, out)
        self.assertIn(bad[:9], out)

    def test_a_change_request_is_allowed_beside_another_owner(self):
        self.repo.branch("task/with-request")
        self.repo.commit("Task 1.4a and its request",
                         "infra/main.tf", "contracts/changes/1.4a-something.md")
        code, out = self.check("task/with-request")
        self.assertEqual(0, code, out)
        self.assertIn("all owned by ws1-infra", out)


def commands_started(trace: pathlib.Path) -> set[str]:
    """The git commands a GIT_TRACE2_EVENT file saw start, by name.

    Every git process writes one cmd_name event as it starts, a detached one included:
    https://git-scm.com/docs/api-trace2#_event_format
    """
    names = set()
    for line in trace.read_text(encoding="utf-8").splitlines():
        event = json.loads(line)
        if event.get("event") == "cmd_name":
            names.add(event["name"])
    return names


class ThrowawayRepositoryTests(unittest.TestCase):
    """Repo runs no git housekeeping, so no git process outlives the call that started it.

    The exception is a process a developer's own configuration starts, which the case does not look
    for: with core.fsmonitor=true, commands start git fsmonitor--daemon in the background and leave
    it running. Nothing in this repository or its CI workflow sets it.

      https://git-scm.com/docs/git-fsmonitor--daemon

    Ticket 71. A commit or a merge ends by starting `git maintenance run --auto`, which detaches by
    default (maintenance.autoDetach). Git 2.54 made its default strategy geometric, and in a
    repository holding no packs that strategy's default threshold repacks once two loose objects
    share the objects/17 shard, the sample its estimate reads. Commit ids depend on the clock, so
    any commit can be the one. The repack writes into .git after the call the case waited for has
    returned, and when that overlaps TemporaryDirectory.cleanup, the cleanup fails with "Directory
    not empty: '.git'" after every assertion has passed. That is how CI saw it, on git 2.55.0.

      https://github.com/git/git/blob/v2.54.0/Documentation/RelNotes/2.54.0.adoc
      https://github.com/git/git/blob/v2.55.0/Documentation/config/maintenance.adoc
      https://github.com/git/git/blob/v2.55.0/builtin/gc.c (geometric_repack_auto_condition)

    The case reads git's trace rather than racing the cleanup, a race it would win nearly every
    time. Every git process writes a cmd_name event as it starts, a detached one included, so the
    trace names everything the calls started. The commit and the merge are what start housekeeping,
    and the trace must show both, or its silence about housekeeping proves nothing.
    """

    def test_a_commit_or_a_merge_starts_no_housekeeping(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        trace = pathlib.Path(scratch.name) / "trace.json"
        path = pathlib.Path(scratch.name) / "repository"
        path.mkdir()

        with mock.patch.dict(os.environ, {"GIT_TRACE2_EVENT": str(trace)}):
            repo = Repo(path)
            repo.branch("lane")
            repo.commit("hosting", "src/Platform.Hosting/A.cs")
            repo.checkout("main")
            repo.merge("lane", "merge the lane")

        started = commands_started(trace)
        self.assertIn("commit", started)
        self.assertIn("merge", started)
        self.assertNotIn("maintenance", started)
        self.assertNotIn("gc", started)


if __name__ == "__main__":
    unittest.main(verbosity=2)
