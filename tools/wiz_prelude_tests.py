#!/usr/bin/env python3
"""Test companion for tools/wiz_prelude.sh, the wiz skill's prelude. Owner: integrator.

Run: python3 -I tools/wiz_prelude_tests.py

Built in #1010. Every Bash call of a sitting used to repeat the prelude by hand from a block in
.claude/skills/wiz/SKILL.md. In sitting D one call left out its Docker PATH line and
tools/scripts/platform-tfvars.sh failed on docker-credential-desktop, and at midnight UTC its
`date -u +%F` moved the record and bundle paths to the next day mid-sitting. Now every call sources
one file, and the file names the record and the bundle after the date Stage 0 pinned, refusing to
run without one.

**What runs is the script itself.** The prelude finds the repository from git, as the skill's line
does, so each case makes a throwaway git repository, copies the tracked .gitignore,
tools/wiz_prelude.sh, tools/aws-project.env and tools/wiz_redact.py into it whole, and starts /bin/bash there, the shell
the Bash tool runs (#183), which on macOS is 3.2. The environment is built, not inherited: PATH is
the directories of git and python3 and the system's own, HOME is inside the case's directory, and
git reads no configuration of the machine's.

**The skill's text.** `SkillHoldsNoCopy` reads the wiz skill's two files from this checkout and holds
that no line of the prelude's commands is copied into them, so the file stays the one copy.

**Row 2's number (#1088).** D2's `awk` and `grep` count the sitting browser's lines and the skill
writes the tokens they match by hand, so renaming a token left them printing 0 sign-outs on a roll
that signed the browser out, and the row read as failed. `SkillCountsTheWatchsLines` reads the tokens,
the log's name and the watch's flags from tests/e2e/SittingBrowser/, builds a log out of those tokens,
and runs the skill's own two lines over it through the prelude.

**A stop before the watch's first line (#1135).** The watch writes no line for its first interval, so
`SkillCountsTheWatchsLines` runs the confirm's own block and then the two lines over what it leaves:
a count of 0, an earlier attempt's log kept whole, and a log as private as the one the watch creates.

**Stage 0's lines in the record (#1139).** `SkillRecordsStage0sLines` runs the skill's `pmset` and
preflight lines as written, against doubles, and reads the record for each line's command, output and
verdict; the plan list's cases read its first record line the same way. Rule 6 says the record holds
every command and every output, and a bare pipeline keeps that only by echoing itself.

**Which authority the System keychain holds (#1140).** A name matches an older authority left there by
`certs.sh --new-ca`, and the login keychain's copy satisfies `security verify-cert`, so
`SkillMatchesTheAuthorityByFingerprint` runs Stage 0's CA line against a double of `security` that keeps
each keychain apart and the real openssl on certificates made for the case. The expected hashes come
from the certificate's own bytes, not from the line's openssl and sed.

**The plans a confirm reads (#1136).** The route's plan list reads a saved file, and N's plans saved
none. `SkillSavesThePlansItsConfirmsRead` runs N's two blocks and the list through the prelude against
a double of terraform, runs the list over a refresh-only plan's shape, and reads the prose that joins
them: the blocks do not themselves say that the list leads the confirm or that a declined one cleans up.

**The list's verdict (#1137).** A failed read prints no rows, as a plan with no changes does, so the list
ends on a verdict line. The same class runs the line against doubles that fail, print what is not JSON,
print a state file's keys and print a plan terraform marked errored, each of which must name the failure,
and against a plan with no changes, which must not.

**A lost `claude` (#1138).** D10 restores D2's copy and posts on #348, so sitting C has its own way out
and the new session keeps the lost sitting's pin. `SkillGivesEachSittingItsOwnWayOutOfALostClaude` holds
the route's wording, not a run: the route itself has not been run (#1094).

**N's `init` under a guard, and a test user's password (#760).** In the sitting of 2026-11-02 and
2026-11-03 N's `init` exited 1 on "Backend configuration changed" over a cache that lacked a `region`,
and run with `-reconfigure` unguarded it would follow a moved key to empty state. `GuardedInit` runs
`shown guarded_init <root>` as N's step 2 does, on a root made for each case and against a double of
terraform, through every branch the decision on #760 lists: no cached backend, a match, a difference,
a value that cannot be read, and a failed `init`; on each, neither bucket's name reaches the terminal
or the record. The same sitting asked three times for alice's password and got an improvised
`aws ssm get-parameter | pbcopy`. `WizPassword` runs `wiz_password <user>` against doubles of `aws`,
`pbcopy` and `sleep`: the parameter is the one the identity module names, the password reaches the
clipboard and nothing else, the clipboard is emptied after 45 seconds by a job that does not hold the
call's output open, and a failed read touches the clipboard not at all.

Checkout inputs: .claude/skills/wiz/*.md .gitignore infra/modules/idp-cognito/main.tf tests/e2e/Program.cs tests/e2e/SittingBrowser/Observations.cs tests/e2e/SittingBrowser/ObservationLine.cs tests/e2e/SittingBrowser/SittingBrowser.cs tests/e2e/SittingBrowser/SittingCommand.cs
"""
from __future__ import annotations

import datetime
import hashlib
import json
import os
import pathlib
import re
import shutil
import ssl
import stat
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
PRELUDE = ROOT / "tools" / "wiz_prelude.sh"
# .gitignore is the checkout's own, copied whole rather than restated, so the case reads the rule
# that keeps the pin, the record and the bundle out of git: .scratch/sitting/ is tracked, its log/
# is not (CODING_STANDARDS.md, "A guard reads the thing it guards").
COPIED = (".gitignore", "tools/wiz_prelude.sh", "tools/aws-project.env", "tools/wiz_redact.py")
SKILL = ROOT / ".claude" / "skills" / "wiz"
SKILL_FILES = (SKILL / "SKILL.md", SKILL / "c.md", SKILL / "d.md", SKILL / "a-and-b.md")
# The skill a sitting runs by: the main file, then sittings C and D, which #1198 moved beside it.
SKILL_TEXT = "".join(path.read_text() for path in SKILL_FILES[:3])
PIN = ".scratch/sitting/log/date"
SOURCE = ". tools/wiz_prelude.sh"
SITTING_BROWSER = ROOT / "tests" / "e2e" / "SittingBrowser"


def utc_day(offset: int = 0) -> str:
    return (datetime.datetime.now(datetime.timezone.utc).date() + datetime.timedelta(days=offset)).isoformat()


def tool_path() -> str:
    dirs = []
    for tool in ("git", "python3"):
        found = shutil.which(tool)
        if found is None:
            raise RuntimeError(f"{tool} is not on PATH")
        dirs.append(str(pathlib.Path(found).parent))
    dirs += ["/usr/bin", "/bin"]
    return ":".join(dict.fromkeys(dirs))


class Sitting(unittest.TestCase):
    """A throwaway repository laid out as the main checkout, with the prelude copied in."""

    def setUp(self) -> None:
        self.dir = pathlib.Path(tempfile.mkdtemp(prefix="wiz-prelude-")).resolve()
        self.addCleanup(shutil.rmtree, self.dir, True)
        self.repo = self.dir / "repo"
        (self.repo / "tools").mkdir(parents=True)
        for path in COPIED:
            shutil.copy2(ROOT / path, self.repo / path)
        empty = self.dir / "gitconfig"
        empty.write_text("")
        self.env = {
            "PATH": tool_path(),
            "HOME": str(self.dir / "home"),
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": str(empty),
            "LC_ALL": "C",
        }
        (self.dir / "home").mkdir()
        done = subprocess.run(["git", "init", "-q", str(self.repo)], env=self.env, capture_output=True, text=True,
                              timeout=60, check=False)
        self.assertEqual(done.returncode, 0, done.stderr)

    def pin(self, text: str) -> None:
        path = self.repo / PIN
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def bash(self, script: str, cwd: pathlib.Path | None = None) -> subprocess.CompletedProcess:
        return subprocess.run(["/bin/bash", "-c", script], cwd=cwd or self.repo, env=self.env, capture_output=True,
                              text=True, timeout=60, check=False)


