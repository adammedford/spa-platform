#!/usr/bin/env python3
"""Tests for tools/overnight_collect.py. Owner: integrator.

Run: python3 -I tools/overnight_collect_tests.py

Each rule the collect step states has a case, as do the ones #187 added: a spec is a parent and never
a ticket, two tickets with one id refuse, and an issue carrying two of the labels is one ticket.

The script reads three things, and the cases reach each differently. The read from GitHub goes
through a fake `gh` on PATH, in an environment that inherits nothing, so no case reaches a real
repository even where a real gh is installed and GH_REPO is set. The read of the task branches runs
against real git, in a throwaway clone of a bare origin. The roadmap read alone is replaced outright:
every case gives its answer as ROAD, because that answer is `tools/roadmap.py --json`, which
tools/roadmap_tests.py holds.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import pathlib
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parent.parent


def load(name: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tools" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


collect = load("overnight_collect")
roadmap = load("roadmap")

# Answers from the fake gh: the listing for whichever label the call names, from GH_FIXTURES, with
# each call's arguments logged to GH_LOG; GH_FAIL makes it fail as gh does when it cannot reach
# GitHub. A timeline call answers with GH_FIXTURES/timeline-<n>.txt, the lines GH_TIMELINE's --jq
# writes. Anything else it is asked exits 97.
GH_DOUBLE = """#!/bin/bash
printf '%s\\n' "$*" >> "$GH_LOG"
if [ -n "$GH_FAIL" ]; then echo "gh: could not reach GitHub" >&2; exit 1; fi
if [ "$1" = "api" ]; then
  for argument in "$@"; do
    case "$argument" in repos/*/issues/*/timeline*)
      number="${argument#repos/*/issues/}"; number="${number%%/*}"
      cat "$GH_FIXTURES/timeline-$number.txt"; exit $? ;;
    esac
  done
  exit 97
fi
[ "$1 $2" = "issue list" ] || exit 97
for argument in "$@"; do
  case "$argument" in ready-for-agent|ready-for-human|needs-triage) label="$argument" ;; esac
