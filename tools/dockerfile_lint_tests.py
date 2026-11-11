#!/usr/bin/env python3
"""Test companion for tools/dockerfile-lint.sh, hadolint over every tracked Dockerfile (#785).

Owner: integrator. The shape is D-3's in docs/dependencies.md: the real script, run as a
subprocess in a throwaway repository, with a docker double on PATH, so no case pulls hadolint or
needs a daemon. The double records each call and fails the one a case names. tools/check.sh --full
runs the real script, on a change to a Dockerfile, to .hadolint.yaml or to the script, and that run
is what shows the repository's Dockerfiles pass.

The pin is read from the script and found in docs/dependencies.md, and the configuration is read as
it stands: every rule it turns off carries its reason on its own line (#785's fifth box).

Run: python3 -I tools/dockerfile_lint_tests.py
"""

from __future__ import annotations

import pathlib
import re
import shutil
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "dockerfile-lint.sh"
CONFIG = ROOT / ".hadolint.yaml"
DEPENDENCIES = ROOT / "docs" / "dependencies.md"
BASH = "/bin/bash"

DOCKER = r"""#!/bin/bash
n=0
while [ -e "$LOG/call-$n" ]; do n=$((n + 1)); done
printf '%s\0' "$@" > "$LOG/call-$n"
if [ -n "${FAIL_ON:-}" ]; then
  case " $* " in *" $FAIL_ON "*) echo "docker double failed: $*" >&2; exit 1 ;; esac
fi
[ "$1" = run ] && echo "docker double ran hadolint"
exit 0
"""

DOCKERFILES = {"Dockerfile": "FROM scratch\n", "src/App/Dockerfile": "FROM scratch\n",
               "tools/deep/floor/Dockerfile": "FROM scratch\n"}


def pinned() -> str:
    match = re.search(r'^HADOLINT_IMAGE="([^"]+)"$', SCRIPT.read_text(), re.M)
    assert match, "tools/dockerfile-lint.sh has no HADOLINT_IMAGE= line"
    return match[1]


class Case:
    def __init__(self, test: unittest.TestCase, files: dict[str, str] | None = None, fail_on: str = "",
                 docker: bool = True, config: bool = True, git_fails: bool = False):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.work, self.bin, self.log, self.home = (self.dir / "work", self.dir / "bin", self.dir / "log",
                                                    self.dir / "home")
        for directory in (self.work / "tools", self.bin, self.log, self.home):
            directory.mkdir(parents=True)
        shutil.copy(SCRIPT, self.work / "tools" / "dockerfile-lint.sh")
        real_git = shutil.which("git")
        test.assertIsNotNone(real_git, "git is required to run this companion")
        self.env = {"PATH": str(self.bin), "HOME": str(self.home), "LOG": str(self.log), "FAIL_ON": fail_on,
                    "LC_ALL": "C", "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null"}
        self.git = lambda *args: subprocess.run([real_git, *args], cwd=self.work, env=self.env, check=True,
                                                capture_output=True)
        self.git("init", "-q")
        for name, text in (DOCKERFILES if files is None else files).items():
            path = self.work / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text)
        if config:
            (self.work / ".hadolint.yaml").write_text(CONFIG.read_text())
        self.git("add", ".")
        # Untracked, so the census must leave it out.
        (self.work / "scratch").mkdir()
        (self.work / "scratch" / "Dockerfile").write_text("FROM scratch\n")
        if git_fails:
            (self.bin / "git").write_text("#!/bin/bash\n"
                                          'if [ "$1" = ls-files ]; then echo "git double: ls-files failed" >&2; exit 128; fi\n'
                                          f'exec {real_git} "$@"\n')
            (self.bin / "git").chmod(0o755)
        else:
            (self.bin / "git").symlink_to(real_git)
        (self.bin / "bash").symlink_to(BASH)
        if docker:
            (self.bin / "docker").write_text(DOCKER)
            (self.bin / "docker").chmod(0o755)

    def run(self) -> subprocess.CompletedProcess:
        return subprocess.run([BASH, str(self.work / "tools" / "dockerfile-lint.sh")], cwd=self.work, env=self.env,
                              capture_output=True, text=True, timeout=60)

    def calls(self) -> list[list[str]]:
        names = sorted(self.log.glob("call-*"), key=lambda path: int(path.name.split("-")[1]))
        return [path.read_text().split("\0")[:-1] for path in names]


