#!/usr/bin/env python3
"""Tests for tools/ticket_shape.py. Owner: integrator.

Run: python3 -I tools/ticket_shape_tests.py

#247 asks for each rule to be proved against the bodies of #172, #204, #206, #209, #79 and #100 as
filed, and for #147 and #198 to pass. FILED below holds each of those bodies, with the labels, the
sub-issue count and the open blockers it carried then rather than now: #232's audit has since
reworded, relabelled and re-edged most of them, so the issue as it reads today would prove nothing.
Each entry says which revision it is. Two rules have no body among them to fail: none of the eight
carries a `**Touches:**` line, which #227 introduced after they were filed, and none names a blocker
in its text. Those two are proved against #204's and #209's bodies with the one line added that the
rule reads, and the case says so.

The read from GitHub goes through a fake `gh` on PATH, in an environment that inherits nothing, so
no case reaches a real repository.
"""
from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import re
import stat
import subprocess
import sys
import tempfile
import typing
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "ticket_shape.py"
WORKFLOW = ROOT / ".github" / "workflows" / "ticket-shape.yml"


def load(name: str, path: pathlib.Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


shape = load("ticket_shape", SCRIPT)


class Filed(typing.NamedTuple):
    labels: tuple[str, ...]
    sub_issues: int
    blockers: tuple[int, ...]   # the open edges
    body: str
    closed: tuple[int, ...] = ()   # the edges to a closed issue
    # What each number the prose waits for, with no edge, was then: an open or closed issue, a pull
    # request, or no issue (ticket_shape.KINDS).
    states: tuple[tuple[int, str], ...] = ()


FILED = {
    # #172: the revision of 2026-10-21T04:06:06Z, the first with an acceptance box, which #232 audited; labelled ready-for-agent at 02:52Z.
    172: Filed(
        labels=('ready-for-agent', 'wayfinder:task'), sub_issues=0, blockers=(),
        body="""\
## Question

Charting decided the harness and the floor artifact live under `tools/bench`, owned by workstream 6 (verification). `OWNERSHIP.md` is an integrator file, and `tools/` is integrator by the `*` rule, so the path needs a line under "6 Verification and operations" and `AGENTS.md`'s owner table needs the same.

Resolution: `contracts/changes/<id>-tools-bench-ownership.md` filed on a branch and pushed, following the existing change request's shape, naming this ticket and the map. The integrator decides and edits; this ticket closes when the request is filed, and the answer links it.

## Acceptance

- [ ] The change request giving workstream 6 the `tools/bench` path is filed under `contracts/changes/` on a pushed branch, in the existing change requests' shape, naming this ticket and the map (#169); the answer links it.
"""),
    # #204: as filed, 2026-10-21T02:35:54Z.
    204: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(),
        body="""\
**Raised by:** #58 (sitting C, stage C1, 2026-10-21)
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
"""),
    # #206: as filed, 2026-10-21T02:35:57Z.
    206: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(),
        body="""\
**Raised by:** #58 (sitting C, stage C1, 2026-10-21)
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
"""),
    # #209: as filed, 2026-10-21T02:36:00Z, before #232 added the edge to #235.
    209: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(),
        body="""\
**Raised by:** #63 (sitting C, stages C2 and C3, 2026-10-21)
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
"""),
    # #79: never edited; its five sub-issues #83 to #87 as of 2026-10-17T18:44Z, while it carried ready-for-agent.
    79: Filed(
        labels=('ready-for-agent',), sub_issues=5, blockers=(),
        body="""\
**Owner:** integrator
**Plan row:** none

## Problem Statement

Every pull request into `main` is supposed to be reviewed by a session other than the one that wrote it, with `/code-review`, and its findings answered before merge (the landing guide and the review guide both say so). Both guides then exempt the integrator's own work, which "merges on CI plus the human. That is the weakest link here, and it is known." Nothing enforces either rule today. The last time it mattered, four integrator pull requests merged ahead of, around, or without their reviews, and a gate tag froze sentences a still-running review was about to correct (archived overnight ticket 70, continued as #67).

The reviews that do happen vary in what they prove. The review guide's "The bar for a finding" counts it: of nine pull requests reviewed on 15 October 2026, the three that stated their own mutation runs reproduced exactly, and each of the five that stated none shipped an assertion that could not fail. The coding standards make the rule explicit ("A new invariant test names the mutation that made it fail", "A reviewer verifies before reporting", "Grep the noun, then check the scope"), but a review session dispatched by hand decides for itself whether to mutate, how to read the branch without touching the shared tree, and which spec sources to hold the diff to, and every session re-derives all three.

#67 (open, `ready-for-agent`) decides what enforces review: the landing script will refuse to merge a head that no review record clears. Its pre-accepted record is one fixed line, `Reviewed <40-hex head> against <40-hex base>: merge` (or `: hold`), on a line of its own in a pull request comment, written only by an agent session that ran `/code-review` and wrote none of the commits it covers. The latest line naming the current head decides; abbreviated shas match nothing; no pull request into `main` is exempt, tracker-only, generated and overnight-integration pull requests included; and a review covers exactly the head its record names, so commits pushed after it need their own review of the new commits. #67 builds the reader. Nothing writes the record except a person remembering to dispatch a session and that session remembering the line.

The cost shows on the most recent pull request. When this spec was briefed, PR #76 (tracker-only; Documents green, Build and Terraform skipped by design) was open with zero reviews and zero comments. By the time it was written, two hand-dispatched reviews had been posted on it: the first recorded its head as "Reviewed head: <sha>" in free prose and held on two findings; the second wrote the fixed line and cleared it; the landing script then merged it. Two sessions, two shapes of the same record, one pull request. There are zero open pull requests into `main` as this is filed, so the queue starts empty; the next one arrives with the same gap.

The overnight workflow's in-lane reviewer is the closest precedent and the wrong tool for this: it reviews a lane branch against its fork point, reuses the lane's build log, fixes findings itself and commits them as "Review: ...". It never sees a pull request into `main`, and the integration pull request it leaves for the morning carries merges and a roadmap refresh that no lane review covered (ticket 70, Q9).

## Solution

`review-queue` is a Workflow-tool script, a sibling of the overnight workflow, that an integrator session runs in the foreground against every open pull request into `main` (or a named list). For each pull request it:

1. **Inventories** the head, the base, the fork point, the changed files and which owner set they fall in, the CI state of the head, the plan row and issue the body cites, the mutation claims the body makes, any review record already on the pull request, and the candidate invariant tests the diff adds or changes.
2. **Reviews** it on the two axes the review guide fixes, Standards and Spec, as two fresh agents given the pointer to the review guide and the coding standards rather than a transcription, plus the spec sources table. CI has already run; nothing rebuilds the solution.
3. **Mutates** each candidate invariant test in a throwaway worktree outside the repository: break the thing the test guards, run only that test's project, record failed-of-total, discard the worktree. Each row ends `reproduced`, `did-not-fail` or `could-not-run` with why.
4. **Decides**, with one agent per pull request that re-takes every finding's severity itself, marks each finding verified or suspected and how it was checked, and posts **one** comment on the pull request: findings by axis, the mutations with their counts, which of the author's claims it verified, the remainder for the merge body's `Outstanding:` line, and the record line with the full head sha as its last line. `hold` when a verified finding shows the change does not do what it says or a test that proves it cannot fail; otherwise `merge`.

It never merges, pushes, tags, edits the branch, or writes in the main checkout outside the run's log. A head that already carries a record is skipped, so a re-run is idempotent; a head pushed after a record gets a review of the new commits only. It returns one JSON object per pull request, so the `morning` sibling and a report can consume it.

## User Stories

1. As the integrator, I want every open pull request into `main` reviewed by an agent session that wrote none of its commits, started with one command, so that the "CI plus the human" exception closes without a second GitHub account.
2. As the integrator, I want the verdict written on the pull request as one fixed line naming the full head sha, so that the landing script's reader (#67) can refuse an uncleared head without parsing prose.
3. As the integrator, I want a re-run to skip every head that already carries a record and say so in its return value, so that I can run it whenever I like without a second comment landing on the same head.
4. As the integrator, I want a head pushed after a record to get a review of only the commits since the recorded head, with the earlier record named, so that a fix round costs a fix round's review and not a fresh one.
5. As the author of a pull request, I want every finding marked verified or suspected with how it was checked, so that I know which to act on without looking.
6. As the author, I want the mutations my body states re-run and their counts compared with mine, so that my evidence is confirmed or contradicted rather than ignored.
7. As the author, I want the review to push nothing to my branch, so that its independence holds and my branch's history stays mine.
8. As the integrator, I want a `hold` whenever a verified finding shows the change does not do what it says or a test that proves it cannot fail, and a `merge` otherwise with the remainder listed, so that the merge body's `Outstanding:` line is written for me.
9. As the integrator, I want the diff's owner set checked and a cross-owner diff reported as a finding of its own, so that ownership is reviewed even when the push hook was bypassed.
10. As the integrator, I want the review to spend nothing on rebuilding the solution or re-running CI, so that its budget goes to what CI cannot see: whether an assertion could fail, whether a comment is true, whether the diff matches the row.
11. As the integrator, I want every read taken from origin's branch and never from a checkout, so that a dirty shared tree can report on neither the branch nor `main`.
12. As the integrator, I want anything that must build or run to happen in a throwaway worktree outside the repository that is removed afterwards, so that the shared tree is as it was when the run ends.
13. As the integrator, I want the number of agents a run spawns bounded by its arguments and reported, so that a wide queue does not run past the session's guideline unnoticed.
14. As the integrator, I want one structured result per pull request, so that the `morning` sibling can run the same flow on the overnight integration pull request and read what happened.
15. As the person about to cut a gate tag, I want every merge since the previous gate tag to have a record naming its head, so that the tag closes over no open review (#67's tag rule).
16. As a reader of the comment, I want it to say which of the pull request body's claims were verified and how, so that the body is treated as the author's claim and not as the spec.
17. As the `gate-evidence` sibling, I want the mutation agent's prompt, result schema and worktree protocol defined once and named, so that I can copy them rather than reinvent them.
18. As the `retro` sibling, I want every record in one grammar and every mutations table in one column order, so that verdicts and counts can be read across pull requests.
19. As an agent session reading the comment weeks later, I want each mutation row to name what was broken and the failed-of-total count, so that the hazard need not be re-derived to check the test.
20. As the integrator, I want a pull request with no candidate invariant test (docs-only, tracker-only, generated) to still get both axes and a verdict, with "no candidates" stated, so that no pull request is exempt by content.
21. As the integrator, I want a mutation that could not run to say why and never pass silently, so that the review does not repeat the "test that skips on the machine it runs on" shape it exists to catch.
22. As the integrator, I want the verdict agent to re-take the severity of every finding itself, so that a sub-agent's "looks wrong" does not hold a merge on what turns out to be the contract's own design.
23. As the integrator, I want to be able to re-review a held head after its findings were answered in discussion without a new commit, when I say so explicitly, so that the latest line on that head can become `merge`.
24. As the integrator, I want the run's log kept under the git-ignored scratch log path with every mutation's output file, so that the evidence survives the session and the checkout stays clean.
25. As a later auditor, I want the record to name the base the diff was read against, so that what was compared is on the record even though the reader ignores it.
26. As the integrator, I want a pull request whose head moves during the run to get a record naming the head that was actually read, and a flag in the return value, so that the record fails closed and I know to run again.
27. As the integrator, I want a run that cannot reach GitHub when it is time to post to keep the comment body in the log and say so, so that nothing is lost and nothing is posted twice when I retry.

## Implementation Decisions

**Where it lives and how it lands.** One self-contained Workflow-tool script beside the overnight workflow: a local file in Adam's checkout that git ignores, launched by its script path, not by name (a workflow launched by name runs a session snapshot). Like #68's edit to the overnight workflow, it is written in a foreground session in that checkout, never in an overnight lane: a lane cannot land a file git does not track, and this ticket changes no tracked file. Its done-ness is proved by the runs under Testing Decisions and recorded as this issue's Answer comment with their URLs. Its meta names four phases: Inventory, Review, Mutate, Verdict.

**Arguments.** A list of pull request numbers (default: every open pull request whose base is `main`); the main checkout, the worktree root outside the repository and the date, as the overnight workflow takes them; `maxParallel`, the agent slots (default 3, the overnight default); `maxMutationsPerPr`, the cap on mutation agents per pull request (default 3); and `skipRecorded` (default on). Agents per run: one queue agent when no list is given, then per pull request one inventory agent, two axis agents, up to the cap of mutation agents, and one verdict agent. With the defaults, one pull request with three candidates is seven agents; a wide queue is kept under the session's guideline by naming the pull requests and lowering the cap, and the return value states how many agents ran.

**House rules carried by every prompt**, the analogue of the overnight workflow's. The main checkout is read-only apart from the run's log directory and git's own bookkeeping for fetches and worktrees. Every `gh` command runs from the main checkout so it resolves this repository. The branch is read through origin with the review guide's commands: fetch with tags, the three-dot diff against the fork point, the commit list, a file at its branch version, a file as `main` has it now; never check the branch out, never reset the shared tree. Anything that must build or run does so in a throwaway worktree under the worktree root, detached at the pull request's head, and removes it afterwards. One test project at a time, never the solution. Never push, merge, tag, label, assign, or edit the pull request's body or branch; never start a process on a port that was not given. Never log a credential, token, cookie, code or state value; mutation output is kept as the test runner prints it, which N-16's canary rule already governs.

**Phase Inventory.** When no list is given, a queue agent lists the open pull requests whose base is `main`, with their numbers and heads. Then one inventory agent per pull request reads and judges nothing, returning: number, title, author, branch, the 40-hex head, the base branch's current tip, the fork point (the merge base of head and base), the commit list, the changed files; the owner-set result from the path-ownership check run between the base and the head (the review guide names that check as the decider; a diff outside one owner's set, or the check's refusal of a history it cannot audit, is handed to the Standards axis as a finding, as the review guide says it is); the CI state of the head per check name (success, failure, pending, skipped); the plan row, the issue numbers and the merge subject the body proposes; every mutation statement in the body, quoted with its counts; the candidate invariant tests: each xUnit fact or theory, Terraform test file and Python test companion the diff adds or changes, by project or directory and name, each with one sentence saying what it guards and which source it would take to break it; and the records already on the pull request: every line in the record grammar found in its comments and submitted reviews, with the head it names, its verdict, its URL, and whether that head is the current head or an ancestor of it. The last item decides two things before any review agent runs: with `skipRecorded` on, a current-head record of either verdict skips the pull request; a record naming an ancestor makes the review a delta review.

**Phase Review.** Two agents per pull request, in parallel: Standards and Spec, the axes the code-review skill defines, dispatched by the workflow as fresh agents the way that skill dispatches its own sub-agents, so each sees only its prompt. Each receives the inventory, the diff and log commands against origin, the pointer to the review guide and the coding standards (not a transcription of either), the CI state with the sentence "CI has already run", and the no-build rule: one test project in a throwaway worktree where a runtime claim must be run, never the solution. Standards holds the diff to the coding standards, the agent guides and the ownership result, carries the skill's smell baseline as judgement calls, and nominates the assertions it suspects cannot fail for the Mutate phase. Spec holds it to the spec sources table: the plan row; the issue's body and comments; the contracts and the pipeline table; the test ids and evidence ids the row names; change requests; ADRs. The pull request body is the author's claim: Spec lists each claim and says whether it verified it and how. Both return a list of findings, each with axis, title, where, what was read or run, verified or suspected, and an advisory severity the verdict re-takes; Standards adds its nominations, Spec its claim table; both say which sources they read. Whether a review run this way satisfies #67's "ran `/code-review`" wording is for #67's ADR; the comment says exactly what ran. In a delta review both axes are given the range since the recorded head to review and the whole diff for context; a merge of `main` into the branch is such a commit and its resolution is audited on its own, as the review guide requires.

**Phase Mutate.** The mutation list for a pull request is, in order: the author's stated mutations (to be reproduced as stated), the Standards axis's nominations, then the inventory's remaining candidates, deduplicated by test and cut at the cap; what the cap cut is named in the comment as not mutated. One agent per mutation: add a detached throwaway worktree at the head under the worktree root, named for the pull request and the mutation so parallel agents never collide; break the thing the test guards in the source it guards, never in the test, or apply the author's mutation as stated; run only that test's project (the .NET test project, the Terraform test directory, or the Python companion); record failed and total and the failing test names; discard the worktree and prune. The result is `reproduced` when the named test went red (with whether the counts matched a stated claim), `did-not-fail` when the project stayed green with the guarded thing broken, or `could-not-run` with why (the project does not build at the head, the tool is absent, the test was not found, the run timed out, the worktree could not be made). Each agent writes one output file in the run log whose first line names the head and whose last line is the result, the convention the overnight workflow's check logs set. A pull request's mutations start as soon as its two axes report, while another pull request is still reviewing: per pull request a pipeline of inventory, axes, mutations, verdict; across pull requests the pipelines overlap under the shared slots.

**Phase Verdict.** One agent per pull request reads the inventory, both axes and the mutation rows and takes every severity for itself; it may run one further targeted check in a throwaway worktree, never the solution. It marks each finding verified or suspected and how it was checked. The verdict is `hold` when a verified finding shows the change does not do what it says, or when a test that proves it cannot fail: a `did-not-fail` row on the thing the test claims to guard, or a stated mutation that does not reproduce. A `could-not-run` row on a stated or nominated mutation also holds, naming what could not run, because an unverifiable claim about an invariant test is the shape the review exists to refuse and a hold is lifted by one re-run; a `could-not-run` on a candidate nobody suspected is reported and does not hold. Otherwise `merge`, with everything else listed under a heading the merge body copies into its `Outstanding:` line. It posts one comment on the pull request, in this order: the head and base it reviewed, the delta range and earlier record when there is one, the CI state and the owner set; Standards findings; Spec findings with the author's claims and what verified each; the mutations as a table with fixed columns (test, what was broken, failed of total, result, log file); what the cap left unmutated; the remainder for `Outstanding:`; and, as the last line, on a line of its own, the record. Before posting it re-reads the head: if the head moved during the run it still posts, because the record names the head it read and matches nothing else, and sets a flag in the return value so the operator runs again. Before posting it also checks for a record naming this head written by this run, so a resumed run never posts twice. If GitHub cannot be reached, the comment body stays in the run log, the return value names the file, and nothing is retried silently.

**The record line.** `Reviewed <40-hex head> against <40-hex base>: merge` or `: hold`, where the base is the fork point the diff was read against, on a line of its own as the comment's last line. Its exact form is provisional until #67's ADR, and it lives in one place in the script: one expression produces it and one recognises it, and the inventory's detector and the verdict's writer both use that place, so a change decided in #67 is one edit. Abbreviated shas never match. A record from any writer counts for the skip, including a hand-written one such as the line on PR #76, and a `COMMENTED` review counts as a comment. When #67's reader module lands, the detector defers to it rather than keep a second grammar.

**Idempotence and the skip flag.** With `skipRecorded` on, a pull request whose current head carries any record is returned with `skippedBecauseRecorded` true and that record's URL, and no agent runs for it beyond the inventory. With it off, the head is reviewed again and a new record is posted on it; the latest line naming the head decides (#67's rule), which is how a `hold` answered in discussion without a new commit becomes a `merge`. That is the one way a head gets a second comment, it is the operator's explicit choice, and within one run a head gets one comment regardless.

**Delta review.** When the latest record names an ancestor of the current head, the axes review the commits since that head, mutations run for the candidates those commits touch, the comment names the earlier record and the range, and the new record names the current head. A held earlier record's findings are checked for an answer in the range.

**Return value.** Per pull request: number, head, base (the fork point the record names), verdict (`merge` or `hold`, empty when nothing was posted), outcome (`recorded`, `skipped`, `unposted` or `failed`, the last with the stage that failed), recordUrl, findings as two lists, verified and suspected, mutations as a list of rows (test, what it guards, the mutation, failed, total, result, why, log file), `skippedBecauseRecorded`, the earlier record when the review was a delta, and whether the head moved. Run-level: the date, the base tip the run saw, how many agents ran, and the log directory.

**Run log.** A directory under the git-ignored scratch log path, the pattern the ignore file already holds for the overnight run: per pull request the inventory, both axes, every mutation output file, the verdict's comment body as posted, and the run's return JSON. Apart from git's bookkeeping for fetches and worktrees, it is the only thing the run writes into the main checkout.

**Failure handling.** An agent that returns nothing fails that pull request at its stage, with no comment posted; the other pull requests continue. A mutation agent that dies is a `could-not-run` row and the verdict still runs. A queue of zero pull requests returns an empty list and posts nothing.

## Testing Decisions

**What a good test is here.** The workflow is judged only by what it leaves on GitHub and what it returns: the one comment, the record line, the verdict, the mutation rows, the count of comments on a head. Prompt wording and agent transcripts are not asserted on. Every proof names the outcome that would fail it, and the pair of a planted defect and a clean change is what makes the verdict an assertion that can fail (`CODING_STANDARDS.md`, "An assertion must be able to fail").

**The proofs**, each on a throwaway branch pushed to origin as a pull request into `main` that is named explicitly in the run's list, never merged, closed and its branch deleted afterwards (the comments stay readable on the closed pull request), with a single-owner diff so A-05 holds:

1. **The planted defect.** A pull request carrying one assertion that cannot fail, in one of the coding standards' three shapes (a rule asserted only against a tree that already satisfies it, for instance), with a body stating no mutation, as the five did. The run must say `hold`, and that test's mutation row must read `did-not-fail` with the thing that was broken named. A `merge`, or `reproduced` on that row, fails the proof.
2. **The clean change.** A pull request carrying a test that can fail and a body stating its mutation with counts, in the merge-body form ("... failed N of M"). The run must say `merge`, every stated mutation must read `reproduced` with matching counts, and no finding may be marked verified without saying how. A `hold` fails the proof.
3. **The same head twice.** A second run over the head from proof 2. The return must carry `skippedBecauseRecorded` true with the first record's URL, and the comment count on the pull request must be unchanged. A second comment fails the proof.
4. **The moved head.** One commit pushed to the branch from proof 2 after its record. The run must review the new commits only: the comment names the earlier record and the range, mutations run only for candidates that range touches, and the new record names the new head. A record naming the old head, or a full re-review, fails the proof.
5. **The mutation that cannot run.** Proof 1 re-run with `skipRecorded` off and a worktree root that cannot be created. Every mutation row must read `could-not-run` with why, none may read `did-not-fail` or `reproduced`, and the verdict must be `hold` naming what could not run. A row that passes silently fails the proof.

**Seams chosen** (the check-in is deferred; these are the implementer's to confirm):

- The inventory JSON per pull request: the seam between reading GitHub and git and judging anything; every later agent consumes it and nothing else about the pull request, so a proof can be replayed from it.
- The per-axis findings JSON: the seam between the axes and the verdict, across which severity is re-taken.
- The mutation row and its output file: the seam `gate-evidence` copies, one row per agent.
- The record line's one producer and one recogniser: the seam shared with #67's reader.
- The posted comment and the return value: the external surface every proof reads back through `gh`.

**Prior art.** The review guide's commands for reading a branch without the checkout; the "Review:" commits the overnight workflow's in-lane reviewer leaves; the merge bodies' "Reviewed by a second session with `/code-review`" paragraphs and their mutation sentences with counts ("... failed 22 of 177"); the overnight workflow's REVIEW schema and its check-log convention (`HEAD=` first line, `GATE: GREEN|RED` last line) as run evidence; the two hand-written reviews on PR #76 as the before and after of the record's shape; and, for any script-side helper, the Python companions in `.github/*_tests.py` and `tools/roadmap_tests.py`, which test their check on trees built to trip it. The workflow itself has no companion in the tree, because the file is not in the tree; the record grammar's tests belong to #67's module.

## Out of Scope

- The five sibling workflows: `morning` (which runs this flow on the overnight integration pull request), `truth-audit`, `gate-evidence` (which reuses the Mutate agent's design), `retro` (which reads these records) and `pins-sweep`.
- Anything that merges, tags a gate, pushes to `main` outside the sanctioned landing script, or applies AWS or touches credentials.
- The reader side of the record: the landing script's refusal, the pinned merge, the recorded escape, the merge-body trailer, the gate-tag rule, the ADR and the glossary terms. All of it is #67.
- Fixing findings on the author's branch. The overnight workflow's in-lane reviewer does that; this one reports and records.
- Pull requests whose base is not `main`, closed or merged pull requests, and the retroactive reviews of the merges archived overnight ticket 70 names.
- Re-running CI, the solution build or the smoke test.
- Edits to the overnight workflow, including its morning step calling this flow.
- Branch protection, a second GitHub identity, and reviewing the human's decision to land.
- A comment on the issue the pull request cites.

## Further Notes

**Glossary gaps.** None of these has an entry: Review, Review record, Open review, Agent session and Gate tag (proposed in archived overnight ticket 70 and carried by #67); Verdict (`merge` or `hold`); Finding, and the verified/suspected distinction the review guide draws; Axis (Standards, Spec); Mutation, which the coding standards use without defining; Candidate invariant test; Delta review; and the fork point, which the overnight workflow calls the fork point, the code-review skill the fixed point and the review guide the merge base (PR #76's reviews wrote "Fixed point"). This spec says fork point. The overnight workflow's build-and-suite log is called a gate log; `Gate` is reserved for the G-gates, so this spec says check log and calls nothing of its own a gate.

**ADR conflicts, flagged not overridden.** ADR-0020 decision 3 puts "the reviewer's findings" among the comments on the issue; archived overnight ticket 70 (Q7) and #67 put the review record on the pull request, and this spec posts there only. If a pointer comment on the cited issue is wanted, #67's ADR is the place to say so. The record line's form, and whether a two-axis review dispatched by a workflow satisfies the "ran `/code-review`" wording, are #67's decisions; the line is held in one place so that either answer is one edit.

**Decisions taken beyond the brief**, for the implementer to confirm: the base in the record is the fork point; a `could-not-run` on a stated or nominated mutation holds; `skipRecorded` off is the one way a head gets a second record, so that an answered hold can be cleared on the same head; one inventory agent per pull request, plus a queue agent only when no list is given.

**Dependencies.** On no sibling. #67 fixes the line and builds the reader; until it lands the line is provisional. #66's script companions and #68's single local check are not needed: mutations run one project. The run shape assumes the Workflow tool's pipeline pattern for overlapping per-pull-request chains.

**What the siblings reuse.** `morning` runs this flow by pull request number on the overnight integration pull request; `gate-evidence` copies the Mutate agent's prompt, result schema and worktree protocol; `retro` reads the record line and the mutations table, whose column order this spec fixes.

**For the overnight workflow's collect step.** This issue carries `ready-for-agent` and `**Plan row:** none`. The collect step reads a Plan row line as the ticket's id and treats every `ready-for-agent` issue as tonight's work, so it would pick this spec up and hand it to an integrator lane that cannot land a git-ignored file. Until that step learns to leave foreground-only specs alone, run the overnight workflow with this issue in its skip list.

**Evidence that moved while this was written.** The brief found PR #76 open with zero reviews and zero comments; it now carries two hand-written reviews and is merged, and there are zero open pull requests into `main`. The problem is unchanged; the example is now the before and after.
"""),
    # #100: as filed, 2026-10-18T18:14:56Z, before #232 added the edge to #135.
    100: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(),
        body="""\
**Owner:** integrator  
**Plan row:** none  
**Session:** foreground

## Parent

#99

## What to build

A ticket stays open until its work is on main. The overnight run's lanes stop closing issues: a complete ticket stays open and assigned, which collect already reads as claimed and GitHub's blocked-by count already treats as an open blocker, and the landing pull request's description closes it with a `Closes #n` line when Adam lands.

Evidence (retrospective item 1): the lanes of 2026-10-18 ran `gh issue close` on #65 and #74 (04:38Z, both by #65's lane), #66 (05:13Z), #72 (09:07Z) and #73 (09:31Z) while their work sat on the unpushed `overnight/2026-10-18`. Once #66 closed, #67, #96 and #97 read as unblocked, though they build on `tools/land_tests.py`, which main lacks; #96 and #97, unassigned, were open to any agent. The reviews of #65, #66 and #74 flagged it and deferred it as the workflow's call, and it became R1 on #98. GLOSSARY's *Spec*, and *Ticket* once ADR-0022's branch lands, say a ticket closes when its work lands.

Decided in the grill (Q1, Q9): closing keywords in the landing pull request's description, because `tools/land.sh` merges through `gh pr merge --merge`, and GitHub closes a linked issue when the pull request merges into the default branch. Rejected: a keyword in the integration merge commit, which is fixed in history before Adam reviews it, and closing by hand after landing, today's fallback, which is easy to forget.

The work is in `.claude/workflows/overnight.js`, which is git-ignored, so it is done in a foreground session.

## Acceptance criteria

- [ ] The implement step never closes an issue. A complete ticket's `## Answer` says where each part went (branch and sha) and that the ticket closes when that lands, and the ticket stays open and assigned.
- [ ] The report writes the landing pull request's body into its morning steps: one `Closes #n` line per ticket whose work is complete on the branch, and `Ticket: #n` per partial one.
- [ ] The review and integrate prompts lose their "reopen the issue first if it is closed" steps, which no longer arise.
- [ ] The morning step "close the issue of every landed row that is still open" stays, as the fallback.
- [ ] The next overnight run closes no issue, and its report's landing body carries a `Closes` line for every complete ticket.
"""),
    # #147: never edited; blocked by #145 since it was filed.
    147: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(145,),
        body="""\
**Owner:** 4 API and security
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
"""),
    # #198: never edited; blocked by #193 since it was filed.
    198: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(193,),
        body="""\
**Owner:** 1 AWS and IaC
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
"""),
}


