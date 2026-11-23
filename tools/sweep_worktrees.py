#!/usr/bin/env python3
"""Removes the linked worktrees whose work is on origin/main, and says why it keeps the rest.
Owner: integrator.

    tools/sweep_worktrees.py                     list every linked worktree with its verdict, its
                                                 reason and its size, and remove nothing
    tools/sweep_worktrees.py --apply             remove what the listing marks, then git worktree prune
    tools/sweep_worktrees.py --apply --verbose   and list each kept worktree, not only a count
    tools/sweep_worktrees.py --strip-idle [--idle-hours N] --end-orphans
                                                 and list what the two rules below would do; with
                                                 --apply, each flag runs its rule after the removals

Nothing else removes a worktree (#1503). tools/land.sh removes only the detached one a landing ran
in (#490); tools/prune_branches.py keeps every branch a worktree holds and never removes one; the
morning's After pass removes its own night's lane worktrees and no others. A foreground task
worktree, a repair worktree and a review worktree stay until somebody deletes them, each keeping its
branch from the branch cleanup and its caches on the disk: on 2026-11-23 the repository had 89
worktrees holding 67.8 GB with 3.4 GB of 460 GB free, and a full disk fails the build in every lane
at once. A worktree with its caches gone is 15 MB; one where tools/check.sh ran its Terraform half
holds 4.5 GB more.

tools/land.sh runs --apply after every merge into main, before tools/prune_branches.py --delete, so
a branch whose worktree has just gone can go in the same run. A worktree is removed only when all
seven of these hold, and the first that does not names the rule that keeps it:

  1. It is a spare: a linked worktree whose directory is there, not locked, not the one holding
     `main`, and not the one this runs in.
  2. It is clean: `git status --porcelain` in it prints nothing, no change and no untracked file,
     and no rebase, merge, bisect, cherry-pick or revert is part-way through in it.
  3. Its work is on origin/main. On a branch, by `on_main` in tools/prune_branches.py, imported
     rather than copied, so the two tools cannot disagree. On a detached HEAD, only when the tip is
     an ancestor of origin/main: no branch keeps a detached commit, and a patch-equivalent reading
     can be wrong for a merge.
  4. It holds nothing ignored that cannot be rebuilt. `git worktree remove` deletes ignored files
     without --force (https://git-scm.com/docs/git-worktree#_commands), so every path
     `git status --porcelain --ignored` lists as ignored must be a directory named one of DIRS or a
     file named one of FILES or ending in one of SUFFIXES. Any other ignored path keeps the
     worktree and is named: on 2026-11-23 the main checkout held a Terraform state, `.env`,
     `.certs/` and four `terraform.tfvars` as ignored files.
  5. Nothing runs in it: no process has its working directory inside it, as `lsof -a -d cwd` reads.
     An lsof that is missing or exits non-zero keeps every worktree that reaches this rule, since it
     cannot say.
  6. It is not a running night's. A worktree under a directory named `overnight-<date>` is kept
     unless an issue titled `Overnight <date>` reads closed and none of that title reads open: the
     After pass removes a night's worktrees and reports them, and the report issue is only created
     at the end of the run, so a night with no such issue yet is still running. A gh that cannot
     answer keeps it too.
  7. It is not waiting to start. A worktree made at origin/main and not yet committed to passes
     rules 1 to 5: a landing's `git worktree add --detach ../spa-land origin/main`, a task worktree
     before its first commit, a throwaway before its mutation. Its tip is a commit on origin/main's
     first-parent line, where a tip that came in by a merge, or as patch-equivalents, is not. Such
     a worktree is kept until nothing in it, file or directory, has changed for 72 hours.

Rules 5 and 6 are asked only of a worktree that passed the rules before them, so lsof runs at most
once and gh only for a night's worktree that would otherwise go.

It reads origin/main as this clone has it and does not fetch: land.sh has fetched just before it
runs, and an origin/main that is behind keeps more, never less, since a tip it does not reach fails
rule 3 and its first-parent line only grows.

Each worktree is listed with its size, the bytes its files take on the disk counted once per inode,
as du counts them. An --apply run checks each marked worktree's tip and status again just before
removing it, removes it with `git worktree remove` and never --force, so git itself refuses one that
changed since, and prints the size it freed and the command that brings it back:
`git worktree add <path> <branch>`, or `git worktree add --detach <path> <sha>` for a detached one.
The branch is never deleted here. It then runs `git worktree prune`, which forgets the worktrees
whose directories are gone. It counts what it kept under a short reason each, commonest first, as
`prune_branches.py --delete` does (#524), and --verbose lists each kept one. The last line is the
count removed and the total freed.

Two more rules make room without removing a worktree (#1504), each asked for by its own flag, so
--apply alone is the removals and nothing else. Without --apply a flag lists its candidates and acts
on nothing; with it, its rule runs after the removals on a process table read again then, and the
last line adds the bytes each rule freed. On 2026-11-23 deleting only these caches in two worktrees
waiting on a review freed 8.9 GB, and ending nine orphaned providers with one worktree freed 5.18 GB.

  --strip-idle   For each linked worktree the sweep keeps, other than the one holding `main`, whose
                 `.terraform` directories are initialised against the real backends: when nothing
                 outside its ignored cache directories has changed in --idle-hours (72), and no
                 process has its working directory inside it, delete each directory
                 `git status --porcelain --ignored` lists as wholly ignored whose last component is
                 one of STRIP. The list comes from git, so a tracked directory of one of those names
                 is never a candidate, and each is listed again just before it goes. The deletion
                 never follows a symlink: a provider linked into TF_PLUGIN_CACHE_DIR loses its link
                 and the cache keeps it. Each line gives the directory's size and how long ago its
                 worktree last changed. An lsof that cannot answer strips nothing.
  --end-orphans  Send SIGTERM to each process whose executable's name begins `terraform-provider-`,
                 whose parent is pid 1 and whose working directory, as lsof names it, no longer
                 exists: Terraform started it in a worktree since removed, and it holds its deleted
                 binary open where du cannot see it. Each line gives its pid, start time, executable
                 and the bytes of deleted files it holds open (`lsof +L1`). One whose parent still
                 runs, or whose working directory exists, is left and not listed. A ps or lsof that
                 cannot answer ends nothing. The executable is ps's `comm`, which is the whole path
                 on macOS; a ps that cuts it short, as Linux's cuts it at 15 characters, matches no
                 provider and so ends nothing.

A rule that cannot judge says so in one line and changes nothing, and the exit status stays the
removals'.

It exits 0 once every worktree is judged, whatever it kept; 1, removing nothing, when it cannot judge
(no origin/main, a worktree listing git cannot give), and 1 when `git worktree prune` fails; 2 for an
unknown argument.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import pathlib
import re
import shlex
import shutil
import signal
import stat
import subprocess
import sys
import time
from typing import NamedTuple

HERE = pathlib.Path(__file__).resolve().parent
# Loaded by path: `python3 -I` puts no script directory on sys.path, and rule 3 is that file's.
_spec = importlib.util.spec_from_file_location("prune_branches", HERE / "prune_branches.py")
prune_branches = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(prune_branches)

MAIN = prune_branches.MAIN
# What rule 4 lets `git worktree remove` delete: a cache a build, a test run or `terraform init`
# makes again.
DIRS = frozenset({".terraform", "bin", "obj", "TestResults", "__pycache__", "node_modules"})
FILES = frozenset({".terraform.lock.hcl", ".DS_Store"})
SUFFIXES = (".pyc",)
# Rule 7's quiet time.
IDLE_SECONDS = 72 * 60 * 60
# What --strip-idle deletes from an idle worktree the sweep keeps (#1504): the build output and
# Terraform's working directory, about 0.5 GB and 4.4 GB of a built worktree, which a build or
# `terraform init` makes again. Its quiet time is --idle-hours, 72 unless given.
STRIP = frozenset({".terraform", "bin", "obj"})
STRIP_IDLE_HOURS = 72.0
# What --end-orphans ends (#1504): a provider Terraform started, outliving it.
PROVIDER = "terraform-provider-"
NIGHT = re.compile(r"^overnight-(\d{4}-\d{2}-\d{2})$")
# What a part-way operation leaves in a worktree's git directory: https://git-scm.com/docs/git-status.
IN_PROGRESS = ("rebase-merge", "rebase-apply", "MERGE_HEAD", "BISECT_LOG", "CHERRY_PICK_HEAD",
               "REVERT_HEAD")


class Tree(NamedTuple):
    path: str
    head: str
    branch: str  # "" when detached
    locked: bool
    prunable: bool


class Verdict(NamedTuple):
    tree: Tree
    remove: bool
    reason: str
    group: str
    size: int


class Refused(Exception):
    """The sweep cannot decide anything here, and says why."""


def git(*args: str, cwd: str | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, check=False)


def last_line(done: subprocess.CompletedProcess) -> str:
    lines = (done.stderr or done.stdout).strip().splitlines()
    return lines[-1] if lines else f"exit {done.returncode}"


def real(path: str) -> str:
    return os.path.realpath(path)


# --- reading the repository --------------------------------------------------------------------


def linked_trees() -> list[Tree]:
    """Every linked worktree, from the porcelain listing, whose first block is the repository's own
    working tree and is left out: https://git-scm.com/docs/git-worktree#_porcelain_format."""
    done = git("worktree", "list", "--porcelain", "-z")
    if done.returncode != 0:
        raise Refused(f"git worktree list failed: {last_line(done)}")
    trees = []
    for block in done.stdout.split("\0\0"):
        fields = [field for field in block.split("\0") if field]
        if not fields or not fields[0].startswith("worktree "):
            continue
        path, head, branch, locked, prunable, bare = fields[0][len("worktree "):], "", "", False, False, False
        for field in fields[1:]:
            if field.startswith("HEAD "):
                head = field[len("HEAD "):]
            elif field.startswith("branch refs/heads/"):
                branch = field[len("branch refs/heads/"):]
            elif field == "locked" or field.startswith("locked "):
                locked = True
            elif field == "prunable" or field.startswith("prunable "):
                prunable = True
            elif field == "bare":
                bare = True
        if not bare:
            trees.append(Tree(path, head, branch, locked, prunable))
    return trees[1:]


