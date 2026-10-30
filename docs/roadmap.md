# The gateway, from mechanism to mature release

The gateway is the product. One ASP.NET Core 10 process serves every SPA: it resolves the app from
the request host, holds that app's OAuth client and session, and is the only thing in the platform
that handles a browser request. Everything else in this repository exists to prove something about
it — the Terraform to run it somewhere real, the fixtures to give it traffic to be wrong about, the
suites to catch it being wrong, the test identity provider to make it fail in ways a real provider
will not fail on demand.

That is not how the plan is organised. `docs/plan/` is cut by workstream, because six agents work in
parallel and each needs to know what it owns. This file cuts the same work the other way: along the
gateway's own capability ladder, from "the process starts" to a release an enterprise could adopt.
It adds no work. It names the order the gateway's capabilities arrive in, what each one is proved
by, and — at the top of the ladder, where the plan thins out — what a mature release still needs
that nothing has yet written down.

Owner: integrator. The plan is the plan; where this file and `docs/plan/` disagree about a task, the
plan is right and this file is stale.

## Where the gateway stands

Derived, never typed. `tools/roadmap.py` writes the block below from `git tag`, from the merge
subjects whose form `docs/agents/landing-work.md` fixes, and from the plan's own task tables. Run it
after landing a row; `--check` runs in `.githooks/pre-push` and in CI. A program reads the same
derivation with `--json`: the closed gates, and each plan row as landed, part-landed, open or
ongoing.

<!-- BEGIN GENERATED stages -->
| Stage | Gate | Plan rows landed | What the stage still owes |
| --- | --- | --- | --- |
| S1 It boots, and refuses a host it does not know | G1a, tagged `skeleton-v1` | 5 of 5 | nothing |
| S2 The mechanism holds on the shipped parts | G0, tagged `g0-mechanism` | 6 of 6 | nothing |
| S3 The identity surface is frozen | G1b, tagged `contracts-v1` | 1 of 1 | nothing |
| S4 Feature complete, hermetically | G2, tagged `g2-local-slice` | 29 of 29 | nothing |
| S5 The same gateway on real infrastructure | G3, tagged `g3-deployed-slice` | 2 of 9 | `6.9`; part-landed: `1.3`, `1.4`, `1.5`, `1.6b`, `1.7`, `6.5` |
| S6 Hardened: every attempt is refused | G4, tagged `g4-negative-green` | 0 of 1 | part-landed: `6.3` |
| S7 Operable, and accepted for Phase 1 | G5, tagged `g5-phase-1-accepted` | 0 of 1 | part-landed: `6.7` |
| S8 Continuous: it survives its own deployment | G6, not tagged | 4 of 5 | part-landed: `P2-4` |
| S9 Portable: a second provider behind the same seam | G7, not tagged | 0 of 1 | `P2-6` |
| S10 Mature: bounded, assured and supported | G8, not tagged | 0 of 4 | `P2-7`, `P2-8`, `P2-9`, `P2-10` |
| SX Throughout | none | ongoing | — |
<!-- END GENERATED stages -->

A row is **landed** when a merge subject names it, **part-landed** when a merge says "part of" or
when a split of it merged on its own. Landed is not done: a row is done when its tests pass in CI,
it touched only its owner's paths and it leaves no change request open, and only the row's owner
can say that. Treat the block as the floor, not the ceiling, and `git log --oneline` as the detail.

## Three words, defined

The brief these stages serve asks for a gateway that is feature complete, hardened and mature. Each
word needs a test, or it is a mood.

**Feature complete** means every entry point in `contracts/pipeline.md` is filled by its owner and
nothing is a stub: nine of the ten middleware stages, since routing and authentication are the framework's own, plus
six service registrations and three endpoint maps. The proof is the hermetic functional suite,
F-01 to F-06 and F-08 to F-11, against the real composition. That is stage S4, gate G2. Feature
complete says nothing about whether the features are right.

**Hardened** means every attempt in the negative suite is refused, in both tiers, and every threat
T1 to T18 in `docs/plan/01-plan.md` either has a passing refusal or a written acceptance. The proof
is N-01 to N-27, plus the invariants held outside the gateway's own code — the task role, the
security group, the bucket policy — which are checked by P-03, P-06 and P-04 because a gateway
cannot be trusted to prove things about itself. That is stage S6, gate G4.

