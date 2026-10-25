#!/usr/bin/env python3
"""Tonight's overnight tickets, resolved from GitHub Issues by rule rather than by reading. Owner:
integrator.

The overnight workflow's collect step turns the open issues into the tickets its lanes work. Until
#187 an agent applied the rules by reading them in its prompt, and two kinds of issue passed through
silently on 2026-10-20:
- two issues carried one plan row (#56 and #180, both 1.4), and the run keys everything by id, so
  one overwrote the other;
- the four spec issues #139 to #142 carried `ready-for-agent` and no checkbox, so each would have had
  a lane of its own.

This script applies the same rules, adds those two, and prints the result for the workflow to use.
A pre-launch audit is the same command.

    python3 -I tools/overnight_collect.py --json               read GitHub, git and the roadmap
    python3 -I tools/overnight_collect.py --json --from DIR    read saved listings instead of GitHub
    python3 -I COPY/tools/overnight_collect.py --json --repo CHECKOUT
                                                               forecast with another copy's rules

--repo names the checkout whose git, gh repository (every gh call runs in it), tools/roadmap.py and
docs/agents/issue-tracker.md are read, from any directory in it; without it, the checkout this
script is in. It is for a forecast by origin/main's rules before the main checkout has moved (#377):
a copy of the tool kept outside any checkout, read against the main checkout. The rules themselves,
tools/ticket_shape.py, .github/path-ownership.py and OWNERSHIP.md, are the copy's own, read beside
it. A --repo that is not a git checkout exits 1 naming it.

The rules, as the collect step states them:
- A ticket is an open issue carrying `ready-for-agent`, `ready-for-human` or `needs-triage`. An issue
  with sub-issues is a spec, listed under "parents", and never a ticket.
- id: the `**Plan row:** X` value when it is a plan row id, else `issue-<number>`.
- owner: the first role the `**Owner:**` line names, by its place in the line, mapped to
  OWNERSHIP.md's roles; an I-* row is the integrator's. When the line names other roles, "why" says
  they land in their own pull requests.
- blockedBy: the open blockers, by id when tonight's set holds them, else `issue-<number>`.
- status, in this order: ready-for-human; needs-triage; needs-triage again for a ready-for-agent
  label some other account applied (#316); claimed (an assignee, or a `task/<id>-` branch on a row
  not landed); close-in-morning (#379) when every acceptance box is ticked (openBoxes 0), so the
  ticket gets no lane and the report lists it with the issues to close, whatever it waits on;
  ready-for-agent when every open blocker is itself a ready-for-agent or close-in-morning ticket
  tonight; blocked otherwise. The last two depend on each other, so they are settled by repeating
  until nothing changes. An issue carrying two of the labels is one ticket, under the label this order
  puts first.
- Who labelled it (#316): `ready-for-agent` is a lane's promise (docs/agents/triage-labels.md), and
  CODING_STANDARDS.md -> "An authorisation is read from its author" says it counts only when the
  account the rule names applied it. So each ticket listed under it has its timeline read, and the
  actor of the last `labeled` event that applied `ready-for-agent` must be the account
  docs/agents/issue-tracker.md names in its "The human's account is `@<login>`." line, or the
  repository's owner (from the issue's own URL) while the guide names none. Any other actor, a
  deleted one, or no such event, and the ticket is needs-triage with the actor in "why": whoever
  can label an issue cannot send a lane at it.
- openBoxes: the `- [ ]` lines in the body, or -1 when it has no checkbox at all.
- willEndPartial (#228): why a lane cannot finish the ticket, [] when nothing says so. Since #248
  each entry is a failure of tools/ticket_shape.py, the check that holds #227's rules where a ticket
  is written: {"rule", "reason"}, the rule's name and the line the check prints for it, in the
  check's order. The ticket is judged under the label it is listed by, so the rules that read
  `ready-for-agent` alone (sitting-or-landing, foreground) flag only a lane's ticket. A spec is a
  parent and never judged. One grammar, then, at creation and at launch: a ticket the check fails
  is a ticket the launch plan forecasts as partial. The launch plan forecasts from it.
  A number the prose waits for is a wait only while it is an open issue (#284): a listed issue is
  one, and the state of each other number is asked of GitHub in one query; a closed issue or a
  pull request named in prose is stale prose, which the check notes and the forecast leaves out.
- n: plan rows in the plan's order, then `issue-<number>` tickets by number.

Two tickets with one id stop it: exit 1, stdout empty, both issues named on stderr. A read that fails
also exits 1, because a partial answer would send a lane at the wrong work, and so does a
ticket-shape check that cannot load or judge (its script, .github/path-ownership.py or
OWNERSHIP.md missing or broken), because a forecast with nothing judged would read as none flagged. In JSON mode everything
but the answer goes to stderr.

--from DIR reads ready-for-agent.json, ready-for-human.json and needs-triage.json (each the JSON of
`gh issue list`; a missing file is an empty list), and optionally branches.txt (one branch name a
line) and roadmap.json (`tools/roadmap.py --json`). Absent ones are read live. It also reads
timelines/<n>.json for each ticket listed under ready-for-agent, the JSON array GitHub's timeline
endpoint gives; one missing is refused, not read live and not read as labelled by the account.
"""
from __future__ import annotations

