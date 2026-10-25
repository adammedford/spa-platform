#!/usr/bin/env python3
"""Tests for tools/test_reach.py. Owner: integrator. Built in #375.

Run: python3 -I tools/test_reach_tests.py

**What runs is the script itself**, as `python3 -I tools/test_reach.py --log <log> --base <rev>
--head <rev>`, in a throwaway repository whose project files copy the shape of this repository's:
Spactl.Tests references tools/spactl, which references the registry, and copies contract and
fixture files with None items; the harness references the gateway; the architecture tests read
every project. Each case commits one change and writes the log tools/check.sh would have left.

The 2026-10-24 replay is #305's: a change to Python under tools/bench/ alone, and 30 Spactl.Tests
failures (#373). Its failed lines are typed from .scratch/overnight/log/issue-305-integrate.txt,
with the worktree path replaced by the case's.

The last case reads this repository: every top-level entry a test source names as a string literal
must be a key of the tool's RUNTIME_ROOTS, so a test that starts reading a new part of the tree by
name fails here until the tool says how that read is covered.
"""
from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "test_reach.py"

PROJECTS = {
    "src/Platform.Abstractions/Platform.Abstractions.csproj": [],
    "src/Platform.Registry/Platform.Registry.csproj": [
        '<EmbeddedResource Include="../../contracts/*.schema.json" />'],
    "src/Gateway/Gateway.csproj": ['<ProjectReference Include="../Platform.Registry/Platform.Registry.csproj" />'],
    "tools/spactl/spactl.csproj": [
        '<ProjectReference Include="../../src/Platform.Abstractions/Platform.Abstractions.csproj" />',
        '<ProjectReference Include="../../src/Platform.Registry/Platform.Registry.csproj" />'],
    "tools/bench/floor/Bench.Floor.csproj": [],
    "tests/Spactl.Tests/Spactl.Tests.csproj": [
        '<ProjectReference Include="../../tools/spactl/spactl.csproj" />',
        '<None Include="../../docs/samples/**/*.json" CopyToOutputDirectory="PreserveNewest" />'],
    "tests/Platform.Registry.Tests/Platform.Registry.Tests.csproj": [
        '<ProjectReference Include="..\\..\\src\\Platform.Registry\\Platform.Registry.csproj" />'],
    "tests/Gateway.TestHarness/Gateway.TestHarness.csproj": [
        '<ProjectReference Include="../../src/Gateway/Gateway.csproj" />'],
    "tests/Gateway.IntegrationTests/Gateway.IntegrationTests.csproj": [
        '<ProjectReference Include="../Gateway.TestHarness/Gateway.TestHarness.csproj" />'],
    "tests/e2e/Gateway.E2ETests.csproj": [
        '<ProjectReference Include="../Gateway.TestHarness/Gateway.TestHarness.csproj" />'],
    "tests/Gateway.ArchitectureTests/Gateway.ArchitectureTests.csproj": [
        '<ProjectReference Include="../../src/Platform.Abstractions/Platform.Abstractions.csproj" />'],
    "tests/Odd.Tests/Odd.Tests.csproj": ['<None Include="$(RepoRoot)/anything.txt" />'],
}

TESTS_FAILED = "check: fail: the tests failed."


def failed_line(top: pathlib.Path, project: str, dll: str, errors: int = 30) -> str:
    return f"  {top}/{project}/bin/Debug/net10.0/{dll}.dll (net10.0|arm64) failed with {errors} error(s) (7s 652ms)"


def passed_line(top: pathlib.Path, project: str, dll: str) -> str:
    return f"  {top}/{project}/bin/Debug/net10.0/{dll}.dll (net10.0|arm64) passed (1s 082ms)"


