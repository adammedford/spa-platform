#!/usr/bin/env python3
"""Tests for tools/overnight_collect.py. Owner: integrator.

Run: python3 -I tools/overnight_collect_tests.py

Each rule the collect step states has a case, as do the two #187 added: a spec is a parent and never a
ticket, and two tickets with one id refuse. The read from GitHub goes through a fake `gh` on PATH,
in an environment that inherits nothing, so no case reaches a real repository even where a real gh
is installed and GH_REPO is set.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import pathlib
import stat
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parent.parent


def load(name: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tools" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


collect = load("overnight_collect")
roadmap = load("roadmap")

# Answers from the fake gh: the listing for whichever label the call names, from GH_FIXTURES, with
# each call's arguments logged to GH_LOG; GH_FAIL makes it fail as gh does when it cannot reach
# GitHub. Anything else it is asked exits 97.
GH_DOUBLE = """#!/bin/bash
printf '%s\\n' "$*" >> "$GH_LOG"
if [ -n "$GH_FAIL" ]; then echo "gh: could not reach GitHub" >&2; exit 1; fi
[ "$1 $2" = "issue list" ] || exit 97
for argument in "$@"; do
  case "$argument" in ready-for-agent|ready-for-human|needs-triage) label="$argument" ;; esac
done
[ -n "$label" ] || exit 97
cat "$GH_FIXTURES/$label.json"
"""

ROAD = {
    "closed_gates": [{"gate": "G2", "tag": "g2-local-slice"}],
    "rows": {"I-4": "landed", "1.4": "part-landed", "1.6b": "open", "1.7": "open", "6.3": "open",
             "6.9": "open"},
    "splits": {"6.3a": "landed"},
    "tickets": {"issue-65": "landed"},
}


def issue(number: int, body: str = "", *, title: str = "", assignees: tuple[str, ...] = (),
          blockers: tuple[tuple[int, str], ...] = (), subs: int = 0) -> dict:
    """One issue as `gh issue list --json` gives it."""
    return {
        "number": number,
        "title": title or f"Issue {number}",
        "body": body,
        "assignees": [{"login": login} for login in assignees],
        "url": f"https://github.com/o/r/issues/{number}",
        "labels": [],
        "blockedBy": {"nodes": [{"number": n, "state": state} for n, state in blockers]},
        "subIssuesSummary": {"total": subs, "completed": 0, "percentCompleted": 0},
    }


def body(row: str = "none", owner: str = "integrator", boxes: str = "- [ ] one") -> str:
    return f"**Owner:** {owner}\n**Plan row:** {row}\n\n## Acceptance\n{boxes}\n"


def resolved(agent=(), human=(), triage=(), branches=(), road=None) -> dict:
    listings = {"ready-for-agent": list(agent), "ready-for-human": list(human),
                "needs-triage": list(triage)}
    return collect.resolve(listings, list(branches), road or ROAD)


def by_issue(answer: dict) -> dict:
    return {t["issue"]: t for t in answer["tickets"]}


class Rules(unittest.TestCase):
    def test_the_plan_row_gives_the_id_and_anything_else_the_issue_number(self):
        for row, expected in (
            ("1.4", "1.4"), ("1.6b", "1.6b"), ("I-4", "I-4"), ("P2-7", "P2-7"), ("1.4  ", "1.4"),
            ("none", "issue-7"), ("", "issue-7"), ("-", "issue-7"),
            ("none; a leftover of the 2026-10-15 sitting.", "issue-7"),
        ):
            with self.subTest(row=row):
                found = by_issue(resolved(agent=[issue(7, body(row))]))[7]["id"]
                self.assertEqual(expected, found)

    def test_the_id_pattern_is_the_roadmaps(self):
        """Copied so the script stands alone, and held to the original here."""
        self.assertEqual(roadmap.ID.pattern, collect.PLAN_ID.pattern)

    def test_the_owner_is_the_first_role_the_owner_line_names(self):
        for line, expected in (
            ("1 AWS and IaC", "ws1-infra"), ("ws1-infra, run by the human", "ws1-infra"),
            ("2 Hosting and registry", "ws2-hosting"), ("workstream 2 (`tests/X`)", "ws2-hosting"),
            ("3 OAuth and sessions", "ws3-identity"), ("4 API and security", "ws4-proxy"),
            ("5 Fixtures", "ws5-fixtures"), ("6 Verification and operations", "ws6-verification"),
            ("workstream 6 (`tests/Gateway.*`)", "ws6-verification"),
            ("integrator (anyone may write a change request)", "integrator"),
            ("workstream 4, then the integrator", "ws4-proxy"), ("someone", "integrator"),
        ):
            with self.subTest(line=line):
                found = by_issue(resolved(agent=[issue(7, body(owner=line))]))[7]["owner"]
                self.assertEqual(expected, found)

    def test_an_integrator_row_is_the_integrators_whatever_the_owner_line_says(self):
        found = by_issue(resolved(agent=[issue(7, body("I-7", owner="ws2-hosting"))]))[7]
        self.assertEqual("integrator", found["owner"])

    def test_the_status_takes_the_first_rule_that_fires(self):
        answer = by_issue(resolved(
            agent=[issue(1, body(), assignees=("adam",)),
                   issue(2, body("1.7")),
                   issue(3, body("I-4")),
                   issue(4, body("1.6b"))],
            human=[issue(5, body(), assignees=("adam",))],
            triage=[issue(6, body())],
            branches=["task/1.7-scripts", "task/I-4-landed-row", "task/1.6-other-row"],
        ))
        self.assertEqual("claimed", answer[1]["status"])          # an assignee
        self.assertEqual("claimed", answer[2]["status"])          # its row's branch, not landed
        self.assertEqual("ready-for-agent", answer[3]["status"])  # a branch on a landed row
        self.assertEqual("ready-for-agent", answer[4]["status"])  # task/1.6- is not task/1.6b-
        self.assertEqual("ready-for-human", answer[5]["status"])  # the label beats the assignee
        self.assertEqual("needs-triage", answer[6]["status"])

    def test_blocked_settles_however_long_the_chain(self):
        """10 waits on 11, 11 on 12, and 12 is a human's: listed first, 10 is judged before 11
        is demoted, so a single pass would leave it ready."""
        answer = by_issue(resolved(
            agent=[issue(10, body(), blockers=((11, "OPEN"),)),
                   issue(11, body(), blockers=((12, "OPEN"),)),
                   issue(13, body(), blockers=((14, "OPEN"),)),
                   issue(14, body())],
            human=[issue(12, body())],
        ))
        self.assertEqual("blocked", answer[11]["status"])
        self.assertEqual("blocked", answer[10]["status"])
        self.assertEqual(["issue-11"], answer[10]["blockedBy"])
        self.assertEqual("ready-for-agent", answer[13]["status"])  # its blocker runs first tonight
        self.assertEqual("ready-for-agent", answer[14]["status"])

    def test_a_blocker_outside_tonights_set_blocks_and_a_closed_one_does_not(self):
        answer = by_issue(resolved(agent=[issue(20, body(), blockers=((99, "OPEN"),)),
                                          issue(21, body(), blockers=((98, "CLOSED"),))]))
        self.assertEqual(("blocked", ["issue-99"]), (answer[20]["status"], answer[20]["blockedBy"]))
        self.assertEqual(("ready-for-agent", []), (answer[21]["status"], answer[21]["blockedBy"]))

    def test_a_blocker_tonight_is_named_by_its_id(self):
        answer = by_issue(resolved(agent=[issue(30, body(), blockers=((31, "OPEN"),)),
                                          issue(31, body("1.7"))]))
        self.assertEqual(["1.7"], answer[30]["blockedBy"])

    def test_open_boxes_are_counted_and_no_checkbox_is_minus_one(self):
        answer = by_issue(resolved(agent=[
            issue(40, body(boxes="- [ ] a\n- [ ] b\n- [x] c")),
            issue(41, body(boxes="- [x] a\n- [X] b")),
            issue(42, "**Owner:** integrator\n\nNo checkbox here."),
        ]))
        self.assertEqual([2, 0, -1], [answer[n]["openBoxes"] for n in (40, 41, 42)])

    def test_an_issue_with_sub_issues_is_a_parent_and_never_a_ticket(self):
        """#139 to #142 on 2026-10-20: specs labelled ready-for-agent, with no checkbox."""
        answer = resolved(agent=[issue(139, "## Problem Statement\n", subs=2), issue(153, body())])
        self.assertEqual([153], [t["issue"] for t in answer["tickets"]])
        self.assertEqual([139], [p["issue"] for p in answer["parents"]])

    def test_a_foreground_session_line_is_listed_apart_and_prose_naming_one_is_not(self):
        """#137 quotes the line in a sentence and is a ticket; #83 carries it and is not."""
        answer = resolved(agent=[
            issue(83, "**Owner:** integrator\n**Session:** foreground — not an overnight lane\n"),
            issue(137, "Today #83 carries `**Session:** foreground — ...`, which this ends.\n"),
        ])
        self.assertEqual([137], [t["issue"] for t in answer["tickets"]])
        self.assertEqual([83], [f["issue"] for f in answer["foreground"]])

    def test_two_tickets_with_one_id_refuse_and_name_both(self):
        """#56 and #180 on 2026-10-20, both `**Plan row:** 1.4`."""
        with self.assertRaises(collect.Refused) as refused:
            resolved(agent=[issue(56, body("1.4")), issue(180, body("1.4"))])
        self.assertIn("issues #56 and #180 share the id 1.4", str(refused.exception))

    def test_tickets_are_numbered_in_plan_order_then_by_issue(self):
        answer = resolved(agent=[issue(9, body()), issue(5, body("6.9")), issue(7, body("1.4")),
                                 issue(8, body())])
        self.assertEqual([(1, 7), (2, 5), (3, 8), (4, 9)],
                         [(t["n"], t["issue"]) for t in answer["tickets"]])

    def test_an_open_issue_on_a_landed_row_is_reported(self):
        answer = resolved(agent=[issue(65, body())], branches=[])
        self.assertEqual([{"id": "issue-65", "issue": 65}], answer["landedButOpen"])


