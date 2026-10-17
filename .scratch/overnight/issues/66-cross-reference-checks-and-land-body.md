# 66: Hold cross-document claims with a check, not with review

**Task:** none (retrospective, 2026-10-16)
**Owner:** integrator
**Blocked by:** None
**Status:** ready-for-human

## Snapshot and current scope

The problem statement and counts below record the retrospective's pre-PR41/PR42 snapshot, not
current trunk. PR41/PR42 landed the method allowlist telemetry write, its composed assertions,
and the related contract corrections; tickets 62–65 are now tracked on trunk. Claims below that
trunk stops at 61, that the 405 write is unpushed, or that fifteen exception sites still await that
write are historical evidence. Their deletion obligation is satisfied by those landed changes.
The proposed cross-reference checks, test companion and landing-script body option remain future
work; this ticket does not claim them implemented. Ticket 67 is the separate G2 session-composition
work. The duplicate 61 is resolved: task 6.1a's ticket is 68 now, renumbered by
cross-reference-checks ticket 04, and the security suite's flake ticket kept 61.

## Problem Statement

A document in this repository can assert something the repository contradicts, and nothing fails.

On 2026-10-16 one false sentence in the contracts — that every rejection reaches
`security.rejections`, when the method allowlist's 405 does not — took six review rounds and four
pull requests to eliminate. It survived in nine places across four files. Every round found a new
defect in the *fix* rather than in the original claim: a missed file, a missed phrasing, a missed
path scope, an invented test-coverage claim, and a qualifier scoped to a rule id that two stages
write when only one was excepted.

None of it was reachable by a test. `Contracts.Tests` asserts that a contract file exists, exceeds
200 bytes, and contains the literal `Freezes at:`. So the only thing standing between a frozen
contract and a falsehood is a reviewer reading carefully, and `contracts-v1` was tagged over one of
these sentences while the review that found it was still running.

Three instances of the same shape are mechanical and currently unchecked:

- **A freeze claim that disagrees with its own target.** `contracts/README.md`'s index table carries
  a "Freezes at" cell per contract, and each contract carries its own `**Freezes at:**` header.
  Nothing compares them. The Pipeline row was landed reading "G1a, except the stage order at G1b",
  asserting a freeze at a gate the stage order never froze at, contradicting
  `contracts/pipeline.md` two directories away.
- **A cited ticket that does not exist.** `docs/agents/issue-tracker.md` says a document citing a
  ticket by path "is citing a file every other session and CI can open, which is the whole point of
  citing it". Merged code in `tests/Gateway.SecurityTests` cites tickets 63 and 64; `origin/main`
  stops at 61.
- **Two tickets sharing a number.** `.scratch/overnight/issues/` currently holds two files numbered
  `61`, written by two sessions that could not see each other's unlanded work.

Separately, `tools/land.sh` cannot do what `docs/agents/landing-work.md` requires of it. The guide
says a merge body carries `Outstanding:` lines; the script accepts only `--subject`. Five merges in
this session were landed by running the script's sequence by hand — fetch, fast-forward,
`tools/roadmap.py`, commit the refresh, push — which is precisely the error path the script exists
to prevent.

## Solution

Extend `.github/steering.py` from the two steering files to the whole tracked tree, and give it the
test companion it is the only check in its family to lack.

Its docstring already carries the rule: **"No dangling pointer."** The three new rules are that rule
applied wider. A pointer that resolves to nothing and a cell that disagrees with the file it indexes
are the same defect — a claim about the repository that the repository does not support — and they
belong behind one check that runs in the pre-push hook's one-second tier, beside the steering rules
it already holds.

Add `--body-file` to `tools/land.sh`, so landing a pull request through the sanctioned path can
carry the body the guide requires.

## User Stories

1. As an integrator, I want the contracts index checked against each contract's own freeze header,
   so that a freeze gate cannot be claimed in the index and denied in the file it points at.
2. As a workstream agent, I want a cited ticket number to resolve to a file, so that following a
   citation in merged code lands on a spec rather than on nothing.
3. As a session filing a ticket, I want a duplicate number to fail before it is pushed, so that two
   sessions working concurrently cannot both claim `61`.
4. As a reviewer, I want these three classes caught mechanically, so that my attention goes to the
   judgement calls no check can make instead of to cross-referencing cells by hand.
5. As an integrator landing a pull request, I want `tools/land.sh` to take a merge body, so that the
   `Outstanding:` lines `landing-work.md` requires survive the merge.
6. As an integrator, I want `land.sh` to remain the one sanctioned merge path, so that the roadmap
   derivation it performs is never skipped because the script could not express what was needed.
7. As a maintainer of `steering.py`, I want a test companion, so that widening its charter does not
   silently break the two rules it already holds.
8. As a session running `git push`, I want the new rules in the one-second tier, so that the guard
   costs an amended commit rather than a red pull request.
9. As a session on a machine without `python3`, I want the check to report that it did not run,
   matching how the existing checks degrade.
10. As an author of a contract, I want the failure message to name both sides of the disagreement,
    so that I can see which of the index and the header is wrong without opening either.
11. As an author citing a ticket early, I want a documented way to name one that does not exist yet,
    matching how `PLANNED` already exempts a promised path in `steering.py`.
