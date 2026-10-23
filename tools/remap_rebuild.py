#!/usr/bin/env python3
"""Carries an overnight run's record over a rebuild of its integration branch (#318).

Owner: integrator. Run by whichever session rebuilds `overnight/<date>`, from inside the repository,
once the branch points at the rebuilt tip:

    python3 -I tools/remap_rebuild.py --old <old tip> --new <new tip> --issue <Overnight issue> \\
        --worktree-root <the run's worktree root> --session "<who rebuilt it, and why>" [--apply]

**Why it exists.** A rebuild (docs/agents/landing-work.md, "A task merge's message is changed by
rebuilding the run branch") recreates every commit after the run's base with its tree and a new
sha. On 2026-10-22 the session repairing PR #287's review findings rebuilt the branch, and nothing
followed it: the Overnight issue (#286) still cited the old merge shas, every lane worktree's tip
fell out of the branch's ancestry, the morning's Verify step 3 (.claude/workflows/morning.js) stopped
on that, and the old-to-new map was rebuilt by hand in git.

**What it does**, in this order, and nothing is written until every read has succeeded:

1. **The map.** Every commit the old tip reaches and the new tip does not, the lane commits reached
   through second parents included, is paired with a commit the new tip reaches and the old tip
   does not that has the same tree. A tree alone can be ambiguous, because a `--no-ff` merge of a
   lane forked from the branch's own tip has that lane commit's tree; then the candidate whose
   parents are the old commit's parents carried through the map is taken, the rewrite the guide
   prescribes. An old commit with no partner, or with more than one, is a refusal: the record is
   not rewritten on a guess. Commits are taken parents first, so a parent is mapped before its
   children are matched.
2. **The body.** A copy of the Overnight issue's body has every sha-like token that abbreviates a
   replaced commit rewritten to the rebuilt commit's sha, abbreviated to at least the same length
   (`git rev-parse --short`), and gains one line, after the `Run:` paragraph, naming the rebuild,
   both tips and the rebuilding session. A line already beginning `Rebuilt ` is a record of a
   rebuild, this one's or an earlier one's, and keeps the shas it names; a body that already
   names this rebuild's new tip on such a line gets no second one, so a run repeated after a
   failure does not stack them. The diff is printed.
3. **The worktrees.** Each worktree under the root whose tip the rebuild replaced is moved to the
   rebuilt commit with `git reset --keep`, branch and all, when `git status --porcelain` prints
   nothing; `--keep` aborts rather than overwrite a change made since that read
   (https://git-scm.com/docs/git-reset). A dirty one is named and left, for its owner to settle. A tip already reached from the
   new tip needs nothing, and a tip outside the old graph, a failed lane's, is named and left: the
   morning expects it (morning.js, Verify step 3).
4. **The comment.** One comment on the Overnight issue with both tips, the session and the map.

Without `--apply` it is a dry run: it prints the map, the diff, the worktree plan and the comment,
and writes nothing, on GitHub or on disk. The last line is the verdict, `remap_rebuild: applied:`,
`remap_rebuild: dry run:`, `remap_rebuild: refused:` (exit 1) or `remap_rebuild: stopped:` (exit 2,
input it could not read, or a write that failed partway, which it names).

gh is called as its manual gives the three calls, from the repository so it resolves this one:
https://cli.github.com/manual/gh_issue_view, https://cli.github.com/manual/gh_issue_edit and
https://cli.github.com/manual/gh_issue_comment.

Test companion: tools/remap_rebuild_tests.py. Kept 3.9-compatible (docs/dependencies.md, D-3).
"""

from __future__ import annotations

import argparse
import datetime
import difflib
import pathlib
import re
import subprocess
import sys
import tempfile

PREFIX = "remap_rebuild"
# A sha as a person writes one in a record: seven hex digits or more, standing alone.
SHA_TOKEN = re.compile(r"(?<![0-9A-Za-z])[0-9a-f]{7,40}(?![0-9A-Za-z])")
REBUILD_LINE = "Rebuilt "
GH_TIMEOUT = 60


