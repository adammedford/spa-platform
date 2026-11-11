#!/usr/bin/env python3
"""Tests for tools/endpoint_paths.py. Owner: integrator. Built in #760.

Run: python3 -I tools/endpoint_paths_tests.py

**What runs is the script itself**, as `python3 -I tools/endpoint_paths.py`, in a throwaway
repository whose tracked files are a copy of this checkout's contracts/http-contract.md and the
skill, runbook and test-catalogue files a case writes. The expected verdicts are written here by
hand from the contract's Endpoints table and its reserved-paths sentence, not derived the way the
script derives them, and the last cases run the script on this checkout, which must pass as it
stands, and on a contract whose table or sentence is gone, which must not.

Checkout inputs: contracts/http-contract.md .claude/skills/*.md docs/runbooks/*.md docs/plan/04-tests.md
"""
from __future__ import annotations

import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "endpoint_paths.py"
CONTRACT = "contracts/http-contract.md"


class Tree(unittest.TestCase):
    """A throwaway repository holding the checkout's contract and the files a case writes."""

    def setUp(self) -> None:
        self.dir = pathlib.Path(tempfile.mkdtemp(prefix="endpoint-paths-")).resolve()
        self.addCleanup(shutil.rmtree, self.dir, True)
        self.git("init", "-q")
        self.write(CONTRACT, (ROOT / CONTRACT).read_text())

    def git(self, *args: str) -> None:
        done = subprocess.run(["git", "-C", str(self.dir), *args], capture_output=True, text=True, timeout=60,
                              check=False, env={"PATH": "/usr/bin:/bin:" + str(pathlib.Path(shutil.which("git")).parent),
                                                "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null",
                                                "HOME": str(self.dir)})
        self.assertEqual(done.returncode, 0, done.stderr)

    def write(self, path: str, text: str, tracked: bool = True) -> None:
        target = self.dir / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
        if tracked:
            self.git("add", path)

    def run_check(self, cwd: pathlib.Path | None = None) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, "-I", str(SCRIPT)], cwd=cwd or self.dir, capture_output=True,
                              text=True, timeout=60, check=False)

    def assertFails(self, done: subprocess.CompletedProcess, *named: str) -> None:
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        for line in named:
            self.assertIn(line, done.stderr.splitlines())
        self.assertTrue(done.stdout.splitlines()[-1].startswith("endpoint-paths: fail:"), done.stdout)

    def assertPasses(self, done: subprocess.CompletedProcess) -> None:
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertTrue(done.stdout.splitlines()[-1].startswith("endpoint-paths: pass:"), done.stdout)


class UnknownPaths(Tree):
    def test_bff_user_in_a_skill_fails_naming_the_file_and_the_line(self) -> None:
        self.write(".claude/skills/x/SKILL.md", "intro\n- `/bff/user` answering 200 before\n")
        self.assertFails(self.run_check(),
                         "endpoint-paths: .claude/skills/x/SKILL.md:2: /bff/user is not an endpoint "
                         "contracts/http-contract.md makes known")

    def test_a_path_in_a_runbook_and_in_the_test_catalogue_is_read(self) -> None:
        self.write("docs/runbooks/r.md", "curl https://app1.spa-poc.test/bff/user\n")
        self.write("docs/plan/04-tests.md", "| N-99 | `/readyz/extra` answers |\n")
        self.assertFails(self.run_check(),
                         "endpoint-paths: docs/runbooks/r.md:1: /bff/user is not an endpoint "
                         "contracts/http-contract.md makes known",
                         "endpoint-paths: docs/plan/04-tests.md:1: /readyz/extra is not an endpoint "
                         "contracts/http-contract.md makes known")

    def test_a_skill_file_at_any_depth_is_read(self) -> None:
        self.write(".claude/skills/x/deep/more.md", "`GET /bff/sessions`\n")
        self.assertFails(self.run_check(), "endpoint-paths: .claude/skills/x/deep/more.md:1: /bff/sessions is not "
                                           "an endpoint contracts/http-contract.md makes known")

    def test_the_bff_prefix_named_in_the_sentence_makes_no_path_under_it_known(self) -> None:
        self.write(".claude/skills/x/SKILL.md", "`/bff/anything`\n")
        self.assertFails(self.run_check())


