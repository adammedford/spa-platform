#!/usr/bin/env python3
"""Which failed test projects a change could have reached. Owner: integrator. Built in #375.

    python3 -I tools/test_reach.py --log <check log> --base <rev> --head <rev>

Run from the checkout the log was written in. The overnight integrate step runs it when its
integration gate goes red (.claude/workflows/overnight.js): on 2026-10-24 #305 changed Python under
tools/bench/ alone and was reset because 30 Spactl.Tests tests failed with the registry-schema
RefResolutionException that #373 tracks, and on 2026-10-23 #150 was reset by the same 30. A test
project the ticket's diff cannot reach is re-run there once, alone, before the merge is reset; this
tool supplies the facts that decision reads, and the workflow script makes it.

It reads two things:
- The log's verdict, its last non-empty line. "check: fail: the tests failed." is a test failure
  (tools/check.sh writes it when `dotnet test --solution` fails); any other line is not, and its
  failed projects are not read.
- The failed test projects: every line of the log's test run naming an assembly under
  `<project>/bin/` that "failed", as Microsoft Testing Platform prints one per project and again in
  its "Test run summary". The project is the directory before /bin/, so tests/e2e (whose assembly is
  Gateway.E2ETests.dll) and fixtures/Fixtures.Tests are named by where they live.

And it judges, for each failed project, the files `git diff --no-renames --name-only <base> <head>`
lists: the change the integrate step merged. A file reaches the project when any of these holds,
and is listed in its "reachedBy" with the rule that fired:
- "project": it lies under the directory of the project or of any project it references through
  ProjectReference, transitively.
- "item": it matches the Include of another item (None, EmbeddedResource, Content, ...) of a project
  file in that closure, such as Spactl.Tests' copy of contracts/registry.example.json.
- "build-wide": it is a file at the repository's top level (Directory.Build.props, global.json,
  SpaPlatform.slnx, nuget.config, .editorconfig, a README a test reads), or any .csproj, .props or
  .targets file, which the build of every project can read.
- "read at runtime": it lies under a path the test sources read by name from the repository root,
  RUNTIME_READS below, whatever their project references say.
- "whole solution": the project reads every project of the solution (WHOLE_SOLUTION below), and the
  file lies under the directory of any tracked project.

A failed project whose reach cannot be read reaches everything, as a check fails closed
(CODING_STANDARDS.md): a path outside this checkout, a directory with no project file, an Include
naming an MSBuild property. Being wrong in that direction costs a re-run that never happens, which is
the run's behaviour before #375; being wrong in the other costs a re-run of a project the change did
reach, which still has to pass alone and under the rest of the gate.

Its output is one JSON line on standard output, exit 0:
  {"failedAt": "tests"|"other", "verdict": "<the log's last line>",
   "projects": [{"project": "tests/Spactl.Tests", "reachedBy": ["<file> (<rule>)", ...]}],
   "changed": ["<file>", ...]}
A log it cannot read, or a git that cannot list the change, ends on `test-reach: fail: <why>` on
standard error with exit 2 and no JSON, so a caller never reads a judgement that was not made.
"""
from __future__ import annotations

import argparse
import json
import os
import posixpath
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

TESTS_FAILED = "check: fail: the tests failed."

# One project's line in Microsoft Testing Platform's output, as tools/check.sh's log holds it:
#   /<checkout>/tests/Spactl.Tests/bin/Debug/net10.0/Spactl.Tests.dll (net10.0|arm64) failed with 30 error(s) (7s 652ms)
FAILED_ASSEMBLY = re.compile(r"(?P<path>/\S+?)/bin/\S+\.dll \([^)]*\) failed\b")

# Paths the test sources read by name from the repository root, found by the string literals that
# name a top-level entry (tests/Platform.Abstractions.Tests reads contracts/*.md, tests/e2e reads
# infra/ and tools/aws-project.env, the harness reads fixtures/). Each top-level entry a test source
# names is a key of RUNTIME_ROOTS, with what covers it; tools/test_reach_tests.py fails when a test
# source names one that is not, so a new runtime read is decided here before it is trusted.
RUNTIME_READS = ("contracts/", "fixtures/", "infra/", "docs/plan/", "tools/aws-project.env")
RUNTIME_ROOTS = {
    "contracts": "RUNTIME_READS",
    "fixtures": "RUNTIME_READS",
    "infra": "RUNTIME_READS",
    "docs": "RUNTIME_READS names docs/plan/, the one folder of docs/ a test reads (04-tests.md)",
    "tools": "RUNTIME_READS names tools/aws-project.env; tools/spactl is a project",
    "src": "every folder a test reads under src/ is a project's",
    "tests": "every folder a test reads under tests/ is a project's",
    "SpaPlatform.slnx": "build-wide: a top-level file",
    "README.md": "build-wide: a top-level file",
}

# Test projects that read every project of the solution: the architecture tests load each project
# file and scan each project's sources for rule-id literals (A-01..A-05, ADR-0015).
WHOLE_SOLUTION = ("tests/Gateway.ArchitectureTests",)

BUILD_WIDE_SUFFIXES = (".csproj", ".props", ".targets")


