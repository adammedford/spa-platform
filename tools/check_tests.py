#!/usr/bin/env python3
"""Test companion for tools/check.sh, the local check, and for .githooks/pre-push, which runs it.

Owner: integrator. Built in #68; the tool choice is D-3 in docs/dependencies.md, and the shape is
tools/land_tests.py's.

**What runs is the script itself.** Each case runs the tracked files' current bytes, whole, in
place: `/bin/bash <absolute path of the hook or the script>`, with a throwaway repository as the
working directory. Both start by moving to that repository's top level, so the check files they
reach from there -- .github/touches.sh, tools/smoke.sh, the Python checks -- are the case's. The
case links .github/touches.sh and tools/check.sh to the real ones, so the halves are decided by the
real classifier and the hook reaches the real script; it puts a double at tools/smoke.sh.

**The interpreter is named**, as in land_tests.py: /bin/bash, so a Mac with Homebrew's bash first
on PATH still tests the 3.2 macOS ships, and SCRIPT_TESTS_REQUIRE_BASH (CI's macOS job sets it to
3.2) fails the run when /bin/bash is another version. The case's PATH holds a `bash` that is
/bin/bash too, because `.github/touches.sh` and tools/smoke.sh run through `#!/usr/bin/env bash`.

**A constructed environment, inheriting nothing.** PATH is the case's bin directory alone; HOME,
the git configuration and the locale are the case's own. An inherited GIT_DIR would point every
git call at another repository.

**Real git; doubles for python3, dotnet, terraform and the smoke test.** The Python checks have
their own companions and ask GitHub things; the build, the suite and Terraform take minutes and
reach the network. Each double records its arguments, in one sequence shared by all of them, so a
case asserts what ran and in what order. dotnet and terraform print a line on standard output, so
a case can see whether the script showed or hid it. A double fails when a case names the call.

**The hook's output is asserted literally.** The expected lines in HookTests are the old hook's
messages, typed from its text, not read from the script under test (#68: same checks, same skips,
same messages), except the Terraform lines #69 changed on purpose: the skip message names the floor
too, a line names the Terraform that ran, and the last line names that version.

Run: python3 -I tools/check_tests.py
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
import textwrap
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "check.sh"
HOOK = ROOT / ".githooks" / "pre-push"
TOUCHES = ROOT / ".github" / "touches.sh"
FLOOR_HELPER = ROOT / "tools" / "terraform-floor.sh"
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"

# The floor, as the repository's one file names it: CI installs exactly this version and the local
# check runs it when this machine has it (#69). Read, not typed, so raising the floor raises the
# cases with it; the file's own shape is FloorFileTests' to check.
FLOOR_FILE = ROOT / ".terraform-version"
FLOOR = FLOOR_FILE.read_text().strip()


def later_than(version: str) -> str:
    """A release five minor versions after `version`, or 99.0.0 when `version` is not one exact
    MAJOR.MINOR.PATCH, which FloorFileTests reports."""
    match = re.fullmatch(r"(\d+)\.(\d+)\.\d+", version)
    return f"{match[1]}.{int(match[2]) + 5}.5" if match else "99.0.0"


# What the Mac runs while the floor is not installed: a later release, as 1.16.5 was to the 1.11.0
# floor on 2026-10-17. Worked out from the floor rather than typed, so raising the floor to 1.16.5
# or past it leaves these cases a substitute that is not the floor (#69: a one-line change).
LATER = later_than(FLOOR)
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"

# The real tools the script and the classifier use, reached from the case's bin directory, which
# is the whole of the case's PATH. find runs dirname itself, through PATH.
TOOLS = ("git", "find", "sort", "head", "dirname", "basename", "grep")

PORT = "5299"

# A double: records its name and arguments as one call in the shared sequence, then fails when the
# case named this call, and otherwise prints a line (dotnet, terraform, smoke) or nothing (python3).
# A Terraform double answers `version` as Terraform does, first line `Terraform v<version>`, with the
# version the case gave it (#69).
DOUBLE = """#!/bin/bash
log={log}
name={name}
fails_on={fails_on}
loud={loud}
reports={reports}
n=0
while [ -e "$log/call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$name" "$@" > "$log/call-$n"
if [ -n "$reports" ] && [ "$*" = version ]; then
  printf 'Terraform v%s\\non darwin_arm64\\n' "$reports"
  exit 0
fi
if [ -n "$fails_on" ]; then
  case " $* " in
    *" $fails_on "*) printf '%s double failed: %s\\n' "$name" "$*" >&2; exit 1 ;;
  esac
