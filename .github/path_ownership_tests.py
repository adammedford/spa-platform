#!/usr/bin/env python3
"""Regressions for A-05's two rules: whole diff, and per commit on an integration branch.

Owner: integrator. Run: python3 -I .github/path_ownership_tests.py

Each case builds a throwaway repository, because the rules are about shapes of history -- a lane
branch, two lanes merged, a commit that crosses owners -- and a shape is cheaper to build than to
find. The module resolves its root from __file__ and runs git there, so both are patched.
"""

import contextlib
import importlib.util
import io
import pathlib
import subprocess
import sys
import tempfile
import unittest

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
        self.assertIn("a commit on this branch touches more than one owner", out.lower())
        self.assertIn("a stray fix across two owners", out)

    def test_a_change_request_is_allowed_beside_another_owner(self):
        self.repo.branch("task/with-request")
        self.repo.commit("Task 1.4a and its request",
                         "infra/main.tf", "contracts/changes/1.4a-something.md")
        code, out = self.check("task/with-request")
        self.assertEqual(0, code, out)
        self.assertIn("all owned by ws1-infra", out)


if __name__ == "__main__":
    unittest.main(verbosity=2)
