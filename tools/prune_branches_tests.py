#!/usr/bin/env python3
"""Test companion for tools/prune_branches.py, and for the landing that runs it (#71). Owner:
integrator.

**What runs is the script itself**, as in tools/land_tests.py (D-3 in docs/dependencies.md): each
case starts the tracked file whole, through its arguments, the working directory, PATH and the
environment, and the script carries no switch put there so a test could reach it.

**Real git, a real roadmap, a double for gh.** Each case builds a bare `origin` and a clone of it in
a throwaway directory. The clone's first commit holds copies of the five files tools/roadmap.py reads
(itself, the two documents it writes and the two plan files), so `roadmap.py --json` derives the row
states from that case's own merge subjects, which the case writes. gh is the one call that leaves the
machine, so each case's bin holds a strict gh double: it answers the pull request listing the cleanup
makes for an overnight branch, and the three `pr view` reads and the merge tools/land.sh makes, and
any other call is recorded and exits 97.

**A constructed environment, inheriting nothing**, for land_tests.py's reasons: PATH is the case's
own bin directory, HOME is inside the throwaway directory, GIT_CONFIG_NOSYSTEM=1, GIT_CONFIG_GLOBAL
names the case's own file, LC_ALL=C. Every repository is made with `gc.auto=0` and
`maintenance.auto=false`, so no background writer races the cleanup.

**The interpreter is named** for the cases that run tools/land.sh: `/bin/bash`, so a Mac tests the
3.2 that macOS ships. When SCRIPT_TESTS_REQUIRE_BASH is set (CI's macOS job sets it to 3.2), a
different /bin/bash fails the run rather than skipping it.

Run: python3 -I tools/prune_branches_tests.py
"""

from __future__ import annotations

import datetime
import os
import pathlib
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "prune_branches.py"
LAND = ROOT / "tools" / "land.sh"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"

# What tools/roadmap.py reads and writes, read from this checkout: a copy kept here would drift.
ROADMAP_FILES = (
    "tools/roadmap.py",
    "README.md",
    "docs/roadmap.md",
    "docs/plan/03-workstreams-and-tasks.md",
    "docs/plan/06-phase-2-and-risks.md",
)
TOOLS = ("git", "dirname", "basename", "awk")
VERDICTS = ("delete", "keep", "deleted", "kept")

GH_DOUBLE = """#!/bin/bash
# A double for gh, written by tools/prune_branches_tests.py for one case. It answers the calls
# tools/prune_branches.py and tools/land.sh make, in the shapes gh's manual gives them:
#
#   gh pr list --head <branch> --base main --state merged --json number --jq '.[].number'
#                                                            https://cli.github.com/manual/gh_pr_list
#   gh pr view <number> --json <field> --jq <expression>     https://cli.github.com/manual/gh_pr_view
#   gh pr merge <number> --merge --match-head-commit <sha> [--subject <text>] --body <text>
#                                                            https://cli.github.com/manual/gh_pr_merge
#
# A merged pull request is a file under $fixtures/merged named for its head branch, holding its
# number. A file under $fixtures/on-list named the same way is run before the listing answers, which
# is how a case moves a branch on origin while the cleanup is between reading it and deleting it.
# The merge really merges: in $server, a clone of the case's origin, as GitHub would on its side.
log={log}
fixtures={fixtures}
server={server}

n=0
while [ -e "$log/call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$@" > "$log/call-$n"

unexpected() {{
  : > "$log/unexpected"
  printf 'gh double: unexpected call, recorded as call-%s\\n' "$n" >&2
  exit 97
}}

# Bash's own substitution, because the case's PATH holds no tr.
flat() {{ printf '%s' "${{1//\//_}}"; }}

case "$1 $2" in
  "pr list")
    [ "$#" -eq 12 ] && [ "$3" = --head ] && [ "$5" = --base ] && [ "$6" = main ] \\
      && [ "$7" = --state ] && [ "$8" = merged ] && [ "$9" = --json ] && [ "${{10}}" = number ] \\
      && [ "${{11}}" = --jq ] && [ "${{12}}" = '.[].number' ] || unexpected
    [ ! -e "$fixtures/gh-fails" ] || {{ echo "gh double: could not reach GitHub" >&2; exit 1; }}
    name="$(flat "$4")"
    [ ! -x "$fixtures/on-list/$name" ] || "$fixtures/on-list/$name" >&2
    [ ! -e "$fixtures/merged/$name" ] || /bin/cat -- "$fixtures/merged/$name"
    exit 0 ;;
  "pr view")
    [ "$#" -eq 7 ] && [ "$4" = --json ] && [ "$6" = --jq ] || unexpected
    case "$5 $7" in
      "state .state") echo OPEN ;;
      "baseRefName .baseRefName") echo main ;;
      "headRefOid .headRefOid") /bin/cat -- "$fixtures/head" ;;
      *) unexpected ;;
    esac
    exit 0 ;;
  "pr merge")
    [ "$#" -eq 10 ] && [ "$4" = --merge ] && [ "$5" = --match-head-commit ] \\
      && [ "$7" = --subject ] && [ "$9" = --body ] || unexpected
    [ "$6" = "$(/bin/cat -- "$fixtures/head")" ] || {{ echo "gh double: head moved" >&2; exit 1; }}
    git -C "$server" fetch -q origin || exit 1
    git -C "$server" checkout -q --detach origin/main || exit 1
    git -C "$server" merge -q --no-ff -m "$8" -m "${{10}}" "$6" || exit 1
    git -C "$server" push -q origin HEAD:refs/heads/main || exit 1
    /bin/cat -- "$fixtures/number" > "$fixtures/merged/$(flat "$(/bin/cat -- "$fixtures/head-name")")"
    exit 0 ;;
  *) unexpected ;;
esac
"""