class PinnedDate(Sitting):
    def test_a_date_pinned_yesterday_names_the_record_and_the_bundle_not_today(self) -> None:
        yesterday = utc_day(-1)
        self.pin(yesterday + "\n")
        done = self.bash(SOURCE + ' && printf "%s\\n%s\\n" "$R" "$E" && shown echo hello')
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines()[:2],
                         [f".scratch/sitting/log/{yesterday}.log", f".scratch/sitting/log/{yesterday}"])
        log = self.repo / ".scratch" / "sitting" / "log"
        self.assertTrue((log / yesterday).is_dir(), "the bundle directory of the pinned date was not made")
        self.assertEqual((log / f"{yesterday}.log").read_text(), "$ echo hello\nhello\nexit=0\n")
        for day in (utc_day(0), utc_day(1)):
            self.assertFalse((log / day).exists(), f"a bundle directory was made for {day}")
            self.assertFalse((log / f"{day}.log").exists(), f"a record was written for {day}")

    def test_the_prelude_sets_the_profile_region_docker_path_umask_and_spactl(self) -> None:
        self.pin(utc_day(-1) + "\n")
        done = self.bash(SOURCE + ' && printf "%s\\n" "$AWS_PROFILE" "$AWS_REGION" "${PATH%%:*}" "$(umask)"'
                         ' "$(type -t shown)" "$(type -t captured)" "$(type -t spactl)" "${SPACTL-unset}"')
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines(),
                         ["spa-platform", "us-east-2", str(self.dir / "home" / ".docker" / "bin"), "0077",
                          "function", "function", "function", "unset"])

    def test_sitting_browser_runs_the_e2e_project_on_the_sittings_bundle(self) -> None:
        day = utc_day(-1)
        self.pin(day + "\n")
        # dotnet is a function here, so the case reads the words the prelude passes and starts nothing.
        done = self.bash(SOURCE + ' && dotnet() { printf "%s\\n" "$@"; } && sitting_browser login main --app app1')
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines(),
                         ["run", "--project", "tests/e2e", "--", "sitting-browser", "--bundle",
                          f".scratch/sitting/log/{day}", "login", "main", "--app", "app1"])
        # The two words are the entry point's constants: a rename there has to fail here.
        entry_point = (ROOT / "tests" / "e2e" / "Program.cs").read_text()
        self.assertIn('SittingBrowserCommand = "sitting-browser"', entry_point)
        self.assertIn('BundleOption = "--bundle"', entry_point)

    def test_a_call_from_a_subdirectory_runs_at_the_top_level(self) -> None:
        self.pin(utc_day(-1) + "\n")
        (self.repo / "infra").mkdir()
        done = self.bash(". ../tools/wiz_prelude.sh && pwd -P", cwd=self.repo / "infra")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout, f"{self.repo}\n")


class NoPinnedDate(Sitting):
    def assertRefuses(self) -> None:
        done = self.bash(SOURCE + "; echo the call went on")
        self.assertNotEqual(done.returncode, 0)
        self.assertIn("Stage 0", done.stderr)
        self.assertNotIn("the call went on", done.stdout)
        log = self.repo / ".scratch" / "sitting" / "log"
        self.assertFalse(log.exists() and any(p.name != "date" for p in log.iterdir()),
                         "the refused prelude wrote a record or a bundle")

    def test_no_pin_exits_non_zero_naming_stage_0(self) -> None:
        self.assertRefuses()

    def test_a_pin_that_is_not_a_date_exits_non_zero_naming_stage_0(self) -> None:
        for text in ("", "\n", "today\n", "2026-11-4\n", "2026-11-04 extra\n", "2026-11-04\n2026-11-05\n"):
            with self.subTest(pin=text):
                self.pin(text)
                self.assertRefuses()


class StageZeroPins(Sitting):
    def test_pin_writes_todays_utc_date_once_and_the_prelude_reads_it(self) -> None:
        before = utc_day(0)
        done = self.bash("/bin/bash tools/wiz_prelude.sh --pin")
        after = utc_day(0)
        self.assertEqual(done.returncode, 0, done.stderr)
        pinned = (self.repo / PIN).read_text()
        self.assertIn(pinned, (before + "\n", after + "\n"))
        self.assertEqual(done.stdout, f"wiz prelude: the sitting's date is pinned to {pinned.strip()} in {PIN}\n")
        self.assertEqual((self.repo / PIN).stat().st_mode & 0o777, 0o600)
        ignored = subprocess.run(["git", "check-ignore", "-q", PIN], cwd=self.repo, env=self.env, capture_output=True,
                                 text=True, timeout=60, check=False)
        self.assertEqual(ignored.returncode, 0, "the pin is not ignored, so Stage 0's git status would show it")
        later = self.bash(SOURCE + ' && echo "$R"')
        self.assertEqual(later.returncode, 0, later.stderr)
        self.assertEqual(later.stdout, f".scratch/sitting/log/{pinned.strip()}.log\n")

    def test_pin_replaces_an_earlier_sittings_date(self) -> None:
        self.pin("2026-01-01\n")
        done = self.bash("/bin/bash tools/wiz_prelude.sh --pin")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertNotEqual((self.repo / PIN).read_text(), "2026-01-01\n")

    def test_run_without_pin_it_refuses_and_writes_nothing(self) -> None:
        for args in ("", " --pinn", " --pin extra"):
            with self.subTest(args=args):
                done = self.bash("/bin/bash tools/wiz_prelude.sh" + args)
                self.assertEqual(done.returncode, 2, done.stderr)
                self.assertIn("usage", done.stderr)
                self.assertFalse((self.repo / PIN).exists())


class SkillHoldsNoCopy(unittest.TestCase):
    """The skill describes the prelude and sources it; it never repeats its commands (#1010)."""

    # The commands a hand copy of the old block carried, written out here rather than read from the
    # prelude, so a copy is caught even when the prelude changes.
    SIGNATURES = ("date -u", ".docker/bin", "umask 077;", "aws-project.env && set +a", "shown() {", "captured() {",
                  "spactl() {", "sitting_browser() {", "wiz_password() {", "guarded_init() {", "wiz_redact.py --stream; echo", "git check-ignore -q \"$E/x\"")

    def skill_lines(self) -> list[tuple[str, int, str]]:
        return [(path.name, n, line) for path in SKILL_FILES
                for n, line in enumerate(path.read_text().splitlines(), 1)]

    def test_the_skill_holds_none_of_the_old_blocks_commands(self) -> None:
        for name, n, line in self.skill_lines():
            for signature in self.SIGNATURES:
                self.assertNotIn(signature, line, f"{name}:{n} copies the prelude")

    def test_the_skill_holds_no_line_of_the_prelude(self) -> None:
        commands = set()
        for line in PRELUDE.read_text().splitlines():
            code = line.split(" # ")[0].strip()
            if len(code) >= 16 and not code.startswith("#"):
                commands.add(code)
        self.assertGreater(len(commands), 10, "the prelude's commands were not read")
        for name, n, line in self.skill_lines():
            for code in commands:
                self.assertNotIn(code, line, f"{name}:{n} copies the prelude")


