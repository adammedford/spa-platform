#!/usr/bin/env python3
"""Tests for .github/outside-issues.py and the workflow that runs it. Owner: integrator.

Run: python3 -I .github/outside_issues_tests.py

Three seams, each through what a caller sees (#1547):

- The script, run as a subprocess against an event payload written here: its exit code and its
  last line, which is the verdict the workflow reads. Every `author_association` GitHub documents
  has a case, so dropping one from either side of the test turns a case red.
- The workflow's run block, taken from .github/workflows/outside-issues.yml as it stands and run
  under bash with a fake `gh` first on PATH that logs each call and reaches nothing. What it asserts
  is the calls: one POST that names `needs-triage` for an outside author, none for an inside one,
  and none for a payload the script refused.
- The workflow's text: the event it runs on and the permissions it holds, compared whole, so an
  added event type or an added write permission fails rather than passing beside the expected one.
"""
from __future__ import annotations

import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / ".github" / "outside-issues.py"
WORKFLOW = ROOT / ".github" / "workflows" / "outside-issues.yml"

# GitHub's values for author_association, from the webhook and REST schemas
# (https://docs.github.com/en/graphql/reference/enums#commentauthorassociation).
OUTSIDE = ("CONTRIBUTOR", "FIRST_TIME_CONTRIBUTOR", "FIRST_TIMER", "NONE", "MANNEQUIN")
INSIDE = ("OWNER", "MEMBER", "COLLABORATOR")


def payload(association: str | None = None, number: int = 41, omit: bool = False) -> dict:
    issue: dict = {"number": number, "title": "A title nobody here wrote", "body": "text"}
    if not omit:
        issue["author_association"] = association
    return {"action": "opened", "issue": issue}


class Case(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.temp.name)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def event(self, body: dict | str) -> pathlib.Path:
        path = self.dir / "event.json"
        path.write_text(body if isinstance(body, str) else json.dumps(body), encoding="utf-8")
        return path

    def decide(self, body: dict | str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, "-I", str(SCRIPT), str(self.event(body))],
                              capture_output=True, text=True, env={"PATH": os.environ["PATH"]})


class Decision(Case):
    """The script's verdict on one opened issue's payload."""

    def test_an_outside_author_gets_needs_triage(self):
        for association in OUTSIDE:
            with self.subTest(association):
                run = self.decide(payload(association))
                self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
                self.assertEqual(run.stdout.splitlines()[-1],
                                 f"outside-issues: label: #41 needs-triage: author_association "
                                 f"{association} is from outside the repository")

    def test_an_inside_author_is_left_alone(self):
        for association in INSIDE:
            with self.subTest(association):
                run = self.decide(payload(association))
                self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
                self.assertEqual(run.stdout.splitlines()[-1],
                                 f"outside-issues: leave: #41: author_association {association} "
                                 f"is inside the repository")

    def test_a_payload_with_no_author_association_fails_naming_the_issue(self):
        for name, body in (("absent", payload(omit=True, number=58)),
                           ("null", payload(None, number=58)),
                           ("empty", payload("", number=58)),
                           ("not a string", payload(7, number=58))):  # type: ignore[arg-type]
            with self.subTest(name):
                run = self.decide(body)
                self.assertEqual(run.returncode, 2, run.stdout + run.stderr)
                self.assertEqual(run.stdout.splitlines()[-1],
                                 "outside-issues: fail: #58: the payload carries no "
                                 "author_association; no label is written")

    def test_a_payload_that_names_no_issue_fails(self):
        for name, body in (("no issue", {"action": "opened"}),
                           ("no number", {"action": "opened", "issue": {"author_association": "NONE"}}),
                           ("a boolean number",
                            {"action": "opened", "issue": {"number": True, "author_association": "NONE"}}),
                           ("not json", "{"),
                           ("not an object", "[]")):
            with self.subTest(name):
                run = self.decide(body)
                self.assertEqual(run.returncode, 2, run.stdout + run.stderr)
                self.assertTrue(run.stdout.splitlines()[-1].startswith("outside-issues: fail: "),
                                run.stdout)

    def test_the_verdict_never_prints_the_title_or_body(self):
        body = payload("NONE")
        body["issue"]["title"] = "canary-title-6c1f"
        body["issue"]["body"] = "canary-body-6c1f"
        run = self.decide(body)
        self.assertNotIn("canary", run.stdout + run.stderr)

    def test_a_missing_argument_fails(self):
        run = subprocess.run([sys.executable, "-I", str(SCRIPT)], capture_output=True, text=True)
        self.assertEqual(run.returncode, 2, run.stdout + run.stderr)


