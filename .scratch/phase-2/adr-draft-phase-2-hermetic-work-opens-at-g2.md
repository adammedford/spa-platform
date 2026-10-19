# ADR draft: A Phase 2 package's hermetic half opens at G2; its deployed proof and gates G6 to G8 wait for G5

- **Status:** Draft, for the integrator's decision. Carries no number until committed
  (`docs/adr/README.md`); it lands under `docs/adr/` with an `Authority:` trailer naming the Phase 2
  spec issue (to be filed).
- **Date:** 2026-10-19
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Gates, G5's "Unblocks: Phase 2", and the sentence under the
  table that adds G6, G7 and G8; `docs/plan/06-phase-2-and-risks.md` → the package table, P2-1 to
  P2-10, with its "Accepted when" column; `docs/roadmap.md` → S8 to S10; tasks 6.2, 6.5 and 6.9,
  whose rows already split a hermetic half from a deployed one; ADR-0004, ADR-0011 and ADR-0012,
  which each name what Phase 2 changes behind a seam Phase 1 built. `docs/plan/` is a frozen export;
  this ADR records how its one sentence on Phase 2 is read.

## Context

The gates table in `docs/plan/01-plan.md` ends at G5, whose last column reads "Unblocks: Phase 2",
and the sentence under it adds G6 for multi-task continuity, G7 for PingFederate and G8 for
enterprise readiness. `docs/plan/06-phase-2-and-risks.md` defines the ten packages, each with a gate
and an "Accepted when" cell, and says each "ends in a test, not a review". Neither says whether
"Unblocks" opens the writing of a package or only the running of its gate. The column reads both
ways in the same table: G1a unblocks "every task that does not touch identity", which is code, and
G2 unblocks "the first gateway deployment", which is an act of task 6.9, the row the dispatch order
opens at G2 and that carries G3, G4 and G5, while workstream 1's Terraform rows, which build what it
deploys, opened "before G1a". So the plan's own practice for Phase 1 is that code opens early and
the proof that is a gate waits. Task 6.5's browser suite is "written against a local gateway before
G3" and "passes locally, apart from what only AWS can show". Task 6.2 is "green at G2, except F-07
at G5": F-07 has a hermetic test today,
`tests/Gateway.IntegrationTests/Functional/OnboardingTests.cs`, and the `g2-local-slice` tag says
"F-07's deployed half remains for G5".

On 2026-10-19, `origin/main` at `ce9895e` carries four gate tags: `skeleton-v1`, `g0-mechanism`,
`contracts-v1` and `g2-local-slice`. The roadmap's generated block reads S4 as 29 of 29 rows landed,
S5 as 1 of 9, S6 and S7 as 0 of 1 each, and S8 to S10 as 0 of 10 between them; the README's summary
reads 42 of 62 plan rows landed, 6 part-landed. `src/Gateway/Program.cs` calls every entry point in
`contracts/pipeline.md`, and no stub remains. The gateway is feature complete, hermetically, as
`docs/roadmap.md` → "Three words, defined" defines the term.

What stands between G2 and G5 is a chain of tickets, each needing a sitting in AWS: #125, a manual
code-flow login that re-proves 1.8, blocks #55 (1.3, the ALB), which blocks #56 (1.4, the service),
which blocks #57 (1.6b, the tunnel) and #58 (1.7, the scripts), which block #63 (6.9, the deployed
runs that close G3, G4 and G5), which blocks #62 (6.7, the operating notes). Each is an apply or a
read-back in the project, which CI cannot do (ADR-0016: no AWS credential in CI) and which
`docs/agents/aws-rules.md` bounds to one Region under a spend limit. The overnight run cannot
shorten that chain.

