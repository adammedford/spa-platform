#!/usr/bin/env python3
"""Whether an issue has the shape of a ticket a lane can finish. Owner: integrator.

docs/agents/issue-tracker.md states five rules a ticket keeps, confirmed on #227. Until #247 nothing
held them where a ticket is written: A-05 (.github/path-ownership.py) catches a two-owner ticket at
the push, after a lane has built half of it, which is why seven of the 22 tickets of #226 ended
partial instead of never starting. This reads one issue and fails it, naming the rule, on:

- one-owner: its `**Touches:**` paths have more than one owner in OWNERSHIP.md (rules 1 and 5),
  read with A-05's own `rules` and `owner_of`;
- touches: a `**Touches:**` line with no path in it, which read as absent would leave only the
  Owner line judged (CODING_STANDARDS.md, "A check fails closed"). A path is read from a code span
  or bare, as a token with a `/` or a `.` in a name (`.gitignore`, `tools/check.sh`), so the prose
  the build tickets mix in ("and, if a ... is added,") is not taken for one;
- owner-line: its `**Owner:**` line names other than exactly the role those paths derive, by
  tools/overnight_collect.py's ROLES. A body with no Touches line derives no role, so its Owner line
  must name exactly one; the tickets filed before #227 have none, and they are judged by what they
  have rather than failed wholesale;
- sitting-or-landing: under `ready-for-agent`, an open `- [ ]` box using a word of SITTING_OR_LANDING
  (rules 2 and 3). A word list misfires; the escape is to reword the box, or to move it to a
  ready-for-human ticket blocked by the one that builds what it runs;
- blocker-edge: a "Blocked by #N", "after #N" or "once #N lands" with no native blocked-by edge to
  an open #N, so the scheduler cannot see the wait the prose states (ADR-0020);
- foreground: under `ready-for-agent`, a `**Session:** foreground` line, which no lane can honour;
- spec: under `ready-for-agent`, any sub-issue, since an issue with sub-issues is a spec and never a
  ticket.

    python3 -I tools/ticket_shape.py <number>     read the issue through gh
    python3 -I tools/ticket_shape.py -            read `gh issue view --json` output on stdin

The JSON is `gh issue view <n> --json number,body,labels,subIssuesSummary,blockedBy`, the fields
FIELDS names. Each failure is a line `ticket-shape: <rule>: <what>`, and the last line is the
verdict: `ticket-shape: pass: ...` with exit 0, or `ticket-shape: fail: ...` with exit 1 when a rule
failed and exit 2 when nothing was judged: the issue could not be read, or the check itself broke
(a script it reuses or OWNERSHIP.md missing, or any other error). Python's own exit for an uncaught
error is 1, the code of a failed rule, so main() lets no error escape, and
.github/workflows/ticket-shape.yml relabels only a ticket that was judged.
"""
from __future__ import annotations

import functools
import importlib.util
import json
import pathlib
import re
import subprocess
import sys
import typing

ROOT = pathlib.Path(__file__).resolve().parent.parent
FIELDS = "number,body,labels,subIssuesSummary,blockedBy"


def load(name: str, path: pathlib.Path):
    """A sibling script as a module. Run under -I, the script's own directory is not on sys.path,
    and .github/path-ownership.py's name is no module name."""
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# Loaded on first use, inside main()'s handler, so that a missing or broken sibling is a verdict of
# nothing judged (exit 2) and not a traceback on Python's exit 1, which reads as a failed rule.
@functools.cache
def ownership():
    return load("path_ownership", ROOT / ".github" / "path-ownership.py")


@functools.cache
def collect():
    return load("overnight_collect", ROOT / "tools" / "overnight_collect.py")

READY = "ready-for-agent"

# The words of a box that needs a sitting or a landing, as #247 lists them, each matched as a word
# and in any case. One differs from the list: #247 names "AWS" bare, and #198 says "looking the
# names up in AWS" of a companion that fails, a box a lane finishes against a double; #247 also
# asks that #198 pass. So "AWS" counts only as "against AWS", the reach a sitting has and a lane
# does not, as "against the applied" does for the applied stack.
SITTING_OR_LANDING = tuple((word, re.compile(pattern, flags)) for word, pattern, flags in (
    ("deployed", r"\bdeployed\b", re.IGNORECASE),
    ("the cell", r"\bthe cell\b", re.IGNORECASE),
    ("against the applied", r"\bagainst the applied\b", re.IGNORECASE),
    ("against AWS", r"\bagainst AWS\b", 0),
    ("sitting", r"\bsittings?\b", re.IGNORECASE),
    ("by hand", r"\bby hand\b", re.IGNORECASE),
    ("browser", r"\bbrowsers?\b", re.IGNORECASE),
    ("pushed", r"\bpushed\b", re.IGNORECASE),
    ("landed", r"\blanded\b", re.IGNORECASE),
    ("landing", r"\blanding\b", re.IGNORECASE),
    ("merge body", r"\bmerge bod(?:y|ies)\b", re.IGNORECASE),
    ("pull request", r"\bpull requests?\b", re.IGNORECASE),
    ("on main", r"\bon `?main\b", re.IGNORECASE),
    ("posted on #", r"\bposted on #", re.IGNORECASE),
))
OPEN_BOX = re.compile(r"(?m)^[ \t]*- \[ \][ \t]*(?P<text>[^\n]*)$")