def first_parent_line() -> set[str]:
    done = git("rev-list", "--first-parent", MAIN)
    if done.returncode != 0:
        raise Refused(f"git cannot read {MAIN}'s first-parent line: {last_line(done)}")
    return set(done.stdout.split())


def measure(path: str) -> tuple[int, float]:
    """(bytes on the disk, counted once per inode, as du counts them; the newest change time of
    anything under the path, the path itself included). Nothing is followed through a link."""
    seen: set[tuple[int, int]] = set()
    size, newest = 0, 0.0

    def count(entry: str) -> bool:
        nonlocal size, newest
        try:
            status = os.lstat(entry)
        except OSError:
            return False
        newest = max(newest, status.st_mtime)
        key = (status.st_dev, status.st_ino)
        if key not in seen:
            seen.add(key)
            size += getattr(status, "st_blocks", 0) * 512 or status.st_size
        return stat.S_ISDIR(status.st_mode)

    if count(path):
        for directory, subdirectories, files in os.walk(path, onerror=lambda _error: None):
            for name in subdirectories + files:
                count(os.path.join(directory, name))
    return size, newest


def human(size: int) -> str:
    for unit, scale in (("GB", 10**9), ("MB", 10**6), ("kB", 10**3)):
        if size >= scale:
            return f"{size / scale:.1f} {unit}"
    return f"{size} B"


