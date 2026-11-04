#!/usr/bin/env python3
"""Says which decision comment a merge body owes a citation to, for one issue a pull request closes.

Owner: integrator. Built for #530, which tools/land.sh runs it for. Two forms, which give the same
answer for the same issue:

    python3 -I tools/decision_citation.py --issue <number> [--guide <path>]
    python3 -I tools/decision_citation.py [--guide <path>]  < {"issue": ..., "comments": ...}

**The rule.** docs/agents/tracker-policy.md -> "Landing": when a change request's decision says
`ADR: no`, the merge body states the why and cites the decision comment (#413). The why is a
judgement and stays with the reviewer; the citation is mechanical, and this is the reader of it.
#492's merge (92675bb, 2026-10-29) closed #495, #497 and #498, each decided `ADR: no`, and its body
cited none of their decisions; a merge body cannot be edited afterwards.

An issue owes a citation when it carries the `change-request` label and its latest decision
comment accepts with `ADR: no`. A decision comment is one in the form tracker-policy.md ->
"Deciding" fixes, posted by the human's account: its first line is exactly `Decision: accept`,
`Decision: accept with changes`, `Decision: decline` or `Decision: accept as drafted <URL>`, and
its second line is `ADR: yes` or `ADR: no`. A comment that departs from the form is not a decision,
whoever wrote it, and one in the form from any other account is not one either: the repository is
public, anyone signed in can comment, and CODING_STANDARDS.md -> "An authorisation is read from its
author" counts a comment only when the account the rule names wrote it. Trailing whitespace and
the carriage return a web-edited comment ends each line with are ignored, as Markdown does.

**The latest decision decides**, by `created_at`, as the latest review record does for a head
(tools/review_record.py): an accept followed by a decline leaves the request declined and owes
nothing, and `ADR: no` followed by `ADR: yes` owes nothing, where #530's words ("the latest
accepting decision ... whose second line is `ADR: no`") would still demand the earlier accept's
URL. Its third criterion, that a declined request asks for nothing, is what the later decision
settles, so the latest one is read (decided by the implementer on #530). An issue without the
label, open or closed, owes nothing and its comments are not read.

**The account** is the login docs/agents/tracker-policy.md names on its "The human's account is
`@<login>`." line, the line .github/steering.py and tools/overnight_collect.py read, from the guide
in the checkout this file sits in, so the reader beside land.sh judges by the policy it was
written against; --guide names another file, for the test companion. A guide naming no account
makes the input unreadable rather than reading anyone's decision: nothing can be judged.

**Exit status.** 0 and the decision comment's URL on standard output: the merge body owes it. 1 and
a line on standard error saying why: nothing is owed. 2: wrong arguments, a gh call that failed
(its own message passes through), or input not in GitHub's shape. land.sh stops on 2, and on a 0
that printed nothing.

**The `--issue` form looks the issue up itself**, with gh, from the repository the working
directory is in: `gh api repos/{owner}/{repo}/issues/<number>` for its labels, which answers for a
pull request's number too, and, only for a change request, `gh api
repos/{owner}/{repo}/issues/<number>/comments --paginate --slurp` for every page of its comments.
**The stdin form** takes `{"issue": <that object>, "comments": <those pages>}` from a caller
already holding them. The fields read are GitHub's REST shapes and nothing else of them:
https://docs.github.com/en/rest/issues/issues#get-an-issue and
https://docs.github.com/en/rest/issues/comments#list-issue-comments

Test companion: tools/decision_citation_tests.py. Kept 3.9-compatible (docs/dependencies.md, D-3).
"""

from __future__ import annotations

import datetime
import json
import pathlib
import re
import subprocess
import sys

