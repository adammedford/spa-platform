#!/usr/bin/env python3
"""Tests for tools/landing_subjects.py. Owner: integrator. Built in #296.

Run: python3 -I tools/landing_subjects_tests.py

**What runs is the script itself**, as `python3 -I tools/landing_subjects.py --base <ref>`, in a
throwaway repository whose commits are the subjects a case judges. The read from GitHub goes
through a fake `gh` on PATH, in an environment that inherits nothing, so no case reaches a real
repository: it answers `gh issue view <n>` with the body a case wrote for issue n, says what gh says
of a number it has no issue for, and fails as gh does with no network when the case asks.

The 2026-10-22 replay holds #62's body as it read when the 6.7 lane reported itself complete: box 1
ticked, boxes 2 and 3 open (#287, #296). It is typed from the issue, not read from GitHub, so the
case proves the same thing after #62 changes.
"""
from __future__ import annotations

import os
import pathlib
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "landing_subjects.py"

# #62 as it read on 2026-10-22, after the 6.7 lane and before the review (#287).
ISSUE_62 = """**Owner:** ws6-verification
**Plan row:** 6.7
**Moved from:** `.scratch/overnight/issues/41-6.7-runbooks.md` (read-only archive, ADR-0020); history only.

**What to build:** Operating notes in `docs/runbooks/`: deploy, rollback, release rollback, registry rollback, secret rotation, ending every session for an app, teardown. Each is the command sequence the scripts already run (`contracts/resource-tags.md` names the teardown query). Needs G3. Done when: 6.9 exercises deploy and teardown; the rest are walked through once.
## Acceptance
- [x] Runbooks for deploy, rollback, release rollback, registry rollback, secret rotation, ending an app's sessions, teardown
- [ ] Deploy and teardown exercised by 6.9
- [ ] The rest walked through once
"""

FINISHED = """## Acceptance criteria

- [x] The first thing.
- [X] The second thing.
"""

# The fake gh. `issue view <n> ...` prints $GH_ISSUES/<n>.md, or says what gh says of a number the
# repository has no issue for; GH_FAIL makes every call fail as gh does with no network. Each call
# is logged to $GH_LOG, so a case can see which issues were asked about.
GH_DOUBLE = """#!/bin/bash
printf '%s\\n' "$*" >> "$GH_LOG"
if [ -n "$GH_FAIL" ]; then echo "error connecting to api.github.com" >&2; exit 1; fi
if [ "$1 $2" = "issue view" ] && [ -f "$GH_ISSUES/$3.md" ]; then cat "$GH_ISSUES/$3.md"; exit 0; fi
echo "GraphQL: Could not resolve to an issue or pull request with the number of $3. (repository.issue)" >&2
exit 1
"""

# A number the fake gh has no issue for. Held in a name, never written after a `#`, because the
# steering check asks GitHub about every `#NNN` a tracked file cites, and this one names nothing.
NO_SUCH_ISSUE = 999


class Case:
    """A throwaway repository on a base commit, a directory of issue bodies, and a bin directory,
    the case's whole PATH, holding the fake gh and the real git, python3, bash and cat."""

    def __init__(self, test: unittest.TestCase, *, gh: bool = True):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.repo = self.dir / "repo"
        self.issues = self.dir / "issues"
        self.bin = self.dir / "bin"
        self.gh_log = self.dir / "gh.log"
        for directory in (self.repo, self.issues, self.bin):
            directory.mkdir()
        self.gh_log.write_text("")
        # The fake gh is a bash script that answers with cat.
        for tool in ("git", "python3", "bash", "cat"):
            found = sys.executable if tool == "python3" else shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion")
            (self.bin / tool).symlink_to(os.path.realpath(found))
        if gh:
            fake = self.bin / "gh"
            fake.write_text(GH_DOUBLE)
            fake.chmod(fake.stat().st_mode | stat.S_IXUSR)
        self.env = {
            "PATH": str(self.bin),
            "HOME": str(self.dir),
            "GIT_CONFIG_GLOBAL": str(self.dir / "gitconfig"),
            "GIT_CONFIG_NOSYSTEM": "1",
            "GH_LOG": str(self.gh_log),
            "GH_ISSUES": str(self.issues),
            "LANG": "C.UTF-8",
        }
        (self.dir / "gitconfig").write_text(
            "[user]\n\tname = Landing Tests\n\temail = landing-tests@users.noreply.github.com\n"
            "[init]\n\tdefaultBranch = main\n")
        self.git("init", "-q")
        self.commit("Base")
        self.git("tag", "base")

    def git(self, *arguments: str) -> str:
        return subprocess.run(["git", *arguments], cwd=self.repo, env=self.env, check=True,
                              capture_output=True, text=True).stdout.strip()

    def commit(self, subject: str, *body: str) -> str:
        message = subject + ("\n\n" + "\n".join(body) if body else "")
        self.git("commit", "-q", "--allow-empty", "-m", message)
        return self.git("rev-parse", "--short", "HEAD")

    def issue(self, number: int, body: str):
        (self.issues / f"{number}.md").write_text(body)

    def run(self, *arguments: str, **env: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, "-I", str(SCRIPT), *(arguments or ("--base", "base"))],
                              cwd=self.repo, env={**self.env, **env}, capture_output=True, text=True)

    def asked(self) -> list[str]:
        return self.gh_log.read_text().splitlines()