12. As a reviewer of this change, I want each new rule to fail on a case built for it, so that a
    rule that cannot fire is caught before it ships.
13. As a session reading `docs/agents/issue-tracker.md`, I want the numbering convention to say what
    happens when several sessions file concurrently, so that the collision has a documented answer
    rather than a check that only reports it.
14. As an integrator, I want `CODING_STANDARDS.md` to carry the scoping rule that cost four review
    rounds, so that the reviewer agent enforces it on the next contract sentence.
15. As a reviewer, I want `docs/agents/review.md` to carry the search discipline that ended the
    cycle, so that the next reviewer greps the noun rather than the sentence.

## Implementation Decisions

**One seam.** All three rules go into `.github/steering.py`. It is an existing check, already wired
into both the pre-push hook and the CI step named "Steering files claim no progress and every
pointer resolves", and its `PATHLIKE`/`dangling()` machinery already resolves backticked paths
against the tree. Widening it costs no new file and no new wiring. The CI step's name and the
script's module docstring both need updating to say what it now covers, since the current name
describes only the narrower charter.

**Rule one — the freeze column agrees with the header.** Parse the index table in
`contracts/README.md`, take each row's target file and its "Freezes at" cell, read that file's own
`**Freezes at:**` header, and fail when the cell's leading gate differs from the header's gate. The
cell is allowed to say more than the header — `G1a; stage order changed at G1b` and `G1a, except the
`oauth` section at G1b` are both legitimate — so the comparison is on the leading gate token, not on
string equality. A cell naming a *different* gate than its target, or naming a second gate as the
freeze rather than as a change, is the failure.

**Rule two — a cited ticket resolves.** Scan tracked files for ticket citations and fail when the
number resolves to no file under `.scratch/*/issues/`. The citation shapes seen in the tree are
`ticket NN`, `tickets NN and NN`, and `overnight ticket NN`; the pattern must cover all three,
because the session that introduced the dangling citation wrote two of them and a narrower grep
found neither. Follow `PLANNED`'s precedent for a ticket named before it is filed: an explicit
allow-list in the script, which fails once the ticket appears, so the list cannot outlive its
entries.

**Rule three — ticket numbers are unique per feature directory.** Within each
`.scratch/<feature-slug>/issues/`, no two files share a leading number. Numbering restarts per
directory by design, so the check is scoped to one directory at a time rather than across the
tracker.

**The test companion.** Add `.github/steering_tests.py`, following `path_ownership_tests.py`: build
a throwaway tree per case rather than asserting against the live repository, so a case is a shape
rather than a search. It must cover the two existing rules as well as the three new ones —
`steering.py` is the only check in its family shipping untested, and widening it is the moment to
close that. Wire it into the CI step beside the check itself, as `plan-secrecy-tests.py` and
`roadmap_tests.py` already are.

**`tools/land.sh`.** Add `--body-file <path>`, passed through to `gh pr merge --body-file`. The
existing `--subject` handling and the "no subject given" confirmation prompt stay as they are. The
script's header comment documents the two forms a merge subject may take; it should now also say
that the body carries the `Outstanding:` lines, with the pointer to `landing-work.md` that makes
that requirement findable.

**Two prose additions, which no check substitutes for.** `CODING_STANDARDS.md` gains the scoping
rule: a sentence stating a universal names its exceptions by the *writer*, never by an identifier,
because an identifier may have several writers and only some be excepted. `docs/agents/review.md`
gains the search discipline: grep the noun a claim cannot paraphrase away, then verify the claim's
scope is the thing that actually has the property. Both are judgement calls about cross-file
consistency, which is what `CODING_STANDARDS.md` is for.

## Testing Decisions

**What makes a good test here.** Each rule is tested through the check's own exit code and output on
a tree built to trip it — external behaviour, not the parsing helpers underneath. A test that
asserted on `dangling()`'s return shape would pin an implementation detail; a test that builds a
tree with a mismatched freeze cell and asserts the check fails pins the rule.

**Every rule fails on a case built for it.** This is the bar the repository already states in
`CODING_STANDARDS.md` → "An assertion must be able to fail", and the reason it matters here is
specific: a cross-reference check whose pattern does not match the shape in the tree passes
silently and looks identical to a clean repository. The session that produced this spec ran a
`git grep -E` with a `\b` that POSIX ERE does not support; it matched nothing and read as "zero stale
references" until a second search contradicted it. Each rule therefore needs a red case and a green
case, and the red case must be built from a shape actually seen in the tree, not an invented one.

**Modules tested.** `.github/steering.py` only. `tools/land.sh` is not tested — the repository has
no shell test harness, and adding one is out of scope below.

**Prior art.** `.github/path_ownership_tests.py` is the closest model: throwaway repositories built
per case, the module resolved from `__file__` so the root can be patched, `contextlib` and `io` used
to capture output. `tools/roadmap_tests.py` and `.github/plan-secrecy-tests.py` are the same family
and show the CI wiring pattern — tests run immediately before the check in the same step.

## Out of Scope

- **The 405's actual write.** `MethodAllowlist` writing its rule id to `IRequestTelemetryFeature` is
  workstream 4's, tracked as ticket 63, already written on an unpushed branch. Workstream 6's
  assertion is ticket 64.
