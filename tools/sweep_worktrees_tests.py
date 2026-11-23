#!/usr/bin/env python3
"""Test companion for tools/sweep_worktrees.py (#1503). Owner: integrator.

**What runs is the script itself**, as in tools/prune_branches_tests.py: each case starts the
tracked file whole, through its arguments, the working directory, PATH and the environment, and the
script carries no switch put there so a test could reach it. Rule 7's 72 hours are reached by setting
a worktree's file times back, not by a clock the script would read from the environment.

**Real git, real lsof, a double for gh.** Each case builds a bare `origin`, a clone of it whose main
has a merge, a cherry-picked commit and a fast-forward line, and the linked worktrees the case
judges. A child process with its working directory inside a worktree is a real one, seen by the real
lsof. gh is the one call that leaves the machine, so each case's bin holds a strict gh double: it
answers the issue listing the sweep makes for an `overnight-<date>` directory, from the case's
fixture, and any other call is recorded and exits 97.

**A stubbed process table for --end-orphans** (#1504). Its cases replace ps and lsof with doubles
answering from the case's fixtures, so the real process table, where orphaned providers may be
running, is never read by a run that may end one. Each row the table holds names a real `sleep` the
case started and ends, under an invented parent, name and working directory, so the SIGTERM the
sweep sends reaches a process the case owns and the rule's verdict is the table's.

**A constructed environment, inheriting nothing**, for tools/land_tests.py's reasons: PATH is the
case's own bin directory, HOME is inside the throwaway directory, GIT_CONFIG_NOSYSTEM=1,
GIT_CONFIG_GLOBAL names the case's own file, LC_ALL=C. Every repository is made with `gc.auto=0` and
`maintenance.auto=false`.

**The cases run at the same time**, on the threads of tools/companion_runner.py. Each keeps to its
own throwaway directory and its own constructed environment, and changes nothing the process shares.

Run: python3 -I tools/sweep_worktrees_tests.py
"""

from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import re
import shlex
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
SCRIPT = ROOT / "tools" / "sweep_worktrees.py"
TOOLS = ("git", "lsof")
VERDICTS = ("remove", "keep", "removed", "kept")
SIZE = re.compile(r"^\d+(\.\d)? (B|kB|MB|GB)$")
# Rule 7's quiet time, and a margin past it.
SET_BACK = 73 * 60 * 60

GH_DOUBLE = """#!/bin/bash
# A double for gh, written by tools/sweep_worktrees_tests.py for one case. It answers the one call
# tools/sweep_worktrees.py makes, in the shape gh's manual gives it
# (https://cli.github.com/manual/gh_issue_list):
#
#   gh issue list --state all --search 'in:title "Overnight <date>"' --json title,state --limit 100
#
# from $fixtures/issues.json, and records every call. Any other call exits 97.
log={log}
fixtures={fixtures}

n=0
while [ -e "$log/call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$@" > "$log/call-$n"

[ "$#" -eq 10 ] && [ "$1 $2" = "issue list" ] && [ "$3 $4" = "--state all" ] && [ "$5" = --search ] \\
  && [ "$7 $8" = "--json title,state" ] && [ "$9 ${{10}}" = "--limit 100" ] || {{
  : > "$log/unexpected"; echo "gh double: unexpected call" >&2; exit 97; }}
[ ! -e "$fixtures/gh-fails" ] || {{ echo "gh double: could not reach GitHub" >&2; exit 1; }}
/bin/cat -- "$fixtures/issues.json"
"""

# The lsof the race case puts first: it writes an ignored Terraform state into one worktree, then
# runs the real lsof, so that worktree passes rule 4 when it is judged and holds the file when it is
# removed.
RACING_LSOF = """#!/bin/sh
: > {target}/terraform.tfstate
exec {lsof} "$@"
"""


# The lsof the strip race case puts first: it adds a file in the worktree's bin/ to the index, then
# runs the real lsof, so bin/ is a candidate when listed and tracked when its deletion comes.
RACING_ADD = """#!/bin/sh
git -C {tree} add -f bin/Gateway.dll
exec {lsof} "$@"
"""


