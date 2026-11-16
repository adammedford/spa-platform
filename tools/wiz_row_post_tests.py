#!/usr/bin/env python3
"""Test companion for tools/wiz_row_post.py, the draft of a continuity row's post. Owner: integrator.

Run: python3 -I tools/wiz_row_post_tests.py

Built in #1204. Sitting D of 2026-11-14 wrote each of rows 3 to 9's posts on #348 by hand: an inline
Python read of `row<n>.json`, a `shasum` of the bundle's files and a thirty-line heredoc, with the
session ids taken out by hand. Each case here writes a bundle of fixture files in a throwaway
directory, in the shape the sitting leaves it (`continuity/row<n>.json`, `results-row<n>/*.junit.xml`,
`row<n>-test.out`), runs the real script under `python3 -I` and compares its output with text written
out by hand. No case needs a cell.

**The evidence file's shape is read from the code that writes it.** `RowEvidence` in
tests/e2e/Stack/ContinuityEvidence.cs names the members and `Verdict` in ContinuityObserver.cs the
verdicts; `EvidenceShapeIsTheDriversOwn` holds the fixtures to both, so a renamed member turns a case
red here rather than a post quietly blank (CODING_STANDARDS.md, "A guard reads the thing it guards").

The task ids and session refs below are made up for the cases.
"""
from __future__ import annotations

import hashlib
import json
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "wiz_row_post.py"
EVIDENCE_CS = ROOT / "tests" / "e2e" / "Stack" / "ContinuityEvidence.cs"
OBSERVER_CS = ROOT / "tests" / "e2e" / "Stack" / "ContinuityObserver.cs"

OLD = "33dad938113140c4ae3f27ec3e1ea9f7"
NEW = "16d997825b494de0917d82235c195ad7"
REF = "a1b2c3d4e5f6"
REF2 = "0f9e8d7c6b5a"


def evidence(row: str, verdict: str, reason: str, observations: list[str], attempts: int = 1,
             wall: float = 91.25) -> dict:
    return {
        "Row": row,
        "Attempts": attempts,
        "WallSeconds": wall,
        "Verdict": verdict,
        "Reason": reason,
        "Observations": observations,
        "Steps": [{"Name": "session-1", "TraceId": "00-aa-bb-01", "Status": 200}],
        "Page": None,
    }


class Bundle:
    """A throwaway bundle directory holding one row's three files."""

    def __init__(self, n: int, doc: dict | str | None, junit: bool = True, output: bool = True) -> None:
        self.dir = tempfile.TemporaryDirectory()
        self.path = pathlib.Path(self.dir.name)
        self.n = n
        (self.path / "continuity").mkdir()
        if doc is not None:
            text = doc if isinstance(doc, str) else json.dumps(doc, indent=2)
            (self.path / "continuity" / f"row{n}.json").write_text(text, encoding="utf-8")
        if junit:
            (self.path / f"results-row{n}").mkdir()
            (self.path / f"results-row{n}" / "adam_mac_2026-11-15_13_59_51.junit.xml").write_text(
                '<testsuites><testsuite tests="1" failures="0"/></testsuites>\n', encoding="utf-8")
        if output:
            (self.path / f"row{n}-test.out").write_text("Test run summary: Passed!\n", encoding="utf-8")

    def sha(self, relative: str) -> str:
        return hashlib.sha256((self.path / relative).read_bytes()).hexdigest()

    def close(self) -> None:
        self.dir.cleanup()


def run(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, "-I", str(SCRIPT), *args], capture_output=True, text=True,
                          timeout=60, check=False)


class Case(unittest.TestCase):
    def bundle(self, n: int, doc, **kwargs) -> Bundle:
        made = Bundle(n, doc, **kwargs)
        self.addCleanup(made.close)
        return made

    def post(self, made: Bundle) -> str:
        done = run(str(made.n), "--bundle", str(made.path))
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stderr, "")
        return done.stdout

    def bundle_lines(self, made: Bundle) -> str:
        junit = f"results-row{made.n}/adam_mac_2026-11-15_13_59_51.junit.xml"
        return (
            "Bundle (not uploaded; SHA-256):\n"
            "\n"
            f"- `continuity/row{made.n}.json` {made.sha(f'continuity/row{made.n}.json')}\n"
            f"- `{junit}` {made.sha(junit)}\n"
            f"- `row{made.n}-test.out` {made.sha(f'row{made.n}-test.out')}\n"
        )