fi
if [ -n "$loud" ]; then printf '%s double ran: %s\\n' "$name" "$*"; fi
exit 0
"""

# The files each case's repository starts with. Three Terraform directories: two roots and a
# module; one root keeps its test in tests/, the module beside its configuration, which are the two
# places Terraform looks (https://developer.hashicorp.com/terraform/language/tests#test-directory).
FILES = {
    ".terraform-version": FLOOR + "\n",
    "README.md": "readme\n",
    "docs/notes.md": "notes\n",
    "src/App/App.cs": "class App {}\n",
    "infra/roots/a/main.tf": "# a\n",
    "infra/roots/b/main.tf": "# b\n",
    "infra/roots/b/tests/b.tftest.hcl": "# b's test\n",
    "infra/modules/m/main.tf": "# m\n",
    "infra/modules/m/m.tftest.hcl": "# m's test\n",
}

ROOTS = ["infra/modules/m", "infra/roots/a", "infra/roots/b"]
TEST_DIRS = ["infra/modules/m", "infra/roots/b"]

# The hook's lines, as the hook before #68 printed them. #69 changed one on purpose: the Terraform
# half now names the version it ran, in a line of its own and in the last line.
HOOK_RAN = "A-05, the steering documents, every ticket citation, the ticket numbers, the roadmap, the artifacts"
HOOK_BUILD_SKIPPED = "pre-push: build skipped: no .NET source changed."
HOOK_TERRAFORM_SKIPPED = "pre-push: Terraform skipped: nothing under infra/ or the floor changed."
HOOK_FIX = "pre-push: fix it, or push with --no-verify if you mean it."

# The steering check is handed the base A-05 judges against, for its authority rule's range
# (#102), and is run without one when there is no base, so that it reads every commit instead.
STEERING = ["python3", "-I", ".github/steering.py"]
PYTHON_CHECKS = [
    ["python3", "-I", ".github/path-ownership.py", "origin/main", "HEAD"],
    STEERING + ["--base", "origin/main"],
    ["python3", "-I", "tools/roadmap.py", "--check"],
]
BUILD = [["dotnet", "build", "--nologo", "--verbosity", "quiet"]]
TESTS = [["dotnet", "test", "--solution", "SpaPlatform.slnx", "--no-build", "--ignore-exit-code", "8"]]
SMOKE = [["smoke", "Debug", PORT]]


def terraform_checks() -> list[list[str]]:
    # The check first asks which version it has, to run the floor and name it (#69).
    calls = [["terraform", "version"], ["terraform", "fmt", "-check", "-recursive", "infra"]]
    for root in ROOTS:
        calls += [["terraform", f"-chdir={root}", "init", "-backend=false", "-input=false"],
                  ["terraform", f"-chdir={root}", "validate"]]
    return calls


TERRAFORM_TESTS = [["terraform", f"-chdir={directory}", "test"] for directory in TEST_DIRS]


def floor_binary(home: pathlib.Path) -> pathlib.Path:
    """Where the floor is installed on a machine whose home is `home`: the -path preflight's
    hc-install line names, typed here from docs/dependencies.md rather than read from the helper."""
    return home / ".local" / "share" / "terraform" / FLOOR / "terraform"


def at_the_floor(me: str, binary: pathlib.Path) -> str:
    """The line that names the floor when the check runs it (#69)."""
    return f"{me}: Terraform {FLOOR} from {binary}: the floor, which CI runs."


def as_run_by(name: str, calls: list[list[str]]) -> list[list[str]]:
    """The same calls, made by the double called `name` rather than by `terraform`."""
    return [[name, *call[1:]] if call[0] == "terraform" else call for call in calls]


def bash_version() -> str:
    """The major.minor of the bash every case runs, as that bash reports it."""
    return subprocess.run(
        [BASH, "-c", 'printf "%s.%s" "${BASH_VERSINFO[0]}" "${BASH_VERSINFO[1]}"'],
        env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True, check=True).stdout


def setUpModule():
    full = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout
    print(f"check_tests: tools/check.sh runs under {BASH} {full}", file=sys.stderr)


class Result:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout.decode("utf-8", "surrogateescape")
        self.stderr = completed.stderr.decode("utf-8", "surrogateescape")

    def last_line(self, stream: str) -> str:
        lines = stream.splitlines()
        return lines[-1] if lines else ""


class Case:
    """One throwaway directory: a bare origin, a clone of it on a branch named `task`, and a bin
    directory holding the doubles and a way to each real tool, which is the case's whole PATH."""

    def __init__(self, test: unittest.TestCase, *, change: str | None = None,
                 fails_on: dict[str, str] | None = None, missing: tuple[str, ...] = (),
                 email: str = "check-tests@users.noreply.github.com", origin: bool = True,
                 path_terraform: str = FLOOR, floor_terraform: str | None = None,
                 floor_file: str | None = None):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.bin = self.dir / "bin"
        self.log = self.dir / "log"
        self.home = self.dir / "home"
        self.gitconfig = self.dir / "gitconfig"
        for directory in (self.bin, self.log, self.home):
            directory.mkdir()
        self.gitconfig.write_bytes(b"")
        for tool in TOOLS:
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            # An exec of the resolved binary rather than a link, as land_tests.py does: a git built
            # with RUNTIME_PREFIX finds its helpers relative to the path it was started from.
            self.write_executable(self.bin / tool,
                                  f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(found))} "$@"\n')
        (self.bin / "bash").symlink_to(BASH)
        fails_on = fails_on or {}
        for name, loud in (("python3", ""), ("dotnet", "yes"), ("terraform", "yes")):
            if name not in missing:
                reports = path_terraform if name == "terraform" else ""
                self.write_executable(self.bin / name,
                                      self.double(name, fails_on.get(name, ""), loud, reports))
        # The floor where preflight's line installs it, outside PATH, as hc-install leaves it. It
        # records its calls as `floor-terraform`, so a case can tell which Terraform ran.
        self.floor = floor_binary(self.home)
        if floor_terraform is not None:
            self.floor.parent.mkdir(parents=True)
            self.write_executable(self.floor, self.double("floor-terraform", fails_on.get("terraform", ""),
                                                          "yes", floor_terraform))

        self.work = self.dir / "work"
        if origin:
            self.origin = self.dir / "origin.git"
            self.git("init", "-q", "--bare", "-b", "main", str(self.origin), cwd=self.dir)
            self.git("clone", "-q", str(self.origin), str(self.work), cwd=self.dir)
        else:
            self.git("init", "-q", "-b", "main", str(self.work), cwd=self.dir)
        self.git("config", "user.name", "Check Tests", cwd=self.work)
        self.git("config", "user.email", email, cwd=self.work)
        files = dict(FILES)
        if floor_file is not None:
            files[".terraform-version"] = floor_file
        for name, text in files.items():
            path = self.work / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text)
        self.git("add", ".", cwd=self.work)
        self.git("commit", "-q", "-m", "Start", cwd=self.work)
        if origin:
            self.git("push", "-q", "origin", "main", cwd=self.work)
        self.git("checkout", "-q", "-b", "task", cwd=self.work)

        # The check files the script reaches from the top level: the real classifier and the real
        # script, linked, and a double for the smoke test. Left untracked, so no diff includes them.
        (self.work / ".github").mkdir()
        (self.work / ".github" / "touches.sh").symlink_to(TOUCHES)
        (self.work / "tools").mkdir()
        (self.work / "tools" / "check.sh").symlink_to(SCRIPT)
        (self.work / "tools" / "terraform-floor.sh").symlink_to(FLOOR_HELPER)
        self.write_executable(self.work / "tools" / "smoke.sh",
                              self.double("smoke", fails_on.get("smoke", ""), "yes"))

        if change:
            path = self.work / change
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(path.read_text() + "changed\n" if path.exists() else "new\n")
            self.git("add", change, cwd=self.work)
            self.git("commit", "-q", "-m", f"Change {change}", cwd=self.work)

    def double(self, name: str, fails_on: str, loud: str, reports: str = "") -> str:
        return DOUBLE.format(log=shlex.quote(str(self.log)), name=shlex.quote(name),
                             fails_on=shlex.quote(fails_on), loud=shlex.quote(loud),
                             reports=shlex.quote(reports))

    @staticmethod
    def write_executable(path: pathlib.Path, text: str):
        path.write_text(text)
        path.chmod(0o755)

    def env(self) -> dict[str, str]:
        return {"PATH": str(self.bin), "HOME": str(self.home), "GIT_CONFIG_NOSYSTEM": "1",
                "GIT_CONFIG_GLOBAL": str(self.gitconfig), "LC_ALL": "C"}

    def git(self, *args: str, cwd: pathlib.Path):
        subprocess.run(["git", *args], cwd=cwd, env=self.env(), check=True, capture_output=True)

    def run(self, *args: str, hook: bool = False, through_shebang: bool = False,
            cwd: pathlib.Path | None = None) -> Result:
        target = HOOK if hook else SCRIPT
        command = [str(target), *args] if through_shebang else [BASH, str(target), *args]
        completed = subprocess.run(command, cwd=cwd or self.work, env=self.env(),
                                   stdin=subprocess.DEVNULL, capture_output=True, timeout=60)
        return Result(completed)

    def calls(self) -> list[list[str]]:
        names = sorted(self.log.glob("call-*"), key=lambda path: int(path.name.split("-")[1]))
        return [path.read_bytes().decode("utf-8", "surrogateescape").split("\0")[:-1]
                for path in names]