The run of 2026-10-19 (#129, landed as `ca14a44`) showed what the chain leaves the C# lanes. The run
takes its work from the open `ready-for-agent` issues (`ce9895e`'s body: 43 of them that night). It
dispatched the open C# plan rows and found their hermetic halves already done: the lane for 6.3
(#60) found 123 of 123 negative tests passing, the lane for 6.5 (#61) found 34 of 34 browser tests
passing, and the lane for 2.1b (#64) found its work done. None of the three made a commit; #60 and
#61 stay open only for the deployed run and the AWS-only clauses, and #64 was closed when the run
landed. Twelve tickets landed complete and two in part, all tooling, guides and Terraform tests but
one proxy test. P2-1 to P2-10 have no issue, so no lane can take them, and the C# tickets filed
since (#140, the stage-host spec, with its sub-issues and its change request ticket #143) are
test-infrastructure refactors, not plan rows. Read against the chain, the lanes idle until the
sittings happen, on a codebase whose next body of C# work the plan already names.

Phase 1 was built expecting that work. `ISessionStore` says sessions are "behind the same interface
in Phase 2", and `ReplaceAsync` takes an expected version because "the Phase 2 store needs it for
optimistic concurrency, and having it now means Phase 2 changes no caller"
(`src/Platform.Abstractions/ISessionStore.cs`, lines 25 and 38).
`Platform.Testing.SessionStoreContract` is the suite "the Phase 2 store must pass" (task 3.4;
`src/Platform.Testing/SessionStoreContract.cs`, line 7), and `MemorySessionStore` passes it
"unchanged, as the Phase 2 store must". `SessionGates` says of itself "In process only. Two gateway
tasks share no gate" (`src/Platform.Identity/SessionGates.cs`, line 18).
`contracts/platform-config.schema.json` gives `Session.Store` the enum `["memory", "dynamodb"]`,
"memory in Phase 1, dynamodb in Phase 2", and makes `Platform.Cell` "part of the data-protection
application name" (ADR-0012). ADR-0004 says "Phase 2 moves it to DynamoDB"; ADR-0011 says
"PingFederate becomes a profile, not a redesign". The cell module's `session_store` variable
defaults to `memory` and validates against the schema's enum (`infra/modules/cell/variables.tf`),
and the platform root's plan-policy test lists `aws_dynamodb_table` as "a Phase 1 tripwire, because
Session.Store admits dynamodb only in Phase 2" (`infra/envs/poc/platform/tests/root.tftest.hcl`,
line 807). Two seams are not yet filled on the code side: `MemorySessionStore.FromConfiguration`
refuses any `Session:Store` but `memory` (lines 52 to 57), and no data-protection call exists under
`src/`, so the key ring is the framework's default.

Each package's "Accepted when" reads two ways, and the plan's two tiers (`docs/plan/04-tests.md` →
"How the suites run") name them. P2-1: "the store contract suite passes" is hermetic; "killing a
task loses no session" is deployed. P2-2: "a cookie and a login state issued by one task are
accepted by another" holds for two gateway hosts in one test process sharing one key repository, and
again on the stack. P2-3: "fifty parallel calls split across two tasks cause one refresh grant"
holds for two hosts over one fake store against the test identity provider, and again on the stack.
P2-5: a login that starts on one host and finishes on another during a reload, in a process and then
on the stack. P2-6: "the functional and negative suites pass against PingFederate" runs hermetically
against the test identity provider extended to stand in for it; the real provider is the deployed
proof. P2-4 and P2-8 are Terraform and have one reading, the deployed one. P2-7, P2-9 and P2-10 are
mostly process and infrastructure, with small C# parts.

## Decision

**1. Every Phase 2 package has a hermetic half and a deployed half, and the hermetic half opens at
G2.** The hermetic half is code in its owners' paths, proved in the owner's test project against the
doubles in `Platform.Testing` and again in workstream 6's composed harness, which is how S4 was
built. The deployed half is the same "Accepted when" cell proved on the stack, through the tunnel,
and it is the gate's exit test: the gates table stops at G5, so a Phase 2 gate's exit test is the
deployed reading of its packages' cells, and its tag names them as `g2-local-slice` names F-01 to
F-06 and F-08 to F-11. The deployed halves, and gates G6 to G8 themselves, wait for G5. That is how
"Unblocks: Phase 2" is read: a gate closes a proof, which is what the plan's own Phase 1 rows 6.2,
6.5 and 6.9 already do. `docs/plan/` is a frozen export, so this ADR is the record of the reading.

**2. Phase 1's behaviour does not change until G5.** Every Phase 2 mechanism lands behind platform
configuration whose default is the Phase 1 value: `Session:Store` stays `memory` in
`contracts/platform-config.example.json` and in the cell module's default; no key ring configured
means the framework's current behaviour; the `standard` and `cognito` profiles are not touched. The
binary that #63 deploys for G3, G4 and G5 is the Phase 1 gateway plus dormant code, and the hermetic
tier, F-01 to F-11 and N-01 to N-25 and N-27, stays green at every merge. One guard is not an
accident of dormant code: a test that the example platform configuration selects no Phase 2
mechanism. It belongs in the integrator's `tests/Contracts.Tests`, beside
`Example_document_validates` and `Session_store_is_memory_in_phase_1_and_dynamodb_in_phase_2`,
because the example and the schema are the integrator's files and the commit that adds a Phase 2 key
to the schema is the commit that must extend the guard; workstream 6's architecture tests read
project references, not documents. The test's id is the integrator's to assign. The deployed side of
the same guard exists already: the cell module's default and its validation, and P-01's tripwire on
`aws_dynamodb_table`.

**3. The work is filed as specs and tickets, and a row lands only with its deployed proof.** One
spec issue per gate's body of work: G6's five packages as one spec with sub-issues per owner, P2-6
as its own spec, and the C# parts of G8 as follow-ups whose `Raised by:` line names the Phase 2 spec
issue (to be filed). The owners follow the code: the store and the lease are workstream 3's, as
`MemorySessionStore` and `SessionGates` are; the readiness body is workstream 2's, as
`UsePlatformHealth` is; the provider's stand-in is workstream 5's; the harness and the composed
proofs are workstream 6's; the key ring's owner is decided with its change request, since nothing
configures data protection today. A hermetic ticket carries `ready-for-agent` and the hermetic
reading as its acceptance list. A deployed ticket carries the deployed reading, is blocked by #63,
and by P2-4's deployed ticket where the proof needs two tasks; it opens to nobody until its blockers
close. Bodies take the shape `docs/agents/issue-tracker.md` fixes: what to build and the acceptance
list, with the record in comments. A merge of a hermetic half uses the subject `Merge part of task
P2-n: ...`, so `tools/roadmap.py` reads the row as part-landed and never as landed before its gate;
the merge that carries the deployed proof uses `Merge task P2-n: ...`. A Phase 2 row is **landed**
only by that merge.

**4. Inside G6, the key ring and the store come first.** P2-2 and P2-1 are proved on their own: P2-1
by `SessionStoreContract` against the DynamoDB store over a fake DynamoDB server, in the style of
`tests/Platform.Registry.Tests/FakeS3Server.cs`; P2-2 by two hosts sharing one key repository. P2-3,
the lease, and P2-5, convergence, are proved over them: one grant across two hosts needs one store,
and a login that finishes on another host needs its login state readable there. The common proof for
P2-2, P2-3 and P2-5 is a harness that starts two gateway hosts over one fake store and one key
repository. `GatewayHarness` starts one today, so that harness is a ticket of its own, workstream
6's, and the three composed proofs are blocked by it. This is a dependency, not a schedule: nothing
here says which lane runs first, only what each proof needs to exist.

**5. A contract change goes by change request, decided before the ticket that needs it opens.**
`Directory.Packages.props`, `src/Platform.Abstractions`, `src/Platform.Testing` and the schemas are
the integrator's. `contracts/README.md` → "What may land between gates" already says what lands on
the integrator's decision, a new type that changes no existing signature, and what waits for a gate.
The `Profile` enum is a part G1b froze, so the request that widens it is the integrator's call under
that rule. A ticket whose code needs a pin, a type or a configuration key is blocked until the
decision is written beside its request, as that page requires. The requests expected are listed
under Consequences.

**6. What does not open.** Nothing applies in AWS: no lane under this decision touches the project.
P2-4 and P2-8 are Terraform and wait for G5 and the sittings; so do the DynamoDB table, the KMS key
and the parameters that P2-1 and P2-2 need on the stack, which are workstream 1's and outside P-01's
allowlist of resource types. P2-7, P2-9 and P2-10 open only for the C# parts their tickets name. The
tags `g6-continuity`, `g7-pingfederate` and `g8-enterprise-ready` are cut only after their exit
tests run on the stack, as `docs/agents/landing-work.md` → "Gates" requires of every gate.

## Alternatives rejected

- **Wait for G5 as written.** Cheapest to govern: nothing to decide, no dormant code in the Phase 1
  binary, and the gates table read literally. Rejected because the C# lanes idle while the AWS chain
  waits on sittings. The run of 2026-10-19 showed it: the three C# plan-row lanes made no commit,
  the night's one C# change was a proxy test, and the chain from #125 to #63 is six tickets long
  with a sitting in each. The plan's Phase 1 practice is already the split this ADR names.
- **Close G3 to G5 without their deployed runs.** Fastest: the hermetic evidence exists, and the
  tags could be cut this week. Rejected because a gate tag is permanent
  (`docs/agents/landing-work.md` → "Gates"; ADR-0022's context records `contracts-v1` still pointing
  at sentences corrected on `main` 34 and 71 minutes after the tag), and the three gates are Phase
  1's claim: "hardened" is defined as the negative suite refused "in both tiers", and G3's exit test
  is F-01 to F-06 and F-08 deployed with P-01 to P-06. A tag cut on hermetic evidence would say
  "deployed" of a binary that never ran on the stack.
- **Build Phase 2 on a long-lived branch outside the dispatch.** Keeps dormant code off `main` and
  the Phase 1 binary exactly Phase 1. Rejected because the overnight run takes tickets, not
  branches, so the branch would be worked by human sessions only; because `AGENTS.md` says to build
  against `Platform.Testing` and not against another branch; and because review debt accumulates
  against a moving `main`: `docs/agents/landing-work.md` → "Review debt from before the check"
  records what five unreviewed merges cost, a gate tag held until a record clears each, and a branch
  months long would arrive as one such range.
- **Edit the gates table in `docs/plan/01-plan.md`.** The reading would sit where a reader looks for
  it. Rejected because the plan is a frozen export whose source of truth is the Claude Doc, never
  edited in place (`docs/adr/README.md`); a decision after the 11 October 2026 export belongs here.
  A later export may carry the reading; until then this ADR is it.
- **Open everything, the Terraform of P2-4 and P2-8 included.** One spec for the whole of G6, with
  no package split between the phases. Rejected because a Terraform package ends in an apply, an
  apply is a sitting under `docs/agents/aws-rules.md` and ADR-0016, and the sittings are the
  resource the AWS chain is already waiting on; opening the Terraform would lengthen the queue this
  decision routes around. P-01's tripwire on `aws_dynamodb_table` makes the table a gate decision in
  any case. Nothing in this decision touches AWS.

## Consequences

- `docs/roadmap.md` is unchanged. P2-1 to P2-10 are already placed in S8 to S10, so the ladder check
  holds; the generated block will show `part-landed: P2-1` and so on under S8 before G5 closes,
  which the paragraph under the block explains. S8's prose, "the first stage past the proof of
  concept", is about the gate and stays true.
- `docs/plan/` is unchanged: a frozen export. This ADR is the live record of the reading.
- `GLOSSARY.md` is unchanged. *Gate*, *Gate tag*, *Landed*, *Hermetic (H)* and *Deployed (D)*
  already say what this decision needs. If the integrator wants the reading visible there, the one
  clause is on *Gate*, that its exit test proves a body of work and does not open the writing of it,
  and the commit would cite the Phase 2 spec issue as its authority (ADR-0023 decision 3).
- `AGENTS.md` is unchanged. Its one progress-independent rule, that nothing in
  `src/Platform.Identity` starts before `contracts-v1`, is met: the tag is on `origin/main`.
- `contracts/README.md` is unchanged; the change requests are its route. Expected under
  `contracts/changes/`, each decided by the integrator: `p2-1-dynamodb-session-store.md` (the
  `AWSSDK.DynamoDBv2` pin, a `Session:TableName` key, a value comparison in the store contract
  suite, and a fake DynamoDB server as the hermetic proof); `p2-2-data-protection-key-ring.md`
  (the `Amazon.AspNetCore.DataProtection.SSM` pin, a `DataProtection` section, and which of the
  six registrations carries the ring); `p2-3-session-lease-abstraction.md` (a lease type in
  `Platform.Abstractions` with its double in `Platform.Testing`, rather than a member on the frozen
  `ISessionStore`); `p2-5-readiness-reports-registry-revision.md` (the `/readyz` body names the
  process that answered, so a wait can cover every task); `p2-6-pingfederate-profile.md`
  (`pingfederate` in the `Profile` enum, `PrivateKeyJwt` in `ClientCredentialStyle`, a `POST`
  callback, and the pushed-authorization, audience, logout and back-channel-logout decisions). Four
  more are raised against the roadmap's gaps with the same spec issue as their authority, since the
  tickets that close them are Phase 2's: `mr-2-shut-down-sequence-in-the-pipeline-contract.md`
  (the start-up and shut-down sequence, with its three numbers),
  `mr-4-upstream-bounds-named-in-the-contract.md` (prose naming what bounds a slow or busy
  upstream), `mr-5-dependency-down-catalogue.md` (a table of what the gateway does when a
  dependency is down, and one `store_unavailable` code) and
  `mr-8-platform-configuration-validated-at-start-up.md` (validation at start-up, and a smoke test
  that supplies a configuration).
- `docs/dependencies.md` and `Directory.Packages.props` gain the pins those requests ask for, on the
  integrator's decision, and nothing before it.
- The tracker gains the Phase 2 spec issue, its sub-issues and the follow-ups, filed by the human or
  on a confirmed answer; no ticket is filed on a pre-accepted answer alone (ADR-0021 decisions 1 and
  6; ADR-0023 decision 4). This ADR lands with that spec issue as its `Authority:`.
- The risk accepted: dormant Phase 2 code, and the packages it references, in the binary G3 to G5
  deploy. Decision 2 holds it to the Phase 1 defaults, the hermetic tier proves those defaults at
  every merge, and the example-configuration guard proves the example selects none of it. What that
  does not cover is a pin carried unused, to which the advisory warnings of MR-7 apply as they do to
  every other pin.
- The tags of G3, G4 and G5 will each cover Phase 2 part-merges in their ranges, since a gate tag
  covers every merge since the previous one. The ordinary rule applies: each such merge's review is
  closed before the tag is cut. This ADR adds no exception.
- The overnight run gains C# work it can take. A lane's code rests on a pre-accepted answer where a
  ticket needs one (ADR-0023 decision 1), and the human lands (ADR-0021 decision 5).

## Evidence

- `git tag --merged origin/main` at `ce9895e`, 2026-10-19: `skeleton-v1`, `g0-mechanism`,
  `contracts-v1`, `g2-local-slice`. The `g2-local-slice` message: F-01 to F-06 and F-08 to F-11
  hermetic, A-01 to A-04 in CI and A-05 in the pre-push hook with its CI regression tests, 1,525 of
  1,525 locally, deciding CI run 39028415993; "No AWS apply,
  deployed evidence, or live provider authentication is claimed"; "F-07's deployed half remains for
  G5".
- `docs/roadmap.md`, generated block: S4 29 of 29; S5 1 of 9, part-landed 1.3, 1.4, 1.5, 1.9 and
  6.5; S6 0 of 1, part-landed 6.3; S7 0 of 1; S8 0 of 5; S9 0 of 1; S10 0 of 4. `README.md`,
  generated summary: 42 of 62 plan rows landed, 6 part-landed.
- `ca14a44`, the 2026-10-19 run (#129): twelve tickets complete and two in part; 1,537 tests on
  `031c6d3`; blast radius "tooling, agent guides, the Terraform tests of three roots, one proxy
  test". #129's report: 12 integrated, 2 partial, 9 skipped; the 6.3 lane (#60) 123 of 123, the 6.5
  lane (#61) 34 of 34, the 2.1b lane (#64) its work done, none with a commit. `ce9895e`'s body: the
  collect reads the `ready-for-agent` issues, 43 that night.
- The chain to G5, as the tracker read on 2026-10-19: #125 blocks #55; #55 blocks #56; #56 blocks
  #57 and #58; both block #63; #63 blocks #62.
- The seams: `src/Platform.Abstractions/ISessionStore.cs` lines 25 to 26 and 38 to 39;
  `src/Platform.Testing/SessionStoreContract.cs` lines 7 to 8;
  `src/Platform.Identity/MemorySessionStore.cs` lines 13 to 14 and 44 to 57, and
  `src/Platform.Identity/PlatformIdentityExtensions.cs` line 94, which registers it;
  `src/Platform.Identity/SessionGates.cs` line 18; `contracts/platform-config.schema.json` lines 17
  to 19 (`Cell`), 65 to 67 (`Profile`) and 132 to 136 (`Store`); `docs/plan/02-contracts.md`, the
  platform configuration table's `Session:Store` row, "`dynamodb` in Phase 2";
  `infra/modules/cell/variables.tf` lines 306 to 321 and `infra/modules/cell/ecs.tf` line 91;
  `infra/envs/poc/platform/tests/root.tftest.hcl` lines 807 to 809. No `AddDataProtection` call
  under `src/` on 2026-10-19.
- The tests the guard would sit beside: `tests/Contracts.Tests/ExampleDocumentTests.cs` line 15 and
  `tests/Contracts.Tests/PlatformConfigurationTests.cs` line 135. The harness that starts one
  gateway: `tests/Gateway.TestHarness/GatewayHarness.cs`, one `GatewayFactory`. F-07's hermetic
  test: `tests/Gateway.IntegrationTests/Functional/OnboardingTests.cs` line 12.
- `tools/roadmap.py`: `ID` accepts `P2-\d+`; `LANDED_SUBJECT` reads "part of" as part-landed; its
  header names the package table of `docs/plan/06-phase-2-and-risks.md` as a source of plan rows.