def last_line(done: subprocess.CompletedProcess) -> str:
    lines = done.stdout.splitlines()
    return lines[-1] if lines else ""


class JudgedTests(unittest.TestCase):
    def test_a_full_landing_merge_whose_ticket_has_an_open_box_fails(self):
        case = Case(self)
        case.issue(300, "## Acceptance criteria\n\n- [x] Done.\n- [ ] Not done.\n")
        sha = case.commit("Merge task issue-300: something", "What landed.", "", "Ticket: #300")
        done = case.run()
        self.assertEqual(1, done.returncode, done.stdout + done.stderr)
        self.assertIn(f'landing-subjects: held: {sha} "Merge task issue-300: something" lands #300 whole, '
                      f'but #300 has 1 open box: box 2 "Not done."', done.stdout.splitlines())
        self.assertTrue(last_line(done).startswith("landing-subjects: fail: "), done.stdout)

    def test_a_full_landing_merge_whose_ticket_has_no_open_box_passes(self):
        case = Case(self)
        case.issue(301, FINISHED)
        case.commit("Merge task issue-301: something", "Ticket: #301")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertEqual("landing-subjects: pass: 1 full-landing merge from base to HEAD: 1 clear, 0 unchecked, "
                         "0 with no Ticket line.",
                         last_line(done))

    def test_the_2026_10_22_case_merge_task_6_7_over_62_fails(self):
        case = Case(self)
        case.issue(62, ISSUE_62)
        sha = case.commit("Merge task 6.7: operating notes", "What was verified.", "", "Ticket: #62",
                          "", "Co-Authored-By: Claude <noreply@anthropic.com>")
        done = case.run()
        self.assertEqual(1, done.returncode, done.stdout + done.stderr)
        self.assertIn(f'landing-subjects: held: {sha} "Merge task 6.7: operating notes" lands #62 whole, '
                      'but #62 has 2 open boxes: box 2 "Deploy and teardown exercised by 6.9", '
                      'box 3 "The rest walked through once"', done.stdout.splitlines())
        self.assertEqual("landing-subjects: fail: 1 full-landing merge names a ticket with an open box; "
                         "land it as \"Merge part of task\" with its Outstanding lines, or finish the boxes.",
                         last_line(done))

    def test_a_plural_subject_judges_every_ticket_it_names(self):
        case = Case(self)
        case.issue(145, FINISHED)
        case.issue(146, "- [ ] Open.\n")
        sha = case.commit("Merge tasks issue-145 and issue-146: two", "Ticket: #145", "Ticket: #146")
        done = case.run()
        self.assertEqual(1, done.returncode, done.stdout + done.stderr)
        self.assertEqual(["issue view 145 --json body --jq .body", "issue view 146 --json body --jq .body"],
                         case.asked())
        self.assertIn(f'landing-subjects: held: {sha} "Merge tasks issue-145 and issue-146: two" lands #146 '
                      'whole, but #146 has 1 open box: box 1 "Open."', done.stdout.splitlines())

    def test_the_subject_is_matched_whatever_its_case(self):
        case = Case(self)
        case.issue(302, "- [ ] Open.\n")
        case.commit("merge Task issue-302: lower case", "Ticket: #302")
        self.assertEqual(1, case.run().returncode)

    def test_every_held_subject_is_named_before_the_verdict(self):
        case = Case(self)
        case.issue(62, ISSUE_62)
        case.issue(303, "- [ ] Open.\n")
        first = case.commit("Merge task 6.7: operating notes", "Ticket: #62")
        second = case.commit("Merge task issue-303: another", "Ticket: #303")
        done = case.run()
        self.assertEqual(1, done.returncode)
        held = [line for line in done.stdout.splitlines() if line.startswith("landing-subjects: held: ")]
        self.assertEqual(2, len(held), done.stdout)
        self.assertTrue(held[0].startswith(f"landing-subjects: held: {first} "), held)
        self.assertTrue(held[1].startswith(f"landing-subjects: held: {second} "), held)
        self.assertTrue(last_line(done).startswith("landing-subjects: fail: 2 full-landing merges name"))

    def test_an_indented_box_is_a_box(self):
        case = Case(self)
        case.issue(304, "- [x] Top.\n  - [ ] Nested.\n")
        case.commit("Merge task issue-304: nested", "Ticket: #304")
        done = case.run()
        self.assertEqual(1, done.returncode)
        self.assertIn('box 2 "Nested."', done.stdout)

    def test_a_box_mentioned_mid_line_is_not_a_box(self):
        case = Case(self)
        case.issue(305, "- [x] Done.\nA ticket writes `- [ ]` for an open box.\n")
        case.commit("Merge task issue-305: prose", "Ticket: #305")
        self.assertEqual(0, case.run().returncode)

    def test_a_ticket_mentioned_mid_line_is_not_the_line(self):
        # Only a line that starts `Ticket: #n` names the issue; a mention in prose does not.
        case = Case(self)
        case.issue(306, "- [ ] Open.\n")
        case.commit("Merge task issue-306: prose", "This follows the earlier Ticket: #306")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout)
        self.assertEqual([], case.asked())

    def test_only_the_range_is_read(self):
        case = Case(self)
        case.issue(62, ISSUE_62)
        case.commit("Merge task 6.7: operating notes", "Ticket: #62")
        case.git("tag", "later")
        done = case.run("--base", "later")
        self.assertEqual(0, done.returncode, done.stdout)
        self.assertEqual("landing-subjects: pass: no full-landing merge from later to HEAD.", last_line(done))
        self.assertEqual([], case.asked())

    def test_an_issue_named_twice_is_asked_once(self):
        case = Case(self)
        case.issue(307, FINISHED)
        case.commit("Merge task issue-307: one", "Ticket: #307")
        case.commit("Merge task issue-307: again", "Ticket: #307")
        self.assertEqual(0, case.run().returncode)
        self.assertEqual(1, len(case.asked()))


