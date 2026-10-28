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
# repository has no issue for; `issue list ...` prints every issue there not marked closed, as the
# JSON array `--json number,body` asks for; GH_FAIL makes every call fail as gh does with no network,
# and GH_LIST_FAIL only the listing. Each call is logged to $GH_LOG, so a case can see which issues
# were asked about.
GH_DOUBLE = """#!/bin/bash
printf '%s\\n' "$*" >> "$GH_LOG"
if [ -n "$GH_FAIL" ]; then echo "error connecting to api.github.com" >&2; exit 1; fi
if [ "$1 $2" = "issue list" ]; then
  if [ -n "$GH_LIST_FAIL" ]; then echo "error connecting to api.github.com" >&2; exit 1; fi
  exec python3 -I -c 'import json, os, pathlib
d = pathlib.Path(os.environ["GH_ISSUES"])
print(json.dumps([{"number": int(p.stem), "body": p.read_text()} for p in sorted(d.glob("*.md"))
                  if not (d / (p.stem + ".closed")).exists()]))'
fi
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

    def issue(self, number: int, body: str, *, closed: bool = False):
        (self.issues / f"{number}.md").write_text(body)
        if closed:
            (self.issues / f"{number}.closed").write_text("")

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

    def test_a_box_ticked_with_a_capital_x_counts_in_the_places(self):
        # GitHub ticks a box as `[x]` but takes `[X]` as ticked too; it is still a box, so the open
        # one after it is box 2, not box 1.
        case = Case(self)
        case.issue(310, "- [X] Done.\n- [ ] Open.\n")
        case.commit("Merge task issue-310: capital", "Ticket: #310")
        done = case.run()
        self.assertEqual(1, done.returncode, done.stdout)
        self.assertIn('box 2 "Open."', done.stdout)

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

    def test_a_held_ticket_is_not_hidden_by_an_unreadable_one_after_it_in_the_same_merge(self):
        case = Case(self)
        case.issue(309, "- [ ] Open.\n")
        case.commit("Merge tasks issue-309 and issue-999: held then unreadable",
                    "Ticket: #309", f"Ticket: #{NO_SUCH_ISSUE}")
        done = case.run()
        self.assertEqual(1, done.returncode, done.stdout)
        self.assertIn("held:", done.stdout)
        self.assertIn("could not be read", done.stdout)


# A carrier, finished, and part tickets in the forms the live ones used (#388): #338 and #417 as they
# read when P2-1 and P2-2 landed, #341 as P2-3's part, and a `none` line that is no part ticket.
CARRIER_P2_1 = "**Owner:** ws3-identity\n**Plan row:** P2-1\n\n## Acceptance criteria\n\n- [x] The store.\n"
PART_338 = ("**Owner:** ws1-infra\n**Plan row:** none (part of P2-1 and P2-2; the carriers are the store and "
            "key-ring tickets)\n\n## Acceptance criteria\n\n- [ ] The table.\n")
PART_417 = "**Owner:** ws1-infra\n**Plan row:** none (part of P2-1 and P2-2, as #338 is)\n\n- [x] Done.\n"
PART_341 = "**Owner:** ws3-identity\n**Plan row:** none (part of P2-3; the carrier is the composed P2-3 ticket)\n"
PART_342 = "**Owner:** ws3-identity\n**Plan row:** none (part of P2-2)\n"
NO_PART = "**Owner:** ws6-verification\n**Plan row:** none (the gate run's own ticket, P2-1 named in prose)\n"
P2_1_HELD_FAIL = ('landing-subjects: fail: 1 full-landing merge names a plan row an open part ticket declares; '
                  'land it as "Merge part of task" with an Outstanding line naming the part tickets, '
                  'or land them first.')


class PartTicketTests(unittest.TestCase):
    """#388 Q4 and Q5: a full landing that names a plan row is held while an open issue, other than
    the merge's own Ticket issue, declares itself part of that row."""

    def test_a_row_landed_whole_while_its_part_ticket_is_open_is_held(self):
        # fa96201, "Merge task P2-1" over #344, while #338 declared itself part of P2-1.
        case = Case(self)
        case.issue(344, CARRIER_P2_1)
        case.issue(338, PART_338)
        sha = case.commit("Merge task P2-1: the session store", "Ticket: #344")
        done = case.run()
        self.assertEqual(1, done.returncode, done.stdout + done.stderr)
        self.assertIn(f'landing-subjects: held: {sha} "Merge task P2-1: the session store" lands P2-1 whole, '
                      'but #338 is open and declares itself part of P2-1', done.stdout.splitlines())
        self.assertEqual(P2_1_HELD_FAIL, last_line(done))

    def test_the_2026_10_24_case_merge_task_p2_2_after_a_part_of_338_is_held_on_both_parts(self):
        # 8a4ec5f "Merge part of task issue-338" then 98e2317 "Merge task P2-2" over #340, with
        # #338 and #342 open.
        case = Case(self)
        case.issue(340, "**Plan row:** P2-2\n\n- [x] The key ring.\n")
        case.issue(338, PART_338)
        case.issue(342, PART_342)
        case.commit("Merge part of task issue-338: the ring prefix", "Ticket: #338", "Outstanding: the table.",
                    "Outstanding test ids: P-01.")
        sha = case.commit("Merge task P2-2: the key ring", "Ticket: #340")
        done = case.run()
        self.assertEqual(1, done.returncode, done.stdout)
        lines = done.stdout.splitlines()
        for number in (338, 342):
            self.assertIn(f'landing-subjects: held: {sha} "Merge task P2-2: the key ring" lands P2-2 whole, '
                          f'but #{number} is open and declares itself part of P2-2', lines)

    def test_the_merges_own_ticket_is_not_its_own_open_part(self):
        # #388 Q6: the last open part names the row too, "Merge tasks issue-417 and P2-1".
        case = Case(self)
        case.issue(417, PART_417)
        case.commit("Merge tasks issue-417 and P2-1: the table", "Ticket: #417")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout)

    def test_a_closed_part_ticket_holds_nothing(self):
        case = Case(self)
        case.issue(344, CARRIER_P2_1)
        case.issue(338, PART_338.replace("- [ ]", "- [x]"), closed=True)
        case.commit("Merge task P2-1: the session store", "Ticket: #344")
        self.assertEqual(0, case.run().returncode)

    def test_a_part_ticket_landed_whole_below_the_merge_holds_nothing(self):
        # Within one run a part's issue stays open until the morning's pull request lands it on
        # main; its own full landing on the run branch, below the carrier's, is what counts.
        case = Case(self)
        case.issue(340, "**Plan row:** P2-2\n\n- [x] The key ring.\n")
        case.issue(342, PART_342)
        case.commit("Merge task issue-342: the ring's tests", "Ticket: #342")
        case.commit("Merge task P2-2: the key ring", "Ticket: #340")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout)

    def test_a_part_ticket_landed_only_in_part_still_holds(self):
        case = Case(self)
        case.issue(340, "**Plan row:** P2-2\n\n- [x] The key ring.\n")
        case.issue(342, PART_342)
        case.commit("Merge part of task issue-342: half", "Ticket: #342", "Outstanding: half.",
                    "Outstanding test ids: none.")
        case.commit("Merge task P2-2: the key ring", "Ticket: #340")
        self.assertEqual(1, case.run().returncode)

    def test_a_none_line_without_part_of_declares_nothing(self):
        case = Case(self)
        case.issue(344, CARRIER_P2_1)
        case.issue(425, NO_PART)
        case.commit("Merge task P2-1: the session store", "Ticket: #344")
        self.assertEqual(0, case.run().returncode)

    def test_row_ids_are_read_up_to_the_first_semicolon(self):
        case = Case(self)
        case.issue(344, CARRIER_P2_1)
        case.issue(341, "**Plan row:** none (part of P2-3; unlike P2-1, which has no part)\n")
        case.commit("Merge task P2-1: the session store", "Ticket: #344")
        self.assertEqual(0, case.run().returncode)

    def test_a_part_of_mentioned_off_the_plan_row_line_declares_nothing(self):
        case = Case(self)
        case.issue(344, CARRIER_P2_1)
        case.issue(426, "**Plan row:** 6.8\n\nThis is part of P2-1 in spirit (part of P2-1).\n")
        case.commit("Merge task P2-1: the session store", "Ticket: #344")
        self.assertEqual(0, case.run().returncode)

    def test_prose_after_a_comma_does_not_hide_the_second_row(self):
        # #417's line: "none (part of P2-1 and P2-2, as #338 is)".
        case = Case(self)
        case.issue(340, "**Plan row:** P2-2\n\n- [x] The key ring.\n")
        case.issue(417, PART_417)
        case.commit("Merge task P2-2: the key ring", "Ticket: #340")
        done = case.run()
        self.assertEqual(1, done.returncode, done.stdout)
        self.assertIn("but #417 is open and declares itself part of P2-2", done.stdout)

    def test_a_subject_naming_no_plan_row_lists_no_issues(self):
        case = Case(self)
        case.issue(338, PART_338.replace("- [ ]", "- [x]"))
        case.commit("Merge task issue-338: the table", "Ticket: #338")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout)
        self.assertEqual(["issue view 338 --json body --jq .body"], case.asked())

    def test_the_open_issues_are_listed_once_for_every_merge(self):
        case = Case(self)
        case.issue(344, CARRIER_P2_1)
        case.issue(346, "**Plan row:** P2-3\n\n- [x] Composed.\n")
        case.commit("Merge task P2-1: the session store", "Ticket: #344")
        case.commit("Merge task P2-3: composed", "Ticket: #346")
        self.assertEqual(0, case.run().returncode)
        self.assertEqual(1, len([call for call in case.asked() if call.startswith("issue list ")]), case.asked())

    def test_an_unreadable_listing_is_unchecked_and_passes(self):
        case = Case(self)
        case.issue(344, CARRIER_P2_1)
        case.issue(338, PART_338)
        sha = case.commit("Merge task P2-1: the session store", "Ticket: #344")
        done = case.run(GH_LIST_FAIL="1")
        self.assertEqual(0, done.returncode, done.stdout)
        self.assertIn(f'landing-subjects: unchecked: {sha} "Merge task P2-1: the session store": the open part '
                      'tickets of P2-1 could not be listed: error connecting to api.github.com',
                      done.stdout.splitlines())
        self.assertEqual("landing-subjects: pass: 1 full-landing merge from base to HEAD: 0 clear, 1 unchecked, "
                         "0 with no Ticket line.", last_line(done))

    def test_an_open_box_and_an_open_part_are_both_named(self):
        case = Case(self)
        case.issue(344, "**Plan row:** P2-1\n\n- [ ] The store.\n")
        case.issue(338, PART_338)
        case.commit("Merge task P2-1: the session store", "Ticket: #344")
        done = case.run()
        self.assertEqual(1, done.returncode)
        self.assertIn('#344 has 1 open box: box 1 "The store."', done.stdout)
        self.assertIn("but #338 is open and declares itself part of P2-1", done.stdout)
        self.assertEqual('landing-subjects: fail: 1 full-landing merge held: 1 on an open box, 1 on an open part '
                         'ticket; land it as "Merge part of task" with its Outstanding lines.', last_line(done))


