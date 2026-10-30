#!/usr/bin/env python3
"""Reads a pull request's review records and says whether one clears the head about to merge.

Owner: integrator. Decided in #67 (archived overnight ticket 70, Q1, Q3, Q6, Q7 and Q14, which Adam
confirmed on #67). Two forms, which give the same answer for the same pull request (#488):

    python3 -I tools/review_record.py --pr <number> [--head <40-hex head>] [--follow-ups]
    python3 -I tools/review_record.py <40-hex head> [--follow-ups]  < listings

**The `--pr` form looks everything up itself**, with gh, from the repository the working directory
is in: the pull request's current head (`gh pr view <number> --json headRefOid --jq .headRefOid`),
unless `--head` names the head to judge, and its issue comments and reviews (`gh api
repos/{owner}/{repo}/issues/<number>/comments --paginate --slurp` and the same of
`pulls/<number>/reviews`). tools/land.sh runs it before every merge it makes without `--unreviewed`,
and passes `--head`, because it pins the merge to the head it read once and a second read could
answer a head pushed since. **The stdin form** is for a caller already holding the two listings: it
reads `{"comments": <pages>, "reviews": <pages>}` on standard input, each value as `gh api
--paginate --slurp` gives it.

Either prints the clearing record's URL and exits 0, or says on standard error why nothing clears the
head and exits 1. A wrong argument, a gh call that fails (its own message passes through) and input
it cannot read exit 2. land.sh stops on either.

**`--follow-ups`, last in either form, adds the carried findings** (#521): after the URL, one line
per row of the `## Follow-ups` table in the comment or review holding the clearing record, each the
row's Finding cell. docs/agents/review.md -> "Landing or holding" has the review-queue workflow end
a record's findings with that table when the review carries a finding rather than holding on it,
and the merge body then owes an `Outstanding:` line, which land.sh checks with these lines. The
table is read as .claude/workflows/morning.js's followUpsOf reads it, so the two cannot name
different rows: the line `| Finding | Axis | Where | First carried by |`, the separator after it,
then every following line bracketed by `|`, split on each `|` not escaped as `\\|`, a row where it
has four cells and a Finding. A refusal prints nothing, with or without the flag. Without the flag
the URL stands alone, as it always did.

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
- A line posted by an account from outside the repository. The repository is public, so any
  signed-in GitHub user can comment on its pull requests, and a record comes only from the reviewing
  agent session (Q7, Q8). GitHub's `author_association` on each comment and review says which: its
  owner (`OWNER`), a member of the organisation that owns it (`MEMBER`) and a collaborator invited
  to it (`COLLABORATOR`) are read; every other value, `CONTRIBUTOR` and `NONE` among them, neither
  clears nor holds.

On this repository the only such account is its owner's, which every agent session and the human
share, so the reader cannot tell which of them wrote a record. It is a sequencing guard against
haste, not a check of identity (Q3).

The fields read are GitHub's REST shapes, and nothing else of them:
https://docs.github.com/en/rest/issues/comments#list-issue-comments and
https://docs.github.com/en/rest/pulls/reviews#list-reviews-for-a-pull-request

Test companion: tools/review_record_tests.py. Kept 3.9-compatible (docs/dependencies.md, D-3).
"""

from __future__ import annotations

import datetime
import json
import re
import subprocess
import sys

FORM = "Reviewed <40-hex head> against <40-hex base>: merge (or : hold)"
FULL_SHA = re.compile(r"[0-9a-f]{40}")
NUMBER = re.compile(r"[1-9][0-9]*")
USAGE = ("the two forms are `review_record.py --pr <number> [--head <40-hex head>]`, which looks the "
         "pull request up with gh, and `review_record.py <40-hex head>` with the two `gh api "
         "--paginate --slurp` listings on standard input, either with `--follow-ups` last")
FOLLOW_UPS = "--follow-ups"
# The header of the table review-queue.js's followUpLines writes under `## Follow-ups`, and the
# cell separator its cell() escapes: the same two strings morning.js's followUpsOf reads by.
FOLLOW_UP_HEAD = "| Finding | Axis | Where | First carried by |"
UNESCAPED_PIPE = re.compile(r"(?<!\\)\|")
RECORD = re.compile(r"Reviewed (?P<head>[0-9a-f]{40}) against (?P<base>[0-9a-f]{40}): "
                    r"(?P<verdict>merge|hold)")
