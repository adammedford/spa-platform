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
| `/readyz` | 200 with `{"revision": N}` once a snapshot is live, 503 before that |

The ALB health check and `spactl wait` both read `/readyz`. The health-check path is a Terraform
variable: `/` for the sample image, `/readyz` for the gateway (task 1.3).
