#!/usr/bin/env python3
"""Every tracked workflow script parses. Owner: integrator.

A workflow script is a tracked `.claude/workflows/*.js`, which the Workflow tool runs (ADR-0024).
The tool wraps the file's body in an async function of its own, so the file may use `return` and
`await` at its top level and begins with `export const meta`. Neither parses as a plain script:
`node --check` refuses the top-level `return`, and the same body wrapped in a function refuses
the `export` on line 1 (#135's grilling record, under node v26.8.1). So this check rewrites that
one line to `const meta`, wraps the body in an async function it never calls, and asks node to
parse the result and run nothing (`node --check`). A script that does not parse fails at the push
that broke it, naming the file and node's message, rather than at the launch that would have run
it that night.

Finding no script at all fails, since a check that found nothing to read would pass having read
nothing. CI runs it on `ubuntu-latest` with the node that image ships and prints `node --version`
beside it (`docs/dependencies.md`); the macOS bash 3.2 job does not run it.

Run from the top level of a checkout: python3 -I .github/workflow-parse.py
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile

PREFIX = ".claude/workflows/"
EXPORT = re.compile(r"^export const meta\b", re.MULTILINE)


def tracked(root: str) -> list[str]:
    listing = subprocess.run(["git", "ls-files", "-z"], cwd=root, capture_output=True, check=True)
    return [name for name in listing.stdout.decode("utf-8", "surrogateescape").split("\0") if name]


def workflow_scripts(root: str) -> list[str]:
    scripts = []
    for name in tracked(root):
        if not (name.startswith(PREFIX) and name.endswith(".js")):
            continue
        if "/" in name[len(PREFIX):]:
            continue
        path = os.path.join(root, name)
        if os.path.islink(path) or not os.path.isfile(path):
            continue
        scripts.append(name)
    return scripts


def wrapped(source: str) -> str:
    """The body as the Workflow tool sees it: `meta` a plain constant, inside an async function
    that is never called, so parsing it runs nothing."""
    body = EXPORT.sub("const meta", source, count=1)
    return "(async function workflow() {\n" + body + "\n});\n"


def parse(root: str, name: str) -> subprocess.CompletedProcess:
    with open(os.path.join(root, name), encoding="utf-8") as handle:
        source = handle.read()
    with tempfile.TemporaryDirectory() as scratch:
        copy = os.path.join(scratch, os.path.basename(name))
        with open(copy, "w", encoding="utf-8") as handle:
            handle.write(wrapped(source))
        return subprocess.run(["node", "--check", copy], capture_output=True, text=True)


def main() -> int:
    root = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True,
                          check=True).stdout.strip()
    scripts = workflow_scripts(root)
    if not scripts:
        print("workflow-parse: found no tracked workflow script, so this check read nothing.", file=sys.stderr)
        return 1
    version = subprocess.run(["node", "--version"], capture_output=True, text=True, check=True).stdout.strip()
    broken = 0
    for name in scripts:
        parsed = parse(root, name)
        if parsed.returncode != 0:
            broken += 1
            print(f"workflow-parse: {name} does not parse under node {version}:", file=sys.stderr)
            for line in parsed.stderr.splitlines():
                print(f"  {line}", file=sys.stderr)
    if broken:
        print(f"workflow-parse: {broken} of {len(scripts)} tracked workflow scripts do not parse.", file=sys.stderr)
        return 1
    noun = "script parses" if len(scripts) == 1 else "scripts parse"
    print(f"workflow-parse: {len(scripts)} tracked workflow {noun} under node {version}: {', '.join(scripts)}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
