#!/usr/bin/env python3
"""Every tracked workflow script parses. Owner: integrator.

A workflow script is a tracked `.claude/workflows/*.js`, which the Workflow tool runs (ADR-0026).
The tool wraps the file's body in an async function of its own, so the file may use `return` and
`await` at its top level and begins with `export const meta`. This check rewrites that one export
to `const meta`, wraps the body in an async function it never calls, and asks node to parse the
result and run nothing (`node --check`). The temporary copy ends in `.cjs` to select CommonJS
explicitly: an ambiguous `.js` copy can incorrectly pass a stray import or export under Node 22,
masking other syntax errors too. Node documents the explicit marker and syntax detection at
https://nodejs.org/api/packages.html#determining-module-system.
A script that does not parse fails at the push that broke it, naming the file and node's message,
rather than at the launch that would have run it that night.

The same pass fails a line that reads an agent's result as an empty list (#277): `(x && x.field) || []`,
or the same guard through `Array.isArray` or a further condition, such as
`(x && Array.isArray(x.f) && x.f) || []` or `(x && !x.ok && x.f) || []`, where the guard and the
field are on the one name `x`. A workflow's agent returns null when it fails, and that read turns a
stage that returned nothing into a stage that found nothing, so the script's result reads clean
(CODING_STANDARDS.md, "A check fails closed"). The site is either fixed, so the stage that returned
nothing is named as failed in the script's result and report, or exempted by a comment on the same
line, `// empty-ok: <reason>`, saying why an empty list is the true reading there (a null `x` is
named elsewhere, or the list is only reported, never judged). A marker with no reason fails, as does
a marker on a line with no site, which a fix left behind and which would exempt whatever moved onto
the line. The search is by line and by text: a site split across lines is not seen, and the
pattern is written on one line in every script it reads.

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
# `( x && ... x.field ) || []`: the guard's name and the field's owner are the same identifier, and
# the condition between may hold one level of parentheses (an Array.isArray call).
SWALLOW = re.compile(r"\(\s*([A-Za-z_$][\w$]*)\s*&&(?:[^()]|\([^()]*\))*?(?<![\w$.])\1\.[\w$]+\s*\)\s*\|\|\s*\[\]")
MARKER = re.compile(r"//\s*empty-ok\b(:?)(.*)$")


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
        copy = os.path.join(scratch, os.path.basename(name) + ".cjs")
        with open(copy, "w", encoding="utf-8") as handle:
            handle.write(wrapped(source))
        return subprocess.run(["node", "--check", copy], capture_output=True, text=True)


def swallows(root: str, name: str) -> list[str]:
    """Each line of the script that reads an agent result as an empty list without a reasoned
    exemption, or carries an exemption with no reason or no site, as a message naming file:line."""
    found = []
    with open(os.path.join(root, name), encoding="utf-8") as handle:
        lines = handle.read().splitlines()
    for number, line in enumerate(lines, 1):
        marker = MARKER.search(line)
        code = line[:marker.start()] if marker else line
        site = SWALLOW.search(code)
        if marker and not (marker.group(1) and marker.group(2).strip()):
            found.append(f"{name}:{number} has an empty-ok exemption with no reason: the marker is "
                         "`// empty-ok: <why an empty list is the true reading here>`")
        elif marker and not site:
            found.append(f"{name}:{number} has an empty-ok exemption but no site to exempt; remove the marker")
        elif site and not marker:
            found.append(f"{name}:{number} reads an agent result as an empty list: {site.group(0)} reads an "
                         "agent that returned nothing as one that found nothing; name the failed stage in the "
                         "result and report, or exempt the line with `// empty-ok: <reason>`")
    return found


def main() -> int:
    root = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True,
                          check=True).stdout.strip()
    scripts = workflow_scripts(root)
    if not scripts:
        print("workflow-parse: found no tracked workflow script, so this check read nothing.", file=sys.stderr)
        return 1
    version = subprocess.run(["node", "--version"], capture_output=True, text=True, check=True).stdout.strip()
    broken = 0
    swallowing = 0
    for name in scripts:
        found = swallows(root, name)
        if found:
            swallowing += 1
            for message in found:
                print(f"workflow-parse: {message}", file=sys.stderr)
        parsed = parse(root, name)
        if parsed.returncode != 0:
            broken += 1
            print(f"workflow-parse: {name} does not parse under node {version}:", file=sys.stderr)
            for line in parsed.stderr.splitlines():
                print(f"  {line}", file=sys.stderr)
    if broken:
        print(f"workflow-parse: {broken} of {len(scripts)} tracked workflow scripts do not parse.", file=sys.stderr)
    if swallowing:
        print(f"workflow-parse: {swallowing} of {len(scripts)} tracked workflow scripts read an agent result "
              "as an empty list.", file=sys.stderr)
    if broken or swallowing:
        return 1
    noun = "script parses" if len(scripts) == 1 else "scripts parse"
    print(f"workflow-parse: {len(scripts)} tracked workflow {noun} under node {version}: {', '.join(scripts)}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
