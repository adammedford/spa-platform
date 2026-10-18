#!/usr/bin/env python3
"""Test companion for tools/preflight.sh, for its Terraform rows. Owner: integrator.

Built in #69, which gave preflight a row for the Terraform floor: the version .terraform-version
names, which CI installs and tools/check.sh runs when this machine has it. The row names the floor,
says whether this machine has it, and when it does not, prints the command that installs it where
the check looks. The shape is tools/check_tests.py's, and the tool choice is D-3 in
docs/dependencies.md.

**What runs is the script itself**, through a link in a throwaway directory laid out like the
repository, `/bin/bash <dir>/tools/preflight.sh`: preflight finds its neighbours from its own
path, so the case's link reaches the case's .terraform-version and, through a second link, the real
tools/terraform-floor.sh.

**A constructed environment, inheriting nothing.** PATH is the case's bin directory alone, holding
the few real tools preflight reads its answers with and a Terraform double, so the rows for git,
.NET, the AWS CLI and the rest report missing and reach no network. Only the Terraform rows are
asserted. HOME is the case's own, which is where the floor is installed.

Run: python3 -I tools/preflight_tests.py
"""

from __future__ import annotations

import os
import pathlib
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "preflight.sh"
FLOOR_HELPER = ROOT / "tools" / "terraform-floor.sh"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"

# The floor as the repository's one file names it, read rather than typed so raising it raises the
# cases with it.
FLOOR = (ROOT / ".terraform-version").read_text().strip()


def later_than(version: str) -> str:
    """A release five minor versions after `version`, or 99.0.0 when `version` is not one exact
    MAJOR.MINOR.PATCH, which tools/check_tests.py's FloorFileTests reports."""
    match = re.fullmatch(r"(\d+)\.(\d+)\.\d+", version)
    return f"{match[1]}.{int(match[2]) + 5}.5" if match else "99.0.0"


# LATER stands for what this Mac runs while the floor is not installed, as 1.16.5 was to the 1.11.0
# floor on 2026-10-17. Worked out from the floor rather than typed, so raising the floor to 1.16.5
# or past it does not make the terraform on PATH the floor, or older than it (#69: a one-line
# change). OLDER is older than any floor the repository can raise to.
LATER = later_than(FLOOR)
OLDER = "1.10.5"

# The real tools preflight reads its answers with. Everything else it looks for is absent.
TOOLS = ("uname", "sed", "head", "tail", "tr", "cut", "awk", "grep", "sort", "dirname")

# A Terraform double answers `version` as Terraform does; nothing else is asked of it here.
DOUBLE = """#!/bin/bash
if [ "$*" = version ]; then printf 'Terraform v%s\\non darwin_arm64\\n' {reports}; fi
exit 0
"""


def row(status: str, label: str, detail: str) -> str:
    """A row as preflight prints it: printf '  %-8s %-24s %s\\n'."""
    return f"  {status:<8} {label:<24} {detail}"


class Case:
    def __init__(self, test: unittest.TestCase, *, path_terraform: str | None = None,
                 floor_terraform: str | None = None, floor_file: str = FLOOR + "\n"):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.bin = self.dir / "bin"
        self.home = self.dir / "home"
        self.repo = self.dir / "repo"
        for directory in (self.bin, self.home, self.repo / "tools"):
            directory.mkdir(parents=True)
        for tool in TOOLS:
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            self.write_executable(self.bin / tool,
                                  f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(found))} "$@"\n')
        (self.repo / ".terraform-version").write_text(floor_file)
        (self.repo / "tools" / "preflight.sh").symlink_to(SCRIPT)
        (self.repo / "tools" / "terraform-floor.sh").symlink_to(FLOOR_HELPER)
        self.path_terraform = self.bin / "terraform"
        if path_terraform is not None:
            self.write_executable(self.path_terraform, DOUBLE.format(reports=shlex.quote(path_terraform)))
        # Where the install line preflight prints puts the floor, typed from docs/dependencies.md
        # rather than read from the helper both scripts share.
        self.floor_dir = self.home / ".local" / "share" / "terraform" / FLOOR
        if floor_terraform is not None:
            self.floor_dir.mkdir(parents=True)
            self.write_executable(self.floor_dir / "terraform",
                                  DOUBLE.format(reports=shlex.quote(floor_terraform)))

    @staticmethod
    def write_executable(path: pathlib.Path, text: str):
        path.write_text(text)
        path.chmod(0o755)

    def run(self) -> list[str]:
        completed = subprocess.run(
            [BASH, str(self.repo / "tools" / "preflight.sh")], cwd=self.dir,
            env={"PATH": str(self.bin), "HOME": str(self.home), "LC_ALL": "C"},
            stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=60)
        return completed.stdout.splitlines()

    def install_line(self) -> str:
        """The command that installs the floor where tools/check.sh looks for it."""
        return (f"brew install hashicorp/tap/hc-install && mkdir -p {self.floor_dir} && "
                f"hc-install install -version {FLOOR} -path {self.floor_dir} terraform")


