#!/usr/bin/env python3
"""Test companion for tools/retro_record.py, the retro workflow's record reader.

Owner: integrator. Specified in #77 -> "The record reader's companion"; built under #260.

**The seam is the command the retro's agents run.** Every case starts the real file as
`python3 -I tools/retro_record.py <command> ...`, on files this companion writes into a throwaway
directory or on JSON it hands over standard input, and asserts on the JSON the reader prints and its
exit status. Nothing is imported, so no case reaches a part of the reader the agents cannot.

**The fixtures are the record's shapes.** The check logs are cut down from real ones under
.scratch/overnight/log/: a first line `HEAD=<sha>`, the test runner's indented `total:` to
`duration:` lines, and a verdict last, in both forms the record carries: `GATE: GREEN|RED`, which the
overnight workflow wrote until #68, and `check: pass: ...` / `check: fail: ...`, which
tools/check.sh --full writes since. The reports' first lines are the 2026-10-16 report's and
questions file's, which carry no date in their names.

Each rule has a case that would pass if the rule were gone: a RED log beside a GREEN one, a side log
beside a log, two findings sharing a noun beside one that does not, a noun that recurs beside a
costlier one that does not, and a body missing its owner line beside one that has it.

Run: python3 -I tools/retro_record_tests.py
"""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
READER = ROOT / "tools" / "retro_record.py"

SHA = "d20cc6f1bf7711c08f0346c0f0333864969a9f39"

SUMMARY = """Test run summary: Passed!
  total: 1516
  failed: 0
  succeeded: 1516
  skipped: 0
  duration: 1m 11s 730ms
"""

GREEN_LOG = f"HEAD={SHA}\nbuild output\n{SUMMARY}steering: ok\nGATE: GREEN\n"
RED_LOG = (f"HEAD={SHA}\nbuild output\n  total: 1624\n  failed: 1\n  succeeded: 1623\n  skipped: 0\n"
           "  duration: 3m 35s 080ms\nTest run completed with non-success exit code: 2\n"
           "check: fail: the tests failed.\n")


def reader(*argv: str, stdin: object | None = None) -> tuple[int, object, str]:
    """Runs the reader; returns its exit status, its standard output read as JSON, and its stderr."""
    completed = subprocess.run(
        [sys.executable, "-I", str(READER), *argv],
        input=None if stdin is None else (stdin if isinstance(stdin, str) else json.dumps(stdin)),
        capture_output=True, text=True)
    try:
        out = json.loads(completed.stdout) if completed.stdout.strip() else None
    except json.JSONDecodeError:
        out = completed.stdout
    return completed.returncode, out, completed.stderr


def finding(noun: str, red: int = 0, attempts: int = 1, lanes: int = 1, nights: int = 1,
            evidence: list | None = None, ftype: str = "repeated-failure") -> dict:
    return {
        "type": ftype, "title": f"about {noun}", "noun": noun,
        "evidence": evidence if evidence is not None else [
            {"kind": "check-log", "ref": f"log/{noun}.txt:1", "lines": "1", "quote": noun}],
        "cost": {"red": red, "attempts": attempts, "nights": nights, "lanes": lanes},
        "fix": {"kind": "check", "owner": "integrator", "summary": "s", "diff": ""},
        "confidence": "verified",
    }


