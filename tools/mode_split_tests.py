#!/usr/bin/env python3
"""Tests for tools/mode_split.py. Owner: integrator. Built in #1344.

Run: python3 -I tools/mode_split_tests.py

**What runs is the script itself**, as `python3 -I tools/mode_split.py`, in a throwaway repository
whose README.md table, contracts/README.md list and tracked files a case writes by hand. It never
reads this checkout's own tree, table or list (#1344): the expected verdicts are written here from
the small tables below, not derived the way the script derives them. tools/check.sh and CI run the
guard on the real tree.
"""
from __future__ import annotations

import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "mode_split.py"

ROWS = [
    "| `src/`, `README.md`, `Directory.*.props` | Development |",
    "| The files of `contracts/` its list names as the project's own: `README.md`, `changes/` | Development |",
    "| `infra/envs/poc`, `tools/scripts/poc/`, `tools/aws-project*.env` | Proof cell |",
    "| `tools/spactl`, and the files of `contracts/` its list names as the unit's | Unit of adoption |",
    "| `infra/modules/cell`, `tools/scripts/` | Reference |",
    "| Every other file directly in `tools/`, `.github/`, `AGENTS.md` | Process |",
]
UNITS = ["- `registry.schema.json`, with `registry.example.json`", "- `http-contract.md`"]
OWN = ["- `README.md`", "- `changes/`"]
FILES = [
    "src/A/x.cs", "Directory.Build.props",
    "contracts/registry.schema.json", "contracts/registry.example.json", "contracts/http-contract.md",
    "contracts/changes/a.md",
    "tools/check.sh", "tools/aws-project-x.env", "tools/scripts/deploy.sh", "tools/scripts/poc/up.sh",
    "tools/spactl/Program.cs", "infra/envs/poc/main.tf", "infra/modules/cell/main.tf",
    ".github/ci.yml", "AGENTS.md",
]
# By hand from the rows above, with README.md and contracts/README.md: 5 development (README.md,
# src/A/x.cs, Directory.Build.props, contracts/README.md, contracts/changes/a.md), 3 proof cell, 4 unit
# of adoption (tools/spactl's file, the schema, its example and the browser contract), 2 reference
# and 3 process.
BASELINE = "mode-split: pass: 17 paths: 5 development, 3 proof cell, 4 unit of adoption, 2 reference, 3 process"


def readme(rows: list[str], begin: str = "<!-- BEGIN path values -->") -> str:
    return ("# A tree\n\nThe gateway image is built from `src/Gateway/Dockerfile`.\n\n" + begin
            + "\n| Path | Value |\n| --- | --- |\n" + "\n".join(rows) + "\n<!-- END path values -->\n\n## After\n")


def contracts_readme(units: list[str], own: list[str], begin: str = "<!-- BEGIN contracts list -->") -> str:
    return ("# Contracts\n\nProse naming `elsewhere.md`.\n\n" + begin + "\nThe unit's:\n\n" + "\n".join(units)
            + "\n\nThe project's own:\n\n" + "\n".join(own) + "\n<!-- END contracts list -->\n\n## After\n")


class Tree(unittest.TestCase):
    """A throwaway repository holding a hand-written table, list and tree."""

    def setUp(self) -> None:
        self.dir = pathlib.Path(tempfile.mkdtemp(prefix="mode-split-")).resolve()
        self.addCleanup(shutil.rmtree, self.dir, True)
        self.git("init", "-q")
        self.write("README.md", readme(ROWS))
        self.write("contracts/README.md", contracts_readme(UNITS, OWN))
        for path in FILES:
            self.write(path, "x\n")

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
        self.assertEqual(sorted(named), sorted(done.stderr.splitlines()))
        self.assertTrue(done.stdout.splitlines()[-1].startswith("mode-split: fail:"), done.stdout)

    def assertUnread(self, done: subprocess.CompletedProcess, what: str) -> None:
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn(what, done.stderr)
        self.assertEqual(done.stdout.splitlines()[-1], "mode-split: fail: nothing was judged")