LABEL = "change-request"
GUIDE = "docs/agents/tracker-policy.md"
DEFAULT_GUIDE = pathlib.Path(__file__).resolve().parent.parent / GUIDE
# The account line, in the form .github/steering.py reads it; tools/decision_citation_tests.py
# holds the two patterns equal.
ACCOUNT_LINE = re.compile(
    r"(?m)^- \*\*The human's account is `@(?P<login>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))`\.\*\*"
)
# The two lines of a decision comment (docs/agents/tracker-policy.md -> "Deciding"), each matched
# whole once its trailing whitespace is dropped.
FIRST_LINE = re.compile(r"Decision: (accept|accept with changes|decline|accept as drafted \S+)")
SECOND_LINE = re.compile(r"ADR: (yes|no)")
TIME_FORMAT = "%Y-%m-%dT%H:%M:%SZ"
NUMBER = re.compile(r"[1-9][0-9]*")
USAGE = ("the two forms are `decision_citation.py --issue <number> [--guide <path>]`, which looks "
         "the issue up with gh, and `decision_citation.py [--guide <path>]` with "
         '{"issue": <the issue>, "comments": <its comment pages>} on standard input')


class Unreadable(Exception):
    """Input the reader cannot judge. land.sh stops on it as on a refusal."""


class WrongArguments(Exception):
    pass


class Decision:
    def __init__(self, accepting: bool, adr: str, at: datetime.datetime, url: str):
        self.accepting = accepting
        self.adr = adr
        self.at = at
        self.url = url


def say(text: str):
    print(text, file=sys.stderr)


def parse(arguments: list[str]) -> tuple[str | None, pathlib.Path]:
    """The issue number of the --issue form, or None for the stdin form, and the guide to read."""
    number = None
    guide = DEFAULT_GUIDE
    rest = list(arguments)
    while rest:
        option = rest.pop(0)
        if option == "--issue" and rest and NUMBER.fullmatch(rest[0]):
            number = rest.pop(0)
        elif option == "--guide" and rest:
            guide = pathlib.Path(rest.pop(0))
        else:
            raise WrongArguments(f"cannot read the argument {option!r}")
    return number, guide


def named_account(guide: pathlib.Path) -> str:
    """The login the guide names, or Unreadable when it names none or is not there."""
    try:
        text = guide.read_text()
    except OSError as problem:
        raise Unreadable(f"{guide} cannot be read ({problem.strerror})") from None
    found = ACCOUNT_LINE.search(text)
    if not found:
        raise Unreadable(f"{guide} names no account on a \"The human's account is `@<login>`.\" "
                         "line, so no decision can be read")
    return found.group("login")


def labels_of(issue: object, number: str) -> list[str]:
    if not isinstance(issue, dict) or not isinstance(issue.get("labels"), list) \
            or not all(isinstance(label, dict) and isinstance(label.get("name"), str)
                       for label in issue["labels"]):
        raise Unreadable(f"issue #{number} has no labels in GitHub's shape, a list of objects "
                         "each with a name")
    return [label["name"] for label in issue["labels"]]


def entries(listing: object) -> list[dict]:
    """The objects of a `gh api --paginate --slurp` listing: a list of pages, each a list."""
    if not isinstance(listing, list) or not all(isinstance(page, list) for page in listing):
        raise Unreadable("the comments are not a list of pages, which `gh api --paginate --slurp` "
                         "gives")
    found = [entry for page in listing for entry in page]
    if not all(isinstance(entry, dict) for entry in found):
        raise Unreadable("the comments hold something other than objects")
    return found


def lines_of(body: object) -> list[str]:
    """The first two lines of a body, each without its trailing whitespace; fewer when it has
    fewer. A body GitHub gives as null is empty."""
    if not isinstance(body, str):
        return []
    return [line.rstrip() for line in body.split("\n", 2)[:2]]


def read_decision(entry: dict, login: str, passed_over: list[str]) -> Decision | None:
    """The decision a comment carries, None for a comment that is not one. A comment in the form
    from another account is named in passed_over, so the refusal can say it was seen and not read."""
    lines = lines_of(entry.get("body"))
    first = FIRST_LINE.fullmatch(lines[0]) if lines else None
    second = SECOND_LINE.fullmatch(lines[1]) if len(lines) > 1 else None
    if not first or not second:
        return None
    user = entry.get("user")
    author = user.get("login") if isinstance(user, dict) else None
    if author != login:
        passed_over.append(f"a comment in the decision form by {author or 'a deleted account'}, "
                           f"not by {login}, is not read as a decision")
        return None
    url = entry.get("html_url")
    if not isinstance(url, str) or not url:
        raise Unreadable("a decision comment has no html_url")
    at = entry.get("created_at")
    try:
        when = datetime.datetime.strptime(at, TIME_FORMAT)
    except (TypeError, ValueError):
        raise Unreadable(f"a decision comment, {url}, has no created_at in GitHub's form") from None
    return Decision(first.group(1) != "decline", second.group(1), when, url)


