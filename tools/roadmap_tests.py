#!/usr/bin/env python3
"""Regressions for PR #14: derived task state and mandatory generated blocks; and for #70: the JSON
mode prints the derivation the blocks are written from; and for #290: the check refuses a row
merged both whole and in part. Owner: integrator."""

import contextlib
import importlib.util
import io
import json
import os
import pathlib
import re
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("roadmap", ROOT / "tools/roadmap.py")
roadmap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(roadmap)


class MergeSubjectTests(unittest.TestCase):
    def test_github_merges_preserve_suffix_rows_and_recognize_integrator_and_split_ids(self):
        rows = roadmap.plan_rows(ROOT)
        cases = {"1.6a": ("1.6a", "landed"), "1.6b": ("1.6b", "landed"),
                 "3.0a": ("3.0", "part"), "i-3": ("I-3", "landed"),
                 "p2-1": ("P2-1", "landed")}
        for branch_id, (row, state) in cases.items():
            with self.subTest(branch_id=branch_id):
                self.assertIn(row, rows)
                subject = f"Merge pull request #17 from adam/task/{branch_id}-example"
                with patch.object(roadmap, "subjects", return_value=[subject]):
                    self.assertEqual({row: state}, roadmap.landed(ROOT, rows))

    def test_partial_subject_does_not_overwrite_an_existing_full_landing(self):
        subjects = ["Merge part of task 1.6a: follow-up", "Merge task 1.6a: first landing"]
        with patch.object(roadmap, "subjects", return_value=subjects):
            self.assertEqual({"1.6a": "landed"}, roadmap.landed(ROOT, roadmap.plan_rows(ROOT)))


class Documents(unittest.TestCase):
    """Copies of the documents the script reads and writes, with git's answers fixed.

    SUBJECTS and TAGS stand in for `git log` and `git tag`, so a case states the history it is
    about instead of inheriting whatever this clone holds.
    """

    SUBJECTS: list = []
    TAGS: set = set()

    def setUp(self):
        # Exercise the real main/check path against copies of the documents it guards.
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.root = pathlib.Path(self.scratch.name)
        for name in ("README.md", roadmap.ROADMAP, roadmap.PLAN_TASKS, roadmap.PLAN_PHASE2):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        stack = contextlib.ExitStack()
        self.addCleanup(stack.close)
        stack.enter_context(patch.object(roadmap, "__file__", str(self.root / "tools/roadmap.py")))
        stack.enter_context(patch.object(roadmap, "subjects", return_value=self.SUBJECTS))
        stack.enter_context(patch.object(roadmap, "tags", return_value=self.TAGS))
        stack.enter_context(patch.object(roadmap, "shallow", return_value=False))
        self.assertEqual(0, self.invoke([])[0])

    def invoke(self, arguments):
        """Run the script; return its exit code and stdout, and keep stderr in self.errors."""
        output, self.errors = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(self.errors):
            code = roadmap.main(arguments)
        return code, output.getvalue()

    def block(self, name, which):
        """The lines between one BEGIN/END marker pair, as the last run wrote them."""
        text = (self.root / name).read_text()
        begin, end = (roadmap.MARKER.format(edge, which) for edge in ("BEGIN", "END"))
        return text.split(begin, 1)[1].split(end, 1)[0].strip().splitlines()


class GeneratedBlockTests(Documents):
    def test_current_blocks_pass_check(self):
        self.assertEqual(0, self.invoke(["--check"])[0])

    def test_removing_both_markers_cannot_turn_stale_summary_into_accepted_prose(self):
        path = self.root / "README.md"
        text = path.read_text()
        for edge in ("BEGIN", "END"):
            text = text.replace(roadmap.MARKER.format(edge, "summary"), "")
        text = text.replace("**Plan rows landed:**", "**Plan rows landed:** STALE 999")
        path.write_text(text)
        self.assertEqual(1, self.invoke(["--check"])[0])
        self.assertEqual(1, self.invoke([])[0])

    def test_every_expected_pair_is_required_even_when_both_markers_are_absent(self):
        targets = {"README.md": ("summary",), roadmap.ROADMAP: ("stages",)}
        for name, blocks in targets.items():
            path = self.root / name
            original = path.read_text()
            for block in blocks:
                with self.subTest(file=name, block=block):
                    text = original
                    for edge in ("BEGIN", "END"):
                        text = text.replace(roadmap.MARKER.format(edge, block), "")
                    path.write_text(text)
                    self.assertEqual(1, self.invoke(["--check"])[0])
                    path.write_text(original)

    def test_a_missing_single_marker_or_duplicate_pair_is_rejected(self):
        path = self.root / "README.md"
        original = path.read_text()
        begin = roadmap.MARKER.format("BEGIN", "summary")
        end = roadmap.MARKER.format("END", "summary")
        for text in (original.replace(begin, ""), original.replace(end, ""),
                     original + "\n" + begin + "\n" + end):
            with self.subTest(text=text[-100:]):
                path.write_text(text)
                self.assertEqual(1, self.invoke(["--check"])[0])
        path.write_text(original)


