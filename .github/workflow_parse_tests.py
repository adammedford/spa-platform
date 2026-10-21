#!/usr/bin/env python3
"""Test companion for .github/workflow-parse.py: a tracked workflow script that does not parse
fails CI, and the shape the Workflow tool accepts passes.

Owner: integrator. Built in #135, whose second box is this check. The passing copy is this
repository's own overnight script, so the case parses a real script rather than a toy one; the
failing copies break it, because a check that has only ever seen clean scripts cannot show it
reads any.

Each case builds a throwaway repository, tracks the files it names, and runs the real check from
there with `python3 -I`, as CI's step runs it from the checkout's top level.

Run: python3 -I .github/workflow_parse_tests.py
"""

from __future__ import annotations

import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
CHECK = ROOT / ".github" / "workflow-parse.py"
OVERNIGHT = ROOT / ".claude" / "workflows" / "overnight.js"

# The two features of a workflow script that a plain parse refuses: the export on line 1, and a
# return at the top level. Both must pass here.
SHAPE = "export const meta = { name: 'shape', description: 'the shape the tool accepts' }\n" \
        "const answer = await Promise.resolve(42)\n" \
        "if (answer !== 42) return { failed: true }\n" \
        "return { answer }\n"


class Result:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout
        self.stderr = completed.stderr


class Repository:
    def __init__(self, test: unittest.TestCase):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.git("init", "-q", "-b", "main")

    def git(self, *args: str):
        subprocess.run(["git", *args], cwd=self.dir, check=True, capture_output=True,
                       env={"PATH": "/usr/bin:/bin", "HOME": str(self.dir), "GIT_CONFIG_NOSYSTEM": "1",
                            "GIT_CONFIG_GLOBAL": "/dev/null", "LC_ALL": "C"})

    def write(self, name: str, text: str, *, track: bool = True) -> str:
        path = self.dir / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        if track:
            self.git("add", name)
        return name

    def check(self) -> Result:
        return Result(subprocess.run([sys.executable, "-I", str(CHECK)], cwd=self.dir,
                                     capture_output=True, text=True, timeout=120))


class WorkflowParseTests(unittest.TestCase):
    def assertRefuses(self, result: Result, *names: str):
        self.assertEqual(1, result.status, result.stdout + result.stderr)
        for name in names:
            self.assertIn(f"workflow-parse: {name} does not parse", result.stderr)

    def assertParses(self, result: Result, count: int, *names: str):
        self.assertEqual(0, result.status, result.stderr)
        noun = "script parses" if count == 1 else "scripts parse"
        self.assertRegex(result.stdout, rf"^workflow-parse: {count} tracked workflow {noun} under node v\S+: "
                                        + ", ".join(names).replace(".", r"\.") + r"\.$")

    def test_the_real_overnight_script_parses(self):
        repository = Repository(self)
        repository.write(".claude/workflows/overnight.js", OVERNIGHT.read_text())
        self.assertParses(repository.check(), 1, ".claude/workflows/overnight.js")

    def test_the_export_line_and_a_top_level_return_are_the_accepted_shape(self):
        repository = Repository(self)
        repository.write(".claude/workflows/shape.js", SHAPE)
        self.assertParses(repository.check(), 1, ".claude/workflows/shape.js")

    def test_a_broken_copy_of_the_real_script_fails_naming_it(self):
        repository = Repository(self)
        source = OVERNIGHT.read_text()
        # Lose the last closing brace in the file, which leaves a block open.
        index = source.rstrip().rfind("}")
        self.assertGreater(index, 0)
        repository.write(".claude/workflows/overnight.js", source[:index] + source[index + 1:])
        result = repository.check()
        self.assertRefuses(result, ".claude/workflows/overnight.js")
        self.assertIn("SyntaxError", result.stderr)

    def test_every_broken_script_is_named_not_only_the_first(self):
        repository = Repository(self)
        first = repository.write(".claude/workflows/a.js", "export const meta = {}\nif (true) {\n")
        second = repository.write(".claude/workflows/b.js", "const x = [1, 2\n")
        result = repository.check()
        self.assertRefuses(result, first, second)
        self.assertIn("2 of 2 tracked workflow scripts do not parse", result.stderr)

    def test_the_export_is_rewritten_once_and_a_later_export_still_fails(self):
        # Only line 1's `export const meta` is the tool's; a second export is a real error.
        repository = Repository(self)
        repository.write(".claude/workflows/twice.js", "export const meta = {}\nexport const other = 1\n")
        self.assertRefuses(repository.check(), ".claude/workflows/twice.js")

    def test_what_is_not_a_tracked_workflow_script_is_not_parsed(self):
        repository = Repository(self)
        repository.write(".claude/workflows/untracked.js", "if (true) {\n", track=False)
        repository.write(".claude/workflows/harness.mjs", "if (true) {\n")
        repository.write(".claude/workflows/fixtures/nested.js", "if (true) {\n")
        repository.write("tools/elsewhere.js", "if (true) {\n")
        repository.write(".claude/workflows/ok.js", SHAPE)
        self.assertParses(repository.check(), 1, ".claude/workflows/ok.js")

    def test_a_repository_with_no_workflow_script_fails(self):
        # A check that finds nothing to check would pass having checked nothing.
        repository = Repository(self)
        repository.write("README.md", "readme\n")
        result = repository.check()
        self.assertEqual(1, result.status, result.stdout)
        self.assertIn("workflow-parse: found no tracked workflow script", result.stderr)

    def test_this_repository_tracks_the_overnight_script(self):
        # A guard that reads the thing it guards: the check, run on this repository, finds the
        # overnight script and no companion or fixture beside it.
        result = subprocess.run([sys.executable, "-I", str(CHECK)], cwd=ROOT,
                                capture_output=True, text=True, timeout=120)
        self.assertEqual(0, result.returncode, result.stderr)
        listed = result.stdout.rstrip().rstrip(".").split(": ", 2)[2].split(", ")
        self.assertIn(".claude/workflows/overnight.js", listed)
        for name in listed:
            self.assertTrue(name.endswith(".js"), name)
            self.assertNotIn("/fixtures/", name)


if __name__ == "__main__":
    unittest.main()
