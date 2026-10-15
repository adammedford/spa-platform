#!/usr/bin/env python3
"""A Terraform plan may not leave a CI job. Owner: integrator.

A plan's `after` holds the values the apply will write. For two of this platform's roots that is
credentials: the identity root's plan carries every generated test-user password (task 1.1) and the
apps root's carries the OAuth client secrets (task 1.8). `-refresh=false` does not change it -- the
values are in the plan because the plan is going to write them.

Change request 1.1 asked for two things. The first was `*.tfplan` and `*.tfplan.json` in
`.gitignore`, which closes the `git add` path. This is the second: the plans reach the policy
checker **inside the job** and never leave it. Two ways out of a job exist, and both are checked
here:

- **A workflow artifact.** An uploaded plan is a credential sitting in the Actions retention store,
  readable by anyone who can read the run, long after the job is gone.
- **A file in the workspace.** A plan written beside the checkout is one `upload-artifact: .` or one
  careless `git add` away from leaving, and the ignore patterns only cover the names they match.

So a plan file written in CI must live under the runner's temp directory, which is outside the
workspace and discarded with the runner.

`.github/steering.py` makes the argument for checking rather than asking: "A paragraph asking for
the rule is not the same as a check enforcing it." The requirement was prose in a change request;
this is the check. It starts green -- no workflow uploads an artifact or writes a plan today -- so
its whole job is to fail the day task 1.9 wires the policy checker the convenient way.

Run: python3 -I .github/plan-secrecy.py
"""

from __future__ import annotations

import pathlib
import re
import sys

WORKFLOWS = ".github/workflows"

# Scripts that handle a plan and so are checked alongside the workflows. Each is named before it
# exists (task 1.9 writes the first), and absence is not a failure: the check covers it the day it
# lands, which is the day it could get this wrong.
SCRIPTS = ["tools/scripts/plan-policy.sh"]

# An uploaded artifact is the main way a plan escapes, so every upload is refused unless it is
# listed here with the reason its path cannot name a plan. The list is deliberately empty: nothing
# in this repository uploads an artifact yet. Adding an entry is a decision, not a formality.
ALLOWED_UPLOADS: dict[str, str] = {}

# A plan file must be written here and nowhere else: outside the workspace, gone with the runner.
TEMP_PREFIXES = ("$RUNNER_TEMP", "${RUNNER_TEMP}", "${{ runner.temp }}", '"$RUNNER_TEMP', "$TMPDIR")

UPLOAD = re.compile(r"uses:\s*actions/upload-artifact")
# `-out=X` and `-out X`, the two spellings Terraform accepts.
OUT = re.compile(r"-out[=\s]+(\S+)")
# A redirect of plan JSON into a file, as opposed to a pipe into the checker.
SHOW_JSON_REDIRECT = re.compile(r"show\s+-json\b[^|>]*>\s*(\S+)")


def targets(root: pathlib.Path) -> list[pathlib.Path]:
    found = sorted((root / WORKFLOWS).glob("*.yml")) + sorted((root / WORKFLOWS).glob("*.yaml"))
    return found + [root / s for s in SCRIPTS if (root / s).exists()]


def in_temp(value: str) -> bool:
    return value.strip("'\"").startswith(TEMP_PREFIXES) or value.startswith(TEMP_PREFIXES)


def check(root: pathlib.Path) -> list[str]:
    problems: list[str] = []
    for path in targets(root):
        rel = path.relative_to(root).as_posix()
        for n, line in enumerate(path.read_text().splitlines(), 1):
            # A commented line cannot run, in YAML or in shell, so it is prose -- including the
            # worked example in ci.yml's own plan-policy step, which spells the forbidden forms
            # out in order to forbid them.
            if line.lstrip().startswith("#"):
                continue
            if UPLOAD.search(line) and rel not in ALLOWED_UPLOADS:
                problems.append(
                    f"{rel}:{n}: uploads a workflow artifact. An uploaded plan is a credential in "
                    f"the Actions retention store. Pipe the plan to the checker instead, or add "
                    f"{rel!r} to ALLOWED_UPLOADS in this script with the reason its path cannot "
                    f"name a plan."
                )
            for match in OUT.finditer(line):
                value = match.group(1)
                if not in_temp(value):
                    problems.append(
                        f"{rel}:{n}: writes a plan to {value!r}, inside the workspace. A plan's "
                        f"`after` holds the test-user passwords and the client secrets. Write it "
                        f"under $RUNNER_TEMP, which is outside the checkout and discarded with the "
                        f"runner."
                    )
            for match in SHOW_JSON_REDIRECT.finditer(line):
                value = match.group(1)
                if not in_temp(value):
                    problems.append(
                        f"{rel}:{n}: redirects plan JSON to {value!r}, inside the workspace. The "
                        f"JSON carries the same values as the plan. Pipe it to the checker, or "
                        f"write it under $RUNNER_TEMP."
                    )
    return problems


def main() -> int:
    root = pathlib.Path(__file__).resolve().parent.parent
    problems = check(root)

    for line in problems:
        print(f"  {line}")
    if problems:
        print()
        print("PLAN SECRECY FAILED: a Terraform plan can leave a CI job.")
        print("The identity root's plan carries every generated test-user password and the apps")
        print("root's carries the OAuth client secrets (change request 1.1). The plans reach the")
        print("policy checker inside the job and never leave it.")
        return 1

    scanned = ", ".join(p.relative_to(root).as_posix() for p in targets(root))
    print(f"plan secrecy: no plan can leave a job ({scanned}).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