class JsonModeTests(Documents):
    """#70: a program asks the script how far the work has got, and gets the blocks' own answer.

    The history below reaches every way a row comes to be counted, or not: a task merge, GitHub's
    merge subject, an integrator row committed straight to the trunk, a "part of" merge, a split
    merging on its own, and a row of SX, the stage with no gate, landing by its subject.
    """

    SUBJECTS = [
        "Merge task 2.1: registry validator, schema check and per-entry fingerprint",
        "Merge pull request #17 from adam/task/i-3-abstractions",
        "I-1: import the skeleton",
        "Merge part of task 1.3: the cell's network",
        "Merge task 3.0a: one half of the G0 spike",
        "I-7: decide change request 1.6a and record the platform tag",
    ]
    TAGS = {"skeleton-v1", "g0-mechanism"}

    def derived(self):
        code, output = self.invoke(["--json"])
        self.assertEqual(0, code, output)
        return json.loads(output)

    def test_the_landed_rows_are_exactly_the_rows_the_generated_blocks_count_as_landed(self):
        rows = self.derived()["rows"]
        from_json = {row for row, state in rows.items() if state == "landed"}

        # Read the stages block back as a reader would: a stage's landed rows are its plan rows
        # less the ones its last column still owes, and that must come to the count beside them.
        stage_rows = {stage.id: stage.rows for stage in roadmap.ladder(self.root)[0]}
        from_blocks = set()
        for line in self.block(roadmap.ROADMAP, "stages")[2:]:
            title, _, count, owes = (cell.strip() for cell in line.strip().strip("|").split("|"))
            if count == "ongoing":
                continue
            owed = re.findall(r"`([^`]+)`", owes)
            landed = [row for row in stage_rows[title.split()[0]] if row not in owed]
            self.assertEqual(int(count.split()[0]), len(landed), line)
            from_blocks |= set(landed)
        self.assertEqual(from_blocks, from_json)

        summary = " ".join(self.block("README.md", "summary"))
        total = re.search(r"\*\*Plan rows landed:\*\* (\d+) of", summary)
        self.assertEqual(int(total.group(1)), len(from_json))

        # The history above, read the way GLOSSARY.md defines landed and part-landed.
        self.assertEqual({"2.1", "I-3", "I-1"}, from_json)
        self.assertEqual("part-landed", rows["1.3"])
        self.assertEqual("part-landed", rows["3.0"])
        self.assertEqual("ongoing", rows["I-7"])
        self.assertEqual("ongoing", rows["6.8"])
        self.assertEqual("open", rows["6.7"])
        self.assertEqual(roadmap.plan_rows(self.root), list(rows))

    def test_the_closed_gates_are_the_ones_the_summary_names(self):
        gates = self.derived()["closed_gates"]
        summary = " ".join(self.block("README.md", "summary"))
        named = re.search(r"\*\*Closed gates:\*\* (.*?)\.\s\*\*", summary).group(1)
        self.assertEqual(
            [{"gate": "G1a", "tag": "skeleton-v1"}, {"gate": "G0", "tag": "g0-mechanism"}], gates
        )
        self.assertEqual(", ".join(f"{g['gate']} (`{g['tag']}`)" for g in gates), named)

    def test_no_state_is_printed_when_it_would_not_be_whole(self):
        # A program reads stdout as the answer, so a partial one must not reach it: a shallow clone
        # lacks the subjects, a git that fails returns none, and a ladder that does not line up with
        # the plan has no row count.
        shallow = patch.object(roadmap, "shallow", return_value=True)
        with self.subTest(case="shallow clone"), shallow:
            self.assertEqual((1, ""), self.invoke(["--json"]))
            self.assertIn("shallow clone", self.errors.getvalue())
        silent = patch.object(roadmap, "subjects", return_value=[])
        with self.subTest(case="git returned no history"), silent:
            self.assertEqual((1, ""), self.invoke(["--json"]))
            self.assertIn("no commit subjects", self.errors.getvalue())
        with self.subTest(case="a cited row the plan lacks"):
            path = self.root / roadmap.ROADMAP
            path.write_text(path.read_text() + "\nCites `9.9`.\n")
            self.assertEqual((1, ""), self.invoke(["--json"]))
            self.assertIn("'9.9'", self.errors.getvalue())


