#!/usr/bin/env python3
"""Deletes the branches of finished work, locally and on origin, and says why it keeps the rest.
Owner: integrator.

    tools/prune_branches.py                      say what --delete would do, and delete nothing
    tools/prune_branches.py --delete             delete the finished branches locally and on origin
    tools/prune_branches.py --delete --verbose   and list each kept branch, not only a count
    tools/prune_branches.py --delete --only <branch>...   delete the named branches alone

docs/agents/landing-work.md makes a task's branch its claim and says the claim is deleted when the
task is done, and nothing deleted it (#71). On 17 October 2026 this repository held 44 `task/*`
branches, 19 of them not ancestors of main; five tickets that night needed dated branch names
because older branches of the same names sat at stale heads; and `overnight/2026-10-16` looked
unmerged though every commit on it was on main in equivalent form.

tools/land.sh runs --delete after every merge into main. Run by hand from a checkout at origin/main,
it is the one-off pass over what is left, by the same rule. It reads `task/*` and `overnight/*`
branches and no others, and deletes a copy only when all three of these hold:

  1. Its work is finished, by its kind's rule.
       task/<id>-<slug>   every id the name carries reads as landed in `tools/roadmap.py --json`:
                          a plan row in "rows", a split of one (2.1b) in "splits", a ticket
                          (issue-66) in "tickets". A split no merge subject names reads as its row,
                          because a row that landed whole landed its splits with it: 3.0 did, and
                          3.0a and 3.0b were never named. A part-landed, open or ongoing id keeps the
                          branch, because the claim is how the next session knows the row is taken.
                          An id that is none of the three, or no id, claims nothing a session could
                          take -- ADR-0020 made every ticket a GitHub issue, so a live claim names a
                          row, a split or issue-<n>, and a ticket-<n> name is the archived tracker's
                          -- so rules 2 and 3 alone decide such a branch.
       overnight/<date>   a pull request from it into main has merged, as gh reports.
  2. No worktree has it checked out. The cleanup never removes a worktree.
  3. Its current tip's work is on origin/main: the tip is an ancestor, or every commit on it that
     main lacks has a patch-equivalent on main and every merge among them is the merge git makes
     unaided, so no resolution change exists only on the branch. Anything else -- unmerged
     commits, a merge with changes of its own, a tip git cannot compare -- keeps the branch,
     however many earlier pull requests from it merged.

Each copy is judged by its own tip: the local branch, and the branch on origin as `git ls-remote`
reads it. Origin's copy is deleted by a push whose lease names the tip judged, so a push made to it
since keeps it. A local copy is deleted only while its tip is still the one judged, and when git
refuses to delete it, origin's copy is kept with it.

Every copy deleted gets a restore line, the git command that recreates it at the tip it had. The
lines are printed at the end, and each is also added, the moment its copy is gone, to
pruned-branches/<UTC date>-<12-hex HEAD sha>.txt in the repository's common git directory, which
the last line printed names (#115). On 18 October 2026 the first landing to run this deleted 34
branches, its session read the landing through `tail -20`, and 14 of the lines went with the
terminal. The common directory is the one every worktree shares, so the file outlives the worktree
a landing ran in. A dry run, or a run that deletes nothing, writes no file, and a later run at the
same HEAD on the same day adds its lines after the earlier run's. A run that cannot write the file
deletes nothing more once it finds out, still prints every line it has, and exits 1. A line works
while this repository still has the commit it names; a tip main does not reach, one whose work
landed as patch-equivalents, has no other copy here once git's garbage collection prunes it
(https://git-scm.com/docs/git-gc).

Rule 3 uses `git merge-tree --write-tree`, which Git 2.38 introduced, so the floor
docs/dependencies.md gives git is 2.38.

A dry run lists every copy with its verdict and reason. A --delete run lists each copy it deletes
or fails to delete, and counts the copies it keeps under a short reason each -- the claim of an
unfinished row, a worktree, work main lacks -- commonest first, for land.sh runs it after every
merge and most branches are kept for the same few reasons every time: the landing of #507 printed
48 kept lines and deleted nothing (#524). --verbose lists each kept copy with its full reason, as a
dry run does.

--delete --only <branch>... deletes the named branches alone, each as --delete would, with its
restore line, and counts every other copy as kept because it is not named. A named branch the
listing keeps, or does not list, refuses the whole run with the listing's reason, and nothing is
deleted (#574): on 29 October 2026 a session's `--delete`, whose command line said nothing of which
branches would go, was refused in auto mode as a deletion of unverifiable scope, and the session
fell back to `git branch -d`, which writes no restore line. --only without --delete is an argument
error: a dry run already lists every branch with its verdict.

HEAD must be origin/main or behind it: the roadmap reads HEAD's merge subjects as well as main's, and
one only HEAD has would call a row landed that main has not landed. It exits 0 once every branch is
judged, whatever it kept; 1, deleting nothing, when it cannot judge or when --only names a branch
the listing keeps or lacks, and 1 when it cannot write the file of restore lines, deleting nothing
after that; 2 for an unknown argument, or --only without --delete.
"""