class Case(unittest.TestCase):
    """One throwaway origin and clone, with the roadmap's inputs committed on main."""

    def setUp(self):
        scratch = tempfile.TemporaryDirectory(prefix="prune-branches-")
        self.addCleanup(scratch.cleanup)
        self.scratch = pathlib.Path(scratch.name).resolve()
        self.bin = self.scratch / "bin"
        self.log = self.scratch / "log"
        self.fixtures = self.scratch / "fixtures"
        for directory in (self.bin, self.log, self.fixtures / "merged", self.fixtures / "on-list",
                          self.scratch / "home"):
            directory.mkdir(parents=True)
        for tool in TOOLS:
            found = shutil.which(tool)
            self.assertIsNotNone(found, f"{tool} is not on PATH")
            (self.bin / tool).symlink_to(found)
        (self.bin / "python3").symlink_to(sys.executable)

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
        self.server = self.scratch / "server"
        self.git("init", "-q", "--bare", str(self.origin), cwd=self.scratch)
        self.git("clone", "-q", str(self.origin), str(self.work), cwd=self.scratch)
        for name in ROADMAP_FILES:
            target = self.work / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "Import the plan and the roadmap")
        self.git("push", "-q", "origin", "HEAD:refs/heads/main")
        self.git("clone", "-q", str(self.origin), str(self.server), cwd=self.scratch)

        gh = self.bin / "gh"
        gh.write_text(GH_DOUBLE.format(log=shlex.quote(str(self.log)),
                                       fixtures=shlex.quote(str(self.fixtures)),
                                       server=shlex.quote(str(self.server))))
        gh.chmod(0o755)

    # --- building a history ------------------------------------------------------------------

    def git(self, *args, cwd=None):
        done = subprocess.run(["git", *args], cwd=cwd or self.work, env=self.env,
                              capture_output=True, text=True, check=False)
        self.assertEqual(0, done.returncode, f"git {' '.join(args)}: {done.stderr}")
        return done.stdout.strip()

    def commit(self, branch, path, text=None, start="main", push=True):
        """A commit on `branch`, made from `start` when the branch is new. Returns its sha."""
        if not self.git("branch", "--list", branch):
            self.git("branch", branch, start)
        self.git("checkout", "-q", branch)
        (self.work / path).parent.mkdir(parents=True, exist_ok=True)
        (self.work / path).write_text(text if text is not None else f"{branch} {path}\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", f"{branch}: {path}")
        if push:
            self.git("push", "-q", "origin", f"{branch}:refs/heads/{branch}")
        self.git("checkout", "-q", "--detach", "main")
        return self.git("rev-parse", branch)

    def land(self, subject, branch=None):
        """Merge `branch` into main with `subject`, or commit the subject alone, and push main."""
        self.git("checkout", "-q", "main")
        if branch:
            self.git("merge", "-q", "--no-ff", "-m", subject, branch)
        else:
            self.git("commit", "-q", "--allow-empty", "-m", subject)
        self.git("push", "-q", "origin", "main")
        self.git("checkout", "-q", "--detach", "main")

    def merged_pull_request(self, branch, number="47"):
        (self.fixtures / "merged" / branch.replace("/", "_")).write_text(f"{number}\n")

    # --- reading the result ------------------------------------------------------------------

    def prune(self, *args, cwd=None, **env):
        done = subprocess.run([sys.executable, "-I", str(SCRIPT), *args], cwd=cwd or self.work,
                              env={**self.env, **env}, capture_output=True, text=True, check=False)
        return done.returncode, done.stdout, done.stderr

    def restore_lines(self, output):
        """The commands under the script's "To restore one" heading, without their indent."""
        return [line[2:] for line in output.splitlines() if line.startswith("  git ")]

    def restore_files(self):
        """What pruned-branches/ in the clone's common git directory holds, found from the clone
        itself rather than from anything a script says (#115)."""
        directory = self.work / ".git" / "pruned-branches"
        return sorted(directory.iterdir()) if directory.exists() else []

    def named_file(self, output):
        """The path the output's last line ends with, resolved."""
        return pathlib.Path(output.splitlines()[-1].split()[-1]).resolve()

    def tips(self):
        """{(where, branch): tip} for every branch here and on origin."""
        found = {}
        for line in self.git("for-each-ref", "--format=%(refname:strip=2) %(objectname)",
                             "refs/heads").splitlines():
            branch, tip = line.split()
            found[("local", branch)] = tip
        for line in self.git("ls-remote", "--heads", "origin").splitlines():
            tip, ref = line.split("\t")
            found[("origin", ref[len("refs/heads/"):])] = tip
        return found

    def assert_each_line_restores_one_copy(self, lines, before):
        """Run in turn, each line brings back exactly one copy missing from `before`, at the tip it
        had there: judged by what the line does, not by what it says."""
        for line in lines:
            with self.subTest(line=line):
                missing = set(before) - set(self.tips())
                ran = subprocess.run(["/bin/sh", "-c", line], cwd=self.work, env=self.env,
                                     capture_output=True, text=True, check=False)
                self.assertEqual(0, ran.returncode, ran.stderr)
                now = self.tips()
                back = [copy for copy in missing if copy in now]
                self.assertEqual(1, len(back), f"{line} brought back {back}")
                self.assertEqual(before[back[0]], now[back[0]], f"{line} is not at its old tip")

    def block_restore_files(self):
        """A file where pruned-branches/ goes, so the cleanup cannot write a restore line."""
        (self.work / ".git" / "pruned-branches").write_text("in the way\n")

    def verdicts(self, output):
        """{(branch, where): (verdict, reason)} from the script's lines."""
        found = {}
        for line in output.splitlines():
            parts = line.split(None, 4)
            if len(parts) == 5 and parts[0] in VERDICTS:
                verdict, where, branch, _sha, reason = parts
                found[(branch, where)] = (verdict, reason)
        return found

    def local(self):
        return set(self.git("for-each-ref", "--format=%(refname:strip=2)", "refs/heads").split())

    def remote(self):
        listed = self.git("ls-remote", "--heads", "origin")
        return {line.split("\t")[1][len("refs/heads/"):] for line in listed.splitlines()}

    def calls(self):
        recorded = []
        n = 0
        while (self.log / f"call-{n}").exists():
            recorded.append((self.log / f"call-{n}").read_bytes().split(b"\0")[:-1])
            n += 1
        return [[part.decode() for part in call] for call in recorded]


