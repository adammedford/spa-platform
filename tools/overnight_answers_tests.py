#!/usr/bin/env python3
"""Test companion for tools/overnight_answers.py: which questions on the open `Overnight <date>`
issues wait on the pull request being landed, and whether a Confirm comment covers each.

Owner: integrator. Built for #531.

**The seam is the command land.sh runs.** Every case starts the real file as `python3 -I
tools/overnight_answers.py --number <n> ...` and hands it, on standard input, the object the
--lookup form builds from gh: `{"issues": <pages>, "comments": {"<n>": <pages>}}`, the open issues
as `gh api repos/{owner}/{repo}/issues?state=open --paginate --slurp` pages them and each Overnight
issue's comments as the same pagination of its comments endpoint. It asserts the exit status, the
lines on standard output (the ones land.sh prints before its merge) and the fragment of standard
error that says why nothing could be read. Nothing is imported, so no case can reach a part of the
reader that land.sh cannot.

**The --lookup form has the same seam, with gh doubled.** `overnight_answers.py --lookup` makes the
gh calls itself, so its cases put a strict gh double alone on PATH that serves the listing and the
comments and records every call; any other call exits 97. One case runs the same fixtures through
both forms and asserts the same output, so the two cannot drift apart.

**The rules are the morning's.** A questions comment, a question and a Confirm comment are read
as .claude/workflows/morning.js reads them ("the questions, read here"): the heading
`## Questions: #<n>, <task>, <title>`, the `(part k of m)` suffix, the marks `**Q<n>.**`, `**Q<n>**`
and `Q<n>.`, and a Confirm comment by the human's account whose first sentence names the questions
it covers, with its `Q<n>:` lines covering too. The fixtures are written out here in those shapes,
not read from either reader.

Run: python3 -I tools/overnight_answers_tests.py
"""

from __future__ import annotations

import json
import pathlib
import shlex
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
READER = ROOT / "tools" / "overnight_answers.py"
HUMAN = "adammedford"
GUIDE = ("# Tracker policy\n\n- **Who may decide.** Not `@someone-else`.\n"
         f"- **The human's account is `@{HUMAN}`.** A `Confirm` comment is the human's act.\n"
         "- **Something else.** `@another` is named in passing.\n")
PR = 5
NIGHT = 528
TITLE = "Overnight 2026-10-29"
T1, T2 = "2026-10-29T01:00:00Z", "2026-10-29T02:00:00Z"


def issue(number: int = NIGHT, title: str = TITLE, **extra) -> dict:
    """An issue as the list endpoint gives it:
    https://docs.github.com/en/rest/issues/issues#list-repository-issues"""
    found = {"number": number, "title": title, "state": "open",
             "html_url": f"https://github.com/adammedford/spa-platform/issues/{number}"}
    found.update(extra)
    return found


def comment(body: str | None, at: str = T1, login: str = HUMAN, n: int = 1) -> dict:
    return {"id": n, "html_url": f"https://github.com/adammedford/spa-platform/issues/{NIGHT}"
                                 f"#issuecomment-{n}",
            "body": body, "created_at": at, "updated_at": at, "user": {"login": login},
            "author_association": "OWNER"}


def questions(*blocks: str, source: int = 495, task: str = "issue-495",
              title: str = "tracker-policy.md gains a Change requests section",
              part: str = "") -> str:
    """A questions comment in the shape .claude/workflows/overnight.js posts (its step 1b)."""
    return f"## Questions: #{source}, {task}, {title}{part}\n\nSome prose first.\n\n" + \
        "\n\n".join(blocks) + "\n"


def question(n: int, text: str, source: str = "(#495 Q1)", recommended: str = "yes.",
             answer: str = "open") -> str:
    return f"**Q{n}.** {source} {text}\nRecommended: {recommended}\nAnswer: {answer}"


class Outcome:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout.decode("utf-8")
        self.stderr = completed.stderr.decode("utf-8")