**Mature** is the word the plan covers least, and it is not a longer list of features. It means the
gateway survives its own operation: more than one of it runs, a deployment does not sign everyone
out, its limits are measured rather than assumed, its failures are catalogued, its dependencies are
current, and someone can support it from its logs. Stages S8 to S10 carry most of that. The gaps
section below carries the rest, because the plan does not yet name it.

## The gateway's surface, and what fills it

Feature complete is a per-stage question, so here is the ladder stage that fills each stage of the
request path. The order is `contracts/pipeline.md`, frozen at G1a and changed once at G1b
(ADR-0018), and it is security-relevant;
the owner column is that contract's, not this file's.

| Request path | Entry point | Owner | Filled at |
| --- | --- | --- | --- |
| 1 Telemetry | `UsePlatformTelemetry` | 6 | S4 |
| 2 Forwarded headers and the method allowlist | `UsePlatformForwardedHeaders` | 4 | S4 |
| 3 Health | `UsePlatformHealth` | 2 | S4 |
| 4 Host resolution | `UsePlatformHostResolution` | 2 | S4 |
| 5 Security headers and CSP | `UsePlatformSecurityHeaders` | 4 | S4 |
| 6 Request gate | `UsePlatformRequestGate` | 4 | S4 |
| 7 Routing and authentication | the framework's own, on workstream 3's schemes | 3 | S4, behind S3 |
| 8 Shell gate | `UsePlatformShellGate` | 3 | S4, behind S3 |
| 9 Antiforgery | `UsePlatformAntiforgery` | 4 | S4 |
| 10 Endpoints | `MapPlatformBff`, `MapPlatformProxy`, `MapPlatformAssets` | 3, 4, 2 | S4, `/bff` behind S3 |

Two things follow from the table. The gateway reaches feature complete in **one** stage, S4, because
every entry point is independent of the others once the contracts are frozen — that is what the
parallel workstreams buy. And the identity half of S4 cannot start until S3 closes, which is why the
critical path runs through G0 and G1b and why a gate about a library's behaviour sits two stages
below a gate about a feature.

## The ladder

Ten stages and one lane that runs across them. Each stage ends at a gate from
`docs/plan/01-plan.md`, and a gate closes as an annotated tag — the tag column gives the name the
tag takes, and `tools/roadmap.py` reads this table to tell a closed gate from an open one. Only
`skeleton-v1` and `contracts-v1` are named by the plan; the rest are named here so that closure is
detectable, and renaming one means editing this table, which is the only place the name lives.

| Stage | What the gateway can do that it could not before | Gate | Tag |
| --- | --- | --- | --- |
| S1 | Start, and refuse every host it has no entry for | G1a | `skeleton-v1` |
| S2 | Nothing yet: the framework and the libraries are shown able to do it | G0 | `g0-mechanism` |
| S3 | Nothing yet: the shapes identity is built on stop moving | G1b | `contracts-v1` |
| S4 | The whole job, in process, against doubles | G2 | `g2-local-slice` |
| S5 | The whole job on real infrastructure, against a real provider | G3 | `g3-deployed-slice` |
| S6 | Refuse every attempt in the negative suite, in both tiers | G4 | `g4-negative-green` |
| S7 | Take on a new app while serving, with no restart and no new resource | G5 | `g5-phase-1-accepted` |
| S8 | Run as more than one task, and survive its own deployment | G6 | `g6-continuity` |
| S9 | Work against a second identity provider, unchanged | G7 | `g7-pingfederate` |
| S10 | Be adopted: assured, bounded, cell-isolated, supportable | G8 | `g8-enterprise-ready` |
| SX | — | none | — |

### S1 It boots, and refuses a host it does not know

The gateway exists as a process with a frozen composition root. `Program.cs` calls every entry point
in the pipeline order and nothing else; each one is a stub in its owner's project. The capability is
small and the constraint is large: after this, the order of the request path is a contract, and a
workstream that wants a stage moved files a change request instead of moving it.

Proved by the gate's exit test, which `tools/smoke.sh` holds so that a local run and CI run the same
probes: the solution builds, the gateway starts, and an unknown host gets 404. Nothing in the test
suite proves that, because the suite never starts the process — a composition root that throws and a
settings file the binder rejects both build and test clean, and both have happened here.

