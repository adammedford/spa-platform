#!/usr/bin/env python3
"""Regressions for the three rules .github/steering.py holds.

Owner: integrator. Run: python3 -I .github/steering_tests.py

Each case builds a throwaway tree -- a pair of steering files, a contracts index, and whatever a
pointer in them is supposed to resolve to -- rather than asserting against the live repository,
which is green and so proves nothing about the red side. The freeze rule gets one case against the
repository as well, because its green side is the index and the contracts as they actually stand.
The module resolves its root from __file__, so patching that points it at the tree; PLANNED is
module state, so a case that needs an entry sets one.

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
import json
import pathlib
import re
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("steering", ROOT / ".github/steering.py")
steering = importlib.util.module_from_spec(spec)
spec.loader.exec_module(steering)

# The live PLANNED list, kept because setUp empties the module's copy and the one case that runs
# against the repository needs the repository's own exemptions back.
LIVE_PLANNED = dict(steering.PLANNED)

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

# The contracts index as contracts/README.md carries it: prose, then a four-column table whose
# third column is the freeze cell. A case appends its own rows, so each one names the shape it is
# about and nothing else.
INDEX_PREAMBLE = """# Contracts

## What is here

| Contract | File | Freezes at | Consumed by |
| --- | --- | --- | --- |
"""

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

    def write_index(self, *rows: str) -> int:
        """Write the contracts index with these rows, and say which line the first row became."""
        self.write("contracts/README.md", INDEX_PREAMBLE + "".join(f"{row}\n" for row in rows))
        return len(INDEX_PREAMBLE.splitlines()) + 1

    def write_contract(self, relative: str, freeze: str) -> None:
        """A markdown contract, which carries its freeze as the first bullet under its title."""
        title = pathlib.Path(relative).stem
        body = f"# {title}\n\n- **Freezes at:** {freeze}\n- **Consumed by:** nobody\n"
        self.write(relative, body)

    def write_schema(self, relative: str, freeze: str) -> None:
        """A JSON schema, which carries its freeze as a sentence inside its description."""
        description = f"Source: docs/plan/02-contracts.md -> {relative}. Freezes at {freeze}."
        self.write(relative, json.dumps({
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "title": pathlib.Path(relative).stem,
            "description": description,
            "type": "object",
        }, indent=2) + "\n")

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

    def test_a_markdown_row_that_disagrees_with_its_header_fails(self):
        number = self.write_index(
            "| Pipeline and entry points | [`pipeline.md`](pipeline.md) | G1b | Everyone |"
        )
        self.write_contract("contracts/pipeline.md", "G1a")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: the contracts index and a contract's own freeze header "
                      "disagree.", out)
        self.assertIn(
            f"contracts/README.md:{number}: row 'Pipeline and entry points': the index cell "
            "'G1b' names G1b, and contracts/pipeline.md's header "
            "'Freezes at: G1a' names G1a",
            out,
        )

    def test_a_header_whose_bullet_runs_on_is_quoted_to_the_end_of_its_freeze(self):
        """http-contract.md's shape: the freeze, then its reason, over more than one line.

        The message has to carry the header, and a quote cut at the end of the first line stops
        mid-clause -- which is the one thing the message exists to save its reader.
        """
        number = self.write_index(
            "| Browser contract | [`http-contract.md`](http-contract.md) | G1b | Workstream 3 |"
        )
        self.write("contracts/http-contract.md",
                   "# Browser contract\n\n"
                   "- **Freezes at:** G1a. The cookie table and the `/bff` endpoints describe "
                   "identity behaviour, so\n"
                   "  those parts freeze at G1b, recorded by the annotated `contracts-v1` tag.\n")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(
            f"contracts/README.md:{number}: row 'Browser contract': the index cell 'G1b' names "
            "G1b, and contracts/http-contract.md's header 'Freezes at: G1a' names G1a",
            out,
        )

    def test_a_schema_row_that_disagrees_with_its_description_fails(self):
        number = self.write_index(
            "| Platform configuration | [`platform-config.schema.json`](platform-config.schema."
            "json), [`platform-config.example.json`](platform-config.example.json) | G1b "
            "| Workstream 1 |"
        )
        self.write_schema("contracts/platform-config.schema.json", "G1a")
        self.write("contracts/platform-config.example.json", "{}\n")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: the contracts index and a contract's own freeze header "
                      "disagree.", out)
        self.assertIn(
            f"contracts/README.md:{number}: row 'Platform configuration': the index cell 'G1b' "
            "names G1b, and contracts/platform-config.schema.json's header 'Freezes at G1a.' "
            "names G1a",
            out,
        )

    def test_a_cell_that_says_more_than_its_header_passes(self):
        """Both forms the live index uses: a change after the freeze, and an excepted section."""
        for cell in ("G1a; stage order changed at G1b", "G1a, except the `oauth` section at G1b"):
            with self.subTest(cell=cell):
                self.write_index(f"| Pipeline | [`pipeline.md`](pipeline.md) | {cell} | Everyone |")
                self.write_contract("contracts/pipeline.md", "G1a")
                code, out = self.check()
                self.assertEqual(0, code, out)

    def test_a_cell_whose_leading_gate_is_the_later_one_fails(self):
        """The same sentence with the gates swapped: a second gate named as the freeze itself."""
        number = self.write_index(
            "| Registry document | [`registry.schema.json`](registry.schema.json) "
            "| G1b, except the `oauth` section at G1a | Workstreams 2, 3, 4 |"
        )
        self.write_schema("contracts/registry.schema.json",
                          "G1a, except $defs/oauth, which freezes at G1b")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"contracts/README.md:{number}: row 'Registry document': the index cell "
                      "'G1b, except the `oauth` section at G1a' names G1b, and ", out)

    def test_a_header_whose_gate_falls_on_the_continuation_line_is_still_read(self):
        """A bullet wraps where its author's line ran out, which is not where its claim ends."""
        number = self.write_index(
            "| Request gate | [`request-gate.md`](request-gate.md) | G1a | Workstream 4 |"
        )
        self.write("contracts/request-gate.md",
                   "# Request gate\n\n"
                   "- **Freezes at:**\n"
                   "  G1b, with the browser contract. Workstream 4 owns the gate.\n")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(
            f"contracts/README.md:{number}: row 'Request gate': the index cell 'G1a' names G1a, "
            "and contracts/request-gate.md's header 'Freezes at: G1b, with the browser contract' "
            "names G1b",
            out,
        )

    def test_a_link_carrying_a_fragment_is_still_followed(self):
        """The index links into a schema's $defs elsewhere, so a fragment is a shape to expect."""
        self.write_index(
            "| Registry document | [`registry.schema.json`]"
            "(registry.schema.json#/$defs/oauth) | G1b | Workstreams 2, 3, 4 |"
        )
        self.write_schema("contracts/registry.schema.json", "G1a")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("contracts/registry.schema.json's header 'Freezes at G1a.' names G1a", out)

    def test_a_cell_carrying_an_escaped_pipe_is_still_compared(self):
        """A pipe is escaped in a markdown cell, and the escape must not cost the comparison."""
        self.write_index(
            "| Telemetry | [`telemetry.md`](telemetry.md) | G1b \\| see the note below "
            "| Everyone |"
        )
        self.write_contract("contracts/telemetry.md", "G1a")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("the index cell 'G1b \\| see the note below' names G1b", out)

    def test_a_row_the_rule_cannot_read_is_reported_rather_than_skipped(self):
        """A row whose cells do not line up is a row whose claim nothing compares.

        Silence is the one answer this must not give: the disagreement would vanish instead of
        failing, which is the defect the whole rule exists to stop.
        """
        number = self.write_index("| Telemetry | [`telemetry.md`](telemetry.md) | G1b |")
        self.write_contract("contracts/telemetry.md", "G1a")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: the contracts index has a row this rule cannot read.", out)
        self.assertIn(f"contracts/README.md:{number}: 3 cells in a 4-column table", out)

    def test_a_relational_cell_naming_no_gate_passes(self):
        """Two rows defer to another row rather than naming a gate, and neither is a failure."""
        rows = (
            ("With the registry document", "[`app-definition.schema.json`]"
                                           "(app-definition.schema.json)"),
            ("With the interface each one stands in for", "`src/Platform.Testing` (task I-3)"),
        )
        for cell, files in rows:
            with self.subTest(cell=cell):
                self.write_index(f"| A row | {files} | {cell} | Everyone |")
                self.write_schema("contracts/app-definition.schema.json", "G1a")
                code, out = self.check()
                self.assertEqual(0, code, out)

    def test_a_row_pointing_at_source_code_passes(self):
        """`src/Platform.Abstractions` is a directory of code, and carries no freeze header."""
        self.write_index("| Records and interfaces | `src/Platform.Abstractions` (task I-3) "
                         "| G1a, except the identity interfaces at G1b | Workstreams 2, 3, 4, 6 |")
        self.write("src/Platform.Abstractions/IAppRegistry.cs")
        code, out = self.check()
        self.assertEqual(0, code, out)

    def test_an_example_beside_its_schema_carries_no_header_and_passes(self):
        """The second file in a schema row is sample data: JSON with no description to read."""
        self.write_index(
            "| Registry document | [`registry.schema.json`](registry.schema.json), "
            "[`registry.example.json`](registry.example.json) | G1a | Workstreams 2, 3, 4 |"
        )
        self.write_schema("contracts/registry.schema.json", "G1a")
        self.write("contracts/registry.example.json", '{"schemaVersion": 1, "revision": 42}\n')
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("contracts/README.md agrees with the 1 freeze header it "
                      "indexes", out)

    def test_the_live_index_agrees_with_every_header_it_points_at(self):
        """The one case against the real tree: the shapes above are the shapes actually in it.

        Pointed back at the repository, so the green side is the index and the contracts as they
        stand rather than a tree written to agree with itself. setUp's cleanups undo both.

        The count is asserted as a floor, because a green exit on its own is what a rule that
        compared nothing also prints. The floor is what the index holds today, and a row added to
        it may raise the number but cannot lower it.
        """
        steering.__file__ = str(ROOT / ".github" / "steering.py")
        steering.PLANNED = LIVE_PLANNED
        code, out = self.check()
        self.assertEqual(0, code, out)
        counted = re.search(rf"{steering.INDEX} agrees with the (\d+) freeze headers?", out)
        self.assertIsNotNone(counted, out)
        self.assertGreaterEqual(int(counted.group(1)), 10, out)


if __name__ == "__main__":
    unittest.main(verbosity=2)