class Case(unittest.TestCase):
    """One throwaway origin and clone whose main holds a merge, a cherry-pick and a first-parent
    line, so a worktree's tip can come in by each of the three ways."""

    def setUp(self):
        scratch = tempfile.TemporaryDirectory(prefix="sweep-worktrees-")
        self.addCleanup(scratch.cleanup)
        self.scratch = pathlib.Path(scratch.name).resolve()
        self.bin = self.scratch / "bin"
        self.log = self.scratch / "log"
        self.fixtures = self.scratch / "fixtures"
        self.trees = self.scratch / "trees"
        for directory in (self.bin, self.log, self.fixtures, self.trees, self.scratch / "home"):
            directory.mkdir(parents=True)
        for tool in TOOLS:
            found = shutil.which(tool) or (f"/usr/sbin/{tool}" if os.path.exists(f"/usr/sbin/{tool}")
                                           else None)
            self.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            runner.link_tool(self.bin / tool, os.path.realpath(found))
        self.lsof = os.path.realpath(shutil.which("lsof") or "/usr/sbin/lsof")
        gh = self.bin / "gh"
        gh.write_text(GH_DOUBLE.format(log=shlex.quote(str(self.log)),
                                       fixtures=shlex.quote(str(self.fixtures))))
        gh.chmod(0o755)
        self.issues([])

        config = self.scratch / "gitconfig"
        config.write_text(
            "[user]\n\tname = Case\n\temail = case@example.invalid\n"
            "[init]\n\tdefaultBranch = main\n"
            "[gc]\n\tauto = 0\n[maintenance]\n\tauto = false\n"
            "[advice]\n\tdetachedHead = false\n"
        )
        self.env = {
            "PATH": str(self.bin),
            "HOME": str(self.scratch / "home"),
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": str(config),
            "LC_ALL": "C",
        }

        self.origin = self.scratch / "origin.git"
        self.work = self.scratch / "work"
        self.git("init", "-q", "--bare", str(self.origin), cwd=self.scratch)
        self.git("clone", "-q", str(self.origin), str(self.work), cwd=self.scratch)
        (self.work / ".gitignore").write_text(
            "bin/\nobj/\n.terraform/\n.terraform.lock.hcl\n*.tfstate\n")
        (self.work / "README.md").write_text("main\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "The first commit")
        self.first = self.git("rev-parse", "HEAD")
        # A branch merged with --no-ff, so its tip is main's second parent there, not on its
        # first-parent line.
        self.merged = self.branch_commit("task/merged", "merged.txt")
        self.git("merge", "-q", "--no-ff", "-m", "Merge task/merged", "task/merged")
        # A side commit merged the same way, for the detached worktrees.
        self.side = self.branch_commit("side", "side.txt")
        self.git("merge", "-q", "--no-ff", "-m", "Merge side", "side")
        # A branch whose one commit main holds as a cherry-pick: a patch-equivalent, no ancestor.
        # main moves on first, or the pick would make the same commit again, an ancestor.
        self.equivalent = self.branch_commit("task/equivalent", "equivalent.txt")
        self.git("commit", "-q", "--allow-empty", "-m", "main moves on")
        self.git("cherry-pick", self.equivalent)
        # A branch with a commit main lacks.
        self.unmerged = self.branch_commit("task/unmerged", "unmerged.txt")
        self.git("push", "-q", "origin", "main", "task/merged", "task/equivalent", "task/unmerged")
        self.git("fetch", "-q", "origin")
        self.tip = self.git("rev-parse", "origin/main")

    # --- building the worktrees --------------------------------------------------------------

    def git(self, *args, cwd=None):
        done = subprocess.run(["git", *args], cwd=cwd or self.work, env=self.env,
                              capture_output=True, text=True, check=False)
        self.assertEqual(0, done.returncode, f"git {' '.join(args)}: {done.stderr}")
        return done.stdout.strip()

    def branch_commit(self, branch, path):
        """One commit on a new branch from main's current tip, leaving main checked out."""
        self.git("checkout", "-q", "-b", branch)
        (self.work / path).write_text(f"{branch}\n")
        self.git("add", path)
        self.git("commit", "-q", "-m", f"{branch}: {path}")
        sha = self.git("rev-parse", "HEAD")
        self.git("checkout", "-q", "main")
        return sha

    def add(self, name, *how, under=None):
        """A linked worktree under trees/ (or `under`), on a branch (`how` = the branch) or
        detached at a commit (`how` = "--detach", the commit). Returns its path."""
        path = (under or self.trees) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        if how[0] == "--detach":
            self.git("worktree", "add", "-q", "--detach", str(path), how[1])
        else:
            self.git("worktree", "add", "-q", str(path), how[0])
        return path

    def set_back(self, path):
        """Every file and directory in the worktree, the worktree itself and its .git file included,
        changed 73 hours ago."""
        when = time.time() - SET_BACK
        for directory, subdirectories, files in os.walk(path, topdown=False):
            for name in files + subdirectories:
                os.utime(os.path.join(directory, name), (when, when), follow_symlinks=False)
        os.utime(path, (when, when))

    def child_in(self, path):
        """A real process with its working directory inside the worktree, ended when the case is."""
        child = subprocess.Popen(["/bin/sleep", "300"], cwd=path)
        self.addCleanup(child.wait)
        self.addCleanup(child.kill)

    def issues(self, listing):
        (self.fixtures / "issues.json").write_text(json.dumps(listing))

    def the_twelve(self):
        """The worktrees the ticket's first acceptance criterion names, by a short name each."""
        trees = {
            "merged": self.add("merged", "task/merged"),
            "equivalent": self.add("equivalent", "task/equivalent"),
            "unmerged": self.add("unmerged", "task/unmerged"),
            "untracked": self.add("untracked", "--detach", self.side),
            "detached-merged": self.add("detached-merged", "--detach", self.side),
            "fresh": self.add("fresh", "--detach", self.tip),
            "idle": self.add("idle", "--detach", self.first),
            "detached-equivalent": self.add("detached-equivalent", "--detach", self.equivalent),
            "state": self.add("state", "--detach", self.side),
            "caches": self.add("caches", "--detach", self.side),
            "locked": self.add("locked", "--detach", self.side),
            "busy": self.add("busy", "--detach", self.side),
        }
        (trees["untracked"] / "notes.txt").write_text("nobody has looked at this\n")
        (trees["state"] / "terraform.tfstate").write_text("{}\n")
        (trees["caches"] / "bin").mkdir()
        (trees["caches"] / "bin" / "Gateway.dll").write_bytes(b"\0" * 200_000)
        (trees["caches"] / ".terraform" / "providers").mkdir(parents=True)
        (trees["caches"] / ".terraform" / "providers" / "aws").write_bytes(b"\0" * 300_000)
        self.git("worktree", "lock", str(trees["locked"]))
        (trees["busy"] / "src").mkdir()
        self.child_in(trees["busy"] / "src")
        self.set_back(trees["idle"])
        return trees

    # --- running the sweep and reading it ----------------------------------------------------

    def sweep(self, *args, cwd=None):
        done = subprocess.run([sys.executable, "-I", str(SCRIPT), *args], cwd=cwd or self.work,
                              env=self.env, capture_output=True, text=True, check=False)
        return done.returncode, done.stdout, done.stderr

    def verdicts(self, output, trees):
        """{name: (verdict, size, reason)} for each worktree a line of the output names."""
        found = {}
        for line in output.splitlines():
            if not line.split(None, 1) or line.split(None, 1)[0] not in VERDICTS:
                continue
            for name, path in trees.items():
                marker = f"  {path}  "
                if marker in line:
                    head, reason = line.split(marker, 1)
                    found[name] = (head.split()[0], head.split(None, 1)[1].strip(), reason)
        return found

    def restore_lines(self, output):
        return [line[len("  restore: "):] for line in output.splitlines()
                if line.startswith("  restore: ")]

    def branches(self):
        return set(self.git("for-each-ref", "--format=%(refname:strip=2) %(objectname)",
                            "refs/heads").splitlines())

    def listed(self):
        return self.git("worktree", "list", "--porcelain")

    def calls(self):
        recorded = []
        n = 0
        while (self.log / f"call-{n}").exists():
            recorded.append((self.log / f"call-{n}").read_bytes().split(b"\0")[:-1])
            n += 1
        return [[part.decode() for part in call] for call in recorded]


REMOVED = {"merged", "equivalent", "detached-merged", "idle", "caches"}
KEPT_BY = {
    "unmerged": "rule 3: 1 commit on it has no equivalent on origin/main",
    "untracked": "rule 2: it has uncommitted or untracked files: notes.txt",
    "fresh": "rule 7: ",
    "detached-equivalent": "rule 3: its detached tip is not an ancestor of origin/main",
    "state": "rule 4: it holds ignored paths nothing rebuilds: terraform.tfstate",
    "locked": "rule 1: it is locked",
    "busy": "rule 5: a process has its working directory at ",
}


class TheListingMarksWhatTheSevenRulesAllow(Case):
    """With no flag the sweep lists every linked worktree, its verdict, its size and the rule that
    keeps it, and removes nothing."""

    def test_the_listing_marks_exactly_the_five_and_names_the_rule_keeping_each_other(self):
        trees = self.the_twelve()
        before = self.listed()

        code, out, err = self.sweep()

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out, trees)
        self.assertEqual(set(trees), set(verdicts), out)
        self.assertEqual(REMOVED, {name for name, (word, _, _) in verdicts.items()
                                   if word == "remove"}, out)
        for name, (word, size, reason) in verdicts.items():
            with self.subTest(worktree=name):
                self.assertRegex(size, SIZE, out)
                if name in KEPT_BY:
                    self.assertEqual("keep", word, out)
                    self.assertTrue(reason.startswith(KEPT_BY[name]), reason)
        # The caches are counted in the size: the 500 kB of bin/ and .terraform/.
        self.assertEqual("kB", verdicts["caches"][1].split()[1], out)
        self.assertGreater(float(verdicts["caches"][1].split()[0]), 400, out)
        self.assertIn("sweep_worktrees: --apply would remove 5 and keep 7, freeing ", out)
        self.assertTrue(out.splitlines()[-1].endswith("nothing was removed."), out)
        # Nothing was removed, and nothing asked gh: no worktree is under an overnight directory.
        self.assertEqual(before, self.listed())
        for path in trees.values():
            self.assertTrue(path.exists(), path)
        self.assertEqual([], self.calls())

    def test_a_worktree_part_way_through_a_bisect_is_kept_by_rule_2(self):
        tree = self.add("bisecting", "--detach", self.side)
        self.git("bisect", "start", cwd=tree)

        code, out, err = self.sweep()

        self.assertEqual(0, code, err)
        word, _, reason = self.verdicts(out, {"bisecting": tree})["bisecting"]
        self.assertEqual("keep", word, out)
        self.assertTrue(reason.startswith("rule 2: an operation is part-way through in it: "), reason)
        self.assertIn("BISECT_LOG", reason)

    def test_a_worktree_whose_directory_is_gone_is_kept_and_named(self):
        tree = self.add("gone", "--detach", self.side)
        shutil.rmtree(tree)

        code, out, err = self.sweep()

        self.assertEqual(0, code, err)
        word, _, reason = self.verdicts(out, {"gone": tree})["gone"]
        self.assertEqual("keep", word, out)
        self.assertTrue(reason.startswith("rule 1: its directory is gone"), reason)