**Plan rows.** `I-1`, `I-2`, `I-3`, `I-4`, `I-5`

**What it buys the gateway.** Every other workstream can build against `Platform.Testing` instead of
against each other, and no stage can quietly change where it sits in the pipeline.

### S2 The mechanism holds on the shipped parts

Not a gateway capability — a licence to build one. Three questions decide whether the architecture
is possible at all: can the shipped OpenID Connect handler resolve a client per host through a
replaced scheme provider, does the token-management library refresh under derived schemes, and does
YARP 2.3 proxy on .NET 10. All three passed on the shipped packages. What remains is Cognito, which
is the one part of the mechanism no harness can stand in for: its logout is not standard OpenID
Connect, its refresh rotation has a grace window the design leans on, and its managed login has to
accept a client Terraform created.

Proved by the five items under the feasibility gate in `docs/plan/01-plan.md`, which also name the
way out of each failure. If items 1 or 2 had failed, identity work would stop and the integrator
would choose between Finbuckle's per-tenant options, buying Duende BFF v4, and a dedicated
tenant-aware client stack. That branch is still live for the Cognito items.

**Plan rows.** `1.0`, `1.6a`, `1.1`, `1.8`, `5.5`, `3.0`

**What it buys the gateway.** The right to write `src/Platform.Identity` at all. `AGENTS.md` forbids
starting it before G1b, and G1b cannot close until this reports.

### S3 The identity surface is frozen

The records and interfaces the identity work builds on stop moving: the scheme names, the session
record, `IAccessTokenSource`, `IIdpProfile`. I-6 removes the `[Freeze("G1b")]` draft markers from
`src/Platform.Abstractions` and marks the schema parts frozen. The annotated `contracts-v1` tag
records closure; `contracts/README.md` lists the affected parts and the tag requirement. Freezing
is the capability: four workstreams can then build against one shape.

Proved by the tag. The gate's content is judgement — whether G0's findings are folded in correctly —
so the evidence is the ADR that records them, not an assertion.

**Plan rows.** `I-6`

**What it buys the gateway.** Workstream 3's nine rows and workstream 4's token attachment open at
once, which is the widest single unblocking on the critical path.

### S4 Feature complete, hermetically

The gateway does the whole job, in process, against doubles: two SPAs log in with their own clients,
get their own shells from their own S3 prefixes, call their own APIs with tokens that refresh
underneath them, and log out. Every entry point in the pipeline contract is filled. Every rule the
contracts describe has a passing and a failing case in its owner's test project.

Proved by F-01 to F-06 and F-08 to F-11 hermetically, plus the architecture checks A-01 to A-05.
F-07, onboarding, waits for S7 because it is an acceptance criterion about operations rather than
about the gateway's code.

**Plan rows.** `2.1`, `2.2`, `2.3`, `2.4`, `2.5`, `2.6`, `3.1`, `3.2`, `3.3`, `3.4`, `3.5`, `3.6`,
`3.7`, `3.8`, `3.9`, `4.1`, `4.2`, `4.3`, `4.4`, `4.5`, `4.6`, `5.1`, `5.2`, `5.3`, `5.4`, `6.1`,
`6.2`, `6.4`, `6.6`

**What it buys the gateway.** This is the stage where "feature complete" becomes true, and the only
stage where the gateway's own code is most of the work. Note what supports it: `5.4`, a hand-written
test identity provider, exists so the suites can rotate a signing key, refuse a refresh and delay a
response, which no real provider will do on request; `5.3`, a mock API that shares no code with the
gateway, exists so that criterion 7 — the backend verifies the client itself — is proved by
something that cannot be proved by agreement; `6.1`, the harness, exists so that every later gate
runs the real composition rather than a test double of it.

### S5 The same gateway on real infrastructure

The same binary, behind an internal ALB, in a Fargate task, reading a real bucket and real Parameter
Store, against a real Cognito pool, reached over a Session Manager tunnel. Nothing about the gateway
changes here. What changes is that the invariants it cannot hold for itself become real: the task
security group, the task role, the bucket policy that makes a release immutable, the plan allowlist
that keeps onboarding from creating runtime resources.