def status_of(path: str) -> tuple[list[str], list[str]] | str:
    """(changed or untracked paths, ignored paths) in the worktree, or why git cannot say.

    The status is read without the optional index refresh, so reading writes nothing there
    (https://git-scm.com/docs/git#Documentation/git.txt---no-optional-locks), and with the untracked
    and ignored modes named, so no configuration of the user's hides an untracked file: `normal`
    lists a wholly untracked or ignored directory as the directory, which is what rule 4 names.
    https://git-scm.com/docs/git-status#_porcelain_format_version_1."""
    done = git("--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=normal",
               "--ignored=traditional", cwd=path)
    if done.returncode != 0:
        return f"git cannot read its status: {last_line(done)}"
    changed, ignored = [], []
    entries = done.stdout.split("\0")
    index = 0
    while index < len(entries):
        entry = entries[index]
        index += 1
        if not entry:
            continue
        code, name = entry[:2], entry[3:]
        if code == "!!":
            ignored.append(name)
        else:
            changed.append(name)
            if code[0] in "RC":
                index += 1  # a rename or copy carries its source as the next field
    return changed, ignored


def rebuildable(path: str) -> bool:
    name = path.rstrip("/").rsplit("/", 1)[-1]
    if path.endswith("/"):
        return name in DIRS
    return name in FILES or name.endswith(SUFFIXES)


