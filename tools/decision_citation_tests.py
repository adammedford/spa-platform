#!/usr/bin/env python3
"""Test companion for tools/decision_citation.py: which decision comment, if any, a merge body
owes a citation to.

Owner: integrator. Built for #530.

**The seam is the command land.sh runs.** Every case starts the real file as `python3 -I
tools/decision_citation.py` and hands it, on standard input, the object the --issue form builds
from gh: `{"issue": <the issue>, "comments": <pages>}`, the issue as `gh api
repos/{owner}/{repo}/issues/<n>` gives it and the comments as `gh api --paginate --slurp` pages
them. It asserts the exit status, what the reader prints on standard output (the URL land.sh looks
for in the merge body) and the fragment of standard error that says why nothing is owed or why the
input cannot be read. Nothing is imported, so no case can reach a part of the reader that land.sh
cannot.

**The `--issue` form has the same seam, with gh doubled.** `decision_citation.py --issue <n>` makes
the two gh calls itself, so its cases put a strict gh double alone on PATH that serves the issue
and the comments and records every call; any other call exits 97. One case runs each set of
captured listings through both forms and asserts the same status, standard output and standard
error, so the two cannot drift apart.

**The account is read from the guide.** Who may decide is the login docs/agents/tracker-policy.md
names on its "The human's account is `@<login>`." line, read by the reader from the checkout it
sits in; the cases hand it a guide of their own with --guide, and one case holds the line's pattern
equal to the one .github/steering.py reads, as tools/overnight_collect_tests.py does for its copy.

**The fixtures are GitHub's shapes**, and the reader reads only some of their fields:
https://docs.github.com/en/rest/issues/issues#get-an-issue and
https://docs.github.com/en/rest/issues/comments#list-issue-comments. The decision comment's form
is docs/agents/tracker-policy.md -> "Deciding": the first line one of four, the second `ADR: yes`
or `ADR: no`.

Run: python3 -I tools/decision_citation_tests.py
"""

from __future__ import annotations

import json
import pathlib
import re
import shlex
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
READER = ROOT / "tools" / "decision_citation.py"
STEERING = ROOT / ".github" / "steering.py"

ISSUE = "495"
URL = f"https://github.com/adammedford/spa-platform/issues/{ISSUE}"
HUMAN = "adammedford"
# The guide a case hands the reader: the account line in the form .github/steering.py reads, written
# out here rather than imported, with prose around it that names other accounts.
GUIDE = ("# Tracker policy\n\nSome prose about `@someone-else`.\n\n"
         f"- **The human's account is `@{HUMAN}`.** A `Confirm` comment is the human's act.\n"
         "- Another bullet.\n")
GUIDE_WITHOUT_ACCOUNT = "# Tracker policy\n\nNo account is named here.\n"


def issue(*labels: str, state: str = "closed") -> dict:
    return {"number": int(ISSUE), "state": state, "html_url": URL,
            "labels": [{"name": label} for label in labels]}


def comment(n: int, body: str | None, at: str, login: str = HUMAN) -> dict:
    return {"id": n, "html_url": f"{URL}#issuecomment-{n}", "body": body, "created_at": at,
            "updated_at": at, "user": {"login": login}, "author_association": "OWNER"}


def decision(first: str, adr: str = "no", rest: str = "\n\nThe why, in prose.\n") -> str:
    """A decision comment's body in the form of tracker-policy.md -> "Deciding"."""
    return f"{first}\nADR: {adr}{rest}"


ACCEPT = "Decision: accept"
WITH_CHANGES = "Decision: accept with changes"
AS_DRAFTED = f"Decision: accept as drafted {URL}#issuecomment-3"
DECLINE = "Decision: decline"
T1, T2, T3 = "2026-10-29T01:00:00Z", "2026-10-29T02:00:00Z", "2026-10-29T03:00:00Z"


class Outcome:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout.decode("utf-8")
        self.stderr = completed.stderr.decode("utf-8")