class OneValuePerPath(Tree):
    def test_the_baseline_tree_passes_and_counts_each_value(self) -> None:
        # Two pairs of rows nest here, and pass with the deeper row's value: `tools/scripts/poc/` in
        # `tools/scripts/`, and `tools/aws-project*.env` in the process row's `tools/*`. Read with the
        # shallower value, tools/scripts/poc/up.sh would fail the poc clause and the counts would move.
        done = self.run_check()
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual("", done.stderr)
        self.assertEqual(BASELINE, done.stdout.splitlines()[-1])

    def test_a_path_with_no_row_fails(self) -> None:
        self.write("docs/x.md", "x\n")
        self.assertFails(self.run_check(), "mode-split: the tree: docs/x.md: no row of the path table matches it")

    def test_a_new_directory_under_tools_has_no_row(self) -> None:
        # "Every other file directly in `tools/`" is `tools/*`, which never crosses a `/`.
        self.write("tools/newdir/a.py", "x\n")
        self.assertFails(self.run_check(),
                         "mode-split: the tree: tools/newdir/a.py: no row of the path table matches it")

    def test_a_span_naming_a_directory_covers_it_on_a_segment_boundary_only(self) -> None:
        self.write("infra/modules/cell-x/main.tf", "x\n")
        self.assertFails(self.run_check(),
                         "mode-split: the tree: infra/modules/cell-x/main.tf: no row of the path table matches it")

    def test_a_path_two_rows_match_at_one_depth_fails(self) -> None:
        rows = [row.replace("`Directory.*.props`", "`Directory.*.props`, `AGENTS.md`") for row in ROWS]
        self.write("README.md", readme(rows))
        self.assertFails(self.run_check(), "mode-split: the tree: AGENTS.md: rows match it and neither is deeper: "
                                           "`AGENTS.md` (Development) and `AGENTS.md` (Process)")

    def test_a_deeper_star_span_and_a_deeper_plain_span_take_the_path(self) -> None:
        # `tools/check*` up to its star is `tools/check`, which extends `tools/*`'s `tools/`, so it is
        # deeper and tools/check.sh moves from process to reference; `src/A/` against `src/` is the
        # plain-span case, and src/A/x.cs moves from development to reference.
        rows = ROWS + ["| `tools/check*`, `src/A/` | Reference |"]
        self.write("README.md", readme(rows))
        done = self.run_check()
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual("mode-split: pass: 17 paths: 4 development, 3 proof cell, 4 unit of adoption, "
                         "4 reference, 2 process", done.stdout.splitlines()[-1])

    def test_the_table_losing_the_tools_scripts_row_fails_naming_a_path_under_it(self) -> None:
        # The guard reads the table and holds no copy of it.
        rows = [row.replace(", `tools/scripts/`", "") for row in ROWS]
        self.assertNotEqual(rows, ROWS)
        self.write("README.md", readme(rows))
        self.assertFails(self.run_check(),
                         "mode-split: the tree: tools/scripts/deploy.sh: no row of the path table matches it")

    def test_an_untracked_file_is_not_judged(self) -> None:
        self.write("docs/x.md", "x\n", tracked=False)
        self.assertEqual(0, self.run_check().returncode)

    def test_it_runs_from_a_subdirectory_at_the_top_level(self) -> None:
        self.write("docs/x.md", "x\n")
        self.assertFails(self.run_check(cwd=self.dir / "tools"),
                         "mode-split: the tree: docs/x.md: no row of the path table matches it")


class TheContractsList(Tree):
    def test_an_unlisted_file_under_contracts_fails(self) -> None:
        self.write("contracts/new.md", "x\n")
        self.assertFails(self.run_check(),
                         "mode-split: the tree: contracts/new.md: the list in contracts/README.md does not name it")

    def test_a_row_naming_a_contracts_file_is_not_read_for_it(self) -> None:
        # Every path under contracts/ takes its value from the list, so a row's span there gives none.
        rows = ROWS + ["| `contracts/new.md` | Reference |"]
        self.write("README.md", readme(rows))
        self.write("contracts/new.md", "x\n")
        self.assertFails(self.run_check(),
                         "mode-split: the tree: contracts/new.md: the list in contracts/README.md does not name it")

    def test_an_example_takes_its_schemas_value_only_when_its_bullet_names_it(self) -> None:
        self.write("contracts/README.md", contracts_readme(["- `registry.schema.json`", "- `http-contract.md`"], OWN))
        self.assertFails(self.run_check(), "mode-split: the tree: contracts/registry.example.json: "
                                           "the list in contracts/README.md does not name it")

    def test_a_file_under_both_lead_lines_fails(self) -> None:
        self.write("contracts/README.md", contracts_readme(UNITS, OWN + ["- `http-contract.md`"]))
        self.assertFails(self.run_check(), "mode-split: the tree: contracts/http-contract.md: "
                                           "the list in contracts/README.md names it under both lead lines")

    def test_moving_a_file_between_lead_lines_moves_its_value(self) -> None:
        self.write("contracts/README.md", contracts_readme(UNITS[:1], OWN + ["- `http-contract.md`"]))
        done = self.run_check()
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual("mode-split: pass: 17 paths: 6 development, 3 proof cell, 3 unit of adoption, "
                         "2 reference, 3 process", done.stdout.splitlines()[-1])