Proved by F-01 to F-06 and F-08 deployed, and by P-01 to P-06. P-05 tears the stack down, so the
stage ends with a second deployment from nothing — which also proves the first was repeatable.

**Plan rows.** `1.2`, `1.3`, `1.4`, `1.5`, `1.6b`, `1.7`, `1.9`, `6.5`, `6.9`

**What it buys the gateway.** The difference between "the logic is right" and "it runs". Row `6.9`
spans this stage and the two after it: it is the one row that carries G3, G4 and G5, counted here
because this is where it starts.

### S6 Hardened: every attempt is refused

Twenty-seven attempts, each of which must fail: a session cookie presented to the wrong app, a
callback replayed on a sibling, a code issued to another client, a write with no CSRF token, a
cross-site fetch, a spoofed host, a steered proxy, an escape path on an asset, a poisoned registry,
an open redirect, a token meant for another API, a planted session key, a sibling planting login
cookies in a real browser, and a framework default that signs a user out over GET. Each refusal
carries a rule id in the log, so a rejection is observable rather than merely absent.

Proved by N-01 to N-25 and N-27 hermetically, with the deployed subset adding N-26. Two of those
rows rest on the browser enforcing the `__Host-` prefix, so their deciding run is a real browser;
the hermetic client enforces no prefixes and says so.

**Plan rows.** `6.3`

**What it buys the gateway.** This stage has one plan row because the refusals themselves were built
in S4 by the workstream that owns each rule. What S6 adds is an adversary who does not own the code:
one suite, written against the whole composition, whose job is to get through. A rule proved only by
its author is a rule proved by agreement.

### S7 Operable, and accepted for Phase 1

A third SPA is onboarded while the first two are in use: one Cognito client, one branding style, one
parameter, one registry change. No restart, no new runtime resource, no deployment. The task id and
task definition revision are the same before and after. Every operation an operator will actually
perform — deploy, roll back, switch a release, roll back the registry, rotate a secret, end every
session for an app, tear down — exists as a command sequence that has been run.

Proved by F-07 in both tiers and the seven acceptance criteria. Phase 1 claims nothing about
availability here, and says so: one task, in-memory sessions, so every deployment signs users out.

**Plan rows.** `6.7`
**What it buys the gateway.** The architecture's central claim, that an app is only a registry
entry, a release prefix and an OAuth client. Everything before this stage is evidence for it.

### S8 Continuous: it survives its own deployment

The first stage past the proof of concept, and the one that turns the gateway from a demonstration
into something that can be run. Sessions move to DynamoDB, data-protection keys to Parameter Store
under a KMS key, and refresh becomes one grant per session across tasks rather than per process. Two
or more tasks run in private subnets with no public address. Onboarding waits until every task
reports the new revision, so a login that starts on one task can finish on another.

Proved by the store contract suite passing against the durable store, by a cookie issued on one task
being accepted on another, by fifty parallel calls across two tasks causing one grant, and by a
rolling deployment and a killed task costing no session and no login.

**Plan rows.** `P2-1`, `P2-2`, `P2-3`, `P2-4`, `P2-5`

**What it buys the gateway.** Everything in the Phase 1 gap register's top half closes here:
availability, session durability, key durability, revocation across tasks. Until it does, the honest
description of the gateway is "one replica, in-memory, restarts sign everyone out", which is what
`docs/plan/06-phase-2-and-risks.md` says and what nobody should read past.

### S9 Portable: a second provider behind the same seam

PingFederate behind `IIdpProfile`, with the decisions a real enterprise provider forces: `form_post`
or not, pushed authorization, `private_key_jwt` in place of per-app client secrets, audience, logout
and back-channel logout. The functional and negative suites run against it unchanged.

Proved by both suites passing against a second provider. That is the only proof that the profile
seam is a seam: a seam with one implementation is a guess. ADR-0011 chose the seam so that
PingFederate would be a profile rather than a redesign, and this stage is where that claim is
tested.

**Plan rows.** `P2-6`

**What it buys the gateway.** Two of the plan's standing hazards become decisions instead. The
authorization code in the URL (T13, accepted in Phase 1 because Cognito documents no `form_post`)
and one client secret per app (which `private_key_jwt` would remove outright) are both provider
properties, not gateway properties.

