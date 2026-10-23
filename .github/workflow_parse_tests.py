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

# The Workflow tool accepts the metadata export, top-level await and top-level return.
# The check must accept that shape without executing it.
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

    def test_module_syntax_cannot_hide_another_syntax_error(self):
        # On Node 22, an ambiguous .js copy can return success on module syntax before
        # reporting an unclosed block. Exercise the actual CLI, not a mocked node result.
        for statement in ("export const other = 1", "import { other } from './missing.js'"):
            with self.subTest(statement=statement):
                repository = Repository(self)
                name = repository.write(".claude/workflows/broken.js",
                                        "export const meta = {}\n" + statement + "\nif (true) {\n")
                result = repository.check()
                self.assertRefuses(result, name)
                self.assertIn("SyntaxError", result.stderr)

    def test_parsing_does_not_execute_the_workflow(self):
        repository = Repository(self)
        name = repository.write(".claude/workflows/never-run.js",
                                "export const meta = {}\nthrow new Error('workflow body executed')\n")
        self.assertParses(repository.check(), 1, name)

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

    # An agent's result read as an empty list (#277). `(x && x.field) || []` turns an agent that
    # returned nothing into a stage that found nothing, so the check fails the line unless it carries
    # the exemption marker with a reason.
    def assertSwallows(self, result: Result, name: str, line: int):
        self.assertEqual(1, result.status, result.stdout + result.stderr)
        self.assertIn(f"workflow-parse: {name}:{line} reads an agent result as an empty list", result.stderr)

    def test_an_agent_result_read_through_or_empty_fails_naming_file_and_line(self):
        for site in ("(r && r.claims) || []",
                     "(first && !first.ok && first.conflictFiles) || []",
                     "(clustered && Array.isArray(clustered.findings) && clustered.findings) || []"):
            with self.subTest(site=site):
                repository = Repository(self)
                name = repository.write(".claude/workflows/swallow.js",
                                        "export const meta = {}\nconst r = await agent('x')\n"
                                        f"for (const c of {site}) log(c)\n")
                result = repository.check()
                self.assertSwallows(result, name, 3)
                self.assertIn("1 of 1 tracked workflow scripts", result.stderr)

    def test_a_guard_on_another_name_and_a_plain_default_are_not_sites(self):
        # `(ok && got.names)` guards on a judgement already made, and `x.list || []` reads no
        # possibly-missing agent result through a guard; neither is the pattern.
        repository = Repository(self)
        name = repository.write(".claude/workflows/fine.js",
                                "export const meta = {}\nconst got = await agent('x')\nconst ok = !!got\n"
                                "const a = (ok && got.names) || []\nconst b = (got.list || [])\n")
        self.assertParses(repository.check(), 1, name)

    def test_an_exempted_site_passes(self):
        repository = Repository(self)
        name = repository.write(".claude/workflows/exempt.js",
                                "export const meta = {}\nconst r = await agent('x')\n"
                                "for (const c of (r && r.rows) || []) log(c) // empty-ok: a null r is named in failed above\n")
        self.assertParses(repository.check(), 1, name)

    def test_an_exemption_with_no_reason_fails(self):
        for marker in ("// empty-ok:", "// empty-ok:   ", "// empty-ok"):
            with self.subTest(marker=marker):
                repository = Repository(self)
                name = repository.write(".claude/workflows/bare.js",
                                        "export const meta = {}\nconst r = await agent('x')\n"
                                        f"for (const c of (r && r.rows) || []) log(c) {marker}\n")
                result = repository.check()
                self.assertEqual(1, result.status, result.stdout + result.stderr)
                self.assertIn(f"workflow-parse: {name}:3 has an empty-ok exemption with no reason", result.stderr)

    def test_an_exemption_on_a_line_with_no_site_fails(self):
        # A marker left behind after its site was fixed would exempt whatever moves onto the line.
        repository = Repository(self)
        name = repository.write(".claude/workflows/stale.js",
                                "export const meta = {}\nconst r = await agent('x')\n"
                                "for (const c of r.rows) log(c) // empty-ok: a null r is named in failed above\n")
        result = repository.check()
        self.assertEqual(1, result.status, result.stdout + result.stderr)
        self.assertIn(f"workflow-parse: {name}:3 has an empty-ok exemption but no site to exempt", result.stderr)

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
