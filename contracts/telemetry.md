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
session's requests without being usable as a credential. Log this, never the session key.

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
