# Phase 2 issues: the plan

Drafted 2026-10-19 by the planning session that found the C# lanes idle (#129's lanes for 6.3,
6.5 and 2.1b made no commit). It rests on the ADR draft beside this file and on the change
requests under `contracts/changes/` that the same session drafted. Nothing here is filed, decided
or committed on its own authority: the human files the spec issue, lands the ADR and decides each
request (ADR-0021 decisions 1 and 6, ADR-0023 decisions 3 and 4).

## What was drafted, and where each piece lands

| Draft | Where it is now | Where it lands | How |
| --- | --- | --- | --- |
| ADR draft: a Phase 2 package's hermetic half opens at G2 | `.scratch/phase-2/adr-draft-phase-2-hermetic-work-opens-at-g2.md` | `docs/adr/00NN-phase-2-hermetic-work-opens-at-g2.md` and a row in `docs/adr/README.md` | The session that commits it takes the next free number (`docs/adr/README.md`); the commit carries `Authority: #<spec>` |
| Change requests `p2-*.md` and `mr-*.md` | `contracts/changes/` on this branch, each `Status: open` | The same files, with a `## Decision` section and the status line changed | The integrator decides and edits; editing a change request is a steering change, so that commit also carries the trailer |
| This plan and the spec and ticket bodies | `.scratch/phase-2/` | GitHub Issues | `gh issue create`, per `docs/agents/issue-tracker.md` |

## The landing sequence

Each step needs the one before it, because every steering change cites the spec issue and every
ticket cites a decided request.

1. **File the spec issue.** `gh issue create --title "<title>" --body-file
   .scratch/phase-2/spec-g6-continuity.md`. Its number is the authority for everything below, so it
   goes first. The PingFederate spec is a second issue, filed the same way from
   `spec-p2-6-pingfederate.md`.
2. **Land the ADR.** Move the draft to `docs/adr/00NN-...md` with the next free number, change its
   status line from draft to Accepted, add its row to `docs/adr/README.md`, and commit with
   `Authority: #<spec>` in the last paragraph. `.github/steering.py` holds the push otherwise.
3. **Decide the change requests**, in the order the tickets need them: `p2-2` and `p2-1` first, then
   `p2-3` and `p2-5`, then `p2-6`; the `mr-*` requests whenever. Each decision is a `## Decision`
   section and a status line, as `4.2-access-token-result-provider-unavailable.md` records one, plus
   the integrator edits it asks for (a pin in `Directory.Packages.props`, a row in
   `docs/dependencies.md`, a key in a schema, a type in `src/Platform.Abstractions`, a double in
   `src/Platform.Testing`). A ticket whose code needs a pin or a type is blocked until its request
   is decided (ADR draft, decision 5).
4. **File the tickets** as sub-issues of their spec (`gh issue create --parent <spec>`), each with
   the body drafted under "The tickets" below, with `--add-blocked-by` for every edge in its row,
   and `--label ready-for-agent` on the hermetic ones. A deployed ticket is blocked by #63 and gets
   no label until its blockers close; a ticket that needs a sitting gets `ready-for-human`.
5. **Let the next overnight run take them.** The collect reads open `ready-for-agent` issues with no
   assignee and no open blocker, and sends each to the lane its `**Owner:**` line names. A ticket
   that cannot start until its change request is decided is held by the blocked-by edge, not by a
   label.

## How the tickets are shaped

The body is the spec and the comments are the record (`docs/agents/issue-tracker.md`). Each ticket
body carries, in this order: `**Owner:**` (one of `ws1-infra`, `ws2-hosting`, `ws3-identity`,
`ws4-proxy`, `ws5-fixtures`, `ws6-verification`, `integrator`), `**Plan row:**` (`P2-1` … or
`none`), `## Parent` naming the spec, `## What to build`, and `## Acceptance criteria` as a checkbox
list where every box is a behaviour a test can fail. A ticket touches one owner's paths only
(`OWNERSHIP.md`); what it needs from an integrator path is a change request it names as a blocker.
Merge subjects are `Merge part of task P2-n: ...` for a hermetic half and `Merge task P2-n: ...`
only for the merge that carries the deployed proof (ADR draft, decision 3). What needs AWS goes in
the merge body's `Outstanding:` line, as every Terraform row of Phase 1 did.

## The spec issues

