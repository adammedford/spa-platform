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
same messages).

Run: python3 -I tools/check_tests.py
"""

from __future__ import annotations

import os
import pathlib
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "check.sh"
HOOK = ROOT / ".githooks" / "pre-push"
TOUCHES = ROOT / ".github" / "touches.sh"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"

# The real tools the script and the classifier use, reached from the case's bin directory, which
# is the whole of the case's PATH. find runs dirname itself, through PATH.
TOOLS = ("git", "find", "sort", "head", "dirname", "basename", "grep")

PORT = "5299"

# A double: records its name and arguments as one call in the shared sequence, then fails when the
# case named this call, and otherwise prints a line (dotnet, terraform, smoke) or nothing (python3).
DOUBLE = """#!/bin/bash
log={log}
name={name}
fails_on={fails_on}
loud={loud}
n=0
while [ -e "$log/call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$name" "$@" > "$log/call-$n"
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

# The hook's lines, as the hook before #68 printed them.
HOOK_RAN = "A-05, the steering documents, every ticket citation, the ticket numbers, the roadmap, the artifacts"
HOOK_BUILD_SKIPPED = "pre-push: build skipped: no .NET source changed."
HOOK_TERRAFORM_SKIPPED = "pre-push: Terraform skipped: nothing under infra/ changed."
HOOK_FIX = "pre-push: fix it, or push with --no-verify if you mean it."

PYTHON_CHECKS = [
    ["python3", "-I", ".github/path-ownership.py", "origin/main", "HEAD"],
    ["python3", "-I", ".github/steering.py"],
    ["python3", "-I", "tools/roadmap.py", "--check"],
]
BUILD = [["dotnet", "build", "--nologo", "--verbosity", "quiet"]]
TESTS = [["dotnet", "test", "--solution", "SpaPlatform.slnx", "--no-build", "--ignore-exit-code", "8"]]
SMOKE = [["smoke", "Debug", PORT]]


def terraform_checks() -> list[list[str]]:
    calls = [["terraform", "fmt", "-check", "-recursive", "infra"]]
    for root in ROOTS:
        calls += [["terraform", f"-chdir={root}", "init", "-backend=false", "-input=false"],
                  ["terraform", f"-chdir={root}", "validate"]]
    return calls


TERRAFORM_TESTS = [["terraform", f"-chdir={directory}", "test"] for directory in TEST_DIRS]


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
        self.output = completed.stdout.decode("utf-8", "surrogateescape")

    def last_line(self, stream: str) -> str:
        lines = stream.splitlines()
        return lines[-1] if lines else ""


class Case:
    """One throwaway directory: a bare origin, a clone of it on a branch named `task`, and a bin
    directory holding the doubles and a way to each real tool, which is the case's whole PATH."""

    def __init__(self, test: unittest.TestCase, *, change: str | None = None,
                 fails_on: dict[str, str] | None = None, missing: tuple[str, ...] = (),
                 email: str = "check-tests@users.noreply.github.com", origin: bool = True):
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
                self.write_executable(self.bin / name, self.double(name, fails_on.get(name, ""), loud))

        self.work = self.dir / "work"
        if origin:
            self.origin = self.dir / "origin.git"
            self.git("init", "-q", "--bare", "-b", "main", str(self.origin), cwd=self.dir)
            self.git("clone", "-q", str(self.origin), str(self.work), cwd=self.dir)
        else:
            self.git("init", "-q", "-b", "main", str(self.work), cwd=self.dir)
        self.git("config", "user.name", "Check Tests", cwd=self.work)
        self.git("config", "user.email", email, cwd=self.work)
        for name, text in FILES.items():
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
        self.write_executable(self.work / "tools" / "smoke.sh",
                              self.double("smoke", fails_on.get("smoke", ""), "yes"))

        if change:
            path = self.work / change
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(path.read_text() + "changed\n" if path.exists() else "new\n")
            self.git("add", change, cwd=self.work)
            self.git("commit", "-q", "-m", f"Change {change}", cwd=self.work)

    def double(self, name: str, fails_on: str, loud: str) -> str:
        return DOUBLE.format(log=shlex.quote(str(self.log)), name=shlex.quote(name),
                             fails_on=shlex.quote(fails_on), loud=shlex.quote(loud))

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
        self.assertLines([HOOK_BUILD_SKIPPED, f"pre-push: {HOOK_RAN}, Terraform pass."], result.stdout)
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
        self.assertLines([HOOK_BUILD_SKIPPED], result.stdout)
        self.assertLines(["terraform double failed: -chdir=infra/modules/m validate",
                          "pre-push: terraform validate failed in infra/modules/m.", HOOK_FIX],
                         result.stderr)

    def test_a_steering_check_that_fails(self):
        case = Case(self, change="docs/notes.md", fails_on={"python3": ".github/steering.py"})
        result = case.run(hook=True)
        self.assertEqual(1, result.status)
        self.assertLines(["python3 double failed: -I .github/steering.py",
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
        self.assertLines([f"pre-push: {HOOK_RAN}, the build, Terraform pass."], result.stdout)
        self.assertEqual(PYTHON_CHECKS[1:] + BUILD + terraform_checks(), case.calls())

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
        self.assertIn("check: Terraform and its tests skipped: nothing under infra/ changed.",
                      result.stdout)
        self.assertNotIn("pre-push", result.stdout + result.stderr)

    def test_a_change_under_infra_runs_terraform_test_where_there_are_tests(self):
        case = Case(self, change="infra/roots/a/main.tf")
        result = self.full(case)
        self.assertVerdict(result, 0, f"check: pass: {FULL_RAN}, Terraform, the Terraform tests.")
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