- **The 82 stale `stage 1`/`stage 2` references** in `src/Platform.Security`,
  `src/Platform.Telemetry`, `src/Platform.Hosting`, `src/Platform.Proxy` and their tests, including
  `RequestTelemetry.cs`'s inverse-wrong "outside both". Workstream 2, 4 and 6's paths.
- **Workstream 1's five `infra/` stage references**, two inside Terraform `error_message` strings.
- **A shell test harness.** `tools/preflight.sh` shipped five defects this session and its
  parse-versus-shell agreement is verified only by manual runs. A real gap, and a larger piece of
  work than this spec; filing it separately keeps it visible.
- **Enforcing review before merge.** Two pull requests merged without a completed review this
  session, and one was tagged mid-review, which is how a false guarantee ended up frozen. Worth its
  own decision, because the options differ in kind — a check on `land.sh`, a branch protection rule,
  or accepting it as the documented weak link.
- **Dropping the `@docs/agents/aws-rules.md` import** from `CLAUDE.md`, which loads 76 lines of
  vendor-generated rules into every session for services this platform does not use.

## Further Notes

The deletion obligation matters to whoever picks this up. Fifteen sites currently say "except the
method allowlist's 405", and all fifteen become wrong in the other direction the moment ticket 63
lands its one line. The list is recorded in
`contracts/changes/6.4-request-telemetry-feature.md`, which is the file that implementer will be
reading — but nothing enforces it, and the new freeze-column rule does not cover it. If a fourth
cross-reference rule is ever worth writing, that is the candidate.

On why this is a check and not a standard: the three rules are fixed, syntactic, and decidable from
the tree alone, which is the test `CODING_STANDARDS.md` itself applies when deciding between a rule
and a guardrail. The two prose additions in this spec are the residue that genuinely cannot be
decided mechanically.

## Comments

**Overnight collect, 2026-10-17:** Status was `ready-for-agent`, now `ready-for-human`. Its work landed in `37fdca2` ("Merge cross-reference checks and the land.sh merge body"), split into six tickets in the cross-reference-checks tracker. Whether this ticket is now resolved or stays open as the parent of tickets 69 and 70 is the decision that cross-reference-checks ticket 09 leaves to Adam.

## Grilling record

Run on paper on 2026-10-17 by the overnight grill, against `eb65be8`. That is origin/main as last
fetched; it was not re-fetched, because this grill writes only this file. Adam's standing answer
of 2026-10-15 accepts every recommendation, so each round's recommendations were adopted as
decided before the next round was computed. The root of the tree is the decision
cross-reference-checks ticket 09 leaves open: whether this ticket is resolved, or stays open as
the parent of overnight tickets 69 and 70.

### Facts established before round 1 (looked up, not asked)