Two specs and one group of follow-ups, as the ADR draft's decision 3 shapes them. Each spec body
is drafted beside this file in the shape of #140 (problem, solution, user stories, implementation
and testing decisions, out of scope, and the ticket graph in Further Notes), so `/to-tickets` or a
hand can cut its tickets from it.

| Spec | Body | Plan rows | Tickets |
| --- | --- | --- | --- |
| G6 continuity: durable sessions, a shared key ring, one refresh across tasks, registry convergence | `spec-g6-continuity.md` | P2-1, P2-2, P2-3, P2-5; MR-2, MR-9 | 26: H-1, P2-1a to P2-1d, P2-2a to P2-2d, P2-3a to P2-3d, P2-5a to P2-5f, MR-2a to MR-2c, MR-9a to MR-9c |
| P2-6 PingFederate: a profile and its hermetic stand-in | `spec-p2-6-pingfederate.md` | P2-6 | 7: P2-6a to P2-6g |
| Follow-ups raised by the G6 spec, no spec of their own | the tables under "The follow-ups" below | MR-4, MR-5, MR-8; P2-7 and P2-9's C# | 9: MR-4a, MR-4b, MR-5a to MR-5c, MR-8a, P2-7a, P2-7b, P2-9a |

The G6 spec is the authority the ADR cites, so it is filed first. The follow-ups carry
`**Raised by:** #<G6 spec>` at the top of each body and are neither sub-issues nor blockers of it
(`docs/agents/issue-tracker.md` → "An issue is held open by its own scope").

## The tickets, per package

The full rows, with paths, what to build and the acceptance boxes, are in the two spec bodies. This
is the index: owner, tier, and what each waits on first.

| Ticket | Owner | Tier | Waits on |
| --- | --- | --- | --- |
| H-1 Two gateway hosts in the harness | ws6 | hermetic | nothing |
| P2-1a The DynamoDB store and its fake | ws3 | hermetic | `p2-1` decided |
| P2-1b The ticket is encrypted in the item | ws3 | hermetic | P2-1a |
| P2-1c Table, key, role | ws1 | needs AWS | P2-1a, the ADR-0016 amendment |
| P2-1d Killing a task loses no session | ws6 | needs AWS | P2-1a to P2-1c, P2-2c, P2-4, #63 |
| P2-2a The ring in Parameter Store | ws2 | hermetic | `p2-2` decided |
| P2-2b A cookie and a login state cross two hosts | ws3 | hermetic | nothing |
| P2-2c Key, prefix, role | ws1 | needs AWS | P2-2a, the ADR-0016 amendment |
| P2-2d Accepted by another task | ws6 | needs AWS | P2-2a, P2-2c, P2-4, #63 |
| P2-3a `ISessionLeases` | integrator | hermetic | `p2-3` decided |
| P2-3b One refresh per session across tasks | ws3 | hermetic | P2-3a, P2-1a |
| P2-3c The composed two-gateway proof | ws6 | hermetic | H-1, P2-3a, P2-3b |
| P2-3d Two real tasks | ws6 | needs AWS | P2-3b, P2-1c, P2-4, #63 |
| P2-5a `/readyz` names its instance | ws2 | hermetic | `p2-5` decided |
| P2-5b `spactl wait --instances` | ws2 | hermetic | `p2-5` decided |
| P2-5c The registry instruments and the swap event | ws2 | hermetic | nothing |
| P2-5d Convergence, composed | ws6 | hermetic | H-1, P2-5a, P2-5b |
| P2-5e The deploy script waits per task | ws1 | needs AWS | #58, P2-4, P2-5b, P2-5c |
| P2-5f The deployed login across tasks | ws6 | needs AWS | P2-1d, P2-2d, P2-4, P2-5e, #63 |
| MR-2a The shutdown timeout | ws2 | hermetic | `mr-2` decided |
| MR-2b The task's drain settings | ws1 | hermetic (`terraform test`) | `mr-2` decided |
| MR-2c Deployment and kill with live sessions | ws6 | needs AWS | P2-1d, P2-2d, P2-4, MR-2a, MR-2b, #63 |
| MR-9a The format rule | integrator | decision | nothing |
| MR-9b The reader honours the rule | ws3 | hermetic | MR-9a, P2-1a |
| MR-9c Rollback with live sessions | ws6, ws1 | needs AWS | MR-9b, P2-1d, #63 |
| P2-6a The stand-in speaks PingFederate's dialect | ws5 | hermetic | nothing |
| P2-6b Decide the P2-6 change request | integrator | decision | nothing |
| P2-6c The pingfederate profile | ws3 | hermetic | P2-6a, P2-6b |
| P2-6d The callback takes a POST | ws4 | hermetic | P2-6b |
| P2-6e The composed suites run under a profile matrix | ws6 | hermetic | P2-6a, P2-6c, P2-6d |
| P2-6f The deployed run against PingFederate | ws6, ws1 | needs a real provider | an instance nobody has; P2-6e |
| P2-6g Back-channel logout | ws3, integrator, ws2, ws4, ws6 | hermetic, then deployed | `p2-6` item 7, `p2-3`, P2-1a, a gate for its rule id |