class ApplyRemovesTheFiveAlone(Case):
    """--apply removes what the listing marks and nothing else, keeps every branch, and prints a
    restore line for each removal that brings it back at the same tip."""

    def test_apply_removes_the_five_keeps_every_branch_and_each_restore_line_brings_one_back(self):
        trees = self.the_twelve()
        branches = self.branches()
        tips = {name: self.git("rev-parse", "HEAD", cwd=path) for name, path in trees.items()}

        code, out, err = self.sweep("--apply", "--verbose")

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out, trees)
        self.assertEqual(REMOVED, {name for name, (word, _, _) in verdicts.items()
                                   if word == "removed"}, out)
        for name, path in trees.items():
            with self.subTest(worktree=name):
                self.assertEqual(name not in REMOVED, path.exists(), out)
                self.assertEqual(name not in REMOVED, f"worktree {path}\n" in self.listed() + "\n",
                                 out)
        self.assertEqual(branches, self.branches(), "the sweep deleted a branch")
        self.assertRegex(out.splitlines()[-1],
                         r"^sweep_worktrees: removed 5 and kept 7; freed \d+(\.\d)? (B|kB|MB|GB)\.$")

        lines = self.restore_lines(out)
        self.assertEqual(5, len(lines), out)
        for line in lines:
            with self.subTest(line=line):
                ran = subprocess.run(["/bin/sh", "-c", line], cwd=self.work, env=self.env,
                                     capture_output=True, text=True, check=False)
                self.assertEqual(0, ran.returncode, ran.stderr)
        for name in REMOVED:
            with self.subTest(restored=name):
                self.assertTrue(trees[name].exists(), out)
                self.assertEqual(tips[name], self.git("rev-parse", "HEAD", cwd=trees[name]))
        # A branch worktree comes back on its branch, a detached one detached.
        self.assertEqual("task/merged", self.git("symbolic-ref", "--short", "HEAD",
                                                 cwd=trees["merged"]))
        self.assertEqual("HEAD", self.git("rev-parse", "--abbrev-ref", "HEAD",
                                          cwd=trees["detached-merged"]))

    def test_a_counting_run_names_no_kept_worktree_and_counts_each_reason(self):
        trees = self.the_twelve()

        code, out, err = self.sweep("--apply")

        self.assertEqual(0, code, err)
        self.assertEqual({"removed"}, {word for word, _, _ in self.verdicts(out, trees).values()},
                         out)
        counts = [line for line in out.splitlines() if line.startswith("sweep_worktrees: kept ")]
        self.assertEqual(7, sum(int(line.split()[2]) for line in counts), out)
        # One line a reason, the commonest first.
        numbers = [int(line.split()[2]) for line in counts]
        self.assertEqual(sorted(numbers, reverse=True), numbers, counts)
        self.assertEqual(len(counts), len(set(line.split(" because ", 1)[1] for line in counts)))

    def test_a_worktree_that_changes_between_its_verdict_and_its_removal_is_kept(self):
        # The racing lsof writes a Terraform state into the worktree after rule 4 has passed it.
        tree = self.add("racing", "--detach", self.side)
        (self.bin / "lsof").unlink()
        (self.bin / "lsof").write_text(RACING_LSOF.format(target=shlex.quote(str(tree)),
                                                          lsof=shlex.quote(self.lsof)))
        (self.bin / "lsof").chmod(0o755)

        code, out, err = self.sweep("--apply", "--verbose")

        self.assertEqual(0, code, err)
        self.assertEqual(("kept", "its files changed after it was judged"),
                         (self.verdicts(out, {"racing": tree})["racing"][0],
                          self.verdicts(out, {"racing": tree})["racing"][2]), out)
        self.assertTrue((tree / "terraform.tfstate").exists())