import argparse
import functools
import importlib.util
import json
import pathlib
import re
import subprocess
import sys
import typing

LABELS = ("ready-for-agent", "ready-for-human", "needs-triage")
ROOT = pathlib.Path(__file__).resolve().parent.parent

# The labels in the order the collect step's status rule reads them. A human relabelling by hand can
# leave two of them on one issue, which then comes back in two listings: it is one ticket, under the
# label this order puts first.
PRECEDENCE = ("ready-for-human", "needs-triage", "ready-for-agent")
FIELDS = "number,title,body,labels,assignees,url,blockedBy,subIssuesSummary"
REPO_URL = "https://github.com/adammedford/spa-platform/issues/{}"

# A plan row's id, as tools/roadmap.py's ID reads one: I-4, 2.1, 1.6a, P2-7. Copied rather than
# imported, so this script runs without the other one; overnight_collect_tests.py holds the two equal.
PLAN_ID = re.compile(r"^(?:I-\d+|\d+\.\d+[a-z]?|P2-\d+)$")

PLAN_ROW = re.compile(r"(?m)^\*\*Plan row:\*\*[ \t]*(?P<value>[^\n]*)$")
OWNER = re.compile(r"(?m)^\*\*Owner:\*\*[ \t]*(?P<value>[^\n]*)$")
# Who may apply `ready-for-agent` (#316): the account the tracker guide names on a line of this
# shape, matched narrowly so that prose about accounts is not read as naming one.
# .github/steering.py reads the same line for who may write a `Confirm`, and
# overnight_collect_tests.py holds the two equal.
GUIDE = "docs/agents/issue-tracker.md"
ACCOUNT_LINE = re.compile(
    r"(?m)^- \*\*The human's account is `@(?P<login>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))`\.\*\*"
)
# The repository's owner, read off a URL GitHub gave for one of its issues, while the guide names
# no account: no further call, and the owner GitHub says rather than one typed here.
URL_OWNER = re.compile(r"^https://github\.com/(?P<owner>[^/\s]+)/")
# Where a ticket's timeline is read: every `labeled` event, oldest first, one JSON object a line.
# gh fills {owner}/{repo} from the remote of the directory it runs in, and {number} is the issue.
# https://docs.github.com/en/rest/issues/timeline#list-timeline-events-for-an-issue
GH_TIMELINE = (
    "gh", "api", "--paginate", "repos/{owner}/{repo}/issues/{number}/timeline?per_page=100",
    "--jq", '.[] | select(.event == "labeled") | tojson',
)

# Why a ticket with every acceptance box ticked gets no lane (#379). overnight.js's report prompt
# finds those tickets by this sentence, so the two say it alike.
CLOSE_IN_MORNING = "every acceptance box is ticked; close it in the morning"
# The statuses a dependant may wait on and still be ready: a ticket a lane runs first tonight, and
# one with nothing left to build, which the workflow settles as met before any lane starts.
MET_TONIGHT = ("ready-for-agent", "close-in-morning")

OPEN_BOX = re.compile(r"(?m)^\s*- \[ \]")
ANY_BOX = re.compile(r"(?m)^\s*- \[[ xX]\]")