class ARowsStateDecidesItsClaim(Case):
    """A claim branch is finished when the id its name carries reads as landed in roadmap.py --json."""

    def test_a_landed_rows_claim_is_listed_for_deletion_and_an_open_rows_claim_is_kept(self):
        self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        self.commit("task/2.2-hosting", "hosting.txt")

        code, out, err = self.prune()

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out)
        self.assertEqual("delete", verdicts[("task/2.1-registry", "local")][0], out)
        self.assertEqual("delete", verdicts[("task/2.1-registry", "origin")][0], out)
        self.assertEqual("keep", verdicts[("task/2.2-hosting", "local")][0], out)
        self.assertIn("2.2 is open", verdicts[("task/2.2-hosting", "local")][1])
        # Without --delete nothing is deleted.
        self.assertIn("task/2.1-registry", self.local())
        self.assertIn("task/2.1-registry", self.remote())
        self.assertEqual([], self.calls())

    def test_delete_removes_a_landed_rows_claim_locally_and_on_origin_and_keeps_a_part_landed_one(self):
        tip = self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        self.commit("task/3.1-scheme", "scheme.txt")
        self.land("Merge part of task 3.1: the scheme, first half", "task/3.1-scheme")

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out)
        self.assertEqual("deleted", verdicts[("task/2.1-registry", "local")][0], out)
        self.assertEqual("deleted", verdicts[("task/2.1-registry", "origin")][0], out)
        self.assertNotIn("task/2.1-registry", self.local())
        self.assertNotIn("task/2.1-registry", self.remote())
        # The claim is how the next session knows the row is taken.
        self.assertEqual(("kept", "3.1 is part-landed, so the branch is its claim"),
                         verdicts[("task/3.1-scheme", "local")])
        self.assertEqual("kept", verdicts[("task/3.1-scheme", "origin")][0])
        self.assertIn("task/3.1-scheme", self.local())
        self.assertIn("task/3.1-scheme", self.remote())
        self.assertIn("prune_branches: deleted 2 and kept 2.", out)
        self.assertIn(f"  git branch task/2.1-registry {tip}\n", out)
        self.assertIn(f"  git push origin {tip}:refs/heads/task/2.1-registry\n", out)


class EveryIdFormReadsItsOwnState(Case):
    """Each id a claim branch's name carries, read the way roadmap.py --json reports it."""

    def test_splits_tickets_paired_rows_and_names_the_roadmap_cannot_place(self):
        landed = {
            "task/2.1-registry": "Merge task 2.1: the registry",
            "task/2.1b-follow-up": "Merge part of task 2.1b: a follow-up of a landed row",
            "task/3.0a-harness": "Merge task 3.0a: one half of the spike",
            "task/issue-66-land-tests": "Merge task issue-66: land.sh's companion",
            "task/issue-67-review": "Merge part of task issue-67: review before merge",
            "task/1.2-and-1.5-network-and-bucket": "Merge task 1.2: the network",
            "task/i-3-abstractions": "Merge task I-3: the abstractions",
            "task/6.8-runbooks": "Merge task 6.8: the runbooks so far",
            "task/ticket-71-a05": "Merge pull request #45 from adammedford/task/ticket-71-a05",
            "task/2.7a-hosting-names-its-stages": "Merge part of task 2.7a: an archived ticket's id",
            # The way 3.0a and 3.0b landed: inside the merge that landed their row whole.
            "task/3.2a-landed-with-its-row": "Merge task 3.2: the options, the split's work in it",
        }
        for branch, subject in landed.items():
            self.commit(branch, branch.replace("/", "_") + ".txt")
            self.land(subject, branch)
        self.land("Merge part of task 1.5: the bucket, first half")
        for branch in ("task/2.1c-fresh-follow-up", "task/issue-90-not-yet", "task/9.9-nothing",
                       "task/p2-1a-a-split-of-a-phase-2-row", "task/Issue-91-upper-case"):
            self.commit(branch, branch.replace("/", "_") + ".txt")

        code, out, err = self.prune()

        self.assertEqual(0, code, err)
        expected = {
            "task/2.1-registry": ("delete", "2.1 is landed"),
            # 2.1 landed, yet its split's own merge said "part of": a live claim under a done row.
            "task/2.1b-follow-up": ("keep", "2.1b is part-landed, so the branch is its claim"),
            # A split no subject names reads as its row, and a fresh follow-up claim of a landed
            # row is kept by the work on it that main lacks.
            "task/2.1c-fresh-follow-up": (
                "keep",
                "2.1c (as its row 2.1) is landed, and 1 commit on it has no equivalent on origin/main",
            ),
            # Its row landed whole with its work inside: a leftover, which the one-off pass clears.
            "task/3.2a-landed-with-its-row": ("delete", "3.2a (as its row 3.2) is landed"),
            "task/3.0a-harness": ("delete", "3.0a is landed"),
            "task/issue-66-land-tests": ("delete", "issue-66 is landed"),
            "task/issue-67-review": ("keep", "issue-67 is part-landed, so the branch is its claim"),
            "task/issue-90-not-yet": ("keep", "issue-90 is open, so the branch is its claim"),
            # Read in any case, as tools/roadmap.py reads a ticket id.
            "task/Issue-91-upper-case": ("keep", "issue-91 is open, so the branch is its claim"),
            "task/1.2-and-1.5-network-and-bucket":
                ("keep", "1.5 is part-landed, so the branch is its claim"),
            "task/i-3-abstractions": ("delete", "I-3 is landed"),
            "task/6.8-runbooks": ("keep", "6.8 is ongoing, so the branch is its claim"),
            "task/p2-1a-a-split-of-a-phase-2-row":
                ("keep", "P2-1a (as its row P2-1) is open, so the branch is its claim"),
            # An id that names no row, split or ticket is no claim a session could take: since
            # ADR-0020 claims name a plan row, a split of one or an issue, so such a name is left
            # over from the archived tracker. Its work alone decides it.
            "task/ticket-71-a05": ("delete", "its name carries no task id, so it claims nothing"),
            "task/2.7a-hosting-names-its-stages":
                ("delete", "2.7a names no plan row, split or ticket, so it claims nothing"),
            "task/9.9-nothing": (
                "keep",
                "9.9 names no plan row, split or ticket, so it claims nothing a session could take, "
                "and 1 commit on it has no equivalent on origin/main",
            ),
        }
        verdicts = self.verdicts(out)
        for branch, (word, reason) in expected.items():
            with self.subTest(branch=branch):
                verdict, said = verdicts[(branch, "local")]
                self.assertEqual(word, verdict, out)
                self.assertTrue(said.startswith(reason), said)