# The bodies #284 names, each at the revision #232's second pass (2026-10-22) read, with the labels,
# the edges and the state of each number its prose names as it stood then. #213 is the one the Action
# run of 2026-10-22T01:14:41Z failed; the others were open in the audit's set.
AUDITED = {
    # #213: the revision of 2026-10-22T01:14:37Z, which the Action run of 01:14:41Z failed for
    # "#214", a merged pull request; ready-for-agent then, its one edge to #135, closed at
    # 2026-10-21T18:33:41Z.
    213: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(), closed=(135,),
        states=((214, 'PULL_REQUEST'),),
        body="""\
**Raised by:** #135 (Q11, confirmed on #185 on 2026-10-21)
**Owner:** integrator

## What to build

#135 left two things outside review on purpose, so that the script could land sooner (ADR-0026). The Q numbers below are #226's, confirmed in https://github.com/adammedford/spa-platform/issues/226#issuecomment-6319841927. The reasoning is in this ticket's "## Grilling record" comment.

1. **The test companion.** `.claude/workflows/overnight_harness.mjs` runs the overnight script end to end with every agent mocked, on a fixed ticket set under `.claude/workflows/fixtures/`, and checks how the lanes schedule. It is the script's only behavioural proof, and it is git-ignored.
   - It is tracked beside the script as `.claude/workflows/overnight_tests.mjs`. It stays `.mjs`, and it is called the overnight script's test companion, not "the harness" (Q27).
   - Its fixture `fixtures/2026-10-20-tickets.json` is tracked as it is, home paths included. The fixture argument is kept so that later fixtures can sit beside it (Q28).
   - The Documents job runs `node .claude/workflows/overnight_tests.mjs` on every push, with no path condition, right after "Every workflow script parses" (Q29). It lands wall-clock timed as it is, and a virtual clock comes only if it ever flakes (Q30).
   - `tools/check.sh` stays unchanged. CI on the landing PR is what proves a scheduler edit before its launch, under ADR-0026 decision 3 (Q32).
2. **The project skills.** `.claude/skills/` holds `wiz` (the sitting skill) and is ignored, so its three known defects (#135's comment of 2026-10-21) have no reviewable home.
   - Project skills are tracked under the workflow scripts' rule: every change is a branch, a review and a landing (Q26).
   - A project skill stays off the steering-document list. Like a workflow script under ADR-0026 decision 2, it is integrator code whose authority is its ticket and the landing review (Q37).
   - No sitting runs a skill edit that has not landed. A defect found mid-sitting stops the sitting at that stage until its fix lands (Q38).
   - The user-level skills under `~/.agents/skills` stay outside the repository and its review, and nothing is vendored (Q33).
   - The skill stays `wiz` (Q39).
   - The note on cached ticket expectations (A3, A4, B2) and W-4's tested redaction script are follow-up tickets. They are not part of this landing (Q40, Q41).

`.gitignore` adds `!.claude/workflows/overnight_tests.mjs`, `!.claude/workflows/fixtures/` and `!.claude/skills/` after #214's lines. It keeps the re-ignore of the workflows folder's other contents, and the comment above the lines is reworded (Q36).

## How the work splits

1. **Adam runs `.scratch/sequence/unblock.sh` on his Mac.** The script is on branch `claude/eager-carson-hoy367`. It creates `task/issue-213-track-companion-and-skills` from origin/main and pushes it with one commit (Q42). That commit holds:
   - the live bytes of `overnight_harness.mjs`, renamed `overnight_tests.mjs`;
   - the live bytes of `fixtures/2026-10-20-tickets.json`;
   - the live bytes of `.claude/skills/wiz/SKILL.md`;
   - Q36's three `.gitignore` lines.
2. **Agent work on that branch** builds the rest, each part as its own commit after the live-bytes commit: the header rename, the comment above the ignore lines, the three `wiz` corrections, the CI step, the ADR, the glossary entry, and the draft's removal and the repointed citations.

**Adam's landing step, before the fast-forward.** Before `git merge --ff-only origin/main` in the main checkout, Adam compares three live files against the live-bytes commit's blobs with `cmp`, and refuses the fast-forward on any difference (Q42):
- `overnight_harness.mjs` against `overnight_tests.mjs`;
- the fixture;
- `skills/wiz/SKILL.md`.

After the fast-forward, he removes `.claude/workflows/overnight_harness.mjs`. `unblock.sh` prints these commands with the commit's sha.

## Acceptance criteria

- [x] The live-bytes commit is pushed on `task/issue-213-track-companion-and-skills` by `unblock.sh`
- [x] The companion's header names it the overnight script's test companion, tracked. It no longer calls it the harness or says it is git-ignored (Q27).
- [x] The comment above Q36's `.gitignore` lines names the companion, its fixtures and the skills (Q36).
- [x] The Documents job runs `node .claude/workflows/overnight_tests.mjs` on every push, with no path condition, right after "Every workflow script parses" (Q29). The landing PR body records two runs:
  - one push running it 20 times in a loop (Q30);
  - the companion exiting 1 on a throwaway copy of the script with the ready-first pick reverted, with no test of the companion added (Q31).
- [x] The `wiz` skill lands with the three corrections noted on #135, each as its own commit after the live-bytes commit (Q40).
- [x] A decision on `.claude/skills/` is recorded, and `.gitignore` says it. The ADR comes from the draft in https://github.com/adammedford/spa-platform/issues/213#issuecomment-6308043619 (Q33, Q37, Q43):
  - it is a new ADR amending ADR-0026 decision 1;
  - ADR-0026's status line records the amendment;
  - the session that commits it numbers it.
- [x] GLOSSARY.md gains *Project skill* as proposed in that comment, with no change to *Harness* or *Test companion* (Q44).
- [x] The landing removes the draft `.scratch/sitting/wizard-SKILL.md` with `git rm`, and `.scratch/sitting/spec.md`'s two citations of it point to `.claude/skills/wiz/SKILL.md` (Q39).
"""),
    # #255: as filed, 2026-10-21T18:34:22Z, and unedited since: ready-for-human, "Blocked by: #83"
    # with no edge, and #83 closed.
    255: Filed(
        labels=('ready-for-human',), sub_issues=0, blockers=(), states=((83, 'CLOSED'),),
        body="""\
**Owner:** integrator, run by the human
**Touches:** none (runs the script against throwaway PRs)
Blocked by: #83

## Parent

Part of #79, the `review-queue` spec. The proof half of #83 (ticket 1 of 5, the walking skeleton), split out by #232 Q15: these boxes need a pushed throwaway pull request and a real GitHub round-trip, which a lane cannot make.

## Acceptance criteria

- [ ] The spec's proof 2 without mutations: a throwaway pull request carrying a test that can fail, with a single-owner diff, gets `merge`, one comment, the record line last, and every verified finding says how it was checked.
- [ ] The spec's proof 3: a second run on the same head posts nothing, the return carries skipped-because-recorded with the first record's URL, and the comment count is unchanged.
- [ ] The proof pull request is closed and its branch deleted afterwards; its URL is in this ticket's Answer comment.
"""),
    # #239: the revision of 2026-10-22T03:24:28Z, box 2 open, ready-for-agent until 03:24:38Z.
    239: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(),
        body="""\
**Raised by:** #59
**Owner:** workstream 1 (AWS and IaC)
**Touches:** `infra/modules/cell/tests/module.tftest.hcl`

## What to build

#59's review, "Found, not fixed: outside row 1.9": an `override_policy_documents` entry on `ecs_tasks_trust` letting any principal assume both roles passed 43 of 43 at `cc42e2d`, and the execution role's document has the same shape. P-03's task-role run has the null check; 1.4a's trust and execution runs (`module.tftest.hcl` lines 55 and 63) do not.

## Acceptance criteria

- [x] The runs over `data.aws_iam_policy_document.ecs_tasks_trust` and `data.aws_iam_policy_document.task_execution` assert `override_policy_documents` and `source_policy_documents` are null, as P-03's task-role run does
- [ ] Planting an `override_policy_documents` entry that lets any principal assume the role fails that run, and the merge body names the count

## Disclosed impediments

- None known. `terraform test` with mock providers runs in the full check and CI.
"""),
    # #122: the revision of 2026-10-19T08:16:32Z, box 3 open, which #232's second pass read under
    # ready-for-agent.
    122: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(),
        body="""\
**Owner:** ws1-infra
**Plan row:** none
**Raised by:** #92, from its grilling of 2026-10-18 (Q20, Q21, Q25–Q30 and Q32 on #98, confirmed on 2026-10-19: https://github.com/adammedford/spa-platform/issues/98#issuecomment-6257481813). It blocks #92: the row's two refusals are proven by this script's run in the sitting, and the merge that lands it, `Merge task 1.5: …`, carries the run's output (Q30).

## What to build

`tools/scripts/verify-release-immutability.sh`, beside `verify-tunnel.sh` (Q21). It reads `bucket` from the platform root's output, runs as the operator's own session (the `spa-platform` profile, the principal spactl writes as) and refuses to start as the teardown role (Q20, Q25). It writes one probe key under an appId no registry entry names, `apps/p04-probe/releases/<UTC timestamp>/index.html`, fresh on every run (Q28), and asserts, one output line per case, exiting non-zero when any case is not refused as expected (Q32):

- a first PUT with `If-None-Match: *` succeeds; a second PUT with that header gets 412;
- a PUT without the header gets 403; a PUT with only `If-Match` gets 403;
- a DELETE with no version id gets 403; a DELETE of the version gets 403;
- the key still has exactly one version and no delete marker;
- after the delete attempts, a PUT with `If-None-Match: *` still gets 412 (Q25);
- a CopyObject onto the key gets 403; a multipart upload completed without `If-None-Match` gets 403, and the upload is aborted (Q26);
- read-backs: versioning enabled, all four public-access-block settings on, a policy status that is not public, no lifecycle or replication configuration, one plain-HTTP request refused, and both image repositories `IMMUTABLE` (Q27).

Behind a flag that is off by default: assume the teardown role in a separate session, assert that the role cannot write, then delete the probe key's versions (Q29, Q32). The policy: `contracts/s3-layout.md` → Rules; ADR-0014; test id P-04 in `docs/plan/04-tests.md`. #63 runs this script as P-04 at G3 (Q32).

The script deletes objects through a break-glass role, so its pull request is reviewed and then held open: Adam runs it in the sitting against the applied bucket and lands it with `tools/land.sh` as `Merge task 1.5: …` with the run's output in the body (Q30). The lane proves what it can without AWS: `bash -n`, the case list and exit codes against a stub, and that the role check refuses.

## Acceptance

- [x] The script exists with the cases above, one line per case, a non-zero exit on any unexpected result, and refuses to start as the teardown role
- [x] The teardown-role cleanup is behind a flag that is off by default
- [ ] Run in the sitting against the applied bucket: every case as expected, with the output in the landing merge's body
"""),
    # #204: the revision of 2026-10-21T17:44:57Z, which #232's second pass read: the Owner line
    # still names workstream 5 beside the integrator.
    204: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(),
        body="""\
**Raised by:** #58 (sitting C, stage C1, 2026-10-21)
**Owner:** integrator (root files, `src/Gateway/Dockerfile`), with workstream 5 for `fixtures/MockApi/Dockerfile`

## What to build

A workstation builds and runs the gateway and mock API images the way CI does, and the files the scripts write never reach a commit or a builder.

Tonight's first deployment hit two defects that a clean CI checkout can't show. The image build copied the workstation's own build output over the image's `linux-arm64` restore, so `dotnet publish --no-restore` failed with `NETSDK1047`; and because no `.dockerignore` exists, every build also sent the local certificate authority's private key, the scratch directory and the git-ignored Terraform variables to the builder. Then, once the images built, the gateway died on its first line: `UnauthorizedAccessException` on `appsettings.json`, because the files were owner-only on the workstation, `COPY` and `publish` keep that mode, and the final stage runs as `$APP_UID`.

The scripts also write the infrastructure outputs document into the tree, and git doesn't ignore it. It carries the account id and both apps' client ids, so one `git add .` would commit it.

## Acceptance criteria

- [x] A root `.dockerignore` keeps build output, the local certificate authority, the scratch directory, `.git/`, Terraform's working directories, state and variable files out of the build context
- [x] The infrastructure outputs document the scripts write is git-ignored
- [ ] Both Dockerfiles make the published output readable by the runtime user whatever the build context's file modes were
- [x] From a tree that has host build output and an owner-only `appsettings.json`, the gateway image builds, and a container started as its own user gets past `WebApplication.CreateBuilder`

## Blocked by

None (can start immediately).
"""),
}