from __future__ import annotations

import argparse
import datetime
import json
import pathlib
import re
import subprocess
import sys
from typing import NamedTuple

MAIN = "origin/main"
# How the last line starts when it names the file of restore lines. tools/land.sh reads a line by
# these words to name the file in its own last line, so the two change together.
RESTORE_FILE_LINE = "prune_branches: these commands are also in "

# The ids a claim branch's name carries, in the forms the task tables and the tracker give them:
# task/2.1-registry, task/i-3-abstractions, task/1.4a-cell, task/issue-66-land-tests, and two rows
# claimed at once, task/1.2-and-1.5-network-and-bucket. A split letter is read on every row form,
# P2-1a too, though the roadmap reports no such split: unreported, it reads as its row, P2-1. A
# ticket id is read in any case, as tools/roadmap.py reads it.
ID = r"(?:[Ii]-\d+[a-z]?|[Pp]2-\d+[a-z]?|\d+\.\d+[a-z]?|(?i:issue)-\d+)"
TASK = re.compile(rf"^task/({ID}(?:-and-{ID})*)(?:-|$)")
# The integration branch an overnight run builds and lands as one pull request.
OVERNIGHT = re.compile(r"^overnight/\d{4}-\d{2}-\d{2}$")


class Copy(NamedTuple):
    branch: str
    where: str  # "local", or "origin" for the branch on origin
    tip: str


class Verdict(NamedTuple):
    copy: Copy
    delete: bool
    reason: str
    # A kept copy's reason in the words a --delete run counts it under: the reason without what
    # differs between branches, such as the id, the worktree's path or the number of commits.
    group: str = ""


class Judged(NamedTuple):
    """One answer about a branch: whether it passes, the reason either way, and the reason's group
    when it does not."""
    ok: bool
    reason: str
    group: str = ""


class Refused(Exception):
    """The cleanup cannot decide anything here, and says why."""


def git(*args: str, check: bool = True) -> subprocess.CompletedProcess:
    done = subprocess.run(["git", *args], capture_output=True, text=True, check=False)
    if check and done.returncode != 0:
        raise Refused(f"git {' '.join(args)} failed: {done.stderr.strip()}")
    return done


# --- what the roadmap says is finished ---------------------------------------------------------


def roadmap_states(root: str) -> dict:
    """tools/roadmap.py --json, the one derivation of how far the work has got (#70)."""
    done = subprocess.run([sys.executable, "-I", f"{root}/tools/roadmap.py", "--json"],
                          capture_output=True, text=True, check=False)
    if done.returncode != 0:
        raise Refused(f"tools/roadmap.py --json gave no state: {done.stderr.strip()}")
    states = json.loads(done.stdout)
    missing = [key for key in ("rows", "splits", "tickets") if key not in states]
    if missing:
        raise Refused(
            f"tools/roadmap.py --json reports no {' or '.join(missing)}, so it predates #71 and "
            f"cannot say which splits and tickets are finished"
        )
    return states