class ATipsWorkMustBeOnMain(Case):
    """A branch goes only when its current tip's work is on main: as an ancestor, or as commits
    with patch-equivalents there. Anything else is listed and kept."""

    def test_work_added_after_an_earlier_merge_keeps_the_branch(self):
        self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        self.commit("task/2.1-registry", "afterwards.txt")

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out)
        for where in ("local", "origin"):
            verdict, reason = verdicts[("task/2.1-registry", where)]
            self.assertEqual("kept", verdict, out)
            self.assertIn("1 commit on it has no equivalent on origin/main", reason)
        self.assertIn("task/2.1-registry", self.local())
        self.assertIn("task/2.1-registry", self.remote())

    def test_a_branch_whose_rebased_copy_landed_is_on_main_by_patch_equivalence(self):
        # The way overnight/2026-10-16 came to look unmerged: the work reached main as a rebased
        # copy, with new commits and the same patches, and the branch stayed at its old tip.
        self.commit("task/2.1-registry", "registry.txt")
        self.commit("task/2.1-registry", "schema.txt")
        self.land("Merge task 2.2: hosting")
        self.git("branch", "rebased", "task/2.1-registry")
        self.git("rebase", "-q", "--force-rebase", "main", "rebased")
        self.land("Merge task 2.1: the registry", "rebased")
        self.git("branch", "-D", "rebased")
        tip = self.git("rev-parse", "task/2.1-registry")
        self.assertNotEqual(
            0, subprocess.run(["git", "merge-base", "--is-ancestor", tip, "main"], cwd=self.work,
                              env=self.env, check=False).returncode,
            "the premise: the rebased tip is not an ancestor of main",
        )

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out)
        for where in ("local", "origin"):
            verdict, reason = verdicts[("task/2.1-registry", where)]
            self.assertEqual("deleted", verdict, out)
            self.assertIn("every commit on it has an equivalent on origin/main", reason)
        self.assertNotIn("task/2.1-registry", self.local())
        self.assertNotIn("task/2.1-registry", self.remote())


class AMergesOwnChangesMustBeOnMainToo(Case):
    """`git cherry` leaves merge commits out, so a resolution made in a merge is read on its own."""

    def build(self, resolution):
        """task/2.1-registry merges a side branch, adding `resolution` in the merge if given; main
        receives both ordinary commits as cherry-picks, and the row lands. Returns the tip."""
        first = self.commit("task/2.1-registry", "registry.txt", push=False)
        side = self.commit("side", "side.txt", push=False)
        self.git("checkout", "-q", "task/2.1-registry")
        self.git("merge", "-q", "--no-ff", "--no-commit", "side")
        if resolution:
            (self.work / "resolution.txt").write_text(resolution)
            self.git("add", "resolution.txt")
        self.git("commit", "-q", "-m", "Merge side into task/2.1-registry")
        self.git("push", "-q", "origin", "task/2.1-registry")
        # Main moves first: a cherry-pick onto the same parent within the same second makes the very
        # same commit object, which would put the branch's own commits on main.
        self.land("Merge task 2.2: hosting")
        self.git("checkout", "-q", "main")
        self.git("cherry-pick", first, side)
        self.git("push", "-q", "origin", "main")
        self.land("Merge task 2.1: the registry")
        tip = self.git("rev-parse", "task/2.1-registry")
        marks = [line.split()[0] for line in self.git("cherry", "main", tip).splitlines()]
        self.assertEqual(["-", "-"], marks,
                         "the premise: both ordinary commits have an equivalent on main")
        return tip

    def test_a_merge_with_changes_of_its_own_keeps_the_branch(self):
        tip = self.build("made only in the merge\n")

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out)
        for where in ("local", "origin"):
            verdict, reason = verdicts[("task/2.1-registry", where)]
            self.assertEqual("kept", verdict, out)
            self.assertIn(f"merge {tip[:12]} has changes of its own", reason)
        self.assertIn("task/2.1-registry", self.local())
        self.assertIn("task/2.1-registry", self.remote())

    def test_a_git_that_cannot_re_make_the_merge_keeps_the_branch_and_says_why(self):
        # A git older than 2.38 has no `merge-tree --write-tree`, and exits 129 on it, which is
        # neither the clean merge nor the conflict the check reads.
        self.build(None)
        real = os.readlink(self.bin / "git")
        (self.bin / "git").unlink()
        (self.bin / "git").write_text(
            "#!/bin/bash\n"
            "[ \"$1\" = merge-tree ] && { echo 'usage: git merge-tree <branch1> <branch2>' >&2; "
            "exit 129; }\n"
            f"exec {shlex.quote(real)} \"$@\"\n"
        )
        (self.bin / "git").chmod(0o755)

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        for where in ("local", "origin"):
            verdict, reason = self.verdicts(out)[("task/2.1-registry", where)]
            self.assertEqual("kept", verdict, out)
            self.assertIn("git could not re-make merge", reason)
            self.assertIn("needs git 2.38", reason)
        self.assertIn("task/2.1-registry", self.local())
        self.assertIn("task/2.1-registry", self.remote())

    def test_a_merge_git_would_make_on_its_own_leaves_nothing_behind(self):
        self.build(None)

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out)
        for where in ("local", "origin"):
            self.assertEqual("deleted", verdicts[("task/2.1-registry", where)][0], out)
        self.assertNotIn("task/2.1-registry", self.local())