class Refused(Exception):
    """The input is readable and says the record must not be rewritten."""


class Stopped(Exception):
    """Input that could not be read, or a write that failed."""


def git(repo: pathlib.Path, *args: str) -> str:
    done = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True,
                          check=False)
    if done.returncode != 0:
        raise Stopped(f"git {' '.join(args)}: {done.stderr.strip() or 'exit ' + str(done.returncode)}")
    return done.stdout


def is_ancestor(repo: pathlib.Path, commit: str, tip: str) -> bool:
    """Whether `tip` reaches `commit`. git answers 0 for yes and 1 for no, and signals an error with
    any other non-zero status (https://git-scm.com/docs/git-merge-base, --is-ancestor), so an error
    stops rather than reading as "no": a worktree it could not judge is not one to leave unnamed."""
    done = subprocess.run(["git", "-C", str(repo), "merge-base", "--is-ancestor", commit, tip],
                          capture_output=True, text=True, check=False)
    if done.returncode in (0, 1):
        return done.returncode == 0
    raise Stopped(f"git merge-base --is-ancestor {commit} {tip}: "
                  f"{done.stderr.strip() or 'exit ' + str(done.returncode)}")


def gh(repo: pathlib.Path, *args: str) -> str:
    try:
        done = subprocess.run(["gh", *args], cwd=repo, capture_output=True, text=True, check=False,
                              timeout=GH_TIMEOUT)
    except FileNotFoundError:
        raise Stopped("gh is not on PATH")
    except subprocess.TimeoutExpired:
        raise Stopped(f"gh {' '.join(args[:2])} did not answer within {GH_TIMEOUT} seconds")
    if done.returncode != 0:
        raise Stopped(f"gh {' '.join(args[:3])}: {done.stderr.strip() or 'exit ' + str(done.returncode)}")
    return done.stdout


def commit(repo: pathlib.Path, rev: str) -> str:
    try:
        return git(repo, "rev-parse", "--verify", "--quiet", f"{rev}^{{commit}}").strip()
    except Stopped:
        raise Stopped(f"{rev} is not a commit git can read here")


def graph(repo: pathlib.Path, tip: str, other: str) -> list[tuple[str, str, list[str]]]:
    """(sha, tree, parents) of every commit `tip` reaches and `other` does not, parents first."""
    out = git(repo, "log", "--topo-order", "--reverse", "--format=%H %T %P", tip, "--not", other)
    rows = []
    for line in out.splitlines():
        sha, tree, *parents = line.split()
        rows.append((sha, tree, parents))
    return rows


def build_map(old_rows, new_rows) -> dict[str, str]:
    """Pairs each old commit with its rebuilt one; see the module's docstring, step 1."""
    by_tree: dict[str, list[tuple[str, list[str]]]] = {}
    for sha, tree, parents in new_rows:
        by_tree.setdefault(tree, []).append((sha, parents))
    mapping: dict[str, str] = {}
    used: set[str] = set()
    for sha, tree, parents in old_rows:
        candidates = [c for c in by_tree.get(tree, []) if c[0] not in used]
        if len(candidates) > 1:
            carried = [mapping.get(p, p) for p in parents]
            candidates = [c for c in candidates if c[1] == carried]
        if not candidates:
            raise Refused(f"{sha} has no tree-equal partner in the rebuilt graph")
        if len(candidates) > 1:
            raise Refused(f"{sha} has {len(candidates)} tree-equal partners with the same parents: "
                          + ", ".join(c[0] for c in candidates))
        mapping[sha] = candidates[0][0]
        used.add(candidates[0][0])
    return mapping