def normal(id_: str) -> str:
    """An id as the roadmap writes it: I-3 and P2-1 with a capital, 1.4a as written, and issue-66 in
    lower case, as tools/roadmap.py reads a ticket id written in any case."""
    if id_.lower().startswith("issue-"):
        return id_.lower()
    return id_ if id_[0].isdigit() else id_[0].upper() + id_[1:]


def id_state(id_: str, states: dict) -> tuple[str, str]:
    """(state, how the reason names the id): landed, part-landed, open or ongoing, or unknown for an
    id the plan and the tracker lack.

    A split a merge subject names reads by its own subjects, so 2.1b's "part of" keeps its claim
    under a row that landed whole. A split no subject names reads as its row: a row that landed
    whole landed its splits with it, and a fresh follow-up claim that has commits main lacks, or is
    checked out in a worktree, is kept by rules 3 and 2 whatever its id reads as.
    """
    if id_ in states["rows"]:
        return states["rows"][id_], id_
    if id_.startswith("issue-"):
        return states["tickets"].get(id_, "open"), id_
    row = id_[:-1]
    if id_[-1].isalpha() and row in states["rows"]:
        if id_ in states["splits"]:
            return states["splits"][id_], id_
        return states["rows"][row], f"{id_} (as its row {row})"
    return "unknown", id_


# The group of an overnight branch GitHub could not be asked about.
UNASKED = "nothing says whether a pull request from it merged"


def merged_pull_request(branch: str) -> Judged:
    """Whether a pull request from the branch into main has merged, as GitHub says.

    An overnight branch names no row, and its merge subject is the run's own, so only the pull
    request says it landed. Its tip says nothing: a night that has just begun forks from main, so
    its tip is on main before any of its work is. https://cli.github.com/manual/gh_pr_list
    """
    try:
        done = subprocess.run(
            ["gh", "pr", "list", "--head", branch, "--base", "main", "--state", "merged",
             "--json", "number", "--jq", ".[].number"],
            capture_output=True, text=True, check=False,
        )
    except OSError:
        return Judged(False, "gh is not on PATH, so nothing says whether a pull request from it "
                      "merged", UNASKED)
    if done.returncode != 0:
        return Judged(False, "gh could not say whether a pull request from it merged: "
                      f"{last_line(done)}", UNASKED)
    numbers = done.stdout.split()
    if not numbers:
        return Judged(False, "no pull request from it into main has merged",
                      "no pull request from it into main has merged")
    return Judged(True, f"pull request #{numbers[0]} from it merged")


def finished(branch: str, states: dict) -> Judged:
    """Whether the branch's work is finished by its kind's rule, and the reason either way."""
    if branch.startswith("overnight/"):
        if not OVERNIGHT.match(branch):
            reason = "its name is no overnight/<date>, so nothing says which run it is"
            return Judged(False, reason, reason)
        return merged_pull_request(branch)
    # A name that carries no id the roadmap knows is no claim a session could take: ADR-0020 made
    # every ticket a GitHub issue, so a live claim names a plan row, a split of one or issue-<n>, and
    # such a name -- task/ticket-71-..., task/2.7a-... -- is left over from the archived tracker, and
    # its work alone decides it.
    match = TASK.match(branch)
    if not match:
        return Judged(True, "its name carries no task id, so it claims nothing a session could take")
    ids = [normal(id_) for id_ in re.split(r"-and-", match.group(1))]
    claimed = []
    for id_ in ids:
        state, named = id_state(id_, states)
        if state == "unknown":
            continue
        if state != "landed":
            article = "an" if state[0] in "aeiou" else "a"
            return Judged(False, f"{named} is {state}, so the branch is its claim",
                          f"it is the claim of {article} {state} row, split or ticket")
        claimed.append(named)
    if not claimed:
        return Judged(True, f"{' and '.join(ids)} {'names' if len(ids) == 1 else 'name'} no plan "
                      f"row, split or ticket, so it claims nothing a session could take")
    return Judged(True, " and ".join(claimed) + (" is" if len(claimed) == 1 else " are") + " landed")