class CheckTestCase(unittest.TestCase):
    def assertLines(self, expected: list[str], stream: str):
        self.assertEqual(expected, stream.splitlines())


class HookTests(CheckTestCase):
    """#68's first box: the hook's output is unchanged for a docs-only push, a .NET change and a
    change under infra/, now that the checks are tools/check.sh's."""

    def test_a_docs_only_push(self):
        case = Case(self, change="docs/notes.md")
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertLines([HOOK_BUILD_SKIPPED, HOOK_TERRAFORM_SKIPPED, f"pre-push: {HOOK_RAN} pass."],
                         result.stdout)
        self.assertEqual("", result.stderr)
        self.assertEqual(PYTHON_CHECKS, case.calls())

    def test_a_dotnet_change(self):
        case = Case(self, change="src/App/App.cs")
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        # The build's own output is not shown: the hook sends it to /dev/null.
        self.assertLines([HOOK_TERRAFORM_SKIPPED, f"pre-push: {HOOK_RAN}, the build pass."],
                         result.stdout)
        self.assertEqual("", result.stderr)
        self.assertEqual(PYTHON_CHECKS + BUILD, case.calls())

    def test_a_change_under_infra(self):
        case = Case(self, change="infra/roots/a/main.tf")
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertLines([HOOK_BUILD_SKIPPED, at_the_floor("pre-push", case.bin / "terraform"),
                          f"pre-push: {HOOK_RAN}, Terraform {FLOOR} pass."], result.stdout)
        self.assertEqual("", result.stderr)
        self.assertEqual(PYTHON_CHECKS + terraform_checks(), case.calls())

    def test_a_build_that_fails(self):
        case = Case(self, change="src/App/App.cs", fails_on={"dotnet": "build"})
        result = case.run(hook=True)
        self.assertEqual(1, result.status)
        self.assertLines([], result.stdout)
        self.assertLines(["dotnet double failed: build --nologo --verbosity quiet",
                          "pre-push: the solution does not build.", HOOK_FIX], result.stderr)

    def test_a_validate_that_fails(self):
        case = Case(self, change="infra/roots/a/main.tf", fails_on={"terraform": "validate"})
        result = case.run(hook=True)
        self.assertEqual(1, result.status)
        self.assertLines([HOOK_BUILD_SKIPPED, at_the_floor("pre-push", case.bin / "terraform")],
                         result.stdout)
        self.assertLines(["terraform double failed: -chdir=infra/modules/m validate",
                          "pre-push: terraform validate failed in infra/modules/m.", HOOK_FIX],
                         result.stderr)

    def test_a_steering_check_that_fails(self):
        case = Case(self, change="docs/notes.md", fails_on={"python3": ".github/steering.py"})
        result = case.run(hook=True)
        self.assertEqual(1, result.status)
        self.assertLines(["python3 double failed: -I .github/steering.py --base origin/main",
                          "pre-push: the steering check failed.", HOOK_FIX], result.stderr)
        self.assertEqual(PYTHON_CHECKS[:2], case.calls())

    def test_an_email_that_is_not_the_noreply_address(self):
        case = Case(self, change="docs/notes.md", email="someone@example.com")
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertLines([
            "pre-push: warning: committing as 'someone@example.com', not an @users.noreply.github.com address.",
            "pre-push: if that address is private on GitHub, it will refuse the push.",
        ], result.stderr)

    def test_without_python3_or_terraform(self):
        case = Case(self, change="infra/roots/a/main.tf", missing=("python3", "terraform"))
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertLines([
            "pre-push: A-05 not checked: python3 is not on PATH.",
            "pre-push: steering not checked: python3 is not on PATH.",
            "pre-push: the roadmap not checked: python3 is not on PATH.",
            "pre-push: Terraform not checked: terraform is not on PATH.",
        ], result.stderr)
        self.assertLines([HOOK_BUILD_SKIPPED, f"pre-push: {HOOK_RAN} pass."], result.stdout)

    def test_without_a_main_to_diff_against(self):
        # No origin/main and no main: both halves run, because a skip has to be earned by a diff.
        case = Case(self, origin=False)
        case.git("branch", "-q", "-m", "main", "trunk", cwd=case.work)
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertLines(["pre-push: A-05 not checked: no main to diff against."], result.stderr)
        self.assertLines([at_the_floor("pre-push", case.bin / "terraform"),
                          f"pre-push: {HOOK_RAN}, the build, Terraform {FLOOR} pass."], result.stdout)
        # No base, so the steering check is handed none and reads every commit HEAD reaches.
        self.assertEqual([STEERING] + PYTHON_CHECKS[2:] + BUILD + terraform_checks(), case.calls())

    def test_duplicated_build_artifacts(self):
        case = Case(self, change="docs/notes.md")
        duplicate = case.work / "src" / "App" / "bin" / "App 2.dll"
        duplicate.parent.mkdir(parents=True)
        duplicate.write_bytes(b"")
        result = case.run(hook=True)
        self.assertEqual(1, result.status)
        self.assertLines([
            "pre-push: duplicated build artifacts:",
            "src/App/bin/App 2.dll",
            "pre-push: delete every bin/ and obj/ and build again, and move the clone out of iCloud Drive:",
            "  find src tests fixtures tools -type d \\( -name bin -o -name obj \\) -prune -exec rm -rf {} +",
            "pre-push: duplicated build artifacts make the build report errors that are not in the source.",
            HOOK_FIX,
        ], result.stderr)

    def test_the_hook_ignores_what_git_passes_it(self):
        # git runs a pre-push hook with the remote's name and URL as arguments and the refs on
        # standard input (https://git-scm.com/docs/githooks#_pre_push); none of it is an option.
        case = Case(self, change="docs/notes.md")
        result = case.run("origin", "git@github.com:example/example.git", hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(f"pre-push: {HOOK_RAN} pass.", result.last_line(result.stdout))

    def test_the_hook_runs_through_its_own_shebang(self):
        self.assertTrue(os.access(HOOK, os.X_OK), f"{HOOK} is not executable")
        case = Case(self, change="docs/notes.md")
        result = case.run(hook=True, through_shebang=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(f"pre-push: {HOOK_RAN} pass.", result.last_line(result.stdout))


FULL_RAN = HOOK_RAN


class FullModeTests(CheckTestCase):
    """--full: the hook's checks, and what CI runs that the hook leaves to it, ending on a verdict."""

    def full(self, case: Case, *extra: str) -> Result:
        return case.run("--full", "--port", PORT, *extra)

    def assertVerdict(self, result: Result, status: int, verdict: str):
        """The last line of everything the check wrote is its verdict: a log's tail is enough."""
        self.assertEqual(status, result.status, result.stdout + result.stderr)
        stream = result.stdout if status == 0 else result.stderr
        self.assertEqual(verdict, result.last_line(stream))

    def test_a_dotnet_change_runs_the_build_the_tests_and_the_smoke_test(self):
        case = Case(self, change="src/App/App.cs")
        result = self.full(case)
        self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}, the build, the tests, the smoke test.")
        self.assertEqual(PYTHON_CHECKS + BUILD + TESTS + SMOKE, case.calls())
        # Shown, not hidden: a lane reads its failure from the log.
        self.assertIn("dotnet double ran: build --nologo --verbosity quiet", result.stdout)
        self.assertIn("check: Terraform and its tests skipped: nothing under infra/ or the floor changed.",
                      result.stdout)
        self.assertNotIn("pre-push", result.stdout + result.stderr)

    def test_a_change_under_infra_runs_terraform_test_where_there_are_tests(self):
        case = Case(self, change="infra/roots/a/main.tf")
        result = self.full(case)
        self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}, Terraform {FLOOR}, the Terraform tests.")
        self.assertEqual(PYTHON_CHECKS + terraform_checks() + TERRAFORM_TESTS, case.calls())
        self.assertIn("check: build, tests and smoke test skipped: no .NET source changed.",
                      result.stdout)

    def test_a_docs_only_change_runs_neither_half(self):
        case = Case(self, change="docs/notes.md")
        result = self.full(case)
        self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}.")
        self.assertEqual(PYTHON_CHECKS, case.calls())

    def test_a_base_the_caller_names(self):
        # An overnight lane judges its branch against its fork point, not main.
        case = Case(self, change="src/App/App.cs")
        fork = subprocess.run(["git", "rev-parse", "HEAD"], cwd=case.work, env=case.env(),
                              capture_output=True, text=True, check=True).stdout.strip()
        (case.work / "docs" / "notes.md").write_text("later\n")
        case.git("commit", "-q", "-am", "Docs after the fork", cwd=case.work)
        result = self.full(case, "--base", fork)
        self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}.")
        self.assertEqual(["python3", "-I", ".github/path-ownership.py", fork, "HEAD"], case.calls()[0])
        # The steering check's authority rule judges the same range A-05 does (#102).
        self.assertEqual(STEERING + ["--base", fork], case.calls()[1])

    def test_failures_end_on_the_verdict_and_stop_there(self):
        cases = {
            "the build": ({"dotnet": "build"}, "src/App/App.cs", "check: fail: the solution does not build.",
                          PYTHON_CHECKS + BUILD),
            "the tests": ({"dotnet": "test"}, "src/App/App.cs", "check: fail: the tests failed.",
                          PYTHON_CHECKS + BUILD + TESTS),
            "the smoke test": ({"smoke": "Debug"}, "src/App/App.cs", "check: fail: the smoke test failed.",
                               PYTHON_CHECKS + BUILD + TESTS + SMOKE),
            "terraform test": ({"terraform": "test"}, "infra/roots/a/main.tf",
                               "check: fail: terraform test failed in infra/modules/m.",
                               PYTHON_CHECKS + terraform_checks() + TERRAFORM_TESTS[:1]),
            "A-05": ({"python3": ".github/path-ownership.py"}, "docs/notes.md",
                     "check: fail: check A-05 failed.", PYTHON_CHECKS[:1]),
        }
        for name, (fails_on, change, verdict, calls) in cases.items():
            with self.subTest(failing=name):
                case = Case(self, change=change, fails_on=fails_on)
                result = self.full(case)
                self.assertVerdict(result, 1, verdict)
                self.assertEqual(calls, case.calls())
                self.assertNotIn("--no-verify", result.stderr)

    def test_a_missing_tool_fails_rather_than_skipping(self):
        # The hook skips a check whose tool is missing and passes; a verdict must not hide that.
        for tool, change in (("python3", "docs/notes.md"), ("terraform", "infra/roots/a/main.tf"),
                             ("dotnet", "src/App/App.cs")):
            with self.subTest(tool=tool):
                case = Case(self, change=change, missing=(tool,))
                result = self.full(case)
                self.assertVerdict(result, 1, f"check: fail: {tool} is not on PATH.")

    def test_no_base_fails_rather_than_skipping_a_05(self):
        case = Case(self, origin=False)
        case.git("branch", "-q", "-m", "main", "trunk", cwd=case.work)
        result = self.full(case)
        self.assertVerdict(result, 1, "check: fail: no origin/main or main to judge against; name one with --base.")

    def test_a_named_base_that_does_not_resolve(self):
        for mode in ((), ("--full", "--port", PORT)):
            with self.subTest(mode=mode or "the hook's"):
                case = Case(self, change="docs/notes.md")
                result = case.run(*mode, "--base", "no-such-ref")
                self.assertEqual(1, result.status)
                self.assertIn("no commit no-such-ref to judge against.", result.stderr)
                self.assertEqual([], case.calls())

    def test_an_unexpected_failure_still_ends_on_the_verdict(self):
        # Outside any repository, the first git call fails, which no fail() line names.
        case = Case(self)
        outside = case.dir / "outside"
        outside.mkdir()
        result = case.run("--full", "--port", PORT, cwd=outside)
        self.assertEqual(1, result.status)
        self.assertRegex(result.last_line(result.stderr), r"^check: fail: stopped with status \d+\.$")

    def test_usage_errors(self):
        for args, message in (
            (("--full",), "--full needs --port <port>"),
            (("--full", "--port"), "--port needs a value"),
            (("--full", "--port", "eighty"), "--port takes a number, not eighty"),
            (("--port", PORT), "--port is for --full"),
            (("--base",), "--base needs a value"),
            (("--fast",), "unknown argument --fast"),
        ):
            with self.subTest(args=args):
                case = Case(self)
                result = case.run(*args)
                self.assertEqual(2, result.status, result.stderr)
                self.assertEqual(f"check: fail: {message}.", result.last_line(result.stderr))
                self.assertIn("usage: tools/check.sh", result.stderr)
                self.assertEqual([], case.calls())