**What can start on the first night**, with no request to decide and no blocker: H-1 (ws6), P2-2b
(ws3), P2-5c (ws2), P2-6a (ws5), MR-4a (ws4, below), and two integrator decisions, MR-9a and P2-6b.
That is one ticket for each of the five C# lanes.

### The follow-ups

Each is filed with `**Raised by:** #<G6 spec>`; owners, tier and blockers read as in the spec
bodies. The full rows are here because no spec body carries them.

| Ticket | Owner | Paths | What to build | Acceptance | Blocked by | Tier |
| --- | --- | --- | --- | --- | --- | --- |
| MR-4a The connection bound | ws4-proxy | `src/Platform.Proxy`, `tests/Platform.Proxy.Tests` | `HttpClientConfig.MaxConnectionsPerServer` on every generated cluster, a constant with its reason | [ ] One more concurrent call than the bound to a silent upstream is 504 for the last at the entry's timeout; the upstream saw exactly the bound | none; `mr-4-upstream-bounds-named-in-the-contract.md` follows | hermetic |
| MR-4b Composed upstream bounds | ws6-verification | `tests/Gateway.SecurityTests` | The 502, 504 and at-the-bound cases through the composed gateway, read from the contract text | [ ] Each answer matches the Errors row and carries no rule id on its request line | MR-4a, `mr-4` decided | hermetic |
| MR-5a `store_unavailable` | ws2-hosting | `src/Platform.Hosting`, `tests/Platform.Hosting.Tests` | Catch the store's failure in `Assets.cs`; 503 `store_unavailable` with the problem body; one Warning naming the app and the error type | [ ] Against a throwing `IObjectStore` the asset request is 503 with the code, the Warning carries no key content, and a later request with a working store is served | `mr-5-dependency-down-catalogue.md` decided | hermetic |
| MR-5b Discovery down at login | ws3-identity | `src/Platform.Identity`, `tests/Platform.Identity.Tests` | Measure `/bff/login` with discovery unreachable; answer the catalogue's row; log the provider's error without its fields | [ ] The navigation gets the stated page and status. [ ] N-16's canary sweep stays clean | `mr-5` decided | hermetic |
| MR-5c One composed case per row | ws6-verification | `tests/Gateway.SecurityTests`, `tests/Gateway.IntegrationTests` | A throwing store wrapper, the provider's discovery fault, a silent upstream, a session bound of one | [ ] Each row of the catalogue has a composed test reading the contract's text | MR-5a, MR-5b, `mr-5` decided | hermetic |
| MR-8a Configuration validated at start-up | ws2-hosting | `src/Platform.Registry`, `tests/Platform.Registry.Tests` | Rebuild the JSON form from `IConfiguration` by the schema's types, validate in `AddPlatformRegistry`, throw after one Error per finding naming keys only | [ ] A host with `Session:MaxSessions=0` fails to build naming the key. [ ] The example configuration builds. [ ] `"15"` for an integer passes and `"fifteen"` fails. [ ] No value appears in any message | `mr-8-platform-configuration-validated-at-start-up.md` decided; lands together with its smoke-test change | hermetic |
| P2-7a Export wiring in the cell | ws1-infra | `infra/modules/cell`, `tools/scripts/` | `OTEL_EXPORTER_OTLP_ENDPOINT` and the collector or endpoint the security tooling gives, plus a subscription on the log group for the audit lines | [ ] Spans and metrics from a deployed run arrive at the endpoint; the swap and login event lines arrive at the tooling | P2-7's scoping, P2-5c, P2-7b | needs AWS |
| P2-7b The identity events | ws3-identity | `src/Platform.Identity`, `tests/Platform.Identity.Tests` | The login, refresh and logout event lines with named properties, and `auth.logins`, `auth.refreshes` and `auth.sessions.active` on a `Platform.Identity` meter | [ ] Each event is one line with the fields MR-6 names. [ ] Each metric moves on the matching harness run | MR-6's contract, not drafted here (see the open decisions) | hermetic |
| P2-9a Hardened task | ws1-infra | `infra/modules/cell` | `readonlyRootFilesystem = true`, Exec off, a writable `/tmp` mount or `DOTNET_EnableDiagnostics=0`, the user pinned in the task definition | [ ] `terraform test` holds the settings; a deployed task turns ready and completes a login | P2-2 (keys off disk), the tunnel's replacement, P2-5e | needs AWS |