def fail(why: str) -> int:
    print(f"test-reach: fail: {why}", file=sys.stderr, flush=True)
    return 2


def git(top: str, *args: str) -> list[str]:
    out = subprocess.run(["git", "-C", top, *args], capture_output=True, text=True, timeout=60)
    if out.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {out.stderr.strip() or out.returncode}")
    return [line for line in out.stdout.splitlines() if line]


def glob_regex(pattern: str) -> re.Pattern[str]:
    """An MSBuild Include as a regex over repository paths: ** any depth, * and ? within a name."""
    out, i = "", 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            out, i = out + "(?:.*/)?", i + 3
        elif pattern.startswith("**", i):
            out, i = out + ".*", i + 2
        elif pattern[i] == "*":
            out, i = out + "[^/]*", i + 1
        elif pattern[i] == "?":
            out, i = out + "[^/]", i + 1
        else:
            out, i = out + re.escape(pattern[i]), i + 1
    return re.compile(out + r"\Z")


def read_project(top: str, path: str) -> tuple[list[str], list[str]]:
    """A project file's ProjectReferences and its other Includes, as repository paths. An Include
    naming an MSBuild property cannot be resolved here, so it is "**", which matches every file."""
    root = ET.parse(os.path.join(top, path)).getroot()
    here = posixpath.dirname(path)
    refs, items = [], []
    for element in root.iter():
        include = element.get("Include")
        if include is None or not isinstance(element.tag, str):
            continue
        tag = element.tag.split("}")[-1]
        for part in (p.strip() for p in include.split(";")):
            if not part:
                continue
            if "$(" in part or "@(" in part:
                items.append("**")
                continue
            resolved = posixpath.normpath(posixpath.join(here, part.replace("\\", "/")))
            (refs if tag == "ProjectReference" else items).append(resolved)
    return refs, items


def under(path: str, directory: str) -> bool:
    return directory in ("", ".") or path.startswith(directory.rstrip("/") + "/")


def reach(top: str, project: str, changed: list[str], all_projects: list[str]) -> list[str]:
    if project.startswith("/"):
        return [f"{project} (outside this checkout, so its reach is unknown)"]
    files = [p for p in all_projects if posixpath.dirname(p) == project]
    if not files:
        return [f"{project} (no project file there, so its reach is unknown)"]
    seen, queue, items = set(), list(files), []
    while queue:
        path = queue.pop()
        if path in seen:
            continue
        seen.add(path)
        if not os.path.isfile(os.path.join(top, path)):
            return [f"{path} (a referenced project file is missing, so its reach is unknown)"]
        try:
            refs, own = read_project(top, path)
        except ET.ParseError as error:
            return [f"{path} (unreadable project file: {error}, so its reach is unknown)"]
        queue.extend(refs)
        items.extend(own)
    dirs = sorted({posixpath.dirname(p) for p in seen})
    patterns = [glob_regex(i) for i in items]
    project_dirs = sorted({posixpath.dirname(p) for p in all_projects})
    hits = []
    for f in changed:
        if any(under(f, d) for d in dirs):
            hits.append(f"{f} (project)")
        elif any(p.match(f) for p in patterns):
            hits.append(f"{f} (item)")
        elif "/" not in f or f.endswith(BUILD_WIDE_SUFFIXES):
            hits.append(f"{f} (build-wide)")
        elif any(f == r or under(f, r) for r in RUNTIME_READS):
            hits.append(f"{f} (read at runtime)")
        elif project in WHOLE_SOLUTION and any(under(f, d) for d in project_dirs):
            hits.append(f"{f} (whole solution)")
    return hits


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="tools/test_reach.py")
    parser.add_argument("--log", required=True)
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", required=True)
    args = parser.parse_args(argv)
    try:
        with open(args.log, encoding="utf-8", errors="replace") as handle:
            lines = handle.read().splitlines()
    except OSError as error:
        return fail(f"cannot read the log {args.log}: {error.strerror or error}")
    nonempty = [line.strip() for line in lines if line.strip()]
    if not nonempty:
        return fail(f"the log {args.log} is empty, so it holds no verdict")
    verdict = nonempty[-1]
    try:
        top = git(".", "rev-parse", "--show-toplevel")[0]
        changed = git(top, "diff", "--no-renames", "--name-only", args.base, args.head)
        all_projects = git(top, "ls-files", "*.csproj")
    except (RuntimeError, IndexError, OSError, subprocess.TimeoutExpired) as error:
        return fail(str(error) or "git gave no answer")
    failed_at = "tests" if verdict == TESTS_FAILED else "other"
    projects = []
    if failed_at == "tests":
        prefix = os.path.realpath(top).rstrip("/") + "/"
        names = []
        for line in lines:
            match = FAILED_ASSEMBLY.search(line)
            if not match:
                continue
            path = match["path"]
            real = os.path.realpath(path)
            name = real[len(prefix):] if real.startswith(prefix) else (path[len(top) + 1:] if path.startswith(top.rstrip("/") + "/") else path)
            if name not in names:
                names.append(name)
        projects = [{"project": n, "reachedBy": reach(top, n, changed, all_projects)} for n in names]
    print(json.dumps({"failedAt": failed_at, "verdict": verdict, "projects": projects, "changed": changed}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
