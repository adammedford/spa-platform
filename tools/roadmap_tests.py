#!/usr/bin/env python3
"""Regressions for PR #14: derived task state and mandatory generated blocks; and for #70: the JSON
mode prints the derivation the blocks are written from. Owner: integrator."""

import contextlib
import importlib.util
import io
import json
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
        # lacks the subjects, and a ladder that does not line up with the plan has no row count.
        shallow = patch.object(roadmap, "shallow", return_value=True)
        with self.subTest(case="shallow clone"), shallow:
            self.assertEqual((1, ""), self.invoke(["--json"]))
            self.assertIn("shallow clone", self.errors.getvalue())
        with self.subTest(case="a cited row the plan lacks"):
            path = self.root / roadmap.ROADMAP
            path.write_text(path.read_text() + "\nCites `9.9`.\n")
            self.assertEqual((1, ""), self.invoke(["--json"]))
            self.assertIn("'9.9'", self.errors.getvalue())


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


if __name__ == "__main__":
    unittest.main()
