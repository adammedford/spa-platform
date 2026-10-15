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

Two rules, and an integration branch needs the second
-----------------------------------------------------
A task branch is one owner's change, so the whole diff is checked at once. An integration branch is
several, each already single-owner, merged in order -- and judged by the whole diff it fails every
time, which is why the overnight run used to prescribe `--no-verify` as a routine step. That is the
expensive part: `--no-verify` skips the steering, roadmap, artifact and build checks too, so a
branch that only ever needed a different ownership rule pushed past four unrelated ones. A stale
roadmap rode through twice that way before a push without the flag caught it.

So when the whole diff spans owners, each commit is checked on its own, and the branch passes when
every commit is single-owner. The second rule is the stricter reading of A-05, not a softer one: one
task, one owner, per commit rather than per branch.

It applies only to a branch that merged something, which is what keeps it from excusing a task
branch that split one owner-crossing change into two commits. A task branch has no merges, so it is
judged by its whole diff, exactly as before.

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

# git's empty tree. A root commit has no parent to diff against.
EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"


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


def commits(base: str, head: str) -> list[str]:
    """Every commit base..head, oldest first."""
    merge_base = run(["git", "merge-base", base, head])
    return [line for line in run(
        ["git", "rev-list", "--reverse", f"{merge_base}..{head}"]).splitlines() if line]


def merged_anything(base: str, head: str) -> bool:
    """Whether base..head contains a merge, which is what marks an integration branch."""
    merge_base = run(["git", "merge-base", base, head])
    return bool(run(["git", "rev-list", "--merges", f"{merge_base}..{head}"]).strip())


def files_in(commit: str) -> list[str]:
    """What one commit changed, against its first parent.

    First parent, so a merge is read as what it brought in -- the lane's own files -- rather than as
    the union of both sides. A root commit has no parent and is diffed against the empty tree.
    """
    parents = run(["git", "rev-list", "--parents", "-n", "1", commit]).split()[1:]
    against = parents[0] if parents else EMPTY_TREE
    listing = run(["git", "diff", "--name-only", against, commit])
    return [line for line in listing.splitlines() if line]


def owners_of(paths: list[str], parsed: list[tuple[str, str]]) -> tuple[dict[str, list[str]], list[str]]:
    """Group paths by owner, dropping the always-allowed ones and collecting the unowned."""
    by_owner: dict[str, list[str]] = {}
    unowned: list[str] = []

    for path in paths:
        if path.startswith(ALWAYS_ALLOWED):
            continue
        owner = owner_of(path, parsed)
        if owner is None:
            unowned.append(path)
            continue
        by_owner.setdefault(owner, []).append(path)

    return by_owner, unowned


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

    by_owner, unowned = owners_of(files, parsed)

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

    # The whole diff spans owners. On a branch that merged something, that is what an integration
    # branch looks like, so read it the stricter way: every commit single-owner.
    if merged_anything(base, head):
        crossing: list[tuple[str, dict[str, list[str]]]] = []
        per_commit: list[tuple[str, str]] = []

        for commit in commits(base, head):
            owners, missing = owners_of(files_in(commit), parsed)
            if missing or len(owners) > 1:
                crossing.append((commit, owners))
            elif owners:
                per_commit.append((commit, next(iter(owners))))

        if not crossing:
            print(f"A-05: {len(files)} file(s) across {len(by_owner)} owners, and every commit is")
            print("one owner's. An integration branch, so each commit answers for itself:")
            for commit, owner in per_commit:
                subject = run(["git", "log", "--format=%s", "-n", "1", commit])
                print(f"  {commit[:9]}  {owner:<17} {subject[:60]}")
            return 0

        print("A-05 FAILED: a commit on this branch touches more than one owner's paths.")
        for commit, owners in crossing:
            subject = run(["git", "log", "--format=%s", "-n", "1", commit])
            print(f"  {commit[:9]}  {subject[:60]}")
            for owner, paths in sorted(owners.items()):
                print(f"    {owner}:")
                for path in sorted(paths):
                    print(f"      {path}")
        print()
        print("Split that commit, or push with --no-verify if you mean it.")
        return 1

    print("A-05 FAILED: this change touches more than one owner's paths.")
    for owner, paths in sorted(by_owner.items()):
        print(f"  {owner}:")
        for path in sorted(paths):
            print(f"    {path}")
    print()
    print("One owner per task branch. Split it, or push with --no-verify if you mean it.")
    print("A branch that merges single-owner lanes is read per commit instead; this one merged")
    print("nothing, so it is judged whole.")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