class SkillCountsTheWatchsLines(Sitting):
    """D2's lines decide row 2's number, so they are run over a log made of the source's own tokens (#1088)."""

    def setUp(self) -> None:
        super().setUp()
        source = (SITTING_BROWSER / "Observations.cs").read_text()
        self.tokens = dict(re.findall(r'public const string (\w+Token) = "([^"]+)";', source))
        listed = re.search(r"NavigationTokens = \[([^\]]*)\]", source)
        self.assertIsNotNone(listed, "Observations.cs has no NavigationTokens list")
        self.navigation = [self.tokens[name.strip()] for name in listed.group(1).split(",")]
        browser = (SITTING_BROWSER / "SittingBrowser.cs").read_text()
        self.watch_log = re.search(r'WatchLog = "([^"]+)"', browser).group(1)
        self.skill = SKILL_TEXT.splitlines()

    def skill_line(self, start: str, end: str = "") -> str:
        found = [line for line in self.skill if line.startswith(start) and line.endswith(end)]
        self.assertEqual(len(found), 1, f"SKILL.md should hold one line starting {start!r}, ending {end!r}: {found}")
        return found[0]

    def run_over_log(self, tool: str, tokens: list[str]) -> tuple[list[str], list[str]]:
        """The skill's line for the tool, run through the prelude; the log it read and what it printed."""
        line = self.skill_line(f"shown {tool} ", f'"$E/{self.watch_log}"')
        day = utc_day(-1)
        self.pin(day + "\n")
        bundle = self.repo / ".scratch" / "sitting" / "log" / day
        bundle.mkdir(parents=True, exist_ok=True)
        lines = [f"{token:<{self.token_width()}} 2026-11-07T00:00:{n:02}Z main app=app1 revision=7 instance=i-1"
                 for n, token in enumerate(tokens)]
        (bundle / self.watch_log).write_text("".join(item + "\n" for item in lines))
        done = self.bash(f"{SOURCE} && {line}")
        self.assertEqual(done.returncode, 0, done.stderr)
        return lines, done.stdout.splitlines()

    @staticmethod
    def token_width() -> int:
        return int(re.search(r"TokenWidth = (\d+);", (SITTING_BROWSER / "ObservationLine.cs").read_text()).group(1))

    def test_the_awk_counts_a_repeated_prompt_once_and_nothing_else_as_a_sign_out(self) -> None:
        signed_in, prompt = self.tokens["SignedInToken"], self.tokens["LoginPromptToken"]
        others = [token for token in self.navigation if token not in (signed_in, prompt)]
        # Two sign-outs: a prompt repeated after a failed login is one (#1025 Q3), and every other
        # token in between is missing coverage, which neither counts nor ends a sign-out.
        _, printed = self.run_over_log("awk", [signed_in, prompt, prompt, *others, prompt, signed_in, *others, prompt])
        self.assertEqual(printed, ["2", "exit=0"])

    def test_the_awk_prints_0_on_a_roll_with_no_sign_out(self) -> None:
        # The row posts this number, and a line with nothing on it is not one (#1088).
        signed_in, prompt = self.tokens["SignedInToken"], self.tokens["LoginPromptToken"]
        others = [token for token in self.navigation if token not in (signed_in, prompt)]
        _, printed = self.run_over_log("awk", [signed_in, *others, signed_in])
        self.assertEqual(printed, ["0", "exit=0"])

    def test_the_grep_prints_every_navigation_line_but_signed_in(self) -> None:
        signed_in = self.tokens["SignedInToken"]
        self.assertIn(signed_in, self.navigation)
        lines, printed = self.run_over_log("grep", self.navigation)
        self.assertEqual(printed, [line for line in lines if not line.startswith(signed_in + " ")] + ["exit=0"])

    def confirm_block(self) -> str:
        """The fenced block the confirm runs before the watch, as the skill holds it."""
        blocks = [block for block in re.findall(r"^```bash\n(.*?)^```$", SKILL_TEXT, re.M | re.S)
                  if f'mv "$E/{self.watch_log}"' in block]
        self.assertEqual(len(blocks), 1, f"SKILL.md should hold one block setting the log aside: {blocks}")
        return blocks[0]

    def stop_before_the_first_line(self, earlier: str | None = None) -> pathlib.Path:
        """The confirm's block run through the prelude; the bundle it leaves."""
        day = utc_day(-1)
        self.pin(day + "\n")
        bundle = self.repo / ".scratch" / "sitting" / "log" / day
        bundle.mkdir(parents=True, exist_ok=True)
        if earlier is not None:
            (bundle / self.watch_log).write_text(earlier)
        done = self.bash(f"{SOURCE} && {self.confirm_block()}")
        self.assertEqual(done.returncode, 0, done.stderr)
        return bundle

    def fence(self, tool: str) -> list[str]:
        line = self.skill_line(f"shown {tool} ", f'"$E/{self.watch_log}"')
        done = self.bash(f"{SOURCE} && {line}")
        self.assertEqual(done.returncode, 0, done.stderr)
        return done.stdout.splitlines()

    def test_a_stop_before_the_first_line_counts_0_and_lists_nothing(self) -> None:
        self.stop_before_the_first_line()
        self.assertEqual(self.fence("awk"), ["0", "exit=0"])
        # grep's 1 is "no line matched", the answer here; a missing file would be its 2.
        self.assertEqual(self.fence("grep"), ["exit=1"])

    def test_a_stop_before_the_first_line_does_not_read_or_lose_an_earlier_attempts_log(self) -> None:
        earlier = f"{self.tokens['LoginPromptToken']} 2026-11-06T00:00:00Z main app=app1 revision=7 instance=i-1\n"
        bundle = self.stop_before_the_first_line(earlier)
        self.assertEqual(self.fence("awk"), ["0", "exit=0"])
        kept = [path.read_text() for path in bundle.glob(f"{self.watch_log}.*")]
        self.assertEqual(kept, [earlier])

    def test_the_log_the_confirm_leaves_is_as_private_as_the_one_the_watch_creates(self) -> None:
        # UnixCreateMode applies "when a new file is created", so the watch's append would not narrow a
        # wider one (https://learn.microsoft.com/en-us/dotnet/api/system.io.filestreamoptions.unixcreatemode);
        # the block leans on the prelude's umask.
        bundle = self.stop_before_the_first_line()
        self.assertEqual(stat.S_IMODE((bundle / self.watch_log).stat().st_mode), 0o600)

    def test_the_watch_appends_to_the_log_the_confirm_leaves_and_the_block_comes_first(self) -> None:
        browser = (SITTING_BROWSER / "SittingBrowser.cs").read_text()
        # A log that exists already must not refuse the watch: WritePrivateAsync's other mode is CreateNew.
        self.assertIn('Path.Combine(bundle, WatchLog), line + "\\n", append: true', browser)
        self.assertIn("Mode = append ? FileMode.Append : FileMode.CreateNew", browser)
        text = SKILL_TEXT
        self.assertLess(text.index(self.confirm_block()), text.index("shown sitting_browser watch main "))

    def test_a_line_starts_with_its_token_then_a_space_as_both_read_it(self) -> None:
        source = (SITTING_BROWSER / "ObservationLine.cs").read_text()
        self.assertIn("line.Append(token.PadRight(TokenWidth)).Append(' ')", source)

    def test_the_watch_and_unwatch_lines_use_the_commands_verbs_and_flags(self) -> None:
        source = (SITTING_BROWSER / "SittingCommand.cs").read_text()
        verbs = dict(re.findall(r'public const string (Watch|Unwatch) = "([^"]+)";', source))
        self.skill_line(f"shown sitting_browser {verbs['Unwatch']} ")
        watch = self.skill_line(f"shown sitting_browser {verbs['Watch']} ").split(" # ")[0]
        flags = re.findall(r"--[a-z-]+", watch)
        # Without these two the watch neither clears a provider session that hides a sign-out (#1012)
        # nor logs in again after one, so the count reads low.
        for needed in ("--clear-provider", "--relogin"):
            self.assertIn(needed, flags)
        allowed = re.search(r"\bWatch => \(\[([^\]]*)\]", source)
        self.assertIsNotNone(allowed, "SittingCommand.cs has no shape for watch")
        for flag in flags:
            self.assertIn(f'"{flag}"', allowed.group(1), f"watch refuses {flag}")
            self.assertRegex(source, rf'options\.(ContainsKey|GetValueOrDefault)\("{flag}"\)', f"watch ignores {flag}")


