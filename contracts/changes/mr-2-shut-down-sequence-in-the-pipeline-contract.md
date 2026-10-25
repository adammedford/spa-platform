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
> The three numbers are a deregistration delay of 30 seconds, a shutdown timeout of 25 seconds
> and a stop timeout of 30 seconds, and they act in that order. The drain covers the longest
> request the gateway makes on its own account, a token refresh with the acquisition of its lease,
> which is bounded by the 21-second lease; the shutdown timeout is at least that lease too, so a
> refresh in flight when the process is told to stop still finishes; and the stop timeout is at
> least the shutdown timeout. A request the proxy is forwarding is bounded by the entry's
> `timeoutSeconds` (`contracts/upstream-contract.md`), which can exceed the window; such a request
> can be cut by a deployment, and the browser sees its connection close.
>
> A login is in flight from the redirect to the provider until the callback. Until P2-2 gives a
> cell one key ring, a callback that lands on a task other than the one that began the login is
> refused with the failure page, so a deployment fails every login in flight. From P2-2 on, the
> callback succeeds on any task of the cell.
>
> Until P2-1, a stopped task takes every session it holds with it. From P2-1 on, it takes none.

The three numbers are #332's row F1, each with its reason (amended 2026-10-25; see the amendment
below). 30 for draining is shorter than the 300-second default because an asset or `/bff` request
takes milliseconds, and the longest request the gateway makes for itself, a refresh with its
acquisition, is bounded by the 21-second lease (`ProviderDeadline` 10 s + `LeaseMargin` 11 s,
`contracts/changes/p2-3-session-lease-abstraction.md`, the amendment of 2026-10-24). 25 for the
shutdown timeout is at least that lease, so a refresh that started before SIGTERM finishes. 30 for
`stopTimeout` is at least 25, so the process finishes on its own terms, and is also Fargate's
default. The 25 seconds cover a code exchange on the identity backchannel only once F1's
`BackchannelTimeout` = `ProviderDeadline` holds; until then the backchannel's own timeout is 60
seconds (`src/Platform.Identity/IssuerDirectory.cs`), and a code exchange that runs past the
shutdown timeout is aborted and its login fails. The integrator decides the numbers; the request is
that they are written down together.

Whether the numbers belong in platform configuration instead, as a `Platform:ShutdownSeconds` the
task definition sets beside `stopTimeout` from one Terraform variable, was the one open question. It
would keep the two values from drifting, at the cost of a key in a closed section of
`contracts/platform-config.schema.json` that the smoke test and the harness would then need too.
The constant is proposed because the Kestrel limits set the precedent, and #347's Q6 took it: a
`Platform.Hosting` constant registered by `AddPlatformHosting`.

## Why each part lands when it does

The section is prose and changes no row, stage or signature: it describes what the composition
does once the three owners set their values. It lands on the integrator's decision under
`contracts/README.md` -> What may land between gates. `/readyz` is unchanged.

## What is proved, and where

- Workstream 2, in `tests/Platform.Hosting.Tests`: a host whose `ShutdownTimeout` is the constant
  finishes a request in flight that takes less than it, and aborts one that takes more.
- Workstream 1, as `terraform test` assertions on the cell module: the gateway container's
  `stopTimeout` is 30 and the target group's deregistration delay is 30, each pinned to its exact
  value; the test names 25 as F1's shutdown timeout in a comment and reads nothing across the
  boundary (#347 Q5 and Q6, ADR-0015).
- Workstream 6: P2-4's acceptance, a rolling deployment and a killed task with live sessions,
  which needs P2-1 and P2-2 and runs with the deployed gate runs (#63). Hermetically, a second host
  in the harness that is stopped mid-request while the first keeps serving.

## Why this is a request rather than an edit

`contracts/pipeline.md` is the integrator's and froze at G1a.

## Amendment, 2026-10-25: #332 F1's numbers

On Adam's answer to Q5 of Overnight 2026-10-24 (#349), confirmed at
https://github.com/adammedford/spa-platform/issues/349#issuecomment-6385117829, which takes #347's
Q5, and for #359. The three numbers first proposed here, a deregistration delay of 120 seconds, a
shutdown timeout of 90 seconds and a stop timeout of 120 seconds, are replaced by #332's row F1:
deregistration delay 30 s, `ShutdownTimeout` 25 s and `stopTimeout` 30 s. The change above and the
paragraph of reasons under it carry the new numbers; the request is otherwise unchanged and still
open.

- **The relations, which are what the contract states.** The three act in the order drain,
  `ShutdownTimeout`, `stopTimeout`. The drain and the shutdown timeout each cover the 21 s lease,
  which bounds a refresh with its acquisition (#332 G1, confirmed at
  https://github.com/adammedford/spa-platform/issues/332#issuecomment-6389235562; #347's note "the
  refresh budget is 21 s, not 16 s"). Where #347 Q5 and #359 say the 16 s budget, read 21 s; every
  relation holds with either. `stopTimeout` is at least `ShutdownTimeout`.
- **The consequence the contract names.** An `/api` call whose entry's `timeoutSeconds` runs past
  the window can be cut by a deployment. The 120-second drain made that rarer and did not remove
  it, since `timeoutSeconds` can exceed any of the three.
- **The open question.** Platform configuration or a constant: #347's Q6 took the constant in
  `Platform.Hosting`, registered by `AddPlatformHosting`.
- **What waits on F1 beyond these numbers.** The 25 s cover a code exchange only once the
  handler's `BackchannelTimeout` equals `ProviderDeadline`, as F1 states; until then the
  backchannel's own timeout is 60 s.
- **Where the numbers are set.** `deregistration_delay` and the gateway container's `stopTimeout`
  by #347's T10 in `infra/modules/cell`, citing F1; `ShutdownTimeout` by workstream 2 in
  `Platform.Hosting`.

Deciding the request, writing the section into `contracts/pipeline.md`, is still the integrator's
separate act under `contracts/README.md` -> What may land between gates.