def rewrite_body(repo, body: str, mapping: dict[str, str], line: str, new: str) -> str:
    """The body with every abbreviation of a replaced commit rewritten, and the rebuild line."""
    olds = list(mapping)

    def swap(found: re.Match) -> str:
        token = found.group(0)
        hits = [sha for sha in olds if sha.startswith(token)]
        if not hits:
            return token
        if len(hits) > 1:
            raise Refused(f"`{token}` in the body abbreviates {len(hits)} replaced commits")
        rebuilt = mapping[hits[0]]
        if len(token) == 40:
            return rebuilt
        return git(repo, "rev-parse", f"--short={len(token)}", rebuilt).strip()

    lines = body.split("\n")
    rewritten = [text if text.startswith(REBUILD_LINE) else SHA_TOKEN.sub(swap, text)
                 for text in lines]
    if any(text.startswith(REBUILD_LINE) and new in text for text in rewritten):
        return "\n".join(rewritten)
    at = next((i for i, text in enumerate(rewritten) if text.startswith("Run:")), None)
    if at is None:
        rewritten += ["", line]
    else:
        # After the whole `Run:` paragraph, so the line is a paragraph of its own.
        end = at
        while end + 1 < len(rewritten) and rewritten[end + 1].strip():
            end += 1
        rewritten[end + 1:end + 1] = ["", line]
    return "\n".join(rewritten)


def worktrees(repo: pathlib.Path, root: pathlib.Path) -> list[tuple[pathlib.Path, str]]:
    """(path, HEAD) of each worktree under `root`, from `git worktree list --porcelain`."""
    found = []
    path = None
    for text in git(repo, "worktree", "list", "--porcelain").splitlines():
        if text.startswith("worktree "):
            path = pathlib.Path(text[len("worktree "):])
        elif text.startswith("HEAD ") and path is not None:
            resolved = path.resolve()
            if resolved != root and root in resolved.parents:
                found.append((path, text[len("HEAD "):]))
    return found


def plan_worktrees(repo, root, mapping, new):
    """What step 3 does to each worktree: ("move", path, old, new), ("dirty", path, old, new),
    ("current", path, head, "") or ("outside", path, head, "")."""
    plan = []
    for path, head in worktrees(repo, root):
        if head in mapping:
            status = git(path, "status", "--porcelain")
            plan.append(("dirty" if status.strip() else "move", path, head, mapping[head]))
        elif is_ancestor(repo, head, new):
            plan.append(("current", path, head, ""))
        else:
            plan.append(("outside", path, head, ""))
    return plan


def comment_text(old, new, session, mapping, subjects, plan) -> str:
    rows = [f"| `{o}` | `{n}` | {subjects.get(n, '')} |" for o, n in mapping.items()]
    moved = [f"- `{p}`: `{o[:12]}` to `{n[:12]}`" for kind, p, o, n in plan if kind == "move"]
    left = [f"- `{p}`: dirty, left at `{o[:12]}`" for kind, p, o, n in plan if kind == "dirty"]
    return "\n".join([
        "## Rebuild of the integration branch",
        "",
        f"Rebuilt by {session}. The old tip was `{old}` and the new tip is `{new}`; every commit "
        "below was recreated with its tree, and this issue's body now cites the rebuilt shas "
        "(`tools/remap_rebuild.py`, #318).",
        "",
        "| Old | Rebuilt | Subject |",
        "| --- | --- | --- |",
        *rows,
        "",
        "Lane worktrees moved to their rebuilt tips:" if moved else "No lane worktree moved.",
        *moved,
        *(["", "Left where they were, dirty:", *left] if left else []),
        "",
    ])


def post(repo, issue, verb, text):
    with tempfile.TemporaryDirectory(prefix="remap-rebuild-") as scratch:
        path = pathlib.Path(scratch) / "body.md"
        path.write_text(text)
        gh(repo, "issue", verb, issue, "--body-file", str(path))


