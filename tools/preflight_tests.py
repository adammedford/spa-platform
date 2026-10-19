#!/usr/bin/env python3
"""Test companion for tools/preflight.sh: the AWS project guard and the parse it reads its file
with, the script's own directory under an exported CDPATH, the two rows a landing needs, and the
Terraform floor's rows. Owner: integrator.

Built in #69 for the floor's rows and extended in #96, in the form D-3 in docs/dependencies.md
records and tools/land_tests.py set (#66).

**What runs is the script itself.** preflight finds its neighbours from its own path, `$0`:
tools/aws-project.env, tools/aws-project.local.env and tools/terraform-floor.sh beside it, and
.terraform-version one level up. So each case lays out a throwaway directory like the repository
and copies the tracked tools/preflight.sh into it whole, from its current bytes, beside a whole-file
copy of tools/terraform-floor.sh and the env files the case crafts. A whole-file copy counts as the
script itself for a script that reads beside itself; an extracted fragment does not, and neither
does a switch added so a test could point the script at another env file, which would give the
guard a second way to read the wrong file (archived overnight ticket 69, Q5, accepted on #66). A
copy and not a link: preflight reads beside the path it was started through, and 9ec9aa1 left that
"alone deliberately", which is not a promise for a case to rest on. Each case starts the copy as
AGENTS.md starts the script, `tools/preflight.sh` from the top level, unless it is about another
way in.

**The interpreter is named.** Each case runs `/bin/bash`, so a Mac with Homebrew's bash first on
PATH still tests the 3.2 macOS ships, which the script's header targets. The companion prints that
bash's version before its first case, and when SCRIPT_TESTS_REQUIRE_BASH is set (CI's macOS job
sets it to 3.2) a different version fails the run rather than skipping it.

**A constructed environment, inheriting nothing.** PATH is the case's bin directory alone; HOME is
inside the case's throwaway directory; GIT_CONFIG_NOSYSTEM=1, GIT_CONFIG_GLOBAL names an empty file,
and LC_ALL=C. A case that needs another variable sets it on purpose: AWS_PROFILE, AWS_REGION,
AWS_DEFAULT_REGION, the static keys or CDPATH. Inherited, any of them changes what a row says.

**Nothing reaches the network or AWS.** The bin directory holds the few real tools preflight reads
its answers with and a double for each tool it asks about: aws, terraform, git, dotnet, openssl, gh
and python3. It holds no curl, so the nuget.org row checks nothing and says so. Each double records
its calls and answers only the ones preflight makes; any other call is recorded and exits 97. Every
run asserts that row, that no double met a call it does not answer, and that preflight wrote
nothing to standard error, where a file it could not source or a tool missing from PATH would show.

**A ready machine by default.** A case starts from a workstation with every day-one tool, an aws
double whose profiles include the intended one, configured for the intended Region and signed in to
the account tools/aws-project.local.env pins, and a whole-file copy of the tracked
tools/aws-project.env, where preflight answers "Day one: ready." and exits 0. Each case takes away
or changes the thing it is about, so the exit status and the count of missing items can be
asserted: a missing tool that is reported but not counted leaves "Day one: ready" standing, which
is how tools/land.sh's pointer to preflight failed until #96, when gh was an `info` row.

**The parse is compared with the shell, not with a copy of it.** tools/aws-project.env documents
itself as sourceable and preflight reads it with sed, so the two must agree, or the remedy preflight
prints would not satisfy the check it came from (7a364fe). Each form 7a364fe, eb39fec and 9ec9aa1
record is written into a crafted env file, sourced by the same /bin/bash with `set -a; .`, and
compared with what preflight's own rows say it read. CRLF is the one recorded divergence: sourcing
keeps the carriage return and the parse drops it, so the guard fails closed, and its case holds it
there.

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
PROJECT_ENV = ROOT / "tools" / "aws-project.env"
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

# The real tools preflight reads its answers with. Every other tool it asks about is a double or
# absent.
TOOLS = ("uname", "sed", "head", "tail", "tr", "cut", "awk", "grep", "sort", "dirname")

# What the documented recipe, `set -a; . tools/aws-project.env; set +a`, leaves in the two keys the
# guard reads, printed NUL-separated so a carriage return or a space survives the trip.
SOURCE = 'set -a; . "$1"; set +a; printf "%s\\0%s\\0" "${AWS_PROFILE-}" "${AWS_REGION-}"'


def sourced(path: pathlib.Path, env: dict[str, str]) -> tuple[str, str]:
    """AWS_PROFILE and AWS_REGION as /bin/bash leaves them after `set -a; . <path>; set +a`."""
    completed = subprocess.run([BASH, "-c", SOURCE, "bash", str(path)], env=env,
                               stdin=subprocess.DEVNULL, capture_output=True, timeout=30)
    profile, region, _ = completed.stdout.decode("utf-8", "surrogateescape").split("\0")
    return profile, region


# The profile and the Region the tracked file names, read the way the recipe reads them, so the
# guard's cases expect whatever the file says and move with it.
INTENDED_PROFILE, INTENDED_REGION = sourced(PROJECT_ENV, {"PATH": "/usr/bin:/bin", "LC_ALL": "C"})
INTENDED = {"AWS_PROFILE": INTENDED_PROFILE}

# Another project's profile, which the case's ~/.aws/config also holds; a profile it does not hold;
# and a Region the tracked file does not name.
OTHER_PROFILE = "another-project"
NO_SUCH_PROFILE = "no-such-profile"
OTHER_REGION = "eu-west-1" if INTENDED_REGION != "eu-west-1" else "us-west-2"

# Account ids from the AWS documentation's examples, never a real account's, and static keys that
# are canaries: no shape a secret scanner reads as a key, and a value no row may print.
ACCOUNT = "111122223333"
FOREIGN_ACCOUNT = "444455556666"
ACCESS_KEY = "preflight-tests-access-key-id"
SECRET_KEY = "preflight-tests-secret-access-key"

# The python3 that macOS's developer tools ship, which docs/dependencies.md's row "Python 3.9 or
# later" names as the one offered.
PYTHON = "3.9.6"
GH_SIGNED_IN = "signed in"
GH_SIGNED_OUT = "signed out"

# A case's project_env by default: a whole-file copy of the tracked tools/aws-project.env.
SHIPPED = object()


def row(status: str, label: str, detail: str) -> str:
    """A row as preflight prints it: printf '  %-8s %-24s %s\\n'."""
    return f"  {status:<8} {label:<24} {detail}"


# With no curl on PATH the nuget.org row checks nothing and says so; every run asserts it.
NUGET_UNCHECKED = row("info", "nuget.org", "curl not found, so this was not checked")


def write_executable(path: pathlib.Path, text: str):
    path.write_text(text)
    path.chmod(0o755)


def double(name: str, log: pathlib.Path, header: str, body: str) -> str:
    """A double's script: its header, a record of each call's arguments as the next numbered file
    in the case's log, and its answers, which end by calling unexpected for anything else."""
    return ("#!/bin/bash\n"
            f"# A double for {name}, written by tools/preflight_tests.py for one case.\n"
            + header +
            f"log={shlex.quote(str(log))}\n"
            "n=0\n"
            f'while [ -e "$log/{name}-$n" ]; do n=$((n + 1)); done\n'
            f"printf '%s\\0' \"$@\" > \"$log/{name}-$n\"\n"
            "unexpected() {\n"
            '  : > "$log/unexpected"\n'
            f"  printf '{name} double: unexpected call, recorded as {name}-%s\\n' \"$n\" >&2\n"
            "  exit 97\n"
            "}\n"
            + body)