class ReaderTestCase(unittest.TestCase):
    def guide(self, text: str | None = GUIDE) -> list[str]:
        """`--guide <path>` naming a file holding `text`, or a path that does not exist for None."""
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = pathlib.Path(directory.name) / "tracker-policy.md"
        if text is not None:
            path.write_text(text)
        return ["--guide", str(path)]

    def read(self, issue_: dict | None = None, comments: list | None = None, *,
             pages: dict | None = None, raw: bytes | None = None,
             guide: str | None = GUIDE, arguments: tuple[str, ...] = ()) -> Outcome:
        """Run the stdin form as land.sh's --issue form feeds the judge: the issue and one page of
        comments."""
        if raw is None:
            payload = pages if pages is not None else {"issue": issue_ if issue_ is not None
                                                        else issue("change-request"),
                                                        "comments": [comments or []]}
            raw = json.dumps(payload).encode("utf-8")
        completed = subprocess.run([sys.executable, "-I", str(READER), *self.guide(guide),
                                    *arguments], input=raw, capture_output=True, timeout=30)
        return Outcome(completed)

    def assertOwed(self, outcome: Outcome, url: str):
        self.assertEqual(0, outcome.status, outcome.stderr)
        self.assertEqual(f"{url}\n", outcome.stdout)

    def assertNothingOwed(self, outcome: Outcome, *fragments: str):
        self.assertEqual(1, outcome.status, outcome.stderr)
        self.assertEqual("", outcome.stdout, "nothing is owed, yet a URL was printed")
        for fragment in fragments:
            self.assertIn(fragment, outcome.stderr)

    def assertUnreadable(self, outcome: Outcome, *fragments: str):
        self.assertEqual(2, outcome.status, outcome.stderr)
        self.assertEqual("", outcome.stdout, "unreadable input printed a URL land.sh could use")
        for fragment in fragments:
            self.assertIn(fragment, outcome.stderr)


class OwedTests(ReaderTestCase):
    """A change request whose latest decision accepts with `ADR: no` owes its decision's URL."""

    def test_an_accept_with_adr_no(self):
        outcome = self.read(comments=[comment(11, decision(ACCEPT), T1)])
        self.assertOwed(outcome, f"{URL}#issuecomment-11")

    def test_each_accepting_form(self):
        for first in (ACCEPT, WITH_CHANGES, AS_DRAFTED):
            with self.subTest(first=first):
                outcome = self.read(comments=[comment(11, decision(first), T1)])
                self.assertOwed(outcome, f"{URL}#issuecomment-11")

    def test_the_real_shape_of_a_decision_as_495_carried_it(self):
        # #495's decision, 2026-10-29: `Decision: accept with changes`, `ADR: no`, then the why;
        # #492's merge body cited none of it, which is why this reader exists.
        body = ("Decision: accept with changes\nADR: no\n\nThe section lands as quoted in this "
                "issue, except that its second paragraph is shortened.\n")
        outcome = self.read(issue("ready-for-human", "change-request"),
                            [comment(6485168110, body, T1),
                             comment(6487113944, "## Grilling record\n\nGrilled on paper.", T2)])
        self.assertOwed(outcome, f"{URL}#issuecomment-6485168110")

    def test_lines_ending_in_a_carriage_return_are_read(self):
        # A comment edited on the web ends each line in \r\n.
        outcome = self.read(comments=[comment(11, "Decision: accept\r\nADR: no\r\n\r\nWhy.\r\n", T1)])
        self.assertOwed(outcome, f"{URL}#issuecomment-11")

    def test_the_latest_decision_decides(self):
        # A decline then an accept: the request stands accepted, and its later decision is cited.
        # Listed out of time order, since the latest is the latest by created_at, not by position.
        outcome = self.read(comments=[comment(12, decision(ACCEPT), T2),
                                      comment(11, decision(DECLINE), T1)])
        self.assertOwed(outcome, f"{URL}#issuecomment-12")

    def test_the_latest_accepting_decision_is_the_one_cited(self):
        outcome = self.read(comments=[comment(11, decision(ACCEPT), T1),
                                      comment(12, decision(WITH_CHANGES), T2)])
        self.assertOwed(outcome, f"{URL}#issuecomment-12")

    def test_a_decision_on_a_later_page(self):
        pages = {"issue": issue("change-request"),
                 "comments": [[comment(n, "Some discussion.", T1) for n in range(1, 31)],
                              [comment(31, decision(ACCEPT), T2)]]}
        self.assertOwed(self.read(pages=pages), f"{URL}#issuecomment-31")

    def test_an_open_request_owes_as_a_closed_one_does(self):
        outcome = self.read(issue("change-request", state="open"),
                            [comment(11, decision(ACCEPT), T1)])
        self.assertOwed(outcome, f"{URL}#issuecomment-11")


