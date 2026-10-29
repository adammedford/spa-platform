#!/usr/bin/env python3
"""Regressions for the rules .github/steering.py holds.

Owner: integrator. Run: python3 -I .github/steering_tests.py

Each case builds a throwaway tree -- a pair of steering files, a contracts index, a tracker
directory, and whatever a pointer in them is supposed to resolve to -- rather than asserting
against the live repository, which is green and so proves nothing about the red side. The freeze,
citation and numbering rules each get one case against the repository as well, because their green
side is the index, the contracts and the tracker as they actually stand. The module resolves its
root from __file__, so patching that points it at the tree; PLANNED, UNFILED, TRANSCRIPTS and
UNCOMPARED are module state, so a case that needs an entry sets one.

No case reaches GitHub. The `#NNN` rule asks it which numbers exist, and that lookup is module
state too: setUp replaces it with one that fails the case, so a tree carrying a `#NNN` has to say
what GitHub would answer, through github() or offline() below. The rule's green side lives on GitHub
and not in the tree, so its one case against the repository holds the scan -- how many citations the
tree carries -- and leaves the resolution to the check itself, which CI runs with the job's token.

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

The authority rule reads commits rather than the tree, so its cases commit: every commit made at a
time the case names, either side of the rule's starting point, and never at the clock's, since
which side a commit falls on is what the rule reads. They name the range as the hook and CI do,
with `--base`, and the cases against the live repository name an empty one, `--base HEAD`, so a
case about the tree does not also judge the repository's history. The comment lookup is a second
GitHub seam, stubbed in setUp as the number lookup is, and the lookup of one number the listing
lacks is a third (#297); each lookup's own parsing is held by cases that point its command at a
stand-in for gh rather than at GitHub, and one case runs the whole rule through those stand-ins
with a listing that stops a page short.
"""

from __future__ import annotations

import contextlib
import datetime
import importlib.util
import io
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("steering", ROOT / ".github/steering.py")
steering = importlib.util.module_from_spec(spec)
spec.loader.exec_module(steering)

# The live exemption lists, kept because setUp empties the module's copies and the cases that run
# against the repository need the repository's own exemptions back.
LIVE_PLANNED = dict(steering.PLANNED)
LIVE_UNFILED = dict(steering.UNFILED)
LIVE_TRANSCRIPTS = dict(steering.TRANSCRIPTS)
LIVE_UNCOMPARED = dict(steering.UNCOMPARED)

# The GitHub lookups as the module defines them, kept because setUp replaces each with a stub
# that fail the case, and the cases holding their own parsing need the real functions back.
REAL_ISSUED_NUMBERS = steering.issued_numbers
REAL_COMMENT_URL = steering.comment_url
REAL_ISSUE_KIND = steering.issue_kind

# Either side of the authority rule's starting point, read from the module rather than typed, so
# moving the starting point moves these with it: a day before it, and an hour after.
SINCE = steering.AUTHORITY_SINCE
BEFORE = SINCE - datetime.timedelta(days=1)
AFTER = SINCE + datetime.timedelta(hours=1)

# Either side of the decision rule's starting point, read from the module the same way.
DECIDED_AFTER = steering.DECIDED_SINCE + datetime.timedelta(hours=1)
DECIDED_BEFORE = steering.DECIDED_SINCE - datetime.timedelta(hours=1)

# A change request as every one under contracts/changes/ writes its Status, open and decided.
OPEN_REQUEST = "# A change request\n\n- **Raised by:** a lane\n- **Status:** open\n"
DECIDED_REQUEST = ("# A change request\n\n- **Raised by:** a lane\n"
                   "- **Status:** decided, 2026-10-20. See the Decision section below.\n")

# One file in each place ADR-0023 decision 2 names as a steering document, typed here rather than
# read from the module: the case holding the module's list to the ADR is a separate one.
STEERING_EXAMPLES = (
    "GLOSSARY.md",
    "AGENTS.md",
    "CLAUDE.md",
    "OWNERSHIP.md",
    "CODING_STANDARDS.md",
    "docs/agents/review.md",
    "docs/adr/0024-a-decision.md",
    "contracts/pipeline.md",
)

# Paths that sit beside a steering document and are not one: a prefix match on the names above, or
# a name matched anywhere in the tree rather than at the root, would hold each of these.
STEERING_NEIGHBOURS = (
    "GLOSSARY.md.orig",
    "docs/GLOSSARY.md",
    "src/AGENTS.md",
    "docs/agents-archive/review.md",
    "docs/adrs/0001-a-decision.md",
    "contracts-old/pipeline.md",
)

# A comment on an issue, in the form GitHub writes a comment's URL, and the comment id in it.
COMMENT_ID = "6253500774"
COMMENT = f"https://github.com/adammedford/spa-platform/issues/98#issuecomment-{COMMENT_ID}"

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

# The two rows of the live index that one relational cell joins: the App definition row names no
# gate and defers to the Registry document row, which names one. Each is the live row's shape --
# its Contract and Freezes at cells as the index writes them -- trimmed to the schema it points at,
# since the example files carry no header. A copy of the whole row would agree with itself whatever
# the index said; what holds the live rows is the case against the repository, whose floor counts
# app-definition.schema.json through the Registry document row.
REGISTRY_ROW = (
    "| Registry document | [`registry.schema.json`](registry.schema.json) "
    "| G1a, except the `oauth` section at G1b | Workstreams 2, 3, 4 |"
)
APP_DEFINITION_ROW = (
    "| App definition | [`app-definition.schema.json`](app-definition.schema.json) "
    "| With the registry document | Workstreams 1, 2 and 5 |"
)

# The one relational cell that names no row at all, on the row the live index carried it in until
# #145 widened the row, and the UNCOMPARED key that exempts that row and no other. The fixtures
# exercise the exemption's mechanism, which the widening did not change.
TEST_DOUBLES_CELL = "With the interface each one stands in for"
TEST_DOUBLES_ROW = (
    f"| Test doubles | `src/Platform.Testing` (task I-3) | {TEST_DOUBLES_CELL} | Everyone |"
)
TEST_DOUBLES_KEY = ("Test doubles", TEST_DOUBLES_CELL)

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


# The `#NNN` citations are assembled for the same reason: a `#` written against a digit here would be
# a citation this repository makes, of a number chosen to name nothing.
HASH = "#"


def issue(number: int) -> str:
    """One `#NNN` citation, in the form GitHub links."""
    return f"{HASH}{number}"


def unissued(number: int) -> str:
    """The failure line the `#NNN` rule prints."""
    return f"cites {issue(number)}, which names no issue or pull request of this repository"


def message(subject: str, *authorities: str) -> str:
    """A commit message as this repository writes one: a subject, a body, and a last paragraph of
    trailers, one `Authority:` line per authority, beside the attribution line."""
    trailers = "".join(f"Authority: {authority}\n" for authority in authorities)
    return (f"{subject}\n\nWhat changed and why.\n\n{trailers}"
            "Co-Authored-By: Steering Tests <steering-tests@example.test>\n")


def holds(sha: str, subject: str, *paths: str) -> str:
    """The line the authority rule prints for a commit that names no authority."""
    return (f'commit {sha[:12]} ("{subject}") changes {", ".join(paths)} and carries no '
            "Authority: trailer")


def undecides(sha: str, subject: str, path: str, *authorities: str,
              state: str = "decided") -> str:
    """The line the decision rule prints for a commit that sets a change request to a decision on
    no confirmed answer's URL."""
    return (f'commit {sha[:12]} ("{subject}") changes {path}, sets {path} to {state}, and cites '
            f"no confirmed answer: Authority: {', '.join(authorities)}")


def unreadable(sha: str, subject: str, path: str) -> str:
    """The line the decision rule prints for a change request whose Status it cannot read."""
    return f'commit {sha[:12]} ("{subject}") leaves {path} with a Status this check cannot read'


UNREADABLE_HEADLINE = "STEERING FAILED: a change request's Status cannot be read."

# Status lines in shapes no change request has used, each of which a writer could reach for, and
# none of which STATUS reads. #188's review found each read as open, so each decided request passed.
OTHER_SHAPES = (
    ("bold label, colon outside", "**Status**: decided, 2026-10-20."),
    ("plain list item", "- Status: decided, 2026-10-20."),
    ("numbered item", "1. **Status:** decided, 2026-10-20."),
    ("italic state", "- **Status:** _decided_, 2026-10-20."),
    ("table row", "| Field | Value |\n| --- | --- |\n| Status | decided |"),
    ("heading", "## Status\n\nDecided, 2026-10-20."),
    ("lower case", "- **status:** decided, 2026-10-20."),
)