# The two bodies #295 names, as they stood on 2026-10-22, when #145 built its half and failed the
# whole-solution build because only #146's commit removes the CS0104 clash it creates. Neither was
# edited after 2026-10-19T20:16:48Z. Both were ready-for-agent; #145's one edge, to #143, was closed
# (2026-10-20T13:37:26Z), and #146's edges were #145, open, and #143.
PAIRED = {
    # #145: the revision of 2026-10-19T20:16:48Z. Box 6 asks that its pull request carry #146's
    # commit.
    145: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(), closed=(143,),
        body="""\
**Owner:** integrator
**Plan row:** none

## Parent

Part of #140, the `stage-host` spec. Ticket 2 of 10: the expand.

## What to build

A test host is started on `StageHost` over a real socket and keeps only what is its own. `Platform.Testing` gains three public modules. `StageHost.StartAsync(StageHostOptions, CancellationToken)` takes a `Services` delegate over the builder, a required `Pipeline` delegate over the built application and `TelemetryStandIn`, false by default, and returns a started host with `BaseAddress`, `Services`, `Log`, `Client`, `RawAsync` in two forms, `TelemetryRules`, `LastTelemetry` and `DisposeAsync`. The construction order is part of the interface: slim builder in the `Production` environment; a loopback URL on port zero; every logger provider cleared; the floor set to Trace; the `Services` delegate; the capture registered; `Build()`; the stand-in if asked, outermost as `UsePlatformTelemetry` is, recording each request's feature before the next middleware runs; the `Pipeline` delegate; start; the address read. The module calls no `Add*`, `Use*` or `Map*` entry point; composing them is the test host's job. A start that yields no address throws `InvalidOperationException`; a server that closes without answering a raw request throws `InvalidDataException`; a connect after dispose fails.

`CapturedLog` is every line a host logged as `CapturedLine` records with `Category`, `Level`, `Message`, `Exception` and `State`, `Text` joining message and exception, `Lines` as a snapshot, `LinesFor(rule, level?)` reading the `Rule` state property, `Contains(value)` over `Text`, and `Rendered` and `Json` present only when a console formatter is configured, resolved on first use by its configured name and never by type. For a caller with no host it offers `Provider`, `ProviderFor(services)` and `CreateLogger<T>()`. `RawHttp` keeps the gateway test harness's endpoint-taking forms and returns the one `RawResponse`: `StatusCode`, `StatusLine`, `Headers` in order with repeats kept, `Body` as the bytes on the wire, `BodyText` de-chunked, `Values(name)` and `Header(name)`; `StageHost.RawAsync` is that sender bound to `BaseAddress`. `Client` is one `HttpClient` with the base address set, no cookie container and no automatic redirect. `EchoUpstreamListener` is rebuilt on the stage host's private loopback seam, which has two callers inside `Platform.Testing` and no public name.

The ticket also carries the steering edits the change request (#143) asked for, citing #140's acceptance as their authority: the test doubles row of `contracts/README.md`, the project file's description, and the six glossary entries from #140's Further Notes (test host, stage host, telemetry stand-in, captured log, terminal, pin). No new package or project. The pull request also carries the gateway test harness's adoption commit (#146), because two composed suites import both namespaces and would stop compiling the moment `Platform.Testing` gains `RawHttp` and `RawResponse`.

## Acceptance criteria

- [ ] `StageHost`, `CapturedLog` and `RawHttp` exist in `Platform.Testing` with the interface above, and `Platform.Testing` references `Platform.Abstractions` and the framework only.
- [ ] `tests/Platform.Testing.Tests` proves the modules over a real socket with the thirteen tests of #140's Testing Decisions, each named for the mutation that fails it, and the merge body carries each mutation and its count: stand-in off by default; stand-in before the `Pipeline` delegate; feature recorded while the request is in flight; every line captured at Trace with exception text in `Text`; `LinesFor` finds a "refused under {Rule}" template; rendering by the configured formatter's name among two registered; capture survives a `Services` delegate that clears providers; no formatter means records but empty `Rendered`; the request line reaches the terminal as written for a dot-segment target; two `Set-Cookie` values come back as two; `BodyText` de-chunked and `Body` not; `Client` keeps no cookie and follows no redirect; a connect after dispose fails.
- [ ] `EchoUpstreamListener` starts on the private loopback seam and its existing tests pass unchanged.
- [ ] The test doubles row of `contracts/README.md`, the project file's description and `GLOSSARY.md` carry the widened charter and the six entries, each edit citing #140's acceptance as its authority.
- [ ] The merge body states that the reset-after-answer tolerance in the raw sender is kept and deliberately untested, and why.
- [ ] The pull request carries #146's workstream-6 commit, and `dotnet build` and the whole test solution are green on its head.

"""),
    # #146: as filed at 2026-10-19T20:15:13Z, never edited. Box 4 asks that its commit sit on #145's
    # pull request; box 5 only cites #144's overload.
    146: Filed(
        labels=('ready-for-agent',), sub_issues=0, blockers=(145,), closed=(143,),
        body="""\
**Owner:** 6 Verification and operations
**Plan row:** none

## Parent

Part of #140, the `stage-host` spec. Ticket 3 of 10: the gateway test harness and the composed suites adopt the shared modules. This is a workstream-6 commit on the expand ticket's (#145) pull request, not a pull request of its own: it cannot start before that branch exists, and the two land together.

## What to build

The composed suites run their never-logged searches and raw sends on `Platform.Testing`'s `CapturedLog` and `RawHttp`, and the gateway test harness's own three copies are gone. Two composed security suites import both the harness namespace and `Platform.Testing` and name `RawHttp` and `RawResponse`, so the moment the expand lands those names they fail to compile on an ambiguous reference unless the harness's copies leave in the same pull request. The harness registers the shared capture after its own services are configured and resolves the console formatter by its configured name, as it does today; the composed N-16 reads `Rendered` and `Json`. The de-chunking helper that existed only because the harness's sender refused to de-chunk retires, its callers reading `BodyText`. The harness's endpoint-taking raw sends keep working with the gateway's address and no stage host. The contract reader stays in the harness; it is #139's. If the test identity provider's `AddClient` overload (#144) has landed, the harness registers its client through it; otherwise the merge body carries an `Outstanding:` line naming that adoption.

A two-owner pull request with one commit per owner is the shape ADR-0018 used, and the ownership check audits each commit within one owner's set.

## Acceptance criteria

- [ ] The gateway test harness's `CapturedLog`, `RawHttp` and the composed suites' `RawBody` are deleted, and every caller in the harness, the composed suites and the end-to-end tests uses `Platform.Testing`'s.
- [ ] The composed suites that assert repeated headers and parse chunked bodies pass unchanged in behaviour, reading `Values`, `Header` and `BodyText`.
- [ ] The composed N-16 canary search reads the rendered lines through the formatter the harness configures by name, and the harness boot tests pass.
- [ ] The commit touches only workstream 6's paths and sits on #145's pull request, whose head is green.
- [ ] The harness registers its client through #144's overload, or the merge body carries an `Outstanding:` line saying so.
"""),
}


