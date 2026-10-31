#!/usr/bin/env python3
"""The derived issue labels: kind, area and vendor, worked out from the issue alone. Owner: integrator.

An issue's triage labels say what it needs (docs/agents/tracker-policy.md, "Lifecycle: not labels").
These say what it is, and nothing judges them: each is a function of what the issue already
carries, read against the tables in tools/issue_labels.json, so the same issue always gets the same
labels and no session or agent decides them.

- kind: `kind:report` when the title is a run's record (`Overnight <date>`, `Retro <date>`,
  `Audit <date>`, `Sequence <date>`); otherwise `kind:parent` when it has a sub-issue, and
  `kind:ticket` when it has none.
- area: each `**Touches:**` path, read as tools/ticket_shape.py reads it, matched against the
  `areas` table by its longest prefix; the issue carries every area its paths reach. An issue with
  no Touches line, or one saying `none`, gets no area, since nothing says what it writes. A path no
  entry matches is a gap in the table, and the issue is skipped rather than given a guess.
- vendor: the values of a `**Vendor:**` line (`none`, or `aws`, `cognito`, ...), with every vendor
  a Touches path implies by the `vendors` table: anything under infra/ is AWS, and so is each named
  adapter file. The line holds the one call a path cannot make ("this assumes Cognito's token
  shape"), written once by whoever files the ticket. A `none` line beside a path that implies a
  vendor, or a vendor the table does not define, is a contradiction, and the issue is skipped. An
  `area:product` issue with no Vendor line is printed as a note and labelled from its paths.

Only the labels of those three groups are written: a stale one is removed by name and a missing
one added, through the REST routes that add or remove the labels they name and leave the rest, so a
triage relabel that .github/workflows/ticket-shape.yml makes at the same moment is never undone.

    python3 -I tools/issue_labels.py check <number>|-|--all    say what would change; exit 1 on drift
    python3 -I tools/issue_labels.py apply <number>|--all      make it so
    python3 -I tools/issue_labels.py labels check|apply        the label definitions themselves

`-` reads `gh issue view --json` output (FIELDS) on stdin. `labels apply` creates or updates each
label the table defines, and deletes each `retire` label that no issue or pull request carries; one
still in use is refused, never stripped. Each line starts `issue-labels:`, and the last is the
verdict: `issue-labels: pass: ...` with exit 0, or `issue-labels: fail: ...` with exit 1 when an
issue drifted (check) or was skipped, and exit 2 when nothing could be judged or a write failed.
.github/workflows/issue-labels.yml runs it.
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
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parent.parent
TABLE = ROOT / "tools" / "issue_labels.json"
FIELDS = "number,title,body,labels,subIssuesSummary"
VENDOR = re.compile(r"(?m)^\*\*Vendor:\*\*[ \t]*(?P<value>[^\n]*)$")


def load(name: str, path: pathlib.Path):
    """A sibling script as a module, as tools/ticket_shape.py loads its own."""
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# Loaded on first use, inside main()'s handler, so a missing sibling is exit 2 and not a traceback.
@functools.cache
def shape():
    return load("ticket_shape", ROOT / "tools" / "ticket_shape.py")


class Unreadable(Exception):
    """The issue, the table or GitHub could not be read, so nothing was judged."""


class Table(typing.NamedTuple):
    groups: tuple[str, ...]
    labels: dict[str, dict]       # name -> {color, description}
    retire: tuple[str, ...]
    reports: re.Pattern
    areas: tuple[tuple[str, str], ...]
    vendors: tuple[tuple[str, str], ...]

    def owns(self, label: str) -> bool:
        return label.split(":", 1)[0] in self.groups and ":" in label


class Issue(typing.NamedTuple):
    number: int
    title: str
    body: str
    labels: tuple[str, ...]
    sub_issues: int


class Derived(typing.NamedTuple):
    labels: frozenset[str]
    problems: tuple[str, ...]    # each one skips the issue
    notes: tuple[str, ...]


def table_of(raw: object) -> Table:
    try:
        table = Table(
            groups=tuple(raw["groups"]),
            labels={entry["name"]: {"color": entry["color"], "description": entry["description"]}
                    for entry in raw["labels"]},
            retire=tuple(raw["retire"]),
            reports=re.compile(raw["reports"]),
            areas=tuple((prefix, area) for prefix, area in raw["areas"]),
            vendors=tuple((prefix, vendor) for prefix, vendor in raw["vendors"]),
        )
    except (KeyError, TypeError, ValueError, re.error) as error:
        raise Unreadable(f"{TABLE.name} has no readable {error}") from error
    # A table naming a label it does not define would write one GitHub invents, with no colour or
    # description, so it is refused here rather than at the first issue it reaches.
    named = ({f"area:{area}" for _, area in table.areas}
             | {f"vendor:{vendor}" for _, vendor in table.vendors}
             | {"kind:report", "kind:parent", "kind:ticket"})
    undefined = sorted(named - set(table.labels))
    if undefined:
        raise Unreadable(f"{TABLE.name} names labels it does not define: {', '.join(undefined)}")
    foreign = sorted(name for name in table.labels if not table.owns(name))
    if foreign:
        raise Unreadable(f"{TABLE.name} defines labels outside its groups: {', '.join(foreign)}")
    return table


def read_table() -> Table:
    try:
        return table_of(json.loads(TABLE.read_text(encoding="utf-8")))
    except (OSError, json.JSONDecodeError) as error:
        raise Unreadable(f"{TABLE.name} could not be read: {error}") from error


def issue_of(raw: object) -> Issue:
    try:
        return Issue(number=int(raw["number"]), title=raw["title"] or "", body=raw["body"] or "",
                     labels=tuple(label["name"] for label in raw["labels"]),
                     sub_issues=int(raw["subIssuesSummary"]["total"]))
    except (KeyError, TypeError, ValueError) as error:
        raise Unreadable(f"the issue's JSON has no readable {error}") from error


def normal(path: str) -> str:
    """A Touches path as the tables write it: no leading `/` or `./`."""
    path = path.strip()
    while path.startswith("./") or path.startswith("/"):
        path = path[2:] if path.startswith("./") else path[1:]
    return path


def matches(path: str, prefix: str) -> bool:
    """Whether a table prefix covers a path; a directory written without its slash (`infra`) is
    read as the directory."""
    return path.startswith(prefix) or (path + "/").startswith(prefix)


def area_of(path: str, table: Table) -> str | None:
    found = [(len(prefix), area) for prefix, area in table.areas if matches(path, prefix)]
    return max(found)[1] if found else None


def kind_of(issue: Issue, table: Table) -> str:
    if table.reports.match(issue.title):
        return "report"
    return "parent" if issue.sub_issues else "ticket"


def derive(issue: Issue, table: Table, parsed: list[tuple[str, str]] | None = None) -> Derived:
    labels = {f"kind:{kind_of(issue, table)}"}
    problems: list[str] = []
    notes: list[str] = []
    read = shape().read_touches(issue.body, parsed)
    paths = [normal(path) for path in (read[0] if read else [])]
    implied: set[str] = set()
    for path in paths:
        area = area_of(path, table)
        if area is None:
            problems.append(f"no area in {TABLE.name} covers the Touches path {path}")
        else:
            labels.add(f"area:{area}")
        implied |= {vendor for prefix, vendor in table.vendors if matches(path, prefix)}
    known = {name.split(":", 1)[1] for name in table.labels if name.startswith("vendor:")}
    line = VENDOR.search(issue.body)
    stated: set[str] = set()
    if line:
        values = [value for value in re.split(r"[\s,;`]+", line.group("value").lower()) if value]
        if values == ["none"]:
            if implied:
                problems.append(f"the Vendor line says none, and its Touches paths imply "
                                f"{', '.join(sorted(implied))}")
        else:
            stated = set(values)
            unknown = sorted(stated - known)
            if unknown:
                problems.append(f"the Vendor line names {', '.join(unknown)}, which "
                                f"{TABLE.name} does not define")
    elif "area:product" in labels:
        notes.append("an area:product issue with no Vendor line; its vendors are read from its "
                     "paths alone")
    labels |= {f"vendor:{vendor}" for vendor in (stated | implied) & known}
    return Derived(frozenset(labels), tuple(problems), tuple(notes))


def changes(issue: Issue, derived: Derived, table: Table) -> tuple[list[str], list[str]]:
    """The labels to add and the ones to remove: only the table's groups are ever touched."""
    current = {label for label in issue.labels if table.owns(label)}
    return sorted(derived.labels - current), sorted(current - derived.labels)


