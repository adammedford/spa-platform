#!/usr/bin/env python3
"""The endpoint paths: every gateway path a skill, a runbook or the test catalogue names is one the
browser contract makes known. Owner: integrator. Built in #760; tools/check.sh runs it in both modes.

    python3 -I tools/endpoint_paths.py

Why (#760). In the sitting of 2026-11-02 and 2026-11-03 the wiz skill's observables for ending an
app's sessions named `/bff/user` answering 200, then 401. No such endpoint exists: it answers 404,
and `GET /bff/session` is the endpoint. It took three round trips with the human and a post that
blamed the runbook, and nothing read the path a skill named against the contract.

It reads the tracked files of the repository it runs in, from the top level: every `.md` file under
`.claude/skills/` at any depth, every `.md` file directly in `docs/runbooks/`, and
`docs/plan/04-tests.md`. In them it collects every path that begins `/bff/`, `/api/`, `/readyz` or
`/healthz`, ending at the first character that cannot be part of a path, and with any query string
and trailing full stop dropped. A path counts at the start of a line, after a character that cannot
end a file-system path segment (so `tests/api/x` is not one), or after a URL's host
(`https://app1.spa-poc.test/bff/user` is) or a host and port with no scheme (`localhost:5180/readyz`
is, since `curl` takes an address with its scheme left out). A bare prefix, `/bff/` or `/api/` with nothing after it
or only `*`, names the prefix and is not a path.

A path is known when `contracts/http-contract.md` makes it known, which this reads from the
contract at each run, never from a copy here (CODING_STANDARDS.md, "A guard reads the thing it
guards"):

- a path the first cell of a row of the Endpoints table names, such as `GET /bff/session`; a cell
  naming a path with a `{placeholder}`, `/api/{name}/{rest}`, makes known every path that begins
  with the text before its first placeholder, so an `/api/` path matches on its prefix;
- a path the reserved-paths sentence after the table names ("An SPA cannot serve content under
  ..."), except one ending `/`, which names a prefix and makes no path under it known: `/healthz`
  and `/readyz` are known, `/bff/user` is not.

It prints each unknown path as `endpoint-paths: <file>:<line>: <path> is not an endpoint
contracts/http-contract.md makes known` on stderr, then one verdict line on stdout,
`endpoint-paths: pass: <n> paths in <m> files` or `endpoint-paths: fail: ...`, and exits 1 on any
unknown path. A contract it cannot read, with no Endpoints table or no reserved-paths sentence, or a
file it cannot list or read, or a listing that holds none of the files it reads, exits 2: a check
that read nothing must not pass (CODING_STANDARDS.md, "A check fails closed").

Run its companion: python3 -I tools/endpoint_paths_tests.py
"""
from __future__ import annotations

import pathlib
import re
import subprocess
import sys

ME = "endpoint-paths"
CONTRACT = "contracts/http-contract.md"
SENTENCE = "An SPA cannot serve content under"
# Where a path a session steers by is written: the skills, the runbooks and the test catalogue.
READ = (re.compile(r"\.claude/skills/.+\.md"), re.compile(r"docs/runbooks/[^/]+\.md"),
        re.compile(r"docs/plan/04-tests\.md"))
PATH = re.compile(r"/(?:bff|api)/[A-Za-z0-9_.~{}*/-]*|/(?:readyz|healthz)[A-Za-z0-9_.~{}*/-]*")
# A character that ends a file-system path segment before a `/`: `tests/api` is a directory.
SEGMENT = re.compile(r"[A-Za-z0-9_.-]")
URL_HOST = re.compile(r"(?:[a-z][a-z0-9+.-]*://[^\s/`'\"()<>]+|[A-Za-z0-9.-]+:[0-9]+)$")


class Unreadable(Exception):
    """What the check could not read, so it judged nothing."""


def git(*args: str) -> str:
    done = subprocess.run(["git", *args], capture_output=True, text=True, check=False)
    if done.returncode != 0:
        raise Unreadable(f"git {' '.join(args)} failed: {done.stderr.strip()}")
    return done.stdout


def known_paths(contract: str) -> tuple[set[str], set[str]]:
    """The exact paths and the prefixes the contract makes known."""
    exact: set[str] = set()
    prefixes: set[str] = set()
    table = re.search(r"^## Endpoints\n(.*?)(?=^## )", contract, re.M | re.S)
    if table is None:
        raise Unreadable(f"{CONTRACT} has no Endpoints table")
    rows = [line for line in table.group(1).splitlines() if line.startswith("|")]
    if not rows:
        raise Unreadable(f"{CONTRACT}'s Endpoints table has no rows")
    for row in rows:
        cell = row.split("|")[1]
        for path in re.findall(r"(?<![^\s`])/[^\s`?]*", cell):
            if "{" in path:
                prefixes.add(path[:path.index("{")])
            else:
                exact.add(path)
    found = re.search(re.escape(SENTENCE) + r"(.*?)\.(?:\s|$)", " ".join(table.group(1).split()), re.S)
    if found is None:
        raise Unreadable(f"{CONTRACT} has no reserved-paths sentence after the Endpoints table")
    named = re.findall(r"`(/[^`]*)`", found.group(1))
    if not named:
        raise Unreadable(f"{CONTRACT}'s reserved-paths sentence names no path")
    exact.update(path for path in named if not path.endswith("/"))
    return exact, prefixes


def paths_in(line: str) -> list[str]:
    found = []
    for match in PATH.finditer(line):
        before = line[:match.start()]
        if before and SEGMENT.match(before[-1]) and not URL_HOST.search(before):
            continue
        path = match.group(0).rstrip(".")
        if re.fullmatch(r"/(?:bff|api)/\**", path):
            continue
        found.append(path)
    return found


def main() -> int:
    try:
        top = git("rev-parse", "--show-toplevel").strip()
        root = pathlib.Path(top)
        try:
            contract = (root / CONTRACT).read_text()
        except OSError as error:
            raise Unreadable(f"{CONTRACT} could not be read: {error}") from error
        exact, prefixes = known_paths(contract)
        tracked = git("-C", top, "ls-files", "-z").split("\0")
        files = sorted(path for path in tracked if path and any(rule.fullmatch(path) for rule in READ))
        if not files:
            raise Unreadable("no tracked skill, runbook or test-catalogue file was found to read")
        unknown = 0
        count = 0
        for name in files:
            try:
                text = (root / name).read_text()
            except OSError as error:
                raise Unreadable(f"{name} could not be read: {error}") from error
            for number, line in enumerate(text.splitlines(), 1):
                for path in paths_in(line):
                    count += 1
                    if path in exact or any(path.startswith(prefix) for prefix in prefixes):
                        continue
                    unknown += 1
                    print(f"{ME}: {name}:{number}: {path} is not an endpoint {CONTRACT} makes known",
                          file=sys.stderr)
    except Unreadable as error:
        print(f"{ME}: {error}", file=sys.stderr)
        print(f"{ME}: fail: nothing was judged")
        return 2
    if unknown:
        print(f"{ME}: fail: {unknown} of {count} paths in {len(files)} files are not endpoints {CONTRACT} makes known")
        return 1
    print(f"{ME}: pass: {count} paths in {len(files)} files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