done
[ -n "$label" ] || exit 97
cat "$GH_FIXTURES/$label.json"
"""

ROAD = {
    "closed_gates": [{"gate": "G2", "tag": "g2-local-slice"}],
    "rows": {"I-4": "landed", "1.4": "part-landed", "1.6b": "open", "1.7": "open", "6.3": "open",
             "6.9": "open"},
    "splits": {"6.3a": "landed"},
    "tickets": {"issue-65": "landed"},
}


# Issue bodies as they were filed, every acceptance box open: #172, #204, #206 and #209 are the shapes
# that ended partial on 2026-10-21 (#226), and #147 and #198 the single-owner, buildable shape #247
# names as passing. Copied whole, so a pattern is tried on the text around a box as well as the box.
FILED = {
    172: '''## Question

Charting decided the harness and the floor artifact live under `tools/bench`, owned by workstream 6 (verification). `OWNERSHIP.md` is an integrator file, and `tools/` is integrator by the `*` rule, so the path needs a line under "6 Verification and operations" and `AGENTS.md`'s owner table needs the same.

Resolution: `contracts/changes/<id>-tools-bench-ownership.md` filed on a branch and pushed, following the existing change request's shape, naming this ticket and the map. The integrator decides and edits; this ticket closes when the request is filed, and the answer links it.

## Acceptance

- [ ] The change request giving workstream 6 the `tools/bench` path is filed under `contracts/changes/` on a pushed branch, in the existing change requests' shape, naming this ticket and the map (#169); the answer links it.
''',
    204: '''**Raised by:** #58 (sitting C, stage C1, 2026-10-21)
**Owner:** integrator (root files, `src/Gateway/Dockerfile`), with workstream 5 for `fixtures/MockApi/Dockerfile`

## What to build

A workstation builds and runs the gateway and mock API images the way CI does, and the files the scripts write never reach a commit or a builder.

Tonight's first deployment hit two defects that a clean CI checkout can't show. The image build copied the workstation's own build output over the image's `linux-arm64` restore, so `dotnet publish --no-restore` failed with `NETSDK1047`; and because no `.dockerignore` exists, every build also sent the local certificate authority's private key, the scratch directory and the git-ignored Terraform variables to the builder. Then, once the images built, the gateway died on its first line: `UnauthorizedAccessException` on `appsettings.json`, because the files were owner-only on the workstation, `COPY` and `publish` keep that mode, and the final stage runs as `$APP_UID`.

The scripts also write the infrastructure outputs document into the tree, and git doesn't ignore it. It carries the account id and both apps' client ids, so one `git add .` would commit it.

## Acceptance criteria

- [ ] A root `.dockerignore` keeps build output, the local certificate authority, the scratch directory, `.git/`, Terraform's working directories, state and variable files out of the build context
- [ ] The infrastructure outputs document the scripts write is git-ignored
- [ ] Both Dockerfiles make the published output readable by the runtime user whatever the build context's file modes were
- [ ] From a tree that has host build output and an owner-only `appsettings.json`, the gateway image builds, and a container started as its own user gets past `WebApplication.CreateBuilder`

## Blocked by

None (can start immediately).
''',
    206: '''**Raised by:** #58 (sitting C, stage C1, 2026-10-21)
**Owner:** integrator (`Directory.Packages.props`, `docs/dependencies.md`), workstream 2 (`tools/spactl`, its tests)

## What to build

`spactl` signs in as the project's profile, so `seed.sh`, onboarding and every operator command run with no exported credentials.

The `spa-platform` profile signs in with `login_session`, which the new AWS experience sets up. The AWS CLI and Terraform resolve it. The .NET SDK resolves it only when the `AWSSDK.Signin` assembly is present, and `spactl` references the S3 package alone, so every provider in its credential chain failed tonight (`Assembly AWSSDK.Signin could not be found or loaded`). The sitting got past it by exporting short-lived credentials into the shell before `seed.sh`.

## Acceptance criteria

- [ ] `AWSSDK.Signin` is pinned centrally at the release that pairs with the pinned S3 package's core, and `spactl` references it
- [ ] `docs/dependencies.md` carries its row and the reason: the project's profile signs in with `login_session`, which the CLI and Terraform resolve on their own and the .NET SDK resolves only with this assembly
- [ ] A test in `spactl`'s test project loads the credential chain against a profile file that uses `login_session` and asserts it gets past the missing-assembly failure, with no AWS call
- [ ] `seed.sh` runs to its pass line on a workstation with the profile alone

## Blocked by

None (can start immediately).
''',
    209: '''**Raised by:** #63 (sitting C, stages C2 and C3, 2026-10-21)
**Owner:** workstream 6 (`tests/e2e`)

## What to build

The browser suite runs against the deployed cell, through the tunnel, against Cognito, and the G3 functional rows (F-01 to F-06 and F-08) pass there. Tonight the stack was up and verified, and none of them could run: every test builds the in-process stack, and nothing points the browser at the tunnel, signs in through the managed login, or reads the gateway's log from CloudWatch.

Running it needs a sitting: the cell deployed, `tunnel.sh` up, and the local authority trusted. The suite reads the cell's names from the infrastructure outputs document, never from Terraform state.

## Acceptance criteria

- [ ] A deployed stack beside the local one, chosen by an environment switch, with the local stack still the default
- [ ] Chromium reaches every app host through the tunnel's local port and trusts the local authority
- [ ] Sign-in goes through Cognito's managed login with the identity root's test users, whose passwords are read from Parameter Store at run time and never logged
- [ ] The gateway's request lines are read from its CloudWatch log group, so the rows that assert a rule id or a redaction still can
- [ ] F-01 to F-06 and F-08 pass against the deployed cell in a sitting, and the run is posted on #63

## Blocked by

None (can start immediately).
''',
    147: '''**Owner:** 4 API and security
**Plan row:** none

## Parent

Part of #140, the `stage-host` spec. Ticket 4 of 10: the security tests' four hosts.

## What to build

`GateHost`, `AntiforgeryHost`, `HeadersHost` and `SecurityHost` in the security tests are each reduced to what is their own: a services delegate with the stage's settings, a pipeline delegate holding the stages they prove in the contract's order, their host-resolution pin and their terminal, and a `SendAsync` carrying their stage's headers. Everything else comes from `StageHost`: the listener, the telemetry stand-in through `TelemetryStandIn`, the captured log, the raw sender and the client. The antiforgery host's stand-in, installed today after the forwarded-headers stage in the reverse of the pipeline's order, is installed by the module outermost, so the method allowlist's 405 finds a feature to write on. The project's own `ListLoggerProvider` and its raw sender and parser are deleted.

Call-shape edits the suite needs: a `string` becomes a `RawResponse` at the raw-send sites, the `lock` blocks over the live log list go because `Lines` is a snapshot, and rule-id queries use `LinesFor(rule, level?)`, which reads the `Rule` state property. With the floor at Trace a single-line assertion may meet a second, lower-level line; pass the level where it does. The pin stays in the pipeline delegate with no option and no helper. Note while migrating: the antiforgery host's pin matches the request's host without its port, while the real stage compares the full authority, port included; the ticket records this and changes nothing in the stage.

## Acceptance criteria

- [ ] The four hosts start on `StageHost` and each is its services delegate, pipeline delegate, pin, terminal and `SendAsync`, with no copied listener, stand-in, capture or sender left.
- [ ] `ListLoggerProvider` and the project's raw sender and parser are deleted.
- [ ] Every behaviour test in the project passes unchanged in behaviour, including the six proofs that a stage writes the feature null-conditionally with the stand-in off.
- [ ] A test sends the antiforgery host a method the allowlist refuses with the stand-in on and reads the method rule id from `TelemetryRules`, which the reversed order could never show.
- [ ] The change touches only workstream 4's paths and leaves no change request open.
''',
    198: '''**Owner:** 1 AWS and IaC
**Plan row:** none
**Raised by:** P8 in #186's independent review, https://github.com/adammedford/spa-platform/pull/186#issuecomment-6302058080

## Parent

#190

## What to build

The tunnel finds the cluster, the service and the load balancer by asking AWS for them by name, because the outputs document did not exist when 1.6b landed. 1.7 now writes it, and the frozen infra-outputs schema says the scripts read their names from it. The 1.6b review asked for this, and its merge body's Outstanding line left it out.

## Acceptance criteria

- [ ] The tunnel reads the ECS cluster, the service and the ALB's DNS name from the outputs document, and refuses with a named error when the document is absent.
- [ ] Its companion fails when the tunnel goes back to looking the names up in AWS.
- [ ] The tunnel's header says where its names come from.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
''',
}


def issue(number: int, body: str = "", *, title: str = "", assignees: tuple[str, ...] = (),
          blockers: tuple[tuple[int, str], ...] = (), subs: int = 0) -> dict:
    """One issue as `gh issue list --json` gives it."""
    return {
        "number": number,
        "title": title or f"Issue {number}",
        "body": body,
        "assignees": [{"login": login} for login in assignees],
        "url": f"https://github.com/o/r/issues/{number}",
        "labels": [],
        "blockedBy": {"nodes": [{"number": n, "state": state} for n, state in blockers]},
        "subIssuesSummary": {"total": subs, "completed": 0, "percentCompleted": 0},
    }


def body(row: str = "none", owner: str = "integrator", boxes: str = "- [ ] one") -> str:
    return f"**Owner:** {owner}\n**Plan row:** {row}\n\n## Acceptance\n{boxes}\n"


# The account the fixtures' URLs name as the repository's owner: https://github.com/o/r/...
OWNER = "o"


def labeled(label: str, actor: str | None, when: str = "2026-10-22T10:00:00Z") -> dict:
    """One `labeled` event as GitHub's timeline gives it, trimmed to the fields that matter and a
    few that do not; actor None is a deleted account, which GitHub gives as a null actor."""
    return {"id": 1, "node_id": "LE_x", "event": "labeled", "created_at": when,
            "actor": None if actor is None else {"login": actor, "id": 7, "type": "User"},
            "label": {"name": label, "color": "0e8a16"}}


def by(actor: str | None) -> list[dict]:
    """A timeline whose one ready-for-agent labelling was by actor."""
    return [labeled("ready-for-agent", actor)]


def resolved(agent=(), human=(), triage=(), branches=(), road=None, states=None, timelines=None,
             guide=None) -> dict:
    """The answer for these listings. Each ready-for-agent ticket was labelled by the repository's
    owner unless timelines says otherwise."""
    listings = {"ready-for-agent": list(agent), "ready-for-human": list(human),
                "needs-triage": list(triage)}
    given = {raw["number"]: by(OWNER) for raw in agent}
    given.update(timelines or {})
    return collect.resolve(listings, list(branches), road or ROAD, states=states, timelines=given,
                           guide=guide)


def refuse_gh(numbers, cwd=None):
    raise AssertionError(f"a test asked GitHub for {sorted(numbers)}; give the case its states")


def refuse_timeline(root, number):
    raise AssertionError(f"a test asked GitHub for the timeline of #{number}; give the case its "
                         "timelines")


def setUpModule():
    # Every in-process case states what the numbers its prose names are, and who labelled each
    # ready-for-agent ticket; none is asked of gh.
    shape = collect.ticket_shape()
    setUpModule.kept, shape.look_up = shape.look_up, refuse_gh
    setUpModule.timeline, collect.timeline = collect.timeline, refuse_timeline


def tearDownModule():
    collect.ticket_shape().look_up = setUpModule.kept
    collect.timeline = setUpModule.timeline


def by_issue(answer: dict) -> dict:
    return {t["issue"]: t for t in answer["tickets"]}


class Rules(unittest.TestCase):
    def test_the_plan_row_gives_the_id_and_anything_else_the_issue_number(self):
        for row, expected in (
            ("1.4", "1.4"), ("1.6b", "1.6b"), ("I-4", "I-4"), ("P2-7", "P2-7"), ("1.4  ", "1.4"),
            ("none", "issue-7"), ("", "issue-7"), ("-", "issue-7"),
            ("none; a leftover of the 2026-10-15 sitting.", "issue-7"),
        ):
            with self.subTest(row=row):
                found = by_issue(resolved(agent=[issue(7, body(row))]))[7]["id"]
                self.assertEqual(expected, found)

    def test_the_id_pattern_is_the_roadmaps(self):
        """Copied so the script stands alone, and held to the original here."""
        self.assertEqual(roadmap.ID.pattern, collect.PLAN_ID.pattern)

    def test_the_owner_is_the_first_role_the_owner_line_names(self):
        for line, expected in (
            ("1 AWS and IaC", "ws1-infra"), ("ws1-infra, run by the human", "ws1-infra"),
            ("2 Hosting and registry", "ws2-hosting"), ("workstream 2 (`tests/X`)", "ws2-hosting"),
            ("3 OAuth and sessions", "ws3-identity"), ("4 API and security", "ws4-proxy"),
            ("5 Fixtures", "ws5-fixtures"), ("6 Verification and operations", "ws6-verification"),
            ("workstream 6 (`tests/Gateway.*`)", "ws6-verification"),
            ("integrator (anyone may write a change request)", "integrator"),
            ("workstream 4, then the integrator", "ws4-proxy"), ("someone", "integrator"),
            # By position in the line, not by the order the roles are listed in ROLES.
            ("integrator, then workstream 4", "integrator"),
            ("workstream 6, then workstream 2", "ws6-verification"),
        ):
            with self.subTest(line=line):
                found = by_issue(resolved(agent=[issue(7, body(owner=line))]))[7]["owner"]
                self.assertEqual(expected, found)

    def test_an_integrator_row_is_the_integrators_whatever_the_owner_line_says(self):
        found = by_issue(resolved(agent=[issue(7, body("I-7", owner="ws2-hosting"))]))[7]
        self.assertEqual("integrator", found["owner"])
        self.assertIn("ws2-hosting, also named as an owner, lands in its own pull request",
                      found["why"])

    def test_why_names_the_other_owners_the_owner_line_names(self):
        """The collect step: when the Owner line names several, the first owns the ticket and
        "why" says the others land in their own pull requests. #75's line is the first case."""
        for line, owner, others in (
            ("workstream 2 (`tests/Platform.Registry.Tests`), workstream 6 (`tests/Gateway.*`)",
             "ws2-hosting", "; ws6-verification, also named as an owner, lands in its own pull "
                            "request"),
            ("workstream 4, then workstream 2, then the integrator", "ws4-proxy",
             "; ws2-hosting and integrator, also named as owners, land in their own pull "
             "requests"),
        ):
            with self.subTest(line=line):
                found = by_issue(resolved(agent=[issue(7, body(owner=line))]))[7]
                self.assertEqual(owner, found["owner"])
                self.assertEqual("ready-for-agent with no open blocker" + others, found["why"])

    def test_why_names_no_other_owner_when_the_line_names_one_role_however_often(self):
        """#159's line names the integrator twice; #90's names one workstream and a person."""
        for line in ("integrator (anyone may write a change request; the integrator decides it)",
                     "ws1-infra, run by the human"):
            with self.subTest(line=line):
                found = by_issue(resolved(agent=[issue(7, body(owner=line))]))[7]
                self.assertEqual("ready-for-agent with no open blocker", found["why"])

    def test_the_status_takes_the_first_rule_that_fires(self):
        answer = by_issue(resolved(
            agent=[issue(1, body(), assignees=("adam",)),
                   issue(2, body("1.7")),
                   issue(3, body("I-4")),
                   issue(4, body("1.6b"))],
            human=[issue(5, body(), assignees=("adam",))],
            triage=[issue(6, body())],
            branches=["task/1.7-scripts", "task/I-4-landed-row", "task/1.6-other-row"],
        ))
        self.assertEqual("claimed", answer[1]["status"])          # an assignee
        self.assertEqual("claimed", answer[2]["status"])          # its row's branch, not landed
        self.assertEqual("ready-for-agent", answer[3]["status"])  # a branch on a landed row
        self.assertEqual("ready-for-agent", answer[4]["status"])  # task/1.6- is not task/1.6b-
        self.assertEqual("ready-for-human", answer[5]["status"])  # the label beats the assignee
        self.assertEqual("needs-triage", answer[6]["status"])

    def test_an_issue_with_two_triage_labels_is_one_ticket_with_the_first_label_in_order(self):
        """A human relabelling by hand can leave two of the three labels on one issue, which then
        comes back in two listings. It is one ticket, its status the label the order puts first:
        ready-for-human, then needs-triage, then ready-for-agent."""
        for listed, expected in (
            ({"agent": [issue(5, body())], "human": [issue(5, body())]}, "ready-for-human"),
            ({"agent": [issue(5, body())], "triage": [issue(5, body())]}, "needs-triage"),
            ({"human": [issue(5, body())], "triage": [issue(5, body())]}, "ready-for-human"),
            ({"agent": [issue(5, body())], "human": [issue(5, body())],
              "triage": [issue(5, body())]}, "ready-for-human"),
        ):
            with self.subTest(labels=sorted(listed)):
                answer = resolved(**listed)
                self.assertEqual([(5, expected)],
                                 [(t["issue"], t["status"]) for t in answer["tickets"]])

    def test_a_spec_with_two_triage_labels_is_one_parent(self):
        answer = resolved(agent=[issue(139, "", subs=2)], human=[issue(139, "", subs=2)])
        self.assertEqual([(139, "ready-for-human")],
                         [(p["issue"], p["label"]) for p in answer["parents"]])

    def test_blocked_settles_however_long_the_chain(self):
        """10 waits on 11, 11 on 12, and 12 is a human's: listed first, 10 is judged before 11
        is demoted, so a single pass would leave it ready."""
        answer = by_issue(resolved(
            agent=[issue(10, body(), blockers=((11, "OPEN"),)),
                   issue(11, body(), blockers=((12, "OPEN"),)),
                   issue(13, body(), blockers=((14, "OPEN"),)),
                   issue(14, body())],
            human=[issue(12, body())],
        ))
        self.assertEqual("blocked", answer[11]["status"])
        self.assertEqual("blocked", answer[10]["status"])
        self.assertEqual(["issue-11"], answer[10]["blockedBy"])
        self.assertEqual("ready-for-agent", answer[13]["status"])  # its blocker runs first tonight
        self.assertEqual("ready-for-agent", answer[14]["status"])

    def test_a_blocker_outside_tonights_set_blocks_and_a_closed_one_does_not(self):
        answer = by_issue(resolved(agent=[issue(20, body(), blockers=((99, "OPEN"),)),
                                          issue(21, body(), blockers=((98, "CLOSED"),))]))
        self.assertEqual(("blocked", ["issue-99"]), (answer[20]["status"], answer[20]["blockedBy"]))
        self.assertEqual(("ready-for-agent", []), (answer[21]["status"], answer[21]["blockedBy"]))

    def test_a_blocker_tonight_is_named_by_its_id(self):
        answer = by_issue(resolved(agent=[issue(30, body(), blockers=((31, "OPEN"),)),
                                          issue(31, body("1.7"))]))
        self.assertEqual(["1.7"], answer[30]["blockedBy"])

    def test_open_boxes_are_counted_and_no_checkbox_is_minus_one(self):
        answer = by_issue(resolved(agent=[
            issue(40, body(boxes="- [ ] a\n- [ ] b\n- [x] c")),
            issue(41, body(boxes="- [x] a\n- [X] b")),
            issue(42, "**Owner:** integrator\n\nNo checkbox here."),
        ]))
        self.assertEqual([2, 0, -1], [answer[n]["openBoxes"] for n in (40, 41, 42)])

    def test_an_issue_with_sub_issues_is_a_parent_and_never_a_ticket(self):
        """#139 to #142 on 2026-10-20: specs labelled ready-for-agent, with no checkbox."""
        answer = resolved(agent=[issue(139, "## Problem Statement\n", subs=2), issue(153, body())])
        self.assertEqual([153], [t["issue"] for t in answer["tickets"]])
        self.assertEqual([139], [p["issue"] for p in answer["parents"]])

    def test_a_session_foreground_line_no_longer_keeps_an_issue_out_of_the_tickets(self):
        """#137 (ADR-0026, Q12 of #135): the workflow files are tracked, so a lane can write them
        and the `**Session:** foreground` rule is gone. An issue that carries the line, as #109 did
        on 2026-10-21, is read as any other ticket, and the answer has no "foreground" list."""
        answer = resolved(agent=[
            issue(109, "**Owner:** integrator\n**Session:** foreground — not an overnight lane\n"),
            issue(137, "Today #83 carries `**Session:** foreground — ...`, which this ends.\n"),
        ])
        self.assertEqual([109, 137], sorted(t["issue"] for t in answer["tickets"]))
        self.assertNotIn("foreground", answer)

    def test_two_tickets_with_one_id_refuse_and_name_both(self):
        """#56 and #180 on 2026-10-20, both `**Plan row:** 1.4`."""
        with self.assertRaises(collect.Refused) as refused:
            resolved(agent=[issue(56, body("1.4")), issue(180, body("1.4"))])
        self.assertIn("issues #56 and #180 share the id 1.4", str(refused.exception))

    def test_tickets_are_numbered_in_plan_order_then_by_issue(self):
        answer = resolved(agent=[issue(9, body()), issue(5, body("6.9")), issue(7, body("1.4")),
                                 issue(8, body())])
        self.assertEqual([(1, 7), (2, 5), (3, 8), (4, 9)],
                         [(t["n"], t["issue"]) for t in answer["tickets"]])

    def test_an_open_issue_on_a_landed_row_is_reported(self):
        answer = resolved(agent=[issue(65, body())], branches=[])
        self.assertEqual([{"id": "issue-65", "issue": 65}], answer["landedButOpen"])


