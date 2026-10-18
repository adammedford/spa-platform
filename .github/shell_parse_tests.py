#!/usr/bin/env python3
"""Test companion for .github/shell-parse.py: a tracked shell script that does not parse fails CI.

Owner: integrator. Built in #68, whose third box is this companion's first case: a deliberately
broken copy of a tracked shell script fails the parse step. The copies are read from this
repository's own scripts, so the case breaks a real script rather than a toy one.

Each case builds a throwaway repository, tracks the files it names, and runs the real check from
there with `python3 -I`, as CI's step runs it from the checkout's top level.

Run: python3 -I .github/shell_parse_tests.py
"""

from __future__ import annotations

import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
CHECK = ROOT / ".github" / "shell-parse.py"

# Two of this repository's scripts: one named .sh, and the hook, which has no extension and is
# recognised by its shebang alone.
NAMED = ROOT / ".github" / "touches.sh"
UNNAMED = ROOT / ".githooks" / "pre-push"


def broken(text: str, line: str) -> str:
    """The script with one whole line removed; the case names a line whose loss leaves an open
    construct, so bash -n must refuse it."""
    lines = text.splitlines(keepends=True)
    matches = [index for index, each in enumerate(lines) if each.strip() == line]
    assert len(matches) == 1, f"expected exactly one line {line!r} to remove, found {len(matches)}"
    del lines[matches[0]]
    return "".join(lines)


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
                                     capture_output=True, text=True, timeout=60))


class ShellParseTests(unittest.TestCase):
    def assertRefuses(self, result: Result, *names: str):
        self.assertEqual(1, result.status, result.stdout + result.stderr)
        for name in names:
            self.assertIn(f"shell-parse: {name} does not parse", result.stderr)

    def test_a_broken_copy_of_a_tracked_script_fails(self):
        repository = Repository(self)
        repository.write("tools/touches.sh", broken(NAMED.read_text(), "esac"))
        self.assertRefuses(repository.check(), "tools/touches.sh")

    def test_a_broken_script_without_an_extension_fails_by_its_shebang(self):
        repository = Repository(self)
        # The hook holds no block to unbalance, so the copy gains an `if` it never closes.
        repository.write(".githooks/pre-push", UNNAMED.read_text() + "if true; then\n")
        self.assertRefuses(repository.check(), ".githooks/pre-push")

    def test_every_broken_script_is_named_not_only_the_first(self):
        repository = Repository(self)
        first = repository.write("a.sh", "if true; then\n")
        second = repository.write("b.sh", "case x in\n")
        result = repository.check()
        self.assertRefuses(result, first, second)
        self.assertIn("2 of 2 tracked shell scripts do not parse", result.stderr)

    def test_the_real_scripts_parse(self):
        repository = Repository(self)
        repository.write("tools/touches.sh", NAMED.read_text())
        repository.write(".githooks/pre-push", UNNAMED.read_text())
        result = repository.check()
        self.assertEqual(0, result.status, result.stderr)
        self.assertRegex(result.stdout, r"^shell-parse: 2 tracked shell scripts parse under /bin/bash \S+: "
                                        r"\.githooks/pre-push, tools/touches\.sh\.$")

    def test_what_is_not_a_tracked_shell_script_is_not_parsed(self):
        repository = Repository(self)
        repository.write("untracked.sh", "if true; then\n", track=False)
        repository.write("tools/check.py", "#!/usr/bin/env python3\nif true; then\n")
        # A first line starting with "#" is not a shebang unless it starts with "#!".
        repository.write("notes.md", "# notes\nif true; then\n")
        repository.write("ok.sh", "echo fine\n")
        result = repository.check()
        self.assertEqual(0, result.status, result.stderr)
        self.assertRegex(result.stdout, r"^shell-parse: 1 tracked shell script parses under /bin/bash \S+: ok\.sh\.$")

    def test_shebangs_that_name_a_shell(self):
        for shebang in ("#!/bin/bash", "#!/bin/sh", "#!/usr/bin/env bash", "#! /usr/bin/env  sh"):
            with self.subTest(shebang=shebang):
                repository = Repository(self)
                repository.write("bin/tool", f"{shebang}\nwhile true; do\n")
                self.assertRefuses(repository.check(), "bin/tool")

    def test_a_tracked_link_is_not_followed(self):
        # git tracks a link as its target's path, not the file it names, which is tracked or not on
        # its own account.
        repository = Repository(self)
        (repository.dir / "elsewhere.sh").write_text("if true; then\n")
        (repository.dir / "link.sh").symlink_to(repository.dir / "elsewhere.sh")
        repository.git("add", "link.sh")
        repository.write("ok.sh", "echo fine\n")
        result = repository.check()
        self.assertEqual(0, result.status, result.stderr)
        self.assertRegex(result.stdout, r": ok\.sh\.$")

    def test_a_repository_with_no_shell_script_fails(self):
        # A check that finds nothing to check would pass having checked nothing.
        repository = Repository(self)
        repository.write("README.md", "readme\n")
        result = repository.check()
        self.assertEqual(1, result.status, result.stdout)
        self.assertIn("shell-parse: found no tracked shell script", result.stderr)

    def test_this_repository_has_the_scripts_the_check_must_find(self):
        # A guard that reads the thing it guards: the check's selection, run on this repository,
        # includes the script with no extension and the local check, not only the .sh files.
        result = subprocess.run([sys.executable, "-I", str(CHECK)], cwd=ROOT,
                                capture_output=True, text=True, timeout=60)
        self.assertEqual(0, result.returncode, result.stderr)
        listed = result.stdout.rstrip().rstrip(".").split(": ", 2)[2].split(", ")
        for name in (".githooks/pre-push", "tools/check.sh", "tools/land.sh", ".github/touches.sh"):
            self.assertIn(name, listed)
        self.assertNotIn("tools/check_tests.py", listed)


if __name__ == "__main__":
    unittest.main()
