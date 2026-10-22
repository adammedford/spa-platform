#!/usr/bin/env python3
"""Test companion for tools/check.sh, the local check, and for .githooks/pre-push, which runs it.

Owner: integrator. Built in #68; the tool choice is D-3 in docs/dependencies.md, and the shape is
tools/land_tests.py's.

**What runs is the script itself.** Each case runs the tracked files' current bytes, whole, in
place: `/bin/bash <absolute path of the hook or the script>`, with a throwaway repository as the
working directory. Both start by moving to that repository's top level, so the check files they
reach from there -- .github/touches.sh, tools/smoke.sh, tools/image-check.sh, the Python checks --
are the case's. The case links .github/touches.sh and tools/check.sh to the real ones, so the halves
are decided by the real classifier and the hook reaches the real script; it puts doubles at
tools/smoke.sh and tools/image-check.sh.

**The interpreter is named**, as in land_tests.py: /bin/bash, so a Mac with Homebrew's bash first
on PATH still tests the 3.2 macOS ships, and SCRIPT_TESTS_REQUIRE_BASH (CI's macOS job sets it to
3.2) fails the run when /bin/bash is another version. The case's PATH holds a `bash` that is
/bin/bash too, because `.github/touches.sh` and tools/smoke.sh run through `#!/usr/bin/env bash`.

**A constructed environment, inheriting nothing.** PATH is the case's bin directory alone; HOME,
the git configuration and the locale are the case's own. An inherited GIT_DIR would point every
git call at another repository.

**Real git; doubles for python3, dotnet, terraform, the smoke test and the image check.** The
Python checks have their own companions and ask GitHub things; the build, the suite, Terraform and
the image take minutes and reach the network. Each double records its arguments, in one sequence
shared by all of them, so a case asserts what ran and in what order. dotnet and terraform print a
line on standard output, so a case can see whether the script showed or hid it. A double fails
when a case names the call.

**The hook's output is asserted literally.** The expected lines in HookTests are the old hook's
messages, typed from its text, not read from the script under test (#68: same checks, same skips,
same messages), except the Terraform lines #69 changed on purpose: the skip message names the floor
too, a line names the Terraform that ran, and the last line names that version.

**The cases run at the same time** (#107), on the threads of tools/companion_runner.py: one per
CPU, up to eight, unless SCRIPT_TESTS_JOBS names another number. That rests on each case keeping to
its own throwaway directory and its own constructed environment. No case changes the working
directory, os.environ or anything else the process shares, and a new case must not either. The
real tools in a case's bin directory are links to wrappers written once for the run, because on the
Mac this was measured on a newly written file costs about 45 ms more the first time it runs; the
doubles are still each case's own.

Run: python3 -I tools/check_tests.py
"""

from __future__ import annotations

import importlib.util
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
spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
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
# is the whole of the case's PATH. find runs dirname itself, through PATH; xargs runs find for the
# file modes (#205).
TOOLS = ("git", "find", "xargs", "sort", "head", "dirname", "basename", "grep")

PORT = "5299"

# A double: records its name and arguments as one call in the shared sequence, then fails when the
# case named this call, and otherwise prints a line (dotnet, terraform, smoke, image) or nothing
# (python3). The image check takes no arguments, so a case names its call as `*`, which fails any.
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
if [ "$fails_on" = "*" ]; then printf '%s double failed\\n' "$name" >&2; exit 1; fi
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
# half now names the version it ran, in a line of its own and in the last line. #205 added a check,
# the file modes, and so a name to the list of what ran.
HOOK_RAN = ("A-05, the steering documents, every ticket citation, the ticket numbers, the roadmap, "
            "the artifacts, the file modes")
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
IMAGE = [["image"]]


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


def bash_full_version() -> str:
    """The whole version string of the bash every case runs, such as 3.2.57(1)-release, as that
    bash reports it."""
    return subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout


def setUpModule():
    print(f"check_tests: tools/check.sh runs under {BASH} {bash_full_version()}", file=sys.stderr)


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

    def __init__(self, test: unittest.TestCase, *, change: str | tuple[str, ...] | None = None,
                 fails_on: dict[str, str] | None = None, missing: tuple[str, ...] = (),
                 email: str = "check-tests@users.noreply.github.com", origin: bool = True,
                 path_terraform: str = FLOOR, floor_terraform: str | None = None,
                 floor_file: str | None = None, extra: dict[str, str] | None = None):
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
            # A link to an exec of the resolved binary, written once for the run, rather than a
            # link to the binary, as land_tests.py does: a git built with RUNTIME_PREFIX finds its
            # helpers relative to the path it was started from.
            runner.link_tool(self.bin / tool, os.path.realpath(found))
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
        files.update(extra or {})
        for name, text in files.items():
            path = self.work / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text)
            # The mode a checkout under umask 022 gives, whatever the umask of the process running
            # this companion: under an agent session's 077, write_text leaves 600, which the file
            # modes check refuses (#205), and every case would fail on that instead of its own point.
            path.chmod(0o644)
        self.git("add", ".", cwd=self.work)
        self.git("commit", "-q", "-m", "Start", cwd=self.work)
        if origin:
            self.git("push", "-q", "origin", "main", cwd=self.work)
        self.git("checkout", "-q", "-b", "task", cwd=self.work)

        # The check files the script reaches from the top level: the real classifier and the real
        # script, linked, and doubles for the smoke test and the image check. Left untracked, so no
        # diff includes them.
        (self.work / ".github").mkdir(exist_ok=True)
        (self.work / ".github" / "touches.sh").symlink_to(TOUCHES)
        (self.work / "tools").mkdir(exist_ok=True)
        (self.work / "tools" / "check.sh").symlink_to(SCRIPT)
        (self.work / "tools" / "terraform-floor.sh").symlink_to(FLOOR_HELPER)
        self.write_executable(self.work / "tools" / "smoke.sh",
                              self.double("smoke", fails_on.get("smoke", ""), "yes"))
        self.write_executable(self.work / "tools" / "image-check.sh",
                              self.double("image", fails_on.get("image", ""), "yes"))

        for name in (change,) if isinstance(change, str) else change or ():
            path = self.work / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(path.read_text() + "changed\n" if path.exists() else "new\n")
            path.chmod(0o644)
            self.git("add", name, cwd=self.work)
            self.git("commit", "-q", "-m", f"Change {name}", cwd=self.work)

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
            cwd: pathlib.Path | None = None, umask: int = 0o022) -> Result:
        # The file-mode mask is the child's own, set between fork and exec, so the cases running
        # at the same time on other threads keep theirs; os.umask would change the whole process's.
        # 022 by default, what a checkout and CI have, whatever mask runs this companion.
        target = HOOK if hook else SCRIPT
        command = [str(target), *args] if through_shebang else [BASH, str(target), *args]
        completed = subprocess.run(command, cwd=cwd or self.work, env=self.env(), umask=umask,
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

    def test_a_tracked_file_only_its_owner_can_read(self):
        # #205: git records only the exec bit, so a file written under umask 077 is 600 with no
        # diff to show it, and an image built from the tree keeps the mode. The check names the
        # file, the fix and why, and judges the working tree, so the file needs no change of its own.
        case = Case(self, change="docs/notes.md")
        (case.work / "src" / "App" / "App.cs").chmod(0o600)
        result = case.run(hook=True)
        self.assertEqual(1, result.status)
        self.assertLines([
            "pre-push: tracked files that group and others cannot read:",
            "src/App/App.cs",
            "pre-push: git does not record these modes, but an image built from this tree keeps them. "
            "Make them readable:",
            "  git ls-files -z | xargs -0 chmod go+r",
            "pre-push: a tracked file only its owner can read is a file the gateway's container cannot "
            "read; run chmod go+r on it.",
            HOOK_FIX,
        ], result.stderr)
        # Refused before the build, which would have carried the mode into what it wrote.
        self.assertEqual(PYTHON_CHECKS, case.calls())

    def test_a_file_the_group_can_read_but_others_cannot(self):
        # 640 is group-readable and still not readable by a container user outside the group.
        case = Case(self, change="docs/notes.md")
        (case.work / "README.md").chmod(0o640)
        result = case.run(hook=True)
        self.assertEqual(1, result.status)
        self.assertEqual(["pre-push: tracked files that group and others cannot read:", "README.md"],
                         result.stderr.splitlines()[:2])

    def test_more_than_ten_such_files_are_counted_past_the_tenth(self):
        names = [f"docs/owner-only-{n:02}.md" for n in range(12)]
        case = Case(self, change="docs/notes.md", extra={name: "x\n" for name in names})
        for name in names:
            (case.work / name).chmod(0o600)
        result = case.run(hook=True)
        self.assertEqual(1, result.status)
        lines = result.stderr.splitlines()
        self.assertEqual(sorted(names)[:10], lines[1:11])
        self.assertEqual("... and 2 more", lines[11])

    def test_files_that_are_not_tracked_or_not_there_are_not_judged(self):
        # An untracked owner-only file, a key under .certs/ say, is meant to be 600, and a tracked
        # file deleted from the working tree has no mode to judge.
        case = Case(self, change="docs/notes.md")
        untracked = case.work / "notes.local"
        untracked.write_text("private\n")
        untracked.chmod(0o600)
        (case.work / "README.md").unlink()
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(f"pre-push: {HOOK_RAN} pass.", result.last_line(result.stdout))

    def test_a_tracked_executable_and_a_tracked_symlink_pass(self):
        # The link gets the mode a umask 077 gives it on macOS, where a link has a mode of its own:
        # chmod go+r follows a link, so a check that judged the link's mode could not be cleared.
        # Linux has no lchmod and keeps every link 777.
        case = Case(self, change="docs/notes.md")
        script = case.work / "run.sh"
        script.write_text("#!/bin/sh\n")
        script.chmod(0o755)
        link = case.work / "link.md"
        link.symlink_to("README.md")
        if hasattr(os, "lchmod"):
            os.lchmod(link, 0o700)
        case.git("add", "run.sh", "link.md", cwd=case.work)
        case.git("commit", "-q", "-m", "Add a script and a link", cwd=case.work)
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(f"pre-push: {HOOK_RAN} pass.", result.last_line(result.stdout))

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

    def test_a_dotnet_change_runs_the_build_the_tests_the_smoke_test_and_the_image(self):
        case = Case(self, change="src/App/App.cs")
        result = self.full(case)
        self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}, the build, the tests, the smoke test, "
                                      "the gateway image.")
        self.assertEqual(PYTHON_CHECKS + BUILD + TESTS + SMOKE + IMAGE, case.calls())
        # Shown, not hidden: a lane reads its failure from the log.
        self.assertIn("dotnet double ran: build --nologo --verbosity quiet", result.stdout)
        self.assertIn("check: Terraform and its tests skipped: nothing under infra/ or the floor changed.",
                      result.stdout)
        self.assertNotIn("pre-push", result.stdout + result.stderr)

    def test_a_change_to_the_dockerignore_builds_the_gateway_image(self):
        # .dockerignore decides what the image is built from, so it is in the build half (#210).
        case = Case(self, change=".dockerignore")
        result = self.full(case)
        self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}, the build, the tests, the smoke test, "
                                      "the gateway image.")
        self.assertEqual(PYTHON_CHECKS + BUILD + TESTS + SMOKE + IMAGE, case.calls())
        self.assertIn("image double ran: ", result.stdout)

    def test_a_change_under_infra_runs_terraform_test_where_there_are_tests(self):
        case = Case(self, change="infra/roots/a/main.tf")
        result = self.full(case)
        self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}, Terraform {FLOOR}, the Terraform tests.")
        self.assertEqual(PYTHON_CHECKS + terraform_checks() + TERRAFORM_TESTS, case.calls())
        self.assertIn("check: build, tests, smoke test and gateway image skipped: no .NET source changed.",
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

    def environment(self, mask: str) -> list[str]:
        """The two lines the full check's log opens with (#230): the bash that runs it, by path and
        by version as that bash reports it, and the file-mode mask as `umask` prints it."""
        return [f"check: shell: {BASH}, bash {bash_full_version()}", f"check: file-mode mask: {mask}"]

    def test_the_log_opens_with_the_shell_and_the_file_mode_mask(self):
        # #230: a lane's "already red on the fork point" is compared with main by its environment,
        # so the log names it before any check has run or said anything.
        case = Case(self, change="docs/notes.md")
        result = self.full(case)
        self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}.")
        self.assertEqual(self.environment("0022"), result.stdout.splitlines()[:2])

    def test_a_mask_that_is_not_the_default_is_printed_as_it_is(self):
        # An agent session's 077, the mask #205's lane had: printed, not normalised, and not judged,
        # since the file modes check judges the files themselves.
        for mask, printed in ((0o077, "0077"), (0o027, "0027"), (0o002, "0002")):
            with self.subTest(mask=printed):
                case = Case(self, change="docs/notes.md")
                result = case.run("--full", "--port", PORT, umask=mask)
                self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}.")
                self.assertEqual(self.environment(printed), result.stdout.splitlines()[:2])

    def test_the_environment_is_logged_when_the_first_check_fails(self):
        # Printed before the author email, the first thing the check refuses, so a log that stops
        # there still says which shell and mask it stopped under.
        case = Case(self, change="docs/notes.md")
        case.git("config", "--unset", "user.email", cwd=case.work)
        result = self.full(case)
        self.assertVerdict(result, 1, "check: fail: git config user.email is unset.")
        self.assertEqual(self.environment("0022"), result.stdout.splitlines())
        self.assertEqual([], case.calls())

    def test_the_hook_does_not_log_the_environment(self):
        # The hook's lines are #68's, unchanged; HookTests asserts them whole.
        case = Case(self, change="docs/notes.md")
        result = case.run(hook=True, umask=0o077)
        self.assertEqual(0, result.status, result.stderr)
        self.assertNotIn("shell:", result.stdout + result.stderr)
        self.assertNotIn("mask", result.stdout + result.stderr)

    def test_failures_end_on_the_verdict_and_stop_there(self):
        cases = {
            "the build": ({"dotnet": "build"}, "src/App/App.cs", "check: fail: the solution does not build.",
                          PYTHON_CHECKS + BUILD),
            "the tests": ({"dotnet": "test"}, "src/App/App.cs", "check: fail: the tests failed.",
                          PYTHON_CHECKS + BUILD + TESTS),
            "the smoke test": ({"smoke": "Debug"}, "src/App/App.cs", "check: fail: the smoke test failed.",
                               PYTHON_CHECKS + BUILD + TESTS + SMOKE),
            "the gateway image": ({"image": "*"}, "src/App/App.cs",
                                  "check: fail: the gateway image failed to build or to start as its "
                                  "runtime user.", PYTHON_CHECKS + BUILD + TESTS + SMOKE + IMAGE),
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

    def test_an_owner_only_file_fails_the_verdict_with_the_fix(self):
        case = Case(self, change="src/App/App.cs")
        (case.work / "src" / "App" / "App.cs").chmod(0o600)
        result = self.full(case)
        self.assertVerdict(result, 1, "check: fail: a tracked file only its owner can read is a file the "
                                      "gateway's container cannot read; run chmod go+r on it.")
        self.assertIn("src/App/App.cs", result.stderr.splitlines())
        self.assertEqual(PYTHON_CHECKS, case.calls())

    def test_the_file_modes_fail_rather_than_pass_when_they_cannot_be_read(self):
        # Without xargs nothing is judged, and an empty list is what a clean tree gives: the check
        # must say it judged nothing rather than pass (#205's review).
        case = Case(self, change="docs/notes.md")
        (case.bin / "xargs").unlink()
        result = self.full(case)
        self.assertVerdict(result, 1, "check: fail: the file modes were not judged: git ls-files or "
                                      "xargs failed.")

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
            f"check: pass: {FULL_RAN}, the build, the tests, the smoke test, the gateway image, Terraform {FLOOR}, "
            "the Terraform tests.",
            result.last_line(result.stdout))
        self.assertEqual(PYTHON_CHECKS + BUILD + TESTS + SMOKE + IMAGE + terraform_checks() + TERRAFORM_TESTS,
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


# Scripts and their test companions, for CompanionTests: one of each shape the repository has. A
# script's companion is `<its name, extension dropped and hyphens as underscores>_tests.py` beside
# it, as tools/land.sh's is tools/land_tests.py and .github/path-ownership.py's is
# .github/path_ownership_tests.py, or its named test file `<its name>-tests.py`, as
# .github/plan-secrecy.py's is .github/plan-secrecy-tests.py. tools/check_tests.py is here so a case
# can show it never runs.
SCRIPTS = {
    "tools/thing.sh": "# a script\n",
    "tools/thing_tests.py": "# its companion\n",
    "tools/other.py": "# another\n",
    "tools/other_tests.py": "# its companion\n",
    ".github/gizmo.py": "# a check\n",
    ".github/gizmo_tests.py": "# its companion\n",
    ".github/two-words.py": "# a hyphenated check\n",
    ".github/two_words_tests.py": "# its companion\n",
    ".github/named.py": "# a check with a named test file\n",
    ".github/named-tests.py": "# its named test file\n",
    "tools/scripts/deep.sh": "# a script a level down\n",
    "tools/scripts/deep_tests.py": "# its companion\n",
    "tools/lonely.sh": "# a script with no companion\n",
    "tools/check_tests.py": "# the check's own companion\n",
}


# Libraries for LibraryTests: files under tools/ or .github/ with no companion of their own, which a
# script or a companion names, as tools/scripts/deploy.sh sources tools/scripts/cell-common.sh, which
# names tools/scripts/cell_documents.py, and the cell companions load tools/scripts/cell_doubles.py
# (#237). Over SCRIPTS, so deep.sh names its library.
LIBRARIES = {
    "tools/scripts/deep.sh": '. "$(dirname "$0")/lib.sh"\n',
    "tools/scripts/lib.sh": "python3 \"$here/lib2.py\"\n",
    "tools/scripts/lib2.py": "# a library only lib.sh names\n",
    "tools/scripts/deep_tests.py": "doubles = load('tools/scripts/doubles.py')\n",
    "tools/scripts/doubles.py": "# a library only the companion names\n",
    "tools/scripts/mylib.sh": "# a library with a longer name\n",
    "tools/other.py": "# reads tools/scripts/mylib.sh\n",
}


def companion(path: str) -> list[str]:
    """The call that runs the companion at `path`."""
    return ["python3", "-I", path]


def companions_run(case: Case) -> list[list[str]]:
    """The calls of the case that ran a test companion, in order."""
    return [call for call in case.calls() if call[0] == "python3" and re.search(r"[_-]tests\.py$", call[-1])]


class CompanionTests(CheckTestCase):
    """#134: under --full, the test companion of each script a change touches in tools/ or .github/
    runs, and its failure fails the verdict; an untouched script's companion does not run; and
    tools/check_tests.py, which runs this check, never does."""

    def full(self, case: Case, *extra: str) -> Result:
        return case.run("--full", "--port", PORT, *extra)

    def test_a_touched_scripts_companion_runs_and_the_verdict_names_it(self):
        case = Case(self, extra=SCRIPTS, change="tools/thing.sh")
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        # Only the touched script's: every other companion here belongs to an untouched script.
        self.assertEqual(PYTHON_CHECKS + [companion("tools/thing_tests.py")], case.calls())
        self.assertEqual(f"check: pass: {FULL_RAN}, the test companion tools/thing_tests.py.",
                         result.last_line(result.stdout))

    def test_each_shape_of_script_reaches_its_own_companion(self):
        for change, expected in (
            ("tools/other.py", "tools/other_tests.py"),
            (".github/gizmo.py", ".github/gizmo_tests.py"),
            (".github/two-words.py", ".github/two_words_tests.py"),
            (".github/named.py", ".github/named-tests.py"),
            ("tools/scripts/deep.sh", "tools/scripts/deep_tests.py"),
            # A change to the companion alone runs it too.
            ("tools/thing_tests.py", "tools/thing_tests.py"),
            (".github/named-tests.py", ".github/named-tests.py"),
        ):
            with self.subTest(change=change):
                case = Case(self, extra=SCRIPTS, change=change)
                result = self.full(case)
                self.assertEqual(0, result.status, result.stderr)
                self.assertEqual(PYTHON_CHECKS + [companion(expected)], case.calls())
                self.assertEqual(f"check: pass: {FULL_RAN}, the test companion {expected}.",
                                 result.last_line(result.stdout))

    def test_a_companion_that_fails_fails_the_verdict(self):
        case = Case(self, extra=SCRIPTS, change=("tools/thing.sh", "tools/other.py"),
                    fails_on={"python3": "tools/other_tests.py"})
        result = self.full(case)
        self.assertEqual(1, result.status, result.stdout)
        self.assertEqual("check: fail: the test companion tools/other_tests.py failed.",
                         result.last_line(result.stderr))
        # Stopped at the first failure, in the companions' order.
        self.assertEqual(PYTHON_CHECKS + [companion("tools/other_tests.py")], case.calls())

    def test_several_touched_scripts_run_each_companion_once(self):
        case = Case(self, extra=SCRIPTS,
                    change=("tools/thing.sh", "tools/thing_tests.py", ".github/gizmo.py"))
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(PYTHON_CHECKS + [companion(".github/gizmo_tests.py"),
                                          companion("tools/thing_tests.py")], case.calls())
        self.assertEqual(f"check: pass: {FULL_RAN}, the test companions .github/gizmo_tests.py, "
                         "tools/thing_tests.py.", result.last_line(result.stdout))

    def test_no_companion_runs_for_a_change_that_touches_no_script_with_one(self):
        for change in ("docs/notes.md", "tools/lonely.sh", "src/App/App.cs", "infra/roots/a/main.tf"):
            with self.subTest(change=change):
                case = Case(self, extra=SCRIPTS, change=change)
                result = self.full(case)
                self.assertEqual(0, result.status, result.stderr)
                self.assertEqual([], companions_run(case))
                self.assertIn("check: test companions skipped: no script with one in tools/ or "
                              ".github/, nor a file one names, changed.", result.stdout.splitlines())
                self.assertNotIn("test companion", result.last_line(result.stdout))

    def test_the_checks_own_companion_never_runs_from_the_check(self):
        # It runs this script, so the check running it would recurse; it stays CI's.
        for mode in ((), ("--both-halves",)):
            with self.subTest(mode=mode or "the diff's"):
                case = Case(self, extra=SCRIPTS, change="tools/check_tests.py")
                result = self.full(case, *mode)
                self.assertEqual(0, result.status, result.stderr)
                self.assertNotIn(companion("tools/check_tests.py"), case.calls())
                # Said, so a green line on a change to the check does not read as having tested it.
                self.assertIn("check: tools/check_tests.py not run: it runs this check, so CI runs it.",
                              result.stdout.splitlines())
                self.assertTrue(result.last_line(result.stdout).endswith(
                    ", but not tools/check_tests.py, which is CI's."), result.stdout)
        case = Case(self, extra=SCRIPTS, change="tools/check_tests.py")
        self.assertIn("check: test companions skipped: no other script with one in tools/ or .github/, "
                      "nor a file one names, changed.", self.full(case).stdout.splitlines())

    def test_the_hook_runs_no_companion(self):
        # The hook's checks are unchanged (#68); the companions are the full check's and CI's.
        case = Case(self, extra=SCRIPTS, change="tools/thing.sh")
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(PYTHON_CHECKS, case.calls())
        self.assertLines([HOOK_BUILD_SKIPPED, HOOK_TERRAFORM_SKIPPED, f"pre-push: {HOOK_RAN} pass."],
                         result.stdout)

    def test_both_halves_runs_every_companion_but_the_checks_own(self):
        # --both-halves promises a run with no skips, which on main's empty diff is its point.
        case = Case(self, extra=SCRIPTS)
        result = self.full(case, "--both-halves")
        self.assertEqual(0, result.status, result.stderr)
        expected = [".github/gizmo_tests.py", ".github/named-tests.py", ".github/two_words_tests.py",
                    "tools/other_tests.py", "tools/scripts/deep_tests.py", "tools/thing_tests.py"]
        self.assertEqual([companion(path) for path in expected],
                         companions_run(case))
        self.assertNotIn("skipped", result.stdout)
        self.assertTrue(result.last_line(result.stdout).endswith(
            ", the test companions " + ", ".join(expected) + ", but not tools/check_tests.py, which is CI's."))

    def test_every_companion_in_the_repository_is_reached_from_its_script(self):
        # The repository's own layout, in stand-ins: every tracked file beside a real companion,
        # empty. A change to every one that is not a companion must run every real companion, or a
        # companion is named so that a change to its script would never run it.
        tracked = subprocess.run(["git", "-C", str(ROOT), "ls-files", "--", "tools", ".github"],
                                 capture_output=True, text=True, check=True).stdout.split()
        real = [path for path in tracked if re.search(r"[_-]tests\.py$", path)]
        self.assertIn("tools/land_tests.py", real)
        folders = {path.rsplit("/", 1)[0] for path in real}
        # The files Case links to the real ones, which a case must not change; .github/touches.sh is
        # the only one with a companion, and its rule is tools/land.sh's.
        linked = {".github/touches.sh", "tools/check.sh", "tools/terraform-floor.sh", "tools/smoke.sh"}
        beside = [path for path in tracked
                  if path.rsplit("/", 1)[0] in folders and path not in linked]
        scripts = tuple(path for path in beside if path not in real)
        case = Case(self, extra={path: "" for path in beside}, change=scripts)
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        # tools/image_build_tests.py judges .dockerignore and the Dockerfiles, which no script's
        # name names, so no change but its own earns it here; CI's documents job runs it on every
        # pull request (#204). It was in this list from #204's merge, and the case failed on it.
        expected = sorted(set(real) - {"tools/check_tests.py", ".github/touches_tests.py",
                                       "tools/image_build_tests.py"})
        self.assertEqual([companion(path) for path in expected],
                         companions_run(case))


class LibraryTests(CheckTestCase):
    """#237: under --full, a change to a library -- a file under tools/ or .github/ with no
    companion of its own -- runs the companion of every script or companion that names it, followed
    through the libraries that name it in turn. On 2026-10-20 a broken redaction in
    tools/scripts/cell-common.sh passed the full check green, because the change touched no script
    with a companion (#186 P6). A file with a companion of its own ends the chain: its companion is
    what tests it."""

    def full(self, case: Case, *extra: str) -> Result:
        return case.run("--full", "--port", PORT, *extra)

    def scripts(self, **override: str) -> dict[str, str]:
        return {**SCRIPTS, **LIBRARIES, **override}

    def test_a_library_a_script_names_runs_that_scripts_companion(self):
        case = Case(self, extra=self.scripts(), change="tools/scripts/lib.sh")
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(PYTHON_CHECKS + [companion("tools/scripts/deep_tests.py")], case.calls())
        self.assertEqual(f"check: pass: {FULL_RAN}, the test companion tools/scripts/deep_tests.py.",
                         result.last_line(result.stdout))

    def test_a_library_two_names_away_runs_it_too(self):
        # lib2.py is named only by lib.sh, which deep.sh names: cell_documents.py's shape, which
        # tools/scripts/cell-common.sh names and the cell scripts source.
        case = Case(self, extra=self.scripts(), change="tools/scripts/lib2.py")
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([companion("tools/scripts/deep_tests.py")], companions_run(case))

    def test_a_library_a_companion_names_runs_that_companion(self):
        case = Case(self, extra=self.scripts(), change="tools/scripts/doubles.py")
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([companion("tools/scripts/deep_tests.py")], companions_run(case))

    def test_a_failing_companion_a_library_reaches_fails_the_verdict(self):
        # The committed break in a redaction: the companion that runs it fails, and so does the check.
        case = Case(self, extra=self.scripts(), change="tools/scripts/lib2.py",
                    fails_on={"python3": "tools/scripts/deep_tests.py"})
        result = self.full(case)
        self.assertEqual(1, result.status, result.stdout)
        self.assertEqual("check: fail: the test companion tools/scripts/deep_tests.py failed.",
                         result.last_line(result.stderr))

    def test_a_file_with_its_own_companion_ends_the_chain(self):
        # thing.sh names deep.sh, and other.py names thing.sh. A change to deep.sh runs deep.sh's
        # companion and thing.sh's, whose script names it; other.py's does not run, since thing.sh
        # has a companion of its own and the chain stops there. Without the stop, a change to
        # almost any script reached through tools/check.sh or the workflow ran 24 of the 30 real
        # companions.
        case = Case(self, extra=self.scripts(**{
            "tools/thing.sh": "# runs tools/scripts/deep.sh\n",
            "tools/other.py": "# runs tools/thing.sh\n",
        }), change="tools/scripts/deep.sh")
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([companion("tools/scripts/deep_tests.py"), companion("tools/thing_tests.py")],
                         companions_run(case))

    def test_a_companion_passes_nothing_on(self):
        # A changed companion runs itself alone, whoever names it: .github/workflows/ci.yml names
        # every companion CI runs. And a file that names a companion earns nothing from its library.
        case = Case(self, extra=self.scripts(**{".github/gizmo.py": "# runs tools/scripts/deep_tests.py\n"}),
                    change=("tools/scripts/deep_tests.py",))
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([companion("tools/scripts/deep_tests.py")], companions_run(case))
        case = Case(self, extra=self.scripts(**{".github/gizmo.py": "# runs tools/scripts/deep_tests.py\n"}),
                    change=("tools/scripts/doubles.py",))
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([companion("tools/scripts/deep_tests.py")], companions_run(case))

    def test_a_name_inside_a_longer_name_is_not_a_use(self):
        # other.py names mylib.sh, not lib.sh; check.sh is not image-check.sh.
        case = Case(self, extra=self.scripts(), change="tools/scripts/lib.sh")
        self.full(case)
        self.assertNotIn(companion("tools/other_tests.py"), case.calls())
        case = Case(self, extra=self.scripts(), change="tools/scripts/mylib.sh")
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([companion("tools/other_tests.py")], companions_run(case))

    def test_a_library_no_one_names_runs_nothing(self):
        case = Case(self, extra=self.scripts(), change="tools/lonely.sh")
        result = self.full(case)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([], companions_run(case))
        self.assertIn("check: test companions skipped: no script with one in tools/ or .github/, nor "
                      "a file one names, changed.", result.stdout.splitlines())

    def test_the_hook_runs_no_companion_for_a_library(self):
        case = Case(self, extra=self.scripts(), change="tools/scripts/lib.sh")
        result = case.run(hook=True)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual(PYTHON_CHECKS, case.calls())

    def test_both_halves_runs_the_same_companions_with_libraries_present(self):
        case = Case(self, extra=self.scripts())
        result = self.full(case, "--both-halves")
        self.assertEqual(0, result.status, result.stderr)
        expected = [".github/gizmo_tests.py", ".github/named-tests.py", ".github/two_words_tests.py",
                    "tools/other_tests.py", "tools/scripts/deep_tests.py", "tools/thing_tests.py"]
        self.assertEqual([companion(path) for path in expected], companions_run(case))


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
    runner.main()