def in_progress(path: str) -> str:
    done = git("rev-parse", "--absolute-git-dir", cwd=path)
    if done.returncode != 0:
        return "git cannot name its git directory"
    found = [name for name in IN_PROGRESS if os.path.exists(os.path.join(done.stdout.strip(), name))]
    return ", ".join(found)


class Processes:
    """Every process's working directory, read once, when a worktree first reaches rule 5, or when
    --strip-idle or --end-orphans first asks."""

    def __init__(self):
        self.read = False
        self.cwds: dict[int, str] = {}
        self.why = ""

    def load(self) -> None:
        if self.read:
            return
        self.read = True
        try:
            # -F pn prints each process's id on a line starting `p`, then each open file's name on
            # a line starting `n`: https://lsof.readthedocs.io. A working directory deleted since
            # still prints its old path, which is how --end-orphans sees that it is gone.
            done = subprocess.run(["lsof", "-a", "-d", "cwd", "-F", "pn"], capture_output=True,
                                  text=True, check=False)
        except OSError:
            self.why = "lsof is not on PATH, so nothing says whether a process runs in it"
            return
        if done.returncode != 0:
            self.why = f"lsof exited {done.returncode}, so nothing says whether a process runs in it"
            return
        pid = -1
        for line in done.stdout.splitlines():
            if line.startswith("p") and line[1:].isdigit():
                pid = int(line[1:])
            elif line.startswith("n"):
                self.cwds[pid] = line[1:]

    def inside(self, path: str) -> str | None:
        """Why a process keeps the worktree, or None when none runs in it."""
        self.load()
        if self.why:
            return self.why
        root = real(path)
        for cwd in self.cwds.values():
            if cwd == root or cwd.startswith(root + "/"):
                return f"a process has its working directory at {cwd}"
        return None

    def cwd_of(self, pid: int) -> str | None:
        """The process's working directory as lsof named it, or None when lsof named none."""
        self.load()
        return self.cwds.get(pid)


class Nights:
    """What gh says of each `Overnight <date>` issue, asked once a date."""

    def __init__(self):
        self.answers: dict[str, str | None] = {}

    def running(self, date: str) -> str | None:
        """Why the night keeps its worktree, or None once its issue reads closed."""
        if date not in self.answers:
            self.answers[date] = self.ask(date)
        return self.answers[date]

    @staticmethod
    def ask(date: str) -> str | None:
        title = f"Overnight {date}"
        try:
            done = subprocess.run(
                ["gh", "issue", "list", "--state", "all", "--search", f'in:title "{title}"',
                 "--json", "title,state", "--limit", "100"],
                capture_output=True, text=True, check=False)
        except OSError:
            return f"gh is not on PATH, so nothing says whether {title} is closed"
        if done.returncode != 0:
            return f"gh could not say whether {title} is closed: {last_line(done)}"
        try:
            states = {issue["state"] for issue in json.loads(done.stdout) if issue["title"] == title}
        except (ValueError, TypeError, KeyError):
            return f"gh's answer about {title} is not the listing asked for"
        if "OPEN" in states:
            return f"{title} is open"
        if "CLOSED" not in states:
            return f"no issue titled {title} exists yet, so the night may still be running"
        return None


# --- the verdict -------------------------------------------------------------------------------