class ARunningNightKeepsItsWorktrees(Case):
    """A worktree under overnight-<date>/ goes only once gh reads `Overnight <date>` closed."""

    def night_tree(self):
        return self.add("lane", "--detach", self.side, under=self.scratch / "overnight-2026-01-01")

    def asked(self):
        return [["issue", "list", "--state", "all", "--search", 'in:title "Overnight 2026-01-01"',
                 "--json", "title,state", "--limit", "100"]]

    def test_it_is_kept_while_the_issue_reads_open_and_removed_once_it_reads_closed(self):
        tree = self.night_tree()
        self.issues([{"title": "Overnight 2026-01-01", "state": "OPEN"}])

        code, out, err = self.sweep("--apply", "--verbose")

        self.assertEqual(0, code, err)
        self.assertEqual("kept", self.verdicts(out, {"lane": tree})["lane"][0], out)
        self.assertIn("rule 6: it is under overnight-2026-01-01, and Overnight 2026-01-01 is open",
                      out)
        self.assertTrue(tree.exists())
        self.assertEqual(self.asked(), self.calls())

        # A title that only contains the night's is not its issue.
        self.issues([{"title": "Overnight 2026-01-01", "state": "CLOSED"},
                     {"title": "Retro Overnight 2026-01-01", "state": "OPEN"}])
        code, out, err = self.sweep("--apply", "--verbose")

        self.assertEqual(0, code, err)
        self.assertEqual("removed", self.verdicts(out, {"lane": tree})["lane"][0], out)
        self.assertFalse(tree.exists())

    def test_a_night_with_no_issue_yet_or_a_gh_that_cannot_answer_keeps_it(self):
        tree = self.night_tree()
        for setup, said in ((lambda: None, "no issue titled Overnight 2026-01-01 exists yet"),
                            (lambda: (self.fixtures / "gh-fails").write_text(""),
                             "gh could not say whether Overnight 2026-01-01 is closed")):
            with self.subTest(said=said):
                setup()
                code, out, err = self.sweep("--apply", "--verbose")
                self.assertEqual(0, code, err)
                self.assertEqual("kept", self.verdicts(out, {"lane": tree})["lane"][0], out)
                self.assertIn(said, out)
                self.assertTrue(tree.exists())