def answering(call: str, stdout: str, stderr: str = "", status: int = 0) -> str:
    """A double's answers when preflight asks it one thing."""
    return (f'if [ "$*" = {shlex.quote(call)} ]; then\n'
            f"  printf '%s' {shlex.quote(stdout)}\n"
            f"  printf '%s' {shlex.quote(stderr)} >&2\n"
            f"  exit {status}\n"
            "fi\n"
            "unexpected\n")


AWS_HEADER = """#
# It answers the three calls tools/preflight.sh makes, in the shapes the AWS CLI's documentation
# gives them, from the profiles in the case's ~/.aws/config, which $profiles lists one a line: the
# name, the Region it is configured for or a dash, and the account it signs in to.
#
#   aws --version
#       https://docs.aws.amazon.com/cli/latest/userguide/cli-chap-welcome.html
#   aws sts get-caller-identity --query Account --output text --cli-connect-timeout 5 --cli-read-timeout 10
#       https://docs.aws.amazon.com/cli/latest/reference/sts/get-caller-identity.html
#   aws configure get region
#       https://docs.aws.amazon.com/cli/latest/reference/configure/get.html
#
# The profile is AWS_PROFILE, or default when it is unset. `configure get` reads the profile's
# config alone, never AWS_REGION or AWS_DEFAULT_REGION, and prints nothing and exits 1 for a value
# it does not find. Static keys in the environment outrank a profile's own credentials
# (https://docs.aws.amazon.com/cli/latest/userguide/cli-chap-authentication.html), so with
# AWS_ACCESS_KEY_ID set the caller is the keys' account, whatever the profile.
"""

AWS_BODY = r"""
profile=${AWS_PROFILE:-default}
found=""
region=""
account=""
while read -r name configured owner; do
  if [ "$name" = "$profile" ]; then found=yes; region=$configured; account=$owner; fi
done < "$profiles"
[ "$region" != - ] || region=""

case "$*" in
  --version)
    printf 'aws-cli/%s Python/3.13.7 Darwin/25.0.0 source/arm64\n' "$version" ;;
  "sts get-caller-identity --query Account --output text --cli-connect-timeout 5 --cli-read-timeout 10")
    if [ -n "${AWS_ACCESS_KEY_ID:-}" ]; then printf '%s\n' "$keys_account"; exit 0; fi
    if [ -z "$found" ]; then printf 'The config profile (%s) could not be found\n' "$profile" >&2; exit 255; fi
    printf '%s\n' "$account" ;;
  "configure get region")
    [ -n "$region" ] || exit 1
    printf '%s\n' "$region" ;;
  *) unexpected ;;
esac
"""

GH_HEADER = """#
# tools/preflight.sh asks it one thing, `gh auth status`, which exits 0 when every account gh knows
# is signed in, and 1, with the problem on standard error, when one is not:
# https://cli.github.com/manual/gh_auth_status
"""