# --- whether a tip's work is on main -----------------------------------------------------------


# The groups of a tip whose work is not on main, and of one whose work git could not tell.
NOT_ON_MAIN = "its tip has commits with no equivalent on origin/main"
UNCERTAIN = "git cannot tell whether its tip's work is on origin/main"


def on_main(tip: str) -> Judged:
    """Whether the tip's work is on origin/main, and how, or why it is not or cannot be told."""
    ancestor = git("merge-base", "--is-ancestor", tip, MAIN, check=False).returncode
    if ancestor == 0:
        return Judged(True, "its tip is an ancestor of origin/main")
    if ancestor != 1:
        return Judged(False, "git could not compare its tip with origin/main, so its work is "
                      "uncertain", UNCERTAIN)
    # `git cherry` marks each commit the tip has and main lacks: `-` when main has a commit with the
    # same patch id, `+` when it has none (https://git-scm.com/docs/git-cherry). It leaves merge
    # commits out, which its manual does not say and its source does: cmd_cherry walks with
    # `revs.max_parents = 1` (https://github.com/git/git/blob/master/builtin/log.c), and the merge
    # case in prune_branches_tests.py asserts it as its premise. So the merges are read below.
    cherry = git("cherry", MAIN, tip, check=False)
    if cherry.returncode != 0:
        return Judged(False, "git cherry could not compare it with origin/main, so its work is "
                      "uncertain", UNCERTAIN)
    unmatched = [line for line in cherry.stdout.splitlines() if line.startswith("+")]
    if unmatched:
        count = len(unmatched)
        return Judged(False, f"{count} commit{'' if count == 1 else 's'} on it "
                      f"{'has' if count == 1 else 'have'} no equivalent on origin/main", NOT_ON_MAIN)
    # A merge's own changes -- a conflict resolved by hand, an edit made while merging -- are in no
    # patch `git cherry` reads, and nothing can find them on main by patch id. So each merge the tip
    # has and main lacks must be the merge git makes unaided: `git merge-tree --write-tree` prints
    # the tree a merge of the two parents gives, exits 1 on a conflict, and exits with anything else
    # when it cannot merge at all, as a git older than 2.38 does ("Exit status",
    # https://git-scm.com/docs/git-merge-tree). Any other merge leaves the work uncertain.
    merges = git("rev-list", "--min-parents=2", f"{MAIN}..{tip}", check=False)
    if merges.returncode != 0:
        return Judged(False, "git could not list the merges on it, so its work is uncertain",
                      UNCERTAIN)
    for merge in merges.stdout.split():
        parents = git("rev-list", "--parents", "-n", "1", merge).stdout.split()[1:]
        if len(parents) != 2:
            return Judged(False, f"merge {merge[:12]} has {len(parents)} parents, so its work is "
                          "uncertain", UNCERTAIN)
        unaided = git("merge-tree", "--write-tree", *parents, check=False)
        if unaided.returncode not in (0, 1):
            return Judged(False, f"git could not re-make merge {merge[:12]} (merge-tree "
                          f"--write-tree needs git 2.38): {last_line(unaided)}, so its work is "
                          "uncertain", UNCERTAIN)
        made = git("rev-parse", f"{merge}^{{tree}}").stdout.strip()
        if unaided.returncode != 0 or unaided.stdout.split()[:1] != [made]:
            return Judged(False, f"merge {merge[:12]} has changes of its own, which nothing finds "
                          "on origin/main, so its work is uncertain",
                          "a merge on it has changes of its own, which nothing finds on origin/main")
    return Judged(True, "every commit on it has an equivalent on origin/main")


# --- the branches ------------------------------------------------------------------------------