class NothingOwedTests(ReaderTestCase):
    """What asks for no citation, each against an input that would owe one if the rule were gone."""

    def test_an_issue_without_the_label(self):
        outcome = self.read(issue("ready-for-agent"), [comment(11, decision(ACCEPT), T1)])
        self.assertNothingOwed(outcome, f"#{ISSUE} is not a change request")

    def test_a_request_decided_adr_yes(self):
        outcome = self.read(comments=[comment(11, decision(ACCEPT, "yes"), T1)])
        self.assertNothingOwed(outcome, "ADR: yes")

    def test_a_declined_request(self):
        outcome = self.read(comments=[comment(11, decision(DECLINE), T1)])
        self.assertNothingOwed(outcome, "declined")

    def test_a_decline_after_an_accept(self):
        # The latest decision decides, so an accept withdrawn by a later decline owes nothing.
        outcome = self.read(comments=[comment(11, decision(ACCEPT), T1),
                                      comment(12, decision(DECLINE), T2)])
        self.assertNothingOwed(outcome, "declined")

    def test_an_adr_yes_after_an_adr_no(self):
        outcome = self.read(comments=[comment(11, decision(ACCEPT), T1),
                                      comment(12, decision(ACCEPT, "yes"), T2)])
        self.assertNothingOwed(outcome, "ADR: yes")

    def test_a_request_with_no_decision(self):
        outcome = self.read(comments=[comment(11, "## Draft decision\n\nProposed decision: accept\n"
                                                  "Proposed ADR: no\n", T1)])
        self.assertNothingOwed(outcome, "no decision comment")

    def test_a_decision_in_the_form_from_another_account(self):
        # The form matched and the author not: anyone who can comment could otherwise decide.
        for login in ("a-stranger", "adammedford-2", "Adammedford"):
            with self.subTest(login=login):
                outcome = self.read(comments=[comment(11, decision(ACCEPT), T1, login=login)])
                self.assertNothingOwed(outcome, "no decision comment", login)

    def test_a_later_decline_from_another_account_does_not_unseat_the_humans_accept(self):
        outcome = self.read(comments=[comment(11, decision(ACCEPT), T1),
                                      comment(12, decision(DECLINE), T2, login="a-stranger")])
        self.assertOwed(outcome, f"{URL}#issuecomment-11")

    def test_a_decision_with_a_deleted_author(self):
        deleted = comment(11, decision(ACCEPT), T1)
        deleted["user"] = None
        self.assertNothingOwed(self.read(comments=[deleted]), "no decision comment")

    def test_lines_close_to_the_form_are_not_decisions(self):
        # tracker-policy.md: a comment that departs from the form is not a decision comment.
        for body in ("Decision: accepted\nADR: no\n",
                     "Decision: Accept\nADR: no\n",
                     " Decision: accept\nADR: no\n",
                     "Decision: accept, with thanks\nADR: no\n",
                     "Decision: accept as drafted\nADR: no\n",
                     "Decision: accept\nADR: none\n",
                     "Decision: accept\n\nADR: no\n",
                     "Decision: accept\n",
                     "Decision: accept",
                     "## Draft decision\nDecision: accept\nADR: no\n",
                     "",
                     None):
            with self.subTest(body=body):
                outcome = self.read(comments=[comment(11, body, T1)])
                self.assertNothingOwed(outcome, "no decision comment")