class ReaderTestCase(unittest.TestCase):
    def guide(self, text: str | None = GUIDE) -> list[str]:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = pathlib.Path(directory.name) / "tracker-policy.md"
        if text is not None:
            path.write_text(text)
        return ["--guide", str(path)]

    def read(self, comments: list | None = None, *, issues: list | None = None,
             numbers: tuple[int, ...] = (PR,), payload: object = None, raw: bytes | None = None,
             guide: str | None = GUIDE, arguments: tuple[str, ...] = ()) -> Outcome:
        """Run the stdin form: one page of open issues (one Overnight issue by default) and one
        page of each one's comments."""
        if raw is None:
            if payload is None:
                payload = {"issues": [issues if issues is not None else [issue()]],
                           "comments": {str(NIGHT): [comments or []]}}
            raw = json.dumps(payload).encode("utf-8")
        number_args = [arg for n in numbers for arg in ("--number", str(n))]
        completed = subprocess.run([sys.executable, "-I", str(READER), *number_args,
                                    *self.guide(guide), *arguments],
                                   input=raw, capture_output=True, timeout=30)
        return Outcome(completed)

    def assertLines(self, outcome: Outcome, *lines: str):
        self.assertEqual(0, outcome.status, outcome.stderr)
        self.assertEqual(list(lines), outcome.stdout.splitlines())

    def assertUnreadable(self, outcome: Outcome, *fragments: str):
        self.assertEqual(2, outcome.status, outcome.stderr)
        self.assertEqual("", outcome.stdout, "unreadable input printed a line land.sh would show")
        for fragment in fragments:
            self.assertIn(fragment, outcome.stderr)