GH_BODY = """if [ "$*" = "auth status" ]; then
  [ "$signed_in" = yes ] && exit 0
  printf 'You are not logged into any GitHub hosts. To log in, run: gh auth login\\n' >&2
  exit 1
fi
unexpected
"""


class Result:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout.decode("utf-8", "surrogateescape")
        self.stderr = completed.stderr.decode("utf-8", "surrogateescape")
        # Split on newlines alone: the CRLF case's row carries a carriage return inside it, which
        # str.splitlines would take for a line break.
        self.lines = self.stdout.split("\n")
        if self.lines and self.lines[-1] == "":
            self.lines.pop()


class Case:
    """One throwaway directory laid out like the repository: whole-file copies of the script and
    the floor's helper beside the env files the case crafts, a bin directory that is the case's
    whole PATH, a HOME, and a log of every call a double answered. The machine is ready unless the
    case says otherwise; each keyword takes one thing away (None) or changes it."""

    def __init__(self, test: unittest.TestCase, *, repo: str = "repo", project_env: object = SHIPPED,
                 local_env: str | None = f"AWS_ACCOUNT_ID={ACCOUNT}\n",
                 path_terraform: str | None = LATER, floor_terraform: str | None = FLOOR,
                 floor_file: str = FLOOR + "\n", git: str | None = "2.50.1",
                 dotnet: str | None = "10.0.401", aws: str | None = "2.37.12",
                 openssl: str | None = "LibreSSL 3.3.6", gh: str | None = GH_SIGNED_IN,
                 python3: str | None = PYTHON):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        # Resolved, because bash's pwd reports the physical path when the caller's PWD is not
        # inherited, and macOS's temporary directory sits behind a symbolic link.
        self.dir = pathlib.Path(scratch.name).resolve()
        self.bin = self.dir / "bin"
        self.log = self.dir / "log"
        self.home = self.dir / "home"
        self.repo = self.dir / repo
        self.tools = self.repo / "tools"
        self.gitconfig = self.dir / "gitconfig"
        for directory in (self.bin, self.log, self.home, self.tools):
            directory.mkdir(parents=True)
        self.gitconfig.write_bytes(b"")
        for tool in TOOLS:
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            # A one-line exec of the resolved binary rather than a symbolic link, as in
            # tools/land_tests.py.
            write_executable(self.bin / tool,
                             f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(found))} "$@"\n')

        # The script and the helper it sources, copied whole from their current bytes.
        self.script = self.tools / "preflight.sh"
        shutil.copyfile(SCRIPT, self.script)
        shutil.copyfile(FLOOR_HELPER, self.tools / "terraform-floor.sh")
        (self.repo / ".terraform-version").write_text(floor_file)
        if project_env is SHIPPED:
            shutil.copyfile(PROJECT_ENV, self.tools / "aws-project.env")
        elif project_env is not None:
            self.write_project_env(str(project_env))
        if local_env is not None:
            (self.tools / "aws-project.local.env").write_bytes(local_env.encode())

        # Terraform's first line of `terraform version` is "Terraform v<version>"; nothing else is
        # asked of it here.
        self.path_terraform = self.bin / "terraform"
        if path_terraform is not None:
            write_executable(self.path_terraform, double(
                "terraform", self.log, "", answering("version", f"Terraform v{path_terraform}\non darwin_arm64\n")))
        # Where the install line preflight prints puts the floor, typed from docs/dependencies.md
        # rather than read from the helper both scripts share.
        self.floor_dir = self.home / ".local" / "share" / "terraform" / FLOOR
        if floor_terraform is not None:
            self.floor_dir.mkdir(parents=True)
            write_executable(self.floor_dir / "terraform", double(
                "floor-terraform", self.log, "",
                answering("version", f"Terraform v{floor_terraform}\non darwin_arm64\n")))
        if git is not None:
            # https://git-scm.com/docs/git#Documentation/git.txt---version
            write_executable(self.bin / "git", double(
                "git", self.log, "", answering("--version", f"git version {git}\n")))
        if dotnet is not None:
            # One SDK a line, its version and then its directory in brackets:
            # https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet#options
            write_executable(self.bin / "dotnet", double(
                "dotnet", self.log, "",
                answering("--list-sdks", f"{dotnet} [/usr/local/share/dotnet/sdk]\n")))
        if aws is not None:
            profiles = self.dir / "aws-profiles"
            profiles.write_text(f"{INTENDED_PROFILE} {INTENDED_REGION} {ACCOUNT}\n"
                                f"{OTHER_PROFILE} {INTENDED_REGION} {FOREIGN_ACCOUNT}\n")
            write_executable(self.bin / "aws", double(
                "aws", self.log, AWS_HEADER,
                f"version={shlex.quote(aws)}\nprofiles={shlex.quote(str(profiles))}\n"
                f"keys_account={FOREIGN_ACCOUNT}\n" + AWS_BODY))
        if openssl is not None:
            # macOS ships LibreSSL as /usr/bin/openssl, which answers `version` with its name and
            # version on one line.
            write_executable(self.bin / "openssl", double(
                "openssl", self.log, "", answering("version", f"{openssl}\n")))
        if gh is not None:
            write_executable(self.bin / "gh", double(
                "gh", self.log, GH_HEADER,
                f"signed_in={'yes' if gh == GH_SIGNED_IN else 'no'}\n" + GH_BODY))
        if python3 is not None:
            # `python3 --version` prints "Python <version>": https://docs.python.org/3/using/cmdline.html#cmdoption-V
            # An empty version stands for a python3 that reports none, as macOS's /usr/bin/python3
            # does before the developer tools it asks to install are there.
            answer = (answering("--version", f"Python {python3}\n") if python3 else
                      answering("--version", "", "xcode-select: note: No developer tools were found, "
                                "requesting install.\n", 1))
            write_executable(self.bin / "python3", double("python3", self.log, "", answer))

    def write_project_env(self, text: str):
        """Crafts the case's tools/aws-project.env, byte for byte."""
        (self.tools / "aws-project.env").write_bytes(text.encode())

    def env(self, extra: dict[str, str] | None = None) -> dict[str, str]:
        env = {"PATH": str(self.bin), "HOME": str(self.home), "GIT_CONFIG_NOSYSTEM": "1",
               "GIT_CONFIG_GLOBAL": str(self.gitconfig), "LC_ALL": "C"}
        env.update(extra or {})
        return env

    def run(self, *argv: str, cwd: pathlib.Path | None = None,
            env: dict[str, str] | None = None) -> Result:
        """preflight as AGENTS.md starts it, `tools/preflight.sh` from the top level, under
        /bin/bash; `argv` and `cwd` replace that for a case about another way in."""
        command = list(argv) if argv else [BASH, "tools/preflight.sh"]
        completed = subprocess.run(command, cwd=cwd or self.repo, env=self.env(env),
                                   stdin=subprocess.DEVNULL, capture_output=True, timeout=60)
        return Result(completed)

    def source(self) -> tuple[str, str]:
        """What the documented recipe makes of this case's tools/aws-project.env."""
        return sourced(self.tools / "aws-project.env", self.env())

    def calls(self, name: str) -> list[list[str]]:
        found = sorted(self.log.glob(f"{name}-*"), key=lambda path: int(path.name.rsplit("-", 1)[1]))
        return [path.read_bytes().decode("utf-8", "surrogateescape").split("\0")[:-1] for path in found]

    def install_line(self) -> str:
        """The command that installs the floor where tools/check.sh looks for it."""
        return (f"brew install hashicorp/tap/hc-install && mkdir -p {self.floor_dir} && "
                f"hc-install install -version {FLOOR} -path {self.floor_dir} terraform")