class SkillSavesThePlansItsConfirmsRead(Sitting):
    """The route's plan list reads a saved file, so a confirm it leads needs one (#1136)."""

    def setUp(self) -> None:
        super().setUp()
        self.text = SKILL_TEXT
        self.n_section = self.between("**N, from nothing", "**C4, the gate tags.**")

    def between(self, start: str, end: str) -> str:
        found = re.search(re.escape(start) + r".*?(?=" + re.escape(end) + ")", self.text, re.S)
        self.assertIsNotNone(found, f"SKILL.md has nothing from {start!r} to {end!r}")
        return found.group(0)

    def step(self, number: int) -> str:
        """Step n of N's list, its wrapped lines joined."""
        found = re.search(rf"^{number}\. (.*?)(?=^{number + 1}\. )", self.n_section, re.M | re.S)
        self.assertIsNotNone(found, f"N has no step {number}")
        return " ".join(found.group(1).split())

    def plan_list(self) -> str:
        """The route's plan list, the one line of it in the skill."""
        listed = [line.strip() for line in self.text.splitlines()
                  if "; terraform -chdir=<root> show -json <plan> | python3" in line]
        self.assertEqual(len(listed), 1, f"SKILL.md should hold one plan list: {listed}")
        return listed[0]

    def rotation_rule(self) -> str:
        """Secret rotation's rule 2 note, its wrapped lines joined."""
        return " ".join(self.between("- **Rule 2.** Secret rotation's", "The deletion of the old secret").split())

    def saved_file(self, plan_block: str) -> str:
        saved = re.search(r'-out="(\$P/[^"]+)"', plan_block)
        self.assertIsNotNone(saved, f"N's plan saves no file under $P: {plan_block}")
        return saved.group(1)

    def n_blocks(self) -> list[str]:
        blocks = re.findall(r"^```bash\n(.*?)^```$", self.n_section, re.M | re.S)
        self.assertEqual(len(blocks), 2, f"N should hold its saved plan's two blocks: {blocks}")
        return blocks

    def test_ns_saved_plan_reaches_the_list_and_the_apply_reads_the_same_file(self) -> None:
        plan_block, apply_block = (block.replace("<root>", "infra/envs/poc/idp") for block in self.n_blocks())
        saved = self.saved_file(plan_block)
        plan_list = self.plan_list().replace("<root>", "infra/envs/poc/idp").replace("<plan>", f'"{saved}"')
        self.pin(utc_day(-1) + "\n")
        changes = {"planned_values": {}, "resource_changes": [
            {"address": "aws_cognito_user_pool.main", "change": {"actions": ["create"]}},
            {"address": "aws_kept.same", "change": {"actions": ["no-op"]}},
        ]}
        # A double of terraform that writes the file plan is told to, and refuses a file that is not there.
        double = ("terraform() { shift; case $1 in\n"
                  "plan) for a in \"$@\"; do case $a in -out=*) echo saved > \"${a#-out=}\";; esac; done;;\n"
                  f"show) [ -f \"$3\" ] && printf '%s' '{json.dumps(changes)}';;\n"
                  "apply) test -f \"$3\" && echo \"applied $3\";;\n"
                  "esac; }\n")
        done = self.bash(f"{SOURCE}\n{double}{plan_block}{plan_list}\n{apply_block}"
                         'test ! -e "$P" && echo removed\n')
        self.assertEqual(done.returncode, 0, done.stderr)
        printed = done.stdout.splitlines()
        self.assertIn("create aws_cognito_user_pool.main", printed)
        self.assertNotIn("no-op aws_kept.same", printed)
        directory = printed[0]
        self.assertIn(f"applied {directory}/{saved.removeprefix('$P/')}", printed)
        self.assertEqual(printed[-1], "removed")

    def test_ns_saved_plan_keeps_the_file_in_rule_6s_private_directory(self) -> None:
        self.assertIn('P=$(mktemp -d); echo "$P"', self.n_section)

    def test_ns_list_on_the_saved_file_leads_the_confirm_and_a_declined_one_removes_it(self) -> None:
        prose = " ".join(self.n_section.split())
        saved = self.saved_file(self.n_blocks()[0])
        self.assertIn(f"Run the route's plan list on `\"{saved}\"` and ask for the confirm", prose)
        self.assertIn("A declined confirm removes the directory", prose)

    def test_steps_2_and_4_run_the_saved_plan_and_name_no_bare_plan_or_apply(self) -> None:
        for number in (2, 4):
            step = self.step(number)
            self.assertIn("N's saved plan", step, f"step {number}")
            self.assertNotRegex(step, r"`(plan|apply)`", f"step {number} names a plan with no file")

    def test_secret_rotations_plan_saves_a_file_in_the_walks_private_directory(self) -> None:
        rule = self.rotation_rule()
        self.assertRegex(rule, r'shown terraform -chdir=infra/envs/poc/apps plan -refresh-only [^`]*-out="\$RUN/[^"]+"')
        self.assertIn("plan list", rule)

    def test_secret_rotations_list_reads_the_drift_where_a_refresh_only_plan_reports_its_moves(self) -> None:
        self.assertIn("with `resource_drift` in place of `resource_changes`", self.rotation_rule())
        # The shape of terraform 1.16's show -json on a refresh-only plan with one move: no resource_changes.
        plan = {"planned_values": {},
                "resource_drift": [{"address": "aws_ssm_parameter.client_secret", "change": {"actions": ["update"]}}]}
        self.pin(utc_day(-1) + "\n")
        double = f"terraform() {{ printf '%s' '{json.dumps(plan)}'; }}\n"
        as_written = self.plan_list().replace("<root>", "infra/envs/poc/apps").replace("<plan>", "x")
        written = self.bash(f"{SOURCE}\n{double}{as_written}\n")
        self.assertEqual(written.returncode, 0, written.stderr)
        self.assertEqual(written.stdout.splitlines(), [self.VERDICT_READ],
                         "the line as written reads a refresh-only plan's moves")
        swapped = as_written.replace("resource_changes", "resource_drift")
        done = self.bash(f"{SOURCE}\n{double}{swapped}\n")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines(), ["update aws_ssm_parameter.client_secret", self.VERDICT_READ])

    VERDICT_READ = "plan list show exit=0 filter exit=0"

    def run_list(self, double: str) -> tuple[subprocess.CompletedProcess, str]:
        """The list as the skill holds it, against a double of terraform; what it did and the record's text."""
        self.pin(utc_day(-1) + "\n")
        line = self.plan_list().replace("<root>", "infra/envs/poc/apps").replace("<plan>", "x")
        done = self.bash(f"{SOURCE}\n{double}\n{line}\n")
        return done, (self.repo / PIN).with_name(f"{utc_day(-1)}.log").read_text()

    def test_a_failed_show_prints_no_rows_and_a_verdict_naming_it(self) -> None:
        done, record = self.run_list("terraform() { echo 'Error: no such file' >&2; return 3; }")
        self.assertEqual(done.stdout.splitlines(), ["plan list show exit=3 filter exit=1"], done.stderr)
        self.assertEqual(record.splitlines()[1:], ["plan list show exit=3 filter exit=1"])

    def test_output_that_is_not_json_is_the_filters_failure(self) -> None:
        done, _ = self.run_list("terraform() { echo 'not json'; }")
        self.assertEqual(done.stdout.splitlines(), ["plan list show exit=0 filter exit=1"], done.stderr)

    def test_a_state_file_read_as_the_plan_fails_and_leaves_its_values_out_of_the_chat_and_the_record(self) -> None:
        # The keys of terraform 1.16's show -json on a state file, which it reads as readily as a plan.
        state = {"format_version": "1.0", "terraform_version": "1.16.5", "values": {"canary": "canary-secret"}}
        done, record = self.run_list(f"terraform() {{ printf '%s' '{json.dumps(state)}'; }}")
        self.assertEqual(done.stdout.splitlines(), ["plan list show exit=0 filter exit=1"], done.stderr)
        for where in (done.stdout, done.stderr, record):
            self.assertNotIn("canary-secret", where)

    def test_a_plan_terraform_marked_errored_fails_and_lists_none_of_its_partial_changes(self) -> None:
        # terraform 1.16 saves the file of a plan that failed, with these keys, and show -json reads it with exit 0.
        plan = {"planned_values": {}, "errored": True, "complete": False, "resource_changes": [
            {"address": "aws_a.b", "change": {"actions": ["create"]}},
        ]}
        done, record = self.run_list(f"terraform() {{ printf '%s' '{json.dumps(plan)}'; }}")
        self.assertEqual(done.stdout.splitlines(), ["plan list show exit=0 filter exit=2"], done.stderr)
        self.assertEqual(record.splitlines()[1:], ["plan list show exit=0 filter exit=2"])

    def test_a_read_plan_lists_its_changes_and_the_record_holds_them_with_the_verdict(self) -> None:
        plan = {"planned_values": {}, "resource_changes": [
            {"address": "aws_a.b", "change": {"actions": ["delete", "create"]}},
            {"address": "aws_kept.same", "change": {"actions": ["no-op"]}},
        ]}
        done, record = self.run_list(f"terraform() {{ printf '%s' '{json.dumps(plan)}'; }}")
        self.assertEqual(done.stdout.splitlines(), ["delete,create aws_a.b", self.VERDICT_READ], done.stderr)
        self.assertEqual(record.splitlines()[1:], ["delete,create aws_a.b", self.VERDICT_READ])

    def test_the_record_opens_on_the_command_as_it_ran_and_the_chat_is_not_given_it(self) -> None:
        plan = {"planned_values": {}, "resource_changes": []}
        self.pin(utc_day(-1) + "\n")
        line = self.plan_list().replace("<root>", "infra/envs/poc/apps").replace("<plan>", '"$P/n.tfplan"')
        double = f"P=/private/dir\nterraform() {{ echo \"$*\" >&2; printf '%s' '{json.dumps(plan)}'; }}"
        done = self.bash(f"{SOURCE}\n{double}\n{line}\n")
        record = (self.repo / PIN).with_name(f"{utc_day(-1)}.log").read_text().splitlines()
        self.assertEqual(done.stdout.splitlines(), [self.VERDICT_READ], done.stderr)
        self.assertRegex(record[0], r"^\$ terraform -chdir=infra/envs/poc/apps show -json /private/dir/n\.tfplan "
                                    r"\| python3 -I -c 'import json,sys; .*'$")
        self.assertIn("-chdir=infra/envs/poc/apps show -json /private/dir/n.tfplan", done.stderr)

    def test_a_plan_with_no_changes_is_an_empty_list_under_a_passing_verdict(self) -> None:
        plan = {"planned_values": {}, "resource_changes": [{"address": "aws_kept.same", "change": {"actions": ["no-op"]}}]}
        done, _ = self.run_list(f"terraform() {{ printf '%s' '{json.dumps(plan)}'; }}")
        self.assertEqual(done.stdout.splitlines(), [self.VERDICT_READ], done.stderr)

    def test_the_route_makes_any_other_verdict_a_stop(self) -> None:
        self.assertIn("# both 0, or stop:", self.plan_list())
        route = " ".join(self.between("- **Plans (#1090 Q7).**", "- **A lost `claude`").split())
        self.assertIn("any other verdict is a rule 7 stop (#1137)", route)