class Drafts(Case):
    def test_a_passed_row_gives_its_verdict_reason_attempts_wall_time_observations_and_hashes(self) -> None:
        made = self.bundle(3, evidence("row3", "Passed", "each pair crossed tasks within 1 attempts",
                                       [f"attempt 1: session-1 on task {OLD}, session-2 on task {NEW}: crossed"]))
        self.assertEqual(self.post(made), (
            "## D3, row 3: passed\n"
            "\n"
            "- Evidence file verdict: `Passed`, \"each pair crossed tasks within 1 attempts\".\n"
            "- Attempts spent: 1. Wall time: 91.2 s.\n"
            "\n"
            "Observations:\n"
            "\n"
            f"- \"attempt 1: session-1 on task {OLD}, session-2 on task {NEW}: crossed\"\n"
            "\n"
            + self.bundle_lines(made)
        ))

    def test_a_failed_row_says_failed_in_its_heading(self) -> None:
        made = self.bundle(6, evidence("row6", "Failed", "session 2: me answered 401", [], attempts=1, wall=300))
        post = self.post(made)
        self.assertEqual(post.splitlines()[0], "## D6, row 6: failed")
        self.assertNotIn("passed", post.splitlines()[0])

    def test_a_not_proved_row_says_not_proved_in_its_heading(self) -> None:
        made = self.bundle(5, evidence("row5", "NotProved", "no attempt was made", [], attempts=0, wall=0))
        post = self.post(made)
        self.assertEqual(post.splitlines()[0], "## D5, row 5: not proved")
        self.assertIn("- Evidence file verdict: `NotProved`, \"no attempt was made\".\n", post)
        self.assertIn("Observations: none.\n", post)

    def test_a_verdict_it_does_not_know_is_refused_and_never_drafted_as_passed(self) -> None:
        made = self.bundle(4, evidence("row4", "passed", "lower case is not the driver's", []))
        done = run("4", "--bundle", str(made.path))
        self.assertEqual(done.returncode, 2)
        self.assertEqual(done.stdout, "")
        self.assertIn("verdict", done.stderr)


class LeavesOutSessionIds(Case):
    def test_the_session_ref_in_a_reason_is_left_out(self) -> None:
        made = self.bundle(6, evidence(
            "row6", "Passed",
            f"session {REF} refreshed once, inside the interval, and no call was refused",
            [f"old tasks {OLD}; new tasks {NEW}", "interval 2026-11-15T16:16:12Z to 2026-11-15T16:18:31Z"]))
        post = self.post(made)
        self.assertNotIn(REF, post)
        self.assertIn("\"session refreshed once, inside the interval, and no call was refused\"", post)
        self.assertIn(f"- \"old tasks {OLD}; new tasks {NEW}\"\n", post)

    def test_a_list_of_session_refs_in_an_observation_is_left_out(self) -> None:
        made = self.bundle(8, evidence(
            "row8", "Failed", f"session {REF2} has 2 Refreshed lines",
            [f"50 of 50 calls answered 200; 25 on task {OLD}, 25 on task {NEW}; session {REF},{REF2} with 0 Refreshed lines"]))
        post = self.post(made)
        self.assertNotIn(REF, post)
        self.assertNotIn(REF2, post)
        self.assertIn(f"25 on task {OLD}, 25 on task {NEW}; session with 0 Refreshed lines", post)
        self.assertIn("\"session has 2 Refreshed lines\"", post)

    def test_a_task_id_is_kept_whole(self) -> None:
        made = self.bundle(9, evidence("row9", "Passed", "each pair crossed tasks within 2 attempts",
                                       [f"attempt 2: logout on task {OLD}, replay on task {NEW}: crossed"]))
        post = self.post(made)
        self.assertIn(OLD, post)
        self.assertIn(NEW, post)

    def test_a_longer_hex_word_after_session_is_not_a_session_ref(self) -> None:
        made = self.bundle(9, evidence("row9", "Passed", f"session {OLD} kept", []))
        self.assertIn(f"\"session {OLD} kept\"", self.post(made))

    def test_an_account_id_or_arn_in_a_reason_is_redacted(self) -> None:
        made = self.bundle(7, evidence(
            "row7", "NotProved",
            "the driver stopped with AmazonECSException: arn:aws:ecs:us-east-2:123456789012:task/spa-poc/x",
            []))
        post = self.post(made)
        self.assertNotIn("123456789012", post)
        self.assertNotIn("arn:aws", post)
        self.assertIn("task/spa-poc/x", post)