def setUpModule():
    full = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout
    print(f"preflight_tests: tools/preflight.sh runs under {BASH} {full}", file=sys.stderr)


class PreflightTestCase(unittest.TestCase):
    def preflight(self, case: Case, *argv: str, cwd: pathlib.Path | None = None,
                  env: dict[str, str] | None = None) -> Result:
        """Runs preflight in `case` and asserts what holds for every run: it wrote nothing to
        standard error and exited 0 or 1, the nuget.org row ran no curl, and no double met a call
        it does not answer."""
        result = case.run(*argv, cwd=cwd, env=env)
        self.assertEqual("", result.stderr, f"preflight wrote to standard error:\n{result.stdout}")
        self.assertIn(result.status, (0, 1), result.stdout)
        self.assertIn(NUGET_UNCHECKED, result.lines, f"the nuget.org row ran curl, or never ran:\n{result.stdout}")
        self.assertFalse((case.log / "unexpected").exists(),
                         f"a double was asked a call it does not answer: {sorted(p.name for p in case.log.iterdir())}")
        return result

    def assertReady(self, result: Result):
        self.assertEqual(0, result.status, result.stdout)
        self.assertRegex((result.lines or [""])[-1], r"^Day one: ready\. [0-9]+ later item\(s\) open\.$")

    def assertMissing(self, result: Result, count: int):
        self.assertEqual(1, result.status, result.stdout)
        self.assertRegex((result.lines or [""])[-1],
                         rf"^Day one: {count} item\(s\) missing\. [0-9]+ later item\(s\) open\.$")