# The roles of OWNERSHIP.md, each with the ways an Owner line names it. The first role the line
# names is the owner, so "ws1-infra, run by the human" is workstream 1's and "integrator (anyone may
# write a change request)" the integrator's. The order here is no ranking: a line is read by place.
ROLES = (
    ("ws1-infra", r"\bws1\b|\bworkstream 1\b|^1 aws\b|\baws and iac\b"),
    ("ws2-hosting", r"\bws2\b|\bworkstream 2\b|^2 hosting\b|\bhosting and registry\b"),
    ("ws3-identity", r"\bws3\b|\bworkstream 3\b|^3 oauth\b|\boauth and sessions\b"),
    ("ws4-proxy", r"\bws4\b|\bworkstream 4\b|^4 api\b|\bapi and security\b"),
    ("ws5-fixtures", r"\bws5\b|\bworkstream 5\b|^5 fixtures\b"),
    ("ws6-verification",
     r"\bws6\b|\bworkstream 6\b|^6 verification\b|\bverification and operations\b"),
    ("integrator", r"\bintegrator\b"),
)


class Refused(Exception):
    """A reason the answer cannot be given: two tickets with one id, or a read that failed."""


class Issue(typing.NamedTuple):
    number: int
    title: str
    body: str
    label: str
    assigned: bool
    url: str
    blockers: tuple[int, ...]   # the open ones
    sub_issues: int
    closed_blockers: tuple[int, ...] = ()   # the edges to a closed issue


# --- reading: the one place this script touches the world --------------------------------------

def run(args: list[str], root: pathlib.Path) -> str:
    """A command's stdout, or Refused naming it when it fails or cannot start."""
    try:
        done = subprocess.run(args, cwd=root, capture_output=True, text=True, check=False)
    except OSError as error:
        raise Refused(f"{args[0]} could not run: {error}") from error
    if done.returncode != 0:
        raise Refused(f"{' '.join(args[:4])} ... exited {done.returncode}: {done.stderr.strip()}")
    return done.stdout


def listing(root: pathlib.Path, label: str) -> list[dict]:
    """The open issues carrying a label, as gh lists them: at most 500, the limit the collect
    step's own calls name, where gh would stop at 30 unless told."""
    out = run(["gh", "issue", "list", "--state", "open", "--label", label, "--limit", "500",
               "--json", FIELDS], root)
    return json.loads(out or "[]")


def branches(root: pathlib.Path) -> list[str]:
    """The task branches, local and on origin, as names without the remote."""
    out = run(["git", "for-each-ref", "--format=%(refname)", "refs/heads/task/",
               "refs/remotes/origin/task/"], root)
    names = []
    for ref in out.split():
        for prefix in ("refs/heads/", "refs/remotes/origin/"):
            if ref.startswith(prefix):
                names.append(ref[len(prefix):])
    return names


def timeline(root: pathlib.Path, number: int) -> list[dict]:
    """The issue's `labeled` events, oldest first, as GitHub's timeline gives them. A line that is
    not a JSON object is refused, not skipped: a skipped one could be the label's last write."""
    command = [part.replace("{number}", str(number)) for part in GH_TIMELINE]
    events = []
    for line in run(command, root).splitlines():
        if not line.strip():
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError as error:
            raise Refused(f"the timeline of #{number} gave a line that is not JSON: "
                          f"{line[:60]!r}") from error
        if not isinstance(event, dict):
            raise Refused(f"the timeline of #{number} gave something other than events: "
                          f"{line[:60]!r}")
        events.append(event)
    return events


def roadmap(root: pathlib.Path) -> dict:
    """tools/roadmap.py --json, which says which rows have landed and the plan's order."""
    script = str(root / "tools" / "roadmap.py")
    return json.loads(run([sys.executable, "-I", script, "--json"], root))


def checkout(given: str) -> pathlib.Path:
    """The top of the git checkout --repo names (#377), from any directory in it, as git finds it.
    Anything else is refused naming it: read as no branches, no roadmap and no guide, it would
    forecast every row unclaimed."""
    where = pathlib.Path(given)
    if not where.is_dir():
        raise Refused(f"--repo {given} is not a git checkout: not a directory")
    try:
        top = run(["git", "rev-parse", "--show-toplevel"], where).strip()
    except Refused as refusal:
        raise Refused(f"--repo {given} is not a git checkout: {refusal}") from refusal
    if not top:
        raise Refused(f"--repo {given} is not a git checkout: git names no working tree")
    return pathlib.Path(top)