# A line a reviewer probably meant as a record: the word, then something sha-shaped. Read only to
# say why it did not count, never to clear anything.
NEAR_MISS = re.compile(r"\breviewed\b.*\b[0-9a-fA-F]{7,}\b", re.IGNORECASE)
TIME_FORMAT = "%Y-%m-%dT%H:%M:%SZ"
# The author_association values of an account the repository has let in. Both REST listings carry
# the field (the two pages cited above), and GitHub's schema says what each value means, enum
# CommentAuthorAssociation in https://docs.github.com/public/fpt/schema.docs.graphql: OWNER "is the
# owner of the repository", MEMBER "is a member of the organization that owns the repository",
# COLLABORATOR "has been invited to collaborate on the repository". CONTRIBUTOR only "has previously
# committed to the repository", which anyone whose commit was once merged has.
REPOSITORY_ACCOUNTS = ("OWNER", "MEMBER", "COLLABORATOR")


class Unreadable(Exception):
    """Input the reader cannot judge. land.sh stops on it as on a refusal."""


class Record:
    def __init__(self, head: str, verdict: str, at: datetime.datetime, url: str,
                 follow_ups: list[str]):
        self.head = head
        self.verdict = verdict
        self.at = at
        self.url = url
        # The Finding cells of the `## Follow-ups` table in the comment or review holding the line.
        self.follow_ups = follow_ups


def follow_ups(body: str) -> list[str]:
    """The Finding cell of each row of the body's `## Follow-ups` table, as morning.js's followUpsOf
    reads it: the first line equal to the header once stripped, skipping the separator line, and
    then each line bracketed by pipes until the first that is not; a row counts when it has four
    cells and a Finding. `\\|` inside a cell is the pipe review-queue.js escaped."""
    lines = [line.strip() for line in body.splitlines()]
    if FOLLOW_UP_HEAD not in lines:
        return []
    found = []
    for line in lines[lines.index(FOLLOW_UP_HEAD) + 2:]:
        if not (line.startswith("|") and line.endswith("|")):
            break
        cells = [cell.strip().replace("\\|", "|") for cell in UNESCAPED_PIPE.split(line[1:-1])]
        if len(cells) == 4 and cells[0]:
            found.append(cells[0])
    return found


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


def read_entry(entry: dict, at_field: str, name: str, not_read: list[str]) -> list[Record]:
    body = entry.get("body") or ""
    if not isinstance(body, str):
        raise Unreadable(f"a body in {name} is not text")
    records = []
    for line in body.splitlines():
        line = line.strip()
        match = RECORD.fullmatch(line)
        if match is None:
            if NEAR_MISS.search(line):
                not_read.append(f"not in the record's form, so not read: "
                                f"{entry.get('html_url', name)}: {line}")
            continue
        association = entry.get("author_association")
        if not isinstance(association, str) or not association:
            raise Unreadable(f"a record in {name} has no author_association")
        if association not in REPOSITORY_ACCOUNTS:
            login = (entry.get("user") or {}).get("login", "an unknown account")
            not_read.append(f"posted by {login}, whose author_association is {association}, "
                            f"from outside the repository, so not read: "
                            f"{entry.get('html_url', name)}: {line}")
            continue
        url = entry.get("html_url")
        if not isinstance(url, str) or not url:
            raise Unreadable(f"a record in {name} has no html_url")
        records.append(Record(match["head"], match["verdict"], when(entry, at_field, name), url,
                              follow_ups(body)))
    return records


def records(listings: object) -> tuple[list[Record], list[str]]:
    if not isinstance(listings, dict):
        raise Unreadable("the input is not an object holding comments and reviews")
    for name in ("comments", "reviews"):
        if name not in listings:
            raise Unreadable(f"the input has no {name}")
    found: list[Record] = []
    not_read: list[str] = []
    for entry in entries(listings["comments"], "comments"):
        found += read_entry(entry, "created_at", "comments", not_read)
    for entry in entries(listings["reviews"], "reviews"):
        if entry.get("state") == "COMMENTED":
            found += read_entry(entry, "submitted_at", "reviews", not_read)
    return found, not_read


def judge(head: str, listings: object) -> tuple[Record | None, list[str]]:
    """The record that clears `head`, or None and the lines that say why not."""
    found, not_read = records(listings)
    naming = [record for record in found if record.head == head]
    if naming:
        latest = max(record.at for record in naming)
        decisive = [record for record in naming if record.at == latest]
        holds = [record for record in decisive if record.verdict == "hold"]
        if not holds:
            return decisive[-1], []
        reasons = [f"the latest review record of {head} says hold: {holds[0].url}"]
    else:
        reasons = [f"no review record names {head}, the head this pull request would merge."]
        others = sorted(found, key=lambda record: record.at)
        for record in others[-3:]:
            reasons.append(f"a record names {record.head} ({record.verdict}): {record.url}. A "
                           "review covers the head it read; commits pushed since need their own.")
    reasons += not_read
    reasons.append(f"the form, on a line of its own: {FORM}. docs/agents/review.md -> "
                   "Landing or holding.")
    return None, reasons