def raw(number: int, filed: Filed, body: str | None = None) -> dict:
    """The issue as `gh issue view --json number,body,labels,subIssuesSummary,blockedBy` gives it."""
    return {
        "number": number,
        "body": filed.body if body is None else body,
        "labels": [{"name": name} for name in filed.labels],
        "subIssuesSummary": {"total": filed.sub_issues, "completed": 0, "percentCompleted": 0},
        "blockedBy": {"nodes": [{"number": n, "state": "OPEN"} for n in filed.blockers]
                      + [{"number": n, "state": "CLOSED"} for n in filed.closed],
                      "totalCount": len(filed.blockers) + len(filed.closed)},
    }


def judged(number: int, body: str | None = None, table: dict | None = None, **changes):
    """The failures and the stale-prose notes of an issue in FILED or AUDITED, the numbers its prose
    names looked up in its own states, never through gh."""
    filed = (table or FILED)[number]._replace(**changes)
    issue = shape.issue_of(raw(number, filed, body))
    states = dict(filed.states)
    return shape.judge(issue, states=states), shape.stale_prose(issue, states)


def rules_broken(number: int, body: str | None = None, **changes) -> list[str]:
    return [failure.rule for failure in judged(number, body, **changes)[0]]


def refuse_gh(numbers):
    raise AssertionError(f"a test asked GitHub for {sorted(numbers)}; give the case its states")