class ItNeverRemovesWhereItRunsOrWhereMainIs(Case):
    """Rule 1: run from inside a linked worktree, the sweep never removes that one, nor the one
    holding main, though each passes every other rule."""

    def test_the_worktree_it_runs_in_and_the_one_holding_main_are_kept(self):
        # The repository's own tree lets go of main, so a linked worktree can hold it; that one's
        # tip is origin/main's and its times are set back, so rule 7 lets it go.
        self.git("checkout", "-q", "--detach", "main")
        holding = self.add("holding-main", "main")
        self.set_back(holding)
        here = self.add("here", "--detach", self.side)
        (here / "sub").mkdir()
        # The premise: from the repository's own tree, both would go.
        code, out, err = self.sweep()
        self.assertEqual(0, code, err)
        trees = {"holding-main": holding, "here": here}
        self.assertEqual("remove", self.verdicts(out, trees)["here"][0], out)
        self.assertEqual("keep", self.verdicts(out, trees)["holding-main"][0], out)

        for cwd in (here, here / "sub"):
            with self.subTest(cwd=cwd):
                code, out, err = self.sweep("--apply", "--verbose", cwd=cwd)

                self.assertEqual(0, code, err)
                verdicts = self.verdicts(out, trees)
                self.assertEqual(("kept", "rule 1: it is the worktree this runs in"),
                                 (verdicts["here"][0], verdicts["here"][2]), out)
                self.assertEqual(("kept", "rule 1: it holds main"),
                                 (verdicts["holding-main"][0], verdicts["holding-main"][2]), out)
                self.assertTrue(here.exists())
                self.assertTrue(holding.exists())


class IdleCase(Case):
    """Worktrees that the sweep keeps, holding the caches --strip-idle deletes and the things it
    must leave (#1504)."""

    def caches_branch(self):
        """A branch whose one commit ignores `.env` and `.certs/` and force-adds a tracked directory named `bin`,
        so a worktree on it is kept by rule 3 and holds both."""
        self.git("checkout", "-q", "-b", "task/caches")
        with open(self.work / ".gitignore", "a") as ignore:
            ignore.write(".env\n.certs/\n")
        (self.work / "tools" / "bin").mkdir(parents=True)
        (self.work / "tools" / "bin" / "run.sh").write_text("#!/bin/sh\n")
        self.git("add", ".gitignore")
        self.git("add", "-f", "tools/bin/run.sh")
        self.git("commit", "-q", "-m", "task/caches: a tracked bin and an ignored .env")
        sha = self.git("rev-parse", "HEAD")
        self.git("checkout", "-q", "main")
        return sha

    def fill(self, path):
        """The ignored bin/ and .terraform/ the rule deletes, and the ignored .env and .certs/ it
        leaves."""
        (path / "bin").mkdir()
        (path / "bin" / "Gateway.dll").write_bytes(b"\0" * 200_000)
        (path / ".terraform" / "providers").mkdir(parents=True)
        (path / ".terraform" / "providers" / "aws").write_bytes(b"\0" * 300_000)
        (path / ".env").write_text("NOT_A_SECRET=1\n")
        # An ignored directory whose name is none of the three.
        (path / ".certs").mkdir(exist_ok=True)
        (path / ".certs" / "local.pem").write_text("not a certificate\n")
        return path

    def strip_lines(self, output, word):
        """{path: (size, rest)} for each line of the output starting with `word`."""
        found = {}
        for line in output.splitlines():
            parts = line.split(None, 1)
            if not parts or parts[0] != word:
                continue
            size_and_path = parts[1].split("  ")
            found[size_and_path[1]] = (size_and_path[0].strip(), "  ".join(size_and_path[2:]))
        return found

    def snapshot(self, path):
        """Every path under `path`, links unfollowed, with its kind and size."""
        seen = {}
        for directory, subdirectories, files in os.walk(path):
            for name in subdirectories + files:
                full = os.path.join(directory, name)
                status = os.lstat(full)
                seen[os.path.relpath(full, path)] = (status.st_mode >> 12, status.st_size)
        return seen