### S10 Mature: bounded, assured and supported

Corporate certificates, the enterprise secret store, telemetry and audit into the security tooling,
deployment approvals, per-team publishing rights, registry changes by reviewed pull request. Cells
instantiated per risk class, so a compromise drill in one reads nothing from another. ECS Exec gone,
a read-only root filesystem, a non-root user, an egress allowlist. A threat model review, a
penetration test, recovery objectives, rehearsed runbooks and a load test.

Proved per control, with evidence recorded, plus findings from the review and the test either closed
or accepted in writing. "Accepted in writing" is doing real work in that sentence: a mature release
is not one with no open risks but one whose open risks are named, owned and dated.

**Plan rows.** `P2-7`, `P2-8`, `P2-9`, `P2-10`

**What it buys the gateway.** Adoption by a team that did not build it. The gaps below are the part
of that this stage does not yet cover.

### SX Throughout
Two rows belong to no stage because they run across all of them: the integrator reviewing each gate
and deciding change requests, and the gap register kept current from findings. They are listed so
the ladder's coverage of the plan is complete rather than nearly complete.

**Plan rows.** `I-7`, `6.8`

## What a mature release still needs, and nothing has written down

Ten gaps, found by reading the plan against the question "could another team adopt this". Each is a
gap in the **written plan**, not a task: `docs/plan/` is a frozen export and never edited in place,
so nothing here is committed work until it is an ADR, a row in a fresh export, or a change request,
an issue labelled `change-request` that the human decides in a decision comment
(`docs/agents/tracker-policy.md`, "Change requests"). Each names where it would land.

| # | Gap | Where it lands |
| --- | --- | --- |
| MR-1 | No gateway version, and no rule for which registry documents a build accepts | An ADR, then the registry contract |
| MR-2 | Start-up and shut-down under load are undefined | S8, with the multi-task work |
| MR-3 | No performance budget and no capacity model, so the load test has no pass mark | S10, as the load test's input |
| MR-4 | No upstream timeout, no per-app bound, no trigger for rate limiting | The proxy and the request gate |
| MR-5 | No catalogue of what the gateway does when a dependency is down | The contracts, and the negative suite |
| MR-6 | The log and metric schema is not yet a contract a supporter can rely on | `contracts/telemetry.md` |
| MR-7 | Package advisories only warn, and no cadence moves the pins | The build properties, and an ADR |
| MR-8 | Platform configuration is not validated at start-up | Hosting, and the smoke test |
| MR-9 | A rollback across a session-record change is undefined | S8, with the durable store |
| MR-10 | No support statement and no service objectives, so "mature" has no measure | S10 |

**MR-1. The gateway has no version, and nothing says which registry documents a build accepts.**
`registry.schema.json` carries `schemaVersion`, and its description commits to the right rule: a
later shape gets a later number, never a changed meaning. The gateway side of that rule is missing.
Deployment pushes by digest, so a build is identified by a hash and nothing else; there is no
version to put in a log line, a header or a runbook. The sharp edge is S8, where a rolling
deployment runs two builds against one registry document: nothing states that both accept it, and
no test covers an old build reading a new document or the reverse. ADR-0019 settles the narrow half: a build accepts the `schemaVersion` its schema names, and refusing a value no build could apply is not a new version. Until it does, a rolling
deployment during an onboarding is a hope.

**MR-2. Start-up and shut-down under load are undefined.** `/readyz` gates the ALB on the first
valid snapshot, which is the start-up half. The shut-down half is blank: no drain timeout, no
deregistration delay, nothing about an in-flight request at SIGTERM, and nothing about a login whose
correlation cookie and PKCE verifier live in a task that is going away. Phase 1 accepts a sign-out
on deployment, so the gap is invisible there. S8's acceptance says a rolling deployment costs no
session and no failed login, which cannot be true unless this is specified and tested.

**MR-3. There is no performance budget, so P2-10's load test has no pass mark.** Risk 11 names the
hazard — hundreds of apps straining options memory, secret preload and registry size — and the
spike ran 303 apps, which is a data point, not a budget. A mature release states numbers it will be
held to: requests per second per task at 0.25 vCPU, p99 for an asset and for an API call, bytes of
options and session per app, the registry document's ceiling, and the cold-start time at N apps.
Without them, "load test" is an activity rather than a gate.