def setUpModule():
    # Every in-process case states the numbers its prose names; a lookup left to gh is a test bug.
    setUpModule.kept, shape.look_up = shape.look_up, refuse_gh


def tearDownModule():
    shape.look_up = setUpModule.kept


def with_line(body: str, after: str, line: str) -> str:
    """The body with one line added after the first line starting with `after`."""
    lines = body.split("\n")
    at = next(i for i, text in enumerate(lines) if text.startswith(after))
    return "\n".join(lines[:at + 1] + [line] + lines[at + 1:])


class OneOwner(unittest.TestCase):
    """Rule 1 and rule 5: the owner is derived from the Touches paths, and there is one."""

    def test_touches_paths_of_two_owners_fail(self):
        # #204 as filed, with the Touches line its Owner line's two paths make.
        body = with_line(FILED[204].body, "**Owner:**",
                         "**Touches:** `src/Gateway/Dockerfile`, `fixtures/MockApi/Dockerfile`")
        self.assertIn("one-owner", rules_broken(204, body))


# Touches lines as the build tickets of 2026-10-21 write them, bare and with prose: #269's and
# #260's verbatim, #261's in a code span. Rule 5 on #227 asks for the paths, not for backticks.
TOUCHES_269 = ("**Touches:** .claude/workflows/pins-sweep.js, tools/pins_inventory.py, "
               "tools/pins_inventory_tests.py, .github/workflows/ci.yml; and, if a script test "
               "companion is added, .claude/workflows/pins-sweep_tests.mjs and .gitignore")
