# Change request P2-5: `/readyz` names the process that answered, so a wait can cover every task

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 2 (Hosting and
  registry), for the Phase 2 spec issue (to be filed)
- **Tasks:** P2-5; 2.3 (health) and 2.6 (`spactl wait`); 1.7 (`deploy.sh`, open under #58); 6.1
  (the harness) and 6.9 (the deployed gate runs, #63)
- **Date:** 2026-10-19
- **Status:** decided, 2026-10-20. See the Decision section below.

## The gap

P2-5 says "onboarding and rotation wait until every task reports the revision", and is accepted
when "a login that starts on one task and finishes on another succeeds during a reload"
(`docs/plan/06-phase-2-and-risks.md`). Every part of the wait exists for one task, and none of it
can tell two apart:

- `/readyz` answers `200` with `{"revision": N}` once a snapshot is live and `503` before it
  (`contracts/pipeline.md` -> Health endpoints; `src/Platform.Hosting/Health.cs`, `ReadyAsync`).
  The revision is read from `IRegistry.Current` on every request, so it is the live one of the
  process that answered. Nothing in the body says which process that was.
- `spactl wait --url <origin> --revision N` reads `/readyz` through the one URL every two seconds
  until a `200` body carries `revision` at `N` or later, for five minutes by default
  (`tools/spactl/Commands.cs`, `WaitAsync`; `SpactlEnvironment.PollInterval`). The ALB hands each
  read to whichever task it picks, so with two tasks one answer at `N` says one task is at `N`.
  The next login may land on the other, still one behind.
- The ALB cannot address a task, and after P2-9 neither can an operator: ECS Exec goes
  (`docs/plan/06-phase-2-and-risks.md`, P2-9), and with it the only route to a task's own listener
  (`infra/modules/cell/ecs.tf`, `enable_execute_command`; `docs/plan/05-infrastructure-and-cost.md`
  -> Workstation access). The task security group admits the gateway's port from the ALB group
  alone (P-06).
- `contracts/telemetry.md` already names `registry.revision`, `registry.age_seconds` and
  `registry.rejected` among the metrics, and promises "one event line for each login, refresh,
  logout and registry swap". No code writes any of them: the only instruments in `src/` are
  `gateway.requests` and `security.rejections` on `Platform.Telemetry`
  (`src/Platform.Telemetry/RequestTelemetry.cs`), and the swap is one free-text Information line,
  "Registry revision {Revision} ({ETag}) is live with {Count} app(s)", in
  `src/Platform.Registry/RegistrySource.cs` (`Swap`). Phase 1 exports no metric in any case
  (`contracts/telemetry.md` -> What Phase 1 exports).

So "every task reports the revision" has no reporter and no reader today.

## The options

| | How a wait learns each task's revision | What it needs | Verdict |
| --- | --- | --- | --- |
| (a) Each task's own `/readyz`, one task at a time | ECS Exec into each task, or a route to each task's address on the gateway's port | Exec, which P2-9 removes; or a hole in the task security group that P-06 forbids | No |
| (b) A `registry.revision` gauge per task, exported and queried | The gauge written (workstream 2), an export pipeline (P2-7: a collector, CloudWatch or the security tooling) and a reader. In `spactl` the reader needs `AWSSDK.CloudWatch`: 4.0.106 is on nuget.org as of today, declares Apache-2.0, and wants `AWSSDK.Core` in `[4.0.102.12, 5.0.0)`, which the pinned S3 and Parameter Store packages also accept | A pin, a pipeline P2-7 has not built, and a delivery lag on top of the poll interval | Not for the wait. The gauge is still owed to the contract: see below |
| (c) The ECS task-definition revision from `describe-services` | Nothing new | It says which image runs, not which registry revision is live, and the registry changes without a deployment by design (ADR-0002) | No |
| (d) `/readyz` names the process that answered, and the wait samples through the ALB until every running task has answered at `N` or later | One member in the readiness body; `spactl wait` takes the task ids to cover, which the deploy script reads from `list-tasks` | A prose change to one contract table, no package, no Exec, no pipeline | **Recommended** |

(d) works because the ALB spreads reads over its healthy targets, and a task at the old revision
is healthy: it answers `200` with its own, older number. A wait that keeps reading until each
named task has answered at `N` or later ends only when all have; a task that is never sampled
times the wait out rather than passing it, which is the failure to have. A task that starts during
the wait is not in the list and is ignored; it reads the registry when it starts, so it starts at
`N` or later.

The per-task log stream is a second reader that costs nothing. The `awslogs` driver names each
container's stream after the task (`infra/modules/cell/ecs.tf`, `awslogs-stream-prefix`), so every
task's swap line is already filed under its task id in CloudWatch Logs, and a deploy or verify
script in `tools/scripts/` can confirm the wait from those streams with the AWS CLI and no
package. That is a check on (d), not a replacement: log delivery lags the swap by seconds, and it
needs the swap line's fields fixed, which is MR-6's contract work.

## The change

1. **`contracts/pipeline.md` -> Health endpoints.** The `/readyz` row becomes:

   | Path | Answer |
   | --- | --- |
   | `/readyz` | `200` with `{"revision": N, "instance": "<id>"}` once a snapshot is live, `503` before that. `instance` names the process that answered: in a cell, the ECS task id; anywhere else, an id unique to the process. A reader takes the members it names and ignores the rest. |

   and the sentence under the table gains: "`spactl wait` can be given the tasks a cell runs, and
   then returns only when each has answered at the revision asked for or later."

   The ECS task id is the identity operators already use: F-07's deployed acceptance reads "the
   task id, start time and task definition revision before and after" (`docs/plan/04-tests.md`).
   How workstream 2 obtains it is its own design. The task metadata endpoint the agent publishes to
   every Fargate container is the documented source, read once at start-up, with a generated id
   when the endpoint is not there, which is every workstation and the harness.

2. **Nothing in `contracts/telemetry.md`.** `registry.revision` is already there. This request does
   not ask for the swap event's field names; that is MR-6.

3. **No package and no row in `docs/dependencies.md`.** (b)'s pin is recorded above so that nobody
   confirms it twice. It is not asked for.

## Why each part lands when it does

- Item 1 is prose and an added member in a body. Nothing built against the frozen row has to change
  to keep working: `spactl` parses the body as an object and reads `revision` out of it
  (`tools/spactl/Commands.cs`, `ReadyRevision`), and the composed suites read `revision` the same
  way (`tests/Gateway.IntegrationTests/Harness/HarnessBootTests.cs`,
  `tests/Gateway.SecurityTests/Negative/RegistryRejectionTests.cs`). No test asserts the body's
  text. Under `contracts/README.md` -> "What may land between gates" that is the added-type case,
  on the integrator's decision.
- `contracts/http-contract.md` does not describe the body; it only reserves the path. Nothing there
  changes.
- The `--instances` option on `spactl wait`, the new member and the id's source are workstream 2's
  own paths (`src/Platform.Hosting`, `tools/spactl`, their tests) and wait for nothing but this
  decision.

## What workstream 2 can do meanwhile

- The revision is already on the snapshot and on every request line as `registry_rev`
  (`src/Platform.Telemetry/RequestTelemetry.cs`), so a reader of one task's log can see what that
  task serves today.
- The swap line is workstream 2's own: `RegistrySource.Swap` can carry the revision, the ETag and
  the app count as named properties now, ahead of MR-6 naming them in the contract, because the
  formatter writes a property under its own name.
- `registry.revision`, `registry.age_seconds` and `registry.rejected` on a `Platform.Registry`
  meter through `IMeterFactory`, as `contracts/telemetry.md` -> The instruments already requires.
  Nothing exports them until P2-7, and nothing has to.
- `spactl wait` can grow the per-instance bookkeeping behind a flag that reads a member the body
  does not carry yet, so the one change that lands with this decision is the body itself.

## The proof, and what blocks it

**Hermetic.** Two gateway hosts in workstream 6's harness over one `InMemoryObjectStore` and one
`FakeClientSecretSource` (`tests/Gateway.TestHarness/GatewayHarness.cs` builds one of each per
harness), behind a `LoadBalancerTransport` that alternates between the two listeners as the ALB
does. The harness polls one host's `RegistrySource` and not the other's, which is the skew the wait
exists for:

1. Onboard app 3 (`OnboardAsync`) and poll host A only. A's `/readyz` says `2`, B's says `1`.
2. A wait against the balancer with both instance ids returns only after B is polled too; a wait
   given a deadline before that times out, naming B as the instance still at `1`.
3. Start a login to app 3 on A, up to the redirect to the provider, then finish its callback on B.
   It lands on app 3's shell.
4. The control that makes the assertion able to fail (`CODING_STANDARDS.md`): the same login with
   B still at `1` ends in `404` `not_found` at the callback, because B does not know the host
   (`src/Platform.Hosting/HostResolution.cs`).

Steps 3 and 4 need the two hosts to share what a login leaves behind. The correlation and nonce
cookies are protected with data-protection keys, and the session the callback creates on B must
be visible to A on the next request. Hermetically that is harness work, not AWS work: both
factories can be given one key ring (`AddDataProtection()` over one shared repository, in the way
`tests/Platform.Security.Tests/AntiforgeryHost.cs` already configures the provider for its own
host) and one `ISessionStore` instance, both through `ConfigureTestServices`. So the hermetic proof
is blocked by a harness ticket and by this request, not by P2-1 and P2-2's implementations.

**Deployed.** The same login across two real tasks needs P2-4 (two tasks), P2-2 (one key ring per
cell, so B can read A's correlation cookie) and P2-1 (a session store both tasks read), and runs
with the deployed gate runs (#63) once `deploy.sh` (#58) can drive `spactl wait` with the task ids.
P2-3 is not on this path: no refresh happens inside a login.

## Why this is a request rather than an edit

`contracts/` is the integrator's (`AGENTS.md`, `OWNERSHIP.md`), and `contracts/pipeline.md` froze
at G1a.

## Decision

Integrator, 2026-10-20, on Adam's answers to the ten questions of
https://github.com/adammedford/spa-platform/issues/220 (all accepted as recommended). **Option (d)**,
applied to `contracts/pipeline.md` → Health endpoints in the same commit as this decision, with one
word changed: the member names a **gateway instance**, not a process. The hermetic proof this request
designs runs two gateway hosts in one test process, so "process" is the wrong unit, and "task" is
right only in a cell.

1. **The member is `instance`.** Its value is the ECS task id in a cell (the last path segment of
   the task ARN, lowercase hex, never the ARN, which carries the account id and the Region and would
   sit on an unauthenticated path) and, anywhere else, an id unique to that instance. The id is not
   a credential and names nothing an attacker could reach (P-06), so it may sit on `/readyz`.
2. **The source is workstream 2's design**, in `Platform.Hosting`: the task metadata endpoint
   (`${ECS_CONTAINER_METADATA_URI_V4}/task`, `TaskARN`'s last segment), read once at start-up with
   a short timeout, never blocking readiness or failing the start; when the variable is absent or
   the read fails, an id generated per host at registration, never the machine name or the pid, with
   one Information line saying the endpoint was not present. No `Platform:Instance` configuration
   key: the harness injects a deterministic id through test services.
3. **Only the 200 body of `/readyz` carries it.** `/healthz` stays bodiless, and the 503 before a
   snapshot stays bodiless: a task answering 503 is out of ALB rotation and no reader sees it.
4. **The contract text** is the row and the sentence as applied, with "gateway instance" and
   "instances"; "a reader takes the members it names and ignores the rest" is the rule that makes
   the next additive member prose too. `contracts/http-contract.md` reserves the path and does not
   change.
5. **`spactl wait --instances <id,id,...>`**: one comma-separated option, optional, no AWS package.
   A repeated option would silently keep its last value on today's parser, and an in-`spactl`
   `list-tasks` would need `AWSSDK.ECS`, which is not pinned, and ECS credentials in a command the
   hermetic tests run with none. With the option, the wait ends with 0 when every listed id has
   answered 200 at `N` or later; the timeout names each listed id not yet seen at `N` or later with
   its last answer; an answer from an unlisted id is printed and is neither required nor fatal (a
   task that started mid-wait read the registry at start); a listed task that stops mid-wait times
   the wait out naming it. The 2-second poll and 5-minute default stay.
6. **The caller.** `deploy.sh` does not call `spactl wait` today; its step 5 waits for ECS stability.
   The wait P2-5 names belongs to onboarding and rotation, which have no script, and a deployment
   changes the image, not the registry. So workstream 1 adds a `cell-common.sh` convergence helper
   that lists the running tasks (`list-tasks --desired-status RUNNING`) and runs
   `spactl wait --instances <ids> --revision N`; `deploy.sh` step 5 (#58) is its first caller, with
   the revision of the document step 1 read, and `deploy_tests.py` proves a task never sampled fails
   it; the deployed onboarding and rotation runs (#63) and any later rotate script call the same
   helper. It lands after #207, which touches the same step. The helper knows nothing of ECS
   states: a task ECS is stopping has desired status `STOPPED` and leaves the list, and a draining
   target receives no new requests; if that reading is wrong the helper also drops targets whose
   health is `draining`.
7. **Between gates**, as the additive case beside change request 6.4: every reader parses
   `revision` by name and ignores the rest (`spactl`'s `ReadyRevision`, `HarnessBootTests`,
   `RegistryRejectionTests`, `ReleaseSwitchTests`, `HealthTests`), and the ALB matches on status
   alone, so nothing built against the frozen row changes.
8. **Nothing in `contracts/telemetry.md`.** The `registry.revision` gauge stays owed to MR-6 and
   P2-7, the swap line's field names are MR-6's, and the log line gains no instance field because
   the `awslogs` stream is already named per task and `cell` is on every line.
9. **`GLOSSARY.md` gains Instance**; no ADR: an additive member and a CLI option are cheap to
   reverse, and the decision follows from ADR-0012 and P-06 rather than committing to anything new.