class SkillRecordsStage0sLines(Sitting):
    """Rule 6's record holds Stage 0's two pipelines and their verdicts, as it holds a `shown` line's (#1139).

    Run as the skill holds them, under the prelude, against doubles of what they read, so no case
    depends on this machine's sleep setting or its preflight.
    """

    def setUp(self) -> None:
        super().setUp()
        self.skill = SKILL_TEXT.splitlines()
        self.pin(utc_day(-1) + "\n")

    def run_line(self, marker: str, double: str = "") -> tuple[subprocess.CompletedProcess, list[str]]:
        """The one skill line naming the pipeline; what it did and the record's lines."""
        found = [line for line in self.skill if marker in line]
        self.assertEqual(len(found), 1, f"SKILL.md should hold one line with {marker!r}: {found}")
        done = self.bash(f"{SOURCE}\n{double}\n{found[0]}\n")
        return done, (self.repo / PIN).with_name(f"{utc_day(-1)}.log").read_text().splitlines()

    def pmset(self, row: str) -> tuple[subprocess.CompletedProcess, list[str]]:
        double = f"pmset() {{ printf '%s\\n' 'Currently in use:' '{row}' ' hibernatemode 3'; }}"
        return self.run_line("; pmset -g | awk", double)

    def preflight(self, row: str, code: int) -> tuple[subprocess.CompletedProcess, list[str]]:
        script = self.repo / "tools" / "preflight.sh"
        script.write_text(f"#!/bin/bash\nprintf '%s\\n' 'ok    AWS account' '{row}'\nexit {code}\n")
        script.chmod(0o755)
        return self.run_line("; tools/preflight.sh | awk")

    def test_the_sleep_line_records_its_command_its_row_and_its_verdict(self) -> None:
        done, record = self.pmset(" sleep                0 (sleep prevented by Claude)")
        self.assertEqual(done.stdout.splitlines(), [" sleep                0 (sleep prevented by Claude)", "sleep exit=0"],
                         done.stderr)
        self.assertEqual(len(record), 3, record)
        self.assertRegex(record[0], r"""^\$ pmset -g \| awk '\$1=="sleep" \{.*\}'$""")
        self.assertEqual(record[1:], [" sleep                0 (sleep prevented by Claude)", "sleep exit=0"])

    def test_system_sleep_on_is_a_failing_verdict_in_the_record(self) -> None:
        done, record = self.pmset(" sleep                1")
        self.assertEqual(done.stdout.splitlines()[-1], "sleep exit=1", done.stderr)
        self.assertEqual(record[-1], "sleep exit=1")

    def test_no_sleep_row_is_a_failing_verdict_in_the_record(self) -> None:
        done, record = self.pmset(" standby              1")
        self.assertEqual(done.stdout.splitlines(), ["sleep exit=1"], done.stderr)
        self.assertEqual(record[1:], ["sleep exit=1"])

    def test_the_preflight_line_records_its_command_its_rows_and_its_verdict(self) -> None:
        row = "ok    Container engine  docker server 29"
        done, record = self.preflight(row, 0)
        self.assertEqual(done.stdout.splitlines(), ["ok    AWS account", row, "preflight exit=0 engine exit=0"], done.stderr)
        self.assertEqual(len(record), 4, record)
        self.assertRegex(record[0], r"""^\$ tools/preflight\.sh \| awk '\{print\} /Container engine/ .*'$""")
        self.assertEqual(record[1:], ["ok    AWS account", row, "preflight exit=0 engine exit=0"])

    def test_the_preflight_verdict_keeps_each_exit_apart_in_the_record(self) -> None:
        done, record = self.preflight("later Container engine  no engine", 3)
        self.assertEqual(done.stdout.splitlines()[-1], "preflight exit=3 engine exit=1", done.stderr)
        self.assertEqual(record[-1], "preflight exit=3 engine exit=1")

    def test_the_awk_program_recorded_is_the_one_that_ran(self) -> None:
        # The recorded text comes from the variable the pipeline runs, so a double of awk can compare them.
        double = ("awk() { echo \"$1\" >&2; command awk \"$1\"; }\n"
                  "pmset() { echo ' sleep 0'; }")
        done, record = self.run_line("; pmset -g | awk", double)
        self.assertEqual(record[0], f"$ pmset -g | awk '{done.stderr.strip()}'")


