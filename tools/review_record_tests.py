#!/usr/bin/env python3
"""Test companion for tools/review_record.py: which review record, if any, clears a head.

Owner: integrator. Decided in #67 (archived overnight ticket 70, Q6, Q7 and Q14, which Adam
confirmed on #67).

**The seam is the command land.sh runs.** Every case starts the real file as `python3 -I
tools/review_record.py <head>` and hands it, on standard input, the object land.sh builds from two
`gh api --paginate --slurp` listings: `{"comments": <pages>, "reviews": <pages>}`. It asserts the
exit status, what the reader prints on standard output (the record's URL, which land.sh writes into
the merge body) and the fragment of standard error that names the refusal. Nothing is imported, so
no case can reach a part of the reader that land.sh cannot.

**The `--pr` form has the same seam, with gh doubled (#488).** `review_record.py --pr <n>` makes the
`gh` calls land.sh used to make, so its cases put a strict gh double alone on PATH that serves the
head and the listings and records every call; any other call exits 97. One case runs each set of
captured listings through both forms and asserts the same status, standard output and standard
error, so the two cannot drift apart. `-I` isolates the reader's imports, not PATH, so the double is
the gh it finds.

**The fixtures are GitHub's shapes.** Each comment and review is built with the fields GitHub's REST
API returns, and the reader reads only some of them:
https://docs.github.com/en/rest/issues/comments#list-issue-comments and
https://docs.github.com/en/rest/pulls/reviews#list-reviews-for-a-pull-request. `gh api --slurp`
wraps each page in an outer array (`gh api --help`), which is why every fixture is a list of pages.

**Each refusal has a case that shows it firing**, against an input that would merge if the rule were
gone: a hold, a record of an earlier head, an abbreviated or malformed line, a sha mentioned in
passing, a review's own `commit_id`, a review that is not `COMMENTED`, a line posted from outside
the repository. docs/agents/review.md -> "Landing or holding" fixes the record line these cases hold
the reader to.

Run: python3 -I tools/review_record_tests.py
"""

from __future__ import annotations

import json
import os
import pathlib
import re
import shlex
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
READER = ROOT / "tools" / "review_record.py"

HEAD = "1a954628a960aaef81d7b2d4521929579f3541e6"
EARLIER = "f12e996adf405952c9666956e73952681bc053d8"
BASE = "1405df66cbe219b0bf6355bc3d60361a8376b6b4"
OTHER = "d0941e68da8f38151ff86a61fc59f7c5cf9fcaa2"

PULL = "https://github.com/adammedford/spa-platform/pull/7"


def record(head: str, verdict: str, base: str = BASE) -> str:
    """The record line, written out here rather than imported: a guard reads the thing it guards."""
    return f"Reviewed {head} against {base}: {verdict}"


def comment(n: int, body: str | None, at: str, association: str = "OWNER",
            login: str = "adammedford") -> dict:
    return {"id": n, "html_url": f"{PULL}#issuecomment-{n}", "body": body, "created_at": at,
            "updated_at": at, "user": {"login": login}, "author_association": association}


def review(n: int, body: str | None, at: str | None, state: str = "COMMENTED",
           commit_id: str = EARLIER, association: str = "OWNER",
           login: str = "adammedford") -> dict:
    return {"id": n, "html_url": f"{PULL}#pullrequestreview-{n}", "body": body, "state": state,
            "submitted_at": at, "commit_id": commit_id, "user": {"login": login},
            "author_association": association}


# GitHub's author_association values for an account outside the repository: everything but OWNER,
# MEMBER and COLLABORATOR, from enum CommentAuthorAssociation in
# https://docs.github.com/public/fpt/schema.docs.graphql
OUTSIDE = ("CONTRIBUTOR", "FIRST_TIMER", "FIRST_TIME_CONTRIBUTOR", "MANNEQUIN", "NONE")


class Outcome:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout.decode("utf-8")
        self.stderr = completed.stderr.decode("utf-8")