class Case:
    def __init__(self, test: unittest.TestCase):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.repo = self.dir / "repo"
        self.repo.mkdir()
        self.env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(self.dir),
                    "GIT_CONFIG_NOSYSTEM": "1", "LC_ALL": "C"}
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "t@example.com")
        self.git("config", "user.name", "T")
        for path, lines in PROJECTS.items():
            self.write(path, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n"
                       + "".join(f"    {line}\n" for line in lines) + "  </ItemGroup>\n</Project>\n")
        for path in ("README.md", "Directory.Build.props", "contracts/registry.schema.json",
                     "src/Platform.Registry/RegistrySchemas.cs", "tools/bench/report.py",
                     "tests/Platform.Registry.Tests/RegistryTests.cs", "docs/notes.md"):
            self.write(path, "base\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "base")
        self.base = self.git("rev-parse", "HEAD")

    def git(self, *args: str) -> str:
        return subprocess.run(["git", *args], cwd=self.repo, env=self.env, capture_output=True, text=True,
                              check=True).stdout.strip()

    def write(self, path: str, text: str):
        target = self.repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)

    def change(self, *paths: str) -> str:
        for path in paths:
            self.write(path, "changed\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "the ticket")
        return self.git("rev-parse", "HEAD")

    def run(self, log_lines: list[str] | None, head: str = "HEAD", base: str | None = None):
        log = self.dir / "integrate.txt"
        if log_lines is not None:
            log.write_text("\n".join(log_lines) + "\n")
        out = subprocess.run([sys.executable, "-I", str(SCRIPT), "--log", str(log), "--base", base or self.base,
                              "--head", head], cwd=self.repo, env=self.env, capture_output=True, text=True)
        return out

    def judge(self, log_lines: list[str], **kw) -> dict:
        out = self.run(log_lines, **kw)
        if out.returncode != 0:
            raise AssertionError(f"exit {out.returncode}: {out.stderr}")
        return json.loads(out.stdout)


def reached(result: dict) -> dict[str, list[str]]:
    return {p["project"]: p["reachedBy"] for p in result["projects"]}


class TestReach(unittest.TestCase):
    def spactl_failed(self, case: Case) -> list[str]:
        return [
            "check: shell: /bin/bash, bash 3.2.57(1)-release",
            "failed Spactl.Tests.AppCommandTests.Add_carries_every_property_of_the_definition_into_the_entry (0ms)",
            passed_line(case.repo, "tests/Platform.Registry.Tests", "Platform.Registry.Tests"),
            failed_line(case.repo, "tests/Spactl.Tests", "Spactl.Tests"),
            "Test run summary: Failed!",
            failed_line(case.repo, "tests/Spactl.Tests", "Spactl.Tests"),
            passed_line(case.repo, "tests/e2e", "Gateway.E2ETests"),
            TESTS_FAILED,
        ]

    def test_the_2026_10_24_replay_reaches_nothing(self):
        # #305: Python under tools/bench/ alone, and 30 Spactl.Tests failures it cannot cause.
        case = Case(self)
        case.change("tools/bench/report.py")
        result = case.judge(self.spactl_failed(case))
        self.assertEqual("tests", result["failedAt"])
        self.assertEqual({"tests/Spactl.Tests": []}, reached(result))
        self.assertEqual(["tools/bench/report.py"], result["changed"])

    def test_the_2026_10_23_replay_reaches_nothing(self):
        # #150: the registry's tests alone; Spactl.Tests references the registry, not its tests.
        case = Case(self)
        case.change("tests/Platform.Registry.Tests/RegistryTests.cs")
        self.assertEqual({"tests/Spactl.Tests": []}, reached(case.judge(self.spactl_failed(case))))

    def test_a_referenced_project_reaches_it_transitively(self):
        case = Case(self)
        case.change("src/Platform.Registry/RegistrySchemas.cs")
        self.assertEqual({"tests/Spactl.Tests": ["src/Platform.Registry/RegistrySchemas.cs (project)"]},
                         reached(case.judge(self.spactl_failed(case))))

    def test_a_backslashed_reference_is_followed(self):
        case = Case(self)
        case.change("src/Platform.Registry/RegistrySchemas.cs")
        result = case.judge([failed_line(case.repo, "tests/Platform.Registry.Tests", "Platform.Registry.Tests"),
                             TESTS_FAILED])
        self.assertEqual({"tests/Platform.Registry.Tests": ["src/Platform.Registry/RegistrySchemas.cs (project)"]},
                         reached(result))

    def test_a_reference_two_projects_away_reaches_it(self):
        # IntegrationTests -> TestHarness -> Gateway -> Registry.
        case = Case(self)
        case.change("src/Platform.Registry/RegistrySchemas.cs")
        result = case.judge([failed_line(case.repo, "tests/Gateway.IntegrationTests", "Gateway.IntegrationTests"),
                             TESTS_FAILED])
        self.assertEqual(["src/Platform.Registry/RegistrySchemas.cs (project)"],
                         reached(result)["tests/Gateway.IntegrationTests"])

    def test_the_projects_own_folder_reaches_it(self):
        case = Case(self)
        case.change("tests/Spactl.Tests/AppCommandTests.cs")
        self.assertEqual(["tests/Spactl.Tests/AppCommandTests.cs (project)"],
                         reached(case.judge(self.spactl_failed(case)))["tests/Spactl.Tests"])

    def test_an_item_include_glob_reaches_it(self):
        case = Case(self)
        case.change("docs/samples/deep/one.json")
        self.assertEqual(["docs/samples/deep/one.json (item)"],
                         reached(case.judge(self.spactl_failed(case)))["tests/Spactl.Tests"])

    def test_an_item_of_a_referenced_project_reaches_it(self):
        # The registry embeds contracts/*.schema.json; contracts/ is also read at runtime, and the
        # item rule fires first.
        case = Case(self)
        case.change("contracts/registry.schema.json")
        self.assertEqual(["contracts/registry.schema.json (item)"],
                         reached(case.judge(self.spactl_failed(case)))["tests/Spactl.Tests"])

    def test_a_build_wide_file_reaches_every_project(self):
        for path in ("Directory.Build.props", "README.md", "global.json", "tools/bench/floor/Bench.Floor.csproj",
                     "infra/build.targets"):
            with self.subTest(path=path):
                case = Case(self)
                case.change(path)
                self.assertEqual([f"{path} (build-wide)"],
                                 reached(case.judge(self.spactl_failed(case)))["tests/Spactl.Tests"])

    def test_a_path_tests_read_at_runtime_reaches_every_project(self):
        for path in ("fixtures/apps/app1.json", "infra/roots/a/main.tf", "docs/plan/04-tests.md",
                     "tools/aws-project.env", "contracts/http-contract.md"):
            with self.subTest(path=path):
                case = Case(self)
                case.change(path)
                self.assertEqual([f"{path} (read at runtime)"],
                                 reached(case.judge(self.spactl_failed(case)))["tests/Spactl.Tests"])

    def test_the_architecture_tests_are_reached_by_any_project(self):
        case = Case(self)
        case.change("tools/bench/floor/Program.cs", "tools/bench/report.py")
        result = case.judge([failed_line(case.repo, "tests/Gateway.ArchitectureTests", "Gateway.ArchitectureTests"),
                             TESTS_FAILED])
        self.assertEqual({"tests/Gateway.ArchitectureTests": ["tools/bench/floor/Program.cs (whole solution)"]},
                         reached(result))

    def test_a_project_named_by_its_folder_not_its_assembly(self):
        case = Case(self)
        case.change("tests/e2e/Flow.cs")
        result = case.judge([failed_line(case.repo, "tests/e2e", "Gateway.E2ETests"), TESTS_FAILED])
        self.assertEqual({"tests/e2e": ["tests/e2e/Flow.cs (project)"]}, reached(result))

    def test_several_failed_projects_are_each_judged_once(self):
        case = Case(self)
        case.change("src/Gateway/Program.cs")
        result = case.judge([failed_line(case.repo, "tests/Spactl.Tests", "Spactl.Tests"),
                             failed_line(case.repo, "tests/e2e", "Gateway.E2ETests", 2),
                             failed_line(case.repo, "tests/Spactl.Tests", "Spactl.Tests"), TESTS_FAILED])
        self.assertEqual({"tests/Spactl.Tests": [], "tests/e2e": ["src/Gateway/Program.cs (project)"]},
                         reached(result))

    def test_a_started_project_with_no_passed_line_is_named_failed(self):
        # #375 review: Spactl.Tests failed, unreached; e2e started and never said "passed" (a crash, a
        # cut-off run, a result word not seen before). Naming only Spactl.Tests would re-run it alone,
        # skip e2e under --skip-tests and keep the merge with e2e unverified.
        case = Case(self)
        case.change("tools/bench/report.py")
        started = lambda project, dll: f"Running tests from {case.repo}/{project}/bin/Debug/net10.0/{dll}.dll (net10.0|arm64)"
        result = case.judge([
            started("tests/Spactl.Tests", "Spactl.Tests"),
            started("tests/e2e", "Gateway.E2ETests"),
            started("tests/Platform.Registry.Tests", "Platform.Registry.Tests"),
            passed_line(case.repo, "tests/Platform.Registry.Tests", "Platform.Registry.Tests"),
            failed_line(case.repo, "tests/Spactl.Tests", "Spactl.Tests"),
            f"  {case.repo}/tests/e2e/bin/Debug/net10.0/Gateway.E2ETests.dll (net10.0|arm64) crashed (1s 001ms)",
            TESTS_FAILED])
        self.assertEqual(["tests/Spactl.Tests", "tests/e2e"], [p["project"] for p in result["projects"]])

    def test_an_unresolvable_include_reaches_everything(self):
        case = Case(self)
        case.change("tools/bench/report.py")
        result = case.judge([failed_line(case.repo, "tests/Odd.Tests", "Odd.Tests"), TESTS_FAILED])
        self.assertEqual({"tests/Odd.Tests": ["tools/bench/report.py (item)"]}, reached(result))

    def test_a_failed_project_with_no_project_file_reaches_everything(self):
        case = Case(self)
        case.change("tools/bench/report.py")
        result = case.judge([failed_line(case.repo, "tests/Gone.Tests", "Gone.Tests"), TESTS_FAILED])
        self.assertEqual(["tests/Gone.Tests (no project file there, so its reach is unknown)"],
                         reached(result)["tests/Gone.Tests"])

    def test_a_failed_project_outside_the_checkout_reaches_everything(self):
        case = Case(self)
        case.change("tools/bench/report.py")
        result = case.judge(["  /elsewhere/tests/X.Tests/bin/Debug/net10.0/X.Tests.dll (net10.0|arm64) failed with 1 error(s)",
                             TESTS_FAILED])
        self.assertEqual(["/elsewhere/tests/X.Tests (outside this checkout, so its reach is unknown)"],
                         reached(result)["/elsewhere/tests/X.Tests"])

    def test_a_failure_that_is_not_the_tests_names_no_project(self):
        for verdict in ("check: fail: the smoke test failed.", "check: fail: the solution does not build.",
                        "check: fail: the steering check failed.", "check: fail: terraform test failed in infra/roots/a."):
            with self.subTest(verdict=verdict):
                case = Case(self)
                case.change("tools/bench/report.py")
                result = case.judge([failed_line(case.repo, "tests/Spactl.Tests", "Spactl.Tests"), verdict])
                self.assertEqual("other", result["failedAt"])
                self.assertEqual(verdict, result["verdict"])
                self.assertEqual([], result["projects"])

    def test_a_test_failure_with_no_project_line_names_none(self):
        case = Case(self)
        case.change("tools/bench/report.py")
        result = case.judge(["Test run aborted", TESTS_FAILED])
        self.assertEqual(("tests", []), (result["failedAt"], result["projects"]))

    def test_a_log_it_cannot_read_fails_with_no_judgement(self):
        case = Case(self)
        out = case.run(None)
        self.assertEqual(2, out.returncode)
        self.assertEqual("", out.stdout)
        self.assertRegex(out.stderr.strip(), r"^test-reach: fail: cannot read the log ")

    def test_an_empty_log_fails_with_no_judgement(self):
        case = Case(self)
        out = case.run(["", "  "])
        self.assertEqual((2, ""), (out.returncode, out.stdout))
        self.assertIn("holds no verdict", out.stderr)

    def test_a_change_git_cannot_list_fails_with_no_judgement(self):
        case = Case(self)
        out = case.run(self.spactl_failed(case), head="no-such-ref")
        self.assertEqual((2, ""), (out.returncode, out.stdout))
        self.assertRegex(out.stderr.strip(), r"^test-reach: fail: git diff ")


class TestRuntimeRoots(unittest.TestCase):
    """A guard that reads the thing it guards (CODING_STANDARDS.md): this repository's test sources."""

    def test_every_top_level_entry_a_test_names_is_covered(self):
        spec = importlib.util.spec_from_file_location("test_reach", SCRIPT)
        tool = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(tool)
        listed = subprocess.run(["git", "-C", str(ROOT), "ls-files"], capture_output=True, text=True, check=True)
        files = listed.stdout.splitlines()
        top = {f.split("/")[0] for f in files}
        sources = [f for f in files if f.endswith(".cs") and f.split("/")[0] in ("tests", "fixtures")]
        self.assertTrue(sources, "no test source was read, so the guard judged nothing")
        named = set()
        for source in sources:
            text = (ROOT / source).read_text(encoding="utf-8", errors="replace")
            named |= {m for m in re.findall(r'"([^"/\\\s]+)"', text) if m in top}
        self.assertIn("contracts", named, "the literal search found no read of contracts/, so it is not working")
        missing = sorted(named - set(tool.RUNTIME_ROOTS))
        self.assertEqual([], missing, "test sources name these top-level entries, which RUNTIME_ROOTS does not cover")


if __name__ == "__main__":
    unittest.main()
