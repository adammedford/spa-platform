# Pipeline and entry points

- **Freezes at:** G1a
- **Consumed by:** everyone
- **Source:** `docs/plan/02-contracts.md` → Pipeline and entry points; `docs/plan/01-plan.md` → Request path
- **Changed at:** G1b. ADR-0018 put `UsePlatformTelemetry` before `UsePlatformForwardedHeaders`, so
  every rejection the application answers is logged and counted under its rule id; change
  request 6.4 raised it, and tasks 4.7a and 6.3b complete the method allowlist's feature write.

The order is security-relevant, so it is frozen with the contracts at G1a. `src/Gateway/Program.cs`
calls these entry points, in this order, and nothing else. A workstream fills in the entry points
this table assigns to it and adds no others.

## Service registration

Called in this order, before the pipeline is built.

| Entry point | Owner | Project |
| --- | --- | --- |
| `AddPlatformTelemetry` | 6 | `Platform.Telemetry` |
| `AddPlatformRegistry` | 2 | `Platform.Registry` |
| `AddPlatformHosting` | 2 | `Platform.Hosting` |
| `AddPlatformIdentity` | 3 | `Platform.Identity` |
| `AddPlatformProxy` | 4 | `Platform.Proxy` |
| `AddPlatformSecurity` | 4 | `Platform.Security` |

## Middleware

| Stage | Entry point | Owner | What the stage does |
| --- | --- | --- | --- |
| 1 | `UsePlatformTelemetry` | 6 | The request log line and the trace scope. It replaces the framework's default request log, which includes the query string. First, so that every rejection a later stage answers reaches the line and `security.rejections` under its rule id. |
| 2 | `UsePlatformForwardedHeaders` | 4 | `X-Forwarded-Proto` and `X-Forwarded-For` are trusted from `ForwardedHeaders:TrustedNetworks` only. `X-Forwarded-Host` is never read. The method allowlist runs here too: a method no path accepts (`TRACE`, `CONNECT`) gets 405 under `gate.method` before any later stage sees it. |
| 3 | `UsePlatformHealth` | 2 | Answers `/healthz` and `/readyz` on any host |
| 4 | `UsePlatformHostResolution` | 2 | The request authority must equal a registry `host` exactly. The app and the snapshot are pinned for the request as `IAppFeature`. Any other host gets 404 before routing runs. |
| 5 | `UsePlatformSecurityHeaders` | 4 | The app's content security policy and the fixed header set, on every response |
| 6 | `UsePlatformRequestGate` | 4 | `contracts/request-gate.md`. It runs before authentication. |
| 7 | `UseRouting`, then `UseAuthentication` | framework | The schemes come from workstream 3. The OpenID Connect callback is handled here. |
| 8 | `UsePlatformShellGate` | 3 | A session before any asset: an unauthenticated navigation is challenged, an unauthenticated fetch gets 401 |
| 9 | `UsePlatformAntiforgery` | 4 | Unsafe methods on `/api` need a valid `X-CSRF-TOKEN`. The check runs after the request gate because it needs the user. |
| 10 | `MapPlatformBff`, `MapPlatformProxy`, `MapPlatformAssets` | 3, 4, 2 | `/bff/*`; `/api/{name}/{rest}` through YARP; everything else from S3 |

Four orderings carry a reason, and none of them is a preference.

- **Telemetry comes first** so that every rejection the application answers, the method
  allowlist's 405 included, is logged on a request line and counted under its rule id
  (`contracts/request-gate.md`).
  It can come first because the request line carries no value forwarded headers settle: no scheme,
  no client address, no host (`contracts/telemetry.md`, ADR-0018).
- **Forwarded headers come before everything that reads the scheme or the client address**, and
  before the antiforgery cookie's `SecurePolicy = Always` is applied: a `__Host-` cookie sent
  without `Secure` is discarded (ADR-0007).
- **Health answers before host resolution**, so the ALB health check and `spactl wait` need no
  registry entry and no known host.
