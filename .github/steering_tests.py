#!/usr/bin/env python3
"""Regressions for the four rules .github/steering.py holds.

Owner: integrator. Run: python3 -I .github/steering_tests.py

Each case builds a throwaway tree -- a pair of steering files, a contracts index, a tracker
directory, and whatever a pointer in them is supposed to resolve to -- rather than asserting
against the live repository, which is green and so proves nothing about the red side. The freeze
rule and the citation rule each get one case against the repository as well, because their green
side is the index, the contracts and the tracker as they actually stand. The module resolves its
root from __file__, so patching that points it at the tree; PLANNED and UNFILED are module state,
so a case that needs an entry sets one.

The tree is a git repository, because the citation rule reads the files git tracks: a transcript
under `.scratch/<slug>/log/` is ignored on purpose, and a rule that read it would fail a push over
a file nobody can cite. check() stages the tree before it runs, so each case's own writes are
tracked by the time the rule looks.

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
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("steering", ROOT / ".github/steering.py")
steering = importlib.util.module_from_spec(spec)
spec.loader.exec_module(steering)

# The live exemption lists, kept because setUp empties the module's copies and the two cases that
# run against the repository need the repository's own exemptions back.
LIVE_PLANNED = dict(steering.PLANNED)
LIVE_UNFILED = dict(steering.UNFILED)

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

# Every citation below is assembled from these pieces rather than written out, and so is every
# assertion quoting one back. This file is tracked, and the rule under test reads tracked files:
# a citation spelled out here would be a citation *this repository* makes, and a red fixture's
# number resolves to nothing by design -- so writing one literally would fail the check on the
# very file that tests it. The pieces never sit adjacent in the source, so the rule's pattern
# finds no citation here.
WORD = "ticket"

# Read out of the module rather than written out beside it: a guard that repeats the value it
# protects still agrees with itself after the value is renamed.
TRACKER = steering.DEFAULT_TRACKER
OTHER_TRACKER = "cross-reference-checks"


def issues_in(tracker: str) -> str:
    """Where a tracker's tickets live, spelled the way the rule spells it in its messages."""
    return f"{steering.TRACKER_ROOT}/{tracker}/{steering.ISSUES}"


def cite(*numbers: str, tracker: str = "") -> str:
    """One citation in the tree's own words.

    The three shapes actually in the tree, and the three the rule promises to match: `ticket NN`
    from one number, `tickets NN and NN` from two, and `<tracker> ticket NN` with a tracker named.
    """
    head = WORD if len(numbers) == 1 else WORD + "s"
    if tracker:
        head = f"{tracker} {head}"
    return f"{head} " + " and ".join(numbers)