class BothHalvesTests(CheckTestCase):
    """--both-halves: both halves run whatever the diff touches. On main the diff against
    origin/main is empty, and #68's second box asks for the full mode green there."""

    def test_an_empty_diff_runs_nothing_heavy_without_it(self):
        # The case --both-halves exists for: main's own diff, which earns every skip.
        case = Case(self)
        result = case.run("--full", "--port", PORT)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(f"check: pass: {FULL_RAN}.", result.last_line(result.stdout))
        self.assertEqual(PYTHON_CHECKS, case.calls())

    def test_the_full_check_runs_everything_on_an_empty_diff(self):
        case = Case(self)
        result = case.run("--full", "--port", PORT, "--both-halves")
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(
            f"check: pass: {FULL_RAN}, the build, the tests, the smoke test, Terraform {FLOOR}, the Terraform tests.",
            result.last_line(result.stdout))
        self.assertEqual(PYTHON_CHECKS + BUILD + TESTS + SMOKE + terraform_checks() + TERRAFORM_TESTS,
                         case.calls())
        self.assertNotIn("skipped", result.stdout)

    def test_the_hooks_checks_run_both_halves_on_a_docs_only_change(self):
        case = Case(self, change="docs/notes.md")
        result = case.run("--both-halves")
        self.assertEqual(0, result.status, result.stderr)
        self.assertLines([at_the_floor("pre-push", case.bin / "terraform"),
                          f"pre-push: {HOOK_RAN}, the build, Terraform {FLOOR} pass."], result.stdout)
        self.assertEqual(PYTHON_CHECKS + BUILD + terraform_checks(), case.calls())