def refuses(sha: str, subject: str, paths: tuple[str, ...], authority: str, why: str) -> str:
    """The line the authority rule prints for an `Authority:` trailer that resolves to nothing."""
    return (f'commit {sha[:12]} ("{subject}") changes {", ".join(paths)}, and Authority: '
            f"{authority} {why}")


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
        self.addCleanup(setattr, steering, "TRANSCRIPTS", steering.TRANSCRIPTS)
        steering.TRANSCRIPTS = {}
        self.addCleanup(setattr, steering, "UNCOMPARED", steering.UNCOMPARED)
        steering.UNCOMPARED = {}
        # The `#NNN` rule's lookup, replaced so that no case reaches GitHub: a tree carrying a
        # citation says what GitHub would answer, or the case fails on this stub. The authority
        # rule's comment lookup likewise, for a trailer naming a comment's URL.
        self.addCleanup(setattr, steering, "issued_numbers", steering.issued_numbers)
        steering.issued_numbers = self.no_lookup
        self.addCleanup(setattr, steering, "comment_url", steering.comment_url)
        steering.comment_url = self.no_comment_lookup
        # The authority rule's lookup of one number the listing lacks, likewise: a trailer citing
        # a number outside github()'s listing says what GitHub would answer with looked_up().
        self.addCleanup(setattr, steering, "issue_kind", steering.issue_kind)
        steering.issue_kind = self.no_issue_lookup
        # The commands the real lookups run, which the cases holding their parsing point at a
        # stand-in for gh.
        self.addCleanup(setattr, steering, "GH_ISSUES", steering.GH_ISSUES)
        self.addCleanup(setattr, steering, "GH_COMMENT", steering.GH_COMMENT)
        self.addCleanup(setattr, steering, "GH_ISSUE", steering.GH_ISSUE)

        # The citation rule reads the files git tracks, so the tree has to be a repository for it
        # to see anything at all. No commit is needed: `git ls-files` reads the index, and
        # check() stages whatever the case has written by then. The authority rule reads commits,
        # so the repository can make them: an identity, no signing, and no housekeeping, since a
        # commit otherwise starts a detached `git maintenance run --auto` that can still be writing
        # into .git when the case's cleanup removes it (ThrowawayRepositoryTests in
        # .github/path_ownership_tests.py has the detail).
        self.git("init", "--quiet")
        self.git("config", "maintenance.auto", "false")
        self.git("config", "user.name", "Steering Tests")
        self.git("config", "user.email", "steering-tests@example.test")
        self.git("config", "commit.gpgsign", "false")

        self.reset()
        for pointer in CLEAN_POINTERS:
            self.write(pointer)

    def git(self, *arguments: str, env: dict[str, str] | None = None) -> str:
        done = subprocess.run(("git", *arguments), cwd=self.root, check=True, env=env,
                              capture_output=True, text=True)
        return done.stdout.strip()

    def no_lookup(self, root: pathlib.Path) -> tuple[set[int], set[int], str]:
        self.fail("the check asked GitHub for its numbers; a case whose tree cites a `#NNN` says "
                  "what GitHub would answer with github() or offline()")

    def no_comment_lookup(self, root: pathlib.Path, comment: str) -> tuple[str | None, str]:
        self.fail("the check asked GitHub for a comment; a case whose trailer names one says what "
                  "GitHub would answer with comments()")

    def no_issue_lookup(self, root: pathlib.Path, number: int) -> tuple[str | None, str]:
        self.fail("the check asked GitHub for one number; a case whose trailer cites a number the "
                  "listing lacks says what GitHub would answer with looked_up()")

    def looked_up(self, kinds: dict[int, str], why: str = "") -> None:
        """What GitHub would answer when asked for one number the listing lacks: "issue" or
        "pull" for a number in `kinds`, None (its 404) for any other; or, given `why`, that it
        could not say."""
        steering.issue_kind = lambda root, number: (None, why) if why else (kinds.get(number), "")

    def github(self, *numbers: int, pulls: tuple[int, ...] = ()) -> None:
        """What GitHub would answer: these numbers are issues, and `pulls` are pull requests."""
        steering.issued_numbers = lambda root: (set(numbers), set(pulls), "")

    def offline(self, why: str) -> None:
        """What the lookup says when GitHub cannot be asked: no numbers, and the reason."""
        steering.issued_numbers = lambda root: (set(), set(), why)

    def comments(self, kept: dict[str, str], why: str = "", author: str = "adammedford") -> None:
        """What GitHub would answer for a comment id: the URL it keeps that comment at and who
        wrote it, by default the repository's owner, or None for an id it has no comment under
        here; or, given `why`, that it could not be asked."""
        def answer(root, comment):
            if why:
                return None, why
            url = kept.get(comment)
            return (steering.Comment(url, author) if url else None), ""
        steering.comment_url = answer

    def commit(self, subject_or_message: str, *, made: datetime.datetime = AFTER,
               authored: datetime.datetime | None = None) -> str:
        """Commit everything the case has written, and say which commit it became.

        Made at the time the case names, never the clock's, and authored then too unless the case
        says otherwise: which side of the starting point a commit falls on is what the rule reads.
        """
        self.git("add", "--all")
        dates = {"GIT_COMMITTER_DATE": made.isoformat(),
                 "GIT_AUTHOR_DATE": (authored or made).isoformat()}
        self.git("commit", "--quiet", "--allow-empty", "--no-verify", "-m", subject_or_message,
                 env={**os.environ, **dates})
        return self.git("rev-parse", "HEAD")

    def baseline(self) -> str:
        """The tree as setUp left it, committed a day before the starting point: the base a case's
        own commits are judged against, and itself a steering commit the rule must not hold."""
        return self.commit("The tree before the rule", made=BEFORE)

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

    def check(self, *arguments: str) -> tuple[int, str]:
        """The exit code and output of the check, as CI reads them, run with these arguments.

        The arguments are passed rather than left to sys.argv, which holds whatever this runner
        was started with: a test name or `-v` there is not an argument of the check's.
        """
        # Staged here rather than in setUp, because a case writes its own files after setUp ran
        # and the rule only sees what the index holds.
        self.git("add", "--all")
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = steering.main(list(arguments))
        return code, buffer.getvalue()

    def live(self) -> None:
        """Point the check back at the repository, with the repository's own exemption lists."""
        steering.__file__ = str(ROOT / ".github" / "steering.py")
        steering.PLANNED = LIVE_PLANNED
        steering.UNFILED = LIVE_UNFILED
        steering.TRANSCRIPTS = LIVE_TRANSCRIPTS
        steering.UNCOMPARED = LIVE_UNCOMPARED
        self.offline("a test never reaches GitHub")

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

    def test_a_relational_cell_is_compared_through_the_row_it_names(self):
        """The App definition row's shape: no gate of its own, so it borrows the registry's.

        The count is what the assertion holds, because the defect this case was written for is a
        header nothing compares, and a skipped header and a compared one both exit 0.
        """
        self.write_index(REGISTRY_ROW, APP_DEFINITION_ROW)
        self.write_schema("contracts/registry.schema.json", "G1a")
        self.write_schema("contracts/app-definition.schema.json", "G1a")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("contracts/README.md agrees with the 2 freeze headers it indexes", out)

    def test_a_relational_target_whose_header_disagrees_with_the_row_it_defers_to_fails(self):
        """The disagreement this rule passed in silence before: the registry row and the app
        definition's own header naming different gates, with nothing in between comparing them.

        The message names the row deferred to as well as both sides, because the cell alone names
        no gate, and a message quoting only the cell would not say where G1a came from.
        """
        number = self.write_index(REGISTRY_ROW, APP_DEFINITION_ROW) + 1
        self.write_schema("contracts/registry.schema.json", "G1a")
        self.write_schema("contracts/app-definition.schema.json", "G1b")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: the contracts index and a contract's own freeze header "
                      "disagree.", out)
        self.assertIn(
            f"contracts/README.md:{number}: row 'App definition': the index cell 'With the "
            "registry document' defers to row 'Registry document', which names G1a, and "
            "contracts/app-definition.schema.json's header 'Freezes at G1b.' names G1b",
            out,
        )

    def test_a_relational_cell_naming_no_row_the_index_holds_fails(self):
        """A cell deferring to nothing makes no claim anything can compare, which is the defect.

        Two shapes: a row the index never held, and the front of a row it does hold, so the
        lookup is shown to want the whole name and not a prefix of one.
        """
        for cell, named in (("With the routing table", "routing table"),
                            ("With the registry", "registry")):
            with self.subTest(cell=cell):
                number = self.write_index(
                    REGISTRY_ROW, APP_DEFINITION_ROW.replace("With the registry document", cell)
                ) + 1
                self.write_schema("contracts/registry.schema.json", "G1a")
                self.write_schema("contracts/app-definition.schema.json", "G1a")
                code, out = self.check()
                self.assertEqual(1, code, out)
                self.assertIn("STEERING FAILED: a freeze cell names neither a gate nor a row of "
                              "the contracts index.", out)
                self.assertIn(
                    f"contracts/README.md:{number}: row 'App definition': the index cell "
                    f"'{cell}' names no gate, and no row of the table is called '{named}'",
                    out,
                )

    def test_a_relational_cell_deferring_to_another_relational_cell_fails(self):
        """One hop and no more: the row deferred to has to name the gate the cell borrows.

        The index writes no chain, and a cycle -- two rows each deferring to the other -- would be
        a claim with no gate anywhere in it. Failing the second hop catches both without a walk.
        """
        number = self.write_index(
            REGISTRY_ROW,
            APP_DEFINITION_ROW,
            "| App example | [`app-definition.example.json`](app-definition.example.json) "
            "| With the app definition | Workstream 5 |",
        ) + 2
        self.write_schema("contracts/registry.schema.json", "G1a")
        self.write_schema("contracts/app-definition.schema.json", "G1a")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a freeze cell names neither a gate nor a row of the "
                      "contracts index.", out)
        self.assertIn(
            f"contracts/README.md:{number}: row 'App example': the index cell 'With the app "
            "definition' defers to row 'App definition', which names no gate either",
            out,
        )

    def test_an_uncompared_cell_is_exempt_while_a_row_carries_it(self):
        """The Test doubles row's shape: a cell deferring to one interface per double, and so to
        no single row, on a row whose target is code with no header to compare."""
        steering.UNCOMPARED = {TEST_DOUBLES_KEY: "each double has its own interface"}
        self.write_index(REGISTRY_ROW, TEST_DOUBLES_ROW)
        self.write_schema("contracts/registry.schema.json", "G1a")
        self.write("src/Platform.Testing/InMemoryRegistry.cs")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("contracts/README.md agrees with the 1 freeze header it indexes", out)

    def test_an_uncompared_cell_on_another_row_is_compared_like_any_other(self):
        """The exemption is the Test doubles row's, not its words': the reason given for it -- code
        with no header -- is false of a row pointing at a contract that carries one, and keyed by
        the cell alone that row's header would go uncompared on a reason nobody gave for it.
        """
        steering.UNCOMPARED = {TEST_DOUBLES_KEY: "each double has its own interface"}
        number = self.write_index(
            REGISTRY_ROW,
            TEST_DOUBLES_ROW,
            f"| Fixture contract | [`fixture.md`](fixture.md) | {TEST_DOUBLES_CELL} "
            "| Workstream 5 |",
        ) + 2
        self.write_schema("contracts/registry.schema.json", "G1a")
        self.write("src/Platform.Testing/InMemoryRegistry.cs")
        self.write_contract("contracts/fixture.md", "G2")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a freeze cell names neither a gate nor a row of the "
                      "contracts index.", out)
        self.assertIn(
            f"contracts/README.md:{number}: row 'Fixture contract': the index cell "
            f"'{TEST_DOUBLES_CELL}' names no gate, and no row of the table is called "
            "'interface each one stands in for'",
            out,
        )
        self.assertNotIn("row 'Test doubles'", out)

    def test_an_uncompared_cell_no_row_carries_fails(self):
        """The list cannot outlive its entries, which is PLANNED's rule one level over.

        The row is still there with one half of its key changed -- the cell reworded, or the row
        renamed -- which is the case that matters: the exemption was for that row saying those
        words, and either change is a claim nobody has decided about.
        """
        why = "each double has its own interface"
        for shape, row in (
            ("a reworded cell", TEST_DOUBLES_ROW.replace(TEST_DOUBLES_CELL,
                                                         "With the registry document")),
            ("a renamed row", TEST_DOUBLES_ROW.replace("| Test doubles |", "| Doubles |")),
        ):
            with self.subTest(shape=shape):
                steering.UNCOMPARED = {TEST_DOUBLES_KEY: why}
                self.write_index(REGISTRY_ROW, row)
                self.write_schema("contracts/registry.schema.json", "G1a")
                code, out = self.check()
                self.assertEqual(1, code, out)
                self.assertIn("STEERING FAILED: an UNCOMPARED cell is in no row that needs it.",
                              out)
                self.assertIn(f"no row 'Test doubles' has the freeze cell '{TEST_DOUBLES_CELL}' "
                              f"naming no row ({why})", out)
                self.assertIn("Remove it from UNCOMPARED", out)

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

    def test_a_target_with_no_header_by_design_passes_and_is_not_counted(self):
        """The three shapes that carry no header: a code file, a directory of code, and a schema's
        example. Each beside a contract that does carry one, so the count shows the headerless
        target was passed over rather than the row."""
        for shape, cell, write in (
            ("a code file", "[`pipeline.md`](pipeline.md), `src/Gateway/Program.cs`",
             lambda: self.write("src/Gateway/Program.cs")),
            ("a directory", "[`pipeline.md`](pipeline.md), `src/Platform.Abstractions`",
             lambda: self.write("src/Platform.Abstractions/")),
            ("a schema's example", "[`pipeline.md`](pipeline.md), "
             "[`pipeline.example.json`](pipeline.example.json)",
             lambda: self.write("contracts/pipeline.example.json", "{}\n")),
        ):
            with self.subTest(shape=shape):
                self.write_index(f"| Pipeline | {cell} | G1a | Everyone |")
                self.write_contract("contracts/pipeline.md", "G1a")
                write()
                code, out = self.check()
                self.assertEqual(0, code, out)
                self.assertIn("contracts/README.md agrees with the 1 freeze header it indexes",
                              out)

    def test_a_target_whose_header_cannot_be_read_fails(self):
        """#493 and #512: each of these was passed over in silence, and the check still said the
        index agreed. A row naming a contract that does not exist is the reproduction from #44's review:
        `ghost.md`, at a gate no contract names."""
        for shape, target, write, message in (
            ("a target that does not exist", "[`ghost.md`](ghost.md)", lambda: None,
             "ghost.md exists neither beside contracts/README.md nor at the repository root"),
            ("a contract with no freeze bullet", "[`telemetry.md`](telemetry.md)",
             lambda: self.write("contracts/telemetry.md", "# Telemetry\n\nNo header.\n"),
             "contracts/telemetry.md carries no '- **Freezes at:**' bullet"),
            ("a header naming no gate", "[`telemetry.md`](telemetry.md)",
             lambda: self.write_contract("contracts/telemetry.md", "when it lands"),
             "contracts/telemetry.md's header 'Freezes at: when it lands' names no gate"),
            ("a schema stating no freeze", "[`registry.schema.json`](registry.schema.json)",
             lambda: self.write("contracts/registry.schema.json",
                                json.dumps({"description": "The registry."}) + "\n"),
             "contracts/registry.schema.json is a schema whose description states no freeze"),
            ("JSON that does not parse", "[`registry.schema.json`](registry.schema.json)",
             lambda: self.write("contracts/registry.schema.json", "{\n"),
             "contracts/registry.schema.json is not JSON that parses"),
            # #512: the part before the '#' is empty, and an empty path resolved to contracts/
            # itself, which passed as a directory of code carrying no header by design.
            ("a link that is only a fragment", "[`pipeline`](#pipeline)", lambda: None,
             "#pipeline is only a fragment, and names no file"),
        ):
            with self.subTest(shape=shape):
                for stale in ("contracts/telemetry.md", "contracts/registry.schema.json"):
                    (self.root / stale).unlink(missing_ok=True)
                number = self.write_index(f"| A contract | {target} | G7z | Nobody |")
                write()
                code, out = self.check()
                self.assertEqual(1, code, out)
                self.assertIn("STEERING FAILED: a contracts index target has no readable freeze "
                              "header.", out)
                self.assertIn(f"contracts/README.md:{number}: row 'A contract': {message}", out)

    def test_the_live_index_agrees_with_every_header_it_points_at(self):
        """The one case against the real tree: the shapes above are the shapes actually in it.

        Pointed back at the repository, so the green side is the index and the contracts as they
        stand rather than a tree written to agree with itself. setUp's cleanups undo both.

        The count is asserted as a floor, because a green exit on its own is what a rule that
        compared nothing also prints. The floor is what the index holds today, and a row added to
        it may raise the number but cannot lower it. Eleven counts app-definition.schema.json,
        compared through the Registry document row its cell defers to; ten was the floor while a
        relational cell was passed over, so a rule that went back to skipping it goes red here.
        """
        self.live()
        code, out = self.check("--base", "HEAD")
        self.assertEqual(0, code, out)
        counted = re.search(rf"{steering.INDEX} agrees with the (\d+) freeze headers?", out)
        self.assertIsNotNone(counted, out)
        self.assertGreaterEqual(int(counted.group(1)), 11, out)

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
        self.cite_in(f"{issues_in(TRACKER)}/47-a-spec.md",
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
        """The overnight tracker held two files numbered 61 when this rule landed.

        A citation of it lands on a spec, which is all this rule promises; that two files share a
        number is a different defect, and the rule that catches it is its own -- so the push still
        fails, on that rule's headline and not on this one's. The count is read off the rule
        directly, because a failing run prints no summary to read it from.
        """
        self.write_ticket("overnight", "61", "one-slug")
        self.write_ticket("overnight", "61", "another-slug")
        self.cite_in(f"{issues_in(TRACKER)}/62-a-spec.md", f"Follows {cite('61')}.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertNotIn("STEERING FAILED: a cited ticket number resolves to no file.", out)
        self.assertIn("STEERING FAILED: two tickets in one tracker share a number.", out)
        missing, filed, resolved = steering.dangling_tickets(self.root)
        self.assertEqual(([], [], 1), (missing, filed, resolved))

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
        self.live()
        code, out = self.check("--base", "HEAD")
        self.assertEqual(0, code, out)
        counted = re.search(r"(\d+) ticket citations? resolves? to a spec\.", out)
        self.assertIsNotNone(counted, out)
        self.assertGreaterEqual(int(counted.group(1)), 350, out)

    # --- a ticket number is unique within its tracker -----------------------------------------

    def test_two_tickets_sharing_a_number_in_one_tracker_fail(self):
        """The overnight tracker's two 61s: one session's flake ticket, and another's task 6.1a.

        Written by two sessions that could not see each other's unlanded work, and each a spec a
        citation of the number might mean. The failure names the directory and both files, so the
        session that has to renumber one does not have to list the directory to find them.
        """
        self.write_ticket(TRACKER, "60")
        self.write_ticket(TRACKER, "61", "security-suite-refresh-outcome-flake")
        self.write_ticket(TRACKER, "61", "6.1a-harness-update-registry-race")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: two tickets in one tracker share a number.", out)
        self.assertIn(
            f"{issues_in(TRACKER)}/: 61 is claimed by 61-6.1a-harness-update-registry-race.md "
            "and 61-security-suite-refresh-outcome-flake.md",
            out,
        )
        self.assertIn("docs/agents/tracker-policy.md", out)

    def test_every_number_sharing_files_is_reported_in_every_tracker(self):
        """Three files on one number are one line naming all three, and a second collision in
        another tracker is reported beside it rather than after the first is fixed."""
        for slug in ("a", "b", "c"):
            self.write_ticket(TRACKER, "07", slug)
        self.write_ticket(OTHER_TRACKER, "02", "one")
        self.write_ticket(OTHER_TRACKER, "02", "two")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"{issues_in(TRACKER)}/: 07 is claimed by 07-a.md and 07-b.md and 07-c.md",
                      out)
        self.assertIn(f"{issues_in(OTHER_TRACKER)}/: 02 is claimed by 02-one.md and 02-two.md",
                      out)

    def test_a_number_repeated_across_trackers_passes(self):
        """Numbering restarts per tracker by design, so `06` in two of them is two tickets.

        The count is what the assertion holds, as the citation rule's green case does: a rule
        deleted outright also exits 0, and the sentence has to say how many tickets it read.
        """
        self.write_ticket(TRACKER, "06", "a-plan-ticket")
        self.write_ticket(OTHER_TRACKER, "06", "a-feature-ticket")
        self.write(f"{issues_in(OTHER_TRACKER)}/README.md", "Not a numbered ticket.\n")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("2 tickets each hold a number no other ticket in its tracker shares.", out)

    def test_a_single_ticket_is_counted_in_the_singular(self):
        """The plural above and the singular here, so the count is a number and not a word."""
        self.write_ticket(TRACKER, "06")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("1 ticket holds a number no other ticket in its tracker shares.", out)

    def test_every_ticket_in_the_live_tree_holds_its_own_number(self):
        """The one uniqueness case against the real tree, where the 61s collided until this rule
        landed and renumbered one of them.

        The count is asserted as a floor for the reason the other two live counts are: a green
        exit on its own is what a rule reading no tracker also prints. The floor is what the
        trackers held when the rule landed: filing a ticket raises the count, and only deleting
        one lowers it, which is a change that should lower this floor on purpose.
        """
        self.live()
        code, out = self.check("--base", "HEAD")
        self.assertEqual(0, code, out)
        counted = re.search(r"(\d+) tickets? (?:each )?holds? a number no other ticket", out)
        self.assertIsNotNone(counted, out)
        self.assertGreaterEqual(int(counted.group(1)), 86, out)

    # --- a cited #NNN names an issue or pull request ------------------------------------------

    def test_a_hash_citation_of_a_number_github_has_issued_passes(self):
        """The green side, and the count it prints, held for the reason the ticket count is."""
        self.github(43)
        self.cite_in("docs/adr/0021-a-decision.md", f"Decided on pull request {issue(43)}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("1 #NNN citation names an issue or pull request.", out)

    def test_two_hash_citations_are_both_counted(self):
        """The singular above and the plural here, so the count is a number and not a word."""
        self.github(43, 44)
        self.cite_in("docs/adr/0021-a-decision.md",
                     f"Landed by {issue(43)} and reviewed in {issue(44)}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("2 #NNN citations name an issue or pull request.", out)

    def test_every_hash_citation_shape_the_tree_writes_is_read(self):
        """Bare, parenthesised, before a comma, after `PR`, starting a line and with four digits.

        The shapes the ADRs, the archive and one test actually write, plus the longest number the
        pattern reads, so widening or narrowing it shows up here before it shows up on a push.
        """
        self.github(43, 1234)
        for shape in (
            f"Fixed in {issue(43)} today.",
            f"Fixed ({issue(43)}).",
            f"Fixed in {issue(43)}, then released.",
            f"PR {issue(43)}: the fix.",
            f"{issue(43)} fixed it.",
            f"Fixed in {issue(1234)}.",
        ):
            with self.subTest(shape=shape):
                self.cite_in("docs/adr/0021-a-decision.md", shape)
                code, out = self.check()
                self.assertEqual(0, code, out)
                self.assertIn("1 #NNN citation names an issue or pull request.", out)

    def test_a_hash_citation_of_a_number_github_has_not_issued_fails(self):
        """The red side: a number past the end of GitHub's sequence, named with file and line."""
        self.github(43)
        line = self.cite_in("docs/adr/0021-a-decision.md", f"Superseded by {issue(999)}.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a cited #NNN names no issue or pull request.", out)
        self.assertIn(f"docs/adr/0021-a-decision.md:{line}: {unissued(999)}", out)
        self.assertIn("owner/repo#NNN", out)

    def test_each_dangling_hash_citation_is_reported_on_its_own_line(self):
        """Two numbers on one line are two failures, as the plural ticket shape's are."""
        self.github(43)
        line = self.cite_in("docs/adr/0021-a-decision.md",
                            f"Blocked on {issue(998)} and {issue(999)}.")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"docs/adr/0021-a-decision.md:{line}: {unissued(998)}", out)
        self.assertIn(f"docs/adr/0021-a-decision.md:{line}: {unissued(999)}", out)

    def test_a_hash_citation_is_reported_on_the_line_it_sits_on(self):
        """cite_in() always writes line 3, so one case puts the citation somewhere else."""
        self.github(43)
        self.write("docs/adr/0021-a-decision.md",
                   f"# A decision\n\nContext.\n\nFollows {issue(999)}.\n")
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(f"docs/adr/0021-a-decision.md:5: {unissued(999)}", out)

    def test_what_is_not_a_hash_citation_is_not_read(self):
        """A heading, an anchor, an entity, a colour, a counter's suffix and a word's own `#`.

        Each is a `#` the tree writes for another reason, and each is left alone by the shape of
        the pattern rather than by a list: a heading puts a space after its `#`, an anchor a letter
        or a slash, an entity an `&` before it, a colour runs letters into its digits, `rt#1` is a
        word's own suffix and a URL puts a slash before its fragment. The stub in setUp is what
        proves none was read: a tree whose only `#` is one of these asks GitHub nothing.
        """
        for shape in (
            "# 43 ways to write a heading",
            "## 2026 in review",
            "See docs/plan/01-plan.md#L43 and registry.schema.json#/$defs/oauth.",
            "It&#39;s an entity.",
            "The label is #0e8a16 green.",
            "Token rt#1 was replaced by at#2.",
            "A C# file holds #region, #if DEBUG and #nullable enable.",
            "https://example.test/issues/#43",
        ):
            with self.subTest(shape=shape):
                self.cite_in("docs/adr/0021-a-decision.md", shape)
                code, out = self.check()
                self.assertEqual(0, code, out)
                self.assertNotIn("#NNN", out)

    def test_a_run_of_five_digits_is_not_read(self):
        """The AWS provider's issue, cited bare from two Terraform comments, has five digits.

        Four digits at most, and a longer run is not read at all rather than read at its first
        four, so nothing is mis-resolved. The stub proves the five digits never reached GitHub.
        """
        self.cite_in("infra/modules/cell/versions.tf",
                     f"# bucket_namespace arrived in 6.37.0 (the provider's CHANGELOG, "
                     f"{issue(46917)}).")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertNotIn("#NNN", out)

    def test_github_is_not_asked_when_nothing_cites_an_issue(self):
        """A tree with no `#NNN` owes GitHub no call; the stub in setUp would fail this case."""
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertNotIn("#NNN", out)

    def test_github_is_asked_once_however_many_files_cite(self):
        """One lookup per run. The hook runs this on every push, and a call per citing file would
        make a tree of four hundred files four hundred calls."""
        asked = []

        def lookup(root):
            asked.append(root)
            return {43}, set(), ""

        steering.issued_numbers = lookup
        self.cite_in("docs/adr/0021-a-decision.md", f"Decided in {issue(43)}.")
        self.cite_in("contracts/pipeline.md", f"Frozen by {issue(43)}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertEqual([self.root], asked, out)
        self.assertIn("2 #NNN citations name an issue or pull request.", out)

    def test_hash_citations_are_reported_as_not_checked_when_github_cannot_be_asked(self):
        """Offline, no gh, no token: one line says so, and nothing fails, as the pre-push hook does
        for a missing tool. The number cited here names nothing, and the case is green anyway,
        because nothing could say so; the summary leaves the issue clause out rather than claim it.
        """
        self.offline("gh is not on PATH")
        self.cite_in("docs/adr/0021-a-decision.md", f"Superseded by {issue(999)}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("steering: 1 #NNN citation not checked: gh is not on PATH.", out)
        self.assertNotIn("STEERING FAILED", out)
        self.assertIn("claim no progress and every pointer resolves.", out)
        self.assertNotIn("names an issue or pull request", out)

    def test_the_not_checked_line_counts_in_the_plural_too(self):
        self.offline("gh: Bad credentials (HTTP 401)")
        self.cite_in("docs/adr/0021-a-decision.md", f"Blocked on {issue(998)} and {issue(999)}.")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertIn("steering: 2 #NNN citations not checked: gh: Bad credentials (HTTP 401).",
                      out)

    def test_a_transcript_is_not_read_for_hash_citations(self):
        """Captured program output counts tokens with `#N`, and three spike runs are tracked.

        The stub proves the file was not read: its `#` names numbers GitHub never issued, and the
        case neither fails nor asks.
        """
        steering.TRANSCRIPTS = {"spikes/g0/a-spike/output.txt": "a token counter per check"}
        self.write("spikes/g0/a-spike/output.txt",
                   f"PASS  E1  one refresh grant  [token {issue(998)} -> {issue(999)}]\n")
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertNotIn("#NNN", out)

    def test_a_transcript_that_is_no_longer_tracked_fails(self):
        """The list cannot outlive its entries, which is PLANNED's rule one level over."""
        why = "a token counter per check"
        steering.TRANSCRIPTS = {"spikes/g0/a-spike/output.txt": why}
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a TRANSCRIPTS path is not tracked.", out)
        self.assertIn(f"'spikes/g0/a-spike/output.txt' is not tracked ({why})", out)
        self.assertIn("Remove it from TRANSCRIPTS", out)

    def test_the_live_tree_s_hash_citations_are_found(self):
        """The one case against the real tree, on the scan alone: the resolution needs GitHub.

        The ADRs, the archive and one test cite pull requests as `#NNN`, so the scan has a floor,
        for the reason the other live counts have one: a scan that found nothing would also exit
        0. The three transcripts are the live ones, and each has to be tracked still.
        """
        steering.TRANSCRIPTS = LIVE_TRANSCRIPTS
        cited, untracked = steering.issue_citations(ROOT)
        self.assertEqual([], untracked)
        self.assertGreaterEqual(len(cited), 150, cited[:5])

    # --- a commit that changes a steering document cites its authority -------------------------

    def test_a_steering_commit_naming_no_authority_fails_with_the_commit_and_its_paths(self):
        """7569b1f's shape: a lane's glossary entry, citing nothing, here beside a guide change.

        The line names the commit and every steering path it changed, so the session that has to
        reword it knows which commit and what the authority has to cover without opening it.
        """
        base = self.baseline()
        self.append("AGENTS.md", "A line.")
        self.write("GLOSSARY.md", "# Glossary\n\n**Local check** -- the checks a branch passes.\n")
        self.write("docs/agents/landing-work.md", "# Landing work\n")
        subject = "Task issue-68: the glossary gains Local check"
        sha = self.commit(message(subject))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a commit changes a steering document and cites no "
                      "authority.", out)
        self.assertIn(holds(sha, subject, "AGENTS.md", "GLOSSARY.md",
                            "docs/agents/landing-work.md"), out)
        self.assertIn("docs/agents/landing-work.md has the forms", out)

    def test_a_change_to_each_steering_document_is_held(self):
        """One commit per place ADR-0023 decision 2 names: five root files and three directories.

        Each judged alone, from the commit before it, so a line naming the wrong path or a place
        the rule stopped reading shows up as that place's subtest and no other.
        """
        self.baseline()
        for path in STEERING_EXAMPLES:
            with self.subTest(path=path):
                base = self.git("rev-parse", "HEAD")
                if (self.root / path).exists():
                    self.append(path, "A line.")
                else:
                    self.write(path, "# A steering document\n")
                subject = f"Change {path}"
                sha = self.commit(message(subject))
                code, out = self.check("--base", base)
                self.assertEqual(1, code, out)
                self.assertIn(holds(sha, subject, path), out)

    def test_a_path_beside_a_steering_document_is_not_held(self):
        """A prefix of a steering name, or the name away from the root, is another file.

        The summary is checked too, because the rule passing and the rule finding nothing to hold
        both exit 0, and only the missing authority clause tells this from a commit that cited one.
        """
        base = self.baseline()
        for path in STEERING_NEIGHBOURS:
            self.write(path, "# Not a steering document\n")
        self.commit(message("Write beside the steering documents"))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)
        self.assertNotIn("authority", out)

    def test_an_authority_naming_an_issue_passes_and_is_counted(self):
        """The green side of `Authority: #<n>`, and the count the line it passes prints.

        The count is what the assertion holds, as the citation rules' green cases do: a rule
        deleted outright also exits 0, and the sentence has to say how many commits it vouched for.
        """
        self.github(102)
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary, as the ticket asks", issue(102)))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertIn("1 commit changing a steering document cites its authority.", out)

    def test_two_authorised_commits_are_both_counted(self):
        """The singular above and the plural here, so the count is a number and not a word."""
        self.github(101, 102)
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary", issue(102)))
        self.write("docs/agents/landing-work.md", "# Landing work\n")
        self.commit(message("The landing guide", issue(101)))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertIn("2 commits changing a steering document cite their authority.", out)

    def test_an_authority_naming_no_issue_fails(self):
        """A `#<n>` past the end of GitHub's sequence: a pointer at nothing, like a dangling
        `#NNN`. The listing lacks it, so it is asked about alone, and GitHub's 404 refuses it."""
        self.github(43)
        self.looked_up({})
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, on a number nobody issued"
        sha = self.commit(message(subject, issue(999)))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: an Authority: trailer names no issue and no comment on "
                      "this repository.", out)
        self.assertIn(refuses(sha, subject, ("GLOSSARY.md",), issue(999),
                              "names no issue of this repository"), out)

    def test_an_authority_naming_a_pull_request_fails(self):
        """A ticket is an issue, and a pull request has no acceptance criterion to ask for a change.

        The number is in GitHub's sequence, so the `#NNN` rule would take it; the authority rule
        tells the two apart from the same listing.
        """
        self.github(43, pulls=(120,))
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, on a pull request"
        sha = self.commit(message(subject, issue(120)))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(refuses(sha, subject, ("GLOSSARY.md",), issue(120),
                              "names a pull request, not an issue"), out)

    def test_an_authority_the_listing_lacks_but_github_finds_alone_passes(self):
        """The listing can exit 0 short of its last page (#297: #185, #135 and #159, each real,
        refused on one run and vouched on the next). A number it lacks is asked about alone, and
        GitHub's answer that it is an issue vouches for it."""
        self.github(43)
        self.looked_up({185: "issue"})
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary, on an issue past the listing's last page", issue(185)))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertIn("1 commit changing a steering document cites its authority.", out)

    def test_an_authority_the_listing_lacks_and_github_finds_a_pull_request_fails(self):
        """The single lookup tells the two apart as the listing does."""
        self.github(43)
        self.looked_up({120: "pull"})
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, on a pull request past the listing's last page"
        sha = self.commit(message(subject, issue(120)))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(refuses(sha, subject, ("GLOSSARY.md",), issue(120),
                              "names a pull request, not an issue"), out)

    def test_an_authority_github_cannot_look_up_alone_is_unchecked_and_passes(self):
        """Any answer to the single lookup but a 404 is GitHub not saying, so the trailer is
        reported unchecked with GitHub's reason and fails nothing (#297)."""
        self.github(43)
        self.looked_up({}, why="gh: Server Error (HTTP 502)")
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary, while GitHub stumbles", issue(185)))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)
        self.assertIn("steering: 1 Authority: citation not checked: gh: Server Error (HTTP 502).",
                      out)
        self.assertNotIn("authority.", out)

    def test_an_authority_the_listing_holds_is_not_looked_up_alone(self):
        """One number at a time only for what the listing lacks: the stub in setUp fails the case
        if this one is asked about."""
        self.github(102, pulls=(120,))
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary", issue(102)))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)

    def test_an_authority_naming_a_comment_github_keeps_there_passes(self):
        """The second form: a confirmed answer's comment, as ADR-0023 cites R4's on #98."""
        self.comments({COMMENT_ID: COMMENT})
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary, as Adam confirmed", COMMENT))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertIn("1 commit changing a steering document cites its authority.", out)

    def test_a_comment_another_account_wrote_fails_naming_its_author(self):
        """#316: a `Confirm` is the human's act, so a comment by any other account that can
        comment on the repository authorises nothing, and the line says who wrote it. With no
        guide in the tree, the account is the repository's owner, read off GitHub's URL."""
        for author, who in (("mallory", "@mallory"), ("", "an account GitHub no longer names")):
            with self.subTest(author=author):
                self.comments({COMMENT_ID: COMMENT}, author=author)
                base = self.baseline()
                self.write("GLOSSARY.md", f"# Glossary {author}\n")
                subject = "The glossary, on someone else's Confirm"
                sha = self.commit(message(subject, COMMENT))
                code, out = self.check("--base", base)
                self.assertEqual(1, code, out)
                self.assertIn(refuses(sha, subject, ("GLOSSARY.md",), COMMENT,
                                      f"is a comment by {who}, not by @adammedford, the "
                                      "repository's owner, since docs/agents/tracker-policy.md "
                                      "names no account"), out)
                self.assertIn("STEERING FAILED: an Authority: comment was written by an account "
                              "that does not confirm.", out)
                self.assertNotIn("cites its authority", out)

    def test_the_owners_login_is_compared_without_case(self):
        self.comments({COMMENT_ID: COMMENT}, author="AdamMedford")
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary, as Adam confirmed", COMMENT))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)

    def test_the_account_the_guide_names_confirms_and_the_owner_then_does_not(self):
        """The account is read from the guide, not assumed: once docs/agents/tracker-policy.md
        names one, its comment vouches and the owner's no longer does."""
        guide = ("# Tracker policy\n\n## This repository's additions\n\n"
                 "- **The human's account is `@adam-m`.** A `Confirm` comment counts only when\n"
                 "  this account wrote it.\n")
        self.write("docs/agents/tracker-policy.md", guide)
        for author, code_wanted in (("adam-m", 0), ("adammedford", 1)):
            with self.subTest(author=author):
                self.comments({COMMENT_ID: COMMENT}, author=author)
                base = self.baseline()
                self.write("GLOSSARY.md", f"# Glossary {author}\n")
                subject = "The glossary, on a Confirm"
                sha = self.commit(message(subject, COMMENT))
                code, out = self.check("--base", base)
                self.assertEqual(code_wanted, code, out)
                if code_wanted:
                    self.assertIn(refuses(sha, subject, ("GLOSSARY.md",), COMMENT,
                                          "is a comment by @adammedford, not by @adam-m, the "
                                          "account docs/agents/tracker-policy.md names for a "
                                          "Confirm"), out)

    def test_an_account_line_left_in_the_setup_written_guide_is_not_read(self):
        """#432: the account line moved to docs/agents/tracker-policy.md, which no setup skill
        writes, so a re-run of the setup skill cannot delete it (#412). A line in
        docs/agents/issue-tracker.md, the guide that skill rewrites, names nobody: @mallory's
        comment is judged against the owner, and the line says which file was read."""
        self.write("docs/agents/issue-tracker.md",
                   "# Issue tracker\n\n## This repository's additions\n\n"
                   "- **The human's account is `@mallory`.** It confirms.\n")
        self.comments({COMMENT_ID: COMMENT}, author="mallory")
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, on the setup guide's account"
        sha = self.commit(message(subject, COMMENT))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(refuses(sha, subject, ("GLOSSARY.md",), COMMENT,
                              "is a comment by @mallory, not by @adammedford, the repository's "
                              "owner, since docs/agents/tracker-policy.md names no account"), out)

    def test_a_range_that_renames_the_account_cannot_vouch_itself(self):
        """#316 review: the account is read from the guide at the base. A range that writes the
        guide naming @mallory, citing @mallory's comment for that change and for another, is
        judged against the owner the base's guide leaves, so both commits fail."""
        self.comments({COMMENT_ID: COMMENT}, author="mallory")
        base = self.baseline()
        self.write(steering.GUIDE, "# Tracker policy\n\n## This repository's additions\n\n"
                                   "- **The human's account is `@mallory`.** It confirms.\n")
        guide_sha = self.commit(message("Name the account", COMMENT))
        self.write("GLOSSARY.md", "# Glossary\n")
        glossary_sha = self.commit(message("The glossary, on mallory's Confirm", COMMENT))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        why = ("is a comment by @mallory, not by @adammedford, the repository's owner, since "
               "docs/agents/tracker-policy.md names no account")
        self.assertIn(refuses(guide_sha, "Name the account", (steering.GUIDE,), COMMENT, why), out)
        self.assertIn(refuses(glossary_sha, "The glossary, on mallory's Confirm",
                              ("GLOSSARY.md",), COMMENT, why), out)

    def test_a_url_that_is_not_a_comment_fails_without_asking_github(self):
        """A page that is not a comment, an issue's own URL, a review comment, and prose citing a
        number: none is either form, and the stubs in setUp prove GitHub was asked nothing."""
        self.baseline()
        for authority in (
            "https://example.test/confirmed",
            "https://github.com/adammedford/spa-platform/issues/98",
            "https://github.com/adammedford/spa-platform/pull/45#discussion_r1234",
            f"confirmed by Adam on {issue(67)}",
        ):
            with self.subTest(authority=authority):
                base = self.git("rev-parse", "HEAD")
                self.append("AGENTS.md", "A line.")
                subject = "Change AGENTS.md on a citation of neither form"
                sha = self.commit(message(subject, authority))
                code, out = self.check("--base", base)
                self.assertEqual(1, code, out)
                self.assertIn(refuses(sha, subject, ("AGENTS.md",), authority,
                                      "is neither #<n> nor the URL of a comment on this "
                                      "repository"), out)

    def test_a_comment_url_this_repository_has_no_comment_under_fails(self):
        """GitHub's 404 for the id: no comment here, whatever the URL's shape promised."""
        self.comments({})
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, on a comment that does not exist"
        sha = self.commit(message(subject, COMMENT))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(refuses(sha, subject, ("GLOSSARY.md",), COMMENT,
                              "names no comment on this repository"), out)

    def test_a_comment_url_that_is_not_where_github_keeps_the_comment_fails(self):
        """Another repository's URL, and this repository's under the wrong issue.

        The id is one this repository has, so only comparing the whole URL with the one GitHub
        keeps tells either from the real thing; the line names that URL, so the fix is a copy.
        """
        self.comments({COMMENT_ID: COMMENT})
        self.baseline()
        for authority in (
            f"https://github.com/someone/elsewhere/issues/98#issuecomment-{COMMENT_ID}",
            f"https://github.com/adammedford/spa-platform/issues/97#issuecomment-{COMMENT_ID}",
        ):
            with self.subTest(authority=authority):
                base = self.git("rev-parse", "HEAD")
                self.append("AGENTS.md", "A line.")
                subject = "Change AGENTS.md on a comment elsewhere"
                sha = self.commit(message(subject, authority))
                code, out = self.check("--base", base)
                self.assertEqual(1, code, out)
                self.assertIn(refuses(sha, subject, ("AGENTS.md",), authority,
                                      f"is not where GitHub keeps comment {COMMENT_ID}: "
                                      f"{COMMENT}"), out)

    def test_every_authority_a_commit_names_is_judged(self):
        """A second trailer citing nothing fails beside one that resolves, and the commit is not
        counted: a pointer at nothing is the defect whatever else sits next to it."""
        self.github(102)
        self.looked_up({})
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, on two citations"
        sha = self.commit(message(subject, issue(102), issue(999)))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(refuses(sha, subject, ("GLOSSARY.md",), issue(999),
                              "names no issue of this repository"), out)
        self.assertNotIn(f"Authority: {issue(102)} ", out)

    def test_an_authority_line_inside_the_prose_is_not_a_trailer(self):
        """git reads a trailer from the group of trailers that ends the message, and a group
        holding a line of prose is not one unless it holds a trailer git knows: so the issue this
        line names, which GitHub has issued, authorises nothing written this way."""
        self.github(102)
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, cited in passing"
        sha = self.commit(f"{subject}\n\nThe ticket asks for it, so this line cites\n"
                          f"Authority: {issue(102)}\n")
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(holds(sha, subject, "GLOSSARY.md"), out)

    def test_the_trailer_s_key_is_read_in_any_case(self):
        """git matches a trailer's key in any case, and the rule reads it through git."""
        self.github(102)
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(f"The glossary\n\nauthority: {issue(102)}\n")
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertIn("1 commit changing a steering document cites its authority.", out)

    def test_a_merge_passes_without_a_trailer(self):
        """A merge's own resolution here changes the glossary, which is the hardest merge to pass.

        What a merge brings in is commits, each held on its own: the side branch's commit touches
        code and passes, and the merge carries a steering line no trailer authorises and passes too.
        """
        base = self.baseline()
        self.git("checkout", "--quiet", "-b", "side")
        self.write("src/App/App.cs", "class App {}\n")
        self.commit("Code on a side branch")
        self.git("checkout", "--quiet", "-")
        self.git("merge", "--quiet", "--no-ff", "--no-commit", "side")
        self.write("GLOSSARY.md", "# Glossary\n\nA line the merge itself adds.\n")
        self.commit("Merge side, with a glossary line of its own")
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_steering_commit_made_before_the_starting_point_passes(self):
        """Made before the rule was written, so it could not have known it; and rewording it would
        mean rewriting a branch that has landed."""
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit("The glossary, before the rule", made=BEFORE)
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_commit_authored_before_the_starting_point_and_made_after_it_is_held(self):
        """An amend or a rebase keeps the author date and makes the commit again: judged by its
        author date, a commit first written before the rule could carry a steering change added
        after it."""
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, amended after the rule"
        sha = self.commit(message(subject), made=AFTER, authored=BEFORE)
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(holds(sha, subject, "GLOSSARY.md"), out)

    def test_a_change_request_added_passes_without_a_trailer(self):
        """ADR-0023 decision 2: adding a change request is not a change to a steering document,
        and AGENTS.md gives it to every workstream as the way to ask for a contract change."""
        base = self.baseline()
        self.write(f"{steering.CHANGE_REQUESTS}issue-102-a-request.md", "# A change request\n")
        self.commit(message("Ask for a contract change"))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_change_request_edited_or_removed_is_held(self):
        """Only adding one passes: editing or removing a change request is the integrator deciding
        it, a change to contracts/ like any other."""
        request = f"{steering.CHANGE_REQUESTS}issue-102-a-request.md"
        self.write(request, "# A change request\n\n**Status:** open\n")
        self.baseline()
        for shape in ("edited", "removed"):
            with self.subTest(shape=shape):
                base = self.git("rev-parse", "HEAD")
                if shape == "edited":
                    self.append(request, "**Decision:** accepted")
                else:
                    (self.root / request).unlink()
                subject = f"The change request, {shape}"
                sha = self.commit(message(subject))
                code, out = self.check("--base", base)
                self.assertEqual(1, code, out)
                self.assertIn(holds(sha, subject, request), out)

    def test_a_change_request_added_as_decided_on_a_ticket_alone_is_held(self):
        """#187: 9f56729 and 37af894 added their requests already decided, each citing its own
        ticket, whose acceptance asked the lane to record the decision. That is a lane deciding;
        ADR-0021 decision 4 wants a confirmed answer, so the ticket alone is held."""
        self.github(143)
        base = self.baseline()
        request = f"{steering.CHANGE_REQUESTS}issue-145-a-request.md"
        self.write(request, DECIDED_REQUEST)
        subject = "Task issue-143: the change request, decided"
        sha = self.commit(message(subject, "#143"), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn("STEERING FAILED: a commit sets a change request to a decision on no "
                      "confirmed answer.", out)
        self.assertIn(undecides(sha, subject, request, "#143"), out)

    def test_a_change_request_added_as_decided_with_no_trailer_is_held(self):
        """No trailer at all is the authority rule's failure, as for any steering change."""
        base = self.baseline()
        request = f"{steering.CHANGE_REQUESTS}issue-145-a-request.md"
        self.write(request, DECIDED_REQUEST)
        subject = "The change request, decided on nothing"
        sha = self.commit(message(subject), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(holds(sha, subject, request), out)

    def test_a_change_request_added_as_decided_on_a_confirmed_answer_passes(self):
        """The comment holding the human's answer is what decides a request."""
        self.comments({COMMENT_ID: COMMENT})
        base = self.baseline()
        self.write(f"{steering.CHANGE_REQUESTS}issue-145-a-request.md", DECIDED_REQUEST)
        self.commit(message("The change request, decided as Adam confirmed", COMMENT),
                    made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertIn("1 commit changing a steering document cites its authority.", out)

    def test_a_change_request_edited_from_open_to_decided_on_a_ticket_alone_is_held(self):
        """Deciding by an edit is the same decision as deciding by an add."""
        self.github(159)
        request = f"{steering.CHANGE_REQUESTS}issue-160-a-request.md"
        self.write(request, OPEN_REQUEST)
        base = self.baseline()
        self.write(request, DECIDED_REQUEST)
        subject = "The change request, decided"
        sha = self.commit(message(subject, "#159"), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(undecides(sha, subject, request, "#159"), out)

    def test_a_decided_change_request_edited_again_on_a_ticket_passes(self):
        """06fe6aa's shape: a review fixing the prose of a request already decided decides nothing
        new, so the ticket asking for the fix is enough, as for any steering edit."""
        self.github(159)
        request = f"{steering.CHANGE_REQUESTS}issue-160-a-request.md"
        self.write(request, DECIDED_REQUEST)
        base = self.baseline()
        self.append(request, "The reason, said better.")
        self.commit(message("Review: the decision's reason", "#159"), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_change_request_added_open_or_with_no_status_passes_after_the_rule(self):
        """Asking stays free: an open request, or one with no Status line, needs no trailer."""
        for name, text in (("open", OPEN_REQUEST), ("bare", "# A change request\n")):
            with self.subTest(request=name):
                base = self.git("rev-parse", "HEAD") if name == "bare" else self.baseline()
                self.write(f"{steering.CHANGE_REQUESTS}issue-1-{name}.md", text)
                self.commit(message(f"Ask for a contract change, {name}"), made=DECIDED_AFTER)
                code, out = self.check("--base", base)
                self.assertEqual(0, code, out)
                self.assertNotIn("STEERING FAILED", out)

    def test_a_change_request_added_as_decided_before_the_rule_passes(self):
        """A commit made before DECIDED_SINCE could not have known the rule: 9f56729 and 37af894
        stay as they landed."""
        self.github(143)
        base = self.baseline()
        self.write(f"{steering.CHANGE_REQUESTS}issue-145-a-request.md", DECIDED_REQUEST)
        self.commit(message("Task issue-143: the change request, decided", "#143"),
                    made=DECIDED_BEFORE)
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_change_request_with_its_status_in_another_shape_is_held_as_unreadable(self):
        """#188's review: a Status line in any other shape read as open, so a decided request in
        it passed with no trailer. Now the check says it cannot read it, naming the path, and does
        not also ask for a trailer an open request would not need."""
        base = self.baseline()
        for number, (shape, line) in enumerate(OTHER_SHAPES, start=1):
            with self.subTest(shape=shape):
                request = f"{steering.CHANGE_REQUESTS}issue-{number}-a-request.md"
                self.write(request, f"# A change request\n\n- **Raised by:** a lane\n{line}\n")
                subject = f"A change request, its Status as a {shape}"
                sha = self.commit(message(subject), made=DECIDED_AFTER)
                code, out = self.check("--base", base)
                self.assertEqual(1, code, out)
                self.assertIn(UNREADABLE_HEADLINE, out)
                self.assertIn(unreadable(sha, subject, request), out)
                self.assertNotIn("carries no Authority: trailer", out)
                base = sha

    def test_an_unreadable_status_is_held_whatever_the_authority_cites(self):
        """A confirmed answer's URL settles a decision, not a Status nobody can read."""
        self.comments({COMMENT_ID: COMMENT})
        base = self.baseline()
        request = f"{steering.CHANGE_REQUESTS}issue-145-a-request.md"
        self.write(request, "# A change request\n\n- **Status:** _decided_, 2026-10-20.\n")
        subject = "The change request, decided as Adam confirmed"
        sha = self.commit(message(subject, COMMENT), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(unreadable(sha, subject, request), out)

    def test_an_open_request_edited_into_another_shape_is_held_as_unreadable(self):
        """An edit that moves the Status out of the two shapes is held too, and the edit's own
        authority is still judged as any steering edit's is."""
        self.github(159)
        request = f"{steering.CHANGE_REQUESTS}issue-160-a-request.md"
        self.write(request, OPEN_REQUEST)
        base = self.baseline()
        self.write(request, "# A change request\n\n- **Raised by:** a lane\n**Status**: decided\n")
        subject = "The change request, decided in a new shape"
        sha = self.commit(message(subject, "#159"), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(unreadable(sha, subject, request), out)
        self.assertNotIn("a commit sets a change request to a decision", out)

    def test_a_change_request_naming_any_state_but_open_is_a_decision(self):
        """`accepted` decides a request as surely as `decided`: held on a ticket alone, passed on a
        confirmed answer's URL."""
        self.github(143)
        self.comments({COMMENT_ID: COMMENT})
        base = self.baseline()
        for number, state in enumerate(("accepted", "rejected"), start=1):
            with self.subTest(state=state):
                request = f"{steering.CHANGE_REQUESTS}issue-{number}-a-request.md"
                self.write(request, f"# A change request\n\n- **Status:** {state}, 2026-10-20.\n")
                subject = f"Task issue-143: the change request, {state}"
                sha = self.commit(message(subject, "#143"), made=DECIDED_AFTER)
                code, out = self.check("--base", base)
                self.assertEqual(1, code, out)
                self.assertIn("STEERING FAILED: a commit sets a change request to a decision on no "
                              "confirmed answer.", out)
                self.assertIn(undecides(sha, subject, request, "#143", state=state), out)
                self.git("reset", "--quiet", "--hard", base)
        self.write(f"{steering.CHANGE_REQUESTS}issue-3-a-request.md",
                   "# A change request\n\n- **Status:** accepted, 2026-10-20.\n")
        self.commit(message("The change request, accepted as Adam confirmed", COMMENT),
                    made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)

    def test_a_change_request_whose_prose_mentions_a_status_but_labels_none_is_open(self):
        """A word in a sentence or an identifier is no Status line: issue-160's request carries
        `int Status` and `StatusOf` in its prose, under a Status line of its own."""
        base = self.baseline()
        self.write(f"{steering.CHANGE_REQUESTS}issue-1-a-request.md",
                   "# A change request\n\nThe gateway answers 401, a status `StatusOf` maps.\n"
                   "- `ImmutableArray<(int Status, string Code)> Rows`: the table's rows.\n"
                   "Status codes stay as they are.\n")
        self.commit(message("Ask for a contract change"), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_second_status_naming_another_state_is_held_as_unreadable(self):
        """The second #188 review: a request left `- **Status:** open` with a decided Status added
        under a Decision heading read as open at the commit and at its parent, so the decision
        passed with no confirmed answer. Two Status lines naming different states cannot say which
        holds, so the file is held, added or edited, whatever the trailer cites."""
        self.github(159)
        self.comments({COMMENT_ID: COMMENT})
        request = f"{steering.CHANGE_REQUESTS}issue-160-a-request.md"
        self.write(request, OPEN_REQUEST)
        base = self.baseline()
        self.append(request, "## Decision\n\n- **Status:** decided, 2026-10-22. Accepted.")
        for authority in ("#159", COMMENT):
            with self.subTest(edited_with=authority):
                subject = f"The change request, decided under its open Status, on {authority}"
                sha = self.commit(message(subject, authority), made=DECIDED_AFTER)
                code, out = self.check("--base", base)
                self.assertEqual(1, code, out)
                self.assertIn(UNREADABLE_HEADLINE, out)
                self.assertIn(unreadable(sha, subject, request), out)
                self.git("reset", "--quiet", "--soft", base)
        self.git("reset", "--quiet", "--hard", base)
        added = f"{steering.CHANGE_REQUESTS}issue-161-a-request.md"
        self.write(added, OPEN_REQUEST + "\n## Decision\n\n- **Status:** accepted, 2026-10-22.\n")
        subject = "Ask for a contract change, already accepted below"
        sha = self.commit(message(subject), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(unreadable(sha, subject, added), out)

    def test_a_status_named_twice_in_the_same_state_is_read_as_that_state(self):
        """Only a disagreement is ambiguous: a request that repeats its state reads as that state,
        so an open one added twice-labelled is still free, and a decided one is still a decision
        a confirmed answer settles."""
        self.github(159)
        self.comments({COMMENT_ID: COMMENT})
        base = self.baseline()
        self.write(f"{steering.CHANGE_REQUESTS}issue-1-a-request.md",
                   OPEN_REQUEST + "\n## Status\n\n- **Status:** open, still.\n")
        self.commit(message("Ask for a contract change, open twice"), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)
        request = f"{steering.CHANGE_REQUESTS}issue-2-a-request.md"
        self.write(request, DECIDED_REQUEST + "\n- **Status:** Decided, as above.\n")
        subject = "The change request, decided twice on a ticket"
        sha = self.commit(message(subject, "#159"), made=DECIDED_AFTER)
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertNotIn(UNREADABLE_HEADLINE, out)
        self.assertIn(undecides(sha, subject, request, "#159"), out)
        self.git("commit", "--quiet", "--amend", "-m",
                 message("The change request, decided twice as Adam confirmed", COMMENT))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_an_open_status_in_another_shape_is_held_and_told_the_shape_to_write(self):
        """The second #188 review: `open` in code marks or italics, or a plain `Status:` label,
        says open and is held all the same. That is kept, not widened (#193, Q6 on #226): the rule
        fails closed, and the message names the shape to write, so a held lane rewrites one line."""
        base = self.baseline()
        shapes = (("code", "- **Status:** `open`"), ("italic", "- **Status:** _open_"),
                  ("plain label", "- Status: open"))
        for number, (shape, line) in enumerate(shapes, start=1):
            with self.subTest(shape=shape):
                request = f"{steering.CHANGE_REQUESTS}issue-{number}-a-request.md"
                self.write(request, f"# A change request\n\n- **Raised by:** a lane\n{line}\n")
                subject = f"Ask for a contract change, open as {shape}"
                sha = self.commit(message(subject), made=DECIDED_AFTER)
                code, out = self.check("--base", base)
                self.assertEqual(1, code, out)
                self.assertIn(unreadable(sha, subject, request), out)
                self.assertIn("`- **Status:** open`", out)
                base = sha

    def test_offline_a_steering_commit_naming_no_authority_still_fails(self):
        """The trailer's presence needs no network, so no network is no excuse for its absence.

        The range holds a commit citing an issue as well, so GitHub is asked and found unreachable
        in this run, rather than never asked: the offline line proves the condition held.
        """
        self.offline("gh is not on PATH")
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary", issue(102)))
        self.write("docs/agents/landing-work.md", "# Landing work\n")
        subject = "The landing guide, offline"
        sha = self.commit(message(subject))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(holds(sha, subject, "docs/agents/landing-work.md"), out)
        self.assertIn("steering: 1 Authority: citation not checked: gh is not on PATH.", out)

    def test_offline_citations_are_reported_as_not_checked_and_pass(self):
        """Both forms, with GitHub unreachable: one line says how many went unchecked and why, as
        the `#NNN` rule says of its own, and the summary claims nothing about them."""
        self.offline("gh is not on PATH")
        self.comments({}, why="gh is not on PATH")
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary", issue(102)))
        self.write("docs/agents/landing-work.md", "# Landing work\n")
        self.commit(message("The landing guide", COMMENT))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertIn("steering: 2 Authority: citations not checked: gh is not on PATH.", out)
        self.assertNotIn("authority.", out)

    def test_offline_a_citation_of_neither_form_still_fails(self):
        """Neither form is decided by the shape alone, so it fails with GitHub unreachable."""
        self.offline("gh is not on PATH")
        base = self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, on prose"
        sha = self.commit(message(subject, "confirmed by Adam"))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(refuses(sha, subject, ("GLOSSARY.md",), "confirmed by Adam",
                              "is neither #<n> nor the URL of a comment on this repository"), out)

    def test_without_a_base_every_commit_head_reaches_is_judged(self):
        """No base named is no range skipped: the hook on a clone with no main names none, and
        the rule then reads every commit since the starting point rather than none."""
        self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        subject = "The glossary, judged with no base"
        sha = self.commit(message(subject))
        code, out = self.check()
        self.assertEqual(1, code, out)
        self.assertIn(holds(sha, subject, "GLOSSARY.md"), out)

    def test_without_a_base_a_history_from_before_the_starting_point_passes(self):
        """The same reading over a history the rule predates: nothing to hold, and no failure."""
        self.baseline()
        self.write("GLOSSARY.md", "# Glossary\n")
        self.commit(message("The glossary, before the rule"), made=BEFORE)
        code, out = self.check()
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)

    def test_a_base_that_names_no_commit_is_refused(self):
        """A typo in the base would otherwise judge nothing and pass, as tools/check.sh's would."""
        self.baseline()
        errors = io.StringIO()
        with contextlib.redirect_stderr(errors), self.assertRaises(SystemExit) as stopped:
            steering.main(["--base", "no-such-ref"])
        self.assertEqual(2, stopped.exception.code)
        self.assertIn("no commit no-such-ref to judge the commits against", errors.getvalue())

    def test_the_steering_documents_are_the_ones_adr_0023_names(self):
        """A guard reads the thing it guards: the list is decision 2's, read from the ADR, and the
        change requests' directory is the one GLOSSARY.md's *Change request* names."""
        adr = next((ROOT / "docs" / "adr").glob("0023-*.md")).read_text()
        decision = adr.split("**The steering documents are**", 1)[1]
        decision = decision.split("Adding a change request", 1)[0]
        self.assertEqual(sorted(re.findall(r"`([^`]+)`", decision)),
                         sorted(steering.STEERING_DOCUMENTS))
        glossary = (ROOT / "GLOSSARY.md").read_text()
        self.assertIn(f"`{steering.CHANGE_REQUESTS}<task-id>-<slug>.md`", glossary)

    # --- the GitHub lookups, against a stand-in for gh ----------------------------------------

    def stand_in(self, script: str, *arguments: str) -> tuple[str, ...]:
        """A command that runs `script` in this interpreter, in place of gh."""
        return (sys.executable, "-I", "-c", script, *arguments)

    def test_the_listing_tells_issues_from_pull_requests(self):
        """The line the --jq writes per number, read back into issues and pull requests."""
        steering.GH_ISSUES = self.stand_in(
            "print('43 issue'); print('120 pull'); print('102 issue')")
        self.assertEqual(({43, 102}, {120}, ""), REAL_ISSUED_NUMBERS(self.root))

    def test_the_listing_says_what_gh_said_when_it_fails(self):
        steering.GH_ISSUES = self.stand_in(
            "import sys; sys.stderr.write('\\ngh: Bad credentials (HTTP 401)\\n'); sys.exit(1)")
        self.assertEqual((set(), set(), "gh: Bad credentials (HTTP 401)"),
                         REAL_ISSUED_NUMBERS(self.root))

    def test_the_listing_refuses_an_answer_that_is_not_its_lines(self):
        """An answer in the old shape, a bare number, is refused rather than read as an issue."""
        steering.GH_ISSUES = self.stand_in("print('43')")
        issues, pulls, why = REAL_ISSUED_NUMBERS(self.root)
        self.assertEqual((set(), set()), (issues, pulls))
        self.assertTrue(why.startswith("gh answered with something other than numbers"), why)

    def test_the_comment_lookup_returns_the_url_github_keeps_the_comment_at(self):
        """The id goes where the command names {comment}, and the answer is the URL gh prints
        and the login after it; a deleted author, which the --jq writes as no login, is ""."""
        for printed, author in ((" mallory", "mallory"), (" ", ""), ("", "")):
            with self.subTest(author=author, printed=printed):
                steering.GH_COMMENT = self.stand_in(
                    "import sys; print('https://github.com/o/r/issues/1#issuecomment-' + "
                    f"sys.argv[1] + {printed!r})", "{comment}")
                self.assertEqual(
                    (steering.Comment("https://github.com/o/r/issues/1#issuecomment-5", author),
                     ""),
                    REAL_COMMENT_URL(self.root, "5"))

    def test_the_comment_lookup_asks_for_the_author(self):
        """The --jq writes the login after the URL, so the rule reads who wrote the comment."""
        self.assertIn(".user.login", steering.GH_COMMENT[-1])
        self.assertTrue(steering.GH_COMMENT[-1].startswith('"\\(.html_url) '))

    def test_the_comment_lookup_reads_a_404_as_no_comment(self):
        """GitHub's answer for an id this repository has no comment under: an answer, not a
        reason the citation went unchecked."""
        steering.GH_COMMENT = self.stand_in(
            "import sys; sys.stderr.write('gh: Not Found (HTTP 404)\\n'); sys.exit(1)")
        self.assertEqual((None, ""), REAL_COMMENT_URL(self.root, "5"))

    def test_the_comment_lookup_reports_any_other_failure_as_unchecked(self):
        for script, why in (
            ("import sys; sys.stderr.write('error connecting to api.github.com\\n'); sys.exit(1)",
             "error connecting to api.github.com"),
            ("import sys; sys.exit(3)", "gh exited 3 and said nothing"),
        ):
            with self.subTest(why=why):
                steering.GH_COMMENT = self.stand_in(script)
                self.assertEqual((None, why), REAL_COMMENT_URL(self.root, "5"))
        steering.GH_COMMENT = ("no-such-command-for-steering-tests", "{comment}")
        self.assertEqual((None, "gh is not on PATH"), REAL_COMMENT_URL(self.root, "5"))

    def test_the_single_lookup_says_what_github_calls_the_number(self):
        """The number goes where the command names {number}, and the answer is the listing's line
        for it, read back into issue or pull request."""
        for kind in ("issue", "pull"):
            with self.subTest(kind=kind):
                steering.GH_ISSUE = self.stand_in(
                    f"import sys; print(sys.argv[1] + ' {kind}')", "{number}")
                self.assertEqual((kind, ""), REAL_ISSUE_KIND(self.root, 185))

    def test_the_single_lookup_reads_a_404_or_a_410_as_no_such_number(self):
        """GitHub's answers for a number it holds no issue under here: a 404, or a 410 for an
        issue deleted from it, which a reader without read access sees as the 404. Each is the
        answer the rule refuses on."""
        for said in ("gh: Not Found (HTTP 404)", "gh: This issue was deleted (HTTP 410)"):
            with self.subTest(said=said):
                steering.GH_ISSUE = self.stand_in(
                    f"import sys; sys.stderr.write('{said}\\n'); sys.exit(1)")
                self.assertEqual((None, ""), REAL_ISSUE_KIND(self.root, 999))

    def test_the_single_lookup_reports_any_other_answer_as_unchecked(self):
        """A failure that is neither a 404 nor a 410, and an answer about another number -- a
        transferred issue redirected to its new home -- each say why the number went unchecked."""
        for script, why in (
            ("import sys; sys.stderr.write('error connecting to api.github.com\\n'); sys.exit(1)",
             "error connecting to api.github.com"),
            ("import sys; sys.stderr.write('gh: Server Error (HTTP 502)\\n'); sys.exit(1)",
             "gh: Server Error (HTTP 502)"),
            ("import sys; sys.exit(3)", "gh exited 3 and said nothing"),
            ("print('7 issue')",
             "gh answered #185 with something other than its line: '7 issue\\n'"),
            ("print('185 discussion')",
             "gh answered #185 with something other than its line: '185 discussion\\n'"),
        ):
            with self.subTest(why=why):
                steering.GH_ISSUE = self.stand_in(script)
                self.assertEqual((None, why), REAL_ISSUE_KIND(self.root, 185))
        steering.GH_ISSUE = ("no-such-command-for-steering-tests", "{number}")
        self.assertEqual((None, "gh is not on PATH"), REAL_ISSUE_KIND(self.root, 185))

    def test_a_truncated_listing_holds_all_three_answers_of_the_single_lookup(self):
        """#297 end to end, through the real lookups and a stand-in for gh: the listing exits 0
        one page short, holding 43 and the pull request 120 and none of the numbers the trailers
        cite. Asked alone, GitHub finds 185, never issued 999, and cannot be reached for 159; the
        first is vouched, the second refused as before, the third unchecked with the reason."""
        steering.issued_numbers = REAL_ISSUED_NUMBERS
        steering.issue_kind = REAL_ISSUE_KIND
        steering.GH_ISSUES = self.stand_in("print('43 issue'); print('120 pull')")
        steering.GH_ISSUE = self.stand_in(
            "import sys\n"
            "number = sys.argv[1]\n"
            "if number == '185': print('185 issue')\n"
            "elif number == '159':\n"
            "    sys.stderr.write('error connecting to api.github.com\\n'); sys.exit(1)\n"
            "else:\n"
            "    sys.stderr.write('gh: Not Found (HTTP 404)\\n'); sys.exit(1)\n",
            "{number}")
        self.baseline()

        base = self.git("rev-parse", "HEAD")
        self.append("AGENTS.md", "A line.")
        self.commit(message("Change AGENTS.md on an issue past the last page", issue(185)))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertIn("1 commit changing a steering document cites its authority.", out)

        base = self.git("rev-parse", "HEAD")
        self.append("AGENTS.md", "A line.")
        subject = "Change AGENTS.md on a number nobody issued"
        sha = self.commit(message(subject, issue(999)))
        code, out = self.check("--base", base)
        self.assertEqual(1, code, out)
        self.assertIn(refuses(sha, subject, ("AGENTS.md",), issue(999),
                              "names no issue of this repository"), out)

        base = self.git("rev-parse", "HEAD")
        self.append("AGENTS.md", "A line.")
        self.commit(message("Change AGENTS.md while GitHub is unreachable", issue(159)))
        code, out = self.check("--base", base)
        self.assertEqual(0, code, out)
        self.assertNotIn("STEERING FAILED", out)
        self.assertIn("steering: 1 Authority: citation not checked: error connecting to "
                      "api.github.com.", out)


if __name__ == "__main__":
    unittest.main(verbosity=2)
