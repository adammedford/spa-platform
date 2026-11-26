#!/usr/bin/env python3
"""Decides whether a newly opened issue came from outside the repository. Owner: integrator.

    python3 -I .github/outside-issues.py <event.json>

.github/workflows/outside-issues.yml runs this on `issues: opened` with the event payload GitHub
writes to $GITHUB_EVENT_PATH, and writes what it decides (#1547). The repository and its tracker
are public, so anyone signed in to GitHub can open an issue; the rules already stop such an issue
from causing work, and this brings it to triage: Adam finds it with
`gh issue list --label needs-triage`.

Outside is an `author_association` other than OWNER, MEMBER or COLLABORATOR, the test
docs/agents/review.md applies to a review record. So CONTRIBUTOR, FIRST_TIME_CONTRIBUTOR,
FIRST_TIMER, NONE and MANNEQUIN are outside, as is any value GitHub adds later. An issue opened
through the human's account, which every session posts through, is OWNER and is left alone.

The last line printed is the verdict, and the exit code says whether one was reached:

    outside-issues: label: #<n> needs-triage: author_association <value> is from outside the repository
    outside-issues: leave: #<n>: author_association <value> is inside the repository
    outside-issues: fail: ...                                                         (exit 2)

A payload with no `author_association` (absent, null, empty or not a string) fails, naming the
issue, rather than reading as either side: a label on a field nobody read would be a judgement
nobody made, and leaving it alone would let an outside issue through unseen. The verdict names the
issue by number only. The title and body are written by whoever opened the issue, and nothing here
prints them into the Actions log.
"""
from __future__ import annotations

import json
import sys

LABEL = "needs-triage"
INSIDE = ("OWNER", "MEMBER", "COLLABORATOR")


def fail(reason: str) -> int:
    print(f"outside-issues: fail: {reason}")
    return 2


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        return fail("usage: outside-issues.py <event.json>")
    try:
        with open(argv[0], encoding="utf-8") as handle:
            event = json.load(handle)
    except (OSError, ValueError) as error:
        return fail(f"the payload could not be read ({type(error).__name__}); no label is written")
    issue = event.get("issue") if isinstance(event, dict) else None
    number = issue.get("number") if isinstance(issue, dict) else None
    if not isinstance(number, int) or isinstance(number, bool):
        return fail("the payload names no issue; no label is written")
    association = issue.get("author_association")
    if not isinstance(association, str) or not association:
        return fail(f"#{number}: the payload carries no author_association; no label is written")
    if association in INSIDE:
        print(f"outside-issues: leave: #{number}: author_association {association} is inside the "
              f"repository")
    else:
        print(f"outside-issues: label: #{number} {LABEL}: author_association {association} is from "
              f"outside the repository")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