class KnownPaths(Tree):
    def test_the_tables_paths_the_sentences_paths_and_an_api_path_pass(self) -> None:
        self.write(".claude/skills/x/SKILL.md",
                   "`GET /bff/session`, `/bff/login?returnUrl=/x`, `POST /bff/logout`.\n"
                   "`/readyz`'s revision, `verify-tunnel.sh /healthz`, `/signin-oidc`\n"
                   "`GET /api/orders/v1/items` through the proxy\n")
        done = self.run_check()
        self.assertPasses(done)
        self.assertEqual(done.stdout.splitlines()[-1], "endpoint-paths: pass: 6 paths in 1 files")

    def test_a_file_outside_the_three_sets_and_an_untracked_one_are_not_read(self) -> None:
        self.write("docs/notes.md", "`/bff/user`\n")
        self.write("docs/runbooks/sub/r.md", "`/bff/user`\n")
        self.write(".claude/skills/x/untracked.md", "`/bff/user`\n", tracked=False)
        self.assertPasses(self.run_check())

    def test_a_filesystem_path_that_holds_api_is_not_an_endpoint(self) -> None:
        self.write(".claude/skills/x/SKILL.md", "`tests/api/fixtures`, `src/bff/x`\n")
        done = self.run_check()
        self.assertPasses(done)
        self.assertEqual(done.stdout.splitlines()[-1], "endpoint-paths: pass: 0 paths in 1 files")

    def test_it_runs_from_a_subdirectory_at_the_top_level(self) -> None:
        self.write(".claude/skills/x/SKILL.md", "`/bff/user`\n")
        (self.dir / "docs").mkdir(exist_ok=True)
        self.assertFails(self.run_check(cwd=self.dir / "docs"))


class TheContract(Tree):
    def test_a_contract_with_no_endpoints_table_is_not_read_and_exits_2(self) -> None:
        text = (ROOT / CONTRACT).read_text().replace("## Endpoints", "## Elsewhere")
        self.write(CONTRACT, text)
        self.write(".claude/skills/x/SKILL.md", "`/bff/session`\n")
        done = self.run_check()
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("Endpoints table", done.stderr)

    def test_a_contract_with_no_reserved_paths_sentence_exits_2(self) -> None:
        text = (ROOT / CONTRACT).read_text().replace("An SPA cannot serve content under", "An SPA serves")
        self.write(CONTRACT, text)
        done = self.run_check()
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("reserved-paths sentence", done.stderr)

    def test_a_row_removed_from_the_table_makes_its_path_unknown(self) -> None:
        text = "".join(line for line in (ROOT / CONTRACT).read_text().splitlines(keepends=True)
                       if not line.startswith("| `POST /bff/logout`"))
        self.write(CONTRACT, text)
        self.write(".claude/skills/x/SKILL.md", "`POST /bff/logout`\n")
        self.assertFails(self.run_check())

    def test_healthz_left_out_of_the_sentence_is_unknown(self) -> None:
        text = (ROOT / CONTRACT).read_text().replace("`/signout-oidc`, `/healthz` or `/readyz`",
                                                     "`/signout-oidc` or `/readyz`")
        self.write(CONTRACT, text)
        self.write(".claude/skills/x/SKILL.md", "`/healthz`\n")
        self.assertFails(self.run_check())

    def test_no_contract_exits_2(self) -> None:
        self.git("rm", "-q", "-f", CONTRACT)
        done = self.run_check()
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)


class ThisCheckout(unittest.TestCase):
    def test_this_checkout_passes(self) -> None:
        done = subprocess.run([sys.executable, "-I", str(SCRIPT)], cwd=ROOT, capture_output=True, text=True,
                              timeout=60, check=False)
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)


if __name__ == "__main__":
    unittest.main()
