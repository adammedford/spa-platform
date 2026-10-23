#!/usr/bin/env python3
"""Test companion for tools/retro_refusals.py, the retro's count of Edit and Write refusals.

Owner: integrator. Specified in #315, from #109's Q166 and Q167.

**The seam is the command the retro's Gather agent runs.** Every case starts the real file as
`python3 -I tools/retro_refusals.py ...` (D-3 in docs/dependencies.md: Python's `unittest`, running
the real script) on a transcripts root this companion writes into a throwaway directory, and asserts
on the line it prints and its exit status. Nothing is imported.

**The fixtures are the transcripts' shapes**, cut down from a real run's
(`<session>/subagents/workflows/wf_<id>/agent-<id>.jsonl` beside its `.meta.json`, and the run's
record `<session>/workflows/wf_<id>.json`): an `assistant` line carrying a `tool_use` block, the
`api-request-blob` line that replays the same block, and a `user` line carrying its `tool_result`,
replayed too.

Each rule has a case that fails when the rule is removed: a replayed call and a replayed result
counted once, a Write refusal counted with Edit's, a Bash result quoting the refusal's words left out,
a result from another run left out, and a transcripts root with no match reported as missing and not
as zero. The baseline case reads the 2026-10-18 run's real transcripts when they are on this machine
and asserts the script says they are not when they are not; neither branch skips.

Run: python3 -I tools/retro_refusals_tests.py
"""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "retro_refusals.py"

NOT_READ = "<tool_use_error>File has not been read yet. Read it first before writing to it.</tool_use_error>"
MODIFIED = ("<tool_use_error>File has been modified since read, either by the user or by a linter. "
            "Read it again before attempting to write it.</tool_use_error>")

BASELINE_RUN = "wf_3b77ab2e-214"
DEFAULT_ROOT = os.path.expanduser("~/.claude/projects/-Users-adammedford-Projects-spa-platform/*/subagents/workflows/")


def run(*argv: str) -> tuple[int, str, str]:
    completed = subprocess.run([sys.executable, "-I", str(SCRIPT), *argv], capture_output=True, text=True)
    return completed.returncode, completed.stdout, completed.stderr


def use(tool_id: str, name: str) -> dict:
    return {"type": "tool_use", "id": tool_id, "name": name, "input": {"file_path": "/w/a.txt"}}


def result(tool_id: str, text: str, error: bool = True) -> dict:
    return {"type": "tool_result", "tool_use_id": tool_id, "content": text, "is_error": error}


def assistant(agent: str, *blocks: dict) -> dict:
    return {"type": "assistant", "agentId": agent, "isSidechain": True,
            "message": {"role": "assistant", "content": list(blocks)}}


def blob(agent: str, role: str, *blocks: dict) -> dict:
    """An api-request-blob line: a request's replay of an earlier message, blocks and all."""
    return {"type": "api-request-blob", "agentId": agent, "hash": "h",
            "message": {"role": role, "content": list(blocks)}}


def user(agent: str, *blocks: dict) -> dict:
    return {"type": "user", "agentId": agent, "isSidechain": True,
            "message": {"role": "user", "content": list(blocks)}}


class Transcripts:
    """A transcripts root in the shape ~/.claude/projects/<project>/ holds."""

    def __init__(self) -> None:
        self.base = tempfile.mkdtemp(prefix="retro-refusals-tests-")
        self.root = os.path.join(self.base, "*", "subagents", "workflows")

    def agent(self, session: str, run_id: str, agent: str, label: str, lines: list) -> None:
        directory = os.path.join(self.base, session, "subagents", "workflows", run_id)
        os.makedirs(directory, exist_ok=True)
        with open(os.path.join(directory, f"agent-{agent}.jsonl"), "w", encoding="utf-8") as handle:
            for line in lines:
                handle.write(json.dumps(line) + "\n")
        with open(os.path.join(directory, f"agent-{agent}.meta.json"), "w", encoding="utf-8") as handle:
            json.dump({"agentType": "workflow-subagent", "description": label}, handle)

    def record(self, session: str, run_id: str, date: str, name: str = "overnight",
               timestamp: str = "2026-10-18T10:06:22.366Z") -> None:
        directory = os.path.join(self.base, session, "workflows")
        os.makedirs(directory, exist_ok=True)
        with open(os.path.join(directory, f"{run_id}.json"), "w", encoding="utf-8") as handle:
            json.dump({"runId": run_id, "timestamp": timestamp, "workflowName": name,
                       "scriptPath": f"/r/.claude/workflows/{name}.js", "args": {"date": date}}, handle)

    def close(self) -> None:
        shutil.rmtree(self.base, ignore_errors=True)


