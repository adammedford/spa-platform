#!/usr/bin/env python3
"""Test companion for tools/remap_rebuild.py (#318). Owner: integrator.

**What runs is the script itself**, as in tools/prune_branches_tests.py (D-3 in
docs/dependencies.md): each case starts the tracked file whole, through its arguments, the working
directory, PATH and the environment.

**Real git, a real rebuild, a double for gh.** Each case builds a throwaway repository holding an
integration branch the way an overnight run leaves one: lane commits on task branches, each merged
with `--no-ff`, a lane forked from the integration tip after another landed, a review commit on top,
a lane worktree per lane under a worktree root, and a failed lane that never merged. It then rebuilds
the branch the way docs/agents/landing-work.md describes, `git commit-tree` parents first, each
commit keeping its tree, author and committer, one merge's message changed, and records the map it
made. That recorded map is the independent answer the script's map is held to.

Two pairs in the rebuilt graph share a tree, which is what makes a tree alone ambiguous: a `--no-ff`
merge of a lane forked from the branch's own tip has its lane commit's tree. The map must still be
the one the rebuild made.

gh is the one call that leaves the machine, so each case's bin holds a strict gh double: it answers
`gh issue view <n> --json body --jq .body` from a file, records the files `gh issue edit <n>
--body-file` and `gh issue comment <n> --body-file` pass, and any other call exits 97.

**A constructed environment, inheriting nothing:** PATH is the case's own bin directory, HOME is
inside the throwaway directory, GIT_CONFIG_NOSYSTEM=1, GIT_CONFIG_GLOBAL names the case's own file,
LC_ALL=C, and every repository has `gc.auto=0` and `maintenance.auto=false`.

**The cases run at the same time** on tools/companion_runner.py's threads, so no case changes the
working directory, os.environ or anything else the process shares.

Run: python3 -I tools/remap_rebuild_tests.py
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
SCRIPT = ROOT / "tools" / "remap_rebuild.py"
ISSUE = "286"
SESSION = "the foreground session repairing PR #287"

GH_DOUBLE = """#!{python} -I
# A double for gh, written by tools/remap_rebuild_tests.py for one case. It answers the three calls
# tools/remap_rebuild.py makes, in the shapes gh's manual gives them:
#   gh issue view <n> --json body --jq .body      https://cli.github.com/manual/gh_issue_view
#   gh issue edit <n> --body-file <file>          https://cli.github.com/manual/gh_issue_edit
#   gh issue comment <n> --body-file <file>       https://cli.github.com/manual/gh_issue_comment
import json, pathlib, sys
fixtures = pathlib.Path({fixtures!r})
args = sys.argv[1:]
with open(fixtures / "calls.jsonl", "a") as log:
    log.write(json.dumps(args) + "\\n")
if (fixtures / "gh-fails").exists():
    print("gh double: could not reach GitHub", file=sys.stderr)
    sys.exit(1)
if args == ["issue", "view", {issue!r}, "--json", "body", "--jq", ".body"]:
    sys.stdout.write((fixtures / "body.md").read_text() + "\\n")
    sys.exit(0)
if len(args) == 5 and args[:3] in (["issue", "edit", {issue!r}], ["issue", "comment", {issue!r}]) \\
        and args[3] == "--body-file":
    (fixtures / (args[1] + "ed.md")).write_text(pathlib.Path(args[4]).read_text())
    sys.exit(0)