def gh(*args: str) -> str:
    try:
        done = subprocess.run(["gh", *args], capture_output=True, text=True, check=False)
    except OSError as error:
        raise Unreadable(f"gh could not run: {error}") from error
    if done.returncode != 0:
        raise Unreadable(f"gh {' '.join(args[:3])} exited {done.returncode}: {done.stderr.strip()}")
    return done.stdout


def parse(text: str) -> object:
    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        raise Unreadable(f"gh's JSON does not parse: {error}") from error


def issues(target: str) -> list[Issue]:
    if target == "-":
        return [issue_of(parse(sys.stdin.read()))]
    if target == "--all":
        listed = parse(gh("issue", "list", "--state", "open", "--limit", "1000", "--json", FIELDS))
        if not isinstance(listed, list):
            raise Unreadable("gh issue list gave no list")
        return [issue_of(raw) for raw in listed]
    if target.isdigit():
        return [issue_of(parse(gh("issue", "view", target, "--json", FIELDS)))]
    raise Unreadable(f"{target!r} is neither an issue number, - nor --all")


def quoted(name: str) -> str:
    return urllib.parse.quote(name, safe="")


def write(number: int, add: list[str], remove: list[str]) -> None:
    if add:
        gh("api", "--method", "POST", f"repos/{{owner}}/{{repo}}/issues/{number}/labels",
           *[arg for name in add for arg in ("-f", f"labels[]={name}")])
    for name in remove:
        gh("api", "--method", "DELETE", f"repos/{{owner}}/{{repo}}/issues/{number}/labels/{quoted(name)}")