# The three ways a body states a wait, each giving the issue numbers it waits for. "Blocked by"
# takes a list, as the tracker guide's fallback line `Blocked by: #<n>, #<n>` writes one.
BLOCKER_PHRASES = (
    re.compile(r"\bblocked by:?[ \t]*(?P<numbers>#\d+(?:(?:[ \t]*,[ \t]*|[ \t]+and[ \t]+)#\d+)*)",
               re.IGNORECASE),
    re.compile(r"\bafter (?P<numbers>#\d+)", re.IGNORECASE),
    re.compile(r"\bonce (?P<numbers>#\d+) lands\b", re.IGNORECASE),
)
NUMBER = re.compile(r"#(\d+)")
FOREGROUND = re.compile(r"(?m)^\*\*Session:\*\*[ \t]*foreground\b", re.IGNORECASE)

TOUCHES = re.compile(r"(?m)^\*\*Touches:\*\*[ \t]*(?P<value>[^\n]*)$")
# A code span, or the text between two.
SPAN_OR_TEXT = re.compile(r"`([^`\n]+)`|([^`]+)")
# A bare token that names a path: a directory separator, or a dot inside or leading a name. "e.g."
# and "i.e." are words, not paths; a URL's colon keeps it out.
BARE_PATH = re.compile(r"^(?=[^/]*/|\.?[\w-]*[\w-]\.[\w-]|\.[\w-])[\w./*-]+$")
NOT_PATHS = {"e.g", "i.e"}


class Unreadable(Exception):
    """The issue could not be read, so no rule was judged."""


class Issue(typing.NamedTuple):
    number: int
    body: str
    labels: tuple[str, ...]
    sub_issues: int
    blockers: tuple[int, ...]   # the open ones


class Failure(typing.NamedTuple):
    rule: str
    detail: str


def issue_of(raw: object) -> Issue:
    """The issue from gh's JSON. A field in any other shape is Unreadable, never read as empty: a
    missing blocker list read as none would fail a ticket whose edge exists."""
    try:
        return Issue(
            number=int(raw["number"]),
            body=raw["body"] or "",
            labels=tuple(label["name"] for label in raw["labels"]),
            sub_issues=int(raw["subIssuesSummary"]["total"]),
            blockers=tuple(int(node["number"]) for node in raw["blockedBy"]["nodes"]
                           if node["state"] == "OPEN"),
        )
    except (KeyError, TypeError, ValueError) as error:
        raise Unreadable(f"the issue's JSON has no readable {error}") from error


def touches(body: str) -> list[str] | None:
    """The paths of the Touches line, in its order: its code spans and its bare tokens that look
    like paths. None when there is no line, and [] when there is one with no path in it."""
    line = TOUCHES.search(body)
    if not line:
        return None
    paths = []
    for span, text in SPAN_OR_TEXT.findall(line.group("value")):
        if span:
            paths.append(span)
            continue
        for token in re.split(r"[\s,;()]+", text):
            token = token.rstrip(".:")
            if token not in NOT_PATHS and BARE_PATH.match(token):
                paths.append(token)
    return paths


def owner_of(path: str, parsed: list[tuple[str, str]]) -> str | None:
    """A Touches path's owner. A directory may be written without its slash (`tests/e2e`), which
    OWNERSHIP.md's `/tests/e2e/` would not match and the catch-all would, so a path naming an owned
    directory is read as that directory."""
    bare = path.strip().lstrip("/").rstrip("/")
    if any(pattern.lstrip("/") == bare + "/" for pattern, _ in parsed):
        return ownership().owner_of(bare + "/", parsed)
    return ownership().owner_of(bare, parsed)