class FloorTests(CheckTestCase):
    """#69: a Terraform change checked here is checked by the Terraform CI runs, the floor that
    .terraform-version names, when this machine has it; when it does not, the check names the
    version it used instead, so a green result never hides the substitution."""

    def test_the_floor_in_its_directory_runs_every_terraform_call_and_is_named(self):
        case = Case(self, change="infra/roots/a/main.tf", path_terraform=LATER, floor_terraform=FLOOR)
        result = case.run("--full", "--port", PORT)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(
            PYTHON_CHECKS + as_run_by("floor-terraform", terraform_checks() + TERRAFORM_TESTS),
            case.calls())
        self.assertIn(at_the_floor("check", case.floor), result.stdout.splitlines())
        self.assertEqual(f"check: pass: {FULL_RAN}, Terraform {FLOOR}, the Terraform tests.",
                         result.last_line(result.stdout))
        self.assertEqual("", result.stderr)

    def test_without_the_floor_the_version_used_is_named_in_the_warning_and_the_verdict(self):
        # Box 4: a green result says it was not the floor, in the lines a log's tail shows.
        case = Case(self, change="infra/roots/a/main.tf", path_terraform=LATER)
        path = case.bin / "terraform"
        warning = [f"check: warning: Terraform {LATER} from {path}, not the floor {FLOOR} that "
                   ".terraform-version names and CI runs.",
                   "check: tools/preflight.sh says how to install the floor."]
        result = case.run("--full", "--port", PORT)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(PYTHON_CHECKS + terraform_checks() + TERRAFORM_TESTS, case.calls())
        self.assertLines(warning, result.stderr)
        self.assertEqual(f"check: pass: {FULL_RAN}, Terraform {LATER} instead of the floor {FLOOR}, "
                         "the Terraform tests.", result.last_line(result.stdout))

    def test_the_hook_names_the_substitute_too(self):
        case = Case(self, change="infra/roots/a/main.tf", path_terraform=LATER)
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertLines([f"pre-push: warning: Terraform {LATER} from {case.bin / 'terraform'}, not the "
                          f"floor {FLOOR} that .terraform-version names and CI runs.",
                          "pre-push: tools/preflight.sh says how to install the floor."], result.stderr)
        self.assertLines([HOOK_BUILD_SKIPPED,
                          f"pre-push: {HOOK_RAN}, Terraform {LATER} instead of the floor {FLOOR} pass."],
                         result.stdout)

    def test_a_terraform_that_reports_no_version_is_named_as_such(self):
        # Whatever the PATH holds, the verdict must not read as the floor.
        case = Case(self, change="infra/roots/a/main.tf", path_terraform="")
        result = case.run("--full", "--port", PORT)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(f"check: pass: {FULL_RAN}, Terraform of no known version instead of the floor "
                         f"{FLOOR}, the Terraform tests.", result.last_line(result.stdout))

    def test_a_floor_directory_holding_another_version_is_not_the_floor(self):
        # A binary is the floor by what it reports, not by where it sits.
        case = Case(self, change="infra/roots/a/main.tf", path_terraform=LATER, floor_terraform="1.10.5")
        result = case.run("--full", "--port", PORT)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(PYTHON_CHECKS + [["floor-terraform", "version"]] + terraform_checks() + TERRAFORM_TESTS,
                         case.calls())
        self.assertIn(f"Terraform {LATER} instead of the floor {FLOOR}", result.last_line(result.stdout))

    def test_the_floor_runs_where_no_terraform_is_on_path(self):
        case = Case(self, change="infra/roots/a/main.tf", missing=("terraform",), floor_terraform=FLOOR)
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual("", result.stderr)
        self.assertEqual(f"pre-push: {HOOK_RAN}, Terraform {FLOOR} pass.", result.last_line(result.stdout))

    def test_neither_the_floor_nor_a_terraform_on_path(self):
        case = Case(self, change="infra/roots/a/main.tf", missing=("terraform",))
        hook = case.run(hook=True)
        self.assertEqual(0, hook.status, hook.stderr)
        self.assertLines(["pre-push: Terraform not checked: terraform is not on PATH."], hook.stderr)
        full = case.run("--full", "--port", PORT)
        self.assertEqual(1, full.status)
        self.assertEqual("check: fail: terraform is not on PATH.", full.last_line(full.stderr))

    def test_a_file_that_names_no_exact_version_fails_both_modes(self):
        # A range or `latest` would have CI install the newest release that matches, not the floor.
        for text in ("<1.12.0\n", "latest\n", "1.11\n", "1.11.0\n1.12.0\n", "1.11.0 \n", ""):
            for mode in ((), ("--full", "--port", PORT)):
                with self.subTest(text=text, mode=mode or "the hook's"):
                    case = Case(self, change="infra/roots/a/main.tf", floor_file=text)
                    result = case.run(*mode)
                    self.assertEqual(1, result.status)
                    self.assertIn(".terraform-version does not name one exact version, such as 1.11.0, so "
                                  "nothing says which Terraform CI runs.", result.stderr)
                    self.assertEqual(PYTHON_CHECKS, case.calls())