class StripIdleDeletesTheCachesOfAnIdleKeptWorktree(IdleCase):
    """--strip-idle names, and with --apply deletes, the ignored .terraform, bin and obj
    directories of a kept worktree in which nothing else has changed for 72 hours."""

    def test_only_the_idle_worktrees_ignored_bin_and_terraform_are_listed_and_deleted(self):
        sha = self.caches_branch()
        idle = self.fill(self.add("idle", "task/caches"))
        self.set_back(idle)
        busy = self.fill(self.add("busy", "--detach", sha))
        busy_before = self.snapshot(busy)

        code, out, err = self.sweep("--strip-idle")

        self.assertEqual(0, code, err)
        listed = self.strip_lines(out, "strip")
        self.assertEqual({str(idle / "bin"), str(idle / ".terraform")}, set(listed), out)
        for path, (size, rest) in listed.items():
            with self.subTest(path=path):
                self.assertRegex(size, SIZE)
                self.assertIn("73 hours ago", rest)
        self.assertTrue((idle / "bin").exists() and (idle / ".terraform").exists(), out)
        self.assertIn("; --strip-idle would strip 2 cache directories, freeing ", out)
        self.assertTrue(out.splitlines()[-1].endswith("nothing was changed."), out)

        code, out, err = self.sweep("--apply", "--strip-idle")

        self.assertEqual(0, code, err)
        self.assertEqual({str(idle / "bin"), str(idle / ".terraform")},
                         set(self.strip_lines(out, "stripped")), out)
        self.assertFalse((idle / "bin").exists(), out)
        self.assertFalse((idle / ".terraform").exists(), out)
        self.assertTrue((idle / ".env").exists())
        self.assertTrue((idle / ".certs" / "local.pem").exists())
        self.assertTrue((idle / "tools" / "bin" / "run.sh").exists())
        self.assertEqual("", self.git("status", "--porcelain", cwd=idle))
        self.assertEqual(busy_before, self.snapshot(busy))
        self.assertRegex(out.splitlines()[-1],
                         r"^sweep_worktrees: removed 0 and kept 2; freed 0 B\. --strip-idle "
                         r"stripped 2 cache directories, freeing \d+(\.\d)? kB\.$")
        freed = float(out.splitlines()[-1].rsplit("freeing ", 1)[1].split()[0])
        self.assertGreater(freed, 400, out)

    def test_a_symlink_in_terraform_goes_and_its_target_outside_the_worktree_stays(self):
        # A provider linked into a shared TF_PLUGIN_CACHE_DIR, as `terraform init` leaves it.
        cache = self.scratch / "plugin-cache" / "aws"
        cache.mkdir(parents=True)
        (cache / "terraform-provider-aws").write_bytes(b"\0" * 100_000)
        idle = self.add("idle", "--detach", self.unmerged)
        (idle / ".terraform" / "providers").mkdir(parents=True)
        (idle / ".terraform" / "providers" / "aws").symlink_to(cache, target_is_directory=True)
        self.set_back(idle)

        code, out, err = self.sweep("--apply", "--strip-idle")

        self.assertEqual(0, code, err)
        self.assertEqual({str(idle / ".terraform")}, set(self.strip_lines(out, "stripped")), out)
        self.assertFalse(os.path.lexists(idle / ".terraform"), out)
        self.assertTrue((cache / "terraform-provider-aws").is_file(), out)
        self.assertEqual(100_000, (cache / "terraform-provider-aws").stat().st_size)

    def test_the_worktree_holding_main_and_one_a_process_runs_in_are_never_stripped(self):
        self.git("checkout", "-q", "--detach", "main")
        holding = self.fill(self.add("holding-main", "main"))
        busy = self.fill(self.add("busy", "--detach", self.unmerged))
        (busy / "src").mkdir()
        # The premise: a third worktree like them, idle and kept, is stripped.
        control = self.fill(self.add("control", "--detach", self.unmerged))
        for tree in (holding, busy, control):
            self.set_back(tree)
        self.child_in(busy / "src")

        code, out, err = self.sweep("--apply", "--strip-idle", "--verbose")

        self.assertEqual(0, code, err)
        self.assertEqual({str(control / "bin"), str(control / ".terraform")},
                         set(self.strip_lines(out, "stripped")), out)
        for tree in (holding, busy):
            with self.subTest(tree=tree.name):
                self.assertTrue((tree / "bin" / "Gateway.dll").exists(), out)
                self.assertTrue((tree / ".terraform" / "providers" / "aws").exists(), out)

    def test_a_cache_git_stops_listing_before_its_turn_is_left(self):
        # The racing lsof, run once the candidates are listed, adds a file in bin/ to the index, so
        # git no longer lists bin/ as wholly ignored when its deletion comes.
        idle = self.fill(self.add("idle", "--detach", self.unmerged))
        self.set_back(idle)
        (self.bin / "lsof").unlink()
        (self.bin / "lsof").write_text(RACING_ADD.format(tree=shlex.quote(str(idle)),
                                                         lsof=shlex.quote(self.lsof)))
        (self.bin / "lsof").chmod(0o755)

        code, out, err = self.sweep("--apply", "--strip-idle")

        self.assertEqual(0, code, err)
        left = self.strip_lines(out, "left")
        self.assertEqual({str(idle / "bin")}, set(left), out)
        self.assertIn("git no longer lists it as an ignored directory", left[str(idle / "bin")][1])
        self.assertTrue((idle / "bin" / "Gateway.dll").exists(), out)
        self.assertFalse((idle / ".terraform").exists(), out)

    def test_an_idle_hours_that_is_no_number_of_hours_is_refused(self):
        tree = self.fill(self.add("idle", "--detach", self.unmerged))
        self.set_back(tree)
        for value in ("-1", "nan", "inf"):
            with self.subTest(value=value):
                code, _, err = self.sweep("--apply", "--strip-idle", "--idle-hours", value)
                self.assertEqual(2, code, err)
                self.assertIn("--idle-hours takes a number of hours, 0 or more", err)
                self.assertTrue((tree / "bin" / "Gateway.dll").exists())

    def test_idle_hours_sets_the_quiet_time(self):
        # Nothing in the worktree is older than this run, so only --idle-hours 0 makes it idle.
        tree = self.fill(self.add("recent", "--detach", self.unmerged))

        code, out, err = self.sweep("--strip-idle")
        self.assertEqual(0, code, err)
        self.assertEqual({}, self.strip_lines(out, "strip"), out)

        code, out, err = self.sweep("--strip-idle", "--idle-hours", "0")
        self.assertEqual(0, code, err)
        self.assertEqual({str(tree / "bin"), str(tree / ".terraform")},
                         set(self.strip_lines(out, "strip")), out)