**MR-4. Nothing bounds a slow upstream, a busy app or a login storm.** Row 4.6 gives request limits
and a method allowlist, which bounds the client. The other direction is open: no upstream connect
or response timeout is named in the contracts, no per-app concurrency limit exists, and rate
limiting is on the deferred list with "no abuse case yet" and no trigger that would change that.
One app's slow API can hold the task's connections, and the plan's own working notes already
flagged the proxy's timeout ceiling as unproven. A deferral is fine; a deferral with no condition
attached is how a limit never gets written.

**MR-5. What the gateway does when a dependency is down is nowhere written.** F-09 covers a bad
registry document, which is the easy case. Missing: S3 unavailable while serving assets, Parameter
Store unavailable during a rotation, the provider's discovery endpoint down at a cold start, the
session store at the capacity bound row 3.4 gives it, and an upstream that times out or returns 5xx.
Each needs one line — the status, the body, the log, whether the snapshot stays live — and one test.
A dependency failure the code handles by accident is a dependency failure that will be handled
differently next release.

**MR-6. The log and metric schema is not yet a contract.** `contracts/telemetry.md` exists and the
redaction rule is strong — N-16 searches every line for canaries. What a supporter needs is the
other half: a request line whose fields do not move, every refusal carrying its rule id, a naming
rule for meters and activity sources, and a correlation id that appears both in the problem-details
body the user sees and in the log line. Row `6.4` lands the line and the redaction; nothing yet
makes the line's shape something a supporter may depend on, and the first attempt at the row raised
that from inside as a change request.

**MR-7. Package advisories warn, and nothing moves the pins.** Warnings are errors here, with
advisories as the stated exception. For a proof of concept that is the right trade; for a mature
release it is backwards, and the fix needs three decisions: which severity fails the build, how long
a known advisory may stay open, and what cadence moves `Directory.Packages.props` between .NET
patch releases — .NET 10 is supported until November 2028, so the question is cadence, not support.
P2-9 says "scanner and policy checks pass" without naming a scanner, a threshold or an SBOM.

**MR-8. The gateway does not validate its own configuration at start-up.** There is a schema for
platform configuration. Nothing says the process reads it, validates against it and refuses to serve
when it fails, which is why the smoke test exists at all: a settings file the binder rejects builds
and tests clean. A mature release fails loudly at start-up rather than per-request, and the cheapest
version of that is validating platform configuration against its own schema in `AddPlatformHosting`
and letting the process die.

**MR-9. A rollback across a session-record change is undefined.** `SessionRecord` carries a
`Version`, which is for optimistic replacement, not for the record's shape. Phase 1 is safe by
accident: sessions are in memory, so a rollback loses them all anyway. In S8 they are durable, and
rolling back the image then means an older build reading records a newer one wrote. That needs a
format version on the record, a rule that one release reads the previous release's shape, and a
rollback test with live sessions. Rollback that has only ever been tested on an empty store is not a
rollback.

**MR-10. There is no support statement and no service objectives.** The plan is unusually honest
about what Phase 1 does not show, and the gap register is the right instrument. What is missing is
the positive statement a team adopting the gateway reads first: which browsers are supported
(assumption A2 leans hard on current managed ones), which providers, how many apps per cell, which
upstream shapes are out of scope — WebSockets and server-sent events are deferred, which an adopter
will discover by trying — and what availability, latency and recovery objectives the platform will
be measured against. Without objectives, S10's assurance work has nothing to assure against.

## How this file stays true

Three mechanisms, because a roadmap is the document most likely to be quietly wrong.

- **The state is derived.** `tools/roadmap.py` writes the generated block from tags, merge subjects
  and the plan. `--check` fails a push whose block is out of date, so the push that lands a row is
  the push that updates it.
- **The ladder is checked against the plan.** The same script fails if a plan row is in no stage, in
  two stages, or named here but absent from the plan. The ladder cannot read as coverage it does not
  have, and a fresh plan export that adds a row fails the check until this file places it.
- **The prose is not checked.** Every sentence outside the generated block is a judgement that can go
  stale without a check noticing. When a gate's content changes, this file is the second thing to
  edit, after the ADR that records the change.