class AMovedRemoteTipIsPreserved(Case):
    """Remote deletion checks that the tip has not changed since it was judged.

    Someone pushes to the branch while the cleanup runs, from a pre-push hook in the clone: it runs
    inside a `git push` the cleanup makes, after that push has read origin's refs and before it sends
    the delete. Its own push skips hooks, or it would run itself, and it fires once.
    """

    def move_on_first_push(self, branch):
        moved = self.commit("elsewhere", "more-work.txt", push=False)
        marker = shlex.quote(str(self.scratch / "moved"))
        hook = self.work / ".git" / "hooks" / "pre-push"
        hook.write_text(
            "#!/bin/bash\n"
            f"[ -e {marker} ] && exit 0\n"
            f": > {marker}\n"
            f"exec git push --no-verify -q --force origin {moved}:refs/heads/{branch}\n"
        )
        hook.chmod(0o755)
        return moved

    def assert_preserved(self, out, branch, moved):
        self.assertTrue((self.scratch / "moved").exists(), "the premise: the hook ran")
        verdict, reason = self.verdicts(out)[(branch, "origin")]
        self.assertEqual("kept", verdict, out)
        self.assertIn("origin refused the delete", reason)
        self.assertEqual(f"{moved}\trefs/heads/{branch}",
                         self.git("ls-remote", "origin", f"refs/heads/{branch}"))
        # The copy here is judged by its own tip, which is on main.
        self.assertEqual("deleted", self.verdicts(out)[(branch, "local")][0])

    def test_a_push_after_the_cleanup_read_origin_and_before_its_delete_preserves_the_branch(self):
        # The lease's own window. task/1.8 sorts first, so its delete is the first push, and the
        # hook moves task/2.1 then: after the cleanup read origin's tips, before task/2.1's push
        # reads them again. Only the lease, naming the tip that was judged, tells the two apart.
        for branch, row in (("task/1.8-first", "1.8"), ("task/2.1-registry", "2.1")):
            self.commit(branch, f"{row}.txt")
            self.land(f"Merge task {row}: {branch}", branch)
        moved = self.move_on_first_push("task/2.1-registry")

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        self.assertEqual("deleted", self.verdicts(out)[("task/1.8-first", "origin")][0], out)
        self.assert_preserved(out, "task/2.1-registry", moved)

    def test_a_push_landing_while_the_delete_is_in_flight_preserves_the_branch(self):
        # The later window, which git itself closes: a push sends the tip it read as the ref's
        # expected old value, and origin refuses a ref that is no longer there.
        self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        moved = self.move_on_first_push("task/2.1-registry")

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        self.assert_preserved(out, "task/2.1-registry", moved)


class AnOvernightBranchGoesAfterItsPullRequestMerges(Case):
    """After its pull request merges, an overnight branch is deleted only if its current tip's work
    is on main and no worktree has it; otherwise it is listed and kept."""

    NIGHT = "overnight/2026-10-17"
    LISTING = ["pr", "list", "--head", NIGHT, "--base", "main", "--state", "merged",
               "--json", "number", "--jq", ".[].number"]

    def night(self):
        self.commit(self.NIGHT, "lane.txt")
        self.land("Merge the 2026-10-17 overnight run: one lane", self.NIGHT)

    def test_a_merged_pull_requests_branch_whose_work_is_on_main_is_deleted(self):
        self.night()
        self.merged_pull_request(self.NIGHT, "47")

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out)
        for where in ("local", "origin"):
            verdict, reason = verdicts[(self.NIGHT, where)]
            self.assertEqual("deleted", verdict, out)
            self.assertIn("pull request #47 from it merged", reason)
        self.assertNotIn(self.NIGHT, self.local())
        self.assertNotIn(self.NIGHT, self.remote())
        # One question to GitHub for the name, whichever copies it has.
        self.assertEqual([self.LISTING], self.calls())

    def test_a_branch_with_no_merged_pull_request_is_kept_though_its_tip_is_on_main(self):
        # A night that has only just begun forks from main, so its tip is on main and its work is
        # not done: the pull request is what says it is.
        self.git("branch", self.NIGHT, "main")
        self.git("push", "-q", "origin", self.NIGHT)

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        for where in ("local", "origin"):
            self.assertEqual(("kept", "no pull request from it into main has merged"),
                             self.verdicts(out)[(self.NIGHT, where)])
        self.assertIn(self.NIGHT, self.local())
        self.assertIn(self.NIGHT, self.remote())

    def test_work_after_the_merge_or_a_worktree_or_no_answer_from_gh_keeps_it(self):
        cases = {
            "work after the merge": "1 commit on it has no equivalent on origin/main",
            "a worktree": "it is checked out in the worktree at",
            "gh cannot answer": "gh could not say whether a pull request from it merged",
        }
        for case, reason in cases.items():
            with self.subTest(case=case):
                self.setUp()
                self.night()
                self.merged_pull_request(self.NIGHT)
                if case == "work after the merge":
                    self.commit(self.NIGHT, "late-lane.txt")
                elif case == "a worktree":
                    self.git("worktree", "add", "-q", str(self.scratch / "night"), self.NIGHT)
                else:
                    (self.fixtures / "gh-fails").write_text("")

                code, out, err = self.prune("--delete")

                self.assertEqual(0, code, err)
                for where in ("local", "origin"):
                    verdict, said = self.verdicts(out)[(self.NIGHT, where)]
                    self.assertEqual("kept", verdict, out)
                    self.assertIn(reason, said)
                self.assertIn(self.NIGHT, self.local())
                self.assertIn(self.NIGHT, self.remote())

    def test_a_concurrent_push_to_origin_preserves_the_branch_there(self):
        self.night()
        self.merged_pull_request(self.NIGHT)
        moved = self.commit("elsewhere", "late.txt", push=False)
        # The push lands while the cleanup asks GitHub about the pull request: after it read
        # origin's tip, before it deletes.
        script = self.fixtures / "on-list" / self.NIGHT.replace("/", "_")
        script.write_text(
            f"#!/bin/bash\nexec git push --no-verify -q --force origin {moved}:refs/heads/{self.NIGHT}\n"
        )
        script.chmod(0o755)

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        verdict, reason = self.verdicts(out)[(self.NIGHT, "origin")]
        self.assertEqual("kept", verdict, out)
        self.assertIn("origin refused the delete", reason)
        self.assertEqual(f"{moved}\trefs/heads/{self.NIGHT}",
                         self.git("ls-remote", "origin", f"refs/heads/{self.NIGHT}"))