No C# ticket for P2-9: the data-protection move is P2-2's and the rest is environment. No ticket
for P2-4, P2-8 or P2-10: Terraform and process, opened at G5 as the plan says.

## The change requests, and what each blocks

Nine files under `contracts/changes/`, all `Status: open`. "Lands" is the reading each request
gives itself under `contracts/README.md` → "What may land between gates"; the integrator decides.

| Request | Asks for | Lands | Blocks |
| --- | --- | --- | --- |
| `p2-1-dynamodb-session-store.md` | The `AWSSDK.DynamoDBv2` 4.0.108 pin; `Session:TableName` with an `if`/`then`; a `docs/dependencies.md` row; the store contract suite's first assertion compared by value; where the fake lives | A widening and a test fix, between gates; the `if`/`then` read as a widening in effect | P2-1a, and through it P2-1b, P2-3b, MR-9b |
| `p2-2-data-protection-key-ring.md` | The `Amazon.AspNetCore.DataProtection.SSM` 4.0.3 pin; a closed `DataProtection` section; which registration carries the ring (`AddPlatformHosting` recommended); a dependencies row | A widening, between gates | P2-2a, P2-2c |
| `p2-3-session-lease-abstraction.md` | `ISessionLeases` and `SessionLease` in `Platform.Abstractions`, `InMemorySessionLeases` and a contract suite in `Platform.Testing`, two names in the surface test | A new type that changes no signature, between gates | P2-3a, and through it P2-3b, P2-3c, P2-6g |
| `p2-5-readiness-reports-registry-revision.md` | `/readyz` carries `instance` in `contracts/pipeline.md`; no package | Prose and an added member, between gates | P2-5a, P2-5b, and through them P2-5d, P2-5e |
| `p2-6-pingfederate-profile.md` | `pingfederate` in the `Profile` enum; `PrivateKeyJwt`; `Issuers:{name}:ClientAssertion`; `POST` on `/signin-oidc`; the cookie table's `SameSite` prose; an ADR amendment; the back-channel decision; a second CI run of the suites | Widenings and prose, between gates; the back-channel rule id waits for a gate | P2-6b, and through it P2-6c, P2-6d, P2-6e |
| `mr-2-shut-down-sequence-in-the-pipeline-contract.md` | A "Start-up and shut-down" section in `contracts/pipeline.md` with three numbers (120, 90, 120 proposed; vendor defaults to be confirmed) | Prose, between gates | MR-2a, MR-2b |
| `mr-4-upstream-bounds-named-in-the-contract.md` | Prose in `contracts/upstream-contract.md` and the `timeoutSeconds` description: an idle limit, a connection bound per origin, and the rate-limiting trigger | Prose, between gates | MR-4b only; MR-4a can start |
| `mr-5-dependency-down-catalogue.md` | A "When a dependency is unavailable" table in `contracts/http-contract.md`; one Errors row, 503 `store_unavailable` | Prose plus one added code, between gates on the integrator's reading | MR-5a, MR-5b, MR-5c |
| `mr-8-platform-configuration-validated-at-start-up.md` | `tools/smoke.sh` supplies a configuration and gains a failing case; one sentence in the schema's description; optionally a row in `contracts/README.md` | Prose and a script change; MR-8a lands together with the smoke-test change or turns CI red | MR-8a |

## Open decisions for the integrator

Consolidated from the three briefs, each with the planning session's recommendation. None is
applied anywhere.

1. **The lease: a contract type or store-private.** Recommend the type (`p2-3`), because the
   composed two-host proof must run over `Platform.Testing` doubles; the DynamoDB store still
   implements it on the session item with one conditional write.