class SkillMatchesTheAuthorityByFingerprint(Sitting):
    """Stage 0 passes only on the current authority in the System keychain, not on its name (#1140)."""

    SYSTEM = "/Library/Keychains/System.keychain"
    LOGIN = "~/Library/Keychains/login.keychain-db"
    CERT = ".certs/spa-platform-poc-ca.pem"

    @classmethod
    def setUpClass(cls) -> None:
        super().setUpClass()
        made = pathlib.Path(tempfile.mkdtemp(prefix="wiz-ca-"))
        cls.addClassCleanup(shutil.rmtree, made, True)
        cls.pem = {}
        cls.sha = {}
        for label in ("current", "stale"):
            subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                            "-subj", "/CN=SPA Platform poc Local CA", "-keyout", str(made / "k"),
                            "-out", str(made / label)], capture_output=True, timeout=60, check=True)
            cls.pem[label] = (made / label).read_text()
            cls.sha[label] = hashlib.sha256(ssl.PEM_cert_to_DER_cert(cls.pem[label])).hexdigest().upper()

    def setUp(self) -> None:
        super().setUp()
        self.env["PATH"] = self.env["PATH"] + ":" + str(pathlib.Path(shutil.which("openssl")).parent)
        self.pin(utc_day(-1) + "\n")
        self.cert = self.repo / self.CERT
        self.cert.parent.mkdir()
        self.cert.write_text(self.pem["current"])

    def double(self, system: tuple[str, ...], login: tuple[str, ...] = ()) -> str:
        """`security find-certificate` as its manual has it: -a for every match, -Z for the hashes."""
        rows = {self.SYSTEM: " ".join(self.sha[c] for c in system), self.LOGIN: " ".join(self.sha[c] for c in login)}
        return f"""security() {{
  local all="" hashes="" name="" keychain="" rows="" found="" hash
  shift
  while [ $# -gt 0 ]; do
    case "$1" in -a) all=1 ;; -Z) hashes=1 ;; -c) name="$2"; shift ;; *) keychain="$1" ;; esac
    shift
  done
  if [ "$name" = "SPA Platform poc Local CA" ]; then
    case "$keychain" in
      {self.SYSTEM}) rows="{rows[self.SYSTEM]}" ;;
      "") rows="{rows[self.SYSTEM]} {rows[self.LOGIN]}" ;;
      *) rows="{rows[self.LOGIN]}" ;;
    esac
  fi
  for hash in $rows; do
    found=1
    [ -z "$hashes" ] || printf 'SHA-256 hash: %s\\nSHA-1 hash: 0000\\n' "$hash"
    printf 'keychain: "%s"\\n    "labl"<blob>="%s"\\n' "$keychain" "$name"
    [ -n "$all" ] || break
  done
  [ -n "$found" ] || {{ echo 'security: The specified item could not be found in the keychain.' >&2; return 44; }}
}}"""

    def run_line(self, double: str) -> tuple[subprocess.CompletedProcess, list[str]]:
        found = [line for line in SKILL_TEXT.splitlines() if "openssl x509 -in" in line]
        self.assertEqual(len(found), 1, f"SKILL.md should hold one CA line: {found}")
        done = self.bash(f"{SOURCE}\n{double}\n{found[0]}\n")
        return done, (self.repo / PIN).with_name(f"{utc_day(-1)}.log").read_text().splitlines()

    def verdict(self, done: subprocess.CompletedProcess) -> str:
        return done.stdout.splitlines()[-1] if done.stdout else f"no output: {done.stderr}"

    def test_the_current_authority_in_the_system_keychain_passes(self) -> None:
        done, _ = self.run_line(self.double(("current",)))
        self.assertEqual(self.verdict(done), "ca lookup exit=0 match exit=0")

    def test_a_stale_authority_of_the_same_name_is_a_failing_verdict(self) -> None:
        done, record = self.run_line(self.double(("stale",)))
        self.assertEqual(self.verdict(done), "ca lookup exit=0 match exit=1")
        self.assertEqual(record[-1], "ca lookup exit=0 match exit=1")

    def test_the_current_authority_found_first_beside_a_stale_one_passes(self) -> None:
        done, _ = self.run_line(self.double(("current", "stale")))
        self.assertEqual(self.verdict(done), "ca lookup exit=0 match exit=0")

    def test_the_current_authority_found_after_a_stale_one_passes(self) -> None:
        done, _ = self.run_line(self.double(("stale", "current")))
        self.assertEqual(self.verdict(done), "ca lookup exit=0 match exit=0")

    def test_the_login_keychains_copy_does_not_satisfy_the_check(self) -> None:
        done, _ = self.run_line(self.double(("stale",), login=("current",)))
        self.assertEqual(self.verdict(done), "ca lookup exit=0 match exit=1")

    def test_no_authority_of_that_name_keeps_each_exit_apart(self) -> None:
        done, record = self.run_line(self.double(()))
        self.assertEqual(self.verdict(done), "ca lookup exit=44 match exit=1")
        self.assertEqual(record[-1], "ca lookup exit=44 match exit=1")

    def test_no_certificate_file_is_a_failing_verdict(self) -> None:
        self.cert.unlink()
        done, record = self.run_line(self.double(("current",)))
        self.assertEqual(self.verdict(done), "ca lookup exit=0 match exit=1")
        self.assertIn("CA file SHA-256: none", done.stdout.splitlines())
        self.assertGreater(record.index("CA file SHA-256: none"), 1, "openssl's own error stays in the record")

    def test_a_certificate_file_that_does_not_parse_is_a_failing_verdict(self) -> None:
        self.cert.write_text("not a certificate\n")
        done, _ = self.run_line(self.double(("current",)))
        self.assertEqual(self.verdict(done), "ca lookup exit=0 match exit=1")

    def test_the_record_holds_both_commands_every_hash_and_the_verdict(self) -> None:
        done, record = self.run_line(self.double(("stale", "current")))
        self.assertEqual(record[0], f"$ openssl x509 -in {self.CERT} -noout -fingerprint -sha256")
        self.assertEqual(len(record), 7, record)
        self.assertRegex(record[1], r"Fingerprint=")
        self.assertEqual(record[2], f"CA file SHA-256: {self.sha['current']}")
        self.assertIn(f'security find-certificate -a -c "SPA Platform poc Local CA" -Z {self.SYSTEM}', record[3])
        self.assertEqual(record[4:], [f"SHA-256 hash: {self.sha['stale']}", f"SHA-256 hash: {self.sha['current']}",
                                      "ca lookup exit=0 match exit=0"])
        self.assertEqual(done.stdout.splitlines(), [record[2]] + record[4:], done.stderr)


class SkillGivesEachSittingItsOwnWayOutOfALostClaude(unittest.TestCase):
    """D10 restores D2's copy and posts on #348, so sitting C has its own way out (#1138)."""

    def setUp(self) -> None:
        text = SKILL_TEXT
        found = re.search(r"- \*\*A lost `claude`.*?(?=- \*\*AWS Settings)", text, re.S)
        self.assertIsNotNone(found, "SKILL.md's route has no lost-claude item")
        self.item = " ".join(found.group(0).split())

    def sitting_c(self) -> str:
        found = re.search(r"\*\*Sitting C never offers D10 \(#1138\)\.\*\*.*", self.item)
        self.assertIsNotNone(found, f"the item gives sitting C no way out: {self.item}")
        return found.group(0)

    def test_only_sitting_d_is_offered_d10(self) -> None:
        self.assertIn("**Sitting D offers D10**", self.item)
        self.assertNotIn("A rule 7 stop that offers D10", self.item)

    def test_sitting_c_asks_c5s_question_and_posts_on_its_own_tickets(self) -> None:
        way_out = self.sitting_c()
        self.assertIn("C5's question", way_out)
        for ticket in ("#242", "#63"):
            self.assertIn(ticket, way_out)

    def test_sitting_c_restores_nothing_and_posts_nothing_on_d_s_ticket(self) -> None:
        way_out = self.sitting_c()
        self.assertIn("Nothing is restored", way_out)
        self.assertNotIn("cp -p", way_out)
        self.assertEqual(way_out.count("#348"), 1, way_out)
        self.assertIn("#348 is not its ticket", way_out)

    def test_sitting_c_reads_what_stands_and_posts_the_cut_stage_as_failed(self) -> None:
        way_out = self.sitting_c()
        self.assertIn("runs Stage 0's reads from the renewal on", way_out)
        self.assertIn("posts the stage that was cut short as failed", way_out)
        # C4 and C5 post on no ticket of their own, and the item's range runs to C5.
        self.assertIn("C4 and C5 have none", way_out)

    def test_a_destroy_first_reads_what_the_lost_session_left_running(self) -> None:
        # R, T and N run in the foreground, and what a kill does to those is as unknown as the background's.
        way_out = self.sitting_c()
        for running in ("`deploy.sh`", "`rollback.sh`", "`teardown.sh`", "`terraform`", "suite"):
            self.assertIn(running, way_out.partition("A destroy runs as T does")[0])
        self.assertIn("in the background or not, is still running", way_out)

    def test_the_new_session_keeps_the_lost_sittings_pin(self) -> None:
        self.assertIn("runs no `--pin`", self.item)