def unresolved(number: str, tracker: str) -> str:
    """The failure line the rule prints, assembled the same way for the same reason."""
    return f"cites {WORD} {number}, which resolves to no file under {issues_in(tracker)}/"


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
        # PLANNED and UNFILED are the live repository's lists; a case that wants an entry says so.
        self.addCleanup(setattr, steering, "PLANNED", steering.PLANNED)
        steering.PLANNED = {}
        self.addCleanup(setattr, steering, "UNFILED", steering.UNFILED)
        steering.UNFILED = {}

        # The citation rule reads the files git tracks, so the tree has to be a repository for it
        # to see anything at all. No commit is needed: `git ls-files` reads the index, and
        # check() stages whatever the case has written by then.
        self.git("init", "--quiet")

        self.reset()
        for pointer in CLEAN_POINTERS:
            self.write(pointer)

    def git(self, *arguments: str) -> None:
        subprocess.run(("git", *arguments), cwd=self.root, check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

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

    def write_ticket(self, tracker: str, number: str, slug: str = "a-ticket") -> None:
        """File a ticket in a tracker, at the path the convention gives it."""
        self.write(f"{issues_in(tracker)}/{number}-{slug}.md", f"# {number}\n")

    def cite_in(self, relative: str, text: str) -> int:
        """Write a document whose body carries a citation, and say which line it landed on."""
        self.write(relative, f"# A document\n\n{text}\n")
        return 3

    def check(self) -> tuple[int, str]:
        """The exit code and output of the check, as CI reads them."""
        # Staged here rather than in setUp, because a case writes its own files after setUp ran
        # and the rule only sees what the index holds.
        self.git("add", "--all")
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
        steering.UNFILED = LIVE_UNFILED
        code, out = self.check()
        self.assertEqual(0, code, out)
        counted = re.search(rf"{steering.INDEX} agrees with the (\d+) freeze headers?", out)
        self.assertIsNotNone(counted, out)
        self.assertGreaterEqual(int(counted.group(1)), 10, out)

    # --- a cited ticket number resolves to a file ---------------------------------------------

    def test_a_citation_resolving_in_its_own_tracker_passes(self):
        """The green side, and the count it prints.

        The count is what the assertion holds, not the exit code: a rule deleted outright also
        exits 0, and the sentence has to name how many citations it actually followed.
        """
        self.write_ticket("overnight", "06")
        self.cite_in(f"{issues_in(TRACKER)}/07-a-spec.md", f"Blocked by {cite('06')}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("1 ticket citation resolves to a spec.", out)

    def test_two_citations_are_both_counted(self):
        """The singular above and the plural here, so the count is a number and not a word."""
        self.write_ticket("overnight", "06")
        self.write_ticket("overnight", "08")
        self.cite_in(f"{issues_in(TRACKER)}/07-a-spec.md",
                     f"Blocked by {cite('06', '08')}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("2 ticket citations resolve to a spec.", out)

    def test_a_citation_at_a_number_its_own_tracker_lacks_fails(self):
        """`ticket NN`: the first of the three shapes, and the one the drift was written in."""
        self.write_ticket("overnight", "06")
        line = self.cite_in(f"{issues_in(TRACKER)}/07-a-spec.md", f"Blocked by {cite('99')}.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a cited ticket number resolves to no file.", out)
        self.assertIn(f"{issues_in(TRACKER)}/07-a-spec.md:{line}: "
                      f"{unresolved('99', 'overnight')}", out)
        self.assertIn("UNFILED in this script with the reason it is named early", out)

    def test_the_plural_shape_reports_each_number_separately(self):
        """`tickets NN and NN`: the shape the session that wrote the dangling citation used.

        Both numbers are reported, because a rule reading only the first would have passed the
        very line it exists to catch half the time.
        """
        self.write_ticket("overnight", "06")
        line = self.cite_in(f"{issues_in(TRACKER)}/07-a-spec.md",
                            f"Merged code cited {cite('98', '99')} while trunk stopped at 06.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"07-a-spec.md:{line}: {unresolved('98', 'overnight')}", out)
        self.assertIn(f"07-a-spec.md:{line}: {unresolved('99', 'overnight')}", out)

    def test_a_named_tracker_overrides_the_one_the_citing_file_sits_in(self):
        """`overnight ticket NN`: the third shape, and the whole reason resolution is relative.

        The number is filed in the tracker the citation names and absent from the citing file's
        own, so a rule ignoring the name would fail this and a global map would resolve it to the
        wrong file.
        """
        self.write_ticket("overnight", "53")
        self.cite_in(f"{issues_in(OTHER_TRACKER)}/03-a-spec.md",
                     f"Raised as {cite('53', tracker=TRACKER)}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("1 ticket citation resolves to a spec.", out)

    def test_the_same_number_unqualified_resolves_against_the_citing_directory_and_fails(self):
        """The line above with the tracker's name taken off: the same number, a different file.

        This is the pair that makes the decision visible. Numbering restarts per tracker, so
        `ticket 53` inside one tracker is not `overnight ticket 53`, and the rule has to read the
        two differently or it reads neither correctly.
        """
        self.write_ticket("overnight", "53")
        line = self.cite_in(f"{issues_in(OTHER_TRACKER)}/03-a-spec.md",
                            f"Raised as {cite('53')}.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"03-a-spec.md:{line}: {unresolved('53', 'cross-reference-checks')}", out)

    def test_a_named_tracker_that_lacks_the_number_fails(self):
        """Naming a tracker is not an exemption: the number still has to be filed there.

        The named tracker exists and holds other tickets; it is this number it does not hold.
        """
        self.write_ticket("overnight", "53")
        line = self.cite_in(f"{issues_in(OTHER_TRACKER)}/03-a-spec.md",
                            f"Raised as {cite('99', tracker=TRACKER)}.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"03-a-spec.md:{line}: {unresolved('99', 'overnight')}", out)

    def test_a_document_outside_the_tracker_resolves_against_the_overnight_tracker(self):
        """An ADR and a contract cite the plan-wide tracker without naming it, so that is the
        default, and `src`, `docs` and `infra` are not trackers of their own."""
        self.write_ticket("overnight", "49")
        self.cite_in("docs/adr/0019-a-decision.md", f"This decides only what {cite('49')} does.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("1 ticket citation resolves to a spec.", out)

    def test_a_document_outside_the_tracker_citing_an_unfiled_number_fails(self):
        self.write_ticket("overnight", "49")
        line = self.cite_in("docs/adr/0019-a-decision.md", f"Blocked on {cite('99')}.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"docs/adr/0019-a-decision.md:{line}: "
                      f"{unresolved('99', 'overnight')}", out)

    def test_a_citation_wrapped_over_two_lines_is_still_read(self):
        """The prose here wraps near column 100, so a split citation is the ordinary case.

        Eight are in the tree today -- `Ticket\\n49` in docs/adr/0015 among them -- and a scan
        reading one line at a time saw none of them. That is this rule's own silent pass: the
        shape it exists to catch, missed by the shape of the scan, and indistinguishable from a
        tree whose citations all resolve.
        """
        self.write_ticket(TRACKER, "06")
        self.write(f"{issues_in(TRACKER)}/07-a-spec.md",
                   f"# A spec\n\nThe argument runs on to the end of the line, naming {WORD}\n"
                   f"99 where the next one starts.\n")
        code, out = self.check()
        self.assertEqual(1, code, out)
        # The line the citation starts on, which is where the word is, not where the number is.
        self.assertIn(f"{issues_in(TRACKER)}/07-a-spec.md:3: {unresolved('99', TRACKER)}", out)

    def test_a_citation_is_not_read_across_a_blank_line(self):
        """One line break and no more: a heading ending in the word and a list item starting with
        a number are two statements, and joining them would cite what neither wrote."""
        self.write_ticket(TRACKER, "06")
        self.write(f"{issues_in(TRACKER)}/07-a-spec.md",
                   f"# A spec\n\n## Every {WORD}\n\n99 of them are still open.\n")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_scratch_directory_with_no_issues_falls_back_to_the_plan_wide_tracker(self):
        """Three directories under .scratch/ hold working notes and no issues/ directory.

        Read as trackers they would resolve every unqualified citation in them against a directory
        that cannot exist, and the only way to write one would be to name another tracker.
        """
        self.write_ticket(TRACKER, "49")
        self.cite_in(f"{steering.TRACKER_ROOT}/some-working-notes/findings.md",
                     f"Measured for {cite('49')}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("1 ticket citation resolves to a spec.", out)

    def test_a_plan_task_id_is_not_read_as_a_ticket_number(self):
        """`workstream 3's tickets 3.3, 3.5 and 3.9` cites plan rows, not tracker files.

        Read as ticket 3 the line would be a dangling citation in a file nobody will change, so
        the rule would be switched off within a week of landing.
        """
        self.write_ticket("overnight", "46")
        self.cite_in(f"{issues_in(TRACKER)}/46-a-spec.md",
                     f"Add notes to workstream 3's {WORD}s 3.3, 3.5 and 3.9.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_three_digit_number_is_not_read_as_its_first_two_digits(self):
        """No ticket has three digits, and reading 100 as 10 would resolve to the wrong file."""
        self.write_ticket("overnight", "10")
        self.cite_in(f"{issues_in(TRACKER)}/11-a-spec.md", f"See {WORD} 100.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertNotIn("ticket citation", out)

    def test_a_citation_in_an_untracked_file_is_not_read(self):
        """A session transcript is ignored by git on purpose; it is a transcript, not a spec."""
        self.write_ticket("overnight", "06")
        self.write(".gitignore", f"{steering.TRACKER_ROOT}/*/log/\n")
        self.cite_in(f"{steering.TRACKER_ROOT}/{TRACKER}/log/session.md",
                     f"I was looking at {cite('99')}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_number_resolving_to_two_files_resolves(self):
        """The tracker holds two files numbered 61 today.

        A citation of it lands on a spec, which is all this rule promises; that two files share a
        number is a different defect, and the rule that catches it is its own.
        """
        self.write_ticket("overnight", "61", "one-slug")
        self.write_ticket("overnight", "61", "another-slug")
        self.cite_in(f"{issues_in(TRACKER)}/62-a-spec.md", f"Follows {cite('61')}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("1 ticket citation resolves to a spec.", out)

    def test_an_unfiled_ticket_is_exempt_while_absent(self):
        why = "the integrator files it with the decision it records"
        steering.UNFILED = {("overnight", "68"): why}
        self.write_ticket("overnight", "06")
        self.cite_in(f"{issues_in(TRACKER)}/07-a-spec.md", f"Superseded by {cite('68')}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_an_unfiled_ticket_that_has_been_filed_fails(self):
        """The list cannot outlive its entries, which is PLANNED's rule one level over."""
        why = "the integrator files it with the decision it records"
        steering.UNFILED = {("overnight", "68"): why}
        self.write_ticket("overnight", "68")
        self.cite_in(f"{issues_in(TRACKER)}/07-a-spec.md", f"Superseded by {cite('68')}.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a ticket on UNFILED has been filed.", out)
        self.assertIn(f"{WORD} 68 is filed under {issues_in(TRACKER)}/ now ({why})", out)
        self.assertIn("Remove it from UNFILED", out)

    def test_an_unfiled_ticket_that_was_filed_fails_even_when_nothing_cites_it(self):
        steering.UNFILED = {("overnight", "68"): "filed with the decision it records"}
        self.write_ticket("overnight", "68")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a ticket on UNFILED has been filed.", out)

    def test_every_citation_in_the_live_tree_resolves(self):
        """The one citation case against the real tree: 400-odd citations in the shapes above.

        The count is asserted as a floor for the reason the freeze count is: a green exit on its
        own is what a rule reading no files also prints. The floor is what the tree holds today,
        and a document added to it may raise the number but cannot lower it.
        """
        steering.__file__ = str(ROOT / ".github" / "steering.py")
        steering.PLANNED = LIVE_PLANNED
        steering.UNFILED = LIVE_UNFILED
        code, out = self.check()
        self.assertEqual(0, code, out)
        counted = re.search(r"(\d+) ticket citations? resolves? to a spec\.", out)
        self.assertIsNotNone(counted, out)
        self.assertGreaterEqual(int(counted.group(1)), 350, out)


if __name__ == "__main__":
    unittest.main(verbosity=2)