class WillEndPartial(unittest.TestCase):
    """#228: the launch plan of 2026-10-21 forecast 22 integrated tickets and got 15 (#226), and
    nothing at collect time said which would end partial. Each ticket now carries "willEndPartial".
    Since #248 its entries are tools/ticket_shape.py's failures, one per rule broken, so the check at
    creation and the forecast at launch judge a ticket by one grammar: each entry is the rule and the
    line ticket_shape prints for it. A case per rule, each red while collect keeps a table of its own."""

    def flags(self, number: int, text: str | None = None, *, label: str = "agent",
              blockers: tuple[tuple[int, str], ...] = (), states=None,
              also: tuple[dict, ...] = ()) -> list[dict]:
        listed = {label: [issue(number, FILED[number] if text is None else text,
                                blockers=blockers), *also]}
        return by_issue(resolved(**listed, states=states))[number]["willEndPartial"]

    def rules(self, flags: list[dict]) -> list[str]:
        return [flag["rule"] for flag in flags]

    def test_one_owner_flags_touches_paths_of_two_owners(self):
        text = ("**Owner:** integrator\n**Touches:** `src/Gateway/Dockerfile`, "
                "`fixtures/MockApi/Dockerfile`\n\n## Acceptance\n- [ ] one\n")
        flags = self.flags(7, text)
        self.assertEqual(["one-owner"], self.rules(flags))
        self.assertIn("ws5-fixtures: fixtures/MockApi/Dockerfile", flags[0]["reason"])

    def test_owner_line_flags_204_and_206_for_their_second_owner(self):
        for number, other in ((204, "ws5-fixtures"), (206, "ws2-hosting")):
            with self.subTest(issue=number):
                flags = self.flags(number)
                self.assertEqual(["owner-line"], self.rules(flags))
                self.assertIn(f"integrator and {other}", flags[0]["reason"])

    def test_owner_line_flags_172_for_naming_no_owner(self):
        flags = self.flags(172)
        self.assertEqual(["owner-line", "sitting-or-landing"], self.rules(flags))
        self.assertIn("no role", flags[0]["reason"])
        self.assertIn("pushed", flags[1]["reason"])

    def test_sitting_or_landing_flags_each_box_of_209_that_names_one(self):
        """Box 1 says "A deployed stack" and box 5 is the sitting's. ticket_shape's word list reads
        both alike, and its docstring names the escape: reword the box."""
        flags = self.flags(209)
        self.assertEqual(["sitting-or-landing", "sitting-or-landing"], self.rules(flags))
        self.assertIn("A deployed stack beside the local one", flags[0]["reason"])
        self.assertIn("F-01 to F-06 and F-08 pass against the deployed cell", flags[1]["reason"])

    def test_sitting_or_landing_reads_ticket_shapes_own_word_list(self):
        """Read from the module the creation check runs, so a word added there is forecast here."""
        for word, _ in collect.ticket_shape().SITTING_OR_LANDING:
            with self.subTest(word=word):
                flags = self.flags(7, body(boxes=f"- [ ] It needs {word} first"))
                self.assertEqual(["sitting-or-landing"], self.rules(flags))
                self.assertIn(word, flags[0]["reason"])

    def test_blocker_edge_flags_a_stated_wait_with_no_edge(self):
        text = body() + "\nBlocked by #100.\n"
        self.assertEqual(["blocker-edge"], self.rules(self.flags(7, text, states={100: "OPEN"})))
        self.assertEqual([], self.flags(7, text, blockers=((100, "OPEN"),)))

    def test_a_listed_ticket_is_an_open_issue_with_no_lookup(self):
        # #100 is in tonight's listings, so it is open; refuse_gh proves nothing is asked.
        text = body() + "\nBlocked by #100.\n"
        flags = self.flags(7, text, also=(issue(100, body()),))
        self.assertEqual(["blocker-edge"], self.rules(flags))

    def test_stale_prose_is_not_forecast(self):
        # #284: a closed issue, a pull request or no issue is no wait, so no partial; a closed edge
        # says so with no lookup.
        text = body() + "\nBlocked by #100.\n"
        for kind in ("CLOSED", "PULL_REQUEST", "MISSING"):
            with self.subTest(kind):
                self.assertEqual([], self.flags(7, text, states={100: kind}))
        self.assertEqual([], self.flags(7, text, blockers=((100, "CLOSED"),)))

    def test_the_numbers_left_are_asked_of_gh_once(self):
        asked = []
        shape = collect.ticket_shape()

        def look_up(numbers, cwd=None):
            asked.append((sorted(numbers), cwd))
            return {n: "CLOSED" for n in numbers}

        shape.look_up = look_up
        try:
            listed = [issue(7, body() + "\nBlocked by #100.\n"),
                      issue(8, body() + "\nStarts after #101 and once #100 lands.\n")]
            answer = by_issue(resolved(agent=listed))
        finally:
            shape.look_up = refuse_gh
        # Run in this checkout, where the tool lives, since no --repo named another (#377).
        self.assertEqual([([100, 101], collect.ROOT)], asked)
        self.assertEqual(([], []), (answer[7]["willEndPartial"], answer[8]["willEndPartial"]))

    def test_a_failed_lookup_refuses_the_answer(self):
        shape = collect.ticket_shape()

        def look_up(numbers, cwd=None):
            raise shape.Unreadable("gh could not reach GitHub")

        shape.look_up = look_up
        try:
            with self.assertRaises(collect.Refused):
                resolved(agent=[issue(7, body() + "\nBlocked by #100.\n")])
        finally:
            shape.look_up = refuse_gh

    def test_foreground_flags_a_ready_for_agent_ticket(self):
        text = "**Session:** foreground\n" + body()
        self.assertEqual(["foreground"], self.rules(self.flags(7, text)))

    def test_spec_is_never_a_ticket_so_never_forecast(self):
        """ticket_shape's sixth rule fails a ready-for-agent issue with sub-issues. Collect lists
        such an issue under "parents" and gives it no lane, so there is no ticket to flag."""
        answer = resolved(agent=[issue(7, body(), subs=2)])
        self.assertEqual(([], [7]), (answer["tickets"], [p["issue"] for p in answer["parents"]]))

    def test_one_owner_and_buildable_boxes_is_not_flagged(self):
        for number in (147, 198):
            with self.subTest(issue=number):
                self.assertEqual([], self.flags(number))

    def test_a_ticked_box_is_not_flagged(self):
        ticked = FILED[209].replace("- [ ] F-01", "- [x] F-01").replace("- [ ] A deployed",
                                                                          "- [x] A deployed")
        self.assertEqual([], self.flags(209, ticked))

    def test_a_human_ticket_carries_the_rules_that_read_no_label(self):
        """The rules under ready-for-agent alone read a lane's work; a human's ticket is judged by
        its own label, as the creation check judges it, and still carries the owner rules."""
        self.assertEqual(["owner-line"], self.rules(self.flags(204, label="human")))
        self.assertEqual([], self.flags(209, label="human"))