class FloorRowTests(PreflightTestCase):
    """#69's second box: preflight names the floor version and says whether this machine has it."""

    def test_the_floor_installed_in_its_directory(self):
        case = Case(self, path_terraform=LATER, floor_terraform=FLOOR)
        lines = self.preflight(case).lines
        self.assertIn(row("ok", f"Terraform floor {FLOOR}",
                          f"{case.floor_dir / 'terraform'}, which CI runs and tools/check.sh uses"), lines)
        self.assertIn(row("ok", "Terraform", f"{LATER} on PATH"), lines)

    def test_the_floor_not_installed_says_what_the_check_uses_and_how_to_install_it(self):
        case = Case(self, path_terraform=LATER, floor_terraform=None)
        lines = self.preflight(case).lines
        self.assertIn(row("MISSING", f"Terraform floor {FLOOR}",
                          f"not installed, so tools/check.sh runs {LATER} instead. {case.install_line()}"),
                      lines)

    def test_a_terraform_on_path_that_reports_the_floor_is_the_floor(self):
        # A version manager reading .terraform-version, or a machine that simply has the floor.
        case = Case(self, path_terraform=FLOOR, floor_terraform=None)
        lines = self.preflight(case).lines
        self.assertIn(row("ok", f"Terraform floor {FLOOR}",
                          f"{case.path_terraform}, which CI runs and tools/check.sh uses"), lines)

    def test_a_floor_directory_holding_another_version_is_not_the_floor(self):
        case = Case(self, path_terraform=LATER, floor_terraform=OLDER)
        lines = self.preflight(case).lines
        self.assertIn(row("MISSING", f"Terraform floor {FLOOR}",
                          f"not installed, so tools/check.sh runs {LATER} instead. {case.install_line()}"),
                      lines)

    def test_no_terraform_at_all(self):
        case = Case(self, path_terraform=None, floor_terraform=None)
        lines = self.preflight(case).lines
        self.assertIn(row("MISSING", f"Terraform floor {FLOOR}",
                          f"not installed, so tools/check.sh runs no Terraform. {case.install_line()}"),
                      lines)
        self.assertIn(row("MISSING", f"Terraform >= {FLOOR}", "brew install hashicorp/tap/terraform"), lines)

    def test_a_terraform_on_path_older_than_the_floor(self):
        # Every root requires at least the floor, so an apply with it would refuse to start.
        case = Case(self, path_terraform=OLDER, floor_terraform=FLOOR)
        lines = self.preflight(case).lines
        self.assertIn(row("MISSING", f"Terraform >= {FLOOR}",
                          f"found: {OLDER}. brew install hashicorp/tap/terraform"), lines)

    def test_a_file_that_names_no_exact_version(self):
        case = Case(self, path_terraform=LATER, floor_file="latest\n")
        lines = self.preflight(case).lines
        self.assertIn(row("MISSING", "Terraform floor",
                          f"{case.repo / '.terraform-version'} does not name one exact version, such as 1.11.0"),
                      lines)


class OfflineTests(PreflightTestCase):
    def test_no_case_reaches_the_network_or_aws(self):
        # Every run asserts the nuget.org row and that no double met a call it does not answer.
        # This shows the ready machine holds no curl, and that the one aws on its PATH, the double,
        # was asked exactly the three calls preflight makes.
        case = Case(self)
        self.assertIsNone(shutil.which("curl", path=str(case.bin)))
        self.assertEqual(str(case.bin / "aws"), shutil.which("aws", path=str(case.bin)))
        result = self.preflight(case, env=INTENDED)
        self.assertReady(result)
        self.assertEqual([["--version"],
                          ["sts", "get-caller-identity", "--query", "Account", "--output", "text",
                           "--cli-connect-timeout", "5", "--cli-read-timeout", "10"],
                          ["configure", "get", "region"]], case.calls("aws"))