# The process table the orphan cases stub: ps and lsof doubles answering from the case's fixtures,
# recording each call, and exiting 97 on any call the sweep was not written to make.
PS_DOUBLE = """#!/bin/bash
log={log}
n=0
while [ -e "$log/ps-call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$@" > "$log/ps-call-$n"
[ "$*" = "-A -o pid=,ppid=,lstart=,comm=" ] || {{ echo "ps double: unexpected call" >&2; exit 97; }}
/bin/cat -- {fixtures}/ps.txt
[ ! -e {fixtures}/ps-fails ] || exit 1
"""

LSOF_DOUBLE = """#!/bin/bash
log={log}
n=0
while [ -e "$log/lsof-call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$@" > "$log/lsof-call-$n"
if [ "$*" = "-a -d cwd -F pn" ]; then exec /bin/cat -- {fixtures}/cwd.txt; fi
if [ "$1 $2 $3" = "-a +L1 -p" ] && [ "$5 $6" = "-F pfis" ]; then exec /bin/cat -- {fixtures}/held.txt; fi
echo "lsof double: unexpected call" >&2
exit 97
"""

START = "Mon Nov 16 00:02:11 2026"


class OrphanCase(IdleCase):
    """Five real child processes, each named in a stubbed process table as something else: so the
    sweep's SIGTERM reaches a process this case owns, and the table says what the rule judges."""

    def stub_table(self):
        for name, text in (("ps", PS_DOUBLE), ("lsof", LSOF_DOUBLE)):
            # setUp linked the real lsof here; unlinked first, so the double never writes through.
            (self.bin / name).unlink(missing_ok=True)
            (self.bin / name).write_text(text.format(log=shlex.quote(str(self.log)),
                                                     fixtures=shlex.quote(str(self.fixtures))))
            (self.bin / name).chmod(0o755)
        gone = str(self.scratch / "removed-worktree" / "infra")
        there = str(self.scratch)
        self.children = {}
        rows, cwds = [], []
        for name, ppid, executable, cwd in (
                ("orphan", 1, f"{gone}/.terraform/providers/terraform-provider-x_v5", gone),
                ("parent-runs", 4242, f"{gone}/.terraform/providers/terraform-provider-x_v5", gone),
                ("cwd-exists", 1, f"{there}/.terraform/providers/terraform-provider-x_v5", there),
                ("other-name", 1, "/usr/local/bin/terraform", gone),
                # lsof names no working directory for it, so nothing says it is gone.
                ("no-cwd-row", 1, f"{gone}/.terraform/providers/terraform-provider-x_v5", None)):
            child = subprocess.Popen(["/bin/sleep", "300"])
            self.addCleanup(child.wait)
            self.addCleanup(child.kill)
            self.children[name] = child
            rows.append(f"{child.pid:>5} {ppid:>5} {START}     {executable}\n")
            if cwd is not None:
                cwds.append(f"p{child.pid}\nfcwd\nn{cwd}\n")
        (self.fixtures / "ps.txt").write_text("".join(rows))
        (self.fixtures / "cwd.txt").write_text("".join(cwds))
        (self.fixtures / "held.txt").write_text(
            f"p{self.children['orphan'].pid}\nftxt\ni4711\ns755000000\n")

    def ended(self, name):
        child = self.children[name]
        try:
            child.wait(timeout=5)
        except subprocess.TimeoutExpired:
            return False
        return child.returncode == -signal.SIGTERM

    def alive(self, name):
        return self.children[name].poll() is None

    def ps_calls(self):
        return sorted(path.name for path in self.log.glob("ps-call-*"))


