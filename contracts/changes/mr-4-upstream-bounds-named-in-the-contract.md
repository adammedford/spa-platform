# Change request MR-4: the upstream contract names what bounds a slow or busy upstream

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 4 (API and
  security), whose proxy the code is, for the Phase 2 spec issue (to be filed)
- **Tasks:** MR-4; 4.1 (the YARP configuration), 4.6 (request limits); 6.3 (the negative suite)
- **Date:** 2026-10-19
- **Status:** moved to #542, 2026-10-29, by the cutover #439

## What the proxy bounds today, and what it does not

`docs/roadmap.md` MR-4: "no upstream connect or response timeout is named in the contracts, no
per-app concurrency limit exists". Read from `src/Platform.Proxy`:

| Direction | Bound | Where |
| --- | --- | --- |
| Browser to gateway | Request line, header and body sizes: Kestrel's defaults, set explicitly | `src/Platform.Security/PlatformSecurityLimits.cs` (row 4.6) |
| Gateway to upstream, silence | The entry's `timeoutSeconds`, passed to YARP as `ForwarderRequestConfig.ActivityTimeout` | `src/Platform.Proxy/RegistryProxyConfig.cs`, `From` |
| Gateway to upstream, the connect | The same timeout: YARP's activity token cancels the send, connecting included, so an upstream that never accepts is `504` `upstream_timeout` at the entry's timeout and one that refuses is `502` `upstream_unavailable` at once | `src/Platform.Proxy/UpstreamFailure.cs`; `tests/Platform.Proxy.Tests/UpstreamFailureTests.cs` |
| Gateway to upstream, the whole exchange | Nothing. The activity timeout restarts on the response headers and on every body read or write (T-C17, T-C18 in `docs/plan/07-evidence.md`), so an upstream that sends a byte inside every interval holds the connection for as long as it likes | `RegistryProxyConfig.cs`, the comment on `ActivityTimeout` |
| Connections per upstream, per task | Nothing. No `HttpClientConfig` is set on a cluster, so the handler's default of unlimited connections per server applies | `RegistryProxyConfig.cs`: a `ClusterConfig` carries `Destinations` and `HttpRequest` only |
| Requests per client | Nothing. Rate limiting is deferred with "no abuse case yet" and no trigger | `docs/plan/06-phase-2-and-risks.md` -> Deferred features |

The contract's own words are narrower than the code. `contracts/registry.schema.json` describes
`timeoutSeconds` as "the entry's upstream timeout. Exceeding it is 504 `upstream_timeout`", and
`contracts/upstream-contract.md` -> Timeouts and failures says "the upstream exceeded the entry's
`timeoutSeconds`". A reader takes that for a bound on the call. It bounds silence.

## The change

Prose, in two files. No schema key, no new error code, no package.

1. **`contracts/upstream-contract.md` -> Timeouts and failures**, under the table:

   > `timeoutSeconds` is an idle limit. The proxy restarts it on the response headers and on every
   > body read or write, so it bounds how long an upstream may stay silent, not how long a call may
   > take; connecting counts as silence. The gateway holds at most a fixed number of connections to
   > each upstream origin per task, a constant in `Platform.Proxy` on the precedent of the Kestrel
   > limits; a request that waits for a connection past the entry's timeout is `504`
   > `upstream_timeout`, like any other silence. Nothing bounds a call whose upstream keeps
   > sending, and nothing limits requests per client: the first is accepted, and the second stays
   > deferred until a load test (P2-10) shows one app's traffic exhausting that connection bound
   > or the task's, which is the trigger `docs/plan/06-phase-2-and-risks.md` leaves open.

2. **`contracts/registry.schema.json`**, the `description` of `apis[].timeoutSeconds`: "The entry's
   upstream idle timeout: the longest the upstream may stay silent, restarted on headers and on
   every body byte. Exceeding it is 504 `upstream_timeout`." A description constrains no instance,
   so this is prose.

## What workstream 4 does with it

Set `HttpClientConfig.MaxConnectionsPerServer` on every generated cluster to the constant, in
`RegistryProxyConfig.From`, and prove in `tests/Platform.Proxy.Tests` that one more concurrent call
than the constant to a silent upstream answers `504` for the last one at the entry's timeout, with
the upstream having seen exactly the constant. The number is workstream 4's to measure against the
task's 0.25 vCPU and 1 GB (plan row 1.4); 64 per origin is a starting point, since a cell has two
upstreams and `MaxRequestBodySize` already bounds what each connection carries. Workstream 6
composes the same case and the two existing rows, `502` and `504`, which the composed suites do not
yet hold: `tests/Gateway.SecurityTests/Negative/UpstreamResponseTests.cs` covers an upstream's
`500` (N-12) and nothing slower.

## Why this is a request rather than an edit

`contracts/` is the integrator's. Both files froze at G1a, and both changes are prose, which lands
on the integrator's decision (`contracts/README.md` -> What may land between gates).

## Blocking

Nothing waits on it. Workstream 4 can set the bound and test it before the sentence lands; the
sentence is what lets the negative suite hold the gateway to it rather than to a number copied into
a test.