class SplitsAndTicketsTests(Documents):
    """#71: a split of a row and a ticket each read by their own merge subjects.

    tools/prune_branches.py deletes a claim branch only when the id its name carries is done, and a
    split's plan row cannot say: 2.1 landed whole, and then a follow-up split of it, 2.1b, merged
    "part of", so 2.1b's claim is live under a landed row. A ticket is no plan row at all. So --json
    reports each by the subjects naming it, in the forms docs/agents/landing-work.md fixes.
    """

    SUBJECTS = [
        "Merge part of task 2.1b: the validator reports a timeout above the maximum",
        "Merge task 2.1: registry validator, schema check and per-entry fingerprint",
        "Merge task 3.0a: one half of the G0 spike",
        "Merge pull request #17 from adam/task/1.4a-cell-ecs-terraform",
        "Merge part of task issue-67: land.sh merges only a reviewed head",
        "Merge task issue-66: land.sh's test companion runs the real script",
        "Merge task Issue-70: the rest of the JSON mode",
        "Merge part of task issue-70: tools/roadmap.py --json",
        "Merge tasks issue-68 and issue-69: two tickets in one merge",
        "Task issue-72: a branch's own commit, which lands nothing",
        "Merge part of task 6.8a: a split of the stage with no gate",
    ]

    def derived(self):
        code, output = self.invoke(["--json"])
        self.assertEqual(0, code, self.errors.getvalue())
        return json.loads(output)

    def test_a_split_reads_by_its_own_subjects_whatever_its_row_says(self):
        derived = self.derived()
        self.assertEqual(
            {"2.1b": "part-landed", "3.0a": "landed", "1.4a": "landed", "6.8a": "part-landed"},
            derived["splits"],
        )
        # In the plan's order of the rows they split, as the rows are listed.
        self.assertEqual(["1.4a", "2.1b", "3.0a", "6.8a"], list(derived["splits"]))
        # The rows themselves read as they always have: a split never lands its row.
        self.assertEqual("landed", derived["rows"]["2.1"])
        self.assertEqual("part-landed", derived["rows"]["3.0"])
        self.assertEqual("part-landed", derived["rows"]["1.4"])

    def test_a_ticket_reads_by_its_own_subjects_and_never_reaches_the_rows(self):
        derived = self.derived()
        self.assertEqual(
            ["issue-66", "issue-67", "issue-68", "issue-69", "issue-70"], list(derived["tickets"])
        )
        self.assertEqual(
            {"issue-66": "landed", "issue-67": "part-landed", "issue-68": "landed",
             "issue-69": "landed", "issue-70": "landed"},
            derived["tickets"],
        )
        self.assertEqual(roadmap.plan_rows(self.root), list(derived["rows"]))
        self.assertNotIn("issue-66", derived["rows"])


