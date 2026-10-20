# Change request MR-2: the pipeline contract states start-up and shut-down, with the numbers

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 2 (Hosting and
  registry), for the Phase 2 spec issue (to be filed)
- **Tasks:** MR-2; P2-4 (two or more tasks), P2-2 (the shared key ring), P2-1 (durable sessions);
  2.3 (health), 1.4 (the ECS service, #56), 6.9 (the deployed gate runs, #63)
- **Date:** 2026-10-19
- **Status:** open

## What is defined, and what is not

`docs/roadmap.md` MR-2: "`/readyz` gates the ALB on the first valid snapshot, which is the start-up
half. The shut-down half is blank." Read from the tree:

| Moment | Where it is decided | What it says |
| --- | --- | --- |
| A task turns ready | `contracts/pipeline.md` -> Health endpoints; `src/Platform.Hosting/Health.cs` | `/readyz` is `503` until the first valid snapshot and `200` after. The ALB health check reads it (`infra/modules/cell/alb.tf`, `health_check`) with 60 seconds of grace (`infra/modules/cell/ecs.tf`, `health_check_grace_period_seconds`) |
| A task cannot read its registry | `src/Platform.Registry/RegistryPoller.cs`, `RegistrySource.cs` | It keeps serving the last snapshot and stays ready. A cold start with nothing readable stays `503`, "so the load balancer keeps the tasks that still work" (`RegistrySource.cs`, the cold-start comment) |
| The process is asked to stop | Nothing. `src/Gateway/Program.cs` sets no `HostOptions`, and no project configures `ShutdownTimeout` | The framework's default: the host closes its listener and waits its default shutdown timeout for requests in flight, then aborts them (`HostOptions.ShutdownTimeout`, Microsoft Learn) |
| ECS stops the task | `infra/modules/cell/ecs.tf`: no `stopTimeout` on either container | The platform's default between SIGTERM and SIGKILL, 30 seconds on Fargate and at most 120 (the task definition parameters page of the ECS developer guide, `stopTimeout`) |
| The ALB stops sending to the task | `infra/modules/cell/alb.tf`: no `deregistration_delay` on the target group | The default draining window of 300 seconds (the target group attributes page of the ALB guide) |
| A login in flight when its task stops | `docs/plan/05-infrastructure-and-cost.md`: "In Phase 1 a gateway deployment signs every user out ... Phase 2 removes that" | The correlation and nonce cookies are protected with the task's own keys, so the callback on another task is refused with the failure page (`contracts/http-contract.md`, under Errors; rule `auth.callback`). P2-2 removes that |
| `/readyz` while stopping | Nothing | It answers `200` until the listener closes |

None of these numbers is wrong. None of them is written where the two owners who must agree on
them, workstream 1 for the task and workstream 2 for the process, would read it; and P2-4's
acceptance, "a rolling deployment and a killed task, with live sessions, cause no sign-out and no
failed login", cannot be tested against a blank. The vendor defaults in the table are quoted from
the pages named beside them and were not re-read when this was drafted, since the drafting sandbox
could not reach them; the decision should confirm each number against the current page before it
is written into the contract.

## Which part is whose

- **C#, workstream 2's own path.** `HostOptions.ShutdownTimeout` is a service registration, and
  `AddPlatformHosting` registers nothing today
  (`src/Platform.Hosting/PlatformHostingExtensions.cs`). Setting it there adds no entry point, so
  `src/Gateway/Program.cs` does not change. The value is a
  constant in `Platform.Hosting` with its reason beside it, as row 4.6's Kestrel limits are
  constants in `src/Platform.Security/PlatformSecurityLimits.cs` rather than configuration.
- **Terraform, workstream 1's own path.** `stopTimeout` on the gateway container,
  `deregistration_delay` on the target group, and the service's rolling-deployment percentages,
  which `ecs.tf` leaves at their defaults.
- **The contract, the integrator's.** The sequence and the relation between the numbers, so that a
  reader of either side knows what the other guarantees. That is this request.

Nothing is asked of `/readyz`. Answering `503` once the host is stopping would let a balancer that
polls readiness drain a task, but ECS drains the other way round: it deregisters the target and
waits out the deregistration delay before it sends SIGTERM (the ECS developer guide's page on
load balancer connection draining), so by the time the process is told to stop the balancer has
stopped sending to it. A readiness flip would change a frozen row for nothing.

## The change

A section in `contracts/pipeline.md` after "Health endpoints", headed "Start-up and shut-down":

> A task is sent traffic once `/readyz` answers `200`, which is at most one poll interval after it
> starts, and the service gives it 60 seconds of grace before a failed check counts.
>
> A task is stopped in three steps, each bounded. First the balancer stops sending it new requests
> and waits for the ones in flight: the target group's deregistration delay, which is the longest
> any one request is given to finish. Then the process is told to stop: it closes its listener and
> waits `HostOptions.ShutdownTimeout`, a constant in `Platform.Hosting`, for anything still in
> flight, then aborts it. Last the task is killed: the container's `stopTimeout`, which workstream
> 1 sets longer than the process's own timeout, so the process always finishes on its own terms.
> The three numbers are a deregistration delay of 120 seconds, a shutdown timeout of 90 seconds
> and a stop timeout of 120 seconds. A request the proxy is forwarding is bounded by the entry's
> `timeoutSeconds` (`contracts/upstream-contract.md`), which can exceed every number here; such a
> request is aborted at the shutdown timeout and the browser sees its connection close.
>
> A login is in flight from the redirect to the provider until the callback. Until P2-2 gives a
> cell one key ring, a callback that lands on a task other than the one that began the login is
> refused with the failure page, so a deployment fails every login in flight. From P2-2 on, the
> callback succeeds on any task of the cell.
>
> Until P2-1, a stopped task takes every session it holds with it. From P2-1 on, it takes none.

The three numbers are proposals, each with its reason. 120 is the Fargate maximum for
`stopTimeout`, which is why the process's own timeout sits under it. 90 covers a code exchange on
the identity backchannel, whose own timeout is 60 seconds
(`src/Platform.Identity/IssuerDirectory.cs`). 120 for draining is shorter than the 300-second
default because an asset or `/bff` request takes
milliseconds and only a proxied call runs longer, where the entry's own timeout governs. The
integrator decides the numbers; the request is that they are written down together.

Whether the numbers belong in platform configuration instead, as a `Platform:ShutdownSeconds` the
task definition sets beside `stopTimeout` from one Terraform variable, is the one open question. It
would keep the two values from drifting, at the cost of a key in a closed section of
`contracts/platform-config.schema.json` that the smoke test and the harness would then need too.
The constant is proposed because the Kestrel limits set the precedent.

## Why each part lands when it does

The section is prose and changes no row, stage or signature: it describes what the composition
does once the three owners set their values. It lands on the integrator's decision under
`contracts/README.md` -> What may land between gates. `/readyz` is unchanged.

## What is proved, and where

- Workstream 2, in `tests/Platform.Hosting.Tests`: a host whose `ShutdownTimeout` is the constant
  finishes a request in flight that takes less than it, and aborts one that takes more.
- Workstream 1, as `terraform test` assertions on the cell module: the task definition's
  `stopTimeout` exceeds the constant, and the target group's deregistration delay is the stated
  value.
- Workstream 6: P2-4's acceptance, a rolling deployment and a killed task with live sessions,
  which needs P2-1 and P2-2 and runs with the deployed gate runs (#63). Hermetically, a second host
  in the harness that is stopped mid-request while the first keeps serving.

## Why this is a request rather than an edit

`contracts/pipeline.md` is the integrator's and froze at G1a.