class PocDirectories(Tree):
    def test_a_path_under_a_poc_directory_whose_value_is_not_proof_cell_fails(self) -> None:
        rows = [row.replace(", `tools/scripts/poc/`", "") for row in ROWS]
        self.assertNotEqual(rows, ROWS)
        self.write("README.md", readme(rows))
        self.assertFails(self.run_check(), "mode-split: the tree: tools/scripts/poc/up.sh: it sits under a "
                                           "directory named poc, and its value is Reference, not Proof cell")

    def test_a_poc_directory_deep_in_a_development_path_fails(self) -> None:
        self.write("src/poc/x/y.cs", "x\n")
        self.assertFails(self.run_check(), "mode-split: the tree: src/poc/x/y.cs: it sits under a "
                                           "directory named poc, and its value is Development, not Proof cell")

    def test_a_file_named_poc_is_not_under_a_poc_directory(self) -> None:
        self.write("src/poc", "x\n")
        self.assertEqual(0, self.run_check().returncode)


class NothingRead(Tree):
    def test_a_readme_with_no_begin_marker_exits_2(self) -> None:
        self.write("README.md", readme(ROWS, begin="<!-- BEGIN path table -->"))
        self.assertUnread(self.run_check(), "README.md has no path table")

    def test_a_contracts_readme_with_no_begin_marker_exits_2(self) -> None:
        self.write("contracts/README.md", contracts_readme(UNITS, OWN, begin="<!-- BEGIN list -->"))
        self.assertUnread(self.run_check(), "contracts/README.md has no contracts list")

    def test_a_missing_readme_exits_2(self) -> None:
        self.git("rm", "-q", "-f", "README.md")
        self.assertUnread(self.run_check(), "README.md could not be read")

    def test_a_readme_that_is_not_utf8_exits_2_on_the_verdict_line(self) -> None:
        # A file the check cannot decode is one it cannot read, so it ends on the verdict line, not
        # on a traceback (CODING_STANDARDS.md, "A check fails closed").
        (self.dir / "README.md").write_bytes(readme(ROWS).encode() + b"\xff\xfe\n")
        self.git("add", "README.md")
        self.assertUnread(self.run_check(), "README.md could not be read")

    def test_a_row_with_an_unknown_value_exits_2(self) -> None:
        self.write("README.md", readme(ROWS + ["| `docs/` | Documentation |"]))
        self.assertUnread(self.run_check(), "a row it cannot read")

    def test_a_row_that_names_no_path_exits_2(self) -> None:
        self.write("README.md", readme(ROWS + ["| The docs | Development |"]))
        self.assertUnread(self.run_check(), "a row that names no path")

    def test_a_table_with_no_header_exits_2(self) -> None:
        self.write("README.md", readme(ROWS).replace("| Path | Value |", "| Where | What |"))
        self.assertUnread(self.run_check(), "header")

    def test_a_list_with_no_lead_line_for_the_projects_own_exits_2(self) -> None:
        self.write("contracts/README.md", contracts_readme(UNITS, OWN).replace("The project's own:", ""))
        self.assertUnread(self.run_check(), "lacks a lead line")

    def test_a_list_line_that_is_not_a_bullet_exits_2(self) -> None:
        self.write("contracts/README.md", contracts_readme(UNITS + ["Also `gates.md`."], OWN))
        self.assertUnread(self.run_check(), "a line it cannot read")

    def test_an_empty_listing_exits_2(self) -> None:
        self.git("rm", "-q", "--cached", "-r", ".")
        self.assertUnread(self.run_check(), "listed no tracked path")


if __name__ == "__main__":
    unittest.main()