class Counts(unittest.TestCase):
    def setUp(self) -> None:
        self.t = Transcripts()
        self.addCleanup(self.t.close)

    def count(self, run_id: str = "wf_aaaa1111-000") -> str:
        status, out, err = run(run_id, "--root", self.t.root)
        self.assertEqual(status, 0, err + out)
        lines = out.splitlines()
        self.assertEqual(len(lines), 1, out)
        return lines[0]

    def test_a_replayed_call_and_a_replayed_result_count_once(self) -> None:
        self.t.agent("s1", "wf_aaaa1111-000", "a1", "implement:issue-1", [
            assistant("a1", use("toolu_1", "Edit")),
            user("a1", result("toolu_1", NOT_READ)),
            blob("a1", "assistant", use("toolu_1", "Edit")),
            blob("a1", "user", result("toolu_1", NOT_READ)),
            assistant("a1", use("toolu_2", "Edit")),
            user("a1", result("toolu_2", "The file /w/a.txt has been updated.", error=False)),
            blob("a1", "assistant", use("toolu_2", "Edit")),
        ])
        self.assertEqual(
            self.count(),
            'retro-refusals: wf_aaaa1111-000: 2 Edit and Write calls (4 tool_use blocks, replays included), '
            '1 "File has not been read yet" (1 Edit, 0 Write), 0 "modified since read"; '
            'agents: implement:issue-1')

    def test_a_write_refusal_counts_with_edits_and_names_its_agent(self) -> None:
        self.t.agent("s1", "wf_aaaa1111-000", "a1", "implement:issue-1", [
            assistant("a1", use("toolu_1", "Edit")),
            user("a1", result("toolu_1", NOT_READ)),
        ])
        self.t.agent("s1", "wf_aaaa1111-000", "a2", "implement:issue-2", [
            assistant("a2", use("toolu_2", "Write")),
            user("a2", result("toolu_2", NOT_READ)),
            assistant("a2", use("toolu_3", "Write")),
            user("a2", result("toolu_3", MODIFIED)),
        ])
        self.t.agent("s1", "wf_aaaa1111-000", "a3", "review:issue-3", [
            assistant("a3", use("toolu_4", "Edit")),
            user("a3", result("toolu_4", "updated", error=False)),
        ])
        self.assertEqual(
            self.count(),
            'retro-refusals: wf_aaaa1111-000: 4 Edit and Write calls (4 tool_use blocks, replays included), '
            '2 "File has not been read yet" (1 Edit, 1 Write), 1 "modified since read"; '
            'agents: implement:issue-1, implement:issue-2')

    def test_a_bash_result_quoting_the_refusal_is_not_a_refusal(self) -> None:
        # A session that reads #109 or a retro ticket prints the refusal's words in a Bash result.
        self.t.agent("s1", "wf_aaaa1111-000", "a1", "implement:issue-1", [
            assistant("a1", use("toolu_1", "Bash")),
            user("a1", result("toolu_1", f"## What to build\n{NOT_READ}\n{MODIFIED}", error=False)),
            assistant("a1", use("toolu_2", "Edit")),
            user("a1", result("toolu_2", "updated", error=False)),
        ])
        self.assertEqual(
            self.count(),
            'retro-refusals: wf_aaaa1111-000: 1 Edit and Write calls (1 tool_use blocks, replays included), '
            '0 "File has not been read yet" (0 Edit, 0 Write), 0 "modified since read"; agents: none')

    def test_a_result_from_another_run_is_left_out(self) -> None:
        self.t.agent("s1", "wf_aaaa1111-000", "a1", "implement:issue-1", [
            assistant("a1", use("toolu_1", "Edit")),
            user("a1", result("toolu_1", "updated", error=False)),
        ])
        self.t.agent("s1", "wf_bbbb2222-000", "b1", "implement:issue-9", [
            assistant("b1", use("toolu_9", "Edit")),
            user("b1", result("toolu_9", NOT_READ)),
        ])
        self.t.agent("s2", "wf_cccc3333-000", "c1", "implement:issue-8", [
            assistant("c1", use("toolu_8", "Write")),
            user("c1", result("toolu_8", MODIFIED)),
        ])
        self.assertEqual(
            self.count(),
            'retro-refusals: wf_aaaa1111-000: 1 Edit and Write calls (1 tool_use blocks, replays included), '
            '0 "File has not been read yet" (0 Edit, 0 Write), 0 "modified since read"; agents: none')

    def test_a_root_with_no_match_is_missing_not_zero(self) -> None:
        self.t.agent("s1", "wf_bbbb2222-000", "b1", "implement:issue-9", [assistant("b1", use("toolu_9", "Edit"))])
        status, out, _ = run("wf_aaaa1111-000", "--root", self.t.root)
        self.assertEqual(status, 1)
        self.assertEqual(out.strip(), f"retro-refusals: wf_aaaa1111-000: missing: no subagent transcripts for this run under {self.t.root}")
        self.assertNotIn(" 0 ", out)

    def test_a_date_resolves_every_overnight_launch_on_it_in_order(self) -> None:
        # Q167: a second launch on the same date is a run of its own, and gets its own line.
        self.t.record("s1", "wf_aaaa1111-000", "2026-10-21", timestamp="2026-10-21T09:00:00.000Z")
        self.t.record("s2", "wf_bbbb2222-000", "2026-10-21", timestamp="2026-10-21T01:00:00.000Z")
        self.t.record("s2", "wf_cccc3333-000", "2026-10-21", name="retro")
        self.t.record("s2", "wf_dddd4444-000", "2026-10-20")
        for run_id, agent in (("wf_aaaa1111-000", "a1"), ("wf_bbbb2222-000", "b1"), ("wf_cccc3333-000", "c1"), ("wf_dddd4444-000", "d1")):
            self.t.agent("s1" if run_id.startswith("wf_a") else "s2", run_id, agent, f"implement:{agent}", [
                assistant(agent, use(f"toolu_{agent}", "Edit")),
                user(agent, result(f"toolu_{agent}", NOT_READ)),
            ])
        status, out, err = run("--date", "2026-10-21", "--root", self.t.root)
        self.assertEqual(status, 0, err + out)
        self.assertEqual([line.split(":")[1].strip() for line in out.splitlines()],
                         ["wf_bbbb2222-000", "wf_aaaa1111-000"])

    def test_a_date_with_no_overnight_launch_is_missing(self) -> None:
        self.t.record("s1", "wf_dddd4444-000", "2026-10-20")
        status, out, _ = run("--date", "2026-10-21", "--root", self.t.root)
        self.assertEqual(status, 1)
        self.assertEqual(out.strip(), f"retro-refusals: 2026-10-21: missing: no overnight run recorded for this date under {self.t.root}")

    def test_a_usage_error_exits_2(self) -> None:
        for argv in ((), ("--date", "21 October"), ("not-a-run-id",)):
            with self.subTest(argv=argv):
                status, _, err = run(*argv)
                self.assertEqual(status, 2, err)


class Baseline(unittest.TestCase):
    """#315's baseline: the 2026-10-18 run, as #109's grilling record counted it."""

    def test_the_2026_10_18_run_or_its_absence(self) -> None:
        status, out, err = run(BASELINE_RUN)
        import glob
        if glob.glob(os.path.join(DEFAULT_ROOT, BASELINE_RUN)):
            self.assertEqual(status, 0, err + out)
            self.assertRegex(out, r'^retro-refusals: wf_3b77ab2e-214: 110 Edit and Write calls \(210 tool_use blocks, replays included\), '
                                  r'11 "File has not been read yet" \(10 Edit, 1 Write\), 5 "modified since read"; agents: ')
        else:
            self.assertEqual(status, 1, err + out)
            self.assertEqual(out.strip(), f"retro-refusals: {BASELINE_RUN}: missing: no subagent transcripts for this run under {DEFAULT_ROOT}")


if __name__ == "__main__":
    unittest.main(verbosity=2)