print("gh double: unexpected call", file=sys.stderr)
sys.exit(97)
"""


class Case(unittest.TestCase):
    """A throwaway repository with an integration branch, its lanes' worktrees, and its rebuild."""

    def setUp(self):
        scratch = tempfile.TemporaryDirectory(prefix="remap-rebuild-")
        self.addCleanup(scratch.cleanup)
        self.scratch = pathlib.Path(scratch.name).resolve()
        self.bin = self.scratch / "bin"
        self.fixtures = self.scratch / "fixtures"
        for directory in (self.bin, self.fixtures, self.scratch / "home"):
            directory.mkdir(parents=True)
        found = shutil.which("git")
        self.assertIsNotNone(found, "git is not on PATH")
        (self.bin / "git").symlink_to(found)
        gh = self.bin / "gh"
        gh.write_text(GH_DOUBLE.format(python=sys.executable, fixtures=str(self.fixtures), issue=ISSUE))
        gh.chmod(0o755)
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
        self.repo = self.scratch / "repo"
        self.wtroot = self.scratch / "worktrees"
        self.build_run()

    # --- the run and its rebuild -------------------------------------------------------------

    def git(self, *args, cwd=None, env=None):
        done = subprocess.run(["git", *args], cwd=cwd or self.repo, env=env or self.env,
                              capture_output=True, text=True, check=False)
        self.assertEqual(0, done.returncode, f"git {' '.join(args)}: {done.stderr}")
        return done.stdout.strip()

    def commit_in(self, worktree, path, text):
        (worktree / path).write_text(text)
        self.git("add", path, cwd=worktree)
        self.git("commit", "-q", "-m", f"Lane writes {path}", cwd=worktree)
        return self.git("rev-parse", "HEAD", cwd=worktree)

    def merge_into_run(self, branch, subject):
        self.git("merge", "-q", "--no-ff", "-m", subject, branch, cwd=self.run_wt)
        return self.git("rev-parse", "HEAD", cwd=self.run_wt)

    def lane(self, name, start):
        path = self.wtroot / name
        self.git("worktree", "add", "-q", "-b", f"task/{name}", str(path), start)
        return path

    def build_run(self):
        self.git("init", "-q", str(self.repo), cwd=self.scratch)
        (self.repo / "README").write_text("base\n")
        self.git("add", "README")
        self.git("commit", "-q", "-m", "Base")
        self.base = self.git("rev-parse", "HEAD")
        self.run_wt = self.wtroot / "overnight"
        self.git("worktree", "add", "-q", "-b", "overnight/2026-10-22", str(self.run_wt), self.base)

        one = self.lane("one", self.base)
        self.l1 = self.commit_in(one, "one.txt", "one\n")
        # Merged onto the base itself: a --no-ff merge whose tree is its lane commit's.
        self.m1 = self.merge_into_run("task/one", "Merge task one")
        two = self.lane("two", self.m1)
        self.l2 = self.commit_in(two, "two.txt", "two\n")
        # Forked from the run's tip and merged onto it: this pair shares a tree after the rebuild too.
        self.m2 = self.merge_into_run("task/two", "Merge task two")
        three = self.lane("three", self.m1)
        self.l3 = self.commit_in(three, "three.txt", "three\n")
        self.m3 = self.merge_into_run("task/three", "Merge task three")
        (self.run_wt / "review.txt").write_text("review\n")
        self.git("add", "review.txt", cwd=self.run_wt)
        self.git("commit", "-q", "-m", "Review: task three", cwd=self.run_wt)
        self.old_tip = self.git("rev-parse", "HEAD", cwd=self.run_wt)
        four = self.lane("four", self.m2)
        self.l4 = self.commit_in(four, "four.txt", "four\n")  # a failed lane: never merged
        self.lanes = {"one": one, "two": two, "three": three, "four": four}

    def rebuild(self, reword=None, change_tree=None):
        """Rebuilds the run branch with git commit-tree, parents first, as landing-work.md says.
        Returns the map it made, old sha to new, for every commit it recreated. `reword` changes
        that commit's message; `change_tree` gives that commit another tree, which no rebuild
        that keeps trees would do."""
        reword = reword or self.m1
        made = {}
        listing = self.git("rev-list", "--topo-order", "--reverse", "--parents",
                           self.old_tip, f"^{self.base}")
        for line in listing.splitlines():
            sha, *parents = line.split()
            fields = self.git("show", "-s", "--format=%an%x00%ae%x00%ad%x00%cn%x00%ce%x00%cd%x00%T",
                              "--date=raw", sha).split("\x00")
            env = dict(self.env, GIT_AUTHOR_NAME=fields[0], GIT_AUTHOR_EMAIL=fields[1],
                       GIT_AUTHOR_DATE=fields[2], GIT_COMMITTER_NAME=fields[3],
                       GIT_COMMITTER_EMAIL=fields[4], GIT_COMMITTER_DATE=fields[5])
            tree = fields[6]
            if sha == change_tree:
                tree = self.git("rev-parse", f"{self.base}^{{tree}}")
            message = self.git("show", "-s", "--format=%B", sha)
            if sha == reword:
                message = message + " (reworded)"
            args = ["commit-tree", tree, "-m", message]
            for parent in parents:
                args += ["-p", made.get(parent, parent)]
            made[sha] = self.git(*args, env=env)
        self.new_tip = made[self.old_tip]
        self.git("reset", "-q", "--hard", self.new_tip, cwd=self.run_wt)
        return {old: new for old, new in made.items() if old != new}

    def write_body(self):
        body = (
            "Standing answer: granted for this run (2026-10-22).\n\n"
            f"Run: base `{self.base}`, integration branch `overnight/2026-10-22`, tip "
            f"`{self.old_tip[:7]}`.\n\n"
            "| Task | Merge |\n| --- | --- |\n"
            f"| one | `{self.m1[:7]}` |\n| two | `{self.m2[:7]}` |\n| three | `{self.m3}` |\n\n"
            f"Lane two committed `{self.l2[:9]}`. Failed lane four is at `{self.l4[:7]}`.\n"
        )
        (self.fixtures / "body.md").write_text(body)
        return body

    # --- running the script ------------------------------------------------------------------

    def remap(self, *extra, old=None, new=None):
        args = [sys.executable, "-I", str(SCRIPT), "--old", old or self.old_tip,
                "--new", new or self.new_tip, "--issue", ISSUE, "--worktree-root", str(self.wtroot),
                "--session", SESSION, *extra]
        return subprocess.run(args, cwd=self.repo, env=self.env, capture_output=True, text=True,
                              check=False, timeout=120)

    def gh_calls(self):
        log = self.fixtures / "calls.jsonl"
        return [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []

    def gh_writes(self):
        return [call for call in self.gh_calls() if call[:2] != ["issue", "view"]]

    def heads(self):
        return {name: self.git("rev-parse", "HEAD", cwd=path) for name, path in self.lanes.items()}

    def printed_map(self, out):
        """The `map <old> <new>` lines the script prints."""
        return dict(re.findall(r"^map ([0-9a-f]{40}) ([0-9a-f]{40})", out, re.MULTILINE))

    def last_line(self, done):
        lines = (done.stdout + done.stderr).strip().splitlines()
        return lines[-1] if lines else ""

    def is_ancestor(self, commit, tip):
        return subprocess.run(["git", "merge-base", "--is-ancestor", commit, tip], cwd=self.repo,
                              env=self.env, check=False).returncode == 0


class TheMap(Case):
    def test_covers_every_old_commit_second_parents_included_each_pair_tree_equal(self):
        made = self.rebuild()
        self.write_body()

        done = self.remap()

        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        printed = self.printed_map(done.stdout)
        # Every commit the rebuild replaced, and nothing else: the lane commits reached through
        # second parents (l2 and l3) as well as the first-parent line.
        old_graph = set(self.git("rev-list", self.old_tip, f"^{self.new_tip}").split())
        self.assertEqual(old_graph, set(printed))
        self.assertIn(self.l2, printed)
        self.assertIn(self.l3, printed)
        self.assertEqual(made, printed)
        for old, new in printed.items():
            self.assertEqual(self.git("rev-parse", f"{old}^{{tree}}"),
                             self.git("rev-parse", f"{new}^{{tree}}"), f"{old} -> {new}")

    def test_a_tree_shared_by_two_rebuilt_commits_is_told_apart_by_their_parents(self):
        made = self.rebuild()
        self.write_body()
        # The fixture's premise, checked: the lane commit and its merge share one tree.
        self.assertEqual(self.git("rev-parse", f"{made[self.l2]}^{{tree}}"),
                         self.git("rev-parse", f"{made[self.m2]}^{{tree}}"))

        printed = self.printed_map(self.remap().stdout)

        self.assertEqual(made[self.l2], printed[self.l2])
        self.assertEqual(made[self.m2], printed[self.m2])


class Refusal(Case):
    def test_an_old_commit_with_no_tree_equal_partner_refuses_and_writes_nothing(self):
        self.rebuild(change_tree=self.l3)
        body = self.write_body()
        before = self.heads()

        done = self.remap("--apply")

        self.assertEqual(1, done.returncode, done.stdout + done.stderr)
        self.assertTrue(self.last_line(done).startswith("remap_rebuild: refused:"), self.last_line(done))
        self.assertIn(self.l3, self.last_line(done))
        self.assertEqual([], self.gh_writes())
        self.assertEqual(before, self.heads())
        self.assertEqual(body, (self.fixtures / "body.md").read_text())

    def test_an_old_tip_the_new_one_already_reaches_is_refused(self):
        self.rebuild()
        self.write_body()

        done = self.remap("--apply", old=self.base)

        self.assertEqual(1, done.returncode, done.stdout + done.stderr)
        self.assertTrue(self.last_line(done).startswith("remap_rebuild: refused:"), self.last_line(done))
        self.assertEqual([], self.gh_writes())

    def test_a_tip_git_cannot_read_stops_with_a_verdict(self):
        self.rebuild()
        self.write_body()

        done = self.remap("--apply", old="0" * 40)

        self.assertEqual(2, done.returncode, done.stdout + done.stderr)
        self.assertTrue(self.last_line(done).startswith("remap_rebuild: stopped:"), self.last_line(done))
        self.assertEqual([], self.gh_calls())

    def test_an_unreadable_issue_stops_before_any_worktree_moves(self):
        self.rebuild()
        self.write_body()
        (self.fixtures / "gh-fails").write_text("")
        before = self.heads()

        done = self.remap("--apply")

        self.assertEqual(2, done.returncode, done.stdout + done.stderr)
        self.assertTrue(self.last_line(done).startswith("remap_rebuild: stopped:"), self.last_line(done))
        self.assertEqual(before, self.heads())


class Worktrees(Case):
    def test_clean_lanes_move_to_their_rebuilt_tips_and_a_dirty_one_is_named_and_left(self):
        made = self.rebuild()
        self.write_body()
        (self.lanes["three"] / "scratch.txt").write_text("unsaved\n")

        done = self.remap("--apply")

        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        heads = self.heads()
        # Two: moved, and the check morning.js's Verify step 3 runs now passes for it.
        self.assertEqual(made[self.l2], heads["two"])
        self.assertTrue(self.is_ancestor(heads["two"], self.new_tip))
        self.assertEqual(made[self.l2], self.git("rev-parse", "refs/heads/task/two"))
        # One: its commit was never replaced, so it was an ancestor already and stays.
        self.assertEqual(self.l1, heads["one"])
        self.assertTrue(self.is_ancestor(heads["one"], self.new_tip))
        # Three: dirty, named, untouched.
        self.assertEqual(self.l3, heads["three"])
        self.assertTrue((self.lanes["three"] / "scratch.txt").exists())
        self.assertRegex(done.stdout, rf"dirty: {re.escape(str(self.lanes['three']))}")
        # Four: a failed lane outside the old graph, named, untouched.
        self.assertEqual(self.l4, heads["four"])
        self.assertRegex(done.stdout, rf"not in the old graph: {re.escape(str(self.lanes['four']))}")

    def test_the_dry_run_moves_no_worktree(self):
        self.rebuild()
        self.write_body()
        before = self.heads()

        done = self.remap()

        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertEqual(before, self.heads())
        self.assertRegex(done.stdout, rf"would move: {re.escape(str(self.lanes['two']))}")


class TheBody(Case):
    def old_shas_in(self, text):
        """Every sha-like token in `text` that abbreviates a commit the rebuild replaced."""
        replaced = set(self.git("rev-list", self.old_tip, f"^{self.new_tip}").split())
        return [token for token in re.findall(r"(?<![0-9a-zA-Z])[0-9a-f]{7,40}(?![0-9a-zA-Z])", text)
                if any(sha.startswith(token) for sha in replaced)]

    def test_the_rewritten_body_holds_no_old_sha_outside_the_rebuild_line(self):
        made = self.rebuild()
        self.write_body()

        done = self.remap("--apply")

        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        edited = (self.fixtures / "edited.md").read_text()
        rebuild_lines = [line for line in edited.splitlines() if line.startswith("Rebuilt ")]
        self.assertEqual(1, len(rebuild_lines), edited)
        self.assertIn(self.old_tip, rebuild_lines[0])
        self.assertIn(self.new_tip, rebuild_lines[0])
        self.assertIn(SESSION, rebuild_lines[0])
        others = "\n".join(line for line in edited.splitlines() if not line.startswith("Rebuilt "))
        self.assertEqual([], self.old_shas_in(others))
        # Each abbreviation became the rebuilt commit's, at no shorter a length.
        self.assertIn(f"| one | `{made[self.m1][:7]}", edited)
        self.assertIn(f"| three | `{made[self.m3]}` |", edited)
        self.assertIn(f"committed `{made[self.l2][:9]}", edited)
        # What the rebuild did not replace stays as it was.
        self.assertIn(f"base `{self.base}`", edited)
        self.assertIn(f"`{self.l4[:7]}`", edited)

    def test_the_comment_names_both_tips_and_the_map(self):
        made = self.rebuild()
        self.write_body()

        self.remap("--apply")

        comment = (self.fixtures / "commented.md").read_text()
        self.assertFalse(comment.lower().startswith("confirm"))
        self.assertIn(self.old_tip, comment)
        self.assertIn(self.new_tip, comment)
        for old, new in made.items():
            self.assertRegex(comment, rf"{old}.*{new}")

    def test_the_dry_run_prints_the_diff_and_writes_nothing(self):
        made = self.rebuild()
        body = self.write_body()

        done = self.remap()

        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertEqual([], self.gh_writes())
        self.assertEqual(body, (self.fixtures / "body.md").read_text())
        self.assertIn(f"-| one | `{self.m1[:7]}` |", done.stdout)
        self.assertIn(f"+| one | `{made[self.m1][:7]}` |", done.stdout)
        self.assertTrue(self.last_line(done).startswith("remap_rebuild: dry run:"), self.last_line(done))

    def test_a_second_run_adds_no_second_rebuild_line(self):
        self.rebuild()
        self.write_body()
        self.remap("--apply")
        shutil.copyfile(self.fixtures / "edited.md", self.fixtures / "body.md")

        done = self.remap("--apply")

        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        edited = (self.fixtures / "edited.md").read_text()
        rebuild_lines = [line for line in edited.splitlines() if line.startswith("Rebuilt ")]
        self.assertEqual(1, len(rebuild_lines), edited)
        # The record of the rebuild keeps the tip it replaced: the line is history, not a citation.
        self.assertIn(self.old_tip, rebuild_lines[0])


if __name__ == "__main__":
    runner.main()