def run_block() -> str:
    """The workflow's one run block, dedented, as the runner hands it to bash."""
    lines = WORKFLOW.read_text(encoding="utf-8").splitlines()
    starts = [i for i, line in enumerate(lines) if re.fullmatch(r"\s*run: \|", line)]
    if len(starts) != 1:
        raise AssertionError(f"{WORKFLOW} holds {len(starts)} run blocks, not one")
    indent = len(lines[starts[0]]) - len(lines[starts[0]].lstrip())
    block = []
    for line in lines[starts[0] + 1:]:
        if line.strip() and len(line) - len(line.lstrip()) <= indent:
            break
        block.append(line)
    return textwrap.dedent("\n".join(block)) + "\n"


FAKE_GH = """#!/bin/sh
printf '%s\\n' "$*" >> "$GH_LOG"
"""


class RunBlock(Case):
    """The workflow's run block, under bash, against a gh that only logs."""

    def run_workflow(self, body: dict, number: str | None = None
                     ) -> tuple[subprocess.CompletedProcess, list[str]]:
        bin_dir = self.dir / "bin"
        bin_dir.mkdir()
        gh = bin_dir / "gh"
        gh.write_text(FAKE_GH, encoding="utf-8")
        gh.chmod(0o755)
        log = self.dir / "gh.log"
        script = self.dir / "run.sh"
        script.write_text(run_block(), encoding="utf-8")
        runner_temp = self.dir / "runner"
        runner_temp.mkdir()
        if number is None:
            number = str(body.get("issue", {}).get("number", ""))
        env = {"PATH": f"{bin_dir}{os.pathsep}{os.environ['PATH']}", "GH_LOG": str(log),
               "GITHUB_EVENT_PATH": str(self.event(body)), "RUNNER_TEMP": str(runner_temp),
               "GH_REPO": "owner/repo", "NUMBER": number}
        # The shell line GitHub documents for a `run:` step on Linux
        # (https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_idstepsshell).
        run = subprocess.run(["bash", "--noprofile", "--norc", "-eo", "pipefail", str(script)],
                             cwd=ROOT, capture_output=True, text=True, env=env)
        calls = log.read_text(encoding="utf-8").splitlines() if log.exists() else []
        return run, calls

    def test_an_outside_author_gets_one_post_that_adds_needs_triage_by_name(self):
        for association in OUTSIDE:
            with self.subTest(association):
                self.tearDown()
                self.setUp()
                run, calls = self.run_workflow(payload(association))
                self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
                self.assertEqual(calls, ["api --method POST repos/owner/repo/issues/41/labels "
                                         "-f labels[]=needs-triage"])

    def test_an_inside_author_makes_no_call(self):
        for association in INSIDE:
            with self.subTest(association):
                self.tearDown()
                self.setUp()
                run, calls = self.run_workflow(payload(association))
                self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
                self.assertEqual(calls, [])

    def test_a_payload_with_no_author_association_fails_the_job_and_makes_no_call(self):
        run, calls = self.run_workflow(payload(omit=True, number=58))
        self.assertNotEqual(run.returncode, 0, run.stdout + run.stderr)
        self.assertIn("#58", run.stdout)
        self.assertEqual(calls, [])

    def test_a_verdict_on_another_issue_than_the_event_names_makes_no_call(self):
        run, calls = self.run_workflow(payload("NONE", number=41), number="42")
        self.assertNotEqual(run.returncode, 0, run.stdout + run.stderr)
        self.assertEqual(calls, [])


def top_level_block(text: str, key: str) -> dict[str, str]:
    """A top-level mapping's direct children, `name: value` as text, read without a YAML parser."""
    lines = text.splitlines()
    try:
        start = lines.index(f"{key}:")
    except ValueError:
        raise AssertionError(f"{WORKFLOW} has no top-level {key}: block") from None
    found: dict[str, str] = {}
    for line in lines[start + 1:]:
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if not line.startswith(" "):
            break
        match = re.fullmatch(r"  ([\w-]+):\s*(.*)", line)
        if match:
            found[match.group(1)] = match.group(2)
    return found


class WorkflowText(unittest.TestCase):
    """What the workflow runs on and what its token may write."""

    text = WORKFLOW.read_text(encoding="utf-8")

    def test_it_runs_on_issues_opened_alone(self):
        self.assertEqual(top_level_block(self.text, "on"), {"issues": ""})
        self.assertEqual(re.findall(r"^\s+types:\s*(.*)$", self.text, re.M), ["[opened]"])

    def test_its_token_writes_issues_and_nothing_else(self):
        self.assertEqual(top_level_block(self.text, "permissions"),
                         {"issues": "write", "contents": "read"})
        # A job-level block would replace the top-level one for that job.
        self.assertEqual(len(re.findall(r"^\s*permissions:", self.text, re.M)), 1)

    def test_it_runs_the_script_on_the_event_payload(self):
        self.assertIn('python3 -I .github/outside-issues.py "$GITHUB_EVENT_PATH"', run_block())

    def test_it_never_puts_issue_text_into_the_run_block(self):
        # The title and body are written by whoever opened the issue; expanded into a run block
        # they would run as shell.
        self.assertNotRegex(self.text, r"github\.event\.issue\.(title|body|user)")


if __name__ == "__main__":
    unittest.main()
