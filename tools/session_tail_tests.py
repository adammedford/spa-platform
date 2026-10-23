#!/usr/bin/env python3
"""Tests for tools/session_tail.py. Owner: integrator.

Run: python3 -I tools/session_tail_tests.py

A review or a morning that starts from what another session left reads that session's intent from
the end of its transcript (#303, raised by #286). A tail that let tool output or task notifications
back in would bury the few lines that matter under the 18,000 characters #286's hand parse printed;
one that dropped a human or assistant turn would hide the very thing it was asked for. Each case
starts the real script as a subprocess under `python3 -I`, in D-3's shape, against a throwaway
projects folder holding a hand-written transcript, and compares stdout, stderr and the exit code
with values written out by hand.

The fixture's records copy the shapes Claude Code wrote into this repository's transcripts on
2026-10-22: a human turn as a string, an assistant turn as a list of blocks, thinking and tool_use
blocks in records of their own, a tool_result in a `user` record, a task notification as a `user`
record whose string begins `<task-notification>` and whose origin kind is `task-notification`, and
an `api-request-blob` record that carries an assistant role and text but is not a turn, and an
`isMeta` record, text the harness injected (a skill's body, a command caveat) rather than a turn.
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "session_tail.py"

SESSION = "0f1e2d3c-4b5a-4968-8776-655443322110"


def human(text: str, ts: str) -> dict:
    return {"type": "user", "timestamp": ts, "origin": {"kind": "human"},
            "message": {"role": "user", "content": text}}


def assistant(blocks: list, ts: str, msg_id: str) -> dict:
    return {"type": "assistant", "timestamp": ts,
            "message": {"id": msg_id, "role": "assistant", "content": blocks}}


def text(t: str) -> dict:
    return {"type": "text", "text": t}


TOOL_OUTPUT = "TOOL-RESULT-CANARY: ## main...origin/main"
THINKING = "THINKING-CANARY: the plan is"
TOOL_CALL = "TOOL-USE-CANARY gh issue view 313"
NOTIFICATION = ("<task-notification>\n<task-id>a382b16d9dbfd24be</task-id>\n"
                "<result>NOTIFICATION-CANARY: the sub-agent's whole report</result>\n"
                "</task-notification>")
META = "META-CANARY: Base directory for this skill: a skill body the harness injected"
BLOB = "BLOB-CANARY: an api-request-blob is a copy of the request, not a turn"

FIXTURE = [
    {"type": "file-history-snapshot", "messageId": "x"},
    human("First, an early turn the tail does not reach.", "2026-10-22T22:40:00.000Z"),
    assistant([text("An early reply the tail does not reach.")],
              "2026-10-22T22:40:05.000Z", "msg_0"),
    human("Land #313, please.", "2026-10-22T22:50:18.000Z"),
    assistant([{"type": "thinking", "thinking": THINKING, "signature": "c2ln"}],
              "2026-10-22T22:50:20.219Z", "msg_1"),
    assistant([{"type": "tool_use", "id": "toolu_1", "name": "Bash",
                "input": {"command": TOOL_CALL}}], "2026-10-22T22:50:20.592Z", "msg_1"),
    {"type": "user", "timestamp": "2026-10-22T22:50:22.361Z",
     "message": {"role": "user", "content": [
         {"type": "tool_result", "tool_use_id": "toolu_1", "content": TOOL_OUTPUT,
          "is_error": False}]}},
    {"type": "user", "timestamp": "2026-10-22T22:50:21.000Z", "isMeta": True,
     "message": {"role": "user", "content": [text(META)]}},
    {"type": "api-request-blob",
     "message": {"role": "assistant", "content": [text(BLOB)]}},
    assistant([text("Making the two edits in a worktree.")], "2026-10-22T22:50:40.073Z", "msg_2"),
    {"type": "user", "timestamp": "2026-10-22T22:55:01.000Z",
     "origin": {"kind": "task-notification", "producer": "agent"},
     "message": {"role": "user", "content": NOTIFICATION}},
    {"type": "attachment", "timestamp": "2026-10-22T22:55:01.100Z",
     "attachment": {"type": "queued_command", "prompt": NOTIFICATION}},
    assistant([text("Landed; the roadmap block is refreshed.")],
              "2026-10-22T22:58:27.000Z", "msg_3"),
]

EXPECTED_LAST_THREE = (
    "--- human 2026-10-22T22:50:18.000Z ---\n"
    "Land #313, please.\n"
    "\n"
    "--- assistant 2026-10-22T22:50:40.073Z ---\n"
    "Making the two edits in a worktree.\n"
    "\n"
    "--- assistant 2026-10-22T22:58:27.000Z ---\n"
    "Landed; the roadmap block is refreshed.\n"
)

CANARIES = ("TOOL-RESULT-CANARY", "THINKING-CANARY", "TOOL-USE-CANARY", "NOTIFICATION-CANARY",
            "BLOB-CANARY", "META-CANARY", "task-notification")


class Tail(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = pathlib.Path(self._tmp.name)
        self.projects = self.tmp / "projects"
        # Claude Code names a project's folder for the path it was started in, every character
        # other than a letter or digit made a hyphen: /Users/adammedford/Projects/spa-platform is
        # -Users-adammedford-Projects-spa-platform and its .claude/worktrees/roadmap worktree is
        # -Users-...-spa-platform--claude-worktrees-roadmap. The project path here is written to
        # give a known folder name.
        self.project = "/work/spa.platform/main"
        self.folder = self.projects / "-work-spa-platform-main"

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def write(self, records: list, session: str = SESSION) -> None:
        self.folder.mkdir(parents=True, exist_ok=True)
        with (self.folder / f"{session}.jsonl").open("w", encoding="utf-8") as out:
            for record in records:
                out.write(json.dumps(record) + "\n")

    def tail(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [sys.executable, "-I", str(SCRIPT), *args,
             "--projects-dir", str(self.projects), "--project", self.project],
            capture_output=True, timeout=60, check=False)


class PrintsTheLastTextTurns(Tail):
    def test_the_last_n_text_turns_print_in_order_with_role_and_time_and_nothing_else(self) -> None:
        self.write(FIXTURE)
        done = self.tail(SESSION, "-n", "3")
        self.assertEqual(done.returncode, 0, done.stderr.decode())
        self.assertEqual(done.stderr.decode(), "")
        out = done.stdout.decode("utf-8")
        for canary in CANARIES:
            self.assertNotIn(canary, out)
        self.assertEqual(out, EXPECTED_LAST_THREE)

    def test_six_turns_by_default(self) -> None:
        self.write(FIXTURE)
        done = self.tail(SESSION)
        self.assertEqual(done.returncode, 0, done.stderr.decode())
        out = done.stdout.decode("utf-8")
        self.assertEqual(out.count("\n--- ") + out.startswith("--- "), 5,
                         "the fixture holds five text turns, so the default of six prints them all")
        self.assertTrue(out.startswith("--- human 2026-10-22T22:40:00.000Z ---\n"
                                       "First, an early turn the tail does not reach.\n"), out)
        self.assertTrue(out.endswith(EXPECTED_LAST_THREE), out)


class CutsALongTurn(Tail):
    def test_a_turn_over_4000_characters_is_cut_with_the_count_dropped(self) -> None:
        long_text = "a" * 3999 + "b" + "c" * 1234
        self.write([assistant([text(long_text)], "2026-10-22T23:00:00.000Z", "msg_9")])
        done = self.tail(SESSION, "-n", "1")
        self.assertEqual(done.returncode, 0, done.stderr.decode())
        self.assertEqual(done.stdout.decode("utf-8"),
                         "--- assistant 2026-10-22T23:00:00.000Z ---\n"
                         + "a" * 3999 + "b\n"
                         "[1234 characters dropped]\n")

    def test_a_turn_of_exactly_4000_characters_is_not_cut(self) -> None:
        self.write([human("d" * 4000, "2026-10-22T23:00:00.000Z")])
        done = self.tail(SESSION, "-n", "1")
        self.assertEqual(done.returncode, 0, done.stderr.decode())
        self.assertEqual(done.stdout.decode("utf-8"),
                         "--- human 2026-10-22T23:00:00.000Z ---\n" + "d" * 4000 + "\n")


class RefusesAnUnknownSession(Tail):
    def test_an_unknown_id_exits_non_zero_and_names_the_folder_searched(self) -> None:
        self.write(FIXTURE)
        done = self.tail("11111111-2222-3333-4444-555555555555")
        self.assertNotEqual(done.returncode, 0)
        self.assertEqual(done.stdout.decode(), "")
        self.assertIn(str(self.folder), done.stderr.decode())

    def test_a_missing_projects_folder_still_names_the_folder_searched(self) -> None:
        done = self.tail(SESSION)
        self.assertNotEqual(done.returncode, 0)
        self.assertIn(str(self.folder), done.stderr.decode())


if __name__ == "__main__":
    unittest.main()