def copies() -> list[Copy]:
    """Every task/ and overnight/ branch, local and on origin, by name and then local first.

    Origin's tips are read with `git ls-remote`, which asks origin itself, rather than from the
    remote-tracking refs, which keep a branch origin has deleted until a fetch prunes them. These
    are the tips judged, and the tips each delete's lease names.
    """
    found = []
    local = git("for-each-ref", "--format=%(refname:strip=2) %(objectname)",
                "refs/heads/task", "refs/heads/overnight")
    for line in local.stdout.splitlines():
        branch, tip = line.split()
        found.append(Copy(branch, "local", tip))
    listed = git("ls-remote", "--heads", "origin", "refs/heads/task/*", "refs/heads/overnight/*")
    for line in listed.stdout.splitlines():
        tip, ref = line.split("\t")
        found.append(Copy(ref[len("refs/heads/"):], "origin", tip))
    return sorted(found, key=lambda copy: (copy.branch, copy.where != "local"))


def worktrees() -> dict[str, str]:
    """Each branch some worktree has checked out, with that worktree's path.

    Read from the porcelain listing, whose `branch refs/heads/<name>` line is the branch a worktree
    has checked out: https://git-scm.com/docs/git-worktree#_porcelain_format. A worktree whose
    directory is gone is still listed, and still keeps its branch: removing it is for a person. A
    worktree in the middle of a rebase or a bisect lists as `detached`, so its branch is not here;
    `git branch -D` still refuses that branch, and `main` then keeps origin's copy with it.
    """
    found: dict[str, str] = {}
    path = ""
    for line in git("worktree", "list", "--porcelain").stdout.splitlines():
        if line.startswith("worktree "):
            path = line[len("worktree "):]
        elif line.startswith("branch refs/heads/"):
            found[line[len("branch refs/heads/"):]] = path
    return found


def judge(copy: Copy, kind: Judged, trees: dict[str, str]) -> Verdict:
    """The verdict on one copy: its kind's answer, then the worktrees, then its own tip's work."""
    if not kind.ok:
        return Verdict(copy, False, kind.reason, kind.group)
    if copy.branch in trees:
        # The origin copy too: a session working there pushes to it.
        return Verdict(copy, False, f"it is checked out in the worktree at {trees[copy.branch]}",
                       "it is checked out in a worktree")
    work = on_main(copy.tip)
    return Verdict(copy, work.ok, f"{kind.reason}, and {work.reason}", work.group)


def only(verdicts: list[Verdict], names: list[str]) -> list[Verdict]:
    """The verdicts with the deletion limited to the named branches (#574).

    Every copy of a named branch must be marked for deletion, and a branch with no copy at all is
    not listed: either refuses the whole run, with the listing's reason, before anything goes. The
    names are the caller's claim about what the listing marks, and one wrong name means the claim
    is wrong. A branch with one copy left, the other already deleted by hand, is listed by that
    copy, so naming it deletes what remains. Every copy not named is kept under one reason, so the
    counts and the restore lines say what this run did and nothing more.
    """
    listed = {verdict.copy.branch for verdict in verdicts}
    named = set(names)
    wrong = []
    for name in names:
        if name not in listed:
            wrong.append(f"{name} is not listed: no task/ or overnight/ branch of that name is here "
                         "or on origin")
    for verdict in verdicts:
        if verdict.copy.branch in named and not verdict.delete:
            wrong.append(f"{verdict.copy.branch} is kept {verdict.copy.where}: {verdict.reason}")
    if wrong:
        raise Refused("--only names what the listing does not mark for deletion, so nothing was "
                      "deleted:\n  " + "\n  ".join(wrong))
    return [verdict if verdict.copy.branch in named
            else Verdict(verdict.copy, False, "it is not named by --only", "it is not named by --only")
            for verdict in verdicts]


