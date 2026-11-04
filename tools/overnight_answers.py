#!/usr/bin/env python3
"""Lists the questions on the open `Overnight <date>` issues that wait on the pull request being
landed, and says of each whether a Confirm comment covers it.

Owner: integrator. Built for #531, which tools/land.sh runs it for. Two forms, which print the same
lines for the same input:

    python3 -I tools/overnight_answers.py --number <n> [--number <n>]... --lookup [--guide <path>]
    python3 -I tools/overnight_answers.py --number <n> [--number <n>]... [--guide <path>]  < {...}

**Why.** An overnight grill often answers a question in terms of another pull request: "closes
when #492 merges", "state this in #492's merge body". Those answers sit on the run's `Overnight
<date>` issue, provisional until a Confirm, and the session running tools/land.sh on that other
pull request has no reason to read them. On 2026-10-29 three such obligations on #528 (its Q1, Q5
and Q11) went stale at #492's merge before anyone confirmed them. So land.sh runs this before it
merges, with the pull request's number and each number its description closes, and prints what it
finds. **It only reports** (#531): nothing it finds, and nothing it fails to read, stops the
merge, which is a deliberate departure from CODING_STANDARDS.md -> "A check fails closed" for a
reader whose answer is advice to the human, not a gate.

**What is read, in the morning's shapes.** An Overnight issue is an open issue whose title is
exactly `Overnight <yyyy-mm-dd>`. A questions comment on it is one whose first line is
`## Questions: #<ticket>, <task id>, <title>`, with a `(part k of m)` suffix when the night split
it; the parts are joined in order before reading so a question cut at a boundary is read whole. A
question opens with `**Q<n>.**`, `**Q<n>**` or `Q<n>.` and runs to the next mark or the comment's
end, its night-wide number unique across the issue (ADR-0021 decision 2). A question names a
number when `#<n>` appears anywhere in it, matched whole, so #35 is not found in #352 nor in a
decision URL's `#issuecomment-`. The heading's own ticket number selects nothing by itself
(#531): a question is listed when its own text, its `(#<ticket> Q<k>)` source tag included, names
a given number. A question a ticket's grill numbered opens with that tag, so a ticket the landing
closes has those questions listed, which is what the landing wants; a question under a heading
naming #<n> whose text and tag name no given number is not. That includes the questions the night
posts untagged, with the night-wide number alone: a status note's question and a partial's choice
question (.claude/workflows/overnight.js step 1b, #632) are listed only when their own text names
a given number.

A Confirm comment is one by the human's account whose body begins `Confirm`, read as
.claude/workflows/morning.js reads it ("the questions, read here", readConfirmations): its first
sentence, up to the first full stop followed by whitespace or the end of the line, names the
questions it covers, as ranges (`Q1 to Q57`, `Q1-Q3`, `Q1–Q3`) and single numbers, and each of its
`Q<n>: <answer>` lines covers Q<n> too. A question any Confirm comment on the issue names or
overrides is covered; the rest are provisional. The account is the login
docs/agents/tracker-policy.md names on its "The human's account is `@<login>`." line, read from
the checkout this file sits in through tools/decision_citation.py's reader of that line, so the
two readers beside land.sh judge by one guide; --guide names another file, for the test companion.

**Output.** One line per matching question on standard output, in the order the issues and their
questions are listed:

    #<issue> Overnight <date> Q<n> names #<number>, a Confirm covers it: <the question's first line>
    #<issue> Overnight <date> Q<n> names #<number>, no Confirm covers it: <the question's first line>

The first line is the text after the mark, up to the question's first line break. Exit 0 with
those lines, or with none when no question matches. Exit 2, with the reason on standard error,
when the input cannot be read: wrong arguments, a gh call that failed (its own message passes
through), or input not in GitHub's shape. land.sh prints its lines prefixed `land: `, and on a 2
prints one line saying the search failed and merges anyway.

**The --lookup form asks gh itself**, from the repository the working directory is in: `gh api
repos/{owner}/{repo}/issues?state=open&per_page=100 --paginate --slurp` for every open issue,
which lists pull requests too (each with a `pull_request` key, which drops it), filtered by title
here rather than through `gh issue list --search`, whose text search returns every issue whose
title contains the word; then, for each Overnight issue, `gh api
repos/{owner}/{repo}/issues/<n>/comments --paginate --slurp`. **The stdin form** takes
`{"issues": <those pages>, "comments": {"<n>": <that issue's pages>}}` from a caller already
holding them. The fields read are GitHub's REST shapes and nothing else of them:
https://docs.github.com/en/rest/issues/issues#list-repository-issues and
https://docs.github.com/en/rest/issues/comments#list-issue-comments

Test companion: tools/overnight_answers_tests.py. Kept 3.9-compatible (docs/dependencies.md, D-3).
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import re
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
DEFAULT_GUIDE = HERE.parent / "docs" / "agents" / "tracker-policy.md"
OVERNIGHT_TITLE = re.compile(r"Overnight [0-9]{4}-[0-9]{2}-[0-9]{2}")
# The questions comment's heading, its part suffix and the marks a question opens with, as
# .claude/workflows/morning.js holds them (HEADING, PART, Q_MARK).
HEADING = re.compile(r"^## Questions: #(\d+), ([^,]+), (.*)$")
PART = re.compile(r"\s*\(part (\d+) of (\d+)\)\s*$")
Q_MARK = re.compile(r"^[ \t]*(?:\*\*Q(\d+)\.?\*\*|Q(\d+)\.(?=[ \t]))[ \t]*", re.MULTILINE)
# A Confirm comment's ranges (morning.js's RANGE) and override lines.
RANGE = re.compile(r"Q(\d+)\s*(?:to|through|–|—|-)\s*Q?(\d+)")
SINGLE = re.compile(r"Q(\d+)")
OVERRIDE = re.compile(r"^Q(\d+):[ \t]*(.+)$")
NUMBER = re.compile(r"[1-9][0-9]*")
USAGE = ("the two forms are `overnight_answers.py --number <n>... --lookup [--guide <path>]`, which "
         "lists the open issues and their comments with gh, and `overnight_answers.py --number "
         '<n>... [--guide <path>]` with {"issues": <pages>, "comments": {"<n>": <pages>}} on '
         "standard input")


class Unreadable(Exception):
    """Input the reader cannot judge. land.sh reports it and merges anyway."""


class WrongArguments(Exception):
    pass


def say(text: str):
    print(text, file=sys.stderr)


def parse(arguments: list[str]) -> tuple[list[str], bool, pathlib.Path]:
    numbers: list[str] = []
    lookup = False
    guide = DEFAULT_GUIDE
    rest = list(arguments)
    while rest:
        option = rest.pop(0)
        if option == "--number" and rest and NUMBER.fullmatch(rest[0]):
            numbers.append(rest.pop(0))
        elif option == "--lookup":
            lookup = True
        elif option == "--guide" and rest:
            guide = pathlib.Path(rest.pop(0))
        else:
            raise WrongArguments(f"cannot read the argument {option!r}")
    if not numbers:
        raise WrongArguments("no --number was given")
    return numbers, lookup, guide


def named_account(guide: pathlib.Path) -> str:
    """The login the guide names, read by tools/decision_citation.py's reader of the line, so the
    two readers beside land.sh cannot disagree on who may confirm. Loaded by path: under -I the
    script's directory is not on sys.path."""
    spec = importlib.util.spec_from_file_location("decision_citation", HERE / "decision_citation.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    try:
        return module.named_account(guide)
    except module.Unreadable as problem:
        raise Unreadable(str(problem)) from None


def entries(listing: object, what: str) -> list[dict]:
    """The objects of a `gh api --paginate --slurp` listing: a list of pages, each a list."""
    if not isinstance(listing, list) or not all(isinstance(page, list) for page in listing):
        raise Unreadable(f"{what} are not a list of pages, which `gh api --paginate --slurp` gives")
    found = [entry for page in listing for entry in page]
    if not all(isinstance(entry, dict) for entry in found):
        raise Unreadable(f"{what} hold something other than objects")
    return found


def overnight_issues(listing: object) -> list[tuple[int, str]]:
    """(number, title) of each open issue titled `Overnight <date>`, pull requests left out."""
    found = []
    for entry in entries(listing, "the open issues"):
        if "pull_request" in entry:
            continue
        number, title = entry.get("number"), entry.get("title")
        if not isinstance(number, int) or not isinstance(title, str):
            raise Unreadable("an open issue has no number or no title")
        if OVERNIGHT_TITLE.fullmatch(title.strip()):
            found.append((number, title.strip()))
    return found


def body_of(entry: dict) -> str:
    body = entry.get("body")
    return body if isinstance(body, str) else ""


def author_of(entry: dict) -> str | None:
    user = entry.get("user")
    return user.get("login") if isinstance(user, dict) else None


class Question:
    def __init__(self, number: int, text: str):
        self.number = number
        self.text = text
        self.first_line = text.split("\n", 1)[0].strip()


def read_questions(comments: list[dict]) -> list[Question]:
    """Every question the issue's questions comments carry, parts joined, in listing order."""
    groups: dict[str, dict] = {}
    for entry in comments:
        body = body_of(entry)
        first, _, rest = body.partition("\n")
        heading = HEADING.match(first.strip())
        if not heading:
            continue
        part = PART.search(heading.group(3))
        title = PART.sub("", heading.group(3))
        key = f"{heading.group(1)}|{heading.group(2)}|{title}"
        group = groups.setdefault(key, {"parts": []})
        group["parts"].append((int(part.group(1)) if part else 1, rest))
    found = []
    for group in groups.values():
        text = "\n".join(part for _, part in sorted(group["parts"], key=lambda item: item[0]))
        marks = list(Q_MARK.finditer(text))
        for index, mark in enumerate(marks):
            end = marks[index + 1].start() if index + 1 < len(marks) else len(text)
            found.append(Question(int(mark.group(1) or mark.group(2)), text[mark.end():end]))
    return found


def covered_questions(comments: list[dict], login: str) -> set[int]:
    """The night-wide numbers some Confirm comment by the account names in its first sentence or
    overrides with a `Q<n>:` line."""
    covered: set[int] = set()
    for entry in comments:
        body = body_of(entry).lstrip()
        if author_of(entry) != login or not re.match(r"Confirm\b", body):
            continue
        first_line = body.split("\n", 1)[0]
        sentence = re.split(r"\.(?:\s|$)", first_line, maxsplit=1)[0]
        for found in RANGE.finditer(sentence):
            covered.update(range(int(found.group(1)), int(found.group(2)) + 1))
        for found in SINGLE.finditer(RANGE.sub(" ", sentence)):
            covered.add(int(found.group(1)))
        for line in body.split("\n"):
            override = OVERRIDE.match(line.strip())
            if override:
                covered.add(int(override.group(1)))
    return covered


def names(text: str, number: str) -> bool:
    """`#<number>` in the text, matched whole: no digit after it, and no word character or `&`
    before the `#`, so an HTML entity's `&#` is not a number."""
    return re.search(rf"(?<![A-Za-z0-9_&])#{number}(?![0-9])", text) is not None


def report(numbers: list[str], issues: list[tuple[int, str]], comments_of, login: str) -> list[str]:
    lines = []
    for issue, title in issues:
        comments = entries(comments_of(issue), f"the comments of #{issue}")
        covered = covered_questions(comments, login)
        for question in read_questions(comments):
            named = [number for number in numbers if names(question.text, number)]
            if not named:
                continue
            verdict = "a Confirm covers it" if question.number in covered else "no Confirm covers it"
            lines.append(f"#{issue} {title} Q{question.number} names #{named[0]}, {verdict}: "
                         f"{question.first_line}")
    return lines


def gh(*arguments: str) -> str:
    command = "gh " + " ".join(arguments)
    try:
        completed = subprocess.run(["gh", *arguments], stdout=subprocess.PIPE)
    except OSError as problem:
        raise Unreadable(f"`{command}` could not start ({problem.strerror}); the --lookup form "
                         "needs gh on PATH") from None
    if completed.returncode != 0:
        raise Unreadable(f"`{command}` failed with status {completed.returncode}")
    try:
        return completed.stdout.decode("utf-8")
    except UnicodeDecodeError:
        raise Unreadable(f"`{command}` printed something that is not UTF-8") from None


def fetched(endpoint: str) -> object:
    text = gh("api", endpoint, "--paginate", "--slurp")
    try:
        return json.loads(text)
    except ValueError:
        raise Unreadable(f"what gh listed for {endpoint} is not JSON") from None


def main(arguments: list[str]) -> int:
    try:
        numbers, lookup, guide = parse(arguments)
    except WrongArguments as problem:
        say(f"{problem}; {USAGE}.")
        return 2
    try:
        login = named_account(guide)
        if lookup:
            issues = overnight_issues(fetched("repos/{owner}/{repo}/issues?state=open&per_page=100"))
            def comments_of(issue: int) -> object:
                return fetched(f"repos/{{owner}}/{{repo}}/issues/{issue}/comments")
        else:
            try:
                payload = json.loads(sys.stdin.buffer.read().decode("utf-8"))
            except (UnicodeDecodeError, ValueError):
                say(f"standard input is not JSON; {USAGE}.")
                return 2
            if not isinstance(payload, dict) or not isinstance(payload.get("issues"), list) \
                    or not isinstance(payload.get("comments"), dict):
                say(f"standard input lacks the issues or the comments; {USAGE}.")
                return 2
            issues = overnight_issues(payload["issues"])
            def comments_of(issue: int) -> object:
                return payload["comments"].get(str(issue), [[]])
        lines = report(numbers, issues, comments_of, login)
    except Unreadable as problem:
        say(f"cannot read the Overnight issues' questions: {problem}.")
        return 2
    for line in lines:
        print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