class InputTests(ReaderTestCase):
    """What the reader cannot judge exits 2, and land.sh stops on it."""

    def test_input_that_is_not_json(self):
        self.assertUnreadable(self.read(raw=b"not json"), "standard input is not JSON")

    def test_input_missing_the_issue_or_the_comments(self):
        for payload in ({"comments": [[]]}, {"issue": issue("change-request")}, [], "text"):
            with self.subTest(payload=payload):
                outcome = self.read(raw=json.dumps(payload).encode("utf-8"))
                self.assertUnreadable(outcome, "lacks the issue or the comments")

    def test_an_issue_without_labels_in_shape(self):
        for labels in (None, "change-request", [{"id": 1}], ["change-request"]):
            with self.subTest(labels=labels):
                broken = issue()
                broken["labels"] = labels
                self.assertUnreadable(self.read(broken, []), "labels")

    def test_comments_that_are_not_pages(self):
        outcome = self.read(pages={"issue": issue("change-request"),
                                   "comments": [comment(11, decision(ACCEPT), T1)]})
        self.assertUnreadable(outcome, "not a list of pages")

    def test_a_decision_with_no_time_or_no_url(self):
        for field in ("created_at", "html_url"):
            with self.subTest(field=field):
                broken = comment(11, decision(ACCEPT), T1)
                del broken[field]
                self.assertUnreadable(self.read(comments=[broken]), field)

    def test_a_comment_that_is_no_decision_may_lack_those_fields(self):
        # Only a decision is read closely: a bare discussion comment missing a time is passed over.
        bare = {"body": "Some discussion.", "user": {"login": HUMAN}}
        outcome = self.read(comments=[bare, comment(11, decision(ACCEPT), T1)])
        self.assertOwed(outcome, f"{URL}#issuecomment-11")

    def test_a_guide_naming_no_account_fails_closed(self):
        # CODING_STANDARDS.md -> "An authorisation is read from its author": with no account named,
        # nobody's decision is read, so nothing can be judged, rather than anyone's being read.
        for text, fragment in ((GUIDE_WITHOUT_ACCOUNT, "names no account"), (None, "cannot be read")):
            with self.subTest(guide=text):
                outcome = self.read(comments=[comment(11, decision(ACCEPT), T1)], guide=text)
                self.assertUnreadable(outcome, "cannot read the decision", fragment)

    def test_the_account_line_is_the_steering_checks(self):
        # Three readers hold the same line; the pattern is written in each, and held equal here as
        # tools/overnight_collect_tests.py holds its own to the steering check's.
        pattern = re.compile(r"ACCOUNT_LINE = re\.compile\(\n\s*(r\"[^\n]*\")\n\)", re.MULTILINE)
        mine = pattern.search(READER.read_text())
        theirs = pattern.search(STEERING.read_text())
        self.assertIsNotNone(mine, "tools/decision_citation.py has no ACCOUNT_LINE in the form")
        self.assertIsNotNone(theirs, ".github/steering.py has no ACCOUNT_LINE in the form")
        self.assertEqual(theirs.group(1), mine.group(1))

    def test_wrong_arguments(self):
        for arguments in (("--issue",), ("--issue", "x"), ("--issue", "0"), ("--what",),
                          ("7",), ("--guide",)):
            with self.subTest(arguments=arguments):
                outcome = self.read(comments=[], arguments=arguments)
                self.assertUnreadable(outcome, "the two forms are")


# The gh calls the --issue form makes, in the shapes gh's manual gives them:
# https://cli.github.com/manual/gh_api
GET_ISSUE = ["api", f"repos/{{owner}}/{{repo}}/issues/{ISSUE}"]
LIST_COMMENTS = ["api", f"repos/{{owner}}/{{repo}}/issues/{ISSUE}/comments", "--paginate", "--slurp"]

GH_DOUBLE = """#!/bin/bash
# A double for gh, written by tools/decision_citation_tests.py for one case. It answers only the
# calls the reader's --issue form makes, records each call's arguments, and exits 97 on any other.
log={log}
n=0
while [ -e "$log/call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$@" > "$log/call-$n"
unexpected() {{ printf 'gh double: unexpected call\\n' >&2; exit 97; }}
case "$*" in
  "api repos/{{owner}}/{{repo}}/issues/{number}")
    [ -z {issue_fails} ] || {{ printf '%s\\n' {issue_fails} >&2; exit 1; }}
    /bin/cat -- {issue} ;;
  "api repos/{{owner}}/{{repo}}/issues/{number}/comments --paginate --slurp")
    [ -z {api_fails} ] || {{ printf '%s\\n' {api_fails} >&2; exit 1; }}
    /bin/cat -- {comments} ;;
  *) unexpected ;;
esac
"""


class Asked:
    """One run of the --issue form against a gh double: its outcome and the calls gh received."""

    def __init__(self, outcome: Outcome, calls: list):
        self.outcome = outcome
        self.calls = calls


