#!/usr/bin/env python3
"""Reads a pull request's review records and says whether one clears the head about to merge.

Owner: integrator. Decided in #67 (archived overnight ticket 70, Q1, Q3, Q6, Q7 and Q14, which Adam
confirmed on #67). tools/land.sh runs it before every merge it makes without `--unreviewed`:

    printf '{"comments": %s, "reviews": %s}\n' "$comments" "$reviews" \
      | python3 -I tools/review_record.py <40-hex head>

where the two values are `gh api --paginate --slurp` listings of the pull request's issue comments
and its reviews. It prints the clearing record's URL and exits 0, or says on standard error why
nothing clears the head and exits 1. Input it cannot read exits 2. land.sh stops on either.

**The record is one fixed line**, specified in docs/agents/review.md -> "Landing or holding":

    Reviewed <40-hex head> against <40-hex base>: merge        (or `: hold`)

on a line of its own, in a pull request comment or in the body of a review whose state is
`COMMENTED`. Whitespace around the line is ignored, because Markdown renders it the same and nobody
can see it; anything else -- backticks, an abbreviated sha, another verdict word -- makes the line
not a record, and the refusal names it so the reviewer can post it again in the form.

**The latest record naming the head decides.** Comments are ordered by `created_at` and reviews by
`submitted_at`. A record of another head is not read for this one: a review covers exactly the head
it read (Q1), so a commit pushed after it needs a record of its own. When two records of the head
share the latest instant -- one comment carrying both verdicts is the plain case -- a hold among
them holds.

**What is not a record**, and why each is refused rather than trusted:

- A sha mentioned in passing. a80e906, #34's merge body, named the merged head as the commit the
  fixes went into, and that head had been verified only by the session that wrote it.
- A review's `commit_id`. GitHub "defaults to the most recent commit in the pull request when you do
  not specify a value", so a review submitted after a push attaches to a head its reviewer never
  read: https://docs.github.com/en/rest/pulls/reviews#create-a-review-for-a-pull-request
- A review that is not `COMMENTED`. The one account here cannot approve or request changes on its
  own pull request, a dismissed review was withdrawn, and a pending one is unsubmitted.

Every comment and review here is written by the same GitHub account, so the reader cannot tell who
wrote a record. It is a sequencing guard against haste, not a check of identity (Q3).

The fields read are GitHub's REST shapes, and nothing else of them:
https://docs.github.com/en/rest/issues/comments#list-issue-comments and
https://docs.github.com/en/rest/pulls/reviews#list-reviews-for-a-pull-request

Test companion: tools/review_record_tests.py. Kept 3.9-compatible (docs/dependencies.md, D-3).
"""

from __future__ import annotations

import datetime
import json
import re
import sys

FORM = "Reviewed <40-hex head> against <40-hex base>: merge (or : hold)"
FULL_SHA = re.compile(r"[0-9a-f]{40}")
RECORD = re.compile(r"Reviewed (?P<head>[0-9a-f]{40}) against (?P<base>[0-9a-f]{40}): "
                    r"(?P<verdict>merge|hold)")
# A line a reviewer probably meant as a record: the word, then something sha-shaped. Read only to
# say why it did not count, never to clear anything.
NEAR_MISS = re.compile(r"\breviewed\b.*\b[0-9a-fA-F]{7,}\b", re.IGNORECASE)
TIME_FORMAT = "%Y-%m-%dT%H:%M:%SZ"


class Unreadable(Exception):
    """Input the reader cannot judge. land.sh stops on it as on a refusal."""


class Record:
    def __init__(self, head: str, verdict: str, at: datetime.datetime, url: str):
        self.head = head
        self.verdict = verdict
        self.at = at
        self.url = url


def entries(listing: object, name: str) -> list[dict]:
    """The objects of a `gh api --paginate --slurp` listing: a list of pages, each a list."""
    if not isinstance(listing, list) or not all(isinstance(page, list) for page in listing):
        raise Unreadable(f"{name} is not a list of pages, which `gh api --paginate --slurp` gives")
    found = [entry for page in listing for entry in page]
    if not all(isinstance(entry, dict) for entry in found):
        raise Unreadable(f"{name} holds something other than objects")
    return found


def when(entry: dict, field: str, name: str) -> datetime.datetime:
    value = entry.get(field)
    try:
        return datetime.datetime.strptime(value, TIME_FORMAT)
    except (TypeError, ValueError):
        raise Unreadable(f"a record in {name} has no {field} in GitHub's form: {value!r}") from None


def read_entry(entry: dict, at_field: str, name: str, near_misses: list[str]) -> list[Record]:
    body = entry.get("body") or ""
    if not isinstance(body, str):
        raise Unreadable(f"a body in {name} is not text")
    records = []
    for line in body.splitlines():
        line = line.strip()
        match = RECORD.fullmatch(line)
        if match is None:
            if NEAR_MISS.search(line):
                near_misses.append(f"{entry.get('html_url', name)}: {line}")
            continue
        url = entry.get("html_url")
        if not isinstance(url, str) or not url:
            raise Unreadable(f"a record in {name} has no html_url")
        records.append(Record(match["head"], match["verdict"], when(entry, at_field, name), url))
    return records


def records(listings: object) -> tuple[list[Record], list[str]]:
    if not isinstance(listings, dict):
        raise Unreadable("the input is not an object holding comments and reviews")
    for name in ("comments", "reviews"):
        if name not in listings:
            raise Unreadable(f"the input has no {name}")
    found: list[Record] = []
    near_misses: list[str] = []
    for entry in entries(listings["comments"], "comments"):
        found += read_entry(entry, "created_at", "comments", near_misses)
    for entry in entries(listings["reviews"], "reviews"):
        if entry.get("state") == "COMMENTED":
            found += read_entry(entry, "submitted_at", "reviews", near_misses)
    return found, near_misses


def judge(head: str, listings: object) -> tuple[str | None, list[str]]:
    """The URL of the record that clears `head`, or None and the lines that say why not."""
    found, near_misses = records(listings)
    naming = [record for record in found if record.head == head]
    if naming:
        latest = max(record.at for record in naming)
        decisive = [record for record in naming if record.at == latest]
        holds = [record for record in decisive if record.verdict == "hold"]
        if not holds:
            return decisive[-1].url, []
        reasons = [f"the latest review record of {head} says hold: {holds[0].url}"]
    else:
        reasons = [f"no review record names {head}, the head this pull request would merge."]
        others = sorted(found, key=lambda record: record.at)
        for record in others[-3:]:
            reasons.append(f"a record names {record.head} ({record.verdict}): {record.url}. A "
                           "review covers the head it read; commits pushed since need their own.")
    for line in near_misses:
        reasons.append(f"not in the record's form, so not read: {line}")
    reasons.append(f"the form, on a line of its own: {FORM}. docs/agents/review.md -> "
                   "Landing or holding.")
    return None, reasons


def say(text: str):
    sys.stderr.buffer.write(f"review_record: {text}\n".encode("utf-8", "backslashreplace"))


def main(arguments: list[str]) -> int:
    if len(arguments) != 1 or not FULL_SHA.fullmatch(arguments[0]):
        say(f"the head must be a full commit sha, and {arguments!r} is not a full commit sha.")
        return 2
    try:
        listings = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        say("standard input is not JSON; land.sh hands it two `gh api` listings.")
        return 2
    try:
        url, reasons = judge(arguments[0], listings)
    except Unreadable as problem:
        say(f"cannot read the records: {problem}.")
        return 2
    if url is None:
        for reason in reasons:
            say(reason)
        return 1
    print(url)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