def tracker_guide(root: pathlib.Path) -> str | None:
    """docs/agents/issue-tracker.md, which names the account that may apply ready-for-agent, or None
    when the checkout has no guide."""
    path = root / GUIDE
    return path.read_text() if path.exists() else None


# --- resolving: pure functions over what was read ------------------------------------------------

def issues_of(listings: dict[str, list[dict]]) -> list[Issue]:
    """Each listed issue once, by number, under the first of its labels in PRECEDENCE."""
    found: dict[int, Issue] = {}
    for label in PRECEDENCE:
        for raw in listings.get(label, []):
            number = int(raw["number"])
            if number in found:
                continue
            summary = raw.get("subIssuesSummary") or {}
            found[number] = Issue(
                number=number,
                title=raw.get("title", ""),
                body=raw.get("body") or "",
                label=label,
                assigned=bool(raw.get("assignees")),
                url=raw.get("url") or REPO_URL.format(raw["number"]),
                # gh gives blockedBy as {"nodes": [...], "totalCount": n}, each node with its
                # state; a listing in any other shape is refused as unreadable, not read as free.
                blockers=tuple(int(node["number"]) for node in raw["blockedBy"]["nodes"]
                               if node["state"] == "OPEN"),
                sub_issues=int(summary.get("total", 0) or 0),
                closed_blockers=tuple(int(node["number"]) for node in raw["blockedBy"]["nodes"]
                                      if node["state"] != "OPEN"),
            )
    return list(found.values())


def labelled_for_agent(listings: dict[str, list[dict]]) -> list[int]:
    """The tickets listed under ready-for-agent, whose labeller the status rule reads: each issue
    issues_of() keeps under that label and does not hold as a spec."""
    return [issue.number for issue in issues_of(listings)
            if issue.label == "ready-for-agent" and issue.sub_issues == 0]


def named_account(guide: str | None) -> str | None:
    """The account the tracker guide names on its "The human's account is `@<login>`." line, or
    None when it names none (or there is no guide)."""
    found = ACCOUNT_LINE.search(guide or "")
    return found.group("login") if found else None


def account_for(named: str | None, url: str) -> tuple[str, str]:
    """Who may apply ready-for-agent to the issue at url, and where that comes from: the guide's
    account, else the repository's owner as the issue's URL names it."""
    if named:
        return named, f"the account {GUIDE} names"
    found = URL_OWNER.match(url)
    owner = found.group("owner") if found else ""
    return owner, f"the repository's owner, since {GUIDE} names no account"


def last_labeller(events: list[dict]) -> str | None:
    """The actor of the last `labeled` event that applied ready-for-agent: its login, "" when
    GitHub names no account (a deleted one), or None when no event applied the label. Raises
    KeyError or TypeError, which main() reports as unreadable, on an event not in GitHub's shape."""
    found = None
    for event in events:
        if event["event"] == "labeled" and event["label"]["name"] == "ready-for-agent":
            found = (event.get("actor") or {}).get("login") or ""
    return found


def unauthorised_label(issue: Issue, events: list[dict], named: str | None) -> str | None:
    """Why the ticket's ready-for-agent label does not count, or None when the account applied
    it."""
    account, source = account_for(named, issue.url)
    actor = last_labeller(events)
    if actor is None:
        return (f"ready-for-agent, but its timeline shows no `labeled` event applying it, so "
                f"nothing says @{account} did, {source}")
    if not account or actor.casefold() != account.casefold():
        who = f"@{actor}" if actor else "an account GitHub no longer names"
        return (f"ready-for-agent was last applied by {who}, not by @{account}, {source}; a "
                "lane goes only where that account applied it")
    return None


def ticket_id(issue: Issue) -> str:
    row = PLAN_ROW.search(issue.body)
    value = row.group("value").strip() if row else ""
    token = re.split(r"[\s;,]", value, maxsplit=1)[0] if value else ""
    return token if PLAN_ID.match(token) else f"issue-{issue.number}"