class MatchingTests(ReaderTestCase):
    """Which questions name one of the numbers given."""

    def test_a_question_naming_the_pull_request_is_printed_with_its_first_line(self):
        body = questions(question(1, "Does #495 need no lane?"),
                         question(3, f"Should this land with #{PR}, whose merge body cites it?\n"
                                     "A second line of the question."))
        self.assertLines(self.read([comment(body)]),
                         f"#{NIGHT} {TITLE} Q3 names #{PR}, no Confirm covers it: (#495 Q1) Should "
                         f"this land with #{PR}, whose merge body cites it?")

    def test_a_question_naming_a_ticket_the_description_closes_is_printed_the_same_way(self):
        body = questions(question(2, "Does #495 close at this merge? It should."))
        self.assertLines(self.read([comment(body)], numbers=(PR, 495)),
                         f"#{NIGHT} {TITLE} Q2 names #495, no Confirm covers it: (#495 Q1) Does #495 "
                         "close at this merge? It should.")

    def test_a_question_naming_several_prints_one_line_with_the_first_number_given(self):
        # Argument order decides, not the text's: the text names #495 first, and land.sh gives the
        # pull request's number before the numbers it closes, so the pull request is the one named.
        # The two orders together tell the first given from the last given and from the first in
        # the text (#1079).
        body = questions(question(2, f"Does #495 close with #{PR}?"))
        self.assertLines(self.read([comment(body)], numbers=(PR, 495)),
                         f"#{NIGHT} {TITLE} Q2 names #{PR}, no Confirm covers it: (#495 Q1) Does #495 "
                         f"close with #{PR}?")
        self.assertLines(self.read([comment(body)], numbers=(495, PR)),
                         f"#{NIGHT} {TITLE} Q2 names #495, no Confirm covers it: (#495 Q1) Does #495 "
                         f"close with #{PR}?")

    def test_the_heading_ticket_selects_nothing_by_itself(self):
        # The heading names #495; Q1's text and source tag name #497, so --number 495 finds
        # nothing, while Q2, whose tag names #495, is found by its own text (#531).
        body = questions(question(1, "Does #497 need a lane?", source="(#497 Q1)"),
                         question(2, "Does it close here?"))
        self.assertLines(self.read([comment(body)], numbers=(495,)),
                         f"#{NIGHT} {TITLE} Q2 names #495, no Confirm covers it: (#495 Q1) Does it "
                         "close here?")

    def test_a_number_is_matched_whole_and_in_any_line_of_the_question(self):
        # #5 is not in #50 (a digit after it), nor in the entity &#5; (an `&` before it), nor in the
        # fragment README.md#5 (a word character before it); each of Q1, Q2 and Q5 holds `#5` only
        # in that one shape, so a matcher missing that half of the rule lists it (#902). The word
        # characters are the lookbehind's four arms, a lower-case letter (Q5), an upper-case letter
        # (Q6), a digit (Q7) and `_` (Q8), each in a question of its own, so dropping any one arm
        # lists its question (#903). It is found in a Recommended or Answer line, which are the
        # question's too.
        body = questions(question(1, "Is #50 the one?"),
                         question(2, "Is the entity &#5; a number?"),
                         question(3, "Which merge?", recommended=f"the one of #{PR}."),
                         question(4, "And this?", answer=f"yes, at #{PR}'s merge"),
                         question(5, "See https://github.com/x/y/blob/main/README.md#5 for it."),
                         question(6, "See the anchor README#5 for it."),
                         question(7, "See the anchor v2#5 for it."),
                         question(8, "See the anchor step_#5 for it."))
        self.assertLines(self.read([comment(body)]),
                         f"#{NIGHT} {TITLE} Q3 names #{PR}, no Confirm covers it: (#495 Q1) Which merge?",
                         f"#{NIGHT} {TITLE} Q4 names #{PR}, no Confirm covers it: (#495 Q1) And this?")

    def test_each_mark_form_opens_a_question(self):
        body = questions(f"**Q1.** (#495 Q1) First, with #{PR}.", f"**Q2** Second, with #{PR}.",
                         f"Q3. Third, with #{PR}.", f"Q4 is not a mark, with #{PR}.")
        self.assertLines(self.read([comment(body)]),
                         f"#{NIGHT} {TITLE} Q1 names #{PR}, no Confirm covers it: (#495 Q1) First, with #{PR}.",
                         f"#{NIGHT} {TITLE} Q2 names #{PR}, no Confirm covers it: Second, with #{PR}.",
                         f"#{NIGHT} {TITLE} Q3 names #{PR}, no Confirm covers it: Third, with #{PR}.")

    def test_a_plain_mark_is_followed_by_a_space_or_a_tab(self):
        # A tab opens a question as a space does (Q2). A digit (Q3), a letter of either case (Q4,
        # Q5), punctuation (Q6) or a line break (Q7) after the full stop does not, so each of those
        # lines stays Q2's text, and the only line printed is Q2's. #5 is in none of Q1's or Q2's
        # own words, so a mark read at any of them takes the number with it and changes the line.
        # Together they tell the space-or-tab rule from a list of what may not follow (#1079).
        body = questions("**Q1.** First.", "Q2.\tSecond.", f"Q3.5 is a version, with #{PR}.",
                         f"Q4.x is a name, with #{PR}.", f"Q5.X is a name, with #{PR}.",
                         f"Q6.) is punctuation, with #{PR}.", f"Q7.\nA line after it, with #{PR}.")
        self.assertLines(self.read([comment(body)]),
                         f"#{NIGHT} {TITLE} Q2 names #{PR}, no Confirm covers it: Second.")

    def test_no_matching_question_prints_nothing(self):
        body = questions(question(1, "Does #495 need no lane?"))
        self.assertLines(self.read([comment(body)]))
        self.assertLines(self.read([]))

    def test_the_parts_of_a_split_comment_are_joined_in_order(self):
        # Q7's tail, naming the pull request, is in part 2, which is listed before part 1.
        second = questions("A line continuing Q7.\nSo it waits on #%d." % PR, part=" (part 2 of 2)")
        first = questions("**Q7.** (#495 Q2) A question cut at the boundary.", part=" (part 1 of 2)")
        self.assertLines(self.read([comment(second, T1, n=1), comment(first, T2, n=2)]),
                         f"#{NIGHT} {TITLE} Q7 names #{PR}, no Confirm covers it: (#495 Q2) A question "
                         "cut at the boundary.")

    def test_only_issues_titled_overnight_date_are_read_and_pull_requests_are_not(self):
        body = questions(question(1, f"Waits on #{PR}."))
        payload = {"issues": [[issue(533, "Define the glossary terms raised by Overnight 2026-10-29"),
                               issue(529, TITLE, pull_request={"url": "x"}), issue(),
                               issue(530, " Overnight 2026-10-30 ")]],
                   "comments": {"533": [[comment(body)]], "529": [[comment(body)]],
                                str(NIGHT): [[comment(body)]], "530": [[comment(body)]]}}
        self.assertLines(self.read(payload=payload),
                         f"#{NIGHT} {TITLE} Q1 names #{PR}, no Confirm covers it: (#495 Q1) Waits on #{PR}.",
                         f"#530 Overnight 2026-10-30 Q1 names #{PR}, no Confirm covers it: (#495 Q1) Waits on #{PR}.")