class ProjectGuardTests(PreflightTestCase):
    """The nine cases 7a364fe lists, against a whole-file copy of the tracked tools/aws-project.env.
    The intended profile and the documented recipe pass; the other seven fire, each on the row that
    names what is wrong."""

    def assertRefused(self, result: Result, expected_row: str):
        self.assertEqual(1, result.status, result.stdout)
        self.assertIn(expected_row, result.lines)

    def project_row(self, using: str) -> str:
        return row("MISSING", "AWS project",
                   f"using profile '{using}', expected '{INTENDED_PROFILE}'. export AWS_PROFILE={INTENDED_PROFILE}")

    def region_row(self, resolved: str) -> str:
        return row("MISSING", "AWS Region",
                   f"this shell resolves to '{resolved}', expected '{INTENDED_REGION}'. "
                   "set -a; . tools/aws-project.env; set +a")

    def passing_rows(self) -> list[str]:
        return [row("ok", "AWS credentials", f"account ending {ACCOUNT[-4:]}, region {INTENDED_REGION}"),
                row("ok", "AWS project",
                    f"profile '{INTENDED_PROFILE}', region {INTENDED_REGION}, as tools/aws-project.env names"),
                row("ok", "AWS account", f"ending {ACCOUNT[-4:]}, as tools/aws-project.local.env names")]

    def test_the_tracked_file_names_a_profile_and_a_region(self):
        # Every case below expects what the file says; an empty value would have them expect nothing.
        self.assertTrue(INTENDED_PROFILE, "tools/aws-project.env, sourced, sets no AWS_PROFILE")
        self.assertTrue(INTENDED_REGION, "tools/aws-project.env, sourced, sets no AWS_REGION")
        self.assertNotIn(INTENDED_PROFILE, ("default", OTHER_PROFILE, NO_SUCH_PROFILE))

    def test_the_intended_profile_passes(self):
        case = Case(self)
        result = self.preflight(case, env=INTENDED)
        self.assertReady(result)
        for expected in self.passing_rows():
            self.assertIn(expected, result.lines)

    def test_a_wrong_profile(self):
        case = Case(self)
        result = self.preflight(case, env={"AWS_PROFILE": OTHER_PROFILE})
        self.assertRefused(result, self.project_row(OTHER_PROFILE))

    def test_an_aws_region_override(self):
        # 7a364fe: the check read the profile's Region alone, so an exported AWS_REGION, which the
        # CLI and Terraform read first, passed it while everything was built somewhere else.
        case = Case(self)
        result = self.preflight(case, env={**INTENDED, "AWS_REGION": OTHER_REGION})
        self.assertRefused(result, self.region_row(OTHER_REGION))
        self.assertMissing(result, 1)

    def test_an_aws_default_region_override(self):
        case = Case(self)
        result = self.preflight(case, env={**INTENDED, "AWS_DEFAULT_REGION": OTHER_REGION})
        self.assertRefused(result, self.region_row(OTHER_REGION))
        self.assertMissing(result, 1)

    def test_a_profile_that_does_not_exist(self):
        # 7a364fe: once diagnosed only as "no working credentials", which sent the reader to aws
        # configure when the export they had just run named the wrong profile. Both rows name it.
        case = Case(self)
        result = self.preflight(case, env={"AWS_PROFILE": NO_SUCH_PROFILE})
        self.assertRefused(result, row("MISSING", "AWS credentials",
                                       f"no working credentials for profile '{NO_SUCH_PROFILE}'. "
                                       "aws configure sso, or aws configure"))
        self.assertIn(self.project_row(NO_SUCH_PROFILE), result.lines)

    def test_run_from_another_directory(self):
        # 7a364fe: the file was looked for from the caller's directory, so one directory up with no
        # AWS_PROFILE said "nothing says which project is intended" and "Day one: ready". It is read
        # beside the script now, however the script is reached.
        case = Case(self)
        result = self.preflight(case, BASH, f"{case.repo.name}/tools/preflight.sh", cwd=case.dir)
        self.assertRefused(result, self.project_row("default"))
        elsewhere = case.dir / "elsewhere"
        elsewhere.mkdir()
        result = self.preflight(case, BASH, str(case.script), cwd=elsewhere, env=INTENDED)
        self.assertReady(result)
        self.assertIn(self.passing_rows()[1], result.lines)

    def test_an_absent_file_is_missing(self):
        # 7a364fe: an absent file was reported as `info`, which no exit status counts.
        case = Case(self, project_env=None)
        result = self.preflight(case, env=INTENDED)
        self.assertRefused(result, row("MISSING", "AWS project",
                                       f"{case.tools / 'aws-project.env'} is absent, so nothing says "
                                       "which project is intended"))
        self.assertMissing(result, 1)

    def test_the_documented_recipe_verbatim(self):
        # The file's header documents the recipe and preflight prints it as the remedy for a shell
        # in another Region; they are one line. Run as written in a shell on another profile and
        # Region, it leaves the environment the guard passes. 7a364fe: it once exported
        # SPA_AWS_PROFILE and SPA_AWS_REGION, which nothing reads.
        documented = re.search(r"^#\s+(set -a; .*)$", PROJECT_ENV.read_text(), re.MULTILINE)
        self.assertIsNotNone(documented, "tools/aws-project.env's header documents no `set -a; ...` recipe")
        recipe = documented[1]
        case = Case(self)
        result = self.preflight(case, env={**INTENDED, "AWS_REGION": OTHER_REGION})
        self.assertRefused(result, row("MISSING", "AWS Region",
                                       f"this shell resolves to '{OTHER_REGION}', expected "
                                       f"'{INTENDED_REGION}'. {recipe}"))
        result = self.preflight(case, BASH, "-c", f"{recipe}; exec {BASH} tools/preflight.sh",
                                env={"AWS_PROFILE": OTHER_PROFILE, "AWS_REGION": OTHER_REGION})
        self.assertReady(result)
        for expected in self.passing_rows():
            self.assertIn(expected, result.lines)

    def test_static_keys_beside_the_intended_profile(self):
        # Naming the right profile is not using it: keys in the environment outrank its credentials.
        case = Case(self)
        result = self.preflight(case, env={**INTENDED, "AWS_ACCESS_KEY_ID": ACCESS_KEY,
                                           "AWS_SECRET_ACCESS_KEY": SECRET_KEY})
        self.assertRefused(result, row("MISSING", "AWS project",
                                       f"profile '{INTENDED_PROFILE}' is named, but AWS_ACCESS_KEY_ID in "
                                       "the environment outranks it. unset AWS_ACCESS_KEY_ID "
                                       "AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN"))
        for value in (ACCESS_KEY, SECRET_KEY):
            self.assertNotIn(value, result.stdout + result.stderr, "preflight printed a credential")


# The values the crafted files give the two keys, and the Region a shell holds when a case needs
# preflight to say which Region it read.
FORM_PROFILE = "parse-forms"
FORM_REGION = "ap-south-2"
SHELL_REGION = "region-of-the-shell"

# Every form a line of the env file takes in 7a364fe, eb39fec and 9ec9aa1, as (name, the line for
# key k and value v, what sourcing it yields). 7a364fe's plain, double-quoted, single-quoted,
# trailing-comment, trailing-space, hash-in-value and duplicate-key forms; eb39fec's export prefix;
# 9ec9aa1's two-space and tab indents and the indented export it names, and the `exported` and
# commented lines it says yield nothing. 7a364fe's CRLF form is the one divergence, and has its own
# case.
FORMS = (
    ("plain", "{k}={v}\n", "{v}"),
    ("double-quoted", '{k}="{v}"\n', "{v}"),
    ("single-quoted", "{k}='{v}'\n", "{v}"),
    ("a trailing comment", "{k}={v} # the intended one\n", "{v}"),
    ("trailing whitespace", "{k}={v} \t \n", "{v}"),
    ("a hash inside the value", "{k}=spa#{v}\n", "spa#{v}"),
    ("a duplicate key, the last of which wins", "{k}=first-{v}\n{k}={v}\n", "{v}"),
    ("the export prefix", "export {k}={v}\n", "{v}"),
    ("a two-space indent", "  {k}={v}\n", "{v}"),
    ("a tab indent", "\t{k}={v}\n", "{v}"),
    ("an indented export", "  export {k}={v}\n", "{v}"),
    ("a word that only starts with export", "exported {k}={v}\n", ""),
    ("a commented-out line", "# {k}={v}\n", ""),
)