class GuardedInit(Sitting):
    """N's step 2 runs a root's `init` under the guard the decision on #760 gives, as `shown guarded_init <root>`."""

    ROOT_DIR = "infra/envs/poc/idp"
    KEY = "poc/idp/terraform.tfstate"
    MOVED = "poc/moved/terraform.tfstate"
    BUCKET = "spa-platform-poc-tfstate-111122223333"
    OTHER = "spa-platform-poc-tfstate-444455556666"
    # A double of terraform that names each call and exits as the case says.
    DOUBLE = 'terraform() { echo "terraform $*"; return "${TF_EXIT:-0}"; }'
    PLAIN = f"terraform -chdir={ROOT_DIR} init -backend-config=backend.hcl"
    RECONFIGURE = f"terraform -chdir={ROOT_DIR} init -reconfigure -backend-config=backend.hcl"

    def setUp(self) -> None:
        super().setUp()
        self.pin(utc_day(-1) + "\n")
        self.root = self.repo / self.ROOT_DIR
        self.root.mkdir(parents=True)
        self.versions(self.KEY)

    def versions(self, key: str) -> None:
        (self.root / "versions.tf").write_text(
            'terraform {\n  required_version = ">= 1.11.0"\n\n  backend "s3" {\n'
            f'    key          = "{key}"\n    encrypt      = true\n    use_lockfile = true\n  }}\n}}\n')

    def backend_hcl(self, bucket: str | None = BUCKET, key: str | None = KEY) -> None:
        lines = ['region = "us-east-2"']
        if bucket is not None:
            lines.insert(0, f'bucket       = "{bucket}"')
        if key is not None:
            lines.append(f'key = "{key}"')
        lines += ["encrypt      = true", "use_lockfile = true"]
        (self.root / "backend.hcl").write_text("\n".join(lines) + "\n")

    def cache(self, bucket: str = BUCKET, key: str = KEY, region: str | None = "us-east-2", text: str | None = None) -> None:
        directory = self.root / ".terraform"
        directory.mkdir(exist_ok=True)
        if text is None:
            config = {"bucket": bucket, "key": key, "encrypt": True, "use_lockfile": True}
            if region is not None:
                config["region"] = region
            text = json.dumps({"version": 3, "terraform_version": "1.16.5",
                               "backend": {"type": "s3", "config": config, "hash": 1}})
        (directory / "terraform.tfstate").write_text(text)

    def run_guard(self, exit_code: int = 0) -> tuple[list[str], str]:
        """`shown guarded_init <root>` through the prelude; the terminal's lines and the record."""
        done = self.bash(f"{SOURCE}\n{self.DOUBLE}\nTF_EXIT={exit_code}\nshown guarded_init {self.ROOT_DIR}\n")
        self.assertEqual(done.returncode, 0, done.stderr)
        record = (self.repo / PIN).with_name(f"{utc_day(-1)}.log").read_text()
        for where in (done.stdout, done.stderr, record):
            # Neither bucket's name, masked or not: the masked one keeps its prefix.
            self.assertNotIn("tfstate-", where)
        return done.stdout.splitlines(), record

    def line(self, rest: str) -> str:
        return f"guarded_init {self.ROOT_DIR}: {rest}"

    def assertStops(self, printed: list[str], rest: str) -> None:
        self.assertEqual(len(printed), 2, printed)
        self.assertEqual(printed[0], self.line("stop, no init: " + rest))
        self.assertRegex(printed[1], r"^exit=[1-9][0-9]*$")

    def test_a_root_with_no_cache_runs_the_plain_init(self) -> None:
        self.backend_hcl()
        printed, _ = self.run_guard()
        self.assertEqual(printed, [self.line("no cached backend; init -backend-config=backend.hcl"), self.PLAIN, "exit=0"])

    def test_a_cache_with_no_backend_object_runs_the_plain_init(self) -> None:
        self.backend_hcl()
        self.cache(text=json.dumps({"version": 3, "terraform_version": "1.16.5"}))
        printed, _ = self.run_guard()
        self.assertEqual(printed, [self.line("no cached backend; init -backend-config=backend.hcl"), self.PLAIN, "exit=0"])

    def test_a_cache_that_matches_runs_the_init_with_reconfigure(self) -> None:
        self.backend_hcl()
        self.cache()
        printed, _ = self.run_guard()
        self.assertEqual(printed, [self.line("bucket same, key same (key from backend.hcl); "
                                             "init -reconfigure -backend-config=backend.hcl"),
                                   self.RECONFIGURE, "exit=0"])

    def test_a_cache_that_differs_in_region_alone_is_a_match(self) -> None:
        self.backend_hcl()
        for region in (None, "us-west-1"):
            with self.subTest(region=region):
                self.cache(region=region)
                printed, _ = self.run_guard()
                self.assertEqual(printed[1:], [self.RECONFIGURE, "exit=0"])

    def test_a_backend_hcl_with_no_key_reads_the_key_from_versions_tf(self) -> None:
        self.backend_hcl(key=None)
        self.cache()
        printed, _ = self.run_guard()
        self.assertEqual(printed, [self.line("bucket same, key same (key from versions.tf); "
                                             "init -reconfigure -backend-config=backend.hcl"),
                                   self.RECONFIGURE, "exit=0"])

    def test_a_key_moved_in_versions_tf_stops_and_prints_both_keys(self) -> None:
        self.backend_hcl(key=None)
        self.versions(self.MOVED)
        self.cache()
        printed, _ = self.run_guard()
        self.assertStops(printed, f"bucket same, key differs (cached {self.KEY}, configured {self.MOVED} from versions.tf)")

    def test_a_key_in_backend_hcl_is_the_one_compared(self) -> None:
        self.versions(self.MOVED)
        self.backend_hcl(key=self.KEY)
        self.cache()
        printed, _ = self.run_guard()
        self.assertEqual(printed[1:], [self.RECONFIGURE, "exit=0"])
        self.versions(self.KEY)
        self.backend_hcl(key=self.MOVED)
        printed, _ = self.run_guard()
        self.assertStops(printed, f"bucket same, key differs (cached {self.KEY}, configured {self.MOVED} from backend.hcl)")

    def test_a_cached_bucket_that_differs_stops(self) -> None:
        self.backend_hcl()
        self.cache(bucket=self.OTHER)
        printed, _ = self.run_guard()
        self.assertStops(printed, f"bucket differs, key same (cached {self.KEY}, configured {self.KEY} from backend.hcl)")

    def test_a_cached_backend_beside_a_backend_hcl_that_names_no_bucket_stops_naming_the_file(self) -> None:
        self.cache()
        printed, _ = self.run_guard()
        self.assertStops(printed, f"cannot read bucket from {self.ROOT_DIR}/backend.hcl (absent)")
        self.backend_hcl(bucket=None)
        printed, _ = self.run_guard()
        self.assertStops(printed, f"cannot read bucket from {self.ROOT_DIR}/backend.hcl (not set)")

    def test_a_key_neither_file_sets_stops(self) -> None:
        self.backend_hcl(key=None)
        (self.root / "versions.tf").write_text('terraform {\n  backend "s3" {\n    encrypt = true\n  }\n}\n')
        self.cache()
        printed, _ = self.run_guard()
        self.assertStops(printed, f"cannot read key from {self.ROOT_DIR}/backend.hcl or {self.ROOT_DIR}/versions.tf (not set)")

    def test_a_cache_that_does_not_parse_stops(self) -> None:
        self.backend_hcl()
        for text in ("not json", json.dumps({"backend": {"type": "s3", "config": {"key": self.KEY}}})):
            with self.subTest(text=text):
                self.cache(text=text)
                printed, _ = self.run_guard()
                self.assertEqual(len(printed), 2, printed)
                self.assertTrue(printed[0].startswith(self.line("stop, no init: cannot read ")), printed)
                self.assertIn(f"{self.ROOT_DIR}/.terraform/terraform.tfstate", printed[0])
                self.assertRegex(printed[1], r"^exit=[1-9]")

    def test_a_failed_init_is_the_exit_shown_prints(self) -> None:
        self.backend_hcl()
        printed, _ = self.run_guard(exit_code=3)
        self.assertEqual(printed[1:], [self.PLAIN, "exit=3"])
        self.cache()
        printed, _ = self.run_guard(exit_code=4)
        self.assertEqual(printed[1:], [self.RECONFIGURE, "exit=4"])

    def test_the_record_holds_the_call_the_line_and_the_exit(self) -> None:
        self.backend_hcl()
        self.cache(bucket=self.OTHER)
        printed, record = self.run_guard()
        self.assertEqual(record.splitlines(), [f"$ guarded_init {self.ROOT_DIR}"] + printed)

    def test_no_argument_is_a_usage_error_and_runs_nothing(self) -> None:
        done = self.bash(f"{SOURCE}\n{self.DOUBLE}\nshown guarded_init\n")
        self.assertEqual(done.stdout.splitlines()[-1], "exit=2", done.stderr)
        self.assertNotIn("terraform ", done.stdout)

    def test_ns_step_2_names_the_guard_and_no_init_of_its_own(self) -> None:
        n_section = re.search(r"\*\*N, from nothing.*?(?=\*\*C4, the gate tags\.\*\*)", SKILL_TEXT, re.S)
        self.assertIsNotNone(n_section)
        step = re.search(r"^2\. (.*?)(?=^3\. )", n_section.group(0), re.M | re.S)
        self.assertIsNotNone(step)
        text = " ".join(step.group(1).split())
        self.assertIn("`shown guarded_init <root>`", text)
        self.assertNotIn("shown terraform", text)