def read(head: str = HEAD, *flags: str, comments: list | None = None, reviews: list | None = None,
         pages: dict | None = None, raw: bytes | None = None) -> Outcome:
    """Run the reader as land.sh does, with `flags` after the head. `comments` and `reviews` go in
    as one page each."""
    if raw is None:
        payload = pages if pages is not None else {"comments": [comments or []],
                                                   "reviews": [reviews or []]}
        raw = json.dumps(payload).encode("utf-8")
    completed = subprocess.run([sys.executable, "-I", str(READER), head, *flags], input=raw,
                               capture_output=True, timeout=30)
    return Outcome(completed)


class ReaderTestCase(unittest.TestCase):
    def assertCleared(self, outcome: Outcome, url: str):
        self.assertEqual(0, outcome.status, outcome.stderr)
        self.assertEqual(f"{url}\n", outcome.stdout)

    def assertRefused(self, outcome: Outcome, *fragments: str):
        self.assertEqual(1, outcome.status, outcome.stderr)
        self.assertEqual("", outcome.stdout, "a refusal printed a record URL land.sh could use")
        for fragment in fragments:
            self.assertIn(fragment, outcome.stderr)


class ClearedTests(ReaderTestCase):
    """A merge record naming the head, the latest of those naming it, clears it."""

    def test_a_comment_whose_line_names_the_head_with_merge(self):
        outcome = read(comments=[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-11")

    def test_a_commented_review_whose_body_carries_the_line(self):
        outcome = read(reviews=[review(21, record(HEAD, "merge"), "2026-10-18T01:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#pullrequestreview-21")

    def test_the_line_among_the_findings_on_a_line_of_its_own(self):
        body = "\r\n".join(["## Standards", "", "PASS.", "", f"  {record(HEAD, 'merge')}  ", "",
                            "## Spec", "", "PASS."])
        outcome = read(comments=[comment(11, body, "2026-10-18T01:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-11")

    def test_a_merge_after_a_hold_on_the_same_head_clears_it(self):
        # Archived overnight ticket 70's scenario 4: a finding answered as not a defect, with no new
        # commit, and the reviewer posts merge on the same head. Listed newest first, so the times
        # decide and not the order GitHub happened to list them in.
        outcome = read(comments=[comment(12, record(HEAD, "merge"), "2026-10-18T02:00:00Z"),
                                 comment(11, record(HEAD, "hold"), "2026-10-18T01:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-12")

    def test_the_latest_across_comments_and_reviews(self):
        outcome = read(comments=[comment(11, record(HEAD, "hold"), "2026-10-18T01:00:00Z")],
                       reviews=[review(21, record(HEAD, "merge"), "2026-10-18T01:00:01Z")])
        self.assertCleared(outcome, f"{PULL}#pullrequestreview-21")

    def test_a_later_record_of_another_head_does_not_unseat_it(self):
        outcome = read(comments=[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z"),
                                 comment(12, record(OTHER, "hold"), "2026-10-18T02:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-11")

    def test_a_record_on_a_later_page(self):
        pages = {"comments": [[comment(n, "a finding, answered", "2026-10-18T00:00:00Z")
                               for n in range(1, 31)],
                              [comment(31, record(HEAD, "merge"), "2026-10-18T01:00:00Z")]],
                 "reviews": []}
        self.assertCleared(read(pages=pages), f"{PULL}#issuecomment-31")

    def test_a_record_from_each_account_the_repository_lets_in(self):
        # The owner is the only one today; a collaborator is who reopens the decision (#67).
        for association in ("OWNER", "MEMBER", "COLLABORATOR"):
            with self.subTest(association=association):
                outcome = read(comments=[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z",
                                                 association=association)])
                self.assertCleared(outcome, f"{PULL}#issuecomment-11")

    def test_a_later_hold_from_outside_the_repository_does_not_hold(self):
        # The other half of the case below: an outside account can neither clear a head nor stop
        # one, so a stranger's hold cannot force the lander onto --unreviewed.
        outcome = read(comments=[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z"),
                                 comment(12, record(HEAD, "hold"), "2026-10-18T02:00:00Z",
                                         association="NONE", login="a-stranger")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-11")

    def test_the_base_is_not_compared(self):
        # main's protection sets strict: false, leaving base drift to CI, so a record names its base
        # for the reader of the record and the check reads only the head.
        outcome = read(comments=[comment(11, record(HEAD, "merge", base=OTHER),
                                         "2026-10-18T01:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-11")


class RefusalTests(ReaderTestCase):
    """Each way a pull request's records leave its head uncleared."""

    def test_no_record_at_all(self):
        outcome = read(comments=[comment(11, "Looks good to me.", "2026-10-18T01:00:00Z")])
        self.assertRefused(outcome, f"no review record names {HEAD}")

    def test_nothing_on_the_pull_request(self):
        self.assertRefused(read(), f"no review record names {HEAD}")

    def test_the_latest_record_of_the_head_holds(self):
        outcome = read(comments=[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z"),
                                 comment(12, record(HEAD, "hold"), "2026-10-18T02:00:00Z")])
        self.assertRefused(outcome, "hold", f"{PULL}#issuecomment-12")

    def test_a_record_holding_both_verdicts_at_once_holds(self):
        body = f"{record(HEAD, 'merge')}\n{record(HEAD, 'hold')}"
        outcome = read(comments=[comment(11, body, "2026-10-18T01:00:00Z")])
        self.assertRefused(outcome, "hold", f"{PULL}#issuecomment-11")

    def test_a_merge_record_of_an_earlier_head(self):
        # #34 and #35: fix commits pushed after the review rode on its verdict.
        outcome = read(comments=[comment(11, record(EARLIER, "merge"), "2026-10-18T01:00:00Z")])
        self.assertRefused(outcome, f"no review record names {HEAD}", EARLIER,
                           f"{PULL}#issuecomment-11")

    def test_an_abbreviated_head(self):
        line = f"Reviewed {HEAD[:7]} against {BASE[:7]}: merge"
        outcome = read(comments=[comment(11, line, "2026-10-18T01:00:00Z")])
        self.assertRefused(outcome, f"no review record names {HEAD}", "not in the record's form",
                           line)

    def test_lines_close_to_the_form_are_not_records(self):
        malformed = [
            f"Reviewed `{HEAD}` against `{BASE}`: merge",
            f"Reviewed {HEAD}: merge",
            f"Reviewed {HEAD} against {BASE}: approve",
            f"Reviewed {HEAD} against {BASE}: merge.",
            f"Reviewed {HEAD.upper()} against {BASE}: merge",
            f"reviewed {HEAD} against {BASE}: merge",
            f"> Reviewed {HEAD} against {BASE}: merge",
            f"Reviewed {HEAD}0 against {BASE}: merge",
        ]
        for line in malformed:
            with self.subTest(line=line):
                outcome = read(comments=[comment(11, line, "2026-10-18T01:00:00Z")])
                self.assertRefused(outcome, f"no review record names {HEAD}",
                                   "not in the record's form")

    def test_the_line_inside_a_sentence(self):
        body = f"For the record: {record(HEAD, 'merge')} -- see above."
        outcome = read(comments=[comment(11, body, "2026-10-18T01:00:00Z")])
        self.assertRefused(outcome, f"no review record names {HEAD}")

    def test_a_full_head_mentioned_in_passing(self):
        # a80e906, #34's merge body: the merged head named as where the fixes went, which a check
        # accepting any mention of the head would have passed.
        body = f"Both findings were real, and both are fixed in {HEAD}."
        outcome = read(comments=[comment(11, body, "2026-10-18T01:00:00Z")])
        self.assertRefused(outcome, f"no review record names {HEAD}")

    def test_a_review_whose_commit_id_is_the_head(self):
        # GitHub defaults a review's commit_id to the newest commit when the reviewer gives none, so
        # it names the head a reviewer was shown, not the one they read.
        outcome = read(reviews=[review(21, "Reviewed; one finding, answered.",
                                       "2026-10-18T01:00:00Z", commit_id=HEAD)])
        self.assertRefused(outcome, f"no review record names {HEAD}")

    def test_a_record_in_a_review_that_is_not_commented(self):
        for state in ("APPROVED", "CHANGES_REQUESTED", "DISMISSED", "PENDING"):
            with self.subTest(state=state):
                at = None if state == "PENDING" else "2026-10-18T01:00:00Z"
                outcome = read(reviews=[review(21, record(HEAD, "merge"), at, state=state)])
                self.assertRefused(outcome, f"no review record names {HEAD}")

    def test_a_record_from_outside_the_repository(self):
        # The repository is public, so any signed-in GitHub user can comment on a pull request and
        # write the line; it is not the reviewing agent session's record.
        for association in OUTSIDE:
            with self.subTest(association=association):
                outcome = read(
                    comments=[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z",
                                      association=association, login="a-stranger")],
                    reviews=[review(21, record(HEAD, "merge"), "2026-10-18T01:00:01Z",
                                    association=association, login="a-stranger")])
                self.assertRefused(
                    outcome, f"no review record names {HEAD}",
                    f"posted by a-stranger, whose author_association is {association}",
                    f"{PULL}#issuecomment-11", f"{PULL}#pullrequestreview-21")

    def test_a_dismissed_hold_still_leaves_an_earlier_merge_standing(self):
        # The other half of the case above: a review that is not COMMENTED is not read at all, so
        # it neither clears nor holds.
        outcome = read(comments=[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z")],
                       reviews=[review(21, record(HEAD, "hold"), "2026-10-18T02:00:00Z",
                                       state="DISMISSED")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-11")


class InputTests(ReaderTestCase):
    """What the reader refuses to read at all: status 2, so land.sh stops, and nothing on stdout."""

    def assertUnreadable(self, outcome: Outcome, fragment: str):
        self.assertEqual(2, outcome.status, outcome.stderr)
        self.assertEqual("", outcome.stdout)
        self.assertIn(fragment, outcome.stderr)

    def test_a_head_that_is_not_a_full_sha(self):
        for head in ("", HEAD[:7], HEAD.upper(), f"{HEAD}0"):
            with self.subTest(head=head):
                outcome = read(head=head, comments=[comment(11, record(HEAD, "merge"),
                                                            "2026-10-18T01:00:00Z")])
                self.assertUnreadable(outcome, "not a full commit sha")

    def test_input_that_is_not_json(self):
        self.assertUnreadable(read(raw=b"<html>rate limited</html>"), "not JSON")

    def test_input_missing_a_listing(self):
        self.assertUnreadable(read(raw=json.dumps({"comments": [[]]}).encode()), "reviews")

    def test_a_listing_that_is_not_pages(self):
        payload = {"comments": [comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z")],
                   "reviews": []}
        self.assertUnreadable(read(pages=payload), "comments")

    def test_a_record_with_no_time(self):
        outcome = read(comments=[comment(11, record(HEAD, "merge"), None)])
        self.assertUnreadable(outcome, "created_at")

    def test_a_record_with_no_url(self):
        entry = comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z")
        del entry["html_url"]
        self.assertUnreadable(read(comments=[entry]), "html_url")

    def test_a_record_with_no_author_association(self):
        # Both listings carry the field, so its absence is input that is not GitHub's shape.
        entry = comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z")
        del entry["author_association"]
        self.assertUnreadable(read(comments=[entry]), "author_association")

    def test_an_empty_body_is_no_record_rather_than_unreadable(self):
        # GitHub returns a review with no summary as an empty body, and an older one as null.
        outcome = read(reviews=[review(21, None, "2026-10-18T01:00:00Z"),
                                review(22, "", "2026-10-18T01:00:01Z")])
        self.assertRefused(outcome, f"no review record names {HEAD}")


# The pull request the `--pr` cases ask about, and the three calls the reader makes of gh for it,
# in the shapes gh's manual gives them: https://cli.github.com/manual/gh_pr_view and
# https://cli.github.com/manual/gh_api. They are the calls land.sh made itself before #488.
NUMBER = "7"
VIEW_HEAD = ["pr", "view", NUMBER, "--json", "headRefOid", "--jq", ".headRefOid"]
LIST_COMMENTS = ["api", f"repos/{{owner}}/{{repo}}/issues/{NUMBER}/comments", "--paginate", "--slurp"]
LIST_REVIEWS = ["api", f"repos/{{owner}}/{{repo}}/pulls/{NUMBER}/reviews", "--paginate", "--slurp"]

# Both forms, as every refusal of an argument names them.
PR_FORM = "review_record.py --pr <number>"
STDIN_FORM = "review_record.py <40-hex head>"

GH_DOUBLE = """#!/bin/bash
# A double for gh, written by tools/review_record_tests.py for one case. It answers only the calls
# the reader's --pr form makes, records each call's arguments, and exits 97 on any other call.
log={log}
n=0
while [ -e "$log/call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$@" > "$log/call-$n"
unexpected() {{ printf 'gh double: unexpected call\\n' >&2; exit 97; }}
case "$*" in
  "pr view {number} --json headRefOid --jq .headRefOid")
    [ -z {view_fails} ] || {{ printf '%s\\n' {view_fails} >&2; exit 1; }}
    printf '%s\\n' {head} ;;
  "api repos/{{owner}}/{{repo}}/issues/{number}/comments --paginate --slurp")
    [ -z {api_fails} ] || {{ printf '%s\\n' {api_fails} >&2; exit 1; }}
    /bin/cat -- {comments} ;;
  "api repos/{{owner}}/{{repo}}/pulls/{number}/reviews --paginate --slurp")
    /bin/cat -- {reviews} ;;
  *) unexpected ;;
esac
"""


class Asked:
    """One run of the `--pr` form against a gh double: its outcome and the calls gh received."""

    def __init__(self, outcome: Outcome, calls: list):
        self.outcome = outcome
        self.calls = calls


def ask(*arguments: str, head: str = HEAD, comments: list | None = None,
        reviews: list | None = None, pages: dict | None = None, raw_comments: bytes | None = None,
        view_fails: str = "", api_fails: str = "", gh: bool = True) -> Asked:
    """Run the reader's --pr form as a caller does, with a gh double first and alone on PATH that
    serves `head` as the pull request's head and the listings as `gh api --paginate --slurp` pages
    them. Captured listings in the stdin form's shape go in as `pages`."""
    if pages is None:
        pages = {"comments": [comments or []], "reviews": [reviews or []]}
    with tempfile.TemporaryDirectory() as directory:
        root = pathlib.Path(directory)
        bin_dir, log = root / "bin", root / "log"
        bin_dir.mkdir()
        log.mkdir()
        (root / "comments.json").write_bytes(raw_comments if raw_comments is not None
                                             else json.dumps(pages["comments"]).encode("utf-8"))
        (root / "reviews.json").write_text(json.dumps(pages["reviews"]))
        if gh:
            double = bin_dir / "gh"
            double.write_text(GH_DOUBLE.format(
                log=shlex.quote(str(log)), number=NUMBER, head=shlex.quote(head),
                comments=shlex.quote(str(root / "comments.json")),
                reviews=shlex.quote(str(root / "reviews.json")),
                view_fails=shlex.quote(view_fails), api_fails=shlex.quote(api_fails)))
            double.chmod(0o755)
        env = {"PATH": str(bin_dir), "HOME": str(root), "LC_ALL": "C"}
        completed = subprocess.run([sys.executable, "-I", str(READER), *arguments], input=b"",
                                   capture_output=True, timeout=30, env=env, cwd=root)
        names = sorted(log.glob("call-*"), key=lambda path: int(path.name.split("-")[1]))
        calls = [path.read_bytes().decode("utf-8").split("\0")[:-1] for path in names]
    return Asked(Outcome(completed), calls)


# Listings as a pull request might carry them, each answered by the stdin form one way: a clear,
# a hold, a record of another head, a near miss, a line from outside, two pages, and nothing.
CAPTURED = {
    "a merge record in a comment": {
        "comments": [[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z")]], "reviews": [[]]},
    "a merge record in a commented review": {
        "comments": [[]], "reviews": [[review(21, record(HEAD, "merge"), "2026-10-18T01:00:00Z")]]},
    "a hold after a merge": {
        "comments": [[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z"),
                      comment(12, record(HEAD, "hold"), "2026-10-18T02:00:00Z")]], "reviews": [[]]},
    "a merge record of an earlier head": {
        "comments": [[comment(11, record(EARLIER, "merge"), "2026-10-18T01:00:00Z")]],
        "reviews": [[]]},
    "an abbreviated record": {
        "comments": [[comment(11, f"Reviewed {HEAD[:7]} against {BASE[:7]}: merge",
                              "2026-10-18T01:00:00Z")]], "reviews": [[]]},
    "a record from outside the repository": {
        "comments": [[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z", "NONE",
                              "a-stranger")]], "reviews": [[]]},
    "a record on the second page": {
        "comments": [[comment(n, "LGTM", "2026-10-18T00:00:00Z") for n in range(1, 31)],
                     [comment(31, record(HEAD, "merge"), "2026-10-18T01:00:00Z")]],
        "reviews": [[]]},
    "nothing on the pull request": {"comments": [[]], "reviews": [[]]},
}


class PullRequestFormTests(ReaderTestCase):
    """`review_record.py --pr <n>` looks the head and both listings up itself (#488), and answers
    exactly what the stdin form answers for the same listings."""

    def test_the_two_forms_agree_on_captured_listings(self):
        for name, pages in CAPTURED.items():
            with self.subTest(listings=name):
                piped = read(pages=pages)
                asked = ask("--pr", NUMBER, pages=pages)
                self.assertEqual((piped.status, piped.stdout, piped.stderr),
                                 (asked.outcome.status, asked.outcome.stdout, asked.outcome.stderr))
                self.assertEqual([VIEW_HEAD, LIST_COMMENTS, LIST_REVIEWS], asked.calls)

    def test_it_judges_the_head_gh_reports(self):
        cleared = ask("--pr", NUMBER, comments=[comment(11, record(HEAD, "merge"),
                                                        "2026-10-18T01:00:00Z")])
        self.assertCleared(cleared.outcome, f"{PULL}#issuecomment-11")
        moved = ask("--pr", NUMBER, head=OTHER, comments=[comment(11, record(HEAD, "merge"),
                                                                  "2026-10-18T01:00:00Z")])
        self.assertRefused(moved.outcome, f"no review record names {OTHER}")

    def test_a_head_given_is_judged_and_not_looked_up(self):
        # land.sh pins the merge to the head it read once, so it hands that head over: gh reporting
        # another head by now changes nothing the reader says.
        asked = ask("--pr", NUMBER, "--head", HEAD, head=OTHER,
                    comments=[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z")])
        self.assertCleared(asked.outcome, f"{PULL}#issuecomment-11")
        self.assertEqual([LIST_COMMENTS, LIST_REVIEWS], asked.calls)

    def test_a_head_given_that_no_record_clears(self):
        asked = ask("--pr", NUMBER, "--head", HEAD, comments=[comment(11, record(HEAD, "hold"),
                                                                      "2026-10-18T01:00:00Z")])
        self.assertRefused(asked.outcome, "says hold", f"{PULL}#issuecomment-11")


class PullRequestFormInputTests(ReaderTestCase):
    """What the --pr form cannot read: status 2, nothing on stdout, and gh's own words passed on."""

    def assertUnreadable(self, asked: Asked, *fragments: str):
        self.assertEqual(2, asked.outcome.status, asked.outcome.stderr)
        self.assertEqual("", asked.outcome.stdout)
        for fragment in fragments:
            self.assertIn(fragment, asked.outcome.stderr)

    def test_a_listing_gh_cannot_fetch(self):
        asked = ask("--pr", NUMBER, api_fails="gh: Server Error (HTTP 502)")
        self.assertUnreadable(asked, "HTTP 502", "gh api")
        self.assertEqual([VIEW_HEAD, LIST_COMMENTS], asked.calls)

    def test_a_head_gh_cannot_look_up(self):
        asked = ask("--pr", NUMBER, view_fails="GraphQL: Could not resolve to a PullRequest")
        self.assertUnreadable(asked, "Could not resolve to a PullRequest", "gh pr view")
        self.assertEqual([VIEW_HEAD], asked.calls)

    def test_a_head_gh_reports_that_is_not_a_full_sha(self):
        asked = ask("--pr", NUMBER, head=HEAD[:7])
        self.assertUnreadable(asked, "not a full commit sha")
        self.assertEqual([VIEW_HEAD], asked.calls)

    def test_a_listing_that_is_not_json(self):
        asked = ask("--pr", NUMBER, raw_comments=b"<html>rate limited</html>")
        self.assertUnreadable(asked, "not JSON")

    def test_gh_missing_from_path(self):
        self.assertUnreadable(ask("--pr", NUMBER, gh=False), "gh")


# A review comment carrying a record and a `## Follow-ups` table, in the shape review-queue.js
# writes (its followUpLines and cell): each Finding cell with `|` escaped as `\|`.
FOLLOW_UP_HEAD = "| Finding | Axis | Where | First carried by |"


def with_follow_ups(line: str, *findings: str) -> str:
    rows = [f"| {finding} | Standards | tools/x.py:1 | this record |" for finding in findings]
    return "\n".join(["## Review", "", line, "", "## Follow-ups", "", FOLLOW_UP_HEAD,
                      "| --- | --- | --- | --- |", *rows, "", "## Outstanding", "", "- none"])


class FollowUpsTests(ReaderTestCase):
    """With `--follow-ups` last, the reader prints, after the URL, one line per row of the clearing
    record's `## Follow-ups` table: its Finding cell, read as morning.js's followUpsOf reads it, so
    land.sh can refuse a merge body with no `Outstanding:` line that carries them (#521). Without the
    flag nothing changes, so what docs/agents/landing-work.md says the reader prints stays true."""

    def test_the_header_is_the_one_review_queue_writes_and_morning_reads(self):
        # The cases below prove the reader reads a table under FOLLOW_UP_HEAD; this one reads that
        # header back from the script that writes the table and the one that files its rows. Were
        # either renamed, the reader would find no table, land.sh's check would pass on every
        # record, and every other case here would still be green: a guard reads the thing it
        # guards (CODING_STANDARDS.md), and a check that reads nothing must not read as a pass.
        for script in ("review-queue.js", "morning.js"):
            with self.subTest(script=script):
                source = (ROOT / ".claude" / "workflows" / script).read_text(encoding="utf-8")
                found = re.search(r"^const FOLLOW_UP_HEAD = '([^']*)'$", source, re.MULTILINE)
                self.assertIsNotNone(found, f"{script} no longer defines FOLLOW_UP_HEAD on one line")
                self.assertEqual(FOLLOW_UP_HEAD, found.group(1))

    def test_each_row_follows_the_url(self):
        body = with_follow_ups(record(HEAD, "merge"), "A test that cannot fail", "An unread flag")
        outcome = read(HEAD, "--follow-ups", comments=[comment(11, body, "2026-10-18T01:00:00Z")])
        self.assertEqual(0, outcome.status, outcome.stderr)
        self.assertEqual(f"{PULL}#issuecomment-11\nA test that cannot fail\nAn unread flag\n",
                         outcome.stdout)

    def test_without_the_flag_the_url_stands_alone(self):
        body = with_follow_ups(record(HEAD, "merge"), "A test that cannot fail")
        outcome = read(comments=[comment(11, body, "2026-10-18T01:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-11")

    def test_a_record_with_no_table_prints_the_url_alone(self):
        outcome = read(HEAD, "--follow-ups",
                       comments=[comment(11, record(HEAD, "merge"), "2026-10-18T01:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-11")

    def test_a_table_with_no_row_prints_the_url_alone(self):
        outcome = read(HEAD, "--follow-ups", comments=[comment(11, with_follow_ups(record(HEAD, "merge")),
                                                               "2026-10-18T01:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-11")

    def test_an_escaped_pipe_in_a_finding_is_read_back_as_a_pipe(self):
        body = with_follow_ups(record(HEAD, "merge"), "`a \\| b` is split wrong")
        outcome = read(HEAD, "--follow-ups", comments=[comment(11, body, "2026-10-18T01:00:00Z")])
        self.assertEqual(f"{PULL}#issuecomment-11\n`a | b` is split wrong\n", outcome.stdout)

    def test_the_table_ends_at_the_first_line_that_is_not_a_row(self):
        # A row with the wrong number of cells, or an empty Finding, is not a follow-up; the table
        # ends at the first line not bracketed by pipes, as morning.js reads it.
        body = "\n".join([record(HEAD, "merge"), "", FOLLOW_UP_HEAD, "| --- | --- | --- | --- |",
                          "| First | Spec | here | this record |", "|  | Spec | here | this record |",
                          "| Three | cells | only |", "", "| After the table | Spec | here | x |"])
        outcome = read(HEAD, "--follow-ups", comments=[comment(11, body, "2026-10-18T01:00:00Z")])
        self.assertEqual(f"{PULL}#issuecomment-11\nFirst\n", outcome.stdout)

    def test_only_the_clearing_record_s_table_is_read(self):
        held = with_follow_ups(record(HEAD, "hold"), "Carried by the hold")
        outcome = read(HEAD, "--follow-ups",
                       comments=[comment(11, held, "2026-10-18T01:00:00Z"),
                                 comment(12, record(HEAD, "merge"), "2026-10-18T02:00:00Z")])
        self.assertCleared(outcome, f"{PULL}#issuecomment-12")

    def test_a_refusal_prints_no_rows(self):
        body = with_follow_ups(record(HEAD, "hold"), "A finding")
        outcome = read(HEAD, "--follow-ups", comments=[comment(11, body, "2026-10-18T01:00:00Z")])
        self.assertRefused(outcome, "says hold")

    def test_the_two_forms_agree_with_the_flag(self):
        body = with_follow_ups(record(HEAD, "merge"), "A test that cannot fail")
        pages = {"comments": [[comment(11, body, "2026-10-18T01:00:00Z")]], "reviews": [[]]}
        piped = read(HEAD, "--follow-ups", pages=pages)
        for arguments in (["--pr", NUMBER, "--follow-ups"],
                          ["--pr", NUMBER, "--head", HEAD, "--follow-ups"]):
            with self.subTest(arguments=arguments):
                asked = ask(*arguments, pages=pages)
                self.assertEqual((piped.status, piped.stdout, piped.stderr),
                                 (asked.outcome.status, asked.outcome.stdout, asked.outcome.stderr))
        self.assertEqual(f"{PULL}#issuecomment-11\nA test that cannot fail\n", piped.stdout)


class ArgumentTests(ReaderTestCase):
    """A wrong argument is refused, before any input is read or any gh call made, with both forms
    named, so a caller holding a number or a head learns the form that takes it."""

    def assertWrongArguments(self, *arguments: str):
        asked = ask(*arguments)
        self.assertEqual(2, asked.outcome.status, asked.outcome.stderr)
        self.assertEqual("", asked.outcome.stdout)
        self.assertIn(PR_FORM, asked.outcome.stderr)
        self.assertIn(STDIN_FORM, asked.outcome.stderr)
        self.assertEqual([], asked.calls)

    def test_a_pull_request_number_where_a_head_is_expected(self):
        self.assertWrongArguments(NUMBER)

    def test_a_head_where_a_pull_request_number_is_expected(self):
        self.assertWrongArguments("--pr", HEAD)

    def test_other_wrong_arguments(self):
        for arguments in ([], ["--pr"], ["--pr", "0"], ["--pr", "-7"], ["--pr", NUMBER, HEAD],
                          ["--head", HEAD], ["--pr", NUMBER, "--head"],
                          ["--pr", NUMBER, "--head", HEAD[:7]], ["--pr", NUMBER, "--head", NUMBER],
                          ["--pr", NUMBER, "--pr", NUMBER], [HEAD, HEAD], ["--merge"],
                          ["--follow-ups"], ["--follow-ups", HEAD], ["--pr", NUMBER, "--follow-ups", HEAD],
                          [HEAD, "--follow-ups", "--follow-ups"], ["--pr", "--follow-ups", NUMBER]):
            with self.subTest(arguments=arguments):
                self.assertWrongArguments(*arguments)


if __name__ == "__main__":
    unittest.main()