class ParseTests(PreflightTestCase):
    """The env file as preflight reads it, against what `set -a; .` makes of the same file under the
    same bash. preflight's rows say what it read: a shell on another profile is told which one the
    file expects, and a shell in another Region which Region."""

    def read_profile(self, case: Case) -> str:
        """The AWS_PROFILE preflight read from the case's file, from the row it prints for a shell
        on the default profile, or nothing when that row says the file names none."""
        result = self.preflight(case)
        names_none = row("MISSING", "AWS project",
                         "tools/aws-project.env names no AWS_PROFILE, so nothing says which project is intended")
        expects = re.compile(re.escape(row("MISSING", "AWS project", ""))
                             + r"using profile 'default', expected '(.*)'\. export AWS_PROFILE=.*")
        for line in result.lines:
            if line == names_none:
                return ""
            match = expects.fullmatch(line)
            if match:
                return match[1]
        self.fail(f"no AWS project row says which profile preflight read:\n{result.stdout}")

    def read_region(self, case: Case, profile: str) -> str:
        """The AWS_REGION preflight read from the case's file, from the row it prints for a shell on
        the file's profile and in another Region, or nothing when it compares no Region and the
        project row passes."""
        result = self.preflight(case, env={"AWS_PROFILE": profile, "AWS_REGION": SHELL_REGION})
        compares_none = row("ok", "AWS project",
                            f"profile '{profile}', region {SHELL_REGION}, as tools/aws-project.env names")
        expects = re.compile(re.escape(row("MISSING", "AWS Region", ""))
                             + rf"this shell resolves to '{SHELL_REGION}', expected '(.*)'\. .*")
        for line in result.lines:
            if line == compares_none:
                return ""
            match = expects.fullmatch(line)
            if match:
                return match[1]
        self.fail(f"no AWS Region row says which Region preflight read:\n{result.stdout}")

    # One case for every form of a key, its file rewritten for each: a script run for the first time
    # in a new directory costs macOS several times what a second run does.
    def test_each_form_of_the_profile_reads_as_sourcing_reads_it(self):
        case = Case(self)
        for name, line, yields in FORMS:
            with self.subTest(form=name):
                case.write_project_env(line.format(k="AWS_PROFILE", v=FORM_PROFILE)
                                       + f"AWS_REGION={FORM_REGION}\n")
                profile, _ = case.source()
                self.assertEqual(yields.format(v=FORM_PROFILE), profile, "sourcing reads the form otherwise")
                self.assertEqual(profile, self.read_profile(case),
                                 "preflight reads the profile otherwise than sourcing does")

    def test_each_form_of_the_region_reads_as_sourcing_reads_it(self):
        case = Case(self)
        for name, line, yields in FORMS:
            with self.subTest(form=name):
                case.write_project_env(f"AWS_PROFILE={FORM_PROFILE}\n"
                                       + line.format(k="AWS_REGION", v=FORM_REGION))
                _, region = case.source()
                self.assertEqual(yields.format(v=FORM_REGION), region, "sourcing reads the form otherwise")
                self.assertEqual(region, self.read_region(case, FORM_PROFILE),
                                 "preflight reads the Region otherwise than sourcing does")

    def test_a_crlf_copy_diverges_and_fails_closed(self):
        # 7a364fe recorded this divergence and eb39fec dismissed fixing it: no checkout makes the
        # file CRLF (the next case). A copy contrived by hand is the only way to one. Sourcing it
        # keeps each carriage return, the parse drops them, and the guard refuses the environment
        # sourcing leaves rather than passing it.
        case = Case(self, project_env=f"AWS_PROFILE={FORM_PROFILE}\r\nAWS_REGION={FORM_REGION}\r\n")
        profile, region = case.source()
        self.assertEqual((f"{FORM_PROFILE}\r", f"{FORM_REGION}\r"), (profile, region))
        self.assertEqual(FORM_PROFILE, self.read_profile(case))
        self.assertEqual(FORM_REGION, self.read_region(case, FORM_PROFILE))
        result = self.preflight(case, env={"AWS_PROFILE": profile, "AWS_REGION": region})
        self.assertEqual(1, result.status, result.stdout)
        self.assertIn(row("MISSING", "AWS project", f"using profile '{profile}', expected '{FORM_PROFILE}'. "
                                                    f"export AWS_PROFILE={FORM_PROFILE}"), result.lines)

    def test_no_checkout_makes_the_file_crlf(self):
        # The premise of the case above, read from the repository's attributes rather than assumed:
        # .gitattributes gives tools/aws-project.env eol=lf, so git writes it with LF everywhere.
        with tempfile.TemporaryDirectory() as home:
            completed = subprocess.run(
                ["git", "-C", str(ROOT), "check-attr", "eol", "--", "tools/aws-project.env"],
                env={"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": home,
                     "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull, "LC_ALL": "C"},
                capture_output=True, text=True, check=True, timeout=30)
        self.assertEqual("tools/aws-project.env: eol: lf\n", completed.stdout)


class ScriptDirectoryTests(PreflightTestCase):
    """preflight finds the files beside it through `cd "$(dirname "$0")"`. An exported CDPATH sends
    that `cd` to another directory of the same name, and `cd` prints the directory it found, so
    every path built from it names another file over two lines: the defect #66 fixed in
    tools/land.sh. CDPATH is looked up only for a relative name that does not start with ./ or ../,
    so the cases start the script as AGENTS.md does, `tools/preflight.sh` from the top level."""

    def assertFoundItsOwnFiles(self, case: Case, result: Result):
        self.assertReady(result)
        self.assertIn(row("ok", f"Terraform floor {FLOOR}",
                          f"{case.floor_dir / 'terraform'}, which CI runs and tools/check.sh uses"), result.lines)
        self.assertIn(row("ok", "AWS project",
                          f"profile '{INTENDED_PROFILE}', region {INTENDED_REGION}, as tools/aws-project.env names"),
                      result.lines)
        self.assertIn(row("ok", "AWS account", f"ending {ACCOUNT[-4:]}, as tools/aws-project.local.env names"),
                      result.lines)

    def decoy(self, directory: pathlib.Path):
        """Env files of another project, where a wrong turn would find them."""
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "aws-project.env").write_text("AWS_PROFILE=decoy-project\nAWS_REGION=decoy-region\n")
        (directory / "aws-project.local.env").write_text(f"AWS_ACCOUNT_ID={FOREIGN_ACCOUNT}\n")

    def test_an_exported_cdpath_holding_another_tools_directory(self):
        case = Case(self)
        self.decoy(case.dir / "decoy" / "tools")
        result = self.preflight(case, env={**INTENDED, "CDPATH": str(case.dir / "decoy")})
        self.assertFoundItsOwnFiles(case, result)

    def test_an_exported_cdpath_that_finds_the_right_directory_still_prints_it(self):
        # CDPATH=. is a common setting, and bash prints a directory found through any non-empty
        # entry, this one included.
        case = Case(self)
        result = self.preflight(case, env={**INTENDED, "CDPATH": "."})
        self.assertFoundItsOwnFiles(case, result)

    def test_a_path_starting_with_a_hyphen(self):
        # dirname and cd read a leading hyphen as an option unless `--` ends their options. A failed
        # dirname leaves cd in the caller's directory, where a decoy waits.
        case = Case(self, repo="-repo")
        self.decoy(case.dir)
        result = self.preflight(case, BASH, "--", "-repo/tools/preflight.sh", cwd=case.dir, env=INTENDED)
        self.assertFoundItsOwnFiles(case, result)