TOUCHES_260 = ("**Touches:** .claude/workflows/retro.js, tools/retro_record.py, "
               "tools/retro_record_tests.py, .github/workflows/ci.yml; and, if a script test "
               "companion is added, .claude/workflows/retro_tests.mjs and .gitignore")
TOUCHES_261 = "**Touches:** `.claude/skills/wiz/SKILL.md`"


def build_ticket(touches_line: str, owner: str = "integrator") -> str:
    return (f"**Raised by:** #213 (confirmed)\n**Owner:** {owner}\n{touches_line}\n\n"
            "## What to build\n\nA script.\n\n## Acceptance\n\n- [ ] Its tests pass.\n")


class BareTouches(unittest.TestCase):
    """Rule 5 derives the owner from the Touches paths however they are written: bare, as #260 and
    #269 write them among prose, or in code spans as #261 does."""

    def broken(self, body: str) -> list[str]:
        return rules_broken(204, body, labels=("ready-for-agent",))

    def test_bare_paths_read_as_paths(self):
        self.assertEqual(shape.touches(build_ticket(TOUCHES_269)), [
            ".claude/workflows/pins-sweep.js", "tools/pins_inventory.py",
            "tools/pins_inventory_tests.py", ".github/workflows/ci.yml",
            ".claude/workflows/pins-sweep_tests.mjs", ".gitignore"])

    def test_bare_paths_of_two_owners_fail(self):
        body = build_ticket("**Touches:** src/Platform.Identity/, src/Platform.Registry/")
        self.assertIn("one-owner", self.broken(body))

    def test_bare_and_spanned_paths_of_two_owners_fail(self):
        body = build_ticket("**Touches:** `src/Gateway/Dockerfile`, fixtures/MockApi/Dockerfile")
        self.assertIn("one-owner", self.broken(body))

    def test_bare_paths_of_one_owner_pass(self):
        for line in (TOUCHES_269, TOUCHES_260):
            with self.subTest(line=line[:50]):
                self.assertEqual(self.broken(build_ticket(line)), [])

    def test_bare_paths_owned_by_other_than_the_owner_line_fail(self):
        body = build_ticket("**Touches:** src/Platform.Identity/, tests/Platform.Identity.Tests/")
        self.assertIn("owner-line", self.broken(body))
        body = build_ticket("**Touches:** src/Platform.Identity/, tests/Platform.Identity.Tests/",
                            owner="workstream 3")
        self.assertEqual(self.broken(body), [])

    def test_spanned_paths_are_read_as_before(self):
        self.assertEqual(shape.touches(build_ticket(TOUCHES_261)), [".claude/skills/wiz/SKILL.md"])
        self.assertEqual(self.broken(build_ticket(TOUCHES_261)), [])

    def test_a_touches_line_with_no_readable_path_fails(self):
        # A check fails closed: a Touches line read as absent would judge only the Owner line.
        for line in ("**Touches:** to be decided", "**Touches:**", "**Touches:** see #213"):
            with self.subTest(line=line):
                self.assertIn("touches", self.broken(build_ticket(line)))

    def test_an_owned_root_directory_beside_a_file_fails(self):
        # `infra` and `fixtures` have no slash or dot, and are workstream 1's and 5's whole
        # directories; read as prose, the integrator's file beside them would pass alone.
        for line in ("**Touches:** infra, tools/check.sh", "**Touches:** fixtures, tools/check.sh",
                     "**Touches:** tools/check.sh and infra"):
            with self.subTest(line=line):
                self.assertIn("one-owner", self.broken(build_ticket(line)))

    def test_an_owned_root_directory_alone_passes_under_its_owner(self):
        self.assertEqual(shape.touches(build_ticket("**Touches:** infra")), ["infra"])
        self.assertEqual(self.broken(build_ticket("**Touches:** infra", owner="workstream 1")), [])
        self.assertIn("owner-line", self.broken(build_ticket("**Touches:** infra")))

    def test_a_root_entry_or_owned_prefix_is_a_path(self):
        # `docs` is a root directory, `tests` a prefix of owned paths; the prose words are neither.
        self.assertEqual(shape.touches(build_ticket("**Touches:** docs and tests, if added")),
                         ["docs", "tests"])

    def test_a_token_neither_path_nor_prose_fails(self):
        # `Gateway` may mean src/Gateway/ or nothing; no owner can be derived from it, so the line
        # cannot be fully judged. Lower-case words are the prose #260 and #269 mix in.
        for line in ("**Touches:** Gateway, tools/check.sh",
                     "**Touches:** tools/check.sh and TestIdp_v2"):
            with self.subTest(line=line):
                self.assertIn("touches", self.broken(build_ticket(line)))

    def test_the_prose_of_260_and_269_is_still_no_path(self):
        self.assertNotIn("and", shape.touches(build_ticket(TOUCHES_269)))
        self.assertEqual(len(shape.touches(build_ticket(TOUCHES_260))), 6)


class OwnerLine(unittest.TestCase):
    """The Owner line names exactly the derived role, by tools/overnight_collect.py's ROLES."""

    def test_two_roles_with_no_touches_line_fail(self):
        # Both as filed, before #227's Touches line existed: each names the integrator and a
        # workstream.
        self.assertIn("owner-line", rules_broken(204))
        self.assertIn("owner-line", rules_broken(206))

    def test_no_owner_line_fails(self):
        self.assertIn("owner-line", rules_broken(172))

    def test_the_role_the_touches_paths_derive_passes(self):
        # #209 as filed says "workstream 6 (`tests/e2e`)"; `tests/e2e` without its slash is still
        # workstream 6's directory.
        body = with_line(FILED[209].body, "**Owner:**", "**Touches:** `tests/e2e`")
        self.assertNotIn("owner-line", rules_broken(209, body))
        self.assertNotIn("one-owner", rules_broken(209, body))

    def test_a_role_other_than_the_derived_one_fails(self):
        body = with_line(FILED[209].body, "**Owner:**", "**Touches:** `tests/e2e`")
        body = body.replace("**Owner:** workstream 6 (`tests/e2e`)", "**Owner:** integrator")
        self.assertIn("owner-line", rules_broken(209, body))


class SittingOrLanding(unittest.TestCase):
    """Rules 2 and 3: under ready-for-agent, no open box needs a sitting, a push or a landing."""

    def failures(self, number: int, body: str | None = None, **changes) -> list[str]:
        filed = FILED[number]._replace(**changes)
        return [failure.detail for failure in shape.judge(shape.issue_of(raw(number, filed, body)))
                if failure.rule == "sitting-or-landing"]

    def test_a_box_that_needs_the_cell_fails(self):
        # #209 as filed: box 1 says "deployed" (a misfire: it is the stack switch, built here), and
        # box 5 needs the deployed cell, a sitting and a post on #63. Boxes 2 to 4 need the cell
        # too, in words the list does not hold: a word list misses as well as misfires.
        details = self.failures(209)
        self.assertEqual(len(details), 2, details)
        self.assertTrue(any("sitting" in detail and "posted on #" in detail for detail in details),
                        details)

    def test_a_box_that_needs_a_push_fails(self):
        details = self.failures(172)
        self.assertEqual(len(details), 1, details)
        self.assertIn("pushed", details[0])

    def test_a_box_that_needs_a_landing_fails(self):
        # #100 as filed: box 2 writes the landing pull request's body, box 4 closes every landed
        # row, and box 5 reads the next run's landing body.
        details = self.failures(100)
        self.assertEqual(len(details), 3, details)
        self.assertIn("landing", details[0])
        self.assertIn("pull request", details[0])
        self.assertIn("landed", details[1])

    def test_a_ticked_box_is_not_judged(self):
        self.assertEqual(self.failures(172, FILED[172].body.replace("- [ ]", "- [x]")), [])

    def test_a_ticket_not_ready_for_agent_is_not_judged(self):
        self.assertEqual(self.failures(209, labels=("ready-for-human",)), [])