class EndOrphansEndsAProviderWhoseTerraformHasGone(OrphanCase):

    def test_only_the_provider_with_parent_pid_1_and_a_gone_working_directory_is_ended(self):
        self.stub_table()
        orphan = self.children["orphan"].pid

        code, out, err = self.sweep("--end-orphans")

        self.assertEqual(0, code, err)
        listed = [line for line in out.splitlines() if line.split(None, 1)[:1] == ["end"]]
        self.assertEqual(1, len(listed), out)
        self.assertIn(f"  pid {orphan} started {START}  ", listed[0])
        self.assertIn("/.terraform/providers/terraform-provider-x_v5  ", listed[0])
        self.assertIn("755.0 MB", listed[0])
        self.assertTrue(all(self.alive(name) for name in self.children), out)
        self.assertTrue(out.splitlines()[-1].endswith(
            "; --end-orphans would end 1 orphaned provider, releasing 755.0 MB of deleted files "
            "they hold open; nothing was changed."), out)

        code, out, err = self.sweep("--apply", "--end-orphans")

        self.assertEqual(0, code, err)
        self.assertTrue(self.ended("orphan"), out)
        for name in ("parent-runs", "cwd-exists", "other-name", "no-cwd-row"):
            with self.subTest(left=name):
                self.assertTrue(self.alive(name), out)
                self.assertNotIn(f"pid {self.children[name].pid} ", out)
        self.assertEqual(f"sweep_worktrees: removed 0 and kept 0; freed 0 B. --end-orphans ended 1 "
                         "orphaned provider, releasing 755.0 MB of deleted files they held open.",
                         out.splitlines()[-1])

    def test_a_ps_that_cannot_answer_ends_nothing_and_says_so(self):
        self.stub_table()
        (self.bin / "ps").unlink()

        code, out, err = self.sweep("--apply", "--end-orphans")

        self.assertEqual(0, code, err)
        self.assertIn("sweep_worktrees: --end-orphans judged no process: ps is not on PATH", out)
        self.assertTrue(all(self.alive(name) for name in self.children), out)

    def test_a_ps_that_prints_its_table_and_exits_non_zero_ends_nothing(self):
        self.stub_table()
        (self.fixtures / "ps-fails").write_text("")

        code, out, err = self.sweep("--apply", "--end-orphans")

        self.assertEqual(0, code, err)
        self.assertIn("sweep_worktrees: --end-orphans judged no process: ps exited 1", out)
        self.assertTrue(all(self.alive(name) for name in self.children), out)


class AnLsofThatCannotAnswerStripsAndEndsNothing(OrphanCase):

    def test_both_rules_say_so_and_change_nothing(self):
        self.stub_table()
        (self.bin / "lsof").unlink()
        (self.bin / "lsof").write_text("#!/bin/sh\nexit 1\n")
        (self.bin / "lsof").chmod(0o755)
        idle = self.fill(self.add("idle", "--detach", self.unmerged))
        self.set_back(idle)

        code, out, err = self.sweep("--apply", "--strip-idle", "--end-orphans")

        self.assertEqual(0, code, err)
        self.assertIn("sweep_worktrees: --strip-idle judged no worktree: lsof exited 1", out)
        self.assertIn("sweep_worktrees: --end-orphans judged no process: lsof exited 1", out)
        self.assertTrue((idle / "bin" / "Gateway.dll").exists(), out)
        self.assertTrue((idle / ".terraform" / "providers" / "aws").exists(), out)
        self.assertTrue(all(self.alive(name) for name in self.children), out)


class EachFlagAppliesItsOwnRuleAlone(OrphanCase):
    """--apply alone stays the removals; --strip-idle and --end-orphans each add their own rule."""

    def test_apply_alone_applies_neither_and_each_flag_applies_only_its_own(self):
        self.stub_table()
        idle = self.add("idle", "--detach", self.unmerged)

        def refill():
            if not (idle / "bin").exists():
                self.fill(idle)
            self.set_back(idle)

        # The run that ends the orphan comes last, since an ended process stays ended.
        for flags, stripped, ended in (((), False, False), (("--strip-idle",), True, False),
                                       (("--end-orphans",), False, True)):
            with self.subTest(flags=flags):
                refill()
                asked = len(self.ps_calls())

                code, out, err = self.sweep("--apply", *flags)

                self.assertEqual(0, code, err)
                self.assertEqual(not stripped, (idle / "bin").exists(), out)
                self.assertEqual(not stripped, (idle / ".terraform").exists(), out)
                self.assertEqual(ended, not self.alive("orphan"), out)
                self.assertEqual(ended, len(self.ps_calls()) > asked, self.ps_calls())
                self.assertEqual(stripped, "--strip-idle" in out.splitlines()[-1], out)
                self.assertEqual(ended, "--end-orphans" in out.splitlines()[-1], out)
        self.assertTrue(self.ended("orphan"))


class ItRefusesWhereItCannotDecide(Case):

    def test_no_origin_main_removes_nothing(self):
        tree = self.add("merged", "task/merged")
        self.git("update-ref", "-d", "refs/remotes/origin/main")

        code, out, err = self.sweep("--apply")

        self.assertEqual(1, code, out)
        self.assertIn("there is no origin/main here", err)
        self.assertTrue(tree.exists())

    def test_an_unknown_or_abbreviated_argument_removes_nothing(self):
        tree = self.add("merged", "task/merged")
        for argument in ("--appl", "--force"):
            with self.subTest(argument=argument):
                code, _, _ = self.sweep(argument)
                self.assertEqual(2, code)
                self.assertTrue(tree.exists())

    def test_an_lsof_that_cannot_answer_keeps_what_reaches_rule_5(self):
        tree = self.add("merged", "task/merged")
        (self.bin / "lsof").unlink()
        (self.bin / "lsof").write_text("#!/bin/sh\nexit 1\n")
        (self.bin / "lsof").chmod(0o755)

        code, out, err = self.sweep("--apply", "--verbose")

        self.assertEqual(0, code, err)
        self.assertIn("rule 5: lsof exited 1, so nothing says whether a process runs in it", out)
        self.assertTrue(tree.exists())


if __name__ == "__main__":
    runner.main()