class Refuses(Case):
    def assertRefused(self, done: subprocess.CompletedProcess, says: str) -> None:
        self.assertEqual(done.returncode, 2, done.stderr)
        self.assertEqual(done.stdout, "")
        self.assertIn(says, done.stderr)

    def test_a_missing_evidence_file(self) -> None:
        made = self.bundle(3, None)
        self.assertRefused(run("3", "--bundle", str(made.path)), "row3.json")

    def test_a_missing_junit_report(self) -> None:
        made = self.bundle(3, evidence("row3", "Passed", "x", []), junit=False)
        self.assertRefused(run("3", "--bundle", str(made.path)), "results-row3")

    def test_two_junit_reports(self) -> None:
        made = self.bundle(3, evidence("row3", "Passed", "x", []))
        (made.path / "results-row3" / "second.junit.xml").write_text("<testsuites/>\n", encoding="utf-8")
        self.assertRefused(run("3", "--bundle", str(made.path)), "--junit")

    def test_a_missing_test_output(self) -> None:
        made = self.bundle(3, evidence("row3", "Passed", "x", []), output=False)
        self.assertRefused(run("3", "--bundle", str(made.path)), "row3-test.out")

    def test_an_evidence_file_of_another_row(self) -> None:
        made = self.bundle(3, evidence("row4", "Passed", "x", []))
        self.assertRefused(run("3", "--bundle", str(made.path)), "row4")

    def test_an_evidence_file_that_is_not_json(self) -> None:
        made = self.bundle(3, "{ not json")
        self.assertRefused(run("3", "--bundle", str(made.path)), "JSON")

    def test_a_row_outside_three_to_nine(self) -> None:
        made = self.bundle(2, evidence("row2", "Passed", "x", []))
        self.assertRefused(run("2", "--bundle", str(made.path)), "3 to 9")


class NamedPaths(Case):
    def test_named_paths_replace_the_bundle_s_and_are_shown_by_name_outside_it(self) -> None:
        made = self.bundle(3, evidence("row3", "Passed", "x", []))
        other = self.bundle(3, evidence("row3", "Failed", "y", []))
        junit = other.path / "results-row3" / "adam_mac_2026-11-15_13_59_51.junit.xml"
        done = run("3", "--bundle", str(made.path), "--evidence", str(other.path / "continuity" / "row3.json"),
                   "--junit", str(junit))
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines()[0], "## D3, row 3: failed")
        self.assertIn(f"- `row3.json` {other.sha('continuity/row3.json')}\n", done.stdout)
        self.assertIn(f"- `{junit.name}` {other.sha('results-row3/' + junit.name)}\n", done.stdout)
        self.assertIn(f"- `row3-test.out` {made.sha('row3-test.out')}\n", done.stdout)
        self.assertNotIn(str(other.path), done.stdout)


class EvidenceShapeIsTheDriversOwn(unittest.TestCase):
    def test_the_fixture_members_are_row_evidence_s_parameters(self) -> None:
        source = EVIDENCE_CS.read_text(encoding="utf-8")
        record = re.search(r"public sealed record RowEvidence\((.*?)\);", source, re.S)
        self.assertIsNotNone(record, "RowEvidence's declaration was not found")
        names = re.findall(r"(\w+)\s*(?:=\s*null)?\s*(?:,|$)", record.group(1).strip())
        self.assertEqual(sorted(names), sorted(evidence("row3", "Passed", "x", []).keys()))

    def test_the_verdicts_are_the_observer_s_enum(self) -> None:
        source = OBSERVER_CS.read_text(encoding="utf-8")
        body = re.search(r"public enum Verdict\s*\{(.*?)\}", source, re.S)
        self.assertIsNotNone(body, "Verdict's declaration was not found")
        self.assertEqual(re.findall(r"\w+", body.group(1)), ["Passed", "Failed", "NotProved"])
        self.assertIn("JsonStringEnumConverter<Verdict>", source, "the verdict is written by name")


if __name__ == "__main__":
    unittest.main()
