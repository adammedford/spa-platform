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

**The skill's text.** The third class reads the wiz skill's two files from this checkout and holds
that no line of the prelude's commands is copied into them, so the file stays the one copy.

**Row 2's number (#1088).** D2's `awk` and `grep` count the sitting browser's lines and the skill
writes the tokens they match by hand, so renaming a token left them printing 0 sign-outs on a roll
that signed the browser out, and the row read as failed. The fourth class reads the tokens, the log's
name and the watch's flags from tests/e2e/SittingBrowser/, builds a log out of those tokens, and runs
the skill's own two lines over it through the prelude.

Checkout inputs: .claude/skills/wiz/*.md .gitignore tests/e2e/Program.cs tests/e2e/SittingBrowser/Observations.cs tests/e2e/SittingBrowser/ObservationLine.cs tests/e2e/SittingBrowser/SittingBrowser.cs tests/e2e/SittingBrowser/SittingCommand.cs
"""
from __future__ import annotations

import datetime
import os
import pathlib
import re
import shutil
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

    def test_the_grep_prints_every_navigation_line_but_signed_in(self) -> None:
        signed_in = self.tokens["SignedInToken"]
        self.assertIn(signed_in, self.navigation)
        lines, printed = self.run_over_log("grep", self.navigation)
        self.assertEqual(printed, [line for line in lines if not line.startswith(signed_in + " ")] + ["exit=0"])

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


if __name__ == "__main__":
    unittest.main()