def judge(tree: Tree, here: str, line: set[str], processes: Processes, nights: Nights,
          now: float) -> Verdict:
    """The verdict on one worktree: its rules in order, the first that fails naming the reason."""
    if tree.prunable or not os.path.isdir(tree.path):
        return Verdict(tree, False, "rule 1: its directory is gone; git worktree prune, which "
                       "--apply runs, forgets it", "rule 1: its directory is gone", 0)
    size, newest = measure(tree.path)

    def keep(reason: str, group: str | None = None) -> Verdict:
        return Verdict(tree, False, reason, group or reason, size)

    if tree.locked:
        return keep("rule 1: it is locked")
    if tree.branch == "main":
        return keep("rule 1: it holds main")
    if real(tree.path) == here:
        return keep("rule 1: it is the worktree this runs in")

    status = status_of(tree.path)
    if isinstance(status, str):
        return keep(f"rule 2: {status}", "rule 2: git cannot read its status")
    changed, ignored = status
    if changed:
        return keep(f"rule 2: it has uncommitted or untracked files: {', '.join(changed[:3])}"
                    + (" ..." if len(changed) > 3 else ""),
                    "rule 2: it has uncommitted or untracked files")
    busy = in_progress(tree.path)
    if busy:
        return keep(f"rule 2: an operation is part-way through in it: {busy}",
                    "rule 2: an operation is part-way through in it")

    if tree.branch:
        try:
            work = prune_branches.on_main(tree.head)
        except prune_branches.Refused as refusal:
            work = prune_branches.Judged(False, str(refusal), prune_branches.UNCERTAIN)
        if not work.ok:
            return keep(f"rule 3: {work.reason}", f"rule 3: {work.group}")
        on_main = f"on its branch {tree.branch}, {work.reason}"
    else:
        ancestor = git("merge-base", "--is-ancestor", tree.head, MAIN).returncode
        if ancestor != 0:
            reason = ("rule 3: its detached tip is not an ancestor of origin/main" if ancestor == 1
                      else "rule 3: git could not compare its detached tip with origin/main")
            return keep(reason)
        on_main = "its detached tip is an ancestor of origin/main"

    lasting = [path for path in ignored if not rebuildable(path)]
    if lasting:
        return keep(f"rule 4: it holds ignored paths nothing rebuilds: {', '.join(lasting)}",
                    "rule 4: it holds ignored paths nothing rebuilds")

    running = processes.inside(tree.path)
    if running:
        return keep(f"rule 5: {running}", "rule 5: a process runs in it, or lsof cannot say")

    for part in pathlib.PurePath(tree.path).parts[:-1]:
        night = NIGHT.match(part)
        if night:
            still = nights.running(night.group(1))
            if still:
                return keep(f"rule 6: it is under {part}, and {still}",
                            "rule 6: its night's Overnight issue does not read closed")

    if tree.head in line and now - newest < IDLE_SECONDS:
        hours = max(0, int((now - newest) // 3600))
        return keep(f"rule 7: its tip is on origin/main's first-parent line and something in it "
                    f"changed {hours} hours ago, under 72",
                    "rule 7: it was made at origin/main and changed in the last 72 hours")
    return Verdict(tree, True, f"{on_main}, and it is clean", "", size)


def restore_line(tree: Tree) -> str:
    if tree.branch:
        return f"git worktree add {shlex.quote(tree.path)} {shlex.quote(tree.branch)}"
    return f"git worktree add --detach {shlex.quote(tree.path)} {tree.head}"


def still_the_same(tree: Tree) -> str | None:
    """Why a marked worktree is no longer the one judged, read just before it is removed."""
    head = git("rev-parse", "--verify", "--quiet", "HEAD", cwd=tree.path).stdout.strip()
    if head != tree.head:
        return "its tip moved after it was judged"
    status = status_of(tree.path)
    if isinstance(status, str) or status[0] or any(not rebuildable(path) for path in status[1]):
        return "its files changed after it was judged"
    return None


# --- --strip-idle: an idle worktree's caches ---------------------------------------------------


class Cache(NamedTuple):
    tree: Tree
    path: str
    size: int
    hours: int


def cache_dirs(path: str) -> list[str] | None:
    """The directories git lists as ignored in the worktree whose last component is one of STRIP,
    as absolute paths, or None when git cannot say. Only a wholly ignored directory is listed with
    the trailing slash (https://git-scm.com/docs/git-status#_porcelain_format_version_1), so a
    tracked directory of one of those names, and a symlink, which git lists as a file, are never
    here."""
    status = status_of(path)
    if isinstance(status, str):
        return None
    return [os.path.normpath(os.path.join(path, entry)) for entry in status[1]
            if entry.endswith("/") and entry.rstrip("/").rsplit("/", 1)[-1] in STRIP]


def newest_outside(path: str, caches: list[str]) -> float:
    """The newest change time of anything under the worktree, itself included, outside the cache
    directories, which are not entered and whose own times are not read. Nothing is followed
    through a link."""
    skip = set(caches)
    newest = os.lstat(path).st_mtime
    for directory, subdirectories, files in os.walk(path, onerror=lambda _error: None):
        subdirectories[:] = [name for name in subdirectories
                             if os.path.normpath(os.path.join(directory, name)) not in skip]
        for name in subdirectories + files:
            try:
                newest = max(newest, os.lstat(os.path.join(directory, name)).st_mtime)
            except OSError:
                continue
    return newest


def idle_caches(trees: list[Tree], processes: Processes, now: float,
                idle_seconds: float) -> tuple[list[Cache], str]:
    """The cache directories --strip-idle deletes from the kept worktrees, and why none was judged
    when lsof cannot say whether a process runs in them."""
    found: list[Cache] = []
    for tree in trees:
        # The worktree holding main is initialised against the real backends (#1504).
        if tree.branch == "main" or tree.prunable or not os.path.isdir(tree.path):
            continue
        caches = cache_dirs(tree.path)
        if not caches:
            continue
        quiet = now - newest_outside(tree.path, caches)
        if quiet < idle_seconds:
            continue
        if processes.inside(tree.path):
            if processes.why:
                return [], processes.why
            continue
        for cache in caches:
            found.append(Cache(tree, cache, measure(cache)[0], max(0, int(quiet // 3600))))
    return found, ""


def strip(cache: Cache) -> tuple[str, int]:
    """Deletes one cache directory: (why it was left, or "" once deleted; the bytes freed)."""
    if cache.path not in (cache_dirs(cache.tree.path) or []):
        return "git no longer lists it as an ignored directory", 0
    try:
        if not stat.S_ISDIR(os.lstat(cache.path).st_mode):
            return "it is no longer a directory", 0
        # rmtree removes a symlink inside the tree and never follows it, and refuses a path that
        # is itself a link: https://docs.python.org/3/library/shutil.html#shutil.rmtree. A provider
        # linked into TF_PLUGIN_CACHE_DIR loses its link and the cache keeps the provider.
        shutil.rmtree(cache.path)
    except OSError as error:
        left = measure(cache.path)[0] if os.path.lexists(cache.path) else 0
        return f"it could not all be deleted: {error}", max(0, cache.size - left)
    return "", cache.size


# --- --end-orphans: a Terraform provider whose Terraform has gone -----------------------------


class Orphan(NamedTuple):
    pid: int
    started: str
    executable: str
    cwd: str


# `ps -o pid=,ppid=,lstart=,comm=`: lstart is five words, `Mon Nov 18 00:02:11 2026`, and comm,
# last so a space in it is kept, is the executable's path
# (https://keith.github.io/xcode-man-pages/ps.1.html).
PS_ROW = re.compile(r"^\s*(\d+)\s+(\d+)\s+(\S+\s+\S+\s+\d+\s+\S+\s+\d+)\s+(.+)$")


def orphans(processes: Processes) -> tuple[list[Orphan], str]:
    """The providers --end-orphans ends, and why none was judged when ps or lsof cannot say."""
    try:
        done = subprocess.run(["ps", "-A", "-o", "pid=,ppid=,lstart=,comm="], capture_output=True,
                              text=True, check=False)
    except OSError:
        return [], "ps is not on PATH, so nothing says which providers are orphaned"
    if done.returncode != 0:
        return [], f"ps exited {done.returncode}, so nothing says which providers are orphaned"
    found: list[Orphan] = []
    for line in done.stdout.splitlines():
        row = PS_ROW.match(line)
        # A row that does not read is not judged, so nothing is ended on a guess.
        if not row or int(row.group(2)) != 1:
            continue
        pid, executable = int(row.group(1)), row.group(4).strip()
        if not os.path.basename(executable).startswith(PROVIDER):
            continue
        cwd = processes.cwd_of(pid)
        if processes.why:
            return [], processes.why
        if cwd is None or os.path.exists(cwd):
            continue
        found.append(Orphan(pid, " ".join(row.group(3).split()), executable, cwd))
    return found, ""


def held(pids: list[int]) -> dict[int, int] | None:
    """The bytes of deleted files each process holds open, which du cannot see and which come back
    once it exits, or None when lsof cannot say. +L1 lists open files with no link left, -F pfis
    names each process, then each file's descriptor, inode and size: https://lsof.readthedocs.io."""
    if not pids:
        return {}
    try:
        done = subprocess.run(["lsof", "-a", "+L1", "-p", ",".join(str(pid) for pid in pids),
                               "-F", "pfis"], capture_output=True, text=True, check=False)
    except OSError:
        return None
    # lsof exits 1 when nothing matches, which here is a process holding no deleted file.
    if done.returncode != 0 and (done.returncode != 1 or done.stdout.strip()):
        return None
    sizes: dict[int, int] = {pid: 0 for pid in pids}
    seen: set[tuple[int, str]] = set()
    pid, inode = -1, ""
    for line in done.stdout.splitlines():
        field, value = line[:1], line[1:]
        if field == "p" and value.isdigit():
            pid = int(value)
        elif field == "f":
            inode = ""
        elif field == "i":
            inode = value
        elif field == "s" and value.isdigit() and pid in sizes and (pid, inode) not in seen:
            seen.add((pid, inode))
            sizes[pid] += int(value)
    return sizes


def end(orphan: Orphan) -> str:
    """Sends the provider SIGTERM: why it was left, or "" once sent."""
    try:
        os.kill(orphan.pid, signal.SIGTERM)
    except ProcessLookupError:
        return "it had exited already"
    except PermissionError:
        return "this user may not signal it"
    return ""


def main(argv: list[str]) -> int:
    # No abbreviations: argparse would otherwise read a typo such as --appl as --apply
    # (https://docs.python.org/3/library/argparse.html#allow-abbrev), and that flag removes.
    parser = argparse.ArgumentParser(
        prog="tools/sweep_worktrees.py",
        description="Remove the linked worktrees whose work is on origin/main, and list the rest.",
        allow_abbrev=False,
    )
    parser.add_argument("--apply", action="store_true",
                        help="remove what the listing marks; without it nothing is removed")
    parser.add_argument("--verbose", action="store_true",
                        help="with --apply, list each kept worktree rather than a count per reason")
    parser.add_argument("--strip-idle", action="store_true",
                        help="list, and with --apply delete, the ignored .terraform, bin and obj "
                             "directories of each idle worktree it keeps")
    parser.add_argument("--idle-hours", type=float, default=STRIP_IDLE_HOURS, metavar="N",
                        help="the hours without a change that make a worktree idle for "
                             "--strip-idle (default 72)")
    parser.add_argument("--end-orphans", action="store_true",
                        help="list, and with --apply send SIGTERM to, each Terraform provider "
                             "whose parent is pid 1 and whose working directory is gone")
    arguments = parser.parse_args(argv)
    if arguments.idle_hours < 0:
        parser.error("--idle-hours takes a number of hours, 0 or more")
    counting = arguments.apply and not arguments.verbose

    try:
        top = git("rev-parse", "--show-toplevel")
        if top.returncode != 0:
            raise Refused(f"this is not a git working tree: {last_line(top)}")
        if git("rev-parse", "--verify", "--quiet", f"{MAIN}^{{commit}}").returncode != 0:
            raise Refused(f"there is no {MAIN} here to judge the worktrees against")
        here = real(top.stdout.strip())
        trees = linked_trees()
        line = first_parent_line()
    except Refused as refusal:
        print(f"sweep_worktrees: {refusal}", file=sys.stderr)
        return 1

    processes, nights, now = Processes(), Nights(), time.time()
    verdicts = [judge(tree, here, line, processes, nights, now) for tree in trees]

    removed, freed = 0, 0
    counted: dict[str, int] = {}
    kept: list[Tree] = []
    for verdict in verdicts:
        tree, reason, word = verdict.tree, verdict.reason, ""
        if not arguments.apply:
            word = "remove" if verdict.remove else "keep"
        elif verdict.remove:
            changed = still_the_same(tree)
            done = None if changed else git("worktree", "remove", "--", tree.path)
            if changed is None and done.returncode == 0:
                word = "removed"
            else:
                word, reason = "kept", changed or f"git refused to remove it: {last_line(done)}"
        elif counting:
            counted[verdict.group] = counted.get(verdict.group, 0) + 1
            kept.append(tree)
            continue
        else:
            word = "kept"
        if word in ("keep", "kept"):
            kept.append(tree)
        print(f"{word:<8} {human(verdict.size):>9}  {tree.path}  {reason}")
        if word == "removed":
            removed += 1
            freed += verdict.size
            print(f"  restore: {restore_line(tree)}")

    status = 0
    if arguments.apply:
        pruned = git("worktree", "prune")
        if pruned.returncode != 0:
            print(f"sweep_worktrees: git worktree prune failed: {last_line(pruned)}", file=sys.stderr)
            status = 1

    # Commonest first; sorted() is stable, so a tie keeps the order the groups were met in.
    for group, count in sorted(counted.items(), key=lambda item: -item[1]):
        print(f"sweep_worktrees: kept {count} because {group}; --verbose names each.")

    # The two rules run after the removals, on a process table read again now: what has run since
    # the verdicts is what decides whether a cache or a provider is still in use.
    after = Processes()
    clauses: list[str] = []
    if arguments.strip_idle:
        caches, why = idle_caches(kept, after, time.time(), arguments.idle_hours * 3600)
        if why:
            print(f"sweep_worktrees: --strip-idle judged no worktree: {why}.")
        stripped, stripped_bytes = 0, 0
        for cache in caches:
            reason = (f"its worktree's newest change outside .terraform, bin and obj was "
                      f"{cache.hours} hours ago")
            word, freed_here = "strip", 0
            if arguments.apply:
                left, freed_here = strip(cache)
                word, reason = ("left", left) if left else ("stripped", reason)
                stripped += 0 if left else 1
                stripped_bytes += freed_here
            print(f"{word:<8} {human(freed_here if word == 'left' else cache.size):>9}  "
                  f"{cache.path}  {reason}")
        if arguments.apply:
            clauses.append(f"--strip-idle stripped {stripped} cache directories, freeing "
                           f"{human(stripped_bytes)}.")
        else:
            clauses.append(f"--strip-idle would strip {len(caches)} cache directories, freeing "
                           f"{human(sum(cache.size for cache in caches))}")
    if arguments.end_orphans:
        found, why = orphans(after)
        if why:
            print(f"sweep_worktrees: --end-orphans judged no process: {why}.")
        sizes = held([orphan.pid for orphan in found])
        ended, ended_bytes = 0, 0
        for orphan in found:
            holding = sizes.get(orphan.pid, 0) if sizes is not None else 0
            reason = f"its parent is pid 1 and its working directory {orphan.cwd} is gone"
            word = "end"
            if arguments.apply:
                left = end(orphan)
                word, reason = ("left", left) if left else ("ended", reason)
                if not left:
                    ended += 1
                    ended_bytes += holding
            size = human(holding) if sizes is not None else "unknown"
            print(f"{word:<8} {size:>9}  pid {orphan.pid} started {orphan.started}  "
                  f"{orphan.executable}  {reason}")
        unknown = "" if sizes is not None else ", lsof unable to say how much"
        if arguments.apply:
            clauses.append(f"--end-orphans ended {ended} orphaned providers, releasing "
                           f"{human(ended_bytes)} of deleted files they held open{unknown}.")
        else:
            clauses.append(f"--end-orphans would end {len(found)} orphaned providers, releasing "
                           f"{human(sum((sizes or {}).values()))} of deleted files they hold "
                           f"open{unknown}")

    if arguments.apply:
        print(" ".join([f"sweep_worktrees: removed {removed} and kept {len(verdicts) - removed}; "
                        f"freed {human(freed)}.", *clauses]))
    else:
        marked = [verdict for verdict in verdicts if verdict.remove]
        print(f"sweep_worktrees: --apply would remove {len(marked)} and keep "
              f"{len(verdicts) - len(marked)}, freeing {human(sum(v.size for v in marked))}; "
              + "".join(f"{clause}; " for clause in clauses)
              + ("nothing was changed." if clauses else "nothing was removed."))
    return status


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