class BlockerEdge(unittest.TestCase):
    """A wait the text states is a native blocked-by edge to an open issue (ADR-0020)."""

    # #209 as filed says "None (can start immediately)." under "## Blocked by"; #232 then gave it
    # the edge to #235. None of the eight bodies names a blocker in its text, so these state #209's
    # wait in each of the three phrasings the rule reads.
    PHRASES = ("Blocked by #235.", "Blocked by: #12, #235", "Starts after #235.",
               "Runs once #235 lands.")

    def body(self, phrase: str) -> str:
        return FILED[209].body.replace("None (can start immediately).", phrase)

    OPEN = ((12, "OPEN"), (235, "OPEN"))

    def test_a_stated_open_issue_with_no_edge_fails(self):
        for phrase in self.PHRASES:
            with self.subTest(phrase):
                self.assertIn("blocker-edge", rules_broken(209, self.body(phrase), states=self.OPEN))

    def test_a_stated_blocker_with_its_open_edge_passes(self):
        for phrase in self.PHRASES:
            with self.subTest(phrase):
                self.assertNotIn("blocker-edge",
                                 rules_broken(209, self.body(phrase), blockers=(12, 235)))

    def test_only_the_open_issue_of_a_list_fails(self):
        # "Blocked by: #12, #235" with #12 closed and #235 open: the failure names #235 alone.
        failures, notes = judged(209, self.body("Blocked by: #12, #235"),
                                 states=((12, "CLOSED"), (235, "OPEN")))
        details = [f.detail for f in failures if f.rule == "blocker-edge"]
        self.assertEqual(len(details), 1, details)
        self.assertIn("#235", details[0])
        self.assertNotIn("#12", details[0])
        self.assertEqual(len(notes), 1, notes)
        self.assertIn("#12", notes[0])

    def test_a_closed_issue_or_a_pull_request_is_stale_prose(self):
        # #284 item 1: "Blocked by #N" fails forever once #N closes, and a `#N` that is a pull
        # request can never pass. Neither is a wait any more; each is a note, not a failure.
        for kind in ("CLOSED", "PULL_REQUEST", "MISSING"):
            with self.subTest(kind):
                failures, notes = judged(209, self.body("Blocked by #235."),
                                         states=((235, kind),))
                self.assertNotIn("blocker-edge", [f.rule for f in failures])
                self.assertEqual(len(notes), 1, notes)
                self.assertIn("#235", notes[0])

    def test_an_edge_to_a_closed_issue_is_stale_prose_with_no_lookup(self):
        # The edge's own state says the wait is over; refuse_gh proves nothing is asked.
        failures, notes = judged(209, self.body("Blocked by #235."), closed=(235,))
        self.assertNotIn("blocker-edge", [f.rule for f in failures])
        self.assertEqual(len(notes), 1, notes)

    def test_an_open_edge_is_no_note(self):
        self.assertEqual(judged(209, self.body("Blocked by #235."), blockers=(235,))[1], [])

    def test_prose_about_a_closed_issue_is_not_a_blocker(self):
        # #100 as filed: "Once #66 closed, #67, #96 and #97 read as unblocked".
        self.assertNotIn("blocker-edge", rules_broken(100))


class LookUp(unittest.TestCase):
    """The numbers the prose names with no edge are asked of GitHub in one GraphQL query."""

    def run_with(self, stdout: str, returncode: int = 0, stderr: str = ""):
        calls = []

        def fake(command, **kwargs):
            calls.append(command)
            return subprocess.CompletedProcess(command, returncode, stdout, stderr)

        kept = shape.subprocess.run
        shape.subprocess.run = fake
        try:
            return setUpModule.kept([214, 83, 99999, 284]), calls
        finally:
            shape.subprocess.run = kept

    ANSWER = {"data": {"repository": {
        "n214": {"__typename": "PullRequest", "state": "MERGED"},
        "n83": {"__typename": "Issue", "state": "CLOSED"},
        "n284": {"__typename": "Issue", "state": "OPEN"},
        "n99999": None}},
        "errors": [{"type": "NOT_FOUND", "path": ["repository", "n99999"],
                    "message": "Could not resolve to an issue or pull request with the number "
                               "of 99999."}]}

    def test_each_number_is_read_as_its_kind(self):
        # gh exits 1 on the NOT_FOUND of #99999 and still prints the data, as it did on 2026-10-22.
        kinds, calls = self.run_with(json.dumps(self.ANSWER), returncode=1)
        self.assertEqual(kinds, {214: "PULL_REQUEST", 83: "CLOSED", 99999: "MISSING", 284: "OPEN"})
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0][:3], ["gh", "api", "graphql"])

    def test_any_other_error_is_unreadable(self):
        broken = dict(self.ANSWER, errors=[{"type": "RATE_LIMITED", "message": "slow down"}])
        for stdout, code in ((json.dumps(broken), 1), ("", 1), ("not json", 0),
                             (json.dumps({"data": None}), 0)):
            with self.subTest(stdout=stdout[:30]):
                with self.assertRaises(shape.Unreadable):
                    self.run_with(stdout, returncode=code, stderr="gh: boom")


class Audited(unittest.TestCase):
    """#284's acceptance, each against a body #232's second pass read on 2026-10-22."""

    def broken(self, number: int, **changes) -> list[str]:
        return [f.rule for f in judged(number, table=AUDITED, **changes)[0]]

    def test_213_passes_with_214_reported_as_stale_prose(self):
        # The Action run of 2026-10-22T01:14:41Z failed it: "the body waits for #214 with no
        # blocked-by edge to it open". "after #214's lines" names a merged pull request.
        failures, notes = judged(213, table=AUDITED)
        self.assertEqual(failures, [])
        self.assertEqual(len(notes), 1, notes)
        self.assertIn("#214", notes[0])

    def test_213_still_fails_when_214_is_an_open_issue(self):
        self.assertEqual(self.broken(213, states=((214, "OPEN"),)), ["blocker-edge"])

    def test_255_passes_with_touches_none_and_a_closed_blocker(self):
        failures, notes = judged(255, table=AUDITED)
        self.assertEqual(failures, [])
        self.assertIn("#83", notes[0])
        self.assertEqual(shape.touches(AUDITED[255].body), [])

    def test_239_passes_with_a_box_asking_for_a_count_in_the_task_merge(self):
        self.assertEqual(self.broken(239), [])

    def test_122_fails_on_the_landing_its_box_still_names(self):
        failures = judged(122, table=AUDITED)[0]
        self.assertEqual([f.rule for f in failures], ["sitting-or-landing"])
        self.assertIn("landing", failures[0].detail)

    def test_204_fails_for_its_second_owner(self):
        failures = judged(204, table=AUDITED)[0]
        self.assertEqual([f.rule for f in failures], ["owner-line"])
        self.assertIn("integrator and ws5-fixtures", failures[0].detail)


class TaskMerge(unittest.TestCase):
    """#284 item 2: a box asking the lane to state something in its task merge is no landing. Each
    task merge is its ticket's merge body, written by the lane's own integrate step (#226)."""

    def test_merge_body_is_not_on_the_list(self):
        self.assertNotIn("merge body", [word for word, _ in shape.SITTING_OR_LANDING])

    def test_landing_and_pushed_stay_on_the_list(self):
        words = [word for word, _ in shape.SITTING_OR_LANDING]
        self.assertIn("landing", words)
        self.assertIn("pushed", words)


class TouchesNone(unittest.TestCase):
    """#284 item 3: a ticket that writes nothing says so, and its owner is the Owner line's."""

    LINE = "**Touches:** none (runs the script against throwaway PRs)"

    def broken(self, body: str) -> list[str]:
        return rules_broken(204, body, labels=("ready-for-human",))

    def test_none_passes_under_one_owner(self):
        for line in (self.LINE, "**Touches:** none", "**Touches:** None."):
            with self.subTest(line=line):
                self.assertEqual(self.broken(build_ticket(line)), [])

    def test_none_then_needs_exactly_one_role(self):
        body = build_ticket(self.LINE, owner="integrator, with workstream 5")
        self.assertEqual(self.broken(body), ["owner-line"])
        self.assertEqual(self.broken(build_ticket(self.LINE, owner="the human")), ["owner-line"])

    def test_a_word_beginning_none_is_no_none(self):
        # "nonexistent" is not "none": the line names no path and fails as before.
        self.assertIn("touches", self.broken(build_ticket("**Touches:** nonexistent")))


class ReadyForAgentOnly(unittest.TestCase):
    """Under ready-for-agent: no foreground session, and no sub-issues."""

    def test_a_foreground_session_fails(self):
        self.assertIn("foreground", rules_broken(100))
        self.assertNotIn("foreground", rules_broken(100, labels=("ready-for-human",)))

    def test_a_spec_fails(self):
        # #79 carried ready-for-agent with five sub-issues until #232 removed the label.
        self.assertIn("spec", rules_broken(79))
        self.assertNotIn("spec", rules_broken(79, labels=()))
        self.assertNotIn("spec", rules_broken(79, sub_issues=0))


class AsFiled(unittest.TestCase):
    """Each of the six bodies #247 names fails as filed, and #147 and #198 pass."""

    BREAKS = {
        172: ["owner-line", "sitting-or-landing"],
        204: ["owner-line"],
        206: ["owner-line"],
        209: ["sitting-or-landing", "sitting-or-landing"],
        79: ["spec"],
        100: ["sitting-or-landing"] * 3 + ["foreground"],
    }

    def test_the_six_fail_on_their_rules(self):
        for number, broken in self.BREAKS.items():
            with self.subTest(number):
                self.assertEqual(rules_broken(number), broken)

    def test_147_and_198_pass(self):
        for number in (147, 198):
            with self.subTest(number):
                self.assertEqual(rules_broken(number), [])


class SharedCommit(unittest.TestCase):
    """#295: under ready-for-agent, no open box needs another ticket's commit on this ticket's
    branch or in its pull request. The overnight run integrates each ticket alone, so such a box
    cannot go green, and every ticket it blocks is skipped (#286)."""

    def details(self, number: int, body: str | None = None, **changes) -> list[str]:
        return [failure.detail for failure in judged(number, body, table=PAIRED, **changes)[0]
                if failure.rule == "shared-commit"]

    def box(self, text: str, labels: tuple[str, ...] = ("ready-for-agent",)) -> list[str]:
        return [failure.rule for failure in judged(
            145, build_ticket("**Touches:** `tools/x.py`").replace("Its tests pass.", text),
            table=PAIRED, labels=labels, closed=())[0]]

    def test_145_fails_on_the_box_that_carries_146s_commit(self):
        details = self.details(145)
        self.assertEqual(len(details), 1, details)
        self.assertIn("#146", details[0])
        self.assertIn("The pull request carries #146's workstream-6 commit", details[0])

    def test_146_fails_on_the_box_that_sits_on_145s_pull_request(self):
        details = self.details(146)
        self.assertEqual(len(details), 1, details)
        self.assertIn("#145", details[0])
        self.assertIn("sits on #145's pull request", details[0])

    def test_the_pair_as_filed_breaks_these_rules(self):
        # Both boxes also say "pull request", which the sitting-or-landing list flagged that night
        # as it would a misfire; shared-commit names the structure.
        for number in (145, 146):
            with self.subTest(number):
                self.assertEqual([f.rule for f in judged(number, table=PAIRED)[0]],
                                 ["sitting-or-landing", "shared-commit"])

    def test_a_box_that_only_cites_another_ticket_passes(self):
        # #146's box 5 names #144's overload, which a lane uses or reports as outstanding.
        self.assertEqual(self.box("The harness registers its client through #144's overload."), [])
        for text in ("The reader stays in the harness; it is #139's.",
                     "The suite reads the floor #12 set, as #12's tests do.",
                     "It follows #12's review, and the commit names each mutation."):
            with self.subTest(text):
                self.assertEqual(self.box(text), [])

    def test_the_words_of_a_shared_commit_fail(self):
        for text in ("The pull request carries #146's workstream-6 commit.",
                     "Its branch carries #12's commit.",
                     "The commit sits on #12's pull request.",
                     "It sits on #12's branch.",
                     "The change goes in #12's pull request.",
                     "The branch includes the commit of #12.",
                     "The branch has #12's two commits on it."):
            with self.subTest(text):
                self.assertIn("shared-commit", self.box(text))

    def test_the_tickets_own_commit_is_not_another_tickets(self):
        # The box helper judges ticket 145: a box naming #145's commit asks for its own work, which
        # its own lane has.
        self.assertEqual(self.box("The merge carries #145's commit and its count."), [])
        self.assertIn("shared-commit", self.box("The merge carries #146's commit and its count."))

    def test_a_ticked_box_is_not_judged(self):
        for number in (145, 146):
            with self.subTest(number):
                self.assertEqual(self.details(number, PAIRED[number].body.replace("- [ ]", "- [x]")),
                                 [])

    def test_only_ready_for_agent_is_judged(self):
        for labels in (("ready-for-human",), ("needs-triage",), ()):
            with self.subTest(labels):
                self.assertEqual(self.details(145, labels=labels), [])
                self.assertEqual(self.box("It sits on #12's branch.", labels=labels), [])