class Logs(unittest.TestCase):
    def setUp(self) -> None:
        self.dir = tempfile.mkdtemp(prefix="retro-record-tests-")

    def tearDown(self) -> None:
        for name in os.listdir(self.dir):
            os.remove(os.path.join(self.dir, name))
        os.rmdir(self.dir)

    def write(self, name: str, text: str) -> str:
        path = os.path.join(self.dir, name)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)
        return path

    def test_a_green_log_parses_to_its_sha_verdict_and_test_counts(self) -> None:
        path = self.write("1.1-implement.txt", GREEN_LOG)
        status, out, err = reader("log", path)
        self.assertEqual(status, 0, err)
        entry = out[0]
        self.assertEqual(entry["sha"], SHA)
        self.assertEqual(entry["verdict"], "GREEN")
        self.assertEqual(entry["verdictLine"], "GATE: GREEN")
        self.assertEqual(entry["tests"], {"total": 1516, "failed": 0, "succeeded": 1516, "skipped": 0,
                                          "duration": "1m 11s 730ms"})
        self.assertFalse(entry["side"])
        self.assertEqual((entry["task"], entry["stage"], entry["round"]), ("1.1", "implement", ""))
        self.assertEqual(entry["lines"], GREEN_LOG.count("\n"))
        self.assertRegex(entry["mtime"], r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")

    def test_a_red_log_parses_to_red_with_its_last_lines(self) -> None:
        for name, text, last in (
                ("issue-158-implement.txt", RED_LOG, "check: fail: the tests failed."),
                ("3.8-integrate.txt", GREEN_LOG.replace("GATE: GREEN", "GATE: RED")
                 .replace("failed: 0", "failed: 1"), "GATE: RED")):
            with self.subTest(name=name):
                status, out, err = reader("log", self.write(name, text))
                self.assertEqual(status, 0, err)
                entry = out[0]
                self.assertEqual(entry["verdict"], "RED")
                self.assertEqual(entry["verdictLine"], last)
                self.assertEqual(entry["lastLines"][-1], last)
                self.assertEqual(entry["lastLines"], text.splitlines()[-5:])
                self.assertEqual(entry["tests"]["failed"], 1)
        status, out, _ = reader("log", self.write("issue-158-implement.txt", RED_LOG))
        self.assertEqual(out[0]["task"], "issue-158")

    def test_a_log_without_head_is_a_side_log(self) -> None:
        side = self.write("1.2-terraform-test.txt", "terraform test\nSuccess! 3 passed\nGATE: GREEN\n")
        unfinished = self.write("1.4a-review.txt", f"HEAD={SHA}\nbuild output\n")
        status, out, err = reader("log", side, unfinished)
        self.assertEqual(status, 0, err)
        for entry in out:
            self.assertTrue(entry["side"], entry["path"])
            self.assertEqual(entry["verdict"], "", "a side log is never read as a verdict")
        self.write("1.1-implement.txt", GREEN_LOG)
        self.write("1.6b-body.md", f"HEAD={SHA}\nGATE: RED\n")
        status, out, err = reader("logs", self.dir)
        self.assertEqual(status, 0, err)
        self.assertEqual(sorted(os.path.basename(p) for p in out["sideLogs"]),
                         ["1.2-terraform-test.txt", "1.4a-review.txt"])
        self.assertEqual([os.path.basename(e["path"]) for e in out["logs"] if not e["side"]],
                         ["1.1-implement.txt"])
        self.assertEqual([os.path.basename(p) for p in out["notLogs"]], ["1.6b-body.md"],
                         "a file that is not a .txt log is listed and never parsed as a verdict")

    def test_a_date_suffixed_log_pairs_with_its_task_as_an_earlier_round(self) -> None:
        self.write("6.2-implement.txt", GREEN_LOG)
        self.write("6.2-implement-2026-10-16.txt", RED_LOG)
        self.write("2.1b-implement.2026-10-18.txt", GREEN_LOG)
        self.write("2.1b-implement.txt", GREEN_LOG)
        self.write("3.9-review.txt", GREEN_LOG)
        status, out, err = reader("logs", self.dir)
        self.assertEqual(status, 0, err)
        rounds = {(r["task"], r["stage"]): r for r in out["rounds"]}
        self.assertEqual(set(rounds), {("6.2", "implement"), ("2.1b", "implement")},
                         "only a task with a date-suffixed log has an earlier round")
        self.assertEqual(os.path.basename(rounds[("6.2", "implement")]["current"]), "6.2-implement.txt")
        self.assertEqual([os.path.basename(p) for p in rounds[("6.2", "implement")]["earlier"]],
                         ["6.2-implement-2026-10-16.txt"])
        self.assertEqual([os.path.basename(p) for p in rounds[("2.1b", "implement")]["earlier"]],
                         ["2.1b-implement.2026-10-18.txt"])
        dated = [e for e in out["logs"] if e["round"]]
        self.assertEqual(sorted(e["round"] for e in dated), ["2026-10-16", "2026-10-18"])


class Reports(unittest.TestCase):
    def test_a_reports_first_line_resolves_an_undated_file_to_its_date(self) -> None:
        with tempfile.TemporaryDirectory() as scratch:
            report = os.path.join(scratch, "REPORT.md")
            with open(report, "w", encoding="utf-8") as handle:
                handle.write("# Overnight report — 2026-10-16\n\nintro\n\n## Integrated\n\n- a\n\n"
                             "## Failed\n\n- 3.8 RED\n\n## Needs you\n")
            questions = os.path.join(scratch, "questions-2.md")
            with open(questions, "w", encoding="utf-8") as handle:
                handle.write("# Questions for Adam, overnight 2026-10-14 (second batch)\n\n## Q1\n")
            status, out, err = reader("report", report)
            self.assertEqual(status, 0, err)
            self.assertEqual((out["date"], out["kind"], out["shape"]), ("2026-10-16", "report", "archive"))
            self.assertEqual(out["sections"], ["Integrated", "Failed", "Needs you"])
            status, out, err = reader("report", questions)
            self.assertEqual((out["date"], out["kind"]), ("2026-10-14", "questions"))
            # The Issues shape: the date is the title's, the body has no first-line heading.
            body = os.path.join(scratch, "issue-226.md")
            with open(body, "w", encoding="utf-8") as handle:
                handle.write("Standing answer: granted.\n\n## Integrated\n\n## Partial (Outstanding)\n")
            status, out, err = reader("report", body, "--title", "Overnight 2026-10-21")
            self.assertEqual(status, 0, err)
            self.assertEqual((out["date"], out["kind"], out["shape"]), ("2026-10-21", "report", "issues"))
            self.assertEqual(out["sections"], ["Integrated", "Partial (Outstanding)"])
            # A file whose first line names no date resolves to none, and says so.
            with open(body, "w", encoding="utf-8") as handle:
                handle.write("# Notes\n")
            status, out, _ = reader("report", body)
            self.assertEqual(status, 1)


class Findings(unittest.TestCase):
    def test_two_findings_with_one_noun_merge_and_sum_their_cost(self) -> None:
        one = finding("\\;", red=1, attempts=2, lanes=1, nights=1)
        two = finding("\\;", red=0, attempts=3, lanes=2, nights=2,
                      evidence=[{"kind": "commit", "ref": "abc1234", "lines": "", "quote": "repair"}])
        other = finding("terraform 1.11.0", red=0, attempts=1)
        status, out, err = reader("merge", stdin=[one, other, two])
        self.assertEqual(status, 0, err)
        self.assertEqual([f["noun"] for f in out], ["\\;", "terraform 1.11.0"])
        merged = out[0]
        self.assertEqual(merged["cost"], {"red": 1, "attempts": 5, "lanes": 3, "nights": 2})
        self.assertEqual([e["ref"] for e in merged["evidence"]], ["log/\\;.txt:1", "abc1234"])
        self.assertEqual(merged["mergedFrom"], 2)
        self.assertEqual(out[1]["cost"]["attempts"], 1)

    def test_ranking_orders_by_cost_times_recurrence(self) -> None:
        # Costlier tonight, never seen before: weight 6, recurrence 1, score 6.
        costly = finding("dotnet build-server", red=2, attempts=3, lanes=1)
        # Cheaper tonight, in two earlier nights' records: weight 3, recurrence 3, score 9.
        recurring = finding("worktree add", red=0, attempts=2, lanes=1)
        status, out, err = reader("rank", stdin={
            "findings": [costly, recurring],
            "history": [
                {"date": "2026-10-15", "text": "## Failed\n- worktree add refused twice"},
                {"date": "2026-10-16", "text": "Q3: why does worktree add fail?"},
                {"date": "2026-10-16", "text": "the questions file names worktree add again"},
            ],
            "drop": [],
        })
        self.assertEqual(status, 0, err)
        self.assertEqual([f["noun"] for f in out["ranked"]], ["worktree add", "dotnet build-server"])
        top = out["ranked"][0]
        self.assertEqual((top["recurrence"], top["score"]), (3, 9),
                         "recurrence is the number of earlier nights carrying the noun, plus one")
        self.assertEqual((out["ranked"][1]["recurrence"], out["ranked"][1]["score"]), (1, 6))
        status, out, err = reader("rank", stdin={
            "findings": [costly, recurring], "history": [],
            "drop": [{"noun": "worktree add", "issue": "#99", "reason": "open issue covers it"}]})
        self.assertEqual([f["noun"] for f in out["ranked"]], ["dotnet build-server"])
        self.assertEqual(out["dropped"], [{"noun": "worktree add", "issue": "#99",
                                           "reason": "open issue covers it"}])

    def test_coverage_against_known_issues_reports_matched_and_missed(self) -> None:
        found = [finding("\\;"), finding("terraform 1.11.0"),
                 finding("leftover branch", evidence=[])]
        status, out, err = reader("coverage", stdin={
            "known": [
                {"number": 68, "text": "The check command's `\\;` cost 15 agents"},
                {"number": 69, "text": "Pin terraform 1.11.0 in the hook"},
                {"number": 70, "text": "The collect step's landed rule misses rows"},
                {"number": 71, "text": "Prune every leftover branch"},
            ],
            "findings": found,
            "missing": ["return JSON: not saved"],
            "why": {"70": "return JSON: not saved"},
        })
        self.assertEqual(status, 0, err)
        self.assertEqual([(m["issue"], m["noun"]) for m in out["matched"]],
                         [(68, "\\;"), (69, "terraform 1.11.0")])
        self.assertTrue(all(m["evidence"] for m in out["matched"]), "every match carries evidence")
        self.assertEqual([m["issue"] for m in out["missed"]], [70, 71],
                         "a finding with no evidence line matches nothing")
        self.assertEqual(out["missed"][0]["reason"], "return JSON: not saved")
        self.assertEqual(out["missed"][1]["reason"], "unexplained",
                         "a miss that names no entry of the index's missing list is unexplained")


class Bodies(unittest.TestCase):
    GOOD = ("**Owner:** integrator\n**Plan row:** none\n\n**What to build:** stop the repair.\n\n"
            "## Acceptance\n- [ ] no lane's log shows the repair\n")

    def test_a_ticket_body_in_the_required_shape_passes_and_one_missing_the_owner_line_fails(self) -> None:
        status, out, err = reader("body-check", stdin=self.GOOD)
        self.assertEqual(status, 0, err)
        self.assertEqual(out, {"ok": True, "problems": []})
        for label, body, problem in (
                ("no owner line", self.GOOD.replace("**Owner:** integrator\n", ""), "owner"),
                ("an owner outside OWNERSHIP.md", self.GOOD.replace("integrator", "ws9-nobody"), "owner"),
                ("no plan-row line", self.GOOD.replace("**Plan row:** none\n", ""), "plan row"),
                ("no open box", self.GOOD.replace("- [ ]", "-"), "Acceptance")):
            with self.subTest(label):
                status, out, _ = reader("body-check", stdin=body)
                self.assertEqual(status, 1)
                self.assertFalse(out["ok"])
                self.assertTrue(any(problem in p for p in out["problems"]), out["problems"])


class State(unittest.TestCase):
    def test_phase_files_and_the_script_fingerprint(self) -> None:
        with tempfile.TemporaryDirectory() as scratch:
            os.makedirs(os.path.join(scratch, "lanes"))
            with open(os.path.join(scratch, "gather.json"), "w", encoding="utf-8") as handle:
                json.dump({"inputs": "k1", "result": {"night": {}}}, handle)
            with open(os.path.join(scratch, "lanes", "ws1-infra.json"), "w", encoding="utf-8") as handle:
                json.dump({"inputs": "k2", "result": {"findings": []}}, handle)
            with open(os.path.join(scratch, "rank.json"), "w", encoding="utf-8") as handle:
                handle.write("{not json")
            status, out, err = reader("state", scratch)
            self.assertEqual(status, 0, err)
            self.assertEqual([(f["name"], f["inputs"]) for f in out],
                             [("gather", "k1"), ("lanes/ws1-infra", "k2")],
                             "a phase file that does not parse is not reused")
            status, out, _ = reader("state", os.path.join(scratch, "absent"))
            self.assertEqual((status, out), (0, []))
            script = os.path.join(scratch, "overnight.js")
            with open(script, "w", encoding="utf-8") as handle:
                handle.write("export const meta = {}\n")
            status, out, err = reader("script", script)
            self.assertEqual(status, 0, err)
            self.assertEqual(out["path"], script)
            self.assertEqual(out["sha256"], hashlib.sha256(b"export const meta = {}\n").hexdigest())
            self.assertRegex(out["mtime"], r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")


if __name__ == "__main__":
    unittest.main(verbosity=2)