class SubjectForTests(unittest.TestCase):
    """The question overnight.js's integrate step asks before it writes a complete lane's subject
    (#388 Q4 and Q6): `--subject-for <id> --ticket <n>` prints the subject's head, judged on HEAD's
    history and the open issues by the same rule as the range."""

    def ask(self, case: Case, id_: str, number: int, **env: str) -> subprocess.CompletedProcess:
        return case.run("--subject-for", id_, "--ticket", str(number), **env)

    def test_a_carrier_with_no_open_part_lands_whole(self):
        case = Case(self)
        case.issue(346, "**Plan row:** P2-3\n")
        done = self.ask(case, "P2-3", 346)
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertEqual("landing-subjects: subject: Merge task P2-3", last_line(done))

    def test_a_carrier_with_an_open_part_lands_in_part_and_names_it(self):
        case = Case(self)
        case.issue(346, "**Plan row:** P2-3\n")
        case.issue(341, PART_341)
        done = self.ask(case, "P2-3", 346)
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertIn("landing-subjects: open part: #341 declares itself part of P2-3", done.stdout.splitlines())
        self.assertEqual("landing-subjects: subject: Merge part of task P2-3", last_line(done))

    def test_the_last_open_part_of_a_part_landed_row_names_the_row(self):
        case = Case(self)
        case.issue(346, "**Plan row:** P2-3\n")
        case.issue(341, PART_341)
        case.commit("Merge part of task P2-3: composed", "Ticket: #346", "Outstanding: #341.",
                    "Outstanding test ids: none.")
        done = self.ask(case, "issue-341", 341)
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertEqual("landing-subjects: subject: Merge tasks issue-341 and P2-3", last_line(done))

    def test_a_part_names_no_row_already_landed_whole(self):
        # Forward only (#388 Q6): P2-1 landed whole at fa96201, so #417 merges as issue-417 alone.
        case = Case(self)
        case.issue(417, PART_417)
        case.commit("Merge task P2-1: the session store", "Ticket: #344")
        case.commit("Merge task P2-2: the key ring", "Ticket: #340")
        done = self.ask(case, "issue-417", 417)
        self.assertEqual("landing-subjects: subject: Merge task issue-417", last_line(done))

    def test_a_part_names_no_row_whose_carrier_has_not_landed(self):
        case = Case(self)
        case.issue(341, PART_341)
        done = self.ask(case, "issue-341", 341)
        self.assertEqual("landing-subjects: subject: Merge task issue-341", last_line(done))

    def test_a_part_names_no_row_while_another_part_is_open(self):
        case = Case(self)
        case.issue(341, PART_341)
        case.issue(337, PART_341)
        case.commit("Merge part of task P2-3: composed", "Ticket: #346", "Outstanding: #341.",
                    "Outstanding test ids: none.")
        done = self.ask(case, "issue-341", 341)
        self.assertEqual("landing-subjects: subject: Merge task issue-341", last_line(done))

    def test_the_last_part_of_two_rows_names_both(self):
        case = Case(self)
        case.issue(338, PART_338)
        case.commit("Merge part of task P2-1: the store", "Ticket: #344", "Outstanding: #338.",
                    "Outstanding test ids: none.")
        case.commit("Merge part of task P2-2: the ring", "Ticket: #340", "Outstanding: #338.",
                    "Outstanding test ids: none.")
        done = self.ask(case, "issue-338", 338)
        self.assertEqual("landing-subjects: subject: Merge tasks issue-338, P2-1 and P2-2", last_line(done))

    def test_an_unreachable_github_answers_as_before_and_says_so(self):
        case = Case(self)
        done = self.ask(case, "P2-3", 346, GH_FAIL="1")
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertTrue(any(line.startswith("landing-subjects: unchecked: ") for line in done.stdout.splitlines()),
                        done.stdout)
        self.assertEqual("landing-subjects: subject: Merge task P2-3", last_line(done))

    def test_the_answer_passes_the_range_check_once_merged(self):
        # One rule (#388 Q4): the subject it gives is one the range check does not hold.
        case = Case(self)
        case.issue(346, "**Plan row:** P2-3\n\n- [x] Composed.\n")
        case.issue(341, PART_341)
        case.commit("Merge part of task P2-3: composed", "Ticket: #346", "Outstanding: #341.",
                    "Outstanding test ids: none.")
        head = last_line(self.ask(case, "issue-341", 341)).removeprefix("landing-subjects: subject: ")
        case.commit(f"{head}: the last part", "Ticket: #341")
        done = case.run()
        self.assertEqual(0, done.returncode, done.stdout)


