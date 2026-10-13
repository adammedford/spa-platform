"""Check A-05: a change that touches a path outside its owner's set fails CI.

CODEOWNERS is the source of truth. Every path has exactly one owner and the last matching line
wins, which is git's own rule.

How the owner of a change is decided
------------------------------------
The owner is inferred from the paths the change touches. If they all resolve to one owner, that is
the owner and the change is inside its set. If they resolve to more than one, the change is outside
somebody's set and fails -- unless it declares an owner, which only the integrator may do.

The declaration is needed because paths alone cannot name the actor. "The integrator seeding a stub
in a workstream's project" and "a workstream reaching into an integrator path" touch the same set of
paths, and the plan allows the first and forbids the second. A declaration is a commit trailer in
the range under test:

    Path-Ownership: integrator (task I-4 seeds the stub entry points)

It is deliberately awkward and it is logged. Only `integrator` is accepted, because the integrator is
the one role the plan lets edit anywhere: "the integrator decides and edits".

Two paths are always allowed
----------------------------
Anyone may add a file under `contracts/changes/`. That is how a change to an integrator path is
requested, and a workstream that could not write there would have no way to ask.

Usage: python3 -I .github/path-ownership.py <base-ref> <head-ref>
"""

from __future__ import annotations

import fnmatch
import re
import subprocess
import sys
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
CODEOWNERS = REPOSITORY_ROOT / "CODEOWNERS"

# Anyone may add a change request. CODEOWNERS says so, and a workstream with no way to ask for a
# contract change would have no way to work.
ALWAYS_ALLOWED = ("contracts/changes/",)

# The only role that may declare itself the owner of a change spanning several owners' paths.
DECLARABLE_OWNER = "integrator"

# The reason runs to the end of the line, and on over any following lines that are indented, which
# is how a git trailer continues. Without that it stopped at the first newline and a wrapped reason
# reached the log truncated mid-sentence -- and the reason is the whole audit record, so a
# truncated one defeats the point of asking for it. A continuation line has to be indented; an
# unindented second line is a new paragraph as far as git is concerned, and guessing otherwise
# would swallow the rest of the message.
DECLARATION = re.compile(
    r"^Path-Ownership:[ \t]*(?P<owner>[\w-]+)[ \t]*(?P<reason>.*(?:\n[ \t]+\S.*)*)$",
    re.MULTILINE,
)


def rules() -> list[tuple[str, str]]:
    """Every (pattern, owner) in CODEOWNERS, in file order. Later lines win."""
    parsed: list[tuple[str, str]] = []

    for line in CODEOWNERS.read_text(encoding="utf-8").splitlines():
        without_comment = line.split("#", 1)[0].strip()
        if not without_comment:
            continue

        fields = without_comment.split()
        if len(fields) < 2:
            continue

        pattern, owners = fields[0], fields[1:]
        # One owner per path is this repository's rule, so the first handle names the role.
        parsed.append((pattern, owners[0].rsplit("/", 1)[-1]))

    return parsed


def owner_of(path: str, parsed: list[tuple[str, str]]) -> str | None:
    """The owner of one path: the last CODEOWNERS line that matches it."""
    found = None

    for pattern, owner in parsed:
        if matches(pattern, path):
            found = owner

    return found


def matches(pattern: str, path: str) -> bool:
    if pattern == "*":
        return True

    anchored = pattern.lstrip("/")

    if anchored.endswith("/"):
        return path.startswith(anchored)

    return path == anchored or fnmatch.fnmatch(path, anchored)


def changed_files(base: str, head: str) -> list[str]:
    merge_base = run(["git", "merge-base", base, head])
    listing = run(["git", "diff", "--name-only", f"{merge_base}..{head}"])
    return [line for line in listing.splitlines() if line]


def declaration(base: str, head: str) -> tuple[str, str] | None:
    merge_base = run(["git", "merge-base", base, head])
    messages = run(["git", "log", "--format=%B", f"{merge_base}..{head}"])

    found = DECLARATION.search(messages)
    if found is None:
        return None

    # Collapse the continuation lines' indentation into single spaces, so the reason logs as one line.
    return found.group("owner"), " ".join(found.group("reason").split())


def run(command: list[str]) -> str:
    completed = subprocess.run(
        command, cwd=REPOSITORY_ROOT, capture_output=True, text=True, check=True
    )
    return completed.stdout.strip()


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    base, head = sys.argv[1], sys.argv[2]
    parsed = rules()

    files = changed_files(base, head)
    if not files:
        print("A-05: nothing changed.")
        return 0

    by_owner: dict[str, list[str]] = {}
    unowned: list[str] = []

    for path in files:
        if path.startswith(ALWAYS_ALLOWED):
            continue

        owner = owner_of(path, parsed)
        if owner is None:
            unowned.append(path)
            continue

        by_owner.setdefault(owner, []).append(path)

    if unowned:
        # CODEOWNERS ends with a catch-all, so this means the catch-all is gone.
        print("A-05 FAILED: no owner for:")
        for path in unowned:
            print(f"  {path}")
        return 1

    if not by_owner:
        print(f"A-05: {len(files)} file(s), all under {', '.join(ALWAYS_ALLOWED)}. Anyone may add one.")
        return 0

    if len(by_owner) == 1:
        owner = next(iter(by_owner))
        print(f"A-05: {len(files)} file(s), all owned by {owner}.")
        return 0

    declared = declaration(base, head)

    print("A-05: this change touches more than one owner's paths.")
    for owner, paths in sorted(by_owner.items()):
        print(f"  {owner}:")
        for path in sorted(paths):
            print(f"    {path}")

    if declared is None:
        print()
        print("A-05 FAILED: a change may touch one owner's paths only.")
        print("Split it, or, if you are the integrator, declare it in a commit message:")
        print(f"  Path-Ownership: {DECLARABLE_OWNER} (why this change spans owners)")
        return 1

    owner, reason = declared
    if owner != DECLARABLE_OWNER:
        print()
        print(f"A-05 FAILED: '{owner}' may not declare itself the owner of a change spanning owners.")
        print(f"Only {DECLARABLE_OWNER} may, because only the integrator edits every path.")
        return 1

    if not reason:
        print()
        print("A-05 FAILED: the declaration needs a reason.")
        print(f"  Path-Ownership: {DECLARABLE_OWNER} (why this change spans owners)")
        return 1

    print()
    print(f"A-05: allowed. Declared owner {owner}: {reason}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