class FloorFileTests(CheckTestCase):
    """#69's first box: one file names the floor, and CI's Terraform job installs the version it
    names. The job's step runs here as the workflow holds it, as .github/touches_tests.py runs the
    decision step, rather than as a second copy of it."""

    STEP_ID = "terraform-floor"

    def workflow_step(self) -> str:
        workflow = WORKFLOW.read_text()
        marker = f"        id: {self.STEP_ID}\n"
        self.assertEqual(1, workflow.count(marker), f"ci.yml has no single step with id {self.STEP_ID}")
        step = workflow.split(marker, 1)[1].split("\n      - ", 1)[0]
        self.assertIn("        run: |\n", step)
        return textwrap.dedent(step.split("        run: |\n", 1)[1])

    def run_step(self, text: str) -> tuple[subprocess.CompletedProcess, str]:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "tools").mkdir()
            (root / "tools" / "terraform-floor.sh").symlink_to(FLOOR_HELPER)
            (root / ".terraform-version").write_text(text)
            output = root / "github-output"
            output.write_text("")
            completed = subprocess.run([BASH, "-c", self.workflow_step()], cwd=root, capture_output=True,
                                       text=True, timeout=30,
                                       env={"PATH": "/usr/bin:/bin", "GITHUB_OUTPUT": str(output)})
            return completed, output.read_text()

    def test_the_file_names_one_exact_version(self):
        # What the reader refuses, CI would refuse too; this is the repository's own file.
        read = subprocess.run([BASH, "-c", '. "$1"; terraform_floor_read "$2"', "read", str(FLOOR_HELPER),
                               str(FLOOR_FILE)], env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True)
        self.assertEqual(0, read.returncode, f"{FLOOR_FILE} does not name one exact version")
        self.assertEqual(FLOOR + "\n", read.stdout)

    def test_ci_reads_the_version_the_file_names(self):
        for version in (FLOOR, "1.12.3"):
            with self.subTest(version=version):
                completed, output = self.run_step(version + "\n")
                self.assertEqual(0, completed.returncode, completed.stderr)
                self.assertEqual([f"version={version}"], output.splitlines())

    def test_ci_refuses_a_file_that_names_no_exact_version(self):
        for text in ("<1.12.0\n", "latest\n", "1.11\n", ""):
            with self.subTest(text=text):
                completed, output = self.run_step(text)
                self.assertNotEqual(0, completed.returncode)
                self.assertIn("::error::.terraform-version does not name one exact version", completed.stdout)
                self.assertEqual("", output)

    def test_setup_terraform_installs_what_the_step_read(self):
        workflow = WORKFLOW.read_text()
        versions = [line.strip() for line in workflow.splitlines() if line.strip().startswith("terraform_version:")]
        self.assertEqual([f"terraform_version: ${{{{ steps.{self.STEP_ID}.outputs.version }}}}"], versions)
        # The step runs first, in the same job as the install.
        job = workflow.split("\n  terraform:\n", 1)[1]
        self.assertLess(job.index(f"id: {self.STEP_ID}"), job.index("uses: hashicorp/setup-terraform@"))


class InterpreterTests(CheckTestCase):
    def test_bash_is_the_version_this_run_requires(self):
        # Set by CI's macOS job, so a runner image whose /bin/bash is no longer 3.2 fails the job
        # instead of quietly testing a bash the script's header does not target.
        required = os.environ.get(REQUIRE_BASH, "")
        version = bash_version()
        self.assertRegex(version, r"^\d+\.\d+$")
        if required:
            self.assertEqual(required, version, f"{REQUIRE_BASH}={required}, but {BASH} is {version}")

    def test_runs_through_its_own_shebang(self):
        self.assertTrue(os.access(SCRIPT, os.X_OK), f"{SCRIPT} is not executable")
        case = Case(self, change="docs/notes.md")
        result = case.run("--full", "--port", PORT, through_shebang=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(f"check: pass: {FULL_RAN}.", result.last_line(result.stdout))


if __name__ == "__main__":
    unittest.main()