def run(args) -> int:
    repo = pathlib.Path(git(pathlib.Path.cwd(), "rev-parse", "--show-toplevel").strip())
    root = pathlib.Path(args.worktree_root).resolve()
    if not root.is_dir():
        raise Stopped(f"the worktree root {args.worktree_root} is not a directory")
    if "\n" in args.session or not args.session.strip():
        raise Stopped("--session takes one line naming the session that rebuilt the branch")
    old = commit(repo, args.old)
    new = commit(repo, args.new)
    old_rows = graph(repo, old, new)
    if not old_rows:
        raise Refused(f"{new} already reaches {old}, so nothing was rebuilt; are the tips swapped?")
    mapping = build_map(old_rows, graph(repo, new, old))
    for o, n in mapping.items():
        print(f"map {o} {n}")

    body = gh(repo, "issue", "view", args.issue, "--json", "body", "--jq", ".body")
    body = body[:-1] if body.endswith("\n") else body
    date = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
    line = (f"Rebuilt {date} by {args.session}: the integration branch went from `{old}` to "
            f"`{new}`, every commit recreated with its tree. The shas in this body are the rebuilt "
            "ones; the map is in this issue's comment of the same date (`tools/remap_rebuild.py`).")
    rewritten = rewrite_body(repo, body, mapping, line, new)
    diff = list(difflib.unified_diff(body.split("\n"), rewritten.split("\n"), "body", "rewritten",
                                     lineterm=""))
    print("\n".join(diff) if diff else "the body needs no change")

    plan = plan_worktrees(repo, root, mapping, new)
    subjects = dict(line.split(" ", 1) for line in
                    git(repo, "log", "--format=%H %s", new, "--not", old).splitlines())
    comment = comment_text(old, new, args.session, mapping, subjects, plan)
    moves = [step for step in plan if step[0] == "move"]
    left = [step for step in plan if step[0] in ("dirty", "outside")]
    for kind, path, head, _ in left:
        print(f"{'dirty' if kind == 'dirty' else 'not in the old graph'}: {path} at {head[:12]}, left")

    if not args.apply:
        for _, path, head, target in moves:
            print(f"would move: {path} from {head[:12]} to {target[:12]}")
        print(comment)
        print(f"{PREFIX}: dry run: {len(mapping)} commits mapped, {len(moves)} worktrees to move, "
              f"{len(left)} left, the body {'changes' if diff else 'is unchanged'}; nothing "
              "written. Run it again with --apply.")
        return 0

    done = []
    try:
        for _, path, head, target in moves:
            git(path, "reset", "-q", "--keep", target)
            if not is_ancestor(repo, target, new):
                raise Stopped(f"{path} moved to {target}, which {new} does not reach")
            print(f"moved: {path} from {head[:12]} to {target[:12]}")
            done.append(f"moved {path}")
        if diff:
            post(repo, args.issue, "edit", rewritten + "\n")
            done.append("edited the body")
        post(repo, args.issue, "comment", comment)
    except Stopped as error:
        raise Stopped(f"{error}; already done: {', '.join(done) or 'nothing'}")
    print(f"{PREFIX}: applied: {len(mapping)} commits mapped, {len(moves)} worktrees moved, "
          f"{len(left)} left, the body {'rewritten' if diff else 'unchanged'}, and the map is "
          f"commented on #{args.issue}.")
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--old", required=True, help="the integration branch's tip before the rebuild")
    parser.add_argument("--new", required=True, help="its tip after the rebuild")
    parser.add_argument("--issue", required=True, help="the run's Overnight issue number")
    parser.add_argument("--worktree-root", required=True, help="the run's worktree root")
    parser.add_argument("--session", required=True, help="the rebuilding session, in one line")
    parser.add_argument("--apply", action="store_true", help="write; without it, a dry run")
    args = parser.parse_args(argv)
    if not args.issue.isdigit():
        print(f"{PREFIX}: stopped: --issue takes a number, not {args.issue!r}")
        return 2
    try:
        return run(args)
    except Refused as error:
        print(f"{PREFIX}: refused: {error}; nothing was written")
        return 1
    except Stopped as error:
        print(f"{PREFIX}: stopped: {error}")
        return 2


if __name__ == "__main__":
    sys.exit(main())
