#!/usr/bin/env python3
"""Regressions for the two rules .github/steering.py holds.

Owner: integrator. Run: python3 -I .github/steering_tests.py

Each case builds a throwaway tree -- a pair of steering files, and whatever a pointer in them is
supposed to resolve to -- rather than asserting against the live repository, which is green and so
proves nothing about the red side. The module resolves its root from __file__, so patching that
points it at the tree; PLANNED is module state, so a case that needs an entry sets one.

The assertions are on main()'s exit code and what it printed, because that pair is the check's
contract with CI. The red lines come from the drift the check was written for: the progress claims
are the ones two retrospectives removed from AGENTS.md, and the dangling pointer is `docs/gates/`,
named twice while absent and later dropped rather than written.

One progress claim per pattern, each asserting the label that pattern prints, because the claim as
AGENTS.md actually carried it tripped three patterns at once and so pinned none of them: deleting
any one left the case green on the other two.
"""

import contextlib
import importlib.util
import io
import pathlib
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("steering", ROOT / ".github/steering.py")
steering = importlib.util.module_from_spec(spec)
spec.loader.exec_module(steering)

# A pair that passes: pointers that resolve, and no sentence carrying state.
CLEAN_AGENTS = """# Agent guide

The plan is in `docs/plan/`. Read the part for your task before you write anything.

- **Which gates have closed:** `git tag --merged origin/main`.
- **What opens next:** the dispatch table in `docs/plan/03-workstreams-and-tasks.md`.
"""

CLEAN_CLAUDE = """# Project instructions

@AGENTS.md

Ownership lives in `OWNERSHIP.md`, and the decisions are indexed by `docs/adr/README.md`.
"""

# Read out of the fixtures rather than listed beside them, so a pointer added to one above is
# created here without a second edit.
CLEAN_POINTERS = sorted(set(steering.PATHLIKE.findall(CLEAN_AGENTS + CLEAN_CLAUDE)))

# The lines 536ab68 and 91e2103 removed from AGENTS.md, each from under the paragraph forbidding
# it. 91e2103's was one sentence tripping three patterns; it is split per pattern here, in its own
# words, and kept whole in GATE_SCORE below.
DRIFT = (
    ("the done list",
     "- **Done:** the plan, and this skeleton (task I-1).",
     "a done list"),
    ("the day-one list",
     "- **Open on day one:** I-2, 1.0, 1.6a and 5.4. The dispatch table in "
     "`docs/plan/03-workstreams-and-tasks.md` says what opens next.",
     "a done list"),
    ("how far the gate got",
     "- **Gate G0 is open, with three of its five.**",
     "a count of how far something got"),
    ("what the gate passed",
     "- **Gate G0 is open, with its items passed.**",
     "a count of what passed"),
    ("the day it passed, written long",
     "The shipped handler, token management and YARP passed on 11 October 2026.",
     "a date"),
    ("the same day, written ISO",
     "The shipped handler, token management and YARP passed on 2026-10-11.",
     "a date"),
)

# 91e2103's line as AGENTS.md carried it.
GATE_SCORE = (
    "- **Gate G0 is open, with three of its five items passed.** The shipped handler, token "
    "management and YARP passed on 11 October 2026."
)


class SteeringTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name).resolve()

        # The check derives its root from the module's own location, two levels up.
        self.addCleanup(setattr, steering, "__file__", steering.__file__)
        steering.__file__ = str(self.root / ".github" / "steering.py")
        # PLANNED is the live repository's list; a case that wants an entry says so.
        self.addCleanup(setattr, steering, "PLANNED", steering.PLANNED)
        steering.PLANNED = {}

        self.reset()
        for pointer in CLEAN_POINTERS:
            self.write(pointer)

    def reset(self) -> None:
        self.write("AGENTS.md", CLEAN_AGENTS)
        self.write("CLAUDE.md", CLEAN_CLAUDE)

    def write(self, relative: str, body: str = "x\n") -> None:
        """Create a path the tree needs. A trailing slash makes it a directory, as `docs/plan/`."""
        target = self.root / relative
        if relative.endswith("/"):
            target.mkdir(parents=True, exist_ok=True)
            return
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(body)

    def append(self, relative: str, line: str) -> int:
        """Add a line to a steering file, and say which line it became."""
        target = self.root / relative
        with target.open("a") as handle:
            handle.write(line + "\n")
        return len(target.read_text().splitlines())

    def check(self) -> tuple[int, str]:
        """The exit code and output of the check, as CI reads them."""
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = steering.main()
        return code, buffer.getvalue()

    def test_a_clean_pair_passes(self):
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("claim no progress and every pointer resolves", out)

    def test_each_progress_claim_the_retrospectives_removed_fails(self):
        for shape, line, label in DRIFT:
            with self.subTest(shape=shape):
                self.reset()
                number = self.append("AGENTS.md", line)
                code, out = self.check()
                self.assertEqual(1, code, out)
                self.assertIn("STEERING FAILED: AGENTS.md and CLAUDE.md state progress.", out)
                self.assertIn(f"AGENTS.md:{number}: {label}: {line.strip()}", out)
                self.assertIn("Point at the source instead", out)

    def test_the_whole_gate_score_line_fails(self):
        number = self.append("AGENTS.md", GATE_SCORE)
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"AGENTS.md:{number}:", out)

    def test_a_progress_claim_in_claude_md_fails_too(self):
        shape, line, label = DRIFT[0]
        number = self.append("CLAUDE.md", line)
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"CLAUDE.md:{number}: {label}:", out)

    def test_a_pointer_at_nothing_fails(self):
        self.append("AGENTS.md", "- **Which gate is open:** the checklist in `docs/gates/`.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a pointer names something that does not exist.", out)
        self.assertIn("points at 'docs/gates/', which does not exist", out)
        self.assertIn("add the path to PLANNED", out)

    def test_a_pointer_that_resolves_passes(self):
        self.append("AGENTS.md", "- **Which gate is open:** the checklist in `docs/gates/`.")
        self.write("docs/gates/G0.md")
        code, out = self.check()
        self.assertEqual(0, code, out)

    def test_a_bare_filename_is_not_read_as_a_pointer(self):
        self.append("AGENTS.md", "`Program.cs` calls them, and the terms are in `GLOSSARY.md`.")
        code, out = self.check()
        self.assertEqual(0, code, out)

    def test_a_planned_path_is_exempt_while_absent(self):
        steering.PLANNED = {"docs/gaps.md": "workstream 6 owns it and has not started"}
        self.append("AGENTS.md", "| 6 Verification | `tests/e2e`, `docs/gaps.md` |")
        self.write("tests/e2e/placeholder.md")
        code, out = self.check()
        self.assertEqual(0, code, out)

    def test_a_planned_path_that_has_landed_fails(self):
        why = "workstream 6 owns it and has not started"
        steering.PLANNED = {"docs/gaps.md": why}
        self.append("AGENTS.md", "| 6 Verification | `docs/gaps.md` |")
        self.write("docs/gaps.md")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a PLANNED path has landed.", out)
        self.assertIn(f"'docs/gaps.md' exists now ({why})", out)
        self.assertIn("Remove it from PLANNED", out)

    def test_a_planned_path_that_landed_fails_even_when_nothing_names_it(self):
        steering.PLANNED = {"docs/gaps.md": "workstream 6 owns it and has not started"}
        self.write("docs/gaps.md")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a PLANNED path has landed.", out)


if __name__ == "__main__":
    unittest.main(verbosity=2)
