#!/usr/bin/env python3
"""Regressions for PR #14: derived task state and mandatory generated blocks. Owner: integrator."""

import contextlib
import importlib.util
import io
import pathlib
import shutil
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


class GeneratedBlockTests(unittest.TestCase):
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
        stack.enter_context(patch.object(roadmap, "subjects", return_value=[]))
        stack.enter_context(patch.object(roadmap, "tags", return_value=set()))
        stack.enter_context(patch.object(roadmap, "shallow", return_value=False))
        self.assertEqual(0, self.invoke([])[0])

    def invoke(self, arguments):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = roadmap.main(arguments)
        return code, output.getvalue()

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


if __name__ == "__main__":
    unittest.main()
