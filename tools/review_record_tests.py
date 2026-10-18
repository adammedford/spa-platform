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

**The fixtures are GitHub's shapes.** Each comment and review is built with the fields GitHub's REST
API returns, and the reader reads only some of them:
https://docs.github.com/en/rest/issues/comments#list-issue-comments and
https://docs.github.com/en/rest/pulls/reviews#list-reviews-for-a-pull-request. `gh api --slurp`
wraps each page in an outer array (`gh api --help`), which is why every fixture is a list of pages.

**Each refusal has a case that shows it firing**, against an input that would merge if the rule were
gone: a hold, a record of an earlier head, an abbreviated or malformed line, a sha mentioned in
passing, a review's own `commit_id`, a review that is not `COMMENTED`. docs/agents/review.md ->
"Landing or holding" fixes the record line these cases hold the reader to.

Run: python3 -I tools/review_record_tests.py
"""

from __future__ import annotations

import json
import pathlib
import subprocess
import sys
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


def comment(n: int, body: str | None, at: str) -> dict:
    return {"id": n, "html_url": f"{PULL}#issuecomment-{n}", "body": body, "created_at": at,
            "updated_at": at, "user": {"login": "adammedford"}, "author_association": "OWNER"}


def review(n: int, body: str | None, at: str | None, state: str = "COMMENTED",
           commit_id: str = EARLIER) -> dict:
    return {"id": n, "html_url": f"{PULL}#pullrequestreview-{n}", "body": body, "state": state,
            "submitted_at": at, "commit_id": commit_id, "user": {"login": "adammedford"},
            "author_association": "OWNER"}


class Outcome:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout.decode("utf-8")
        self.stderr = completed.stderr.decode("utf-8")


def read(head: str = HEAD, comments: list | None = None, reviews: list | None = None, *,
         pages: dict | None = None, raw: bytes | None = None) -> Outcome:
    """Run the reader as land.sh does. `comments` and `reviews` go in as one page each."""
    if raw is None:
        payload = pages if pages is not None else {"comments": [comments or []],
                                                   "reviews": [reviews or []]}
        raw = json.dumps(payload).encode("utf-8")
    completed = subprocess.run([sys.executable, "-I", str(READER), head], input=raw,
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

    def test_an_empty_body_is_no_record_rather_than_unreadable(self):
        # GitHub returns a review with no summary as an empty body, and an older one as null.
        outcome = read(reviews=[review(21, None, "2026-10-18T01:00:00Z"),
                                review(22, "", "2026-10-18T01:00:01Z")])
        self.assertRefused(outcome, f"no review record names {HEAD}")


if __name__ == "__main__":
    unittest.main()
