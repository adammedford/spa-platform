# Telemetry

- **Freezes at:** G1a
- **Consumed by:** everyone
- **Source:** `docs/plan/02-contracts.md` → Telemetry; ADR-0007 → Logging rules this forces

Workstream 6 proposes this contract; the integrator writes it. One JSON log line per request, and
one event line for each login, refresh, logout and registry swap. Traces carry W3C context to
upstreams, and metrics are tagged by app.

| Kind | Names |
| --- | --- |
| Log fields | `ts`, `level`, `msg`, `trace_id`, `span_id`, `cell`, `app_id`, `registry_rev`, `route`, `rule`, `session_ref`, `status`, `duration_ms` |
| `route` values | `asset`, `api`, `bff`, `callback`, `health` |
| Metrics | `gateway.requests`, `auth.logins`, `auth.refreshes`, `auth.sessions.active`, `security.rejections`, `registry.revision`, `registry.age_seconds`, `registry.rejected`, `proxy.upstream.duration` |
| Never logged | `Authorization`, `Cookie`, `Set-Cookie`, query strings, tokens, authorization codes, state, client secrets, session keys |

`rule` carries the rule ids in `contracts/request-gate.md`, and `security.rejections` is counted by
the same id.

`session_ref` is the first 12 hex characters of a SHA-256 of the session key. It correlates a
session's requests without being usable as a credential. Log this, never the session key. Compute it
with `SessionRef.Of` in `src/Platform.Abstractions`, which fixes the two things the sentence above
leaves open: the key's UTF-8 bytes, and lowercase hex. Two writers must produce one reference.

## How `rule` and `session_ref` reach the request line

`UsePlatformTelemetry` is the first stage, and it writes the request line after the rest of the
pipeline has returned, so it can read what a later stage left on the request; but neither of these
values is its own to compute: the stage that rejected the request knows the rule, and only
workstream 3 holds the session key.

`IRequestTelemetryFeature` in `src/Platform.Abstractions` is where they are left. The telemetry
stage sets the feature on the request before calling the rest of the pipeline and reads it back
afterwards; every later stage writes it, null-conditionally, because a stage's own test project
runs without the telemetry stage. The rule id is written only by the stage that ends the request
with a rejection, so at most one stage writes it and nothing enforces write-once.

Two cases are deliberately not rejections and leave `rule` null: the 401 answered when no access
token can be had for a live session, for which `contracts/request-gate.md` has no id, and an upstream
API's own 4xx. Every 401 that *does* have an id in that table carries it — a cookie resolving to no
session for this app is `auth.session`, not a null.

Every rejection the application answers therefore reaches the line, the method allowlist's 405
included: it runs in `UsePlatformForwardedHeaders`, the second stage, inside the telemetry stage
that ADR-0018 made first. **It is the one rejection not yet in the count.** `MethodAllowlist` does
not write `IRequestTelemetryFeature` -- its own comment says the id "reaches the Warning line and
nothing else" -- so its request line carries `rule: null` and `security.rejections` does not move.
Until G1b the 405 reached neither; ADR-0018 closed half of that by making telemetry the first
stage, and the remaining half is one write in `src/Platform.Security/MethodAllowlist.cs`, which is
workstream 4's. What
the server refuses before any stage runs — Kestrel's 431 for oversized headers,
`contracts/http-contract.md` → Errors — is not a rejection in this contract's sense and carries no
rule id; `contracts/request-gate.md` says the same.

The request line carries no value forwarded headers settle: no scheme, no client address, no host.
That is what lets the telemetry stage run before `UsePlatformForwardedHeaders` without logging a
value the trusted-network rule has not yet been applied to, and it is held by the "Log fields" row
above: a line is those fields and nothing else. The span's own tags are the framework
instrumentation's, set when hosting starts the activity before any stage runs, so the order of
these two stages does not change them.

## The instruments

Every platform `Meter` and `ActivitySource` is named `Platform.<Project>` — `Platform.Registry` for
`registry.*`, `Platform.Identity` for `auth.*` — and a `Meter` is created through `IMeterFactory`
rather than with `new Meter(...)`. Workstream 6 subscribes metrics to `Platform.*` and nothing else,
and traces to `Platform.*` plus the proxy's own `Yarp.ReverseProxy`, so an instrument named outside
that shape is one nobody sees.

`security.rejections` is the exception to "the instrument belongs to the project that raises the
event": `UsePlatformTelemetry` counts it, from the same feature field it logs as `rule`, on its own
`Platform.Telemetry` meter. One owner then guarantees that the count equals the request lines
carrying a rule. It is tagged `rule` and `app_id`, and `app_id` is null for a request rejected before
the app was resolved.

## Why query strings are dropped

The callback carries the authorization code and state in the query string, and ADR-0007 accepts that
for Phase 1 because Cognito documents no `form_post` mode. Three things follow, and all three are
this contract's, not a workstream's preference:

- The framework's default request log includes the query string, so workstream 6 replaces it.
- The OpenID Connect handler logs the callback URL at Trace and redirect headers at Debug, so the
  authentication loggers are held at Information or above.
- Test N-16 runs the full suite with canary values for tokens, secrets, cookies, codes and state,
  then searches every log line. No canary may appear.

## What Phase 1 exports

Phase 1 exports the log stream and nothing else. OpenTelemetry tracing and metrics are wired in,
with the OTLP exporter switched on by `OTEL_EXPORTER_OTLP_ENDPOINT`. Phase 1 leaves it unset, so
there is no collector to run or pay for. Tests read spans and metrics through the in-memory
exporter.
