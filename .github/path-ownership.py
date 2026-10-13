"""Check A-05: a change that touches a path outside its owner's set fails.

OWNERSHIP.md is the source of truth. Every path has exactly one owner and the last matching line
wins, which is git's own rule. The owners there are role names, not GitHub handles: this repository
has one author, so nothing resolves them and nothing needs to.

This runs in `.githooks/pre-push`, which is where it is worth running: it catches an agent session
that reached outside its brief at the moment it reached, when the fix is one amended commit. It is
not a CI job. A second catch after the push adds nothing when the same person reviews the pull
request, and the declaration mechanism a blocking CI check would need -- a trailer naming a role,
with nobody to verify it against -- is a permissions model for a team of one.

So there is no escape hatch, because none is needed. When this fails, look at what it names and
either split the change or, if you mean it, `git push --no-verify`.

Two paths are always allowed
----------------------------
Anyone may add a file under `contracts/changes/`. That is how a change to an integrator path is
requested, and a workstream that could not write there would have no way to ask.

Usage: python3 -I .github/path-ownership.py <base-ref> <head-ref>
"""

from __future__ import annotations

import fnmatch
import subprocess
import sys
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
OWNERSHIP = REPOSITORY_ROOT / "OWNERSHIP.md"

# Anyone may add a change request. OWNERSHIP.md says so, and a workstream with no way to ask for a
# contract change would have no way to work.
ALWAYS_ALLOWED = ("contracts/changes/",)


def rules() -> list[tuple[str, str]]:
    """Every (pattern, owner) in OWNERSHIP.md's fenced block, in file order. Later lines win."""
    parsed: list[tuple[str, str]] = []
    inside = False

    for line in OWNERSHIP.read_text(encoding="utf-8").splitlines():
        if line.startswith("```"):
            # The prose around the block explains it; only the block is data.
            if inside:
                break
            inside = True
            continue

        if not inside:
            continue

        without_comment = line.split("#", 1)[0].strip()
        if not without_comment:
            continue

        fields = without_comment.split()
        if len(fields) < 2:
            continue

        parsed.append((fields[0], fields[1]))

    if not parsed:
        raise SystemExit("A-05: OWNERSHIP.md has no rules. Is its fenced block intact?")

    return parsed


def owner_of(path: str, parsed: list[tuple[str, str]]) -> str | None:
    """The owner of one path: the last OWNERSHIP.md line that matches it."""
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
        # OWNERSHIP.md starts with a catch-all, so this means the catch-all is gone.
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

    print("A-05 FAILED: this change touches more than one owner's paths.")
    for owner, paths in sorted(by_owner.items()):
        print(f"  {owner}:")
        for path in sorted(paths):
            print(f"    {path}")
    print()
    print("Split it, or push with --no-verify if you mean it.")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