def owner_of(issue: Issue, tid: str) -> tuple[str, list[str]]:
    """The ticket's owner, and the other roles its Owner line names, in the line's order. The owner
    is the first role named, or the integrator for an I-* row or a line naming none."""
    line = OWNER.search(issue.body)
    value = line.group("value").strip().lower() if line else ""
    places = []
    for role, pattern in ROLES:
        found = re.search(pattern, value)
        if found:
            places.append((found.start(), role))
    named = [role for _, role in sorted(places)]
    owner = "integrator" if tid.startswith("I-") or not named else named[0]
    return owner, [role for role in named if role != owner]


def others_land_apart(others: list[str]) -> str:
    """What "why" adds for the other owners an Owner line names, as the collect step says it."""
    if not others:
        return ""
    if len(others) == 1:
        return f"; {others[0]}, also named as an owner, lands in its own pull request"
    return (f"; {', '.join(others[:-1])} and {others[-1]}, also named as owners, land in their "
            "own pull requests")


@functools.cache
def ticket_shape():
    """tools/ticket_shape.py, the check #247 runs where a ticket is written (#248). Loaded by path:
    under -I the script's own directory is not on sys.path. It loads this script in turn, for
    OWNER and ROLES, on its first judgement; that second copy is read-only and loads nothing back,
    so the two never recurse."""
    path = pathlib.Path(__file__).resolve().parent / "ticket_shape.py"
    spec = importlib.util.spec_from_file_location("ticket_shape", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def ownership_rules() -> list[tuple[str, str]]:
    """OWNERSHIP.md as A-05 reads it, through the check that judges by it. Read once a run."""
    try:
        return ticket_shape().ownership().rules()
    except (Exception, SystemExit) as error:
        # SystemExit is path-ownership.py's refusal of an OWNERSHIP.md with no rules.
        raise Refused(f"the ticket-shape check could not load: {type(error).__name__}: "
                      f"{error}") from error


def judged_issue(issue: Issue):
    """The ticket as ticket_shape reads one, under the label it is listed by."""
    return ticket_shape().Issue(number=issue.number, body=issue.body, labels=(issue.label,),
                                sub_issues=issue.sub_issues, blockers=issue.blockers,
                                closed_blockers=issue.closed_blockers)


def prose_states(listed: list[Issue], tickets: list[Issue],
                 known: dict[int, str] | None = None,
                 root: pathlib.Path = ROOT) -> dict[int, str]:
    """What each number the tickets' prose waits for is (ticket_shape.KINDS), so that a closed
    issue or a pull request named in prose is stale prose and not a forecast partial (#284). A
    listed issue is open; known gives others; the rest are asked of GitHub in one query, run in
    root so that gh resolves the checkout's repository."""
    try:
        shape = ticket_shape()
        states = {issue.number: "OPEN" for issue in listed}
        states.update(known or {})
        missing = sorted({n for issue in tickets for n in shape.stated(issue.body)
                          if n not in issue.blockers and n not in issue.closed_blockers
                          and n not in states})
        if missing:
            states.update(shape.look_up(missing, cwd=root))
        return states
    except (Exception, SystemExit) as error:
        raise Refused(f"the ticket-shape check could not read the issues the prose names: "
                      f"{type(error).__name__}: {error}") from error


def will_end_partial(issue: Issue, rules: list[tuple[str, str]],
                     states: dict[int, str] | None = None) -> list[dict]:
    """Why a lane cannot finish the ticket: ticket_shape's failures, judged under its label."""
    try:
        failures = ticket_shape().judge(judged_issue(issue), rules, states)
    except (Exception, SystemExit) as error:
        raise Refused(f"the ticket-shape check could not judge #{issue.number}: "
                      f"{type(error).__name__}: {error}") from error
    return [{"rule": failure.rule, "reason": f"{failure.rule}: {failure.detail}"}
            for failure in failures]


def open_boxes(body: str) -> int:
    return len(OPEN_BOX.findall(body)) if ANY_BOX.search(body) else -1


def slug_of(title: str, tid: str) -> str:
    text = re.sub(rf"^{re.escape(tid)}\s*[—–-]+\s*", "", title)
    words = re.findall(r"[a-z0-9]+", text.lower())
    return "-".join(words[:8])[:48].strip("-") or tid


def landed_ids(road: dict) -> set[str]:
    found = set()
    for key in ("rows", "splits", "tickets"):
        for tid, state in (road.get(key) or {}).items():
            if state == "landed":
                found.add(tid)
    return found


def resolve(listings: dict[str, list[dict]], branch_names: list[str], road: dict,
            rules: list[tuple[str, str]] | None = None,
            states: dict[int, str] | None = None,
            timelines: dict[int, list[dict]] | None = None,
            guide: str | None = None, root: pathlib.Path = ROOT) -> dict:
    """The answer, from what was read. Raises Refused when two tickets share an id, or when the
    ticket-shape check cannot judge. rules is OWNERSHIP.md as A-05 parses it, which main() reads
    with the rest of the input; None reads it here. states gives what a number the prose names is,
    beyond the listed issues; prose_states() asks GitHub for the rest. timelines gives each
    ready-for-agent ticket's timeline events, and timeline() asks GitHub for any it lacks; guide is
    the text of docs/agents/issue-tracker.md, which names the account that may apply the label.
    root is the checkout those two asks of GitHub run in."""
    landed = landed_ids(road)
    plan_order = list((road.get("rows") or {}).keys())
    parents, tickets = [], []
    for issue in issues_of(listings):
        if issue.sub_issues > 0:
            parents.append({"issue": issue.number, "title": issue.title, "label": issue.label,
                            "subIssues": issue.sub_issues})
        else:
            tickets.append(issue)

    ids: dict[str, list[int]] = {}
    for issue in tickets:
        ids.setdefault(ticket_id(issue), []).append(issue.number)
    shared = {tid: numbers for tid, numbers in ids.items() if len(numbers) > 1}
    if shared:
        raise Refused("; ".join(
            f"issues {' and '.join(f'#{n}' for n in sorted(numbers))} share the id {tid}"
            for tid, numbers in sorted(shared.items())
        ) + ". Every run keys a ticket by its id, so one would overwrite the other: give one its "
            "own **Plan row:** (none, for an issue that is not a plan row) and run again.")

    rules = ownership_rules() if rules is None else rules
    states = prose_states(issues_of(listings), tickets, states, root)
    by_number = {issue.number: issue for issue in tickets}
    id_of = {issue.number: ticket_id(issue) for issue in tickets}

    def blocker_id(number: int) -> str:
        return id_of.get(number, f"issue-{number}")

    named = named_account(guide)
    timelines = dict(timelines or {})
    status: dict[int, str] = {}
    why: dict[int, str] = {}
    for issue in tickets:
        tid = id_of[issue.number]
        claim = next((name for name in branch_names if name.startswith(f"task/{tid}-")), None)
        if issue.label == "ready-for-agent" and issue.number not in timelines:
            timelines[issue.number] = timeline(root, issue.number)
        unlabelled = (unauthorised_label(issue, timelines[issue.number], named)
                      if issue.label == "ready-for-agent" else None)
        if issue.label == "ready-for-human":
            status[issue.number], why[issue.number] = "ready-for-human", "labelled ready-for-human"
        elif issue.label == "needs-triage":
            status[issue.number], why[issue.number] = "needs-triage", "labelled needs-triage"
        elif unlabelled:
            status[issue.number], why[issue.number] = "needs-triage", unlabelled
        elif issue.assigned:
            status[issue.number], why[issue.number] = "claimed", "ready-for-agent with an assignee"
        elif claim and tid not in landed:
            status[issue.number] = "claimed"
            why[issue.number] = f"its row is claimed by the branch {claim}, not landed"
        elif open_boxes(issue.body) == 0:
            status[issue.number], why[issue.number] = "close-in-morning", CLOSE_IN_MORNING
        else:
            status[issue.number] = "ready-for-agent"

    # A ready-for-agent ticket stays ready only while every open blocker is a ready-for-agent or a
    # close-in-morning ticket tonight; demoting one can demote another, so repeat until nothing
    # moves.
    moved = True
    while moved:
        moved = False
        for issue in tickets:
            if status[issue.number] != "ready-for-agent":
                continue
            waits = [n for n in issue.blockers
                     if n not in by_number or status[n] not in MET_TONIGHT]
            if waits:
                status[issue.number] = "blocked"
                why[issue.number] = ("ready-for-agent, but waits on "
                                     + ", ".join(blocker_id(n) for n in waits))
                moved = True
    for issue in tickets:
        if status[issue.number] == "ready-for-agent":
            why.setdefault(issue.number, (
                "ready-for-agent; its open blockers run first tonight" if issue.blockers
                else "ready-for-agent with no open blocker"))

    def order(issue: Issue) -> tuple:
        tid = id_of[issue.number]
        if tid in plan_order:
            return (0, plan_order.index(tid), issue.number)
        return (1, 0, issue.number)

    resolved = []
    for n, issue in enumerate(sorted(tickets, key=order), start=1):
        tid = id_of[issue.number]
        owner, others = owner_of(issue, tid)
        resolved.append({
            "n": n, "id": tid, "slug": slug_of(issue.title, tid), "issue": issue.number,
            "url": issue.url, "title": issue.title, "status": status[issue.number],
            "owner": owner, "blockedBy": [blocker_id(b) for b in issue.blockers],
            "why": why[issue.number] + others_land_apart(others),
            "openBoxes": open_boxes(issue.body),
            "willEndPartial": will_end_partial(issue, rules, states),
        })
    return {
        "tickets": resolved,
        "parents": parents,
        "landed": sorted(landed),
        "landedButOpen": [{"id": t["id"], "issue": t["issue"]} for t in resolved
                          if t["id"] in landed],
        "closedGates": road.get("closed_gates", []),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="tools/overnight_collect.py",
        description="Resolve tonight's overnight tickets from GitHub Issues, by the collect rules.",
    )
    parser.add_argument("--json", action="store_true", required=True,
                        help="print the tickets and parents as JSON")
    parser.add_argument("--from", dest="saved", metavar="DIR",
                        help="read saved gh listings, timelines/<n>.json for each ready-for-agent "
                             "ticket, and optionally branches.txt and roadmap.json from DIR "
                             "instead of asking GitHub")
    parser.add_argument("--repo", metavar="DIR",
                        help="the checkout whose git, gh repository, roadmap and tracker guide "
                             "are read, for a copy of this tool kept outside it; default: the "
                             "checkout this script is in")
    arguments = parser.parse_args(argv)
    root = ROOT
    saved = pathlib.Path(arguments.saved) if arguments.saved else None

    def kept(name: str) -> str | None:
        """A saved file's text, or None when no DIR was given or DIR holds no such file."""
        if saved is None or not (saved / name).exists():
            return None
        return (saved / name).read_text()

    try:
        if arguments.repo is not None:
            root = checkout(arguments.repo)
        listings = {}
        for label in LABELS:
            text = kept(f"{label}.json")
            if text is not None:
                listings[label] = json.loads(text)
            else:
                listings[label] = [] if saved is not None else listing(root, label)
        text = kept("branches.txt")
        branch_names = text.split() if text is not None else branches(root)
        text = kept("roadmap.json")
        road = json.loads(text) if text is not None else roadmap(root)
        timelines = {}
        if saved is not None:
            for number in labelled_for_agent(listings):
                text = kept(f"timelines/{number}.json")
                if text is None:
                    raise Refused(f"{saved / 'timelines' / f'{number}.json'} is missing, so who "
                                  f"applied ready-for-agent to #{number} is unread")
                timelines[number] = json.loads(text)
        answer = resolve(listings, branch_names, road, ownership_rules(), timelines=timelines,
                         guide=tracker_guide(root), root=root)
    except Refused as refusal:
        print(f"overnight_collect: no tickets printed: {refusal}", file=sys.stderr)
        return 1
    except (json.JSONDecodeError, OSError, KeyError, TypeError, ValueError) as error:
        print(f"overnight_collect: no tickets printed: could not read the input: {error}",
              file=sys.stderr)
        return 1

    counts = {}
    for ticket in answer["tickets"]:
        counts[ticket["status"]] = counts.get(ticket["status"], 0) + 1
    print(f"overnight_collect: {len(answer['tickets'])} tickets "
          f"({', '.join(f'{v} {k}' for k, v in sorted(counts.items()))}), "
          f"{len(answer['parents'])} parents; "
          f"{sum(1 for t in answer['tickets'] if t['willEndPartial'])} will end partial.",
          file=sys.stderr)
    print(json.dumps(answer, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