class ItRefusesWhereItCannotDecide(Case):
    """Where the answer would rest on a wrong reading, it deletes nothing and exits non-zero."""

    def test_a_head_with_merges_main_lacks_is_refused(self):
        # A merge subject only this checkout has would read as landed: a claim branch forked from
        # main for that row is on main, and would go.
        self.git("branch", "task/2.2-hosting", "main")
        self.git("checkout", "-q", "-b", "integration", "main")
        self.git("commit", "-q", "--allow-empty", "-m", "Merge task 2.2: only on this branch")

        code, out, err = self.prune("--delete")

        self.assertEqual(1, code, out)
        self.assertEqual("", out)
        self.assertIn("HEAD has commits origin/main lacks", err)
        self.assertIn("task/2.2-hosting", self.local())

    def test_a_roadmap_that_gives_no_state_is_refused(self):
        self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        roadmap = self.work / "docs" / "roadmap.md"
        roadmap.write_text(roadmap.read_text() + "\nCites `9.9`, which no plan row is.\n")
        self.git("commit", "-q", "-am", "Cite a row the plan lacks")
        self.git("push", "-q", "origin", "HEAD:main")

        code, out, err = self.prune("--delete")

        self.assertEqual(1, code, out)
        self.assertEqual("", out)
        self.assertIn("tools/roadmap.py --json gave no state", err)
        self.assertIn("task/2.1-registry", self.local())
        self.assertIn("task/2.1-registry", self.remote())

    def test_a_roadmap_from_before_splits_and_tickets_is_refused(self):
        self.commit("task/2.1b-follow-up", "follow-up.txt")
        self.land("Merge task 2.1b: a follow-up", "task/2.1b-follow-up")
        (self.work / "tools" / "roadmap.py").write_text(
            'import json\nprint(json.dumps({"closed_gates": [], "rows": {"2.1": "landed"}}))\n'
        )
        self.git("commit", "-q", "-am", "A roadmap.py that knows rows only")
        self.git("push", "-q", "origin", "HEAD:main")

        code, out, err = self.prune("--delete")

        self.assertEqual(1, code, out)
        self.assertEqual("", out)
        self.assertIn("reports no splits or tickets, so it predates #71", err)
        self.assertIn("task/2.1b-follow-up", self.local())

    def test_an_unknown_argument_deletes_nothing(self):
        self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")

        code, out, err = self.prune("--delet")

        self.assertEqual(2, code, out)
        self.assertEqual("", out)
        self.assertIn("task/2.1-registry", self.local())
        self.assertIn("task/2.1-registry", self.remote())


class AWorktreeKeepsItsBranch(Case):
    """Branches checked out in any worktree are listed and kept; the cleanup never removes one."""

    def test_a_finished_branch_checked_out_in_a_worktree_is_kept_locally_and_on_origin(self):
        self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        tree = self.scratch / "spa-2.1"
        self.git("worktree", "add", "-q", str(tree), "task/2.1-registry")

        for args, word in (((), "keep"), (("--delete",), "kept")):
            with self.subTest(args=args):
                code, out, err = self.prune(*args)
                self.assertEqual(0, code, err)
                verdicts = self.verdicts(out)
                for where in ("local", "origin"):
                    verdict, reason = verdicts[("task/2.1-registry", where)]
                    self.assertEqual(word, verdict, out)
                    self.assertIn(f"checked out in the worktree at {tree}", reason)

        self.assertIn("task/2.1-registry", self.local())
        self.assertIn("task/2.1-registry", self.remote())
        self.assertTrue((tree / "registry.txt").exists())

    def test_a_worktree_rebasing_a_finished_branch_keeps_it_here_and_on_origin(self):
        # Mid-rebase, the porcelain listing calls the worktree detached and names no branch, so only
        # git's own refusal at `git branch -D` sees the worktree; origin's copy is kept with it.
        tip = self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        tree = self.scratch / "spa-2.1"
        self.git("worktree", "add", "-q", str(tree), "task/2.1-registry")
        stopped = subprocess.run(["git", "rebase", "-q", "--exec", "false", "HEAD~1"], cwd=tree,
                                 env=self.env, capture_output=True, text=True, check=False)
        self.assertNotEqual(0, stopped.returncode, "the premise: the rebase stops")
        listing = self.git("worktree", "list", "--porcelain")
        self.assertNotIn("branch refs/heads/task/2.1-registry", listing,
                         "the premise: the listing names no branch for a worktree mid-rebase")
        self.assertEqual(tip, self.git("rev-parse", "task/2.1-registry"))

        code, out, err = self.prune("--delete")

        self.assertEqual(0, code, err)
        verdicts = self.verdicts(out)
        verdict, reason = verdicts[("task/2.1-registry", "local")]
        self.assertEqual("kept", verdict, out)
        self.assertIn("used by worktree", reason)
        self.assertEqual(("kept", "the copy here was not deleted, so origin's is kept with it"),
                         verdicts[("task/2.1-registry", "origin")])
        self.assertIn("task/2.1-registry", self.local())
        self.assertIn("task/2.1-registry", self.remote())
        self.assertIn("prune_branches: deleted 0 and kept 2.", out)


