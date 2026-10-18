#!/usr/bin/env python3
"""Every tracked shell script parses. Owner: integrator.

CI runs this so a shell script that does not parse fails at the push that broke it, not inside
whatever runs it next (#68). The local check, tools/check.sh, is run as written by the pre-push
hook, by a person and by every overnight lane, so a syntax error there would stop all of them at
once; on 2026-10-17 a command pasted into every lane's prompt lost one character and 15 agents
spent about 85 tool calls repairing it before any could run.

A shell script is a tracked file whose name ends in `.sh`, or whose first line is a shebang naming
bash or sh, directly or through env: `.githooks/pre-push` has no extension. A tracked symbolic link
is skipped, because git tracks the link and not the file it names.

Each is read with `/bin/bash -n`, which reads the commands without running them
(https://www.gnu.org/software/bash/manual/bash.html#The-Set-Builtin, `-n`), so nothing a script
does happens here. /bin/bash, not the bash on PATH, as the scripts' test companions name it: on
CI's macOS job that is the bash 3.2 the scripts are written for, so syntax only a later bash accepts
fails there, and the Linux job reads them with bash 5.

Finding no script at all fails, since a check that found nothing to read would pass having read
nothing.

Run from the top level of a checkout: python3 -I .github/shell-parse.py
"""

from __future__ import annotations

import os
import subprocess
import sys

BASH = "/bin/bash"
SHELLS = {"bash", "sh"}


def tracked(root: str) -> list[str]:
    listing = subprocess.run(["git", "ls-files", "-z"], cwd=root, capture_output=True, check=True)
    return [name for name in listing.stdout.decode("utf-8", "surrogateescape").split("\0") if name]


def names_a_shell(first_line: bytes) -> bool:
    """Whether a shebang line names bash or sh: `#!/bin/bash`, `#!/usr/bin/env bash`, `#! /bin/sh`."""
    if not first_line.startswith(b"#!"):
        return False
    words = first_line[2:].decode("utf-8", "replace").split()
    if words and os.path.basename(words[0]) == "env":
        words = [word for word in words[1:] if not word.startswith("-")]
    return bool(words) and os.path.basename(words[0]) in SHELLS


def shell_scripts(root: str) -> list[str]:
    scripts = []
    for name in tracked(root):
        path = os.path.join(root, name)
        if os.path.islink(path) or not os.path.isfile(path):
            continue
        if name.endswith(".sh"):
            scripts.append(name)
            continue
        with open(path, "rb") as handle:
            if names_a_shell(handle.readline()):
                scripts.append(name)
    return scripts


def main() -> int:
    root = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True,
                          check=True).stdout.strip()
    scripts = shell_scripts(root)
    if not scripts:
        print("shell-parse: found no tracked shell script, so this check read nothing.", file=sys.stderr)
        return 1
    version = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], capture_output=True, text=True,
                             check=True).stdout
    broken = 0
    for name in scripts:
        parsed = subprocess.run([BASH, "-n", name], cwd=root, capture_output=True, text=True)
        if parsed.returncode != 0:
            broken += 1
            print(f"shell-parse: {name} does not parse under {BASH} {version}:", file=sys.stderr)
            for line in parsed.stderr.splitlines():
                print(f"  {line}", file=sys.stderr)
    if broken:
        print(f"shell-parse: {broken} of {len(scripts)} tracked shell scripts do not parse.", file=sys.stderr)
        return 1
    noun = "script parses" if len(scripts) == 1 else "scripts parse"
    print(f"shell-parse: {len(scripts)} tracked shell {noun} under {BASH} {version}: {', '.join(scripts)}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