# The fake gh: logs its arguments to GH_LOG, fails as gh does when GH_FAIL is set, and answers
# `issue view` with GH_ANSWER's file and `api graphql` with GH_GRAPHQL's, when set. Anything else it
# is asked exits 97.
GH_DOUBLE = """#!/bin/bash
printf '%s\\n' "$*" >> "$GH_LOG"
if [ -n "$GH_FAIL" ]; then echo "gh: could not reach GitHub" >&2; exit 1; fi
if [ "$1 $2" = "api graphql" ] && [ -n "$GH_GRAPHQL" ]; then cat "$GH_GRAPHQL"; exit 0; fi
[ "$1 $2" = "issue view" ] || exit 97
cat "$GH_ANSWER"
"""


class CommandLine(unittest.TestCase):
    """main() end to end, with the fake gh on PATH and nothing else of the caller's environment."""

    def setUp(self):
        self.directory = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(subprocess.run, ["rm", "-rf", str(self.directory)], check=False)
        bin_dir = self.directory / "bin"
        bin_dir.mkdir()
        gh = bin_dir / "gh"
        gh.write_text(GH_DOUBLE, encoding="utf-8")
        gh.chmod(stat.S_IRWXU)
        self.log = self.directory / "gh.log"
        self.answer = self.directory / "answer.json"
        self.env = {"PATH": f"{bin_dir}:/usr/bin:/bin", "GH_LOG": str(self.log),
                    "GH_ANSWER": str(self.answer)}

    def run_script(self, *args: str, stdin: str = "", **env: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, "-I", str(SCRIPT), *args], input=stdin,
                              capture_output=True, text=True, env={**self.env, **env},
                              cwd=self.directory, check=False)

    def last_line(self, done: subprocess.CompletedProcess) -> str:
        return done.stdout.strip().splitlines()[-1]

    def test_a_passing_issue_on_stdin_exits_0(self):
        done = self.run_script("-", stdin=json.dumps(raw(198, FILED[198])))
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertTrue(self.last_line(done).startswith("ticket-shape: pass: #198"), done.stdout)

    def test_a_failing_issue_names_each_rule_and_exits_1(self):
        done = self.run_script("-", stdin=json.dumps(raw(100, FILED[100])))
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        lines = done.stdout.strip().splitlines()
        self.assertEqual(sum(line.startswith("ticket-shape: sitting-or-landing: ") for line in lines),
                         3, done.stdout)
        self.assertIn("ticket-shape: foreground: a ready-for-agent ticket says **Session:** "
                      "foreground", lines)
        self.assertTrue(lines[-1].startswith("ticket-shape: fail: #100 breaks "), done.stdout)
        self.assertIn("foreground", lines[-1])

    def test_unreadable_input_exits_2_on_a_verdict(self):
        broken = raw(198, FILED[198])
        del broken["blockedBy"]
        for stdin in ("not json", json.dumps(broken), ""):
            with self.subTest(stdin[:20]):
                done = self.run_script("-", stdin=stdin)
                self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
                self.assertTrue(self.last_line(done).startswith("ticket-shape: fail: "),
                                done.stdout)

    def test_a_number_is_read_through_gh(self):
        self.answer.write_text(json.dumps(raw(204, FILED[204])), encoding="utf-8")
        done = self.run_script("204")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("ticket-shape: owner-line: ", done.stdout)
        self.assertEqual(self.log.read_text(encoding="utf-8").strip(),
                         f"issue view 204 --json {shape.FIELDS}")

    def test_stale_prose_is_a_note_line_before_a_pass(self):
        # #213 as the Action read it at 2026-10-22T01:14:41Z, the state of #214 asked of gh.
        self.answer.write_text(json.dumps(raw(213, AUDITED[213])), encoding="utf-8")
        graphql = self.directory / "graphql.json"
        graphql.write_text(json.dumps({"data": {"repository": {
            "n214": {"__typename": "PullRequest", "state": "MERGED"}}}}), encoding="utf-8")
        done = self.run_script("213", GH_GRAPHQL=str(graphql))
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        lines = done.stdout.strip().splitlines()
        self.assertEqual(len(lines), 2, done.stdout)
        self.assertTrue(lines[0].startswith("ticket-shape: note: stale-prose: "), lines[0])
        self.assertIn("#214", lines[0])
        self.assertTrue(lines[-1].startswith("ticket-shape: pass: #213"), done.stdout)
        asked = self.log.read_text(encoding="utf-8").splitlines()
        self.assertEqual(len(asked), 2, asked)
        self.assertTrue(asked[1].startswith("api graphql "), asked)

    def test_a_failed_state_lookup_exits_2(self):
        # The issue reads, the lookup does not: nothing is judged, so the label is left alone.
        self.answer.write_text(json.dumps(raw(213, AUDITED[213])), encoding="utf-8")
        done = self.run_script("213")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertTrue(self.last_line(done).startswith("ticket-shape: fail: nothing judged: "),
                        done.stdout)

    def test_a_failed_gh_read_exits_2(self):
        done = self.run_script("204", GH_FAIL="1")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertTrue(self.last_line(done).startswith("ticket-shape: fail: "), done.stdout)
        self.assertIn("could not reach GitHub", done.stdout)

    def test_a_broken_check_exits_2_on_a_verdict(self):
        # A copy of the script and the two it reuses, laid out as in the repository, with one part
        # missing each time. Python ends an uncaught error on exit 1, the code of a failed rule,
        # which the workflow would answer with a relabel and a traceback for its reason.
        parts = ("tools/ticket_shape.py", "tools/overnight_collect.py", ".github/path-ownership.py",
                 "OWNERSHIP.md")
        for missing in parts[1:]:
            with self.subTest(missing=missing):
                tree = self.directory / f"tree-{len(missing)}"
                for part in parts:
                    if part != missing:
                        (tree / part).parent.mkdir(parents=True, exist_ok=True)
                        (tree / part).write_bytes((ROOT / part).read_bytes())
                done = subprocess.run([sys.executable, "-I", str(tree / parts[0]), "-"],
                                      input=json.dumps(raw(198, FILED[198])), capture_output=True,
                                      text=True, env=self.env, cwd=self.directory, check=False)
                self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
                self.assertTrue(self.last_line(done).startswith("ticket-shape: fail: nothing "
                                                                "judged: "), done.stdout)

    def test_no_argument_exits_2(self):
        for args in ((), ("204", "206"), ("not-a-number",)):
            with self.subTest(args):
                done = self.run_script(*args)
                self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
                self.assertTrue(self.last_line(done).startswith("ticket-shape: fail: "))


class Workflow(unittest.TestCase):
    """.github/workflows/ticket-shape.yml runs the check where tickets are written, read as text:
    the guard reads the file it guards, and a YAML parser is not in the standard library."""

    def setUp(self):
        self.text = WORKFLOW.read_text(encoding="utf-8")
        # The YAML without its comments, so a step described in a comment is not taken for one.
        self.code = "\n".join(line.split(" #", 1)[0] for line in self.text.splitlines()
                              if not line.lstrip().startswith("#"))

    def test_it_runs_when_an_issue_is_opened_edited_or_labelled(self):
        found = re.search(r"(?m)^on:\n  issues:\n    types: \[(?P<types>[^\]]*)\]", self.code)
        self.assertIsNotNone(found, self.code[:400])
        self.assertEqual(sorted(t.strip() for t in found.group("types").split(",")),
                         ["edited", "labeled", "opened"])

    def test_it_may_write_issues_and_read_pull_requests(self):
        # pull-requests: read lets look_up() answer for a #N that is a pull request (#284).
        self.assertRegex(self.code, r"(?m)^permissions:\n  issues: write\n  pull-requests: read\n"
                                    r"  contents: read$")

    def test_it_runs_the_check_on_the_event_issue(self):
        self.assertRegex(self.code, r"python3 -I tools/ticket_shape\.py \"\$NUMBER\"")
        self.assertRegex(self.code, r"NUMBER: \$\{\{ github\.event\.issue\.number \}\}")

    def test_a_failed_rule_swaps_the_label_and_comments(self):
        self.assertRegex(self.code, r"gh issue edit \"\$NUMBER\" --remove-label ready-for-agent "
                                    r"--add-label needs-triage")
        self.assertRegex(self.code, r"gh issue comment \"\$NUMBER\" --body-file ")

    def test_only_exit_1_relabels(self):
        # Exit 2 judged nothing: the job fails and the issue is left as it was.
        self.assertRegex(self.code, r"\[ \"\$status\" -eq 1 \]")
        self.assertRegex(self.code, r"(?s)\[ \"\$status\" -eq 1 \] \|\| exit \"\$status\"")

    def test_only_a_judged_verdict_relabels(self):
        # Exit 1 is also Python's code for an uncaught error. The script catches every error, and
        # the workflow relabels only on the verdict line of a judged failure as well.
        guard = self.code.index('"ticket-shape: fail: "[#]*" breaks "*)')
        self.assertLess(guard, self.code.index("gh issue edit"))


if __name__ == "__main__":
    unittest.main()