def say(text: str):
    sys.stderr.buffer.write(f"review_record: {text}\n".encode("utf-8", "backslashreplace"))


class WrongArguments(Exception):
    """Arguments in neither form. The refusal names both."""


def parse(arguments: list[str]) -> tuple[str | None, str | None, bool]:
    """(pull request number or None, head or None, whether the follow-ups are wanted) from the
    arguments of one of the two forms, each of which takes --follow-ups last and once."""
    with_follow_ups = bool(arguments) and arguments[-1] == FOLLOW_UPS
    if with_follow_ups:
        arguments = arguments[:-1]
    number, head = parse_form(arguments)
    return number, head, with_follow_ups


def parse_form(arguments: list[str]) -> tuple[str | None, str | None]:
    """(pull request number or None, head or None) from the arguments of one of the two forms."""
    if len(arguments) == 1 and not arguments[0].startswith("-"):
        if not FULL_SHA.fullmatch(arguments[0]):
            raise WrongArguments(f"the head must be a full commit sha, and {arguments[0]!r} is not "
                                 "a full commit sha")
        return None, arguments[0]
    if len(arguments) not in (2, 4) or arguments[0] != "--pr":
        raise WrongArguments(f"{arguments!r} is in neither form")
    if not NUMBER.fullmatch(arguments[1]):
        raise WrongArguments(f"--pr takes a pull request number, and {arguments[1]!r} is not one")
    if len(arguments) == 2:
        return arguments[1], None
    if arguments[2] != "--head":
        raise WrongArguments(f"{arguments!r} is in neither form")
    if not FULL_SHA.fullmatch(arguments[3]):
        raise WrongArguments(f"--head takes a full commit sha, and {arguments[3]!r} is not a full "
                             "commit sha")
    return arguments[1], arguments[3]


def gh(*arguments: str) -> str:
    """gh's standard output for one call. Its standard error passes through to the caller's."""
    command = "gh " + " ".join(arguments)
    try:
        completed = subprocess.run(["gh", *arguments], stdout=subprocess.PIPE)
    except OSError as problem:
        raise Unreadable(f"`{command}` could not start ({problem.strerror}); the --pr form needs "
                         "gh on PATH") from None
    if completed.returncode != 0:
        raise Unreadable(f"`{command}` failed with status {completed.returncode}")
    try:
        return completed.stdout.decode("utf-8")
    except UnicodeDecodeError:
        raise Unreadable(f"`{command}` printed something that is not UTF-8") from None


def look_up(number: str, head: str | None) -> tuple[str, object]:
    """The head to judge and the listings, as land.sh read them before #488."""
    if head is None:
        head = gh("pr", "view", number, "--json", "headRefOid", "--jq", ".headRefOid").strip()
        if not FULL_SHA.fullmatch(head):
            raise Unreadable(f"gh reported {head!r} as the head of #{number}, which is not a full "
                             "commit sha")
    # Both listings whole: --paginate follows every page and --slurp wraps the pages in one array,
    # so a record on page two is read, and gh fills {owner} and {repo} from the remote of the
    # repository the working directory is in (https://cli.github.com/manual/gh_api).
    listings = {}
    for name, endpoint in (("comments", f"repos/{{owner}}/{{repo}}/issues/{number}/comments"),
                           ("reviews", f"repos/{{owner}}/{{repo}}/pulls/{number}/reviews")):
        text = gh("api", endpoint, "--paginate", "--slurp")
        try:
            listings[name] = json.loads(text)
        except ValueError:
            raise Unreadable(f"the {name} gh listed are not JSON") from None
    return head, listings


def main(arguments: list[str]) -> int:
    try:
        number, head, with_follow_ups = parse(arguments)
    except WrongArguments as problem:
        say(f"{problem}; {USAGE}.")
        return 2
    try:
        if number is None:
            try:
                listings = json.loads(sys.stdin.buffer.read().decode("utf-8"))
            except (UnicodeDecodeError, ValueError):
                say("standard input is not JSON; the stdin form takes two `gh api` listings, and "
                    "`review_record.py --pr <number>` fetches them itself.")
                return 2
        else:
            head, listings = look_up(number, head)
        record, reasons = judge(head, listings)
    except Unreadable as problem:
        say(f"cannot read the records: {problem}.")
        return 2
    if record is None:
        for reason in reasons:
            say(reason)
        return 1
    print(record.url)
    if with_follow_ups:
        for finding in record.follow_ups:
            print(finding)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