class WizPassword(Sitting):
    """`wiz_password <user>` puts a test user's password on the clipboard and nowhere else (#760)."""

    CANARY = "canary-Pw-0123456789"
    DOUBLES = ('aws() { printf "%s\\n" "$*" >> "$HOME/aws.args"; [ -z "$AWS_FAIL" ] || { echo "An error occurred '
               '(ParameterNotFound)" >&2; return 254; }; printf "%s\\n" "$AWS_VALUE"; }\n'
               'pbcopy() { { cat; echo "<end>"; } >> "$HOME/clip"; }\n'
               'sleep() { echo "$1" >> "$HOME/sleep.args"; }\n')

    def setUp(self) -> None:
        super().setUp()
        self.pin(utc_day(-1) + "\n")
        self.home = self.dir / "home"

    def run_password(self, user: str = "alice", value: str | None = None, fail: bool = False,
                     shown: bool = False) -> subprocess.CompletedProcess:
        value = self.CANARY if value is None else value
        call = f"shown wiz_password {user}" if shown else f"wiz_password {user}"
        done = self.bash(f"{SOURCE}\n{self.DOUBLES}AWS_VALUE='{value}'\nAWS_FAIL='{'1' if fail else ''}'\n"
                         f"{call}\necho \"rc=$?\"\nwait\n")
        for where in (done.stdout, done.stderr):
            self.assertNotIn(self.CANARY, where)
        record = (self.repo / PIN).with_name(f"{utc_day(-1)}.log")
        if record.exists():
            self.assertNotIn(self.CANARY, record.read_text())
        return done

    def read(self, name: str) -> str:
        path = self.home / name
        return path.read_text() if path.exists() else ""

    def test_the_password_reaches_the_clipboard_and_is_emptied_after_45_seconds(self) -> None:
        done = self.run_password()
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(self.read("clip"), f"{self.CANARY}<end>\n<end>\n")
        self.assertEqual(self.read("sleep.args"), "45\n")

    def test_it_prints_only_the_length_and_the_time_the_clipboard_clears(self) -> None:
        done = self.run_password()
        printed = done.stdout.splitlines()
        self.assertEqual(len(printed), 2, printed)
        self.assertRegex(printed[0], rf"^wiz_password alice: {len(self.CANARY)} characters on the clipboard; "
                                     r"it clears at [0-2][0-9]:[0-5][0-9]:[0-5][0-9]Z$")
        self.assertEqual(printed[1], "rc=0")

    def test_it_reads_the_parameter_the_identity_module_writes(self) -> None:
        module = (ROOT / "infra" / "modules" / "idp-cognito" / "main.tf").read_text()
        template = re.search(r'password_parameter = \{ for name, _ in local\.users : name => "([^"]+)" \}', module)
        self.assertIsNotNone(template, "the identity module names no password parameter")
        name = template.group(1).replace("${var.cell}", "poc").replace("${name}", "bob")
        self.run_password(user="bob")
        self.assertEqual(self.read("aws.args"),
                         f"ssm get-parameter --name {name} --with-decryption --query Parameter.Value --output text\n")

    def test_under_shown_the_record_holds_the_length_and_not_the_password(self) -> None:
        done = self.run_password(shown=True)
        self.assertEqual(done.returncode, 0, done.stderr)
        record = (self.repo / PIN).with_name(f"{utc_day(-1)}.log").read_text().splitlines()
        self.assertEqual(record[0], "$ wiz_password alice")
        self.assertRegex(record[1], rf"^wiz_password alice: {len(self.CANARY)} characters on the clipboard")

    def test_a_failed_read_leaves_the_clipboard_alone_and_exits_non_zero(self) -> None:
        done = self.run_password(fail=True)
        self.assertIn("rc=1", done.stdout.splitlines())
        self.assertEqual(self.read("clip"), "")
        self.assertEqual(self.read("sleep.args"), "")
        self.assertIn("/test-users/alice", done.stderr)

    def test_an_empty_value_leaves_the_clipboard_alone_and_exits_non_zero(self) -> None:
        done = self.run_password(value="")
        self.assertIn("rc=1", done.stdout.splitlines())
        self.assertEqual(self.read("clip"), "")

    def test_no_user_or_two_is_a_usage_error(self) -> None:
        for args in ("", "alice bob"):
            with self.subTest(args=args):
                done = self.bash(f"{SOURCE}\n{self.DOUBLES}wiz_password {args}\necho \"rc=$?\"\n")
                self.assertIn("rc=2", done.stdout.splitlines())
                self.assertEqual(self.read("aws.args"), "")

    def test_the_clearing_job_does_not_hold_the_calls_output_open(self) -> None:
        # The Bash tool reads a call's output to its end; a job that kept it open would hold the call 45 seconds.
        doubles = self.DOUBLES.replace('sleep() { echo "$1" >> "$HOME/sleep.args"; }',
                                       'sleep() { echo "$1" >> "$HOME/sleep.args"; command sleep 3; }')
        started = datetime.datetime.now()
        done = self.bash(f"{SOURCE}\n{doubles}AWS_VALUE='{self.CANARY}'\nwiz_password alice\n")
        took = (datetime.datetime.now() - started).total_seconds()
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertLess(took, 2.5, "the call waited for the clearing job")
        deadline = datetime.datetime.now() + datetime.timedelta(seconds=10)
        while self.read("clip").count("<end>") < 2 and datetime.datetime.now() < deadline:
            subprocess.run(["/bin/sleep", "0.2"], check=False)
        self.assertEqual(self.read("clip"), f"{self.CANARY}<end>\n<end>\n", "the job did not outlive the call")


if __name__ == "__main__":
    unittest.main()