class LabelledBy(unittest.TestCase):
    """#316: `ready-for-agent` is a lane's promise, and CODING_STANDARDS.md -> "An authorisation is
    read from its author" says it counts only when the account the rule names applied it. So the
    actor of the last `labeled` event that applied it is read off the issue's timeline, and any
    other actor leaves the ticket needs-triage, named in "why"."""

    # The guide's line as the proposal on #316 words it, typed here: the live guide names no account
    # yet, and a case on it would pass on the fallback alone.
    GUIDE = ("## This repository's additions\n\n"
             "- **The human's account is `@adam-m`.** A `Confirm` comment and the\n"
             "  `ready-for-agent` label count only when this account wrote them.\n")

    def ticket(self, timeline: list[dict], guide: str | None = None, **listed) -> dict:
        listed.setdefault("agent", [issue(5, body())])
        return by_issue(resolved(timelines={5: timeline}, guide=guide, **listed))[5]

    def test_a_label_another_account_applied_is_needs_triage_naming_the_actor(self):
        found = self.ticket(by("mallory"))
        self.assertEqual("needs-triage", found["status"])
        self.assertEqual("ready-for-agent was last applied by @mallory, not by @o, the "
                         "repository's owner, since docs/agents/issue-tracker.md names no account; "
                         "a lane goes only where that account applied it", found["why"])

    def test_the_owners_label_is_ready_for_agent(self):
        found = self.ticket(by(OWNER))
        self.assertEqual(("ready-for-agent", "ready-for-agent with no open blocker"),
                         (found["status"], found["why"]))

    def test_the_login_is_compared_without_case(self):
        """GitHub's logins are case-insensitive, so the owner's own label is its own whatever the
        case the URL spells it in."""
        self.assertEqual("ready-for-agent", self.ticket(by(OWNER.upper()))["status"])

    def test_on_a_saved_timeline_the_last_ready_for_agent_labelling_is_the_one_read(self):
        """A timeline as GitHub gives one: other events, another label, the label applied, removed
        and applied again. Only the last application of ready-for-agent says who promised it."""
        unlabeled = dict(labeled("ready-for-agent", OWNER), event="unlabeled")
        history = [
            {"event": "cross-referenced", "actor": {"login": "mallory"}},
            labeled("ready-for-agent", "mallory", "2026-10-20T10:00:00Z"),
            unlabeled,
            labeled("ready-for-agent", OWNER, "2026-10-21T10:00:00Z"),
            labeled("needs-info", "mallory", "2026-10-22T10:00:00Z"),
            {"event": "commented", "actor": {"login": "mallory"}, "body": "ready-for-agent"},
        ]
        self.assertEqual("ready-for-agent", self.ticket(history)["status"])
        history.append(labeled("ready-for-agent", "mallory", "2026-10-23T10:00:00Z"))
        found = self.ticket(history)
        self.assertEqual("needs-triage", found["status"])
        self.assertIn("@mallory", found["why"])

    def test_no_labelling_event_or_a_deleted_actor_is_needs_triage(self):
        """Fails closed: a label nothing says the account applied is not the account's."""
        for name, timeline, said in (
            ("no event", [], "shows no `labeled` event applying it"),
            ("another label only", [labeled("bug", OWNER)], "shows no `labeled` event"),
            ("a deleted actor", by(None), "last applied by an account GitHub no longer names"),
        ):
            with self.subTest(name=name):
                found = self.ticket(timeline)
                self.assertEqual("needs-triage", found["status"])
                self.assertIn(said, found["why"])

    def test_an_assignee_does_not_make_an_outsiders_label_a_claim(self):
        """The label does not count at all, so what sits on top of it is not read as a claim."""
        found = self.ticket(by("mallory"), agent=[issue(5, body(), assignees=("adam",))])
        self.assertEqual("needs-triage", found["status"])

    def test_the_human_and_triage_labels_come_first_and_are_not_read_from_the_timeline(self):
        """Neither unlocks a lane, so its writer is not asked; refuse_timeline proves no read."""
        for listed, expected in (({"human": [issue(5, body())]}, "ready-for-human"),
                                 ({"triage": [issue(5, body())]}, "needs-triage")):
            with self.subTest(expected=expected):
                answer = collect.resolve(
                    {"ready-for-agent": [], "ready-for-human": listed.get("human", []),
                     "needs-triage": listed.get("triage", [])}, [], ROAD, timelines={})
                self.assertEqual(expected, by_issue(answer)[5]["status"])

    def test_a_blocker_labelled_by_another_account_blocks_its_dependent(self):
        answer = by_issue(resolved(agent=[issue(5, body(), blockers=((6, "OPEN"),)),
                                          issue(6, body())], timelines={6: by("mallory")}))
        self.assertEqual(("blocked", "needs-triage"), (answer[5]["status"], answer[6]["status"]))

    def test_the_account_the_guide_names_is_the_one_read_not_the_owner(self):
        """Both directions: under the guide's line the owner's label no longer counts, and the
        named account's does."""
        self.assertEqual("adam-m", collect.named_account(self.GUIDE))
        owners = self.ticket(by(OWNER), guide=self.GUIDE)
        self.assertEqual("needs-triage", owners["status"])
        self.assertIn("not by @adam-m, the account docs/agents/issue-tracker.md names",
                      owners["why"])
        self.assertEqual("ready-for-agent", self.ticket(by("adam-m"), guide=self.GUIDE)["status"])

    def test_prose_about_an_account_names_none(self):
        for text in (None, "", "A `Confirm` comment is the human's act.",
                     "The human's account is `@adam-m`.",
                     "  - **The human's account is `@adam-m`.** indented, so not the guide's line"):
            with self.subTest(text=text):
                self.assertIsNone(collect.named_account(text))

    def test_the_live_guide_parses(self):
        """The guide as it stands either names an account or names none; it never fails to read."""
        text = (ROOT / collect.GUIDE).read_text()
        self.assertIn(collect.named_account(text), (None, *[m.group("login") for m in
                                                            collect.ACCOUNT_LINE.finditer(text)]))

    def test_main_reads_the_guide_from_the_checkout_and_none_when_it_is_absent(self):
        """tracker_guide() is main()'s one read of the guide, which ThroughGh pins (#331)."""
        with tempfile.TemporaryDirectory() as name:
            root = pathlib.Path(name)
            self.assertIsNone(collect.tracker_guide(root))
            guide = root / collect.GUIDE
            guide.parent.mkdir(parents=True)
            guide.write_text("- **The human's account is `@someone`.** Only it confirms.\n")
            self.assertEqual("someone", collect.named_account(collect.tracker_guide(root)))

    def test_the_account_line_is_the_steering_checks(self):
        """One line names the account for both readers: the label here, a `Confirm` there."""
        spec = importlib.util.spec_from_file_location("steering", ROOT / ".github" / "steering.py")
        steering = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(steering)
        self.assertEqual(collect.ACCOUNT_LINE.pattern, steering.ACCOUNT_LINE.pattern)
        self.assertEqual(collect.GUIDE, steering.GUIDE)