class NeverJudgedTests(unittest.TestCase):
    def test_a_part_row_merge_is_never_judged(self):
        case = Case(self)
        case.issue(62, ISSUE_62)
        case.commit("Merge part of task 6.7: operating notes", "Ticket: #62", "Outstanding: boxes 2 and 3.",
                    "Outstanding test ids: none.")
        case.commit("Merge part of tasks 6.7 and 6.8: two", "Ticket: #62")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout)
        self.assertEqual([], case.asked())
        self.assertEqual("landing-subjects: pass: no full-landing merge from base to HEAD.", last_line(done))

    def test_a_subject_with_no_ticket_line_is_never_judged(self):
        case = Case(self)
        sha = case.commit("Merge task 5.5: from before the tracker", "What landed.")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout)
        self.assertEqual([], case.asked())
        self.assertIn(f'landing-subjects: note: {sha} "Merge task 5.5: from before the tracker" has no '
                      '"Ticket: #n" line, so it was not judged.', done.stdout.splitlines())
        self.assertEqual("landing-subjects: pass: 1 full-landing merge from base to HEAD: 0 clear, 0 unchecked, "
                         "1 with no Ticket line.",
                         last_line(done))

    def test_other_subjects_are_never_judged(self):
        case = Case(self)
        case.issue(308, "- [ ] Open.\n")
        for subject in ("Task issue-308: the work", "Merge branch 'task/issue-308'",
                        "Merge the integrator's morning", "Merge taskforce 3: not a task"):
            case.commit(subject, "Ticket: #308")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout)
        self.assertEqual([], case.asked())


class UncheckedTests(unittest.TestCase):
    def test_an_unreachable_github_is_unchecked_and_passes(self):
        case = Case(self)
        case.issue(62, ISSUE_62)
        sha = case.commit("Merge task 6.7: operating notes", "Ticket: #62")
        done = case.run(GH_FAIL="1")
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertIn(f'landing-subjects: unchecked: {sha} "Merge task 6.7: operating notes": #62 could not '
                      'be read: error connecting to api.github.com', done.stdout.splitlines())
        self.assertEqual("landing-subjects: pass: 1 full-landing merge from base to HEAD: 0 clear, 1 unchecked, "
                         "0 with no Ticket line.",
                         last_line(done))

    def test_an_issue_github_has_not_got_is_unchecked_and_passes(self):
        case = Case(self)
        case.commit("Merge task issue-999: no such issue", f"Ticket: #{NO_SUCH_ISSUE}")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertIn(f"#{NO_SUCH_ISSUE} could not be read: GraphQL: Could not resolve to an issue or pull request "
                      f"with the number of {NO_SUCH_ISSUE}. (repository.issue)", done.stdout)

    def test_no_gh_is_unchecked_and_passes(self):
        case = Case(self, gh=False)
        case.commit("Merge task 6.7: operating notes", "Ticket: #62")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertIn("#62 could not be read: gh is not on PATH", done.stdout)

    def test_an_unchecked_issue_does_not_hide_a_held_one(self):
        case = Case(self)
        case.issue(309, "- [ ] Open.\n")
        case.commit("Merge task issue-999: unreadable", f"Ticket: #{NO_SUCH_ISSUE}")
        case.commit("Merge task issue-309: held", "Ticket: #309")
        done = case.run()
        self.assertEqual(1, done.returncode, done.stdout)


class BrokenTests(unittest.TestCase):
    def test_a_base_that_is_no_commit_judges_nothing_and_exits_2(self):
        case = Case(self)
        done = case.run("--base", "no-such-ref")
        self.assertEqual(2, done.returncode, done.stdout + done.stderr)
        self.assertTrue(last_line(done).startswith("landing-subjects: fail: nothing judged: "), done.stdout)

    def test_no_base_is_a_usage_error(self):
        case = Case(self)
        done = subprocess.run([sys.executable, "-I", str(SCRIPT)], cwd=case.repo, env=case.env,
                              capture_output=True, text=True)
        self.assertEqual(2, done.returncode)


if __name__ == "__main__":
    unittest.main()
