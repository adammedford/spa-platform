**Owner:** integrator
**Plan row:** P2-1, P2-2, P2-3, P2-5; roadmap gaps MR-2 and MR-9

## Problem Statement

The gateway is feature complete, hermetically: every entry point in `contracts/pipeline.md` is
filled, 29 of 29 rows of stage S4 have landed, and `g2-local-slice` is on `main`. What it still
cannot do is survive its own operation. One task runs; its sessions and its data-protection keys
live in process memory, so a deployment or a crash signs every user out and fails every login in
flight (`docs/plan/06-phase-2-and-risks.md` → Enterprise gaps: availability, session durability,
key durability, revocation). Two tasks would make it worse rather than better: each would hold its
own key ring, so a callback landing on the other task is refused, and each would refresh a session's
tokens on its own, so two grants race inside the provider's grace window and one session loses its
refresh token (C-2.17). The plan's answer is gate G6, packages P2-1 to P2-5, and the gates table
says G5 unblocks it. G5 waits on a chain of sittings in AWS (#125, #55, #56, #57 and #58, #63) that
no overnight run can shorten, and the C# lanes have nothing to do in the meantime: the run of
2026-10-19 (#129) dispatched the open C# rows and found them done.

The people hurt are the ones the plan already names. An operator cannot deploy without signing
everyone out. A user whose login started on one task cannot finish it on another. A session whose
two refreshes raced ends with `invalid_grant`. And the agent lanes that could be building the fix
idle while the AWS chain waits.

## Solution

Build the hermetic half of G6 now, at G2, and leave the deployed half and the gate for G5, as the
ADR draft `.scratch/phase-2/adr-draft-phase-2-hermetic-work-opens-at-g2.md` decides. Four packages
and two roadmap gaps, each as tickets per owner, each proved first in its owner's test project
against the doubles and again in workstream 6's composed harness, with a second gateway host added
to that harness so that "another task" is something a test can start.

- **P2-1, durable sessions.** `DynamoDbSessionStore` in `Platform.Identity` over `IAmazonDynamoDB`,
  proved by the existing `SessionStoreContract` against a fake DynamoDB HTTP server in workstream
  3's test project, the `FakeS3Server` precedent. The item's key is the SHA-256 of the session key;
  the ticket is protected with data protection under a purpose of the store's own; `expiresAt` is
  the table's time-to-live attribute and is also filtered on read; `version` is the conditional
  write's subject; `format` is MR-9's.
- **P2-2, the shared key ring.** `AddPlatformHosting` registers data protection with the cell as
  the application name and `Amazon.AspNetCore.DataProtection.SSM` as the repository, only when a
  `DataProtection` section is configured; absent, the framework's current behaviour, which is what
  every hermetic host and `tools/smoke.sh` want.
- **P2-3, one refresh across tasks.** A new `ISessionLeases` type in `Platform.Abstractions`, with
  an in-memory double and a contract suite in `Platform.Testing`; `AccessTokenSource` takes the
  lease under its in-process gate before it calls the provider, and losers re-read; the DynamoDB
  store implements the lease on the session item with one conditional write.
- **P2-5, registry convergence.** `/readyz` names the process that answered (`instance`, the ECS
  task id in a cell), and `spactl wait` takes the instances a cell runs and returns only when each
  has answered at the revision asked for or later, sampling through the balancer.
- **MR-2.** The start-up and shut-down sequence written into `contracts/pipeline.md`, with
  `HostOptions.ShutdownTimeout` set in `AddPlatformHosting` and the task's drain settings in the
  cell module, held to each other by `terraform test`.
- **MR-9.** A `format` attribute on the item, not on the frozen `SessionRecord`: a build reads its
  own format and the one before it, and reads an unknown newer one as absent with one log line.

Phase 1's behaviour does not change until G5: `Session:Store` stays `memory` in the example
configuration and in the cell module's default, and no `DataProtection` section means no key ring
is configured, so the binary #63 deploys for G3 to G5 is the Phase 1 gateway plus dormant code.

## User Stories

1. As an operator, I want a rolling deployment to cost no session and no login, so that deploying
   is not an event users notice.
2. As a user whose login started on one task, I want its callback to succeed on whichever task the
   balancer picks, so that a deployment during my login does not show me the failure page.
3. As a user with two tabs open, I want fifty parallel calls across two tasks to cause one refresh
   grant and no refused grant, so that my session does not end with `invalid_grant` because two
   tasks raced.
4. As an operator who ended a session through one task, I want the other task to refuse it on its
   next request, so that revocation means what it says with more than one replica.
5. As the deploy script, I want to wait until every task reports the new registry revision, so that
   a login that starts on one task and finishes on another succeeds during an onboarding.
6. As the integrator, I want every Phase 2 mechanism behind a configuration key whose default is the
   Phase 1 value, so that the G3 to G5 runs deploy the gateway Phase 1 claims.
7. As a workstream owner, I want each contract change I need written as a change request and decided
   before my ticket opens, so that my branch never touches an integrator path.
8. As a reviewer, I want every new invariant test to name the mutation that fails it, so that I can
   check the mutation instead of re-deriving the hazard.

## Implementation Decisions

The decisions are the ADR draft's six and the change requests' recommendations, each decided by the
integrator before the ticket that needs it opens. In the order the tickets need them:

1. **`p2-2-data-protection-key-ring.md`.** Pin `Amazon.AspNetCore.DataProtection.SSM` 4.0.3 (no KMS
   package: Parameter Store encrypts on the server); a closed `DataProtection` section with
   `ParameterPrefix` and an optional `KmsKeyId`; the ring registered in `AddPlatformHosting`
   (workstream 2), because hosting owns `Platform:Cell`, the SSM client and the Parameter Store
   stub, and the ring serves three consumers in two workstreams; the application name
   `spa-platform-{cell}`, fixed so a test can assert it against `Platform:Cell`.
2. **`p2-1-dynamodb-session-store.md`.** Pin `AWSSDK.DynamoDBv2` 4.0.108; an optional
   `Session:TableName` with an `if`/`then` making it required under `dynamodb`, read as a widening
   in effect because no `dynamodb` document can run today; the store contract suite's first
   assertion compares by value, since `ReadOnlyMemory<byte>` equality is reference equality and a
   serializing store can never pass it as written; the fake DynamoDB server lives in
   `tests/Platform.Identity.Tests` and speaks the JSON 1.0 protocol for exactly the five operations
   and condition expressions the store sends. The item layout is in the request's section 6.
3. **`p2-3-session-lease-abstraction.md`.** The lease is a contract type, `ISessionLeases` with
   `SessionLease`, not a member on the frozen `ISessionStore` and not store-private, because the
   composed two-host proof must run over `Platform.Testing` doubles; the time to live is the
   holder's provider deadline plus a margin.
4. **`p2-5-readiness-reports-registry-revision.md`.** Option (d): `/readyz` carries `instance`; the
   wait samples through the balancer with the task ids; no package, no ECS Exec, no metric pipeline.
   The per-task log stream is a second reader, once MR-6 names the swap line's fields.
5. **`mr-2-shut-down-sequence-in-the-pipeline-contract.md`.** Constants on the Kestrel-limits
   precedent, 120 seconds of draining, a 90-second shutdown timeout and a 120-second stop timeout,
   confirmed against the vendor pages at decision time; `/readyz` unchanged.
6. **MR-9's rule** is an ADR of its own (ticket MR-9a), not a change request: the item carries
   `format`, a build reads its own and the previous one, an unknown newer format reads as absent,
   and a format change is a rolling-deployment and rollback drill before it ships.
7. **P-01 and P-03 move at G6.** The table, the key and the task role's new statements are outside
   today's allowlist; an amendment to ADR-0016 records the G6 allowlist so workstream 1's
   `terraform test` has a decision to cite when it widens. The applies themselves are sittings and
   wait for G5.
8. **Merge subjects.** Every hermetic ticket lands as `Merge part of task P2-n:`; the row is landed
   only by the merge carrying its deployed proof.

## Testing Decisions

- **The hermetic tier is the whole proof until G5.** Each ticket's acceptance list is the hermetic
  reading of its package's "Accepted when", and workstream 6 proves the composed reading over two
  gateway hosts in one test process sharing one `InMemorySessionStore`, one `InMemorySessionLeases`,
  one in-memory `IXmlRepository` and one `InMemoryObjectStore`, behind a balancer transport that
  alternates listeners. The deployed tier repeats each on two real tasks, blocked by #63 and P2-4.
- **Three hazards the tests must be built around**, from the sessions brief. The store contract
  suite's first assertion compares records by reference, so it is changed before any serializing
  store is held to it. A two-host key-ring test with no repository configured passes for the wrong
  reason, because two hosts in one process share the framework's default directory; both hosts set
  their repository explicitly, or the control cannot fail. A default `GetItem` is eventually
  consistent; the fake refuses a read without `ConsistentRead`, so the re-read before a refusal
  (T-B8) and "ended on one task, refused by the other" keep their meaning on the real table.
- **Every new invariant test names its mutation** in the merge body (`CODING_STANDARDS.md`): two
  lease tables for one, which brings back today's two grants and one refusal; a dropped condition
  expression; two key repositories; a host B left unpolled.
- **The Phase 1 guard.** A test in the integrator's `tests/Contracts.Tests` that the example
  platform configuration selects no Phase 2 mechanism (`Session:Store` is `memory`, no
  `DataProtection` section), beside `Session_store_is_memory_in_phase_1_and_dynamodb_in_phase_2`;
  its id is the integrator's to assign.

## Out of Scope

- P2-4, the multi-task service: Terraform, applied in a sitting after G5. The two-host harness is
  its hermetic stand-in, and nothing here applies anything in AWS.
- The DynamoDB table, the KMS key, the parameter prefix and the task role's statements: workstream
  1's, listed as one-line tickets and applied after G5.
- Back-channel logout and a session index by subject: decided with P2-6 (its change request, item
  7) and built after G6.
- Removing `clientSecretRef` under `private_key_jwt`: P2-6's later request.
- Dropping `Session:MaxSessions` from `required` under `dynamodb`: a narrowing, which waits for a
  gate; the DynamoDB store ignores it.
- The gate tag `g6-continuity`: cut only after the deployed tier runs on the stack.

## Further Notes

**The ticket graph.** Owners are `OWNERSHIP.md`'s role names. "Blocked by" names a change request
by file name (meaning: its decision), a ticket by id, and #63 where the deployed stack is needed.
Tier: hermetic runs in `dotnet test` against the doubles; needs AWS is the deployed tier. The
acceptance text is the ticket body's checklist; each box is a behaviour a test can fail.

| Ticket | Owner | Paths | What to build | Acceptance | Blocked by | Tier |
| --- | --- | --- | --- | --- | --- | --- |
| H-1 Two gateway hosts in the harness | ws6-verification | `tests/Gateway.TestHarness`, `tests/Gateway.IntegrationTests` | A second `GatewayFactory` over the same `InMemoryObjectStore` and `FakeClientSecretSource`; a `LoadBalancerTransport` that alternates listeners; one shared in-memory `IXmlRepository` and one shared `ISessionStore` instance through `ConfigureTestServices`, both set explicitly on both hosts; per-host `RefreshRegistryAsync` | [ ] A `__Host-spa-session` cookie issued by host A gets 200 from `/bff/session` on host B. [ ] A login started on A, carrying its correlation and nonce cookies, completes on B's callback. [ ] The control: two separate repositories, and both are refused. [ ] A registry change polled on A only leaves B's `/readyz` at the old revision | none | hermetic |
| P2-1a The DynamoDB store and its fake | ws3-identity | `src/Platform.Identity`, `tests/Platform.Identity.Tests` | `DynamoDbSessionStore` over `IAmazonDynamoDB` with the request's item layout; `FromConfiguration` branches on `Session:Store` and reads `Session:TableName`; `TryAddSingleton<IAmazonDynamoDB>` built on first use; a fake DynamoDB HTTP server speaking JSON 1.0 for `PutItem`, `GetItem`, `UpdateItem`, `DeleteItem` and `Query`, evaluating the five condition expressions and answering `ConditionalCheckFailedException` as a 400 with `__type` | [ ] `SessionStoreContract` passes against the store over the fake. [ ] `Session:Store = dynamodb` without `Session:TableName` is refused naming the key; with it, the store is built on first use and a host that never touches a session still starts. [ ] A record past `ExpiresAt` reads as absent and a replace at it is refused, with the clock moved by `TimeProvider`. [ ] A replace at a stale version is refused by the fake's condition, not by a check in the store (mutation: drop the condition). [ ] Every `GetItem` the fake saw carried `ConsistentRead: true`. [ ] No request the fake saw contains a session key; every `pk` is its SHA-256. [ ] A cancellation after `ReplaceAsync` sent its write does not drop the write (T-B17) | `p2-1-dynamodb-session-store.md` items 1, 2 and 4 | hermetic |
| P2-1b The ticket is encrypted in the item | ws3-identity | `src/Platform.Identity`, `tests/Platform.Identity.Tests` | The `ticket` attribute protected with `IDataProtectionProvider` under `Platform.Identity.SessionTicket` and the app id before the write, unprotected after the read | [ ] The bytes the fake stored are not the serialized ticket and contain no token the test issued. [ ] A store built over a different key ring reads the item as absent and logs one line without the key. [ ] The contract suite still passes | P2-1a | hermetic |
| P2-1c Table, key, role | ws1-infra | `infra/`, `tools/scripts/` | `aws_dynamodb_table` (pay per request, `pk` hash key, TTL on `expiresAt`, `appId` index, SSE under the cell's key), the cell's KMS key and alias, task-role statements for the table and index, `Session__TableName` and `session_store = dynamodb` through the module, P-01 and P-03 tests widened; `docs/agents/aws-rules.md` read first | One line, as asked: the rows and tests are workstream 1's to cut | P2-1a's decision on the layout; the ADR-0016 amendment (decision 7) | needs AWS |
| P2-1d Killing a task loses no session | ws6-verification | `tests/e2e`, `docs/runbooks/` | The G6 deployed run's first row: sign in, stop the task, sign in again on the replacement without a prompt | [ ] A session established before the task is stopped answers `/bff/session` 200 and an API call 200 on the replacement task. [ ] The same with a refresh due | P2-1a, P2-1b, P2-1c, P2-2c, P2-4, #63 | needs AWS |
| P2-2a The ring in Parameter Store | ws2-hosting | `src/Platform.Hosting`, `tests/Platform.Hosting.Tests` | `AddPlatformHosting` registers `AddDataProtection().SetApplicationName(...).PersistKeysToAWSSystemsManager(prefix, o => o.KMSKeyId = ...)` when the `DataProtection` section is present; refuses `Session:Store = dynamodb` without it; a stub SSM answering `PutParameter` and `GetParametersByPath` | [ ] With the section set, protecting one payload writes one `SecureString` parameter under the prefix with the configured key id, and a second host over the same stub unprotects it. [ ] With the section absent the stub sees no request, and two hosts cannot read each other's payloads. [ ] `Session:Store = dynamodb` with no `DataProtection` section is refused at start-up naming both keys. [ ] The application name is derived from `Platform:Cell` and differs between two cells | `p2-2-data-protection-key-ring.md` items 1 to 3 | hermetic |
| P2-2b A cookie and a login state cross two hosts | ws3-identity | `tests/Platform.Identity.Tests` | Two `IdentityGateway`s sharing one `IXmlRepository` double over a list of `XElement`, repositories set explicitly on both | [ ] A `__Host-spa-session` cookie issued by the first host gets 200 from `/bff/session` on the second. [ ] A login started on the first, carrying its correlation and nonce cookies, completes on the second's callback. [ ] With two separate repositories both are refused (the control) | none | hermetic |
| P2-2c Key, prefix, role | ws1-infra | `infra/` | The ring's parameter prefix, task-role `ssm:PutParameter` and `GetParametersByPath` on it, the KMS grant through Systems Manager for the cell's key, `DataProtection__*` through the module, P-03 test widened | One line, as asked | P2-2a; the ADR-0016 amendment | needs AWS |
| P2-2d Accepted by another task | ws6-verification | `tests/e2e` | A browser-suite row in the shape of 6.5 (#61) against two tasks | [ ] A cookie issued by one task is accepted by the other. [ ] A login started on one task completes on the other | P2-2a, P2-2c, P2-4, #63 | needs AWS |
| P2-3a `ISessionLeases` | integrator | `src/Platform.Abstractions`, `src/Platform.Testing`, `tests/Platform.Abstractions.Tests` | The lease type and `SessionLease`, `InMemorySessionLeases` over `TimeProvider`, `SessionLeaseContract`, the surface test's two new names | [ ] The contract suite passes against the double: a free key is taken, a held key refused, a released key taken, an expired lease taken over, a stale release ignored, two keys independent. [ ] `PublicSurfaceTests` lists both names | the decision on `p2-3-session-lease-abstraction.md` | hermetic |
| P2-3b One refresh per session across tasks | ws3-identity | `src/Platform.Identity`, `tests/Platform.Identity.Tests` | `AccessTokenSource` takes the lease under the in-process gate before the provider call; losers re-read until the version moves or the lease expires; `DynamoDbSessionStore` implements `ISessionLeases` on the session item with `leaseUntil` and `leaseToken` and a fenced release; two `IdentityGateway`s over one store and one lease table | [ ] Twenty-five calls to each of two hosts on a due token: the test provider, grace window at zero, counts one grant and no refusal; all fifty succeed; the record holds one rotated token (mutation: two lease tables give two grants and one refusal). [ ] A logout through one host makes the next request on the other 401 `session_ended`. [ ] The lease against the fake: a second acquire is refused by the fake's condition, an expired lease is taken over, a stale release changes nothing. [ ] Every existing `AccessTokenSourceTests` row still passes | P2-3a, P2-1a | hermetic |
| P2-3c The composed two-gateway proof | ws6-verification | `tests/Gateway.TestHarness`, `tests/Gateway.IntegrationTests`, `tests/Gateway.SecurityTests` | H-1's harness sharing one `InMemorySessionLeases` as well | [ ] P2-3's two sentences, composed, over the doubles: fifty calls across the two hosts cause one grant and no refusal; a session ended on one gateway is refused on the other. [ ] The negative half as a row of the negative suite (#60) | H-1, P2-3a, P2-3b | hermetic |
| P2-3d Two real tasks | ws6-verification | `tests/e2e` | The G6 deployed run's fifty calls split across two tasks, and the logout row | [ ] As the plan words it, against Cognito with its 10-second grace | P2-3b, P2-1c, P2-4, #63 | needs AWS |
| P2-5a `/readyz` names its instance | ws2-hosting | `src/Platform.Hosting`, `tests/Platform.Hosting.Tests` | The `instance` member: the ECS task id read once at start-up from the task metadata endpoint when `ECS_CONTAINER_METADATA_URI_V4` is set, else a per-process id; `/healthz` unchanged | [ ] `/readyz` 200 carries `revision` and `instance`; with the metadata variable pointing at a stub, `instance` is the stub's task id; without it, two hosts in one process report different ids. [ ] `spactl`'s reader and the composed suites pass unchanged | `p2-5-readiness-reports-registry-revision.md` | hermetic |
| P2-5b `spactl wait --instances` | ws2-hosting | `tools/spactl`, `tests/Spactl.Tests` | `--instances a,b`: return 0 only when each named instance has answered at `N` or later through the one URL; time out naming the instances still behind; `--revision` alone behaves as today | [ ] Against a stub that alternates two instances, one at `N` and one behind, the wait blocks until the stub advances the second. [ ] The timeout message names the laggard. [ ] The old form is unchanged | `p2-5-readiness-reports-registry-revision.md` | hermetic |
| P2-5c The registry instruments and the swap event | ws2-hosting | `src/Platform.Registry`, `tests/Platform.Registry.Tests` | `registry.revision`, `registry.age_seconds` and `registry.rejected` on a `Platform.Registry` meter through `IMeterFactory`; the swap line's revision, ETag and app count as named properties | [ ] After a swap the gauge reads the live revision and the age resets. [ ] A rejected document increments `registry.rejected`. [ ] The swap line carries the three properties | none: `contracts/telemetry.md` names them already | hermetic |
| P2-5d Convergence, composed | ws6-verification | `tests/Gateway.IntegrationTests` | Over H-1's harness: onboard app 3 and poll host A only; a wait against the balancer with both instance ids; a login started on A finished on B | [ ] A wait given both ids returns only after B is polled too; one given a shorter deadline times out naming B as the instance still behind. [ ] After the wait, a login to app 3 started on A completes on B's callback and lands on app 3's shell. [ ] The control: the same login with B still at the old revision ends in 404 `not_found` at the callback | H-1, P2-5a, P2-5b | hermetic |
| P2-5e The deploy script waits per task | ws1-infra | `tools/scripts/` | `deploy.sh` (#58) and the rotation steps run `spactl wait --instances` over `aws ecs list-tasks`; a verify step reads each task's swap line from its log stream | [ ] After a registry apply the script returns only when every running task's stream carries the new revision; a task held at the old revision fails the step with its id | #58, P2-4, P2-5b, P2-5c | needs AWS |
| P2-5f The deployed login across tasks | ws6-verification | `tests/e2e`, `docs/runbooks/` | The login that starts on one task and finishes on another during an onboarding, in the deployed gate runs | [ ] P2-5's acceptance, with the task ids recorded as F-07's deployed half records them | P2-1d, P2-2d, P2-4, P2-5e, #63 | needs AWS |
| MR-2a The shutdown timeout | ws2-hosting | `src/Platform.Hosting`, `tests/Platform.Hosting.Tests` | `HostOptions.ShutdownTimeout` as a constant set in `AddPlatformHosting` | [ ] A request shorter than the constant completes across `StopAsync`; one longer is aborted. [ ] The constant equals the contract's number | `mr-2-shut-down-sequence-in-the-pipeline-contract.md` | hermetic |
| MR-2b The task's drain settings | ws1-infra | `infra/modules/cell` | `stopTimeout` on the gateway container, `deregistration_delay` on the target group, the deployment percentages, with `terraform test` assertions | [ ] The assertions hold `stopTimeout` above the shutdown timeout and the stated delay; a mutation of either fails the run | the same request | hermetic (`terraform test` with mocks) |
| MR-2c Deployment and kill with live sessions | ws6-verification | `tests/e2e`, `docs/runbooks/` | P2-4's acceptance run | [ ] A rolling deployment and a killed task cost no session and no login | P2-1d, P2-2d, P2-4, MR-2a, MR-2b, #63 | needs AWS |
| MR-9a The format rule | integrator | `docs/adr/` | An ADR: the item carries `format`; a build reads its own format and the previous one; an unknown newer format reads as absent with one log line; a format change amends the ADR and is a rolling-deployment and rollback drill before it ships | [ ] The ADR is indexed and the P2-1 store's doc comment cites it | none | decision |
| MR-9b The reader honours the rule | ws3-identity | `src/Platform.Identity`, `tests/Platform.Identity.Tests` | The store writes `format = 1`, reads 1, and reads an item at 2 as absent | [ ] An item the fake holds at `format = 2` reads as null, with one Warning carrying the format and `session_ref` and no key. [ ] An item at `format = 1` reads. [ ] A missing `format` reads as absent | MR-9a, P2-1a | hermetic |
| MR-9c Rollback with live sessions | ws6-verification, with ws1-infra for `rollback.sh` | `tests/e2e`, `docs/runbooks/`, `tools/scripts/` | The runbook step and the drill: deploy a build that writes format N+1, sign in, run `rollback.sh`, show that the older build asks for a login once and answers no 500 | [ ] Run at the first real format change; until then P2-4's rolling-deployment test covers a rollback at the same format and the runbook says so | MR-9b, P2-1d, #63 | needs AWS |

**What can start on the first night**, with no decided request and no open blocker: H-1 (ws6),
P2-2b (ws3), P2-5c (ws2), and MR-9a (integrator, a decision). Everything else waits on a request's
decision, on one of those, or on the stack.

**Landing order inside G6.** `p2-2` and `p2-1` decided first, then `p2-3` and `p2-5`; P2-3a and
MR-9a are the integrator's own tickets and can land with the decisions. The deployed tier opens
when #63 closes G5 and P2-4's two tasks exist.