class BrokenTests(unittest.TestCase):
    def test_a_base_that_is_no_commit_judges_nothing_and_exits_2(self):
        case = Case(self)
        done = case.run("--base", "no-such-ref")
        self.assertEqual(2, done.returncode, done.stdout + done.stderr)
        self.assertTrue(last_line(done).startswith("landing-subjects: fail: nothing judged: "), done.stdout)
        # The Broken handler names the problem; only the catch-all names an exception class.
        self.assertNotIn("Broken", last_line(done))

    def test_an_error_nothing_names_exits_2_not_pythons_1_which_is_a_held_merge(self):
        case = Case(self)
        # git log answers in Latin-1, which the script reads as UTF-8: no handler names that.
        case.git("config", "i18n.logOutputEncoding", "iso-8859-1")
        case.commit("Merge task issue-1: \u00ff", "Ticket: #1")
        done = case.run()
        self.assertEqual(2, done.returncode, done.stdout + done.stderr)
        self.assertTrue(last_line(done).startswith("landing-subjects: fail: nothing judged: UnicodeDecodeError: "),
                        done.stdout)

    def test_no_base_is_a_usage_error(self):
        case = Case(self)
        done = subprocess.run([sys.executable, "-I", str(SCRIPT)], cwd=case.repo, env=case.env,
                              capture_output=True, text=True)
        self.assertEqual(2, done.returncode)


if __name__ == "__main__":
    unittest.main()