class IssueFormTests(ReaderTestCase):
    """`decision_citation.py --issue <n>` looks the issue and its comments up itself, and answers
    exactly what the stdin form answers for the same input."""

    def ask(self, *arguments: str, pages: dict | None = None, raw_issue: bytes | None = None,
            issue_fails: str = "", api_fails: str = "", gh: bool = True) -> Asked:
        if pages is None:
            pages = {"issue": issue("change-request"), "comments": [[]]}
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            bin_dir, log = root / "bin", root / "log"
            bin_dir.mkdir()
            log.mkdir()
            (root / "issue.json").write_bytes(raw_issue if raw_issue is not None
                                              else json.dumps(pages["issue"]).encode("utf-8"))
            (root / "comments.json").write_text(json.dumps(pages["comments"]))
            if gh:
                double = bin_dir / "gh"
                double.write_text(GH_DOUBLE.format(
                    log=shlex.quote(str(log)), number=ISSUE,
                    issue=shlex.quote(str(root / "issue.json")),
                    comments=shlex.quote(str(root / "comments.json")),
                    issue_fails=shlex.quote(issue_fails), api_fails=shlex.quote(api_fails)))
                double.chmod(0o755)
            env = {"PATH": str(bin_dir), "HOME": str(root), "LC_ALL": "C"}
            completed = subprocess.run([sys.executable, "-I", str(READER), *self.guide(),
                                        *arguments], input=b"", capture_output=True, timeout=30,
                                       env=env, cwd=root)
            names = sorted(log.glob("call-*"), key=lambda path: int(path.name.split("-")[1]))
            calls = [path.read_bytes().decode("utf-8").split("\0")[:-1] for path in names]
        return Asked(Outcome(completed), calls)

    CAPTURED = {
        "an accept with ADR: no": {
            "issue": issue("change-request"), "comments": [[comment(11, decision(ACCEPT), T1)]]},
        "an accept with ADR: yes": {
            "issue": issue("change-request"),
            "comments": [[comment(11, decision(ACCEPT, "yes"), T1)]]},
        "a decline": {
            "issue": issue("change-request"), "comments": [[comment(11, decision(DECLINE), T1)]]},
        "a decision from another account": {
            "issue": issue("change-request"),
            "comments": [[comment(11, decision(ACCEPT), T1, login="a-stranger")]]},
        "an issue without the label": {
            "issue": issue("ready-for-agent"), "comments": [[comment(11, decision(ACCEPT), T1)]]},
        "a decision on the second page": {
            "issue": issue("change-request"),
            "comments": [[comment(n, "Discussion.", T1) for n in range(1, 31)],
                         [comment(31, decision(ACCEPT), T2)]]},
        "no comment at all": {"issue": issue("change-request"), "comments": [[]]},
    }

    def test_the_two_forms_agree_on_captured_listings(self):
        for name, pages in self.CAPTURED.items():
            with self.subTest(listings=name):
                piped = self.read(pages=pages)
                asked = self.ask("--issue", ISSUE, pages=pages)
                self.assertEqual((piped.status, piped.stdout, piped.stderr),
                                 (asked.outcome.status, asked.outcome.stdout, asked.outcome.stderr))
                # The comments are fetched only for a change request.
                labelled = any(label["name"] == "change-request"
                               for label in pages["issue"]["labels"])
                self.assertEqual([GET_ISSUE, LIST_COMMENTS] if labelled else [GET_ISSUE],
                                 asked.calls)

    def test_an_issue_without_the_label_is_not_asked_for_its_comments(self):
        asked = self.ask("--issue", ISSUE, pages={"issue": issue("ready-for-agent"),
                                                  "comments": [[]]})
        self.assertNothingOwed(asked.outcome, f"#{ISSUE} is not a change request")
        self.assertEqual([GET_ISSUE], asked.calls)

    def test_a_gh_call_that_fails(self):
        for name, failing in (("issue", {"issue_fails": "HTTP 404: Not Found"}),
                              ("comments", {"api_fails": "HTTP 502: Bad Gateway"})):
            with self.subTest(call=name):
                asked = self.ask("--issue", ISSUE, **failing)
                self.assertUnreadable(asked.outcome, "failed with status 1")
                self.assertIn("HTTP", asked.outcome.stderr, "gh's own message did not pass through")

    def test_an_answer_that_is_not_json(self):
        asked = self.ask("--issue", ISSUE, raw_issue=b"<html>")
        self.assertUnreadable(asked.outcome, "not JSON")

    def test_gh_missing_from_path(self):
        asked = self.ask("--issue", ISSUE, gh=False)
        self.assertUnreadable(asked.outcome, "could not start")


if __name__ == "__main__":
    unittest.main()