def remove(copy: Copy) -> str | None:
    """Delete one copy if its tip is still the one judged. None when it went, or why it stays."""
    if copy.where == "origin":
        # The lease names the tip that was judged, so a push made since makes git refuse the delete,
        # and GitHub's side refuses it too if one lands while the delete is in flight: the push
        # sends that tip as the ref's expected old value. https://git-scm.com/docs/git-push
        done = git("push", "--quiet", f"--force-with-lease=refs/heads/{copy.branch}:{copy.tip}",
                   "origin", f":refs/heads/{copy.branch}", check=False)
        if done.returncode != 0:
            return f"origin refused the delete, so its tip may have moved: {last_line(done)}"
        return None
    current = git("rev-parse", "--verify", "--quiet", f"refs/heads/{copy.branch}", check=False)
    if current.stdout.strip() != copy.tip:
        return "its tip moved after it was judged"
    # `git branch -D` refuses a branch any worktree has checked out, or is rebasing or bisecting, at
    # the moment it deletes: "cannot delete branch '<name>' used by worktree at '<path>'". The manual
    # does not say so; prune_branches_tests.py's mid-rebase case holds it.
    done = git("branch", "-D", copy.branch, check=False)
    if done.returncode != 0:
        return f"git refused the delete: {last_line(done)}"
    return None


def last_line(done: subprocess.CompletedProcess) -> str:
    lines = (done.stderr or done.stdout).strip().splitlines()
    return lines[-1] if lines else f"exit {done.returncode}"


# --- the restore lines -------------------------------------------------------------------------


def restore_file() -> pathlib.Path:
    """Where a run's restore lines go: pruned-branches/<UTC date>-<12-hex HEAD sha>.txt in the
    common git directory, which every worktree of the repository shares
    (https://git-scm.com/docs/git-rev-parse#Documentation/git-rev-parse.txt---git-common-dir).
    Not a linked worktree's own git directory, a sub-directory of $GIT_DIR/worktrees that removing
    the worktree removes with it (https://git-scm.com/docs/git-worktree#_details): a landing runs in
    a worktree made for it, and the lines are wanted after it is gone."""
    common = git("rev-parse", "--path-format=absolute", "--git-common-dir").stdout.strip()
    head = git("rev-parse", "--verify", "HEAD").stdout.strip()
    date = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
    return pathlib.Path(common) / "pruned-branches" / f"{date}-{head[:12]}.txt"


def add_restore_line(path: pathlib.Path, line: str) -> None:
    """Add one restore line to the file as soon as its copy is gone, before the next copy is
    deleted, so a run that stops part-way leaves a line for each copy it deleted, but at most the
    one it was deleting when it stopped. Added, never replaced: an earlier run at the same HEAD that
    day keeps its lines."""
    path.parent.mkdir(exist_ok=True)
    with path.open("a", encoding="utf-8") as file:
        file.write(f"{line}\n")