class ThroughGh(unittest.TestCase):
    """main() end to end, with the fake gh on PATH and nothing else of the caller's environment."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        base = pathlib.Path(self.tmp.name)
        self.bin, self.fixtures, self.log = base / "bin", base / "fixtures", base / "gh.log"
        self.bin.mkdir()
        self.fixtures.mkdir()
        gh = self.bin / "gh"
        gh.write_text(GH_DOUBLE)
        gh.chmod(gh.stat().st_mode | stat.S_IXUSR)
        self.env = {"PATH": f"{self.bin}:/usr/bin:/bin", "HOME": str(base),
                    "GH_FIXTURES": str(self.fixtures), "GH_LOG": str(self.log)}
        # The repository's branches and roadmap are this checkout's; the cases pin them.
        for name, value in (("branches", lambda root: []), ("roadmap", lambda root: ROAD)):
            patcher = mock.patch.object(collect, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def listings(self, agent=(), human=(), triage=()) -> None:
        for label, issues in (("ready-for-agent", agent), ("ready-for-human", human),
                              ("needs-triage", triage)):
            (self.fixtures / f"{label}.json").write_text(json.dumps(list(issues)))

    def run_main(self, *argv: str, **extra: str) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.dict(os.environ, {**self.env, **extra}, clear=True), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = collect.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_it_asks_for_each_label_with_the_sub_issue_field_and_prints_the_tickets(self):
        self.listings(agent=[issue(1, body("1.7"))], human=[issue(2, body())])
        code, out, err = self.run_main("--json")
        self.assertEqual(0, code, err)
        calls = self.log.read_text().splitlines()
        self.assertEqual(3, len(calls))
        for label, call in zip(collect.LABELS, calls):
            self.assertIn(f"--label {label} --limit 500 --json {collect.FIELDS}", call)
        self.assertIn("subIssuesSummary", collect.FIELDS)
        answer = json.loads(out)
        self.assertEqual([("1.7", "ready-for-agent"), ("issue-2", "ready-for-human")],
                         [(t["id"], t["status"]) for t in answer["tickets"]])

    def test_a_shared_id_exits_1_with_nothing_on_stdout(self):
        self.listings(agent=[issue(56, body("1.4")), issue(180, body("1.4"))])
        code, out, err = self.run_main("--json")
        self.assertEqual((1, ""), (code, out))
        self.assertIn("issues #56 and #180 share the id 1.4", err)

    def test_a_failed_read_exits_1_with_nothing_on_stdout(self):
        self.listings()
        code, out, err = self.run_main("--json", GH_FAIL="1")
        self.assertEqual((1, ""), (code, out))
        self.assertIn("no tickets printed", err)

    def test_saved_listings_are_read_instead_of_asking_github(self):
        saved = pathlib.Path(self.tmp.name) / "saved"
        saved.mkdir()
        (saved / "ready-for-agent.json").write_text(json.dumps([issue(1, body())]))
        (saved / "branches.txt").write_text("task/issue-1-claimed\n")
        (saved / "roadmap.json").write_text(json.dumps(ROAD))
        code, out, err = self.run_main("--json", "--from", str(saved))
        self.assertEqual(0, code, err)
        self.assertFalse(self.log.exists(), "gh was asked although the listings were given")
        self.assertEqual("claimed", json.loads(out)["tickets"][0]["status"])


if __name__ == "__main__":
    unittest.main(verbosity=1)