class LandedInsideAMergeTests(unittest.TestCase):
    """#70: rows that landed inside a merge whose own subject names no id read as landed.

    A rule that reads first-parent subjects only -- the overnight run's collect step kept one --
    sends rows like these back out for work. Held against this clone's real history, because the
    hazard is in how the history is read: the lists are literals, read from
    `git log <merge>^1..<merge>` on 18 October 2026, and a row once landed never becomes unlanded,
    so they stay true as main grows. CI's documents job checks out the whole history
    (`fetch-depth: 0`); a shallow clone fails here rather than passing on nothing.
    """

    LANDED = {
        # Merge pull request #9 from adammedford/overnight/2026-10-13
        "11c1864ce2192186df99d12f32e70cab73878020": [
            "1.8", "2.1", "2.2", "2.3", "2.4", "2.5", "2.6",
            "4.3", "4.4", "4.5", "4.6", "5.1", "5.2",
        ],
        # Merge reviewed identity components and signed-in verification
        "a00b13111176b5a9768cda0f8322504cfe81d098": [
            "3.1", "3.2", "3.3", "3.4", "3.5", "3.6", "3.7",
        ],
    }
    # Merge overnight carved workstreams: shared rule ids and bounded proofs. It merged splits only
    # (1.4a, 2.3b, 4.3b, 6.2a, 6.3a), and a split never lands its row, so these read as part-landed
    # at least: never open.
    TOUCHED = {"fb4899ae4e10a0eb3dcec5e982b61dd716ebf07c": ["1.4", "2.3", "4.3", "6.2", "6.3"]}

    def test_rows_merged_beneath_a_subject_that_names_no_id_read_as_landed(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = roadmap.main(["--json"])
        self.assertEqual(0, code, "the JSON mode needs the whole history; is this a shallow clone?")
        rows = json.loads(output.getvalue())["rows"]

        for merge, expected in {**self.LANDED, **self.TOUCHED}.items():
            with self.subTest(merge=merge[:7]):
                # The premise: the merge is in this history, and its own subject lands nothing.
                ancestor = subprocess.run(
                    ["git", "merge-base", "--is-ancestor", merge, "HEAD"],
                    cwd=ROOT, capture_output=True, check=False,
                )
                self.assertEqual(0, ancestor.returncode, f"{merge[:7]} is not an ancestor of HEAD")
                subject = roadmap.run(["git", "log", "-1", "--format=%s", merge], ROOT).strip()
                self.assertTrue(subject, f"{merge[:7]} is not in this clone")
                with patch.object(roadmap, "subjects", return_value=[subject]):
                    self.assertEqual({}, roadmap.landed(ROOT, roadmap.plan_rows(ROOT)), subject)

                if merge in self.LANDED:
                    self.assertEqual({row: "landed" for row in expected},
                                     {row: rows[row] for row in expected})
                else:
                    self.assertNotIn("open", [rows[row] for row in expected])


class MergedBothWaysTests(unittest.TestCase):
    """#290: --check refuses a history that both completes a row and leaves it unfinished.

    #287's run branch was rebuilt so row 6.7's merge said "Merge part of task 6.7" (fedb593), but
    the old "Merge task 6.7" (032eb43) was still reachable through the second parents of the rebuilt
    lane merges. Every subject in the ancestry counts, and one full landing marks a row done for
    good, so --check passed and the roadmap said S7 owed nothing. These cases build those histories
    in a scratch repository, with origin/main at its first commit, and run the real script on them.
    """

    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.root = pathlib.Path(self.scratch.name)
        for name in ("README.md", roadmap.ROADMAP, roadmap.PLAN_TASKS, roadmap.PLAN_PHASE2):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        stack = contextlib.ExitStack()
        self.addCleanup(stack.close)
        # Under the pre-push hook git exports GIT_DIR and friends, which would point every git call
        # here, the script's own included, at this clone rather than the scratch repository.
        environment = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        environment.update(GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@example.invalid",
                           GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@example.invalid")
        stack.enter_context(patch.dict(os.environ, environment, clear=True))
        stack.enter_context(patch.object(roadmap, "__file__", str(self.root / "tools/roadmap.py")))
        stack.enter_context(patch.object(roadmap, "tags", return_value=set()))
        # A branch named neither main nor master, so the trunk is the origin/main set below or none.
        self.git("init", "--quiet", "--initial-branch=scratch")
        self.git("add", "--all")
        self.base = self.commit("I-1: import the skeleton", [], tree=self.git("write-tree"))
        self.git("update-ref", "refs/remotes/origin/main", self.base)

    def git(self, *arguments):
        done = subprocess.run(["git", "-c", "commit.gpgsign=false", *arguments], cwd=self.root,
                              capture_output=True, text=True, check=True)
        return done.stdout.strip()

    def commit(self, subject, parents, tree=None):
        tree = tree or self.git("rev-parse", f"{self.base}^{{tree}}")
        flags = [flag for sha in parents for flag in ("-p", sha)]
        return self.git("commit-tree", tree, *flags, "-m", subject)

    def head(self, sha):
        self.git("update-ref", "HEAD", sha)

    def merge(self, subject, into, *parents):
        """A merge of a task branch with one commit of its own, as a lane makes it."""
        work = self.commit(subject.replace("Merge part of task", "Task").replace("Merge task", "Task"),
                           [into])
        return self.commit(subject, [into, work, *parents])

    def checked(self):
        """Write the blocks for the history as it stands, then run --check on it."""
        written, output = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(written), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(0, roadmap.main([]), written.getvalue())
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
            code = roadmap.main(["--check"])
        return code, output.getvalue()

    def test_287s_second_head_a_part_merge_on_the_first_parent_and_a_full_one_beside_it_is_red(self):
        # The old lane merge, from before the rebuild, and the rebuilt one that says "part of".
        old = self.merge("Merge task 6.7: operating notes", self.base)
        rebuilt = self.merge("Merge part of task 6.7: operating notes", self.base)
        # The next lane's merge, rebuilt too, whose second parent carries the old chain in.
        self.head(self.merge("Merge task issue-999: another lane", rebuilt, old))

        code, output = self.checked()
        self.assertEqual(1, code, output)
        self.assertIn("6.7", output)
        self.assertIn(old[:7], output)
        self.assertIn(rebuilt[:7], output)

    def test_287s_third_head_the_part_merge_alone_passes(self):
        rebuilt = self.merge("Merge part of task 6.7: operating notes", self.base)
        self.head(self.merge("Merge task issue-999: another lane", rebuilt))

        code, output = self.checked()
        self.assertEqual(0, code, output)
        text = (self.root / roadmap.ROADMAP).read_text()
        block = text.split(roadmap.MARKER.format("BEGIN", "stages"), 1)[1]
        block = block.split(roadmap.MARKER.format("END", "stages"), 1)[0]
        s7 = [line for line in block.splitlines() if line.startswith("| S7 ")]
        self.assertEqual(1, len(s7))
        self.assertIn("part-landed: `6.7`", s7[0])

    def test_a_row_the_trunk_lists_as_landed_is_not_held_by_a_later_part_follow_up(self):
        # The 2.1 / 2.1b shape (#71): 2.1 landed whole on the trunk, then follow-ups said "part of".
        trunk = self.merge("Merge task 2.1: registry validator", self.base)
        self.git("update-ref", "refs/remotes/origin/main", trunk)
        split = self.merge("Merge part of task 2.1b: the validator reports a timeout", trunk)
        self.head(self.merge("Merge part of task 2.1: a follow-up to the landed row", split))

        self.assertEqual(0, self.checked()[0])

    def test_a_split_merged_in_part_beside_its_row_merged_whole_is_no_contradiction(self):
        whole = self.merge("Merge task 2.1: registry validator", self.base)
        self.head(self.merge("Merge part of task 2.1b: the validator reports a timeout", whole))

        self.assertEqual(0, self.checked()[0])

    def test_with_no_trunk_to_compare_against_the_check_says_so(self):
        self.git("update-ref", "-d", "refs/remotes/origin/main")
        old = self.merge("Merge task 6.7: operating notes", self.base)
        self.head(self.merge("Merge part of task 6.7: operating notes", old))

        code, output = self.checked()
        self.assertEqual(0, code, output)
        self.assertIn("no merge compared", output)

    def test_a_row_the_trunk_lists_as_part_landed_can_be_finished_by_the_branch(self):
        trunk = self.merge("Merge part of task 6.7: operating notes", self.base)
        self.git("update-ref", "refs/remotes/origin/main", trunk)
        self.head(self.merge("Merge task 6.7: the rest of the notes", trunk))

        self.assertEqual(0, self.checked()[0])


if __name__ == "__main__":
    unittest.main()
