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

The rules, as the collect step states them:
- A ticket is an open issue carrying `ready-for-agent`, `ready-for-human` or `needs-triage`. An issue
  with sub-issues is a spec, listed under "parents", and never a ticket.
- id: the `**Plan row:** X` value when it is a plan row id, else `issue-<number>`.
- owner: the first role the `**Owner:**` line names, by its place in the line, mapped to
  OWNERSHIP.md's roles; an I-* row is the integrator's. When the line names other roles, "why" says
  they land in their own pull requests.
- blockedBy: the open blockers, by id when tonight's set holds them, else `issue-<number>`.
- status, in this order: ready-for-human; needs-triage; claimed (an assignee, or a `task/<id>-`
  branch on a row not landed); ready-for-agent when every open blocker is itself a ready-for-agent
  ticket tonight; blocked otherwise. The last two depend on each other, so they are settled by
  repeating until nothing changes. An issue carrying two of the labels is one ticket, under the
  label this order puts first.
- openBoxes: the `- [ ]` lines in the body, or -1 when it has no checkbox at all.
- willEndPartial (#228): why a lane cannot finish the ticket, [] when nothing says so. Since #248
  each entry is a failure of tools/ticket_shape.py, the check that holds #227's rules where a ticket
  is written: {"rule", "reason"}, the rule's name and the line the check prints for it, in the
  check's order. The ticket is judged under the label it is listed by, so the rules that read
  `ready-for-agent` alone (sitting-or-landing, foreground) flag only a lane's ticket. A spec is a
  parent and never judged. One grammar, then, at creation and at launch: a ticket the check fails
  is a ticket the launch plan forecasts as partial. The launch plan forecasts from it.
- n: plan rows in the plan's order, then `issue-<number>` tickets by number.

Two tickets with one id stop it: exit 1, stdout empty, both issues named on stderr. A read that fails
also exits 1, because a partial answer would send a lane at the wrong work, and so does a
ticket-shape check that cannot load or judge (its script, .github/path-ownership.py or
OWNERSHIP.md missing or broken), because a forecast with nothing judged would read as none flagged. In JSON mode everything
but the answer goes to stderr.

--from DIR reads ready-for-agent.json, ready-for-human.json and needs-triage.json (each the JSON of
`gh issue list`; a missing file is an empty list), and optionally branches.txt (one branch name a
line) and roadmap.json (`tools/roadmap.py --json`). Absent ones are read live.
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


def roadmap(root: pathlib.Path) -> dict:
    """tools/roadmap.py --json, which says which rows have landed and the plan's order."""
    script = str(root / "tools" / "roadmap.py")
    return json.loads(run([sys.executable, "-I", script, "--json"], root))


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
            )
    return list(found.values())


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


def will_end_partial(issue: Issue, rules: list[tuple[str, str]]) -> list[dict]:
    """Why a lane cannot finish the ticket: ticket_shape's failures, judged under its label."""
    try:
        shape = ticket_shape()
        judged = shape.Issue(number=issue.number, body=issue.body, labels=(issue.label,),
                             sub_issues=issue.sub_issues, blockers=issue.blockers)
        failures = shape.judge(judged, rules)
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
            rules: list[tuple[str, str]] | None = None) -> dict:
    """The answer, from what was read. Raises Refused when two tickets share an id, or when the
    ticket-shape check cannot judge. rules is OWNERSHIP.md as A-05 parses it, which main() reads
    with the rest of the input; None reads it here."""
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
    by_number = {issue.number: issue for issue in tickets}
    id_of = {issue.number: ticket_id(issue) for issue in tickets}

    def blocker_id(number: int) -> str:
        return id_of.get(number, f"issue-{number}")

    status: dict[int, str] = {}
    why: dict[int, str] = {}
    for issue in tickets:
        tid = id_of[issue.number]
        claim = next((name for name in branch_names if name.startswith(f"task/{tid}-")), None)
        if issue.label == "ready-for-human":
            status[issue.number], why[issue.number] = "ready-for-human", "labelled ready-for-human"
        elif issue.label == "needs-triage":
            status[issue.number], why[issue.number] = "needs-triage", "labelled needs-triage"
        elif issue.assigned:
            status[issue.number], why[issue.number] = "claimed", "ready-for-agent with an assignee"
        elif claim and tid not in landed:
            status[issue.number] = "claimed"
            why[issue.number] = f"its row is claimed by the branch {claim}, not landed"
        else:
            status[issue.number] = "ready-for-agent"

    # A ready-for-agent ticket stays ready only while every open blocker is a ready-for-agent
    # ticket tonight; demoting one can demote another, so repeat until nothing moves.
    moved = True
    while moved:
        moved = False
        for issue in tickets:
            if status[issue.number] != "ready-for-agent":
                continue
            waits = [n for n in issue.blockers
                     if n not in by_number or status[n] != "ready-for-agent"]
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
            "willEndPartial": will_end_partial(issue, rules),
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
                        help="read saved gh listings (and optionally branches.txt, roadmap.json) "
                             "from DIR instead of asking GitHub")
    arguments = parser.parse_args(argv)
    root = pathlib.Path(__file__).resolve().parent.parent
    saved = pathlib.Path(arguments.saved) if arguments.saved else None

    def kept(name: str) -> str | None:
        """A saved file's text, or None when no DIR was given or DIR holds no such file."""
        if saved is None or not (saved / name).exists():
            return None
        return (saved / name).read_text()

    try:
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
        answer = resolve(listings, branch_names, road, ownership_rules())
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