class ADeletionLeavesItsRestoreCommandsInAFile(Case):
    """When --delete deletes anything, the restore lines it prints are written to
    pruned-branches/<UTC date>-<12-hex HEAD sha>.txt under the repository's common git directory,
    and the path is printed (#115). On 18 October 2026 a landing deleted 34 branches, its session
    read land.sh through `tail -20`, and 14 restore lines were lost with the terminal.

    The file is found here from the clone's own .git, not from anything the script says, and every
    run is made from a linked worktree, as tools/land.sh's header asks: there the worktree's own git
    directory is not the common one, so a file in the wrong one is seen.
    """

    def utc_today(self):
        return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")

    def far_from_utc(self):
        """A POSIX TZ whose local date is not today's UTC date: twelve hours behind UTC before
        noon, fourteen ahead after it. A file named by the local date is then misnamed whatever the
        hour, on any machine. In TZ a positive offset is west of Greenwich:
        https://pubs.opengroup.org/onlinepubs/9799919799/basedefs/V1_chap08.html"""
        zone = "AOE12" if datetime.datetime.now(datetime.timezone.utc).hour < 12 else "LINT-14"
        # Asked of an interpreter given that TZ, as the script is: the date it would use.
        local = subprocess.run([sys.executable, "-I", "-c",
                                "import datetime; print(datetime.date.today().isoformat())"],
                               env={**self.env, "TZ": zone}, capture_output=True, text=True,
                               check=True).stdout.strip()
        self.assertNotEqual(self.utc_today(), local, "the premise: the local date is not UTC's")
        return zone

    def clear_of_utc_midnight(self, seconds=30):
        """Waits out the last `seconds` of a UTC day, so that runs made next share one date and so
        one file."""
        now = datetime.datetime.now(datetime.timezone.utc)
        left = 24 * 3600 - (now.hour * 3600 + now.minute * 60 + now.second)
        if left <= seconds:
            time.sleep(left + 1)

    def linked_worktree(self):
        tree = self.scratch / "spa-prune"
        self.git("worktree", "add", "-q", "--detach", str(tree), "main")
        self.assertNotEqual(self.git("rev-parse", "--absolute-git-dir", cwd=tree),
                            str(self.work / ".git"),
                            "the premise: a linked worktree has a git directory of its own")
        return tree

    def test_the_file_holds_exactly_the_printed_lines_and_each_recreates_a_copy_at_its_tip(self):
        self.commit("task/2.1-registry", "registry.txt")
        hosting = self.commit("task/2.2-hosting", "hosting.txt", push=False)
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        # 2.2 lands as a rebased copy, so its tip is on main only as patch-equivalents and exists
        # nowhere but in the branch: two of the 34 tips the 18 October landing deleted were so.
        self.git("branch", "rebased", "task/2.2-hosting")
        self.git("rebase", "-q", "--force-rebase", "main", "rebased")
        self.land("Merge task 2.2: hosting, rebased", "rebased")
        self.git("branch", "-D", "rebased")
        self.assertNotEqual(
            0, subprocess.run(["git", "merge-base", "--is-ancestor", hosting, "main"],
                              cwd=self.work, env=self.env, check=False).returncode,
            "the premise: 2.2's tip is not an ancestor of main",
        )
        self.commit("task/3.1-scheme", "scheme.txt")
        tree = self.linked_worktree()
        head = self.git("rev-parse", "HEAD", cwd=tree)
        zone = self.far_from_utc()
        dates = {self.utc_today()}
        before = self.tips()

        code, out, err = self.prune("--delete", cwd=tree, TZ=zone)

        dates.add(self.utc_today())
        self.assertEqual(0, code, err)
        deleted = sorted(set(before) - set(self.tips()))
        self.assertEqual([("local", "task/2.1-registry"), ("local", "task/2.2-hosting"),
                          ("origin", "task/2.1-registry")], deleted,
                         "the premise: both copies of 2.1 and the one copy of 2.2 go, 3.1 stays")
        files = self.restore_files()
        self.assertEqual(1, len(files), out)
        self.assertIn(files[0].name, {f"{date}-{head[:12]}.txt" for date in dates})
        printed = self.restore_lines(out)
        self.assertEqual("".join(f"{line}\n" for line in printed), files[0].read_text(), out)
        # The path, whole, as the last line says it.
        self.assertTrue(os.path.isabs(out.splitlines()[-1].split()[-1]), out)
        self.assertEqual(files[0], self.named_file(out))

        lines = files[0].read_text().splitlines()
        self.assertEqual(len(deleted), len(lines), lines)
        self.assert_each_line_restores_one_copy(lines, before)
        self.assertEqual(before, self.tips())

    def test_a_dry_run_or_a_run_that_deletes_nothing_writes_no_file(self):
        self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        tree = self.linked_worktree()

        code, out, err = self.prune(cwd=tree)

        self.assertEqual(0, code, err)
        self.assertIn("--delete would delete 2 and keep 0", out, "the premise: there is work to do")
        self.assertEqual([], self.restore_files(), out)
        self.assertNotIn("pruned-branches", out)

        # A worktree on the finished branch keeps both copies, so --delete deletes nothing.
        self.git("worktree", "add", "-q", str(self.scratch / "spa-2.1"), "task/2.1-registry")

        code, out, err = self.prune("--delete", cwd=tree)

        self.assertEqual(0, code, err)
        self.assertIn("deleted 0 and kept 2.", out, "the premise: nothing is deleted")
        self.assertEqual([], self.restore_files(), out)
        self.assertNotIn("pruned-branches", out)

    def test_a_later_run_at_the_same_head_adds_its_lines_after_the_earlier_runs(self):
        registry = self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        hosting = self.commit("task/2.2-hosting", "hosting.txt", push=False)
        self.land("Merge task 2.2: hosting", "task/2.2-hosting")
        tree = self.linked_worktree()
        # The first run keeps 2.2 for its worktree; the second, once that is gone, deletes it.
        hold = self.scratch / "spa-2.2"
        self.git("worktree", "add", "-q", str(hold), "task/2.2-hosting")
        self.clear_of_utc_midnight()

        code, first, err = self.prune("--delete", cwd=tree)
        self.assertEqual(0, code, err)
        self.git("worktree", "remove", str(hold))
        code, second, err = self.prune("--delete", cwd=tree)
        self.assertEqual(0, code, err)

        files = self.restore_files()
        self.assertEqual(1, len(files), first + second)
        self.assertEqual([f"git branch task/2.1-registry {registry}",
                          f"git push origin {registry}:refs/heads/task/2.1-registry",
                          f"git branch task/2.2-hosting {hosting}"],
                         files[0].read_text().splitlines())
        for out in (first, second):
            self.assertEqual(files[0], self.named_file(out))

    def test_a_file_it_cannot_write_stops_the_deleting_and_its_lines_are_still_printed(self):
        # The lines must outlive the terminal, so a run that cannot write them deletes nothing more
        # once it finds out, and prints every line it has, the one it could not write among them.
        self.commit("task/2.1-registry", "registry.txt")
        self.land("Merge task 2.1: the registry", "task/2.1-registry")
        tree = self.linked_worktree()
        before = self.tips()
        self.block_restore_files()

        code, out, err = self.prune("--delete", cwd=tree)

        self.assertEqual(1, code, out)
        self.assertIn("could not write", err)
        self.assertEqual(("kept", "the restore lines cannot be written, so nothing more goes"),
                         self.verdicts(out)[("task/2.1-registry", "origin")])
        self.assertEqual([("local", "task/2.1-registry")], sorted(set(before) - set(self.tips())),
                         "the premise: the local copy, judged first, went before the write failed")
        self.assertNotIn("pruned-branches", out)
        lines = self.restore_lines(out)
        self.assertEqual(1, len(lines), out)
        self.assert_each_line_restores_one_copy(lines, before)
        self.assertEqual(before, self.tips())