class ConfirmTests(ReaderTestCase):
    """Whether a Confirm comment on the issue covers the question, by the morning's rule."""

    BODY = questions(question(1, f"One, at #{PR}."), question(2, f"Two, at #{PR}."),
                     question(3, f"Three, at #{PR}."), question(5, f"Five, at #{PR}."))

    def verdicts(self, *confirms: dict) -> list[str]:
        outcome = self.read([comment(self.BODY, n=1), *confirms])
        self.assertEqual(0, outcome.status, outcome.stderr)
        return [line.split(", ", 1)[1].split(":")[0] for line in outcome.stdout.splitlines()]

    def test_a_range_in_the_first_sentence_covers_each_question_in_it(self):
        for text in ("Confirm Q1 to Q2.", "Confirm Q1-Q2, with overrides below.", "Confirm Q1–2.",
                     "Confirm Q1 through Q2"):
            with self.subTest(text=text):
                self.assertEqual(["a Confirm covers it", "a Confirm covers it", "no Confirm covers it",
                                  "no Confirm covers it"], self.verdicts(comment(text, T2, n=2)))

    def test_single_numbers_in_the_first_sentence_cover(self):
        self.assertEqual(["no Confirm covers it", "a Confirm covers it", "no Confirm covers it",
                          "a Confirm covers it"], self.verdicts(comment("Confirm Q2 and Q5.", T2, n=2)))

    def test_a_number_named_after_the_first_sentence_does_not_cover(self):
        self.assertEqual(["a Confirm covers it", "no Confirm covers it", "no Confirm covers it",
                          "no Confirm covers it"],
                         self.verdicts(comment("Confirm Q1. Q2 and Q3 stay open.", T2, n=2)))

    def test_a_number_named_after_the_first_line_does_not_cover(self):
        # The first line has no full stop, so only the line break can end the first sentence: read
        # past it, the sentence runs to the stop on the second line and takes Q2 and Q3 in (#1079).
        self.assertEqual(["a Confirm covers it", "no Confirm covers it", "no Confirm covers it",
                          "no Confirm covers it"],
                         self.verdicts(comment("Confirm Q1 only\nQ2 and Q3 stay open.", T2, n=2)))

    def test_an_override_line_covers_and_a_bare_confirm_covers_nothing_else(self):
        self.assertEqual(["no Confirm covers it", "no Confirm covers it", "a Confirm covers it",
                          "no Confirm covers it"],
                         self.verdicts(comment("Confirm, with the overrides below.\n\nQ3: no, keep it.",
                                               T2, n=2)))

    def test_every_confirm_comment_counts(self):
        self.assertEqual(["a Confirm covers it", "no Confirm covers it", "a Confirm covers it",
                          "no Confirm covers it"],
                         self.verdicts(comment("Confirm Q1.", T1, n=2), comment("Confirm Q3.", T2, n=3)))

    def test_a_confirm_from_another_account_or_not_opening_confirm_does_not_count(self):
        self.assertEqual(["no Confirm covers it"] * 4,
                         self.verdicts(comment("Confirm Q1 to Q5.", T2, login="a-stranger", n=2),
                                       comment("I would Confirm Q1 to Q5.", T2, n=3),
                                       comment("Confirmation pending for Q1 to Q5.", T2, n=4)))


class UnreadableTests(ReaderTestCase):
    """Input the reader cannot judge: exit 2, nothing on standard output, the reason on standard
    error."""

    def test_wrong_arguments(self):
        for arguments, fragment in ((("--lookup", "--number"), "cannot read the argument '--number'"),
                                    (("--what",), "cannot read the argument '--what'")):
            with self.subTest(arguments=arguments):
                self.assertUnreadable(self.read(arguments=arguments, numbers=()), fragment, "two forms")
        self.assertUnreadable(self.read(numbers=()), "no --number was given")

    def test_standard_input_that_is_not_the_shape(self):
        self.assertUnreadable(self.read(raw=b"not json"), "standard input is not JSON")
        self.assertUnreadable(self.read(payload={"issues": [[]]}), "lacks the issues or the comments")
        self.assertUnreadable(self.read(payload={"issues": [issue()], "comments": {}}),
                              "the open issues are not a list of pages")
        self.assertUnreadable(self.read(payload={"issues": [[{"title": TITLE}]], "comments": {}}),
                              "an open issue has no number or no title")
        self.assertUnreadable(self.read(payload={"issues": [[issue()]], "comments": {str(NIGHT): [1]}}),
                              f"the comments of #{NIGHT} are not a list of pages")

    def test_a_guide_naming_no_account_or_missing(self):
        self.assertUnreadable(self.read(guide="# Tracker policy\n\nNo account.\n"), "names no account")
        self.assertUnreadable(self.read(guide=None), "cannot be read")