def judge(issue: Issue, parsed: list[tuple[str, str]] | None = None) -> list[Failure]:
    """Every rule the issue breaks, in the order the docstring lists them."""
    parsed = ownership().rules() if parsed is None else parsed
    failures: list[Failure] = []

    paths = touches(issue.body)
    if paths == []:
        failures.append(Failure("touches", "the Touches line names no path, so no owner is "
                                "derived from it"))
    by_owner: dict[str, list[str]] = {}
    for path in paths or []:
        by_owner.setdefault(owner_of(path, parsed) or "no owner", []).append(path)
    if len(by_owner) > 1:
        failures.append(Failure("one-owner", "the Touches paths have " + "; ".join(
            f"{owner}: {', '.join(paths)}" for owner, paths in sorted(by_owner.items()))))
    else:
        named = roles_named(issue.body)
        derived = next(iter(by_owner), None)
        if derived is not None and named != [derived]:
            failures.append(Failure("owner-line", f"the Owner line names {said(named)}, and the "
                                    f"Touches paths are {derived}'s"))
        elif derived is None and len(named) != 1:
            failures.append(Failure("owner-line", f"the Owner line names {said(named)}, where a "
                                    "ticket has one owner"))

    if READY in issue.labels:
        for box in OPEN_BOX.finditer(issue.body):
            text = box.group("text")
            words = [word for word, pattern in SITTING_OR_LANDING if pattern.search(text)]
            if words:
                failures.append(Failure("sitting-or-landing", f"an open box says {said(words)}: "
                                        f"{text[:120]}"))

    stated = sorted({int(number) for phrase in BLOCKER_PHRASES
                     for found in phrase.finditer(issue.body)
                     for number in NUMBER.findall(found.group("numbers"))})
    unedged = [number for number in stated if number not in issue.blockers]
    if unedged:
        failures.append(Failure("blocker-edge", "the body waits for " + ", ".join(
            f"#{number}" for number in unedged) + " with no blocked-by edge to it open"))

    if READY in issue.labels and FOREGROUND.search(issue.body):
        failures.append(Failure("foreground", "a ready-for-agent ticket says **Session:** "
                                "foreground"))

    if READY in issue.labels and issue.sub_issues > 0:
        failures.append(Failure("spec", f"a ready-for-agent issue has {issue.sub_issues} "
                                "sub-issue(s), which makes it a spec"))

    return failures


def roles_named(body: str) -> list[str]:
    """The roles the Owner line names, in the line's order, read by the collect step's ROLES so
    that both read one line alike."""
    line = collect().OWNER.search(body)
    value = line.group("value").strip().lower() if line else ""
    places = []
    for role, pattern in collect().ROLES:
        found = re.search(pattern, value)
        if found:
            places.append((found.start(), role))
    return [role for _, role in sorted(places)]


def said(roles: list[str]) -> str:
    if not roles:
        return "no role"
    return roles[0] if len(roles) == 1 else f"{', '.join(roles[:-1])} and {roles[-1]}"


def read(argument: str) -> Issue:
    """The issue an argument names: `-` for gh's JSON on stdin, or a number gh is asked for."""
    if argument == "-":
        text = sys.stdin.read()
    elif argument.isdigit():
        try:
            done = subprocess.run(["gh", "issue", "view", argument, "--json", FIELDS],
                                  capture_output=True, text=True, check=False)
        except OSError as error:
            raise Unreadable(f"gh could not run: {error}") from error
        if done.returncode != 0:
            raise Unreadable(f"gh issue view {argument} exited {done.returncode}: "
                             f"{done.stderr.strip()}")
        text = done.stdout
    else:
        raise Unreadable(f"{argument!r} is neither an issue number nor -")
    try:
        return issue_of(json.loads(text))
    except json.JSONDecodeError as error:
        raise Unreadable(f"the issue's JSON does not parse: {error}") from error


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print(__doc__.split("\n\n")[0])
        print("ticket-shape: fail: name one issue number, or - for JSON on stdin")
        return 2
    try:
        issue = read(argv[0])
        failures = judge(issue)
    except (Exception, SystemExit) as error:
        # Unreadable is a read that failed; SystemExit is path-ownership.py's refusal of an
        # OWNERSHIP.md with no rules; anything else is the check broken. None of them judged the
        # issue, and each ends here on exit 2 with its verdict line (CODING_STANDARDS.md, "A check
        # fails closed").
        print(f"ticket-shape: fail: nothing judged: {type(error).__name__}: {error}")
        return 2
    for failure in failures:
        print(f"ticket-shape: {failure.rule}: {failure.detail}")
    if failures:
        broken = list(dict.fromkeys(failure.rule for failure in failures))
        print(f"ticket-shape: fail: #{issue.number} breaks {len(broken)} rule(s) of the tracker "
              f"guide's ticket shape: {', '.join(broken)}")
        return 1
    print(f"ticket-shape: pass: #{issue.number} has the shape of a ticket a lane can finish")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