def repo_labels() -> dict[str, dict]:
    listed = parse(gh("label", "list", "--limit", "500", "--json", "name,color,description"))
    return {entry["name"]: entry for entry in listed}


def run_issues(apply: bool, target: str) -> int:
    table = read_table()
    found = issues(target)
    parsed = shape().ownership().rules()
    present = repo_labels() if apply else {}
    drifted = skipped = written = 0
    for issue in found:
        derived = derive(issue, table, parsed)
        for note in derived.notes:
            print(f"issue-labels: note: #{issue.number}: {note}")
        if derived.problems:
            skipped += 1
            for problem in derived.problems:
                print(f"issue-labels: skip: #{issue.number}: {problem}")
            continue
        add, remove = changes(issue, derived, table)
        if not add and not remove:
            if len(found) == 1:
                print(f"issue-labels: #{issue.number}: in sync")
            continue
        drifted += 1
        said = "; ".join(part for part in (add and f"add {', '.join(add)}",
                                           remove and f"remove {', '.join(remove)}") if part)
        print(f"issue-labels: #{issue.number}: {said}")
        if apply:
            missing = [name for name in add if name not in present]
            if missing:
                raise Unreadable(f"the repository has no label {', '.join(missing)}; run "
                                 f"`labels apply` first")
            write(issue.number, add, remove)
            written += 1
    counted = f"{len(found)} issue(s) read, {drifted} drifted, {skipped} skipped"
    if apply:
        counted += f", {written} written"
    if skipped or (drifted and not apply):
        print(f"issue-labels: fail: {counted}")
        return 1
    print(f"issue-labels: pass: {counted}")
    return 0


def in_use(name: str) -> bool:
    for kind in ("issue", "pr"):
        listed = parse(gh(kind, "list", "--state", "all", "--label", name, "--limit", "1",
                          "--json", "number"))
        if listed:
            return True
    return False


def run_labels(apply: bool) -> int:
    table = read_table()
    present = repo_labels()
    plan: list[tuple[str, str]] = []
    for name, wanted in table.labels.items():
        have = present.get(name)
        if have is None:
            plan.append(("create", name))
        elif (have.get("color", "").lower(), have.get("description", "")) != \
                (wanted["color"].lower(), wanted["description"]):
            plan.append(("update", name))
    refused = []
    for name in table.retire:
        if name in present:
            if in_use(name):
                refused.append(name)
            else:
                plan.append(("delete", name))
    for action, name in plan:
        print(f"issue-labels: label: {action} {name}")
        if not apply:
            continue
        if action == "create":
            gh("api", "--method", "POST", "repos/{owner}/{repo}/labels", "-f", f"name={name}",
               "-f", f"color={table.labels[name]['color']}",
               "-f", f"description={table.labels[name]['description']}")
        elif action == "update":
            gh("api", "--method", "PATCH", f"repos/{{owner}}/{{repo}}/labels/{quoted(name)}",
               "-f", f"color={table.labels[name]['color']}",
               "-f", f"description={table.labels[name]['description']}")
        else:
            gh("api", "--method", "DELETE", f"repos/{{owner}}/{{repo}}/labels/{quoted(name)}")
    for name in refused:
        print(f"issue-labels: label: refuse to delete {name}: an issue or pull request carries it")
    counted = f"{len(plan)} label change(s){' made' if apply else ' pending'}, {len(refused)} refused"
    if refused or (plan and not apply):
        print(f"issue-labels: fail: {counted}")
        return 1
    print(f"issue-labels: pass: {counted}")
    return 0


USAGE = "usage: issue_labels.py check|apply <number>|-|--all, or issue_labels.py labels check|apply"


def main(argv: list[str]) -> int:
    try:
        if len(argv) == 2 and argv[0] == "labels" and argv[1] in ("check", "apply"):
            return run_labels(argv[1] == "apply")
        if len(argv) == 2 and argv[0] in ("check", "apply") and not (argv[0] == "apply" and argv[1] == "-"):
            return run_issues(argv[0] == "apply", argv[1])
        print(USAGE)
        print("issue-labels: fail: nothing judged: no command")
        return 2
    except (Exception, SystemExit) as error:
        # A read or a write that failed, path-ownership.py's refusal of OWNERSHIP.md, or the script
        # broken: none of them is a judgement, so each ends on exit 2 with its verdict line.
        print(f"issue-labels: fail: nothing judged: {type(error).__name__}: {error}")
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