def judge(number: str, issue: object, comments: object, login: str) -> tuple[str | None, list[str]]:
    """The URL the merge body owes, or None with the reasons nothing is owed."""
    if LABEL not in labels_of(issue, number):
        return None, [f"#{number} is not a change request (no {LABEL} label), so no decision is "
                      "owed a citation"]
    passed_over: list[str] = []
    decisions = [found for found in (read_decision(entry, login, passed_over)
                                     for entry in entries(comments)) if found]
    if not decisions:
        return None, passed_over + [f"#{number} carries no decision comment by {login}, so no "
                                    "decision is owed a citation"]
    # Stable, so of two decisions at the same instant the later-listed one decides.
    latest = sorted(decisions, key=lambda decision: decision.at)[-1]
    if not latest.accepting:
        return None, [f"#{number} is declined by its latest decision, {latest.url}, so no "
                      "decision is owed a citation"]
    if latest.adr == "yes":
        return None, [f"#{number}'s latest decision, {latest.url}, says ADR: yes, so the ADR "
                      "carries the why and no decision is owed a citation"]
    return latest.url, []


def gh(*arguments: str) -> str:
    """gh's standard output for one call. Its standard error passes through to the caller's."""
    command = "gh " + " ".join(arguments)
    try:
        completed = subprocess.run(["gh", *arguments], stdout=subprocess.PIPE)
    except OSError as problem:
        raise Unreadable(f"`{command}` could not start ({problem.strerror}); the --issue form "
                         "needs gh on PATH") from None
    if completed.returncode != 0:
        raise Unreadable(f"`{command}` failed with status {completed.returncode}")
    try:
        return completed.stdout.decode("utf-8")
    except UnicodeDecodeError:
        raise Unreadable(f"`{command}` printed something that is not UTF-8") from None


def fetched(endpoint: str, *options: str) -> object:
    text = gh("api", endpoint, *options)
    try:
        return json.loads(text)
    except ValueError:
        raise Unreadable(f"what gh listed for {endpoint} is not JSON") from None


def look_up(number: str) -> tuple[object, object]:
    """The issue, and its comment pages only when it is a change request: an issue without the
    label owes nothing whatever its comments say, so they are not fetched. gh fills {owner} and
    {repo} from the remote of the repository the working directory is in
    (https://cli.github.com/manual/gh_api)."""
    issue = fetched(f"repos/{{owner}}/{{repo}}/issues/{number}")
    if LABEL not in labels_of(issue, number):
        return issue, [[]]
    comments = fetched(f"repos/{{owner}}/{{repo}}/issues/{number}/comments", "--paginate", "--slurp")
    return issue, comments


def main(arguments: list[str]) -> int:
    try:
        number, guide = parse(arguments)
    except WrongArguments as problem:
        say(f"{problem}; {USAGE}.")
        return 2
    try:
        login = named_account(guide)
        if number is None:
            try:
                payload = json.loads(sys.stdin.buffer.read().decode("utf-8"))
            except (UnicodeDecodeError, ValueError):
                say(f"standard input is not JSON; {USAGE}.")
                return 2
            if not isinstance(payload, dict) or "issue" not in payload or "comments" not in payload:
                say(f"standard input lacks the issue or the comments; {USAGE}.")
                return 2
            issue, comments = payload["issue"], payload["comments"]
            number = str(issue.get("number", "?")) if isinstance(issue, dict) else "?"
        else:
            issue, comments = look_up(number)
        url, reasons = judge(number, issue, comments, login)
    except Unreadable as problem:
        say(f"cannot read the decision: {problem}.")
        return 2
    if url is None:
        for reason in reasons:
            say(reason)
        return 1
    print(url)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