- **The request gate runs before authentication**, and the antiforgery check after it, because the
  token check needs the user and the gate does not.

## What the composed gateway can show before task 2.3

Stage 4's stub answers 404 to every host and calls nothing after itself, which is correct with no
registry: every host is then an unknown host. The consequence is worth stating here, because it is
invisible from the stage that pays it. **No stage after 4 runs in the composed gateway until task
2.3 resolves a host.** A stage-10 endpoint that is filled in, registered and correct answers nothing
there, and the gateway returns an empty 404 from routing.

So an entry point is proven in its owner's own test project, against the doubles, and workstream 6
proves it again in the composed suites once host resolution is real. A session reading that empty 404
as a fault in its own stage has found this stub instead.

## Health endpoints

| Path | Answer |
| --- | --- |
| `/healthz` | 200 while the process runs |
| `/readyz` | `200` with `{"revision": N, "instance": "<id>"}` once a snapshot is live, `503` before that. `instance` names the gateway instance that answered: in a cell, its ECS task id; anywhere else, an id unique to that instance. A reader takes the members it names and ignores the rest. |

The ALB health check and `spactl wait` both read `/readyz`. `spactl wait` can be given the instances a
cell runs, and then returns only when each has answered at the revision asked for or later
(change request P2-5, decided 2026-10-20). The health-check path is a Terraform
variable: `/` for the sample image, `/readyz` for the gateway (task 1.3).

## Start-up and shut-down

A task is sent traffic once `/readyz` answers `200`, which is at most one poll interval after it
starts, and the service gives it 60 seconds of grace before a failed check counts.

A task is stopped in three steps, each bounded. First the balancer stops sending it new requests
and waits for the ones in flight: the target group's deregistration delay. Then the process is told
to stop: it closes its listener and waits `HostOptions.ShutdownTimeout`, a constant in
`Platform.Hosting`, for anything still in flight, then aborts it. Last the task is killed: the
container's `stopTimeout`, which workstream 1 sets longer than the process's own timeout, so the
process always finishes on its own terms. The three numbers are a deregistration delay of 30
seconds, a shutdown timeout of 25 seconds and a stop timeout of 30 seconds, and they act in that
order. The drain promises no request's completion, a token refresh's included: requests drain from
the task during it. The shutdown timeout exceeds the refresh holder's 20-second write deadline,
timed from the send of its lease acquire (the 21-second lease less 1 second of clock skew between
tasks), so a successful refresh admitted before the process is told to stop writes its rotated
token before the process stops, and no provider refresh starts once the process is stopping: it is
refused with the session kept. The durable write is the promise, not the request's completion: a
failed or unknown write and the pause between the lease check and the provider call are its
exceptions, and a loser's wait for the lease is bounded by none of the three numbers. The stop
timeout is at least the shutdown timeout. A code exchange on the identity backchannel is bounded by
`ProviderDeadline`, 10 seconds, which the backchannel's timeout equals, so one in flight when the
process is told to stop ends inside the shutdown timeout. A request the proxy is forwarding is
bounded by the entry's `timeoutSeconds` (`contracts/upstream-contract.md`), which can exceed the
window; such a request can be cut by a deployment, and the browser sees its connection close.

A login is in flight from the redirect to the provider until the callback. Until P2-2 gives a cell
one key ring, a callback that lands on a task other than the one that began the login is refused
with the failure page, so a deployment fails every login in flight. From P2-2 on, the callback
succeeds on any task of the cell.

Until P2-1, a stopped task takes every session it holds with it. From P2-1 on, it takes none.

The numbers are #332's row F1, and the promise the shutdown timeout makes is the one #445 settled
(change request P2-3, second amendment, decisions 2 and 3); the section is change request #541,
decided 2026-10-30. Workstream 1 sets the deregistration delay and `stopTimeout` in
`infra/modules/cell`, and workstream 2 sets `ShutdownTimeout` in `Platform.Hosting`.
