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
  or bare, as a token with a `/` or a `.` in a name (`.gitignore`, `tools/check.sh`), a token that
  is or begins a path OWNERSHIP.md names (`infra`, `fixtures`, `tests`), or the name of an entry at
  the repository's root (`docs`), so the prose the build tickets mix in ("and, if a ... is added,")
  is not taken for one. A token left that is neither a path nor a lower-case word (`Gateway`,
  `#213`) fails too, since the line cannot then be fully judged. A line whose value starts with the
  word `none` says the ticket writes nothing, as a run ticket does (#87, #255): it passes, and the
  owner is then the Owner line's one role, as for a body with no Touches line (#284);
- owner-line: its `**Owner:**` line names other than exactly the role those paths derive, by
  tools/overnight_collect.py's ROLES. A body with no Touches line derives no role, so its Owner line
  must name exactly one; the tickets filed before #227 have none, and they are judged by what they
  have rather than failed wholesale;
- sitting-or-landing: under `ready-for-agent`, an open `- [ ]` box using a word of SITTING_OR_LANDING
  (rules 2 and 3). A word list misfires; the escape is to reword the box, or to move it to a
  ready-for-human ticket blocked by the one that builds what it runs;
- blocker-edge: a "Blocked by #N", "after #N" or "once #N lands" where #N is an open issue with no
  native blocked-by edge, so the scheduler cannot see the wait the prose states (ADR-0020). A #N
  that is a closed issue, a pull request or no issue at all states no wait any more (#284): each is
  printed as a note, `ticket-shape: note: stale-prose: <what>`, and fails nothing. The state of a
  #N with no edge is asked of GitHub in one GraphQL query; a #N with an edge has its state in it;
- foreground: under `ready-for-agent`, a `**Session:** foreground` line, which no lane can honour;
- spec: under `ready-for-agent`, any sub-issue, since an issue with sub-issues is a spec and never a
  ticket.

    python3 -I tools/ticket_shape.py <number>     read the issue through gh
    python3 -I tools/ticket_shape.py -            read `gh issue view --json` output on stdin

The JSON is `gh issue view <n> --json number,body,labels,subIssuesSummary,blockedBy`, the fields
FIELDS names. Each note is a line `ticket-shape: note: <what>`, each failure a line
`ticket-shape: <rule>: <what>`, and the last line is the verdict: `ticket-shape: pass: ...` with
exit 0, or `ticket-shape: fail: ...` with exit 1 when a rule failed and exit 2 when nothing was
judged: the issue, or the state of a #N its prose names, could not be read, or the check itself
broke (a script it reuses or OWNERSHIP.md missing, or any other error). Python's own exit for an
uncaught error is 1, the code of a failed rule, so main() lets no error escape, and
.github/workflows/ticket-shape.yml relabels only a ticket that was judged.
"""
from __future__ import annotations

import functools
import importlib.util
import json
import os
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
# and in any case. Two differ from the list: #247 names "AWS" bare, and #198 says "looking the
# names up in AWS" of a companion that fails, a box a lane finishes against a double; #247 also
# asks that #198 pass. So "AWS" counts only as "against AWS", the reach a sitting has and a lane
# does not, as "against the applied" does for the applied stack. And #247's "merge body" is gone
# (#284): each task merge is its ticket's merge body, written by the lane's own integrate step
# (#226), so a box asking for a count in it is a lane's; "landing" and "pushed" still catch a box
# that needs the landing.
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
# What a #N the prose names can be. Only an open issue is a wait; the others are stale prose.
KINDS = ("OPEN", "CLOSED", "PULL_REQUEST", "MISSING")
STALE = {"CLOSED": "a closed issue", "PULL_REQUEST": "a pull request", "MISSING": "no issue"}
FOREGROUND = re.compile(r"(?m)^\*\*Session:\*\*[ \t]*foreground\b", re.IGNORECASE)

TOUCHES = re.compile(r"(?m)^\*\*Touches:\*\*[ \t]*(?P<value>[^\n]*)$")
# A Touches value saying the ticket writes nothing: the word "none", then anything.
WRITES_NOTHING = re.compile(r"^none\b", re.IGNORECASE)
# A code span, or the text between two.
SPAN_OR_TEXT = re.compile(r"`([^`\n]+)`|([^`]+)")
# A bare token that names a path: a directory separator, or a dot inside or leading a name. "e.g."
# and "i.e." are words, not paths; a URL's colon keeps it out.
BARE_PATH = re.compile(r"^(?=[^/]*/|\.?[\w-]*[\w-]\.[\w-]|\.[\w-])[\w./*-]+$")
NOT_PATHS = {"e.g", "i.e"}
# A word of the prose a Touches line mixes in, as #260 and #269 do: lower case, maybe hyphenated.
PROSE = re.compile(r"[a-z]+(?:['-][a-z]+)*")


class Unreadable(Exception):
    """The issue could not be read, so no rule was judged."""


class Issue(typing.NamedTuple):
    number: int
    body: str
    labels: tuple[str, ...]
    sub_issues: int
    blockers: tuple[int, ...]   # the open ones
    closed_blockers: tuple[int, ...] = ()   # the edges to a closed issue


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
            closed_blockers=tuple(int(node["number"]) for node in raw["blockedBy"]["nodes"]
                                  if node["state"] != "OPEN"),
        )
    except (KeyError, TypeError, ValueError) as error:
        raise Unreadable(f"the issue's JSON has no readable {error}") from error


def root_entries() -> set[str]:
    """The names at the repository's root, exactly as written: a filesystem that ignores case
    would otherwise take "Tools" for `tools`."""
    return set(os.listdir(ROOT))


def bare_path(token: str, parsed: list[tuple[str, str]]) -> bool:
    """Whether a bare token names a path: it has a `/` or a dot in a name, it is or begins a path
    OWNERSHIP.md names (`infra` is workstream 1's `/infra/`, which a slash or dot test would read as
    prose), or it is an entry at the repository's root."""
    if token in NOT_PATHS or not re.fullmatch(r"[\w./*-]+", token):
        return False
    if BARE_PATH.match(token):
        return True
    owned = (pattern.lstrip("/") for pattern, _ in parsed if pattern != "*")
    return (any(pattern.startswith(token + "/") or pattern == token for pattern in owned)
            or token in root_entries())


def touches(body: str, parsed: list[tuple[str, str]] | None = None) -> list[str] | None:
    """The paths of the Touches line, in its order: its code spans and its bare tokens that name
    paths, by bare_path(). None when there is no line, and [] when there is one with no path or one
    saying `none`."""
    read = read_touches(body, parsed)
    return None if read is None else read[0]


def read_touches(body: str, parsed: list[tuple[str, str]] | None = None
                 ) -> tuple[list[str], list[str]] | None:
    """The Touches line's paths and its unread tokens: those neither a path nor a lower-case
    word of prose. None when there is no line, and ([], []) for a line saying `none`: its prose is
    not read, so "(runs the script against throwaway PRs)" leaves no token unread."""
    line = TOUCHES.search(body)
    if not line:
        return None
    if WRITES_NOTHING.match(line.group("value")):
        return [], []
    parsed = ownership().rules() if parsed is None else parsed
    paths: list[str] = []
    unread: list[str] = []
    for span, text in SPAN_OR_TEXT.findall(line.group("value")):
        if span:
            paths.append(span)
            continue
        for token in re.split(r"[\s,;()]+", text):
            token = token.rstrip(".:")
            if bare_path(token, parsed):
                paths.append(token)
            elif token and not PROSE.fullmatch(token):
                unread.append(token)
    return paths, unread


def owner_of(path: str, parsed: list[tuple[str, str]]) -> str | None:
    """A Touches path's owner. A directory may be written without its slash (`tests/e2e`), which
    OWNERSHIP.md's `/tests/e2e/` would not match and the catch-all would, so a path naming an owned
    directory is read as that directory."""
    bare = path.strip().lstrip("/").rstrip("/")
    if any(pattern.lstrip("/") == bare + "/" for pattern, _ in parsed):
        return ownership().owner_of(bare + "/", parsed)
    return ownership().owner_of(bare, parsed)


def writes_nothing(body: str) -> bool:
    line = TOUCHES.search(body)
    return bool(line and WRITES_NOTHING.match(line.group("value")))


def stated(body: str) -> list[int]:
    """The numbers the body's prose waits for, by BLOCKER_PHRASES, ascending."""
    return sorted({int(number) for phrase in BLOCKER_PHRASES
                   for found in phrase.finditer(body)
                   for number in NUMBER.findall(found.group("numbers"))})


def states_of(issue: Issue, known: typing.Mapping[int, str] | None = None) -> dict[int, str]:
    """What each number the prose waits for, other than an open edge, is: one of KINDS. A closed
    edge says it is a closed issue; known gives others; the rest are asked of GitHub at once."""
    states = {number: "CLOSED" for number in issue.closed_blockers}
    states.update(known or {})
    missing = [n for n in stated(issue.body) if n not in issue.blockers and n not in states]
    if missing:
        states.update(look_up(missing))
    return states


def look_up(numbers: list[int]) -> dict[int, str]:
    """Each number's kind, from one GraphQL query of the repository gh resolves (GH_REPO in the
    Action, the checkout's remote elsewhere). gh exits 1 when a number is no issue or pull request
    and still prints the data with a NOT_FOUND error for it, so the answer is read whatever the
    exit; any other error, or an answer that does not parse, is Unreadable. gh fills the
    `{owner}` and `{repo}` field values from that repository (https://cli.github.com/manual/gh_api),
    and `issueOrPullRequest` is the repository field that answers for either kind
    (https://docs.github.com/en/graphql/reference/objects#repository)."""
    fields = " ".join(f"n{n}: issueOrPullRequest(number: {n}) "
                      "{ __typename ... on Issue { state } ... on PullRequest { state } }"
                      for n in numbers)
    query = ("query($owner: String!, $name: String!) "
             f"{{ repository(owner: $owner, name: $name) {{ {fields} }} }}")
    try:
        done = subprocess.run(["gh", "api", "graphql", "-F", "owner={owner}", "-F", "name={repo}",
                               "-f", f"query={query}"],
                              capture_output=True, text=True, check=False)
    except OSError as error:
        raise Unreadable(f"gh could not run: {error}") from error
    why = f"gh api graphql exited {done.returncode}: {done.stderr.strip()}"
    try:
        answer = json.loads(done.stdout)
        repository = answer["data"]["repository"]
        not_found = set()
        for error in answer.get("errors") or []:
            path = error.get("path") or []
            if error.get("type") != "NOT_FOUND" or len(path) != 2:
                raise Unreadable(f"{why}: {error.get('message', error)}")
            not_found.add(path[1])
        kinds = {}
        for n in numbers:
            node = repository[f"n{n}"]
            if node is None:
                if f"n{n}" not in not_found:
                    raise Unreadable(f"{why}: no answer for #{n}")
                kinds[n] = "MISSING"
            elif node["__typename"] == "PullRequest":
                kinds[n] = "PULL_REQUEST"
            else:
                kinds[n] = "OPEN" if node["state"] == "OPEN" else "CLOSED"
        return kinds
    except (json.JSONDecodeError, KeyError, TypeError, AttributeError) as error:
        raise Unreadable(f"{why}: the answer has no readable {error}") from error


def stale_prose(issue: Issue, states: typing.Mapping[int, str] | None = None) -> list[str]:
    """A note for each number the prose waits for that is no wait: a closed issue, a pull request
    or no issue. The scheduler has nothing to see; the prose is to be reworded, not edged."""
    states = states_of(issue, states)
    return [f"the body waits for #{n}, which is {STALE[states[n]]}, so no wait is left to edge; "
            "the line is stale prose" for n in stated(issue.body)
            if n not in issue.blockers and states[n] in STALE]


def judge(issue: Issue, parsed: list[tuple[str, str]] | None = None,
          states: typing.Mapping[int, str] | None = None) -> list[Failure]:
    """Every rule the issue breaks, in the order the docstring lists them. states gives the kind
    of a number the prose names (states_of()); one it lacks is asked of GitHub."""
    parsed = ownership().rules() if parsed is None else parsed
    failures: list[Failure] = []

    read = read_touches(issue.body, parsed)
    paths, unread = read if read is not None else (None, [])
    if paths == [] and not writes_nothing(issue.body):
        failures.append(Failure("touches", "the Touches line names no path, so no owner is "
                                "derived from it"))
    elif unread:
        failures.append(Failure("touches", "the Touches line has " + ", ".join(unread) +
                                ", neither a path nor prose, so its owners cannot all be derived"))
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

    states = states_of(issue, states)
    unedged = [n for n in stated(issue.body) if n not in issue.blockers and states[n] == "OPEN"]
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
        states = states_of(issue)
        failures = judge(issue, states=states)
        notes = stale_prose(issue, states)
    except (Exception, SystemExit) as error:
        # Unreadable is a read that failed; SystemExit is path-ownership.py's refusal of an
        # OWNERSHIP.md with no rules; anything else is the check broken. None of them judged the
        # issue, and each ends here on exit 2 with its verdict line (CODING_STANDARDS.md, "A check
        # fails closed").
        print(f"ticket-shape: fail: nothing judged: {type(error).__name__}: {error}")
        return 2
    for note in notes:
        print(f"ticket-shape: note: stale-prose: {note}")
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
