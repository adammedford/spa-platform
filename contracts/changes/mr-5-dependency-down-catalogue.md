# Change request MR-5: a catalogue of what the gateway does when a dependency is down

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 2 (Hosting and
  registry) and workstream 6 (Verification and operations), for the Phase 2 spec issue (to be
  filed)
- **Tasks:** MR-5; 2.2 (the registry source), 2.4 (assets), 2.5 (client secrets), 3.2 and 3.8
  (discovery and refresh), 3.4 (the session store), 4.1 and 4.2 (the proxy), 6.3 (the negative
  suite); P2-1 (the durable store), P2-5 (the instance id), P2-7 (export)
- **Date:** 2026-10-19
- **Status:** open

## What exists

`docs/roadmap.md` MR-5 asks, per dependency, for "the status, the body, the log, whether the
snapshot stays live" and one test. Read from the tree, with the code that decides each case and the
test that holds it:

| Dependency, case | What the code does | Contract | Test |
| --- | --- | --- | --- |
| S3, the registry object cannot be read on a poll | Warning "could not be read ... Revision N stays live", outcome `Failed`, next poll one interval on; `/readyz` stays `200` at N | ADR-0002 -> Consequences ("survives ... an S3 outage"); `GLOSSARY.md` -> Last known good | `tests/Platform.Registry.Tests/RegistrySourceTests.cs` (`Failed`); no composed test |
| S3, the registry object is missing | Warning, outcome `Missing`. At a cold start the task never turns ready, which is why `deploy.sh` refuses an empty registry (row 1.7) | `docs/plan/05-infrastructure-and-cost.md` -> First deployment | `RegistrySourceTests.cs` (`Missing`) |
| S3, the registry document is invalid | Rejected whole, one Error line per finding, the live snapshot stays | ADR-0002; `contracts/README.md` | F-09 and N-14, composed |
| S3, an asset read fails while serving | Unhandled. `src/Platform.Hosting/Assets.cs` awaits `IObjectStore.GetAsync` with no catch, and `S3ObjectStore` propagates everything but a 304 and `NoSuchKey`. The framework answers `500` with no body; the request line says `500` and no rule | None | None |
| Parameter Store, a secret cannot be read during a rotation | One Warning per reference naming the error code and never the value. With a live snapshot the document is refused whole (`SecretsUnreadable`) and read again next poll; `/readyz` stays `200` at the old revision | `contracts/README.md` -> the `clientSecretRef` row; D3 | F-09's rotation case covers the happy path; the unreadable case is in `RegistrySourceTests.cs` only |
| Parameter Store, unreachable at a cold start | Every app is left out, nothing is worth serving, `/readyz` stays `503` "so the load balancer keeps the tasks that still work" (`RegistrySource.cs`, the cold-start comment, decided by the implementer) | None in words | `RegistrySourceTests.cs` |
| The provider, discovery unreachable at a cold start | Discovery is fetched on the first login, not at start-up (`src/Platform.Identity/IssuerDirectory.cs`: a `ConfigurationManager` over a backchannel with a 60-second timeout). What `/bff/login` answers when it fails has not been measured; the handler's challenge throws, which the framework turns into a `500` | None | None |
| The provider, a refresh fails other than with `invalid_grant` | `503` `idp_unavailable`, the session kept, no rule id | `contracts/http-contract.md` -> Errors, from change request 4.2 | `tests/Gateway.IntegrationTests/Functional/RefreshTests.cs`, composed |
| The session store, at `Session:MaxSessions` | A login succeeds and the session nearest its expiry is ended (`MemorySessionStore.EvictNearestExpiry`, decided at task 3.4); that session's next call is `401` `unauthenticated` | None: the plan names the bound and nothing names what happens at it | `tests/Platform.Identity.Tests/MemorySessionStoreTests.cs` |
| The session store, unreachable | Not a Phase 1 case; P2-1's store will have one | None | None |
| An upstream, unreachable or silent past the timeout | `502` `upstream_unavailable` or `504` `upstream_timeout`, with the problem body | `contracts/upstream-contract.md`; `http-contract.md` -> Errors | `tests/Platform.Proxy.Tests/UpstreamFailureTests.cs`; no composed test |
| An upstream answers `5xx` | Passed through, minus `Set-Cookie` and `Access-Control-*`, under the gateway's headers | `contracts/upstream-contract.md` -> Returned to the browser | N-12, composed |
| Telemetry export, the OTLP endpoint unreachable (P2-7) | The exporter drops and logs; no request is affected. Phase 1 sets no endpoint | `contracts/telemetry.md` -> What Phase 1 exports says only that | None |