class LintTests(unittest.TestCase):
    def assertLast(self, completed: subprocess.CompletedProcess, status: int, line: str):
        self.assertEqual(status, completed.returncode, completed.stdout + completed.stderr)
        lines = (completed.stdout if status == 0 else completed.stderr).splitlines()
        self.assertTrue(lines, completed.stdout + completed.stderr)
        self.assertEqual(line, lines[-1])

    def test_it_runs_the_pinned_hadolint_over_every_tracked_dockerfile_with_the_config(self):
        case = Case(self)
        completed = case.run()
        self.assertLast(completed, 0, "dockerfile-lint: pass: 3 Dockerfiles: Dockerfile, src/App/Dockerfile, "
                                      "tools/deep/floor/Dockerfile.")
        calls = case.calls()
        self.assertEqual(["info"], calls[0])
        self.assertEqual(2, len(calls))
        run = calls[1]
        self.assertEqual("run", run[0])
        self.assertIn(f"{case.work}:/repo:ro", run)
        image = run.index(pinned())
        self.assertEqual(["hadolint", "--no-color", "--config", ".hadolint.yaml", "Dockerfile", "src/App/Dockerfile",
                          "tools/deep/floor/Dockerfile"], run[image + 1:])
        # hadolint's own lines are shown.
        self.assertIn("docker double ran hadolint", completed.stdout)

    def test_a_finding_fails_it(self):
        case = Case(self, fail_on="hadolint")
        self.assertLast(case.run(), 1, "dockerfile-lint: fail: hadolint failed a Dockerfile, or did not run; "
                                       "its lines are above.")

    def test_no_tracked_dockerfile_fails_rather_than_passes(self):
        case = Case(self, files={"README.md": "x\n"})
        self.assertLast(case.run(), 1, "dockerfile-lint: fail: git tracks no Dockerfile, so nothing was linted.")
        self.assertEqual([], [call for call in case.calls() if call[0] == "run"])

    def test_a_failed_listing_fails_rather_than_reads_as_empty(self):
        case = Case(self, git_fails=True)
        self.assertLast(case.run(), 1, "dockerfile-lint: fail: the Dockerfiles were not listed: git ls-files failed.")

    def test_without_the_config_it_fails(self):
        case = Case(self, config=False)
        self.assertLast(case.run(), 1, "dockerfile-lint: fail: .hadolint.yaml is not there, so the rules it turns "
                                       "off and its threshold would be hadolint's defaults.")

    def test_without_docker_or_its_daemon_it_fails(self):
        case = Case(self, docker=False)
        completed = case.run()
        self.assertEqual(1, completed.returncode)
        self.assertTrue(completed.stderr.splitlines()[-1].startswith("dockerfile-lint: fail: docker is not on PATH"))
        case = Case(self, fail_on="info")
        completed = case.run()
        self.assertEqual(1, completed.returncode)
        self.assertIn("does not answer", completed.stderr.splitlines()[-1])


class RepositoryTests(unittest.TestCase):
    def test_hadolint_is_pinned_by_version_and_digest_and_listed(self):
        self.assertRegex(pinned(), r"^hadolint/hadolint:v\d+\.\d+\.\d+@sha256:[0-9a-f]{64}$")
        self.assertIn(f"`{pinned()}`", DEPENDENCIES.read_text())

    def test_the_census_finds_the_three_dockerfiles(self):
        listed = subprocess.run(["git", "-C", str(ROOT), "ls-files", "--", "Dockerfile", "*/Dockerfile"],
                                capture_output=True, text=True, check=True).stdout.split()
        for path in ("src/Gateway/Dockerfile", "fixtures/MockApi/Dockerfile", "tools/bench/floor/Dockerfile"):
            self.assertIn(path, listed)

    def test_every_rule_the_config_turns_off_carries_its_reason(self):
        lines = CONFIG.read_text().splitlines()
        self.assertIn("failure-threshold: info", lines)
        start = lines.index("ignored:")
        items = []
        for line in lines[start + 1:]:
            if not line.startswith("  "):
                break
            items.append(line)
        rules = [line for line in items if line.lstrip().startswith("- ")]
        self.assertNotEqual([], rules)
        for line in rules:
            with self.subTest(rule=line):
                self.assertRegex(line, r"^  - DL\d{4} +# \S.{20,}$", "a rule turned off with no reason beside it")


if __name__ == "__main__":
    unittest.main()