- **Everything the Solution asks for is on origin/main.** `37fdca2` ("Merge cross-reference checks
  and the land.sh merge body", pull request #44) is first-parent on main, and its body opens with
  "Lands overnight ticket 66". It carries cross-reference-checks tickets 01 to 06, each `resolved`
  with an Answer naming its own merge on `integrator/g2-prep`:
  - the test companion (`bafe4b6`);
  - the freeze-header rule (`3d03f35`);
  - the citation rule (`d2523bf`);
  - the numbering rule (`1f559cc`, which renumbered task 6.1a's ticket from 61 to 68);
  - `tools/land.sh --body-file` (`fb6b66d`, with the relative-path fix `2c9962f`);
  - the two prose rules (`c15f58f`).

  Each of the fifteen user stories maps to one of the six. `python3 -I .github/steering.py` passes
  on the tree in 0.8 s, so story 8's one-second tier holds. The margin is small, since the
  citation rule reads every tracked file.
- **The false sentence outlived the commits that should have caught it.** "The proposed
  cross-reference checks, test companion and landing-script body option remain future work" became
  false when `37fdca2` reached main. `83e725c` ("Tracker: the six cross-reference tickets resolved")
  did not touch this file. The one edit to it on that branch, `3bb91b8`, changed the very next
  sentence and left this one standing. It is a present-tense status claim outside the Status line,
  and no check reads such a claim: the defect this ticket is about, inside this ticket.
- **One of the six has an unticked box.** Cross-reference-checks ticket 06's third box is unticked;
  it asks both prose rules to say why they are prose, citing cross-reference-checks tickets 02 to
  04. That ticket's Answer calls it "met in substance". `CODING_STANDARDS.md` cites no ticket
  anywhere. Its preface says everything in it is a judgement call no check could decide, and the
  new section ends by sending "the syntactic half of this family" to a check. The new step in
  `docs/agents/review.md` says "No check decides the scope question".
- **Where each Out of Scope item went.** The "five further gaps" that cross-reference-checks ticket
  09 counts are the second to sixth bullets.
  1. The 405's write: overnight tickets 63 and 64, resolved, landed with PR41/PR42.
  2. The 82 numbered stages under workstreams 2, 4 and 6: tickets 62 to 64 swept them.
     `git grep -n -i -P '\bstages? \d+\b' -- src tests` now finds only `src/Gateway/Program.cs`
     (see below), and `RequestTelemetry.cs`'s "outside both" is gone.
  3. **Workstream 1's five `infra/` references: untracked, and now wrong.** They are at
     `infra/modules/cell/README.md:81`, `ecs.tf:98` and `:99`, and `tests/module.tftest.hcl:565`
     and `:592`, the last two inside `error_message` strings. They say stage 1 is the stage
     that trusts the load balancer's forwarded headers, and two of them cite
     "contracts/pipeline.md stage 1".
     - Task 1.4a wrote them under the G1a order. They reached main in `fb4899a` on the afternoon
       of 2026-10-15.
     - ADR-0018 reached main in `ad5e041` that night and made stage 1 `UsePlatformTelemetry`.
     - Its decision 4 handed out the sweep "for workstreams 2, 4 and 6" only.

     No ticket names these lines. This ticket's Out of Scope bullet is their only record.
  4. A shell test harness: overnight ticket 69, `ready-for-human`.
  5. Enforcing review before a merge: overnight ticket 70, `ready-for-human`.
  6. **The `@docs/agents/aws-rules.md` import: untracked.** `CLAUDE.md` still imports it, between
     AWS Agent Toolkit markers. It arrived with task I-6 (`a4d994e`, 2026-10-15). The file says
     "Re-running that setup replaces this file", so it cannot be edited in place. The bullet
     misses two facts.
     - Every session loads the file, yet `.github/steering.py`'s `FILES` holds only AGENTS.md and
       CLAUDE.md. Lines 3 and 53 of the file each carry a date that the "No progress" rule would
       flag.
     - "Services this platform does not use" overstates it. Its Region, IAM and spend-limit rules
       bind workstream 1's applies.
- **The fourth rule in Further Notes has no live instance.** The only deletion obligation under
  `contracts/changes/` is 6.4's, headed "Historical deletion obligation — completed by PR41".
  Outside that history, no "except the method allowlist's 405" remains in the tree.
- **A contradiction the Out of Scope list never named.** `src/Gateway/Program.cs`, which the
  integrator owns, heads ten comments "Stage 1." to "Stage 10.".
  - `GLOSSARY.md` → Stage says "never 'stage 6'".
  - `CODING_STANDARDS.md` → "A stage is named, not numbered" makes no exception.
  - ADR-0018 decision 4 says its commit "sweeps the integrator's files". Yet that commit,
    `abcfd9c`, rewrote the "Stage 1." and "Stage 2." comments by hand when it swapped the two
    calls.

  The numbers are right today, and no test or tool reads them.
- **"Parent" already means something here.** `tools/roadmap.py` → `parent()` returns the plan row a
  split id belongs to, and "a split id never lands its parent row". The tracker has no parent
  relation between tickets. It writes three other relations:
  - `Blocked by:`: wait for.
  - `**Source:**`: part of that spec, as cross-reference-checks tickets 01 to 06 say of this
    ticket.
  - "raised by" in a `Task:` line: where it came from, as overnight tickets 69 and 70 say.

  `docs/agents/issue-tracker.md` documents only the first.
- **Status vocabulary.** On origin/main, 51 of 92 tickets say `resolved`, 9 `blocked`, 3
  `claimed`, 3 `closed` (all in `g1a-and-landing-work`) and 1 `partial`. CLAUDE.md and
  `issue-tracker.md` send a reader to `docs/agents/triage-labels.md` for status strings, and it
  lists only the five triage roles. `resolved` is documented only in the Wayfinding section of
  `issue-tracker.md`, and the overnight implementer writes it on green.
- **What an open ticket costs here.** The overnight collect re-derives every unresolved ticket
  each night, and its status enum has no `resolved`. A `ready-for-human` ticket goes to the grill
  every night, carried after the first. Deleting the file instead is not an option.
  `.githooks/pre-push` warns that deleting a ticket file "turns every citation of it red", and
  cross-reference-checks ticket 09 and overnight tickets 69 and 70 cite this one.

### Terms challenged against GLOSSARY.md

- **"Feature directory"** (this ticket, rule three) and **"tracker"** (`steering.py`,
  `issue-tracker.md`) name the same numbering scope. The tree writes "feature directory" 7 times
  and "tracker directory" 5 times. `issue-tracker.md` also uses "the tracker" for all of
  `.scratch/`. Proposed: **Tracker**, below.
- **"Parent"** in cross-reference-checks ticket 09 collides with the parent row of `roadmap.py`,
  and it names neither relation the tree writes. Proposed: **Source ticket** and **Follow-up**,
  below.
- **"Task"**: the overnight workflow calls this ticket "task ticket-66", though its `Task:` line
  says none. `GLOSSARY.md` defines neither task nor ticket. Proposed: **Task** and **Ticket**,
  below.
- **"Resolved"** is a ticket's closing status, not a task being done. `landing-work.md` says
  "Merged is not done", and overnight tickets go `resolved` on green, on a lane branch, before any
  merge. It is a label string, so it belongs in `triage-labels.md` (Q8), not in the glossary.

### Concrete scenarios used to test the terms

1. **Ticket 70 is decided as "accept the documented weak link", and ticket 69 is declined.** As a
   parent, this ticket would then need a rule for whether a declined child releases it. Held by its
   own scope, it was settled when `37fdca2` landed.
2. **A follow-up of a follow-up.** Cross-reference-checks ticket 07 follows up on 05, and 08 on 02.
   If follow-ups held their origin open, those two would hold 05 and 02 open, and through them this
   ticket. A fix to 08 that raised a follow-up of its own would lengthen the chain. Today 05 and 02
   are `resolved` while 07 and 08 are open, which is already the source-ticket reading.
3. **A reader follows ticket 69's `Task:` line back here.** They learn why 69 exists whether this
   ticket is open or resolved. Provenance needs the file to exist, not to be open.
4. **A plan row and its split.** Task 6.3a landed, and row 6.3 stays open. It is held by its own
   done-when, "Green at G4", not by the split. So a parent row, too, is held open by its own scope.
5. **A piece of a spec is declined.** Suppose cross-reference-checks ticket 06 had been `wontfix`.
   This ticket's scope would be settled but not fully delivered. Its Answer would then have to say
   what was declined, or "resolved" reads as all of it.

### Round 1

❓ **Q1** - **What holds a ticket open?**
- (a) Its own scope only. A ticket is resolved when what its own text asks for has landed or been
  answered. A spec broken into tickets is resolved when every ticket naming it on a `Source:` line
  is settled. A follow-up, raised from its work, its review or its Out of Scope list, names where
  it came from and holds nothing open.
- (b) Its own scope plus every follow-up raised from it. This is the "parent" reading in
  cross-reference-checks ticket 09.
- (c) Its own scope plus the follow-ups from its own Out of Scope list, but not review follow-ups.

➡️ (a). Under (b) and (c), this ticket's completion depends on its Out of Scope list, which is by
definition what it excludes. Under (b), scenario 2's chain also climbs through follow-ups of
follow-ups. Overnight tickets 69 and 70 already carry the pointer back here. Adopted.

---

❓ **Q2** - **Which lines of a ticket must stay true?**
- (a) Only its standing claims, the `Status:` and `Blocked by:` lines, corrected in place.
  Everything else is a dated record and is never rewritten: the spec as filed, its Comments, its
  Answer and its grilling record. A later truth goes under Comments as a dated line.
- (b) Every present-tense sentence anywhere in a ticket, kept current.
- (c) No rule, as now.

➡️ (a), written as one line under Conventions in `docs/agents/issue-tracker.md`. A dated record
cannot go stale. The false sentence here was a status claim outside the Status line, which no
check reads. It outlived both the commit that resolved its six tickets and the merge that landed
them. (b) would ask every session to re-read every ticket. Adopted.

---

❓ **Q3** - **Is cross-reference-checks ticket 06's third box met?**
- (a) Tick it, and keep its Answer's note.
- (b) Reopen the ticket, and cite that tracker's tickets from `CODING_STANDARDS.md` and
  `docs/agents/review.md`.

➡️ (a). Both rules already say why they are prose. `CODING_STANDARDS.md` states rules and cites no
ticket anywhere, and a citation there would tie a standard to a tracker file. Adopted.

---

❓ **Q4** - **Do workstream 1's five numbered stages get their own ticket?**
- (a) File one overnight ticket at the next free number, in the shape of tickets 62 to 64. The
  owner is ws1-infra, the `Task:` line reads
  `Task: none (raised by overnight ticket 66, "Out of Scope")` as it does for 69 and 70, and the
  status is `ready-for-agent`. The ticket names the five lines.
  - Each "stage 1" becomes `UsePlatformForwardedHeaders`.
  - "contracts/pipeline.md stage 1" becomes that row, named by its entry point.
  - It is done when `git grep -n -i -P '\bstages? \d+\b' -- infra` finds nothing, and
    `terraform fmt -check`, `terraform validate` and the module's `terraform test` pass
    unchanged.
  - The grep is PCRE because `\b` silently matches nothing under POSIX ERE, as this ticket's
    Testing Decisions record.
- (b) Fold the sweep into an open workstream 1 ticket.
- (c) Leave it in this ticket's Out of Scope.

➡️ (a). The lines name the wrong stage today. This ticket cannot carry them, because it is the
integrator's and `infra/` is workstream 1's. (b) would tie a five-line sweep to a row blocked on AWS
(1.4) or to the network row's scope (1.2). The new ticket completes ADR-0018 decision 4, which
listed workstreams 2, 4 and 6 only, so it contradicts nothing in the ADR. Adopted.

---

❓ **Q5** - **Does the `aws-rules.md` import get its own ticket?**
- (a) File one overnight ticket at the next free number, owned by the integrator, with the
  `Task:` line `Task: none (raised by overnight ticket 66, "Out of Scope")` and status
  `ready-for-human`. It carries three options and records the two facts above.
  - **Keep the import**, and have `.github/steering.py` either follow CLAUDE.md's imports or name
    this file as exempt. The file cannot be edited to pass, because the setup rewrites it.
  - **Scope it**, by moving the pointer from `CLAUDE.md` to where AWS work starts, such as the
    "Where to look" table in AGENTS.md and workstream 1's docs.
  - **Drop it.**
- (b) Decide it while closing this ticket.
- (c) Drop the question.

➡️ (a). This is Adam's own tooling, installed on 2026-10-15. A blanket pre-accepted "yes" should
not remove it. Keeping it also leaves a gap in the steering check, which needs its own answer
either way. Adopted.

---

❓ **Q6** - **Is the fourth cross-reference rule in Further Notes declined?**
- (a) Do not file it, and say why in the Answer. It fails this ticket's own test for a check,
  "fixed, syntactic, and decidable from the tree alone":
  - its one instance completed in PR41 and is kept as history;
  - its trigger is a code change landing, not a fact in the tree;
  - "or a phrasing of it" is not syntactic.
- (b) File it as a candidate rule.

➡️ (a). Adopted.

---

❓ **Q7** - **Do the ten "Stage N." comments in `Program.cs` go?**
- (a) File one integrator ticket at the next free number, status `ready-for-agent`. It deletes
  each "Stage N." prefix and keeps the relation the comment states, since the call it labels sits
  on the next line.
- (b) Name the composition root as the one exception, in `CODING_STANDARDS.md` and `GLOSSARY.md`.
- (c) Leave the contradiction as it is.

➡️ (a). These are the copies that rot. A block moved together with its comment carries the old
number into the new position, which is why `abcfd9c` had to rewrite two of them by hand. The
glossary says "never". The change touches comments only, and no test. It is filed apart from Q4's
ticket because the owners differ. Adopted.

---

❓ **Q8** - **Does `docs/agents/triage-labels.md` list the lifecycle states?** It gains a second
table:
- `claimed`: a lane has taken it;
- `blocked`: it waits on what its `Blocked by:` line names;
- `resolved`: closed, with `## Answer` saying where the work or the answer is.

The table says that `closed` and `partial` are not states. The three `g1a-and-landing-work`
tickets that say `closed` change to `resolved` in the same edit.

➡️ Yes. That file is where a session is sent for status strings, and 67 of 92 tickets on
origin/main carry a status it does not list. Adopted.

### Round 2 (after Q1 to Q8)

❓ **Q9** - **Is overnight ticket 66 resolved now?**
- (a) Set `Status: resolved`, recorded under `## Answer`. Under Q1 its scope is settled. All six of
  its source tickets are resolved, with Q3 settling 06. Every Out of Scope item has a home (Q4, Q5)
  or is declined (Q6).
- (b) Keep it `ready-for-human` as the parent of 69 and 70.
- (c) Set it `blocked`, with `Blocked by: 69, 70`.

Three options were pruned without a question, for want of a real alternative:
- `wontfix`, which is false, because its work landed.
- Deleting the file, which fails the next push.
- Moving it to `.scratch/cross-reference-checks/spec.md`. That strands the merge body that says
  "Lands overnight ticket 66", and history cannot be edited.

➡️ (a). (b) and (c) contradict Q1, and either one would put this ticket in every night's collect
with nothing for anyone to do. Adopted.

---

❓ **Q10** - **Does `issue-tracker.md` write Q1's rule down?** It goes under Conventions, in terms
of the lines the tree already writes:
- A `**Source:** <path>` line marks a ticket as part of that spec. The source resolves when every
  ticket naming it is settled, and its Answer lists them and anything declined (scenario 5).
- "raised by <ticket>" in a `Task:` line marks a follow-up, which holds nothing open.
- Tickets have no "parent".

➡️ Yes. Otherwise the next session that splits a spec asks cross-reference-checks ticket 09's
question again. Adopted.

---

❓ **Q11** - **Does ticket 61's line "62 to 65 are taken, so 66 is the next free number" stay as
written?**

➡️ Yes, under Q2. It is a dated Comments note, and it was true when it was written. Adopted.

### Round 3 (after Q9 to Q11)

❓ **Q12** - **Is the resolution applied in one integrator tracker commit, with the steering check
green before the push?** In order, the commit:

1. Files the tickets from Q4, Q5 and Q7, so that the Answer can cite their numbers.
2. Ticks cross-reference-checks ticket 06's third box.
3. Replaces this sentence in this ticket's Snapshot paragraph:

   "The proposed cross-reference checks, test companion and landing-script body option remain
   future work; this ticket does not claim them implemented."

   with this one:

   "Everything this ticket proposes landed on origin/main in `37fdca2` (pull request #44), as
   cross-reference-checks tickets 01 to 06; `## Answer` records where each part went."

   The heading stays, so cross-reference-checks ticket 09's quotation of it still finds it.
4. Sets `Status: resolved` here, and appends the draft below under a new `## Answer` heading.
5. Resolves cross-reference-checks ticket 09: both boxes ticked, `Status: resolved`, and an Answer
   that points here.
6. Adds the lines from Q2 and Q10 to `docs/agents/issue-tracker.md`, and the table from Q8 to
   `docs/agents/triage-labels.md`.

The draft Answer follows. Fill in the three new tickets' numbers when they are filed.

```text
**Resolved <date>**, decided by Adam through the overnight grilling recorded above.

Landed on origin/main in `37fdca2` (pull request #44) as cross-reference-checks tickets 01 to 06.
Each of those tickets' Answers names its own merge:

- the steering check's test companion;
- the freeze-header rule;
- the citation rule;
- the numbering rule, which renumbered task 6.1a's ticket from 61 to 68;
- `tools/land.sh --body-file`, with the relative-path fix `2c9962f`;
- the two prose rules.

Every user story above maps to one of them.

Where each Out of Scope item went:

- The 405's write: overnight tickets 63 and 64, resolved.
- The numbered stages under workstreams 2, 4 and 6: overnight tickets 62 to 64, resolved.
- Workstream 1's five `infra/` references: <the new workstream 1 ticket>.
- A shell test harness: overnight ticket 69.
- Enforcing review before a merge: overnight ticket 70.
- The `@docs/agents/aws-rules.md` import: <the new integrator ticket>.

Also filed from this grilling: <the `Program.cs` ticket>.

The fourth rule in Further Notes is not filed. Its one instance completed in PR41, and its trigger
is a code change, not a fact in the tree.

These follow-ups do not hold this ticket open:

- cross-reference-checks tickets 07 and 08;
- overnight ticket 71, the CI flake that the body of `37fdca2` records as not yet filed;
- overnight tickets 69 and 70.
```

➡️ Yes. The Answer cites the new tickets by number, so they have to land in the same push. One
commit also leaves no state in which this ticket and the tree disagree. Adopted.

### Round 4

Recomputed frontier: empty. Three things are settled elsewhere, deliberately not here:
- which option Q5's ticket takes;
- the decisions overnight tickets 69 and 70 hold;
- the work in cross-reference-checks tickets 07 and 08, which are `ready-for-agent` and need no
  decision.

Nothing is silently assumed. Two facts come from the last fetch rather than a new one: origin/main
at `eb65be8`, and 71 as the highest overnight number. The morning session takes "the next free
number" from a fresh fetch, as `issue-tracker.md` says.

## Questions for Adam

1. **Is a ticket held open by its own scope only?** A spec broken into tickets would resolve when
   every ticket naming it on a `Source:` line is settled. A follow-up raised from its work, review
   or Out of Scope list would hold nothing open.
   Recommendation: yes, and tickets get no "parent" relation.
   Why: an Out of Scope list is what a ticket excludes, so waiting on it contradicts the ticket's
   own text, and overnight tickets 69 and 70 already point back here.
   Answer: yes, pre-accepted by Adam, 2026-10-15
2. **Are a ticket's only standing claims its `Status:` and `Blocked by:` lines, corrected in
   place?** Everything else would be a dated record and never rewritten: the spec as filed, its
   Comments, its Answer and its grilling record. A later truth would be appended as a dated
   Comment. `docs/agents/issue-tracker.md` → Conventions would say so in one line.
   Recommendation: yes.
   Why: this ticket's false "remain future work" was a status claim outside its Status line, which
   no check reads. It outlived both the commit that resolved its six tickets and the merge that
   landed them.
   Answer: yes, pre-accepted by Adam, 2026-10-15
3. **Should cross-reference-checks ticket 06's unticked third box be ticked as met, keeping its
   Answer's note, rather than reopened?**
   Recommendation: yes.
   Why: both prose rules already say why they are prose, and `CODING_STANDARDS.md` cites no ticket
   anywhere.
   Answer: yes, pre-accepted by Adam, 2026-10-15
4. **Should workstream 1's five numbered stages under `infra/modules/cell` get one new overnight
   ticket?** It would take the next free number, with owner ws1-infra, status `ready-for-agent`
   and the `Task:` line `Task: none (raised by overnight ticket 66, "Out of Scope")`. Each
   "stage 1" would become `UsePlatformForwardedHeaders`. It would be done when
   `git grep -n -i -P '\bstages? \d+\b' -- infra` finds nothing, and when fmt, validate and the
   module's `terraform test` pass unchanged.
   Recommendation: yes.
   Why: they have named the wrong stage since ADR-0018, whose sweep listed only workstreams 2, 4
   and 6. This ticket's Out of Scope bullet is their only record.
   Answer: yes, pre-accepted by Adam, 2026-10-15
5. **Should the `@docs/agents/aws-rules.md` import get its own `ready-for-human` overnight ticket,
   owned by the integrator, rather than be decided while closing this one?** The ticket would
   carry three options:
   - keep it, and have `.github/steering.py` follow the import or exempt the file;
   - scope it to where AWS work starts;
   - drop it.

   Recommendation: yes. File the question here, and decide it in that ticket.
   Why: it is your own toolkit setup from 2026-10-15, so a blanket pre-accepted "yes" should not
   remove it. Every session loads a file the steering check never reads, and the file's two dates
   would fail that check's "No progress" rule.
   Answer: yes, pre-accepted by Adam, 2026-10-15
6. **Should the fourth cross-reference rule in Further Notes, a check on a deletion obligation, be
   declined, with the reason in this ticket's Answer?**
   Recommendation: yes.
   Why: its one instance completed in PR41 and is kept as history. Its trigger is a code change,
   not a fact decidable from the tree.
   Answer: yes, pre-accepted by Adam, 2026-10-15
7. **Should the ten "Stage N." comment prefixes in `src/Gateway/Program.cs` be removed, through
   one new `ready-for-agent` integrator ticket?** Each comment would keep its relation. The
   alternative is to name them as an exception to "A stage is named, not numbered".
   Recommendation: yes.
   Why: `GLOSSARY.md` says "never 'stage 6'". A comment block moved with its call carries its old
   number into the new position, which is why ADR-0018's commit had to rewrite two of them by
   hand.
   Answer: yes, pre-accepted by Adam, 2026-10-15
8. **Should `docs/agents/triage-labels.md` gain a table of the lifecycle states the trackers
   write: `claimed`, `blocked` and `resolved`?** `resolved` would be the one closing status, with
   `## Answer` saying where the work or the answer is. The table would say `closed` and `partial`
   are not states. The three `g1a-and-landing-work` tickets that say `closed` would change to
   `resolved`.
   Recommendation: yes.
   Why: that file is where a session is sent for status strings, and 67 of 92 tickets on
   origin/main carry a status it does not list.
   Answer: yes, pre-accepted by Adam, 2026-10-15
9. **Is overnight ticket 66 resolved now, rather than kept open as the parent of overnight
   tickets 69 and 70?** It would be set to `Status: resolved`, recorded under `## Answer`.
   Recommendation: yes.
   Why: everything it proposed landed in `37fdca2`, and the six tickets it was broken into are
   resolved. After this ticket's questions 1 to 6, every Out of Scope item has a home or is
   declined.
   Answer: yes, pre-accepted by Adam, 2026-10-15
10. **Should `docs/agents/issue-tracker.md` → Conventions name the two ticket relations the tree
    already writes, with no "parent" between tickets?**
    - A `**Source:**` line would mean "part of that spec". The source resolves when every ticket
      naming it is settled, and its Answer lists them and anything declined.
    - "raised by" in a `Task:` line would mean a follow-up, which holds nothing open.

    Recommendation: yes.
    Why: otherwise the next session that splits a spec asks cross-reference-checks ticket 09's
    question again. "Parent" already means a plan row in `tools/roadmap.py`.
    Answer: yes, pre-accepted by Adam, 2026-10-15
11. **Should ticket 61's Comments line "62 to 65 are taken, so 66 is the next free number" stay
    as written?**
    Recommendation: yes.
    Why: it is a dated note and was true when written (this ticket's question 2).
    Answer: yes, pre-accepted by Adam, 2026-10-15
12. **Should the morning session apply all of this in one integrator tracker commit, with the
    steering check green before the push?** In order, the commit would:
    1. file the tickets from this ticket's questions 4, 5 and 7;
    2. tick cross-reference-checks ticket 06's box;
    3. replace this ticket's "remain future work" sentence with the one in round 3;
    4. set `Status: resolved` and add the draft Answer from round 3;
    5. resolve cross-reference-checks ticket 09, pointing here;
    6. add the lines from this ticket's questions 2, 8 and 10.

    Recommendation: yes.
    Why: the Answer cites the new tickets by number, so they have to land in the same push. One
    commit also leaves no state in which this ticket and the tree disagree.
    Answer: yes, pre-accepted by Adam, 2026-10-15

## Proposed glossary / ADR

**Glossary terms.** These are proposed and not written to `GLOSSARY.md`. They would go under
"Work, gates and evidence".

- **Task**: one row of the plan's dispatch table, named by its id (`2.4`, `I-6`), or a split of
  one (`1.4a`), which never lands the row it was split from. A task is done when its tests pass in
  CI, it touched only its own paths and it left no change request open: merged is not done.
  _Avoid_: ticket, for a task.
- **Ticket**: one numbered file in a tracker, holding a spec or a question and a status. A ticket
  may carry out a task, and names it on its `Task:` line; a ticket with `Task: none` has no plan
  row to land. _Avoid_: task, for a ticket; issue, outside the directory name.
- **Tracker**: one `.scratch/<slug>/issues/` directory, which numbers its tickets from `01` on its
  own. `overnight` is the plan-wide tracker. A ticket number names a ticket only together with its
  tracker. _Avoid_: feature directory, for the numbering scope; "the tracker", for all of
  `.scratch/`.
- **Source ticket**: the ticket a ticket was broken out of, named on its `Source:` line. It is
  resolved once every ticket broken out of it is settled. _Avoid_: parent, which is what a plan
  row is to its split ids.
- **Follow-up**: a ticket raised by another ticket's work, review or out-of-scope list. It names
  where it came from and never holds that ticket open. _Avoid_: child, sub-ticket.

Three terms were considered and not proposed:
- **Resolved** is a label string, and its home is `docs/agents/triage-labels.md` (Q8).
- **Dated record** appears once in the tree, in
  `contracts/changes/6.4-request-telemetry-feature.md`. Q2 settles it as a rule rather than a term.
- **Freeze** is defined in `contracts/README.md` but missing from `GLOSSARY.md`. That is a real
  gap, but no decision here turns on it.

**ADR candidates: none.** Each decision fails at least one of the three criteria.
- **Q1, Q2 and Q10** decide how a ticket closes and which of its lines must stay true. They are
  real trade-offs and a little surprising, but one line in `docs/agents/issue-tracker.md` reverses
  any of them, so they are not hard to reverse.
- **Q4 and Q7** apply ADR-0018 decision 4, which already records the trade-off. They complete
  that ADR rather than contradict it.
- **Q8** is vocabulary, reversible in one edit.
- **Q5** leaves its decision to the new ticket. If that ticket keeps the import in every session
  and exempts it from the steering check, that deliberate deviation may earn an ADR then.