class LandingDeletesWhatItFinished(Case):
    """tools/land.sh, once its merge is in and the roadmap refreshed, runs the cleanup.

    The real script, under /bin/bash, as tools/land_tests.py runs it, but past the merge: here the
    gh double's merge really merges, into the case's origin from a clone standing in for GitHub's
    side. --unreviewed spares the review records, which tools/land_tests.py covers.
    """

    def land_pull_request(self, branch, subject):
        (self.fixtures / "head").write_text(self.git("rev-parse", branch) + "\n")
        (self.fixtures / "head-name").write_text(branch)
        (self.fixtures / "number").write_text("5\n")
        self.git("checkout", "-q", "--detach", "origin/main")
        done = subprocess.run(
            [BASH, str(LAND), "5", "--subject", subject,
             "--unreviewed", "a case of tools/prune_branches_tests.py"],
            cwd=self.work, env=self.env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, check=False,
        )
        # One stream, in the order a terminal shows it, so its last line is the one a session
        # reading `tools/land.sh ... 2>&1 | tail -1` would see.
        self.assertEqual(0, done.returncode, done.stdout)
        self.assertIn(subject, self.git("log", "-3", "--format=%s", "origin/main"))
        return done.stdout

    def assert_the_last_line_names_the_restore_file(self, out):
        """land.sh's own last line names the file of the cleanup's restore lines (#115)."""
        files = self.restore_files()
        self.assertEqual(1, len(files), out)
        self.assertTrue(files[0].name.endswith(f"-{self.git('rev-parse', 'HEAD')[:12]}.txt"),
                        files[0].name)
        self.assertEqual(self.restore_lines(out), files[0].read_text().splitlines())
        self.assertTrue(out.splitlines()[-1].startswith("land: "), out)
        self.assertEqual(files[0], self.named_file(out), out)

    def test_landing_a_row_as_done_deletes_its_claim_and_keeps_a_part_landed_rows(self):
        self.commit("task/3.1-scheme", "scheme.txt")
        self.land("Merge part of task 3.1: the scheme, first half", "task/3.1-scheme")
        self.commit("task/2.1-registry", "registry.txt")

        out = self.land_pull_request("task/2.1-registry", "Merge task 2.1: the registry")

        self.assertNotIn("task/2.1-registry", self.local(), out)
        self.assertNotIn("task/2.1-registry", self.remote(), out)
        self.assertIn("task/3.1-scheme", self.local(), out)
        self.assertIn("task/3.1-scheme", self.remote(), out)
        self.assertIn("3.1 is part-landed, so the branch is its claim", out)
        self.assert_the_last_line_names_the_restore_file(out)

    def test_a_part_landed_rows_claim_survives_its_own_landing(self):
        self.commit("task/3.1-scheme", "scheme.txt")

        out = self.land_pull_request("task/3.1-scheme", "Merge part of task 3.1: the first half")

        self.assertIn("task/3.1-scheme", self.local(), out)
        self.assertIn("task/3.1-scheme", self.remote(), out)
        self.assertIn("3.1 is part-landed, so the branch is its claim", out)
        # A landing whose cleanup deleted nothing writes no file and names none.
        self.assertEqual([], self.restore_files(), out)
        self.assertNotIn("pruned-branches", out)

    def test_landing_an_overnight_pull_request_deletes_its_branch(self):
        self.commit("overnight/2026-10-17", "lane.txt")

        out = self.land_pull_request("overnight/2026-10-17",
                                     "Merge the 2026-10-17 overnight run: one lane")

        self.assertNotIn("overnight/2026-10-17", self.local(), out)
        self.assertNotIn("overnight/2026-10-17", self.remote(), out)
        self.assertIn("pull request #5 from it merged", out)
        self.assert_the_last_line_names_the_restore_file(out)

    def test_a_landing_that_moves_no_generated_block_names_the_file_too(self):
        # land.sh's other way out: the blocks are current before the merge, and an overnight run's
        # merge names no row, so there is no refresh to push.
        self.git("checkout", "-q", "main")
        subprocess.run([sys.executable, "-I", "tools/roadmap.py"], cwd=self.work, env=self.env,
                       capture_output=True, check=True)
        self.git("commit", "-q", "-am", "Refresh the generated roadmap blocks")
        self.git("push", "-q", "origin", "main")
        self.git("checkout", "-q", "--detach", "main")
        self.commit("overnight/2026-10-17", "lane.txt")

        out = self.land_pull_request("overnight/2026-10-17",
                                     "Merge the 2026-10-17 overnight run: one lane")

        self.assertIn("land: the generated blocks did not move", out, "the premise: no refresh")
        self.assertNotIn("overnight/2026-10-17", self.remote(), "the premise: the cleanup deleted")
        self.assert_the_last_line_names_the_restore_file(out)

    def test_a_cleanup_that_cannot_write_its_file_prints_its_lines_and_the_landing_says_it_stopped(
            self):
        self.commit("task/2.1-registry", "registry.txt")
        self.block_restore_files()

        out = self.land_pull_request("task/2.1-registry", "Merge task 2.1: the registry")

        self.assertNotIn("task/2.1-registry", self.local(), out)
        self.assertIn("task/2.1-registry", self.remote(), out)
        self.assertEqual(1, len(self.restore_lines(out)), out)
        # The last line is the warning's, which names no file.
        self.assertTrue(out.splitlines()[-1].startswith("      tools/prune_branches.py --delete"),
                        out)


def require_bash():
    wanted = os.environ.get(REQUIRE_BASH)
    version = subprocess.run([BASH, "-c", "echo $BASH_VERSION"], capture_output=True, text=True,
                             check=False).stdout.strip()
    print(f"prune_branches_tests: {BASH} is bash {version}", file=sys.stderr)
    if wanted and not version.startswith(wanted + "."):
        print(f"prune_branches_tests: {REQUIRE_BASH}={wanted}, and {BASH} is {version}.",
              file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    require_bash()
    unittest.main()