Two rows carry nothing a supporter could read, and one of them is a `500` with an empty body on
the most common request the gateway serves.

## The change

1. **A table in `contracts/http-contract.md` after Errors, headed "When a dependency is
   unavailable".** Each row: the dependency and the case, the answer to the browser, what is
   logged, and whether the live snapshot stays. The rows above that already have a contract are
   restated in one line each, so a supporter reads one table; the new content is:

   | Dependency, case | Answer | Logged | Snapshot |
   | --- | --- | --- | --- |
   | The object store cannot be read for an asset | `503` `store_unavailable`, no rule id | One Warning per request, naming the app and the error type, never the key's content | Stays |
   | Parameter Store cannot be read at a cold start | `/readyz` stays `503`; every app path is `404` `not_found` | One Error per poll | There is none |
   | The provider cannot be reached for discovery | `/bff/login`: `503`, the static failure page since a person arrived by navigation, no rule id. The callback: the failure page, rule `auth.callback`, as for any callback the handler refuses; whether it carries `503` rather than `400` for an outage is workstream 3's to decide with the row | The provider's error at Warning, without its fields | Stays |
   | The session store is at its bound | The login succeeds; the session nearest its expiry ends, and its next request is `401` `unauthenticated` | One Information line naming the ended session's `session_ref` | Stays |
   | The session store cannot be reached (from P2-1) | To be decided with P2-1; proposed `503` `session_store_unavailable`, the cookie kept | | Stays |
   | The task metadata endpoint cannot be read at start-up (from P2-5) | `/readyz` carries a generated `instance`; one Warning at start-up | | n/a |
   | The telemetry endpoint cannot be reached (from P2-7) | No request is affected; the exporter's own Warning | | Stays |

2. **One row in the Errors table**, since it is a status and a code a browser can see:
   `503 | store_unavailable | The object store could not be read to serve the asset; the live
   snapshot is unaffected`. The `idp_unavailable` row's "When" is unchanged: the discovery case
   above answers a navigation with a page, not a problem body.

Each is marked by what it is under `contracts/README.md` -> What may land between gates:

- The table is prose.
- `store_unavailable` is a new `code`. It is not a rule id: the request is not a rejection and
  `rule` stays null, as `idp_unavailable` does (change request 4.2). An added row that no existing
  stage, owner or signature has to change for is the added-type case, and the integrator decides
  whether it lands now or at G6. Workstream 2 writes it in `Assets.cs` either way, since a `500`
  with no body is not what the gateway means.
- `session_store_unavailable` is a placeholder for P2-1's own request. It is here so the table has
  a row to fill rather than a case to remember.

## What each owner does with it

- Workstream 2: catch the store's failure in `Assets.cs` and answer the row with the Warning as
  stated; prove it against a throwing `IObjectStore` in `tests/Platform.Hosting.Tests`. Put the
  cold-start decision's reason beside the row it now has.
- Workstream 3: measure what `/bff/login` answers today when discovery fails, then make it the row;
  log the provider's error without its fields, as N-16 requires.
- Workstream 6: one composed case per row, with the harness's doubles: a store that throws for one
  key, a provider whose discovery is switched off, an upstream that stays silent, a session store
  at a bound of one. The harness already has fault switches on the provider
  (`GatewayHarness.Idp.Faults`), and a throwing object store is one `IObjectStore` wrapper away.
- Workstream 1: nothing. The deployed tier cannot take S3 or Parameter Store down and does not try.

## Why this is a request rather than an edit

`contracts/http-contract.md` is the integrator's and froze at G1a.

## Blocking

Nothing in Phase 1 waits on it. The `store_unavailable` answer waits for the decision on when its
row lands; every other row describes behaviour that exists and needs only writing down and a
composed test.