GH_DOUBLE = """#!/bin/bash
# A double for gh, written by tools/overnight_answers_tests.py for one case: it serves the open
# issues listing and each issue's comments from files, records every call, and exits 97 on any other.
log={log}
n=0
while [ -e "$log/call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$@" > "$log/call-$n"
if [ -n {fails} ]; then printf '%s\\n' {fails} >&2; exit 1; fi
[ "$#" -eq 4 ] && [ "$1" = api ] && [ "$3" = --paginate ] && [ "$4" = --slurp ] || exit 97
case "$2" in
  "repos/{{owner}}/{{repo}}/issues?state=open&per_page=100") /bin/cat -- {issues} ;;
  "repos/{{owner}}/{{repo}}/issues/"*/comments)
    number="${{2%/comments}}"; number="${{number##*/}}"
    if [ -e {fixtures}/"$number".json ]; then /bin/cat -- {fixtures}/"$number".json
    else printf '[[]]\\n'; fi ;;
  *) exit 97 ;;
esac
"""


class LookupTests(ReaderTestCase):
    """The --lookup form: the same lines from the same fixtures served by a gh double, and the
    calls it makes."""

    def lookup(self, payload: dict, fails: str = "") -> tuple[Outcome, list[list[str]]]:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = pathlib.Path(directory.name)
        (root / "bin").mkdir()
        (root / "log").mkdir()
        (root / "fixtures").mkdir()
        (root / "issues.json").write_text(json.dumps(payload["issues"]))
        for number, pages in payload["comments"].items():
            (root / "fixtures" / f"{number}.json").write_text(json.dumps(pages))
        double = root / "bin" / "gh"
        double.write_text(GH_DOUBLE.format(log=shlex.quote(str(root / "log")), fails=shlex.quote(fails),
                                           issues=shlex.quote(str(root / "issues.json")),
                                           fixtures=shlex.quote(str(root / "fixtures"))))
        double.chmod(0o755)
        completed = subprocess.run([sys.executable, "-I", str(READER), "--number", str(PR), "--number",
                                    "495", "--lookup", *self.guide()], capture_output=True, timeout=30,
                                   env={"PATH": str(root / "bin"), "HOME": str(root)})
        calls = [path.read_bytes().decode().split("\0")[:-1]
                 for path in sorted((root / "log").glob("call-*"),
                                    key=lambda path: int(path.name.split("-")[1]))]
        return Outcome(completed), calls

    def test_both_forms_print_the_same_lines(self):
        body = questions(question(1, f"Waits on #{PR}."), question(2, "Closes #495 then."),
                         question(3, "Nothing here."))
        payload = {"issues": [[issue(), issue(530, "Overnight 2026-10-30"), issue(531, "land.sh lists")]],
                   "comments": {str(NIGHT): [[comment(body), comment("Confirm Q1.", T2, n=2)]],
                                "530": [[comment(questions(question(4, f"Also #{PR}.")))]]}}
        through_stdin = self.read(payload=payload, numbers=(PR, 495))
        through_gh, calls = self.lookup(payload)
        self.assertEqual(0, through_stdin.status, through_stdin.stderr)
        self.assertEqual(0, through_gh.status, through_gh.stderr)
        self.assertEqual(through_stdin.stdout, through_gh.stdout)
        self.assertEqual(4, len(through_gh.stdout.splitlines()), through_gh.stdout)
        self.assertEqual([["api", "repos/{owner}/{repo}/issues?state=open&per_page=100", "--paginate",
                           "--slurp"],
                          ["api", f"repos/{{owner}}/{{repo}}/issues/{NIGHT}/comments", "--paginate",
                           "--slurp"],
                          ["api", "repos/{owner}/{repo}/issues/530/comments", "--paginate", "--slurp"]],
                         calls)

    def test_a_gh_call_that_fails_is_unreadable_with_its_words(self):
        outcome, calls = self.lookup({"issues": [[issue()]], "comments": {}},
                                     fails="gh: Server Error (HTTP 502)")
        self.assertUnreadable(outcome, "HTTP 502", "failed with status 1")
        self.assertEqual(1, len(calls))


if __name__ == "__main__":
    unittest.main()