def setUpModule():
    full = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout
    print(f"preflight_tests: tools/preflight.sh runs under {BASH} {full}", file=sys.stderr)


class FloorRowTests(unittest.TestCase):
    """#69's second box: preflight names the floor version and says whether this machine has it."""

    def test_the_floor_installed_in_its_directory(self):
        case = Case(self, path_terraform=LATER, floor_terraform=FLOOR)
        lines = case.run()
        self.assertIn(row("ok", f"Terraform floor {FLOOR}",
                          f"{case.floor_dir / 'terraform'}, which CI runs and tools/check.sh uses"), lines)
        self.assertIn(row("ok", "Terraform", f"{LATER} on PATH"), lines)

    def test_the_floor_not_installed_says_what_the_check_uses_and_how_to_install_it(self):
        case = Case(self, path_terraform=LATER)
        lines = case.run()
        self.assertIn(row("MISSING", f"Terraform floor {FLOOR}",
                          f"not installed, so tools/check.sh runs {LATER} instead. {case.install_line()}"),
                      lines)

    def test_a_terraform_on_path_that_reports_the_floor_is_the_floor(self):
        # A version manager reading .terraform-version, or a machine that simply has the floor.
        case = Case(self, path_terraform=FLOOR)
        lines = case.run()
        self.assertIn(row("ok", f"Terraform floor {FLOOR}",
                          f"{case.path_terraform}, which CI runs and tools/check.sh uses"), lines)

    def test_a_floor_directory_holding_another_version_is_not_the_floor(self):
        case = Case(self, path_terraform=LATER, floor_terraform=OLDER)
        lines = case.run()
        self.assertIn(row("MISSING", f"Terraform floor {FLOOR}",
                          f"not installed, so tools/check.sh runs {LATER} instead. {case.install_line()}"),
                      lines)

    def test_no_terraform_at_all(self):
        case = Case(self)
        lines = case.run()
        self.assertIn(row("MISSING", f"Terraform floor {FLOOR}",
                          f"not installed, so tools/check.sh runs no Terraform. {case.install_line()}"),
                      lines)
        self.assertIn(row("MISSING", f"Terraform >= {FLOOR}", "brew install hashicorp/tap/terraform"), lines)

    def test_a_terraform_on_path_older_than_the_floor(self):
        # Every root requires at least the floor, so an apply with it would refuse to start.
        case = Case(self, path_terraform=OLDER, floor_terraform=FLOOR)
        lines = case.run()
        self.assertIn(row("MISSING", f"Terraform >= {FLOOR}",
                          f"found: {OLDER}. brew install hashicorp/tap/terraform"), lines)

    def test_a_file_that_names_no_exact_version(self):
        case = Case(self, path_terraform=LATER, floor_file="latest\n")
        lines = case.run()
        self.assertIn(row("MISSING", "Terraform floor",
                          f"{case.repo / '.terraform-version'} does not name one exact version, such as 1.11.0"),
                      lines)


class InterpreterTests(unittest.TestCase):
    def test_bash_is_the_version_this_run_requires(self):
        # Set by CI's macOS job, as in tools/check_tests.py.
        required = os.environ.get(REQUIRE_BASH, "")
        version = subprocess.run(
            [BASH, "-c", 'printf "%s.%s" "${BASH_VERSINFO[0]}" "${BASH_VERSINFO[1]}"'],
            env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True, check=True).stdout
        self.assertRegex(version, r"^\d+\.\d+$")
        if required:
            self.assertEqual(required, version, f"{REQUIRE_BASH}={required}, but {BASH} is {version}")


if __name__ == "__main__":
    unittest.main()