class ThroughGh(unittest.TestCase):
    """main() end to end, with the fake gh on PATH and nothing else of the caller's environment."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        base = pathlib.Path(self.tmp.name)
        self.bin, self.fixtures, self.log = base / "bin", base / "fixtures", base / "gh.log"
        self.bin.mkdir()
        self.fixtures.mkdir()
        gh = self.bin / "gh"
        gh.write_text(GH_DOUBLE)
        gh.chmod(gh.stat().st_mode | stat.S_IXUSR)
        self.env = {"PATH": f"{self.bin}:/usr/bin:/bin", "HOME": str(base),
                    "GH_FIXTURES": str(self.fixtures), "GH_LOG": str(self.log)}
        # The repository's branches, roadmap and tracker guide are this checkout's; the cases pin
        # them. A guide naming no account leaves the fixtures' owner as the one who may label (#316).
        for name, value in (("branches", lambda root: []), ("roadmap", lambda root: ROAD),
                            ("tracker_guide", lambda root: None)):
            patcher = mock.patch.object(collect, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def listings(self, agent=(), human=(), triage=(), actor: str = OWNER) -> None:
        for label, issues in (("ready-for-agent", agent), ("ready-for-human", human),
                              ("needs-triage", triage)):
            (self.fixtures / f"{label}.json").write_text(json.dumps(list(issues)))
        for raw in agent:
            events = [labeled("bug", "someone"), labeled("ready-for-agent", actor)]
            (self.fixtures / f"timeline-{raw['number']}.txt").write_text(
                "".join(json.dumps(event) + "\n" for event in events))

    def run_main(self, *argv: str, **extra: str) -> tuple[int, str, str]:
        # The real timeline read, which setUpModule replaces for the in-process cases.
        with mock.patch.object(collect, "timeline", setUpModule.timeline):
            return self.run_main_as_is(*argv, **extra)

    def run_main_as_is(self, *argv: str, **extra: str) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.dict(os.environ, {**self.env, **extra}, clear=True), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = collect.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_it_asks_for_each_label_with_the_sub_issue_field_and_prints_the_tickets(self):
        self.listings(agent=[issue(1, body("1.7"))], human=[issue(2, body())])
        code, out, err = self.run_main("--json")
        self.assertEqual(0, code, err)
        calls = self.log.read_text().splitlines()
        self.assertEqual(4, len(calls))
        for label, call in zip(collect.LABELS, calls):
            self.assertIn(f"--label {label} --limit 500 --json {collect.FIELDS}", call)
        # And the one ready-for-agent ticket's timeline, which says who labelled it.
        self.assertIn("api --paginate repos/{owner}/{repo}/issues/1/timeline?per_page=100",
                      calls[3])
        self.assertIn("subIssuesSummary", collect.FIELDS)
        answer = json.loads(out)
        self.assertEqual([[], []], [t["willEndPartial"] for t in answer["tickets"]])
        self.assertEqual([("1.7", "ready-for-agent"), ("issue-2", "ready-for-human")],
                         [(t["id"], t["status"]) for t in answer["tickets"]])

    def test_the_account_the_guide_names_is_the_one_whose_label_counts(self):
        """The guide's account line, read through main(), outranks the repository's owner (#316)."""
        guide = "- **The human's account is `@someone`.** A `Confirm` comment counts only from it.\n"
        self.listings(agent=[issue(1, body("1.7"))])
        with mock.patch.object(collect, "tracker_guide", lambda root: guide):
            code, out, err = self.run_main("--json")
        self.assertEqual(0, code, err)
        self.assertEqual([("1.7", "needs-triage")],
                         [(t["id"], t["status"]) for t in json.loads(out)["tickets"]])
        self.listings(agent=[issue(1, body("1.7"))], actor="someone")
        with mock.patch.object(collect, "tracker_guide", lambda root: guide):
            code, out, err = self.run_main("--json")
        self.assertEqual([("1.7", "ready-for-agent")],
                         [(t["id"], t["status"]) for t in json.loads(out)["tickets"]])

    def test_the_json_marks_a_ticket_that_will_end_partial_and_stderr_counts_it(self):
        self.listings(agent=[issue(204, FILED[204]), issue(198, FILED[198])])
        code, out, err = self.run_main("--json")
        self.assertEqual(0, code, err)
        found = {t["issue"]: t["willEndPartial"] for t in json.loads(out)["tickets"]}
        self.assertEqual([], found[198])
        self.assertEqual(["owner-line"], [flag["rule"] for flag in found[204]])
        self.assertIn("1 will end partial", err)

    def test_a_shared_id_exits_1_with_nothing_on_stdout(self):
        self.listings(agent=[issue(56, body("1.4")), issue(180, body("1.4"))])
        code, out, err = self.run_main("--json")
        self.assertEqual((1, ""), (code, out))
        self.assertIn("issues #56 and #180 share the id 1.4", err)

    def test_a_failed_read_exits_1_with_nothing_on_stdout(self):
        self.listings()
        code, out, err = self.run_main("--json", GH_FAIL="1")
        self.assertEqual((1, ""), (code, out))
        self.assertIn("no tickets printed", err)

    def test_saved_listings_are_read_instead_of_asking_github(self):
        saved = pathlib.Path(self.tmp.name) / "saved"
        saved.mkdir()
        (saved / "ready-for-agent.json").write_text(json.dumps([issue(1, body())]))
        (saved / "timelines").mkdir()
        (saved / "timelines" / "1.json").write_text(json.dumps(by(OWNER)))
        (saved / "branches.txt").write_text("task/issue-1-claimed\n")
        (saved / "roadmap.json").write_text(json.dumps(ROAD))
        code, out, err = self.run_main("--json", "--from", str(saved))
        self.assertEqual(0, code, err)
        self.assertFalse(self.log.exists(), "gh was asked although the listings were given")
        self.assertEqual("claimed", json.loads(out)["tickets"][0]["status"])

    def test_an_outsiders_label_read_live_is_needs_triage(self):
        self.listings(agent=[issue(1, body())], actor="mallory")
        code, out, err = self.run_main("--json")
        self.assertEqual(0, code, err)
        ticket = json.loads(out)["tickets"][0]
        self.assertEqual("needs-triage", ticket["status"])
        self.assertIn("last applied by @mallory", ticket["why"])

    def test_a_timeline_line_that_is_not_an_event_is_refused(self):
        self.listings(agent=[issue(1, body())])
        for text in ("not json\n", "[1, 2]\n"):
            with self.subTest(text=text):
                (self.fixtures / "timeline-1.txt").write_text(text)
                code, out, err = self.run_main("--json")
                self.assertEqual((1, ""), (code, out))
                self.assertIn("the timeline of #1 gave", err)

    def test_a_failed_timeline_read_exits_1(self):
        self.listings(agent=[issue(1, body())])
        (self.fixtures / "timeline-1.txt").unlink()
        code, out, err = self.run_main("--json")
        self.assertEqual((1, ""), (code, out))
        self.assertIn("no tickets printed", err)

    def test_a_saved_run_without_a_ready_for_agent_tickets_timeline_is_refused(self):
        """Not read live, and not read as the account's: the answer would be a guess."""
        saved = pathlib.Path(self.tmp.name) / "saved"
        saved.mkdir()
        (saved / "ready-for-agent.json").write_text(json.dumps([issue(1, body())]))
        (saved / "ready-for-human.json").write_text(json.dumps([issue(2, body())]))
        (saved / "branches.txt").write_text("")
        (saved / "roadmap.json").write_text(json.dumps(ROAD))
        code, out, err = self.run_main("--json", "--from", str(saved))
        self.assertEqual((1, ""), (code, out))
        self.assertIn("timelines/1.json is missing", err)
        self.assertFalse(self.log.exists(), "gh was asked although the run was a saved one")

    def test_a_listing_in_another_shape_is_refused_rather_than_read_as_unblocked(self):
        """gh gives blockedBy as nodes, each with its state. A listing that does not is unreadable,
        and reading it as no blocker would give a blocked ticket a lane."""
        saved = pathlib.Path(self.tmp.name) / "saved"
        saved.mkdir()
        (saved / "roadmap.json").write_text(json.dumps(ROAD))
        for shape, blocked in (("a list", [{"number": 9, "state": "OPEN"}]),
                               ("no state", {"nodes": [{"number": 9}]}), ("absent", None)):
            with self.subTest(shape=shape):
                raw = issue(1, body())
                if blocked is None:
                    del raw["blockedBy"]
                else:
                    raw["blockedBy"] = blocked
                (saved / "ready-for-agent.json").write_text(json.dumps([raw]))
                code, out, err = self.run_main("--json", "--from", str(saved))
                self.assertEqual((1, ""), (code, out))
                self.assertIn("could not read the input", err)


class ForecastFailsClosed(unittest.TestCase):
    """A ticket-shape check that cannot load or judge stops the tool on exit 1 with nothing on
    stdout, as a failed read does: an empty willEndPartial from a check that never ran would read
    as "none flagged" (CODING_STANDARDS.md, "A check fails closed")."""

    PARTS = ("tools/overnight_collect.py", "tools/ticket_shape.py", ".github/path-ownership.py",
             "OWNERSHIP.md")

    def test_a_missing_part_of_the_check_exits_1_with_nothing_on_stdout(self):
        for missing in self.PARTS[1:]:
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as scratch:
                tree = pathlib.Path(scratch)
                for part in self.PARTS:
                    if part != missing:
                        (tree / part).parent.mkdir(parents=True, exist_ok=True)
                        (tree / part).write_bytes((ROOT / part).read_bytes())
                saved = tree / "saved"
                saved.mkdir()
                (saved / "ready-for-agent.json").write_text(json.dumps([issue(198, FILED[198])]))
                (saved / "timelines").mkdir()
                (saved / "timelines" / "198.json").write_text(json.dumps(by(OWNER)))
                (saved / "branches.txt").write_text("")
                (saved / "roadmap.json").write_text(json.dumps(ROAD))
                done = subprocess.run(
                    [sys.executable, "-I", str(tree / self.PARTS[0]), "--json", "--from",
                     str(saved)], capture_output=True, text=True, cwd=scratch, check=False,
                    env={"PATH": "/usr/bin:/bin", "HOME": scratch})
                self.assertEqual((1, ""), (done.returncode, done.stdout), done.stderr)
                self.assertIn("no tickets printed: the ticket-shape check could not", done.stderr)

    def test_the_whole_tree_judges(self):
        """The same layout with nothing missing prints the tickets, so the cases above fail on
        the part they leave out and not on the copy."""
        with tempfile.TemporaryDirectory() as scratch:
            tree = pathlib.Path(scratch)
            for part in self.PARTS:
                (tree / part).parent.mkdir(parents=True, exist_ok=True)
                (tree / part).write_bytes((ROOT / part).read_bytes())
            saved = tree / "saved"
            saved.mkdir()
            (saved / "ready-for-agent.json").write_text(json.dumps([issue(204, FILED[204])]))
            (saved / "timelines").mkdir()
            (saved / "timelines" / "204.json").write_text(json.dumps(by(OWNER)))
            (saved / "branches.txt").write_text("")
            (saved / "roadmap.json").write_text(json.dumps(ROAD))
            done = subprocess.run(
                [sys.executable, "-I", str(tree / self.PARTS[0]), "--json", "--from", str(saved)],
                capture_output=True, text=True, cwd=scratch, check=False,
                env={"PATH": "/usr/bin:/bin", "HOME": scratch})
            self.assertEqual(0, done.returncode, done.stderr)
            flags = json.loads(done.stdout)["tickets"][0]["willEndPartial"]
            self.assertEqual(["owner-line"], [flag["rule"] for flag in flags])


class BranchesThroughGit(unittest.TestCase):
    """branches() against real git: a throwaway clone of a bare origin, in an environment that
    inherits nothing, as tools/prune_branches_tests.py builds one. A claim is a `task/<id>-` branch,
    local or on origin, so a branch that exists only on origin claims its row as much as a local
    one does."""

    def setUp(self):
        scratch = tempfile.TemporaryDirectory(prefix="overnight-collect-")
        self.addCleanup(scratch.cleanup)
        base = pathlib.Path(scratch.name).resolve()
        found = shutil.which("git")
        self.assertIsNotNone(found, "git is not on PATH, and branches() reads it")
        (base / "bin").mkdir()
        (base / "bin" / "git").symlink_to(found)
        (base / "home").mkdir()
        config = base / "gitconfig"
        config.write_text("[user]\n\tname = Case\n\temail = case@example.invalid\n"
                          "[init]\n\tdefaultBranch = main\n"
                          "[gc]\n\tauto = 0\n[maintenance]\n\tauto = false\n")
        self.env = {"PATH": str(base / "bin"), "HOME": str(base / "home"),
                    "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": str(config), "LC_ALL": "C"}
        self.origin, self.work = base / "origin.git", base / "work"
        self.git("init", "-q", "--bare", str(self.origin), cwd=base)
        self.git("clone", "-q", str(self.origin), str(self.work), cwd=base)
        self.git("commit", "-q", "--allow-empty", "-m", "The first commit")
        self.git("push", "-q", "origin", "HEAD:refs/heads/main")

    def git(self, *args: str, cwd: pathlib.Path | None = None) -> str:
        done = subprocess.run(["git", *args], cwd=cwd or self.work, env=self.env,
                              capture_output=True, text=True, check=False)
        self.assertEqual(0, done.returncode, f"git {' '.join(args)}: {done.stderr}")
        return done.stdout.strip()

    def test_a_task_branch_local_or_only_on_origin_is_read_and_nothing_else(self):
        self.git("branch", "task/1.7-only-local")
        # Pushed, then deleted locally: what remains is origin's, as the push left it.
        self.git("branch", "task/1.6b-only-on-origin")
        self.git("push", "-q", "origin", "task/1.6b-only-on-origin")
        self.git("branch", "-D", "task/1.6b-only-on-origin")
        self.git("branch", "integrator/not-a-claim")
        self.git("push", "-q", "origin", "integrator/not-a-claim")
        self.assertEqual("", self.git("branch", "--list", "task/1.6b-only-on-origin"))
        with mock.patch.dict(os.environ, self.env, clear=True):
            found = collect.branches(self.work)
        self.assertEqual(["task/1.6b-only-on-origin", "task/1.7-only-local"], sorted(found))
        # And the name it gives is one the claim rule reads.
        answer = by_issue(resolved(agent=[issue(1, body("1.6b"))], branches=found))
        self.assertEqual("claimed", answer[1]["status"])


# The fake gh as the real one behaves (#349): it finds the repository from the directory it runs
# in, so outside a checkout it fails; it logs that directory before each call's arguments, and
# answers the prose look-up's graphql query with GH_FIXTURES/graphql.json.
GH_IN_A_CHECKOUT = GH_DOUBLE.replace(
    'printf \'%s\\n\' "$*" >> "$GH_LOG"\n',
    'printf \'%s\\t%s\\n\' "$PWD" "$*" >> "$GH_LOG"\n'
    'git rev-parse --git-dir >/dev/null 2>&1 || '
    '{ echo "failed to run git: fatal: not a git repository" >&2; exit 1; }\n'
    'if [ "$1 $2" = "api graphql" ]; then cat "$GH_FIXTURES/graphql.json"; exit $?; fi\n', 1)


class RepoFlag(unittest.TestCase):
    """--repo DIR (#377): the checkout whose git, gh, roadmap and tracker guide the tool reads, so
    origin/main's copy of the tool, kept outside any checkout, forecasts against the main checkout.

    Everything here runs against a throwaway checkout that differs from this one in each thing it
    is read for: a task branch that claims 1.6b, a tracker guide naming @someone, the only account
    whose label counts, and a tools/roadmap.py of its own whose closed gate no real roadmap has. A
    tool that read any of the three from anywhere else would print other tickets."""

    PARTS = ForecastFailsClosed.PARTS
    ROAD = {**ROAD, "closed_gates": [{"gate": "G-test", "tag": "from-the-named-checkout"}]}

    def setUp(self):
        self.assertIn("$PWD", GH_IN_A_CHECKOUT, "the fake gh's log line was not replaced")
        scratch = tempfile.TemporaryDirectory(prefix="overnight-collect-repo-")
        self.addCleanup(scratch.cleanup)
        base = pathlib.Path(scratch.name).resolve()
        found = shutil.which("git")
        self.assertIsNotNone(found, "git is not on PATH, and the tool reads it")
        tools = base / "bin"
        tools.mkdir()
        (tools / "git").symlink_to(found)
        gh = tools / "gh"
        gh.write_text(GH_IN_A_CHECKOUT)
        gh.chmod(gh.stat().st_mode | stat.S_IXUSR)
        (base / "home").mkdir()
        config = base / "gitconfig"
        config.write_text("[user]\n\tname = Case\n\temail = case@example.invalid\n"
                          "[init]\n\tdefaultBranch = main\n"
                          "[gc]\n\tauto = 0\n[maintenance]\n\tauto = false\n")
        self.fixtures, self.log = base / "fixtures", base / "gh.log"
        self.fixtures.mkdir()
        self.env = {"PATH": f"{tools}:/bin", "HOME": str(base / "home"),
                    "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": str(config), "LC_ALL": "C",
                    "GH_FIXTURES": str(self.fixtures), "GH_LOG": str(self.log)}

        # The main checkout, with the tool in it as the in-repo copy.
        self.checkout = base / "checkout"
        self.checkout.mkdir()
        self.git("init", "-q")
        self.git("commit", "-q", "--allow-empty", "-m", "The first commit")
        self.git("branch", "task/1.6b-claimed-here")
        self.copy_parts(self.checkout)
        guide = self.checkout / collect.GUIDE
        guide.parent.mkdir(parents=True)
        guide.write_text("- **The human's account is `@someone`.** Only its label counts.\n")
        (self.checkout / "tools" / "roadmap.py").write_text(
            f"import json\nprint(json.dumps({self.ROAD!r}))\n")

        # The copy outside any checkout, and a directory outside any checkout to run it from.
        self.outside = base / "outside"
        self.copy_parts(self.outside)
        self.elsewhere = base / "elsewhere"
        self.elsewhere.mkdir()

        # #1 claimed by the checkout's branch; #2 ready, labelled by the account its guide names;
        # #3 waits in prose on #100, which only the graphql look-up says is open.
        agent = [issue(1, body("1.6b")), issue(2, body("1.7")),
                 issue(3, body() + "\nBlocked by #100.\n")]
        (self.fixtures / "ready-for-agent.json").write_text(json.dumps(agent))
        (self.fixtures / "ready-for-human.json").write_text("[]")
        (self.fixtures / "needs-triage.json").write_text("[]")
        for raw in agent:
            (self.fixtures / f"timeline-{raw['number']}.txt").write_text(
                json.dumps(labeled("ready-for-agent", "someone")) + "\n")
        (self.fixtures / "graphql.json").write_text(json.dumps(
            {"data": {"repository": {"n100": {"__typename": "Issue", "state": "OPEN"}}}}))

    def git(self, *args: str) -> str:
        done = subprocess.run(["git", *args], cwd=self.checkout, env=self.env,
                              capture_output=True, text=True, check=False)
        self.assertEqual(0, done.returncode, f"git {' '.join(args)}: {done.stderr}")
        return done.stdout.strip()

    def copy_parts(self, tree: pathlib.Path) -> None:
        for part in self.PARTS:
            (tree / part).parent.mkdir(parents=True, exist_ok=True)
            (tree / part).write_bytes((ROOT / part).read_bytes())

    def collect_from(self, tree: pathlib.Path, *argv: str) -> subprocess.CompletedProcess:
        """The copy of the tool in tree, run from a directory outside any checkout."""
        if self.log.exists():
            self.log.unlink()
        return subprocess.run(
            [sys.executable, "-I", str(tree / self.PARTS[0]), "--json", *argv],
            capture_output=True, text=True, cwd=self.elsewhere, check=False, env=self.env)

    def gh_directories(self) -> set[str]:
        return {line.split("\t", 1)[0] for line in self.log.read_text().splitlines()}

    def assert_the_checkouts_tickets(self, done: subprocess.CompletedProcess) -> None:
        self.assertEqual(0, done.returncode, done.stderr)
        answer = json.loads(done.stdout)
        self.assertEqual(self.ROAD["closed_gates"], answer["closedGates"], "roadmap read elsewhere")
        found = {t["issue"]: (t["status"], [f["rule"] for f in t["willEndPartial"]])
                 for t in answer["tickets"]}
        self.assertEqual({1: ("claimed", []), 2: ("ready-for-agent", []),
                          3: ("ready-for-agent", ["blocker-edge"])}, found)
        self.assertEqual({str(self.checkout)}, self.gh_directories(), "gh ran elsewhere")

    def test_repo_sends_every_read_to_the_checkout_it_names(self):
        """git's branches, every gh call (the listings, the timelines and the prose look-up), the
        roadmap and the tracker guide all come from DIR, though the tool and its caller are both
        outside it."""
        self.assert_the_checkouts_tickets(
            self.collect_from(self.outside, "--repo", str(self.checkout)))
        calls = [line.split("\t", 1)[1] for line in self.log.read_text().splitlines()]
        self.assertEqual(3, sum(call.startswith("issue list") for call in calls))
        self.assertEqual(3, sum("/timeline" in call for call in calls))
        self.assertEqual(1, sum(call.startswith("api graphql") for call in calls))

    def test_without_repo_the_tool_reads_its_own_checkout_as_before(self):
        """The in-repo copy, run with no --repo from outside its checkout, reads that checkout, and
        prints what naming the checkout prints."""
        plain = self.collect_from(self.checkout)
        self.assert_the_checkouts_tickets(plain)
        named = self.collect_from(self.checkout, "--repo", str(self.checkout))
        self.assertEqual(plain.stdout, named.stdout)

    def test_a_copy_outside_any_checkout_prints_what_the_in_repo_copy_prints(self):
        """The #349 launch: origin/main's copy, run from outside, against the main checkout."""
        inside = self.collect_from(self.checkout)
        outside = self.collect_from(self.outside, "--repo", str(self.checkout))
        self.assert_the_checkouts_tickets(outside)
        self.assertEqual(inside.stdout, outside.stdout)

    def test_a_directory_inside_the_checkout_names_the_checkout(self):
        """git finds the checkout from any directory in it, so the tool does as well."""
        done = self.collect_from(self.outside, "--repo", str(self.checkout / "docs" / "agents"))
        self.assert_the_checkouts_tickets(done)

    def test_a_repo_that_is_not_a_checkout_exits_1_naming_it(self):
        missing = self.elsewhere / "no-such-directory"
        # A path that is no directory is refused before git runs, naming why; a directory git
        # finds no working tree in is refused with git's own reason.
        for given, why in ((self.elsewhere, "not a git repository"),
                           (missing, "not a directory"),
                           (self.checkout / collect.GUIDE, "not a directory")):
            with self.subTest(given=given.name):
                done = self.collect_from(self.outside, "--repo", str(given))
                self.assertEqual((1, ""), (done.returncode, done.stdout))
                self.assertIn(f"--repo {given} is not a git checkout: ", done.stderr)
                self.assertIn(why, done.stderr)
                self.assertFalse(self.log.exists(), "gh was asked although --repo was refused")


if __name__ == "__main__":
    unittest.main(verbosity=1)