def main(argv: list[str]) -> int:
    # No abbreviations: argparse would otherwise read a typo such as --delet as --delete
    # (https://docs.python.org/3/library/argparse.html#allow-abbrev), and the one flag here deletes.
    parser = argparse.ArgumentParser(
        prog="tools/prune_branches.py",
        description="Delete the branches of finished work, locally and on origin, and list the rest.",
        allow_abbrev=False,
    )
    parser.add_argument("--delete", action="store_true",
                        help="delete what the listing marks; without it nothing is deleted")
    parser.add_argument("--verbose", action="store_true",
                        help="with --delete, list each kept branch rather than a count per reason")
    parser.add_argument("--only", nargs="+", metavar="BRANCH",
                        help="with --delete, delete these branches alone; one the listing keeps, "
                             "or does not list, refuses the run and nothing is deleted")
    arguments = parser.parse_args(argv)
    if arguments.only and not arguments.delete:
        parser.error("--only needs --delete: a dry run already lists every branch with its verdict")
    # A --delete run counts what it keeps unless asked to list it; a dry run lists everything.
    counting = arguments.delete and not arguments.verbose

    try:
        root = git("rev-parse", "--show-toplevel").stdout.strip()
        git("fetch", "--quiet", "origin")
        git("rev-parse", "--verify", "--quiet", f"{MAIN}^{{commit}}")
        if git("merge-base", "--is-ancestor", "HEAD", MAIN, check=False).returncode != 0:
            raise Refused(
                "HEAD has commits origin/main lacks, and the roadmap would read their merge "
                "subjects as landed. Run this from a checkout at origin/main: "
                "git worktree add --detach ../spa-prune origin/main"
            )
        states = roadmap_states(root)
        trees = worktrees()
        found = copies()
        # One answer per name, so the two copies of a branch are held to the same one and an
        # overnight branch costs one question to GitHub.
        kinds = {name: finished(name, states) for name in {copy.branch for copy in found}}
        verdicts = [judge(copy, kinds[copy.branch], trees) for copy in found]
        if arguments.only:
            verdicts = only(verdicts, arguments.only)
        # Named before anything is deleted, so a git that cannot say where it goes deletes nothing.
        # Only a deletion writes it.
        restore_path = restore_file() if arguments.delete else None
    except Refused as refusal:
        print(f"prune_branches: {refusal}", file=sys.stderr)
        return 1

    restore = []
    # Why the file of restore lines could not be written, once it could not. Nothing is deleted
    # after that: a copy deleted then would have its line on the screen alone, which is the loss
    # #115 is about.
    unwritten = None
    # Branches whose copy here git would not delete. The local copy is judged first, so a refusal,
    # which is git seeing a worktree use the branch where the porcelain listing does not, keeps
    # origin's copy too: a session working there pushes to it.
    held: set[str] = set()
    # The copies a counting run kept by their verdict, by group, in the order first met.
    counted: dict[str, int] = {}
    for verdict in verdicts:
        copy, reason = verdict.copy, verdict.reason
        if not arguments.delete:
            word = "delete" if verdict.delete else "keep"
        elif verdict.delete and copy.where == "origin" and copy.branch in held:
            word, reason = "kept", "the copy here was not deleted, so origin's is kept with it"
        elif verdict.delete and unwritten is not None:
            word, reason = "kept", "the restore lines cannot be written, so nothing more goes"
        elif verdict.delete:
            refused = remove(copy)
            word, reason = ("deleted", reason) if refused is None else ("kept", refused)
            if refused is not None and copy.where == "local":
                held.add(copy.branch)
            if refused is None:
                line = (f"git branch {copy.branch} {copy.tip}" if copy.where == "local"
                        else f"git push origin {copy.tip}:refs/heads/{copy.branch}")
                # Printed whatever happens to the file.
                restore.append(line)
                try:
                    add_restore_line(restore_path, line)
                except OSError as error:
                    unwritten = f"could not write {restore_path}: {error}"
        elif counting:
            counted[verdict.group] = counted.get(verdict.group, 0) + 1
            continue
        else:
            word = "kept"
        print(f"{word:<8} {copy.where:<7} {copy.branch}  {copy.tip[:12]}  {reason}")

    # Commonest first; sorted() is stable, so a tie keeps the order the groups were met in.
    for group, count in sorted(counted.items(), key=lambda item: -item[1]):
        print(f"prune_branches: kept {count} because {group}; --verbose names each.")

    gone = sum(1 for verdict in verdicts if verdict.delete) if not arguments.delete else len(restore)
    if arguments.delete:
        print(f"prune_branches: deleted {gone} and kept {len(verdicts) - gone}.")
    else:
        print(f"prune_branches: --delete would delete {gone} and keep {len(verdicts) - gone}; "
              "nothing was deleted.")
    if restore:
        print("To restore one, recreate it at the tip it had:")
        for line in restore:
            print(f"  {line}")
        if unwritten is None:
            # The last line; tools/land.sh finds it by these words to name the file in its own.
            print(f"{RESTORE_FILE_LINE}{restore_path}")
    if unwritten is not None:
        print(f"prune_branches: {unwritten}. It deleted nothing after that, and the restore lines "
              "it printed are the one complete record of what it deleted.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