class LandingToolRowTests(PreflightTestCase):
    """gh and python3, which tools/land.sh refuses to start without; its gh refusal sends the reader
    here (#96). Each is a day-one row that counts: missing alone, it holds "Day one: ready"."""

    # docs/dependencies.md's row: Python 3.9 or later.
    PYTHON_ROW = "Python 3.9 or later"

    def test_gh_signed_in(self):
        case = Case(self)
        result = self.preflight(case, env=INTENDED)
        self.assertReady(result)
        self.assertIn(row("ok", "GitHub CLI", "signed in"), result.lines)

    def test_gh_missing(self):
        case = Case(self, gh=None)
        result = self.preflight(case, env=INTENDED)
        self.assertMissing(result, 1)
        self.assertIn(row("MISSING", "GitHub CLI", "brew install gh, then gh auth login"), result.lines)

    def test_gh_installed_but_signed_out(self):
        # Every gh call tools/land.sh makes needs a sign-in, and so does the tracker.
        case = Case(self, gh=GH_SIGNED_OUT)
        result = self.preflight(case, env=INTENDED)
        self.assertMissing(result, 1)
        self.assertIn(row("MISSING", "GitHub CLI", "installed, not signed in. gh auth login"), result.lines)

    def test_python3_at_the_floor(self):
        case = Case(self)
        result = self.preflight(case, env=INTENDED)
        self.assertReady(result)
        self.assertIn(row("ok", self.PYTHON_ROW, PYTHON), result.lines)

    def test_python3_missing(self):
        case = Case(self, python3=None)
        result = self.preflight(case, env=INTENDED)
        self.assertMissing(result, 1)
        self.assertIn(row("MISSING", self.PYTHON_ROW, "xcode-select --install"), result.lines)

    def test_python3_older_than_the_floor(self):
        case = Case(self, python3="3.8.20")
        result = self.preflight(case, env=INTENDED)
        self.assertMissing(result, 1)
        self.assertIn(row("MISSING", self.PYTHON_ROW, "found: 3.8.20. xcode-select --install"), result.lines)

    def test_python3_that_reports_no_version(self):
        case = Case(self, python3="")
        result = self.preflight(case, env=INTENDED)
        self.assertMissing(result, 1)
        self.assertIn(row("MISSING", self.PYTHON_ROW, "found: unknown. xcode-select --install"), result.lines)


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