2. **Who registers the key ring.** Recommend `AddPlatformHosting`, workstream 2 (`p2-2`, item 3).
3. **Where the fake DynamoDB server lives.** Recommend `tests/Platform.Identity.Tests`; it moves to
   `src/Platform.Testing` only if the composed suite must run over the real store, which decision 1
   avoids (`p2-1`, item 5).
4. **`Session:TableName`'s requirement: schema `if`/`then` or code.** Recommend both, the
   `if`/`then` read as a widening in effect; if read as a narrowing, the property alone and the code
   refuses a missing name until G6 (`p2-1`, item 2).
5. **The store contract suite's first assertion.** Change it to a value comparison in the same
   decision as the pin; it blocks P2-1a regardless (`p2-1`, item 4).
6. **The application name.** `spa-platform-{cell}`, fixed in the P2-2 decision.
7. **`DataProtection` required under `dynamodb`.** The code refuses the combination at start-up and
   the schema carries the same `if`/`then` under decision 4's reading.
8. **MR-9's rule as an ADR, not a request** (MR-9a). `SessionRecord` stays frozen; the item carries
   `format`.
9. **P-01 and P-03 at G6.** An amendment to ADR-0016 recording the G6 allowlist (one table, one key,
   the task role's new statements), so workstream 1's tests have a decision to cite when they widen.
10. **Which P2-5 mechanism.** Option (d), `/readyz` naming its instance and the wait sampling
    through the balancer; the gauge is built anyway (P2-5c) but not read by the wait.
11. **`instance` on `/readyz` lands between gates.** Yes, as the added-member case: no reader
    asserts the body text.
12. **Where MR-8's validation runs.** `AddPlatformRegistry`, because A-03 and D-1 both point there;
    the roadmap's "in `AddPlatformHosting`" is a sentence to correct when the gap is rewritten.
13. **MR-2's numbers, and constant versus configuration key.** Constants on the Kestrel-limits
    precedent, 120/90/120 as the starting proposal, confirmed against the vendor pages at decision
    time.
14. **`store_unavailable` now or at G6.** Now: an added row, no stage or signature changes, against
    a bodiless 500 on the gateway's most common request.
15. **MR-4's bound and the rate-limiting trigger.** Accept the prose; the number is workstream 4's
    to measure; the trigger turns the deferral into a condition.
16. **`form_post`, PAR, `private_key_jwt`, audience, logout, back-channel logout.** As `p2-6`'s
    items 3 to 7 recommend: `form_post` and `Require` on `pingfederate` only; `PrivateKeyJwt` with
    one key per cell per issuer; JWT access tokens with `aud` from `resource` where supported;
    RP-initiated logout as `standard`; back-channel logout decided now and built after G6 with
    `ISessionIndex` as a new type and a rule id that waits for a gate.
17. **`clientSecretRef` under `private_key_jwt`.** Defer: five things assume the value; a later
    request once they agree (`p2-6`, item 2).
18. **MR-6, the telemetry event-line fields.** Not drafted: P2-5e's log-stream check and P2-7b both
    read fields nobody has named. Recommend opening it with the G6 spec as a tenth request.
19. **P2-9's temp directory.** `DOTNET_EnableDiagnostics=0` in the task environment unless P2-10's
    runbooks want crash dumps; workstream 1 decides with P2-9a.
20. **The Phase 1 guard's test id**, for the `tests/Contracts.Tests` test that the example
    configuration selects no Phase 2 mechanism (ADR draft, decision 2).

What was not verified while drafting, because this sandbox has no `dotnet` and could not reach the
vendor documentation: each brief's "Unverified" section lists it, and each request marks it for the
ticket's first commit. The briefs are the session's working notes and are not in the repository.

## What stays blocked, and why

The deployed halves wait on the same chain as Phase 1's gates: #125 (a manual code-flow login
re-proving 1.8) blocks #55 (1.3), which blocks #56 (1.4), which blocks #57 (1.6b) and #58 (1.7),
which block #63 (6.9, the runs that close G3, G4 and G5), which blocks #62 (6.7). Each is a sitting
in AWS. Nothing in this plan shortens it, and nothing in this plan applies anything in AWS: P2-4 and
P2-8 are Terraform packages and open at G5 as the plan says, and the DynamoDB table, the KMS key and
the parameters P2-1 and P2-2 need on the stack are workstream 1's, outside P-01's allowlist, and
wait with them (ADR draft, decision 6).
