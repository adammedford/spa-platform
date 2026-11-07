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

Checkout inputs: .claude/skills/wiz/*.md .gitignore tests/e2e/Program.cs tests/e2e/SittingBrowser/Observations.cs tests/e2e/SittingBrowser/ObservationLine.cs tests/e2e/SittingBrowser/SittingBrowser.cs tests/e2e/SittingBrowser/SittingCommand.cs
"""
from __future__ import annotations

import datetime
import json
import os
import pathlib
import re
import shutil
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
SKILL_FILES = (ROOT / ".claude" / "skills" / "wiz" / "SKILL.md", ROOT / ".claude" / "skills" / "wiz" / "a-and-b.md")
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
                  "spactl() {", "sitting_browser() {", "wiz_redact.py --stream; echo", "git check-ignore -q \"$E/x\"")

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
        self.skill = SKILL_FILES[0].read_text().splitlines()

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
        blocks = [block for block in re.findall(r"^```bash\n(.*?)^```$", SKILL_FILES[0].read_text(), re.M | re.S)
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
        text = SKILL_FILES[0].read_text()
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
        self.text = SKILL_FILES[0].read_text()
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
                  if line.strip().startswith("terraform -chdir=<root> show -json <plan>")]
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
        self.assertEqual(record.splitlines(), ["plan list show exit=3 filter exit=1"])

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
        self.assertEqual(record.splitlines(), ["plan list show exit=0 filter exit=2"])

    def test_a_read_plan_lists_its_changes_and_the_record_holds_them_with_the_verdict(self) -> None:
        plan = {"planned_values": {}, "resource_changes": [
            {"address": "aws_a.b", "change": {"actions": ["delete", "create"]}},
            {"address": "aws_kept.same", "change": {"actions": ["no-op"]}},
        ]}
        done, record = self.run_list(f"terraform() {{ printf '%s' '{json.dumps(plan)}'; }}")
        self.assertEqual(done.stdout.splitlines(), ["delete,create aws_a.b", self.VERDICT_READ], done.stderr)
        self.assertEqual(record.splitlines(), ["delete,create aws_a.b", self.VERDICT_READ])

    def test_a_plan_with_no_changes_is_an_empty_list_under_a_passing_verdict(self) -> None:
        plan = {"planned_values": {}, "resource_changes": [{"address": "aws_kept.same", "change": {"actions": ["no-op"]}}]}
        done, _ = self.run_list(f"terraform() {{ printf '%s' '{json.dumps(plan)}'; }}")
        self.assertEqual(done.stdout.splitlines(), [self.VERDICT_READ], done.stderr)

    def test_the_route_makes_any_other_verdict_a_stop(self) -> None:
        self.assertIn("# both 0, or stop:", self.plan_list())
        route = " ".join(self.between("- **Plans (#1090 Q7).**", "- **A lost `claude`").split())
        self.assertIn("any other verdict is a rule 7 stop (#1137)", route)


class SkillGivesEachSittingItsOwnWayOutOfALostClaude(unittest.TestCase):
    """D10 restores D2's copy and posts on #348, so sitting C has its own way out (#1138)."""

    def setUp(self) -> None:
        text = SKILL_FILES[0].read_text()
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


if __name__ == "__main__":
    unittest.main()
