# Workstreams and tasks

Each row is a task an orchestrator can hand to one agent: where it may write, what it needs first, and how it is checked.

## Repository layout

One repository. Every path has exactly one owner, shown in brackets.

```text
AGENTS.md  CLAUDE.md  CODEOWNERS  README.md  Brewfile                          [integrator]
global.json  nuget.config  Directory.Build.props  Directory.Packages.props     [integrator]
SpaPlatform.slnx                                                               [integrator]
.github/workflows/            build, tests, plan policy, path ownership        [integrator]
contracts/                    schemas, written contracts, changes/             [integrator]
src/
  Platform.Abstractions/      records and interfaces                           [integrator]
  Platform.Testing/           test doubles for every interface                 [integrator]
  Gateway/                    Program.cs, settings, Dockerfile                 [integrator]
  Platform.Registry/          validation, snapshot, S3 source, secrets         [2]
  Platform.Hosting/           host resolution, health, assets, SPA fallback    [2]
  Platform.Identity/          schemes, options, sessions, tokens, /bff         [3]
  Platform.Proxy/             YARP configuration and pipeline                  [4]
  Platform.Security/          request gate, antiforgery, headers, CSP          [4]
  Platform.Telemetry/         logging, traces, metrics, redaction              [6]
spikes/g0/                    the G0 harness, outside the solution             [3]
fixtures/
  spa/app1  spa/app2  spa/app3  bff-client.js                                  [5]
  MockApi/                    validates tokens on its own, with its Dockerfile [5]
  TestIdp/                    identity provider for hermetic tests             [5]
  Fixtures.Tests/             tests for everything under fixtures/             [5]
  apps/app1.json  apps/app2.json   app definitions                             [5]
  cognito/pool.json           resource servers and test users                  [5]
  onboarding/app3.json        app 3's definition, added during F-07            [5]
tests/
  Platform.*.Tests/           component tests, owned with the project they test
  Spactl.Tests/               component tests for spactl                       [2]
  Gateway.TestHarness/        gateway factory and test browser, shared         [6]
  Gateway.IntegrationTests/   functional suite                                 [6]
  Gateway.SecurityTests/      negative suite                                   [6]
  Gateway.ArchitectureTests/  reference rules                                  [6]
  e2e/                        browser suite for the deployed stack             [6]
infra/
  bootstrap/                  Terraform state bucket and CI plan role, once    [1]
  modules/cell/  modules/idp-cognito/                                          [1]
  envs/poc/platform/  envs/poc/idp/  envs/poc/apps/                            [1]
tools/
  spactl/                     registry and release command line                [2]
  scripts/                    certs, tunnel, seed, deploy, rollback, teardown  [1]
  preflight.sh                reports what a workstation is missing            [integrator]
docs/
  adr/                        decision records                                 [integrator]
  plan/  dependencies.md      snapshot of this plan; tools and packages        [integrator]
  runbooks/  gaps.md          operating notes and the gap register             [6]
```

## Rules for every task

- **Write only inside your owner's paths.** Reading anywhere is fine. CI rejects a change that touches another owner's path.
- **The integrator's paths are off limits**, even for a one-line fix. The one exception is adding a change request.
- **Need a contract changed?** Add `contracts/changes/<task-id>-<slug>.md` describing the change, and stop. The integrator decides and edits. A new project, a new package or a different package version goes the same way, because the solution file and the package versions are the integrator's.
- **Build against `Platform.Testing`**, not against another workstream's branch.
- **Fill in the entry points the pipeline contract assigns to you, and add no others.** `Program.cs` already calls them.
- **A test id under "Done when" names a behaviour.** Prove it in your own test project, against the doubles. Workstream 6 proves it again in the composed suites.
- **A task is done** when its tests pass in CI, it touched only its own paths, and it left no change request open.

## Integrator

Owns everything shared. I-1 to I-5 produce G1a. I-6 produces G1b once G0 has reported.

| Task | What | Needs | Done when |
| --- | --- | --- | --- |
| I-1 | Repository skeleton: solution, empty projects, build properties, central package versions with the full list, `AGENTS.md`, `CODEOWNERS` | Nothing | `dotnet build` passes on empty projects |
| I-2 | `contracts/` written from the Contracts tab, with JSON schemas that validate the example documents. The identity parts are marked draft until G1b. | Nothing | Schema tests pass |
| I-3 | `Platform.Abstractions` and `Platform.Testing`: in-memory registry, object store and session store, a fake secret source, token source and CSRF tokens, a stub profile, a settable app feature, a fake clock and an echo upstream | I-2 | Each double has tests |
| I-4 | `Program.cs` and the gateway `Dockerfile`, written once. Every entry point is called in the frozen order. Each starts as a stub in its owner's project, and the owner fills it in. | I-3 | The gateway starts on stubs and answers 404 to an unknown host |
| I-5 | CI: build, component tests, hermetic suites, architecture tests, Terraform validate and plan policy, and the path-ownership check | I-1 | Green on the skeleton. A change outside its owner's paths fails, which is A-05. With I-1 to I-4 also done, tag `skeleton-v1` marks G1a. |
| I-6 | Close G1b: fold the G0 findings into the identity contracts, record them as the first decision record, and tag the freeze | G1a, G0 | Tag `contracts-v1` exists |
| I-7 | Review each gate and decide change requests | Ongoing | Each gate has a signed checklist |

The package versions fixed in I-1, each confirmed on nuget.org on 11 October 2026: `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12, `Yarp.ReverseProxy` 2.3.0, `Duende.AccessTokenManagement.OpenIdConnect` 4.2.0 with `Duende.IdentityModel` 8.1.0 as its fallback, `AWSSDK.S3` 4.0.101.6, `AWSSDK.SimpleSystemsManagement` 4.0.104.2, the OpenTelemetry hosting, ASP.NET Core, HTTP, OTLP and in-memory exporter packages at 1.19, `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12 and `Microsoft.IdentityModel.JsonWebTokens` 8.19.2 for the fixtures, `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 for the fake clock, `xunit.v3` 4.0.2, `Microsoft.AspNetCore.Mvc.Testing` 10.0.12 and `Microsoft.Playwright` 1.63.0.

Two things follow from those versions. xUnit v3 4.x runs on Microsoft Testing Platform, so `global.json` selects that runner and tests start with `dotnet test --solution` or `--project`. The JSON Schema validator is an open decision: the current `JsonSchema.Net` package carries a maintenance-fee licence, so the skeleton pins 8.0.5, which declares MIT. `docs/dependencies.md` in the repository has every licence and the alternatives.

## Workstream 1: AWS and IaC

Owner: infra agent. Writes `infra/` and `tools/scripts/`. Until the gateway image exists, the service runs a public sample image that listens on 8080. The brief lists deployment and teardown under workstream 6. The scripts live here, beside the Terraform they drive, and workstream 6 runs them.

| Task | What | Needs | Done when |
| --- | --- | --- | --- |
| 1.0 | `infra/bootstrap`: the Terraform state bucket, private and versioned, applied once with local state. It also creates the read-only role that CI assumes to run plans. Every root then uses the S3 backend with its native lock file, on Terraform 1.11 or later. | Nothing | A second apply started during the first is refused by the lock |
| 1.1 | `modules/idp-cognito` and the identity root: a user pool on the Essentials plan, a prefix domain on managed login, and the resource servers and test users listed in `fixtures/cognito/pool.json`. Test passwords are generated into Parameter Store. No app clients. | 1.0, 5.5 | The root applies and the issuer's discovery document loads |
| 1.2 | `modules/cell` network: a VPC, two public subnets in two zones, no NAT. The ALB group accepts 443 from the task group only and sends 8080 to it. The task group accepts 8080 from the ALB group only and sends 443 out. | 1.0 | The plan has no NAT, bastion or database, and no other ingress rule |
| 1.3 | ALB: internal, one HTTPS listener with the imported wildcard certificate, one target group, host header preserved, invalid header fields dropped. The health-check path is a variable: `/` for the sample image, `/readyz` for the gateway. | 1.2, 1.6a | Applies cleanly. Task 1.6b checks it end to end. |
| 1.4 | ECS: a cluster, one ARM64 task with 0.25 vCPU and 1 GB, one service with a public address, ECS Exec on, the deployment circuit breaker on, and a log group. The task environment follows the platform configuration contract. The mock API container joins once its image exists, with its client allowlists set from the client ids of apps 1 and 2. The task role reads this cell's bucket prefixes and its client-secret parameter path, holds the four `ssmmessages` permissions the tunnel needs, and nothing else. | I-2, 1.3, 1.5, 1.8 | The service is steady on the sample image and its target is healthy |
| 1.5 | Bucket: private, versioned, TLS only, laid out as the S3 contract says. Under `apps/` the policy requires conditional writes and denies deletes to everyone but the teardown role. Two image repositories with immutable tags. | I-2, 1.0 | A second write to a release key is refused. So is a delete followed by a write. |
| 1.6a | `certs.sh`: a local certificate authority and a wildcard certificate for `*.spa-poc.test`, trusted on the workstation and imported to ACM under the platform tag. A second workstation trusts the same authority by fetching its certificate from ACM. | Nothing | The certificate is in ACM, and a local HTTPS listener on an app hostname is trusted |
| 1.6b | `tunnel.sh` and `verify-tunnel.sh`: a Session Manager port forward through the task to the ALB, and a check that the health path answers over it with trusted TLS. The path is a parameter: `/` on the sample image, `/healthz` on the gateway. | 1.4, 1.6a | `verify-tunnel.sh` exits 0 on a clean workstation |
| 1.7 | `seed.sh`, `deploy.sh`, `rollback.sh`, `teardown.sh`. Seeding publishes the fixture releases and writes the first registry through `spactl`. Deployment builds and pushes by digest, refuses to start a gateway against an empty registry, applies, waits for steady state, then asserts the running digest. Every other apply passes the digest already in state, so it never moves the image. Teardown ends with a tag query that must return nothing. | 1.4, 2.2, 2.3, 2.6, 5.1, 5.3 | Each script runs unattended on a gateway build that answers `/readyz`. Task 6.9 repeats them on the full gateway. |
| 1.8 | The apps root: for each definition in `fixtures/apps/`, one Cognito client, its managed-login branding style and one secret parameter. The client is confidential and code-grant only. Its callback and sign-out URLs are built from the definition's `host`. It gets the definition's scopes, 5-minute access tokens, a refresh token that outlives the longest session, refresh rotation with a 10-second grace, and revocation. `ALLOW_REFRESH_TOKEN_AUTH` is left off, because rotation rules it out. Terraform ignores later changes to the parameter's value, so a rotation is not undone. | 1.1, 5.5 | Apps 1 and 2 exist and a manual code-flow login works. The plan for a third definition shows those three resources only. G0 needs this. |
| 1.9 | Plan policy: an allowlist of resource types and counts for the platform root and the apps root, plus assertions on the two security groups and the task role | 1.2 to 1.5, 1.8 | P-01 to P-03 in CI. It fails on an unlisted resource type, a widened rule or a new permission. |

## Workstream 2: Hosting and registry

Owner: hosting agent. Writes `src/Platform.Registry`, `src/Platform.Hosting` and `tools/spactl`. Builds against the in-memory object store. Nothing here waits for identity, so all of it starts at G1a. The `oauth` section of an entry stays draft until G1b, and 2.1 and 2.6 absorb whatever G0 changes in it.

| Task | What | Needs | Done when |
| --- | --- | --- | --- |
| 2.1 | Registry model, schema check and the validator for every rule in the contract, as one pure library that the gateway and `spactl` share. It keeps errors and warnings apart and computes a fingerprint per entry. | G1a | Every rule in the Contracts tab has a passing and a failing case. Two loads of one document give equal fingerprints. S-D8, N-14. |
| 2.2 | The S3 object store, and the registry source over it: conditional GET on a jittered interval, secret preload, validate, swap, then notify. Any failure keeps the last good snapshot. At a cold start, an app whose secret cannot be read is left out and reported. Not ready until the first valid snapshot. | 2.1, 2.5 | Concurrent readers never see a partial snapshot. A bad document changes nothing. One unreadable secret at start-up costs one app, not all. The S3 store first meets a real bucket in task 1.7. |
| 2.3 | Host resolution and health: exact authority match, app and snapshot pinned per request as `IAppFeature`, 404 for unknown hosts, `/healthz` and `/readyz` answered on any host | 2.1 | N-08, S-A3, and `/readyz` reports the live revision |
| 2.4 | Asset endpoint: key built from registry values and a validated path, streamed from S3, conditional requests passed through, content type by extension, cache policy, fallback rules, reserved paths | 2.3 | Two hosts return two different shells, the hosting half of F-01. The F-06 fallback matrix and N-13 |
| 2.5 | `IClientSecretSource` over Parameter Store: version-pinned references, cached in memory by reference | G1a | No value appears in a log or exception message |
| 2.6 | `spactl`: `registry validate`, `registry apply` conditional on the live version, `registry rollback`, `release publish` write-once, `release activate`, `app add`, `app remove`, and `wait` until `/readyz` reports a revision. `app add` completes an app definition with the client id and secret reference from the infrastructure outputs. | 2.1, 2.2 | A full round trip against the in-memory store. `registry rollback` reads S3's version history, so task 6.9 proves it with the rest on the deployed bucket. |

## Workstream 3: OAuth and sessions

Owner: identity agent. Writes `src/Platform.Identity` and `spikes/g0`. This is the critical path. Task 3.0 is G0 and starts as soon as its inputs exist. The rest waits for G1b, then builds against the test identity provider, the in-memory registry and the fake secret source. Check ids starting `S-`, `R-` and `T-` are listed in the Evidence tab. Each expectation becomes a permanent test in the task that names it. Each finding is tested through its fix, which the Evidence tab names. The stock-provider checks, S-F2, S-F3 and R-D7, are not carried over.

| Task | What | Needs | Done when |
| --- | --- | --- | --- |
| 3.0 | G0. Items 1, 4 and 5 passed on 11 October 2026: the real-handler harness, the token-management library and YARP, each on its shipped package. What remains is Cognito: items 2 and 3, and the five questions the Plan tab lists under item 2. Report the five gate items to the integrator. | 1.8, 1.6a | The five G0 items pass, or the way out the Plan tab gives for the failed item is taken |
| 3.1 | Scheme provider: schemes cached per snapshot, defaults by host, foreign names unresolved, `AddScheme` unsupported | G1b | S-A7, S-B8, S-F1, R-A6, R-B6 |
| 3.2 | OIDC options setup: issuer from platform configuration, one shared backchannel and metadata manager per issuer, `ResponseType = code`, `ResponseMode = query`, PKCE, `SaveTokens`, no inbound claim mapping, scopes and `Resource` from the entry, `__Host-` login-state cookies on `Path=/`, `RemoteSignOutPath` cleared, explicit `SignInScheme`, explicit `Validate` | 3.1 | S-A1, S-A2, R-A1, R-A2, R-A7, R-D2, R-D4, R-D9. N-27. |
| 3.3 | Cookie options: `__Host-spa-session`, lifetime from the entry, no sliding expiry, a ticket-store adapter bound to app and client, and a cookie manager that hides the session cookie on the callback path. With 3.2 this completes a login. | 3.2 | S-A4 to S-A6, S-B1 to S-B7, S-C1 to S-C4, R-A3 to R-A5, R-A8 to R-A10, R-B1 to R-B5, R-D6. N-01 to N-05, N-18, N-21. |
| 3.4 | In-memory `ISessionStore`: serialized tickets, versioned replace, a capacity bound, an expiry sweep, purge by app | G1b | A store contract suite passes. The Phase 2 store must pass the same suite. |
| 3.5 | Events: answer non-navigations with 401 instead of a redirect, trim claims at sign-in, and show one failure page that reflects nothing from the request | 3.3 | A fetch gets 401 where a navigation gets a redirect. The stored ticket holds `sub`, `name` and `email`. Every failure shows the same page. |
| 3.6 | Options invalidator: evict when an entry's fingerprint changes or the entry goes, purge sessions on removal or a new client, swap before evict. A name whose build threw is evicted before the next request. | 3.3 | S-D1 to S-D7, R-C1 to R-C5. R-D10 no longer reproduces. The identity half of F-09, and N-22. |
| 3.7 | `IIdpProfile`: `standard` uses RP-initiated logout from discovery. `cognito` redirects to the logout endpoint on the issuer's browser origin with `client_id` and `logout_uri`, landing on `/`. | 3.3 | R-C6 and R-C7 for `standard`. The Cognito style on the test provider. Task 6.9 repeats it against Cognito. |
| 3.8 | `IAccessTokenSource` over Duende.AccessTokenManagement, with a sentinel default scheme and both `ChallengeScheme` and `SignInScheme` on every call. Refresh one minute before expiry. Only `invalid_grant` is a refusal: any other failure leaves the session and fails the call. Before a refusal ends the session, re-read it, and use the newer token if one is there. The token call never takes the request's cancellation token. | 3.3 | F-03, F-10, S-E2, S-E3, T-A1, T-A8, T-A9, T-B8 and T-B16 |
| 3.9 | `/bff/session`, `/bff/login`, `/bff/logout` and the shell gate | 3.3, 3.7, 3.8 | F-02, F-04, F-05, N-15, N-19, N-23, S-E4 |

Seven things the gate work turned up, each with a test:

- **The Cognito logout must not land on `/signout-callback-oidc`.** Cognito returns no `state`, and the handler then answers with an empty page (R-C7). The profile sends users to `/` instead.
- **Sign-in renews an existing session key.** Signing out first in `/bff/login` is not enough (R-D5). The callback must not see the session cookie, which is what the cookie manager in 3.3 is for.
- **The handler defaults to `form_post` and to a live `/signout-oidc`.** Both are wrong here (R-D8, R-D1), and 3.2 overrides both.
- **`AuthenticationProperties` stores `RedirectUri` under the key `.redirect`.** Custom items must not use that key. The spike hit this.
- **The token library's options hook reads the default challenge scheme once.** Under a host-scoped provider it cannot be built outside a request (T-B1), and inside one it binds to the first caller's app (T-B2). Task 3.8 gives it a sentinel. The refresh itself used the right client with or without one (T-A1, T-B3). The sentinel has a cost: a call without both scheme names works until the first refresh, then throws (T-A7).
- **The token library's own locking leaves a gap under rotation.** A request that read the session before another request's refresh finished replays the rotated-out refresh token (T-B6). Cognito's 10-second grace window absorbs that, and G0 item 2 confirms it. The re-read in task 3.8 covers a provider with no window (T-B8). Gateway-owned locking over the library's refresh call is the reserve design, in the Evidence tab.
- **No guard compares the client at challenge and callback.** The stand-in had one. In the real-handler run the provider refused the code when the client changed mid-login (R-C4), so the guard is not carried over.

## Workstream 4: API and security

Owner: proxy agent. Writes `src/Platform.Proxy` and `src/Platform.Security`. Builds against the fake token source and an echo upstream. Everything except 4.2 starts at G1a.

| Task | What | Needs | Done when |
| --- | --- | --- | --- |
| 4.1 | YARP configuration provider: one route per app and API, matched on host and `/api/{name}/**`, prefix removed, replaced atomically when the registry swaps | G1a | N-10 |
| 4.2 | Proxy pipeline: get the token before forwarding and answer 401 when there is none. Attach it. Strip `Cookie`, client `Authorization`, the CSRF header and the browser's forwarding headers: `Forwarded`, `Via`, `X-Real-IP`, and every `X-Forwarded-*` and `X-Original-*`. Strip `Set-Cookie` and `Access-Control-*` from responses on every status, which in YARP needs `ResponseCondition.Always`. | 4.1, G1b | N-11, N-12, N-19, T-C3 and T-C7 |
| 4.3 | Request gate from the rules table, one rule id per rejection, including the check on platform cookie names | G1a | N-07, N-25, N-27 |
| 4.4 | Antiforgery: header tokens only, so a request body is never read. `__Host-spa-csrf` with `SecurePolicy = Always`. App id bound into the token. Validation on unsafe `/api` methods. Cleared at logout. | G1a | N-06, R-E1, R-E2, R-E3b, R-E5, R-E6 |
| 4.5 | Security headers and the CSP builder: the baseline plus the entry's validated additions, provider origin in `form-action`, upstream copies overwritten | G1a | F-06 headers, N-24 |
| 4.6 | Forwarded headers from platform configuration through `KnownIPNetworks`, request limits, a method allowlist, problem-details errors | G1a | N-09, N-20 |

Two details matter in 4.2 and 4.4.

- **Token acquisition is a step in the proxy pipeline, ahead of the forwarder.** A YARP request transform can do the same job and can short-circuit with a 401; Duende's BFF works that way. The pipeline step is the default here because the 401 is then an ordinary response. G0 item 5 tried both on 11 October 2026, and both meet the contract (T-C21). The pipeline step stays.
- **Antiforgery reads a form body when no header token is present (R-E7).** `SuppressReadingTokenFromFormBody` must be on, or the gateway would consume a proxied request body while looking for a token.

## Workstream 5: Fixtures

Owner: fixtures agent. Writes `fixtures/`. Task 5.5 feeds G0, so it starts first. Task 5.4 starts on day one too, but G0 no longer waits for it: the items that remain run against Cognito.

| Task | What | Needs | Done when |
| --- | --- | --- | --- |
| 5.1 | Three static SPAs with no build step. Each shows the session, calls its API, posts with the CSRF header and logs out. Each has a visible marker and one hashed asset. App 2 needs one extra CSP source, to prove per-app policy. | G1a | Each works against stubbed `/bff` responses |
| 5.2 | `bff-client.js`: session fetch, API fetch with the CSRF header and 401 handling, login, logout by form | G1a | Unit tests in a headless browser |
| 5.3 | Mock API: its own project and image, with no reference to `Platform.*`. Two listeners stand for two APIs. Port 5080 is `orders` and checks `aud`, because app 1 uses resource binding. Port 5081 is `reports` and does not, because app 2 has none. Both check issuer, signature, expiry, `token_use`, their own client allowlist and scope. Routes: `/whoami`, `/echo`, `/admin`. | G1a | Each listener rejects each kind of wrong token in turn: F-08 and N-17 at the listener |
| 5.4 | Test identity provider, for tests only: discovery, keys, authorize, token with PKCE and rotating refresh with a grace window, revoke, end session in the standard and the Cognito style, `resource` mapped to `aud`, and switches to inject faults. It starts from the provider in the G0 token-and-proxy harness, which adds refresh, rotation, a grace window and revocation to the one in the review harness. Signing and key handling use Microsoft.IdentityModel. | Nothing | Conformance tests for each grant. |
| 5.5 | Cognito fixtures: `cognito/pool.json` with two resource servers and two test users, definitions for apps 1 and 2, and app 3's definition under `onboarding/`. Each app also lists the other's API as a second upstream, so N-17 has something to refuse. App 3 lists the reports API. | I-2 | Each definition validates against the registry schema with the client id, secret reference and release left out |

The test identity provider is hand-written, and it is the one place this plan writes protocol code. The brief rules that out where a supported library suffices. A real provider will not rotate a signing key, refuse a refresh or delay a response on demand, and the hermetic tier needs all three.

The exception is bounded. The provider is test-only, its signing is delegated to Microsoft.IdentityModel, and an architecture test fails the build if the gateway references it. N-03 to N-05 also run against Cognito, so the double never marks its own work alone. OpenIddict in process is the alternative if the exception is not acceptable.

## Workstream 6: Verification and operations

Owner: verification agent. Writes the gateway test projects, `tests/e2e`, `src/Platform.Telemetry`, `docs/runbooks` and `docs/gaps.md`. The suites are written against fakes while the other workstreams build, then run against the real pieces at each gate.

| Task | What | Needs | Done when |
| --- | --- | --- | --- |
| 6.1 | Harness: a gateway factory wired to the test provider, the real registry over an in-memory object store, the real mock API on two in-process listeners, and an echo upstream. A test browser that keeps cookies per host and sends Fetch Metadata headers. | G1a, 5.3, 5.4 | A stub gateway boots in the harness |
| 6.2 | Functional suite F-01 to F-11, hermetic | 6.1 | Green at G2, except F-07 at G5 |
| 6.3 | Negative suite, hermetic: N-01 to N-25 and N-27 | 6.1 | Green at G4 |
| 6.4 | Telemetry: JSON logs with redaction, OpenTelemetry traces and metrics, authentication loggers capped at Information, and the log canary test | G1a | N-16, F-11 |
| 6.5 | Browser suite for the deployed stack: every row the Tests tab marks D, including N-26 and the third-SPA scenario. Written against a local gateway before G3. | 6.1 | It passes locally, apart from what only AWS can show |
| 6.6 | Architecture tests: project reference rules, mock API independence, the test provider never referenced by the gateway, the token library referenced by one project | G1a | A-01 to A-04 in CI |
| 6.7 | Operating notes: deploy, rollback, release rollback, registry rollback, secret rotation, ending every session for an app, teardown. Each is the command sequence the scripts already run. | G3 | Task 6.9 exercises deploy and teardown. The rest are walked through once. |
| 6.8 | The enterprise gap register, kept current from findings | Ongoing | Reviewed at G5 |
| 6.9 | The deployed gate runs. First seed, deploy, run 6.5, tear down and deploy again. Then the deployed negative subset. Then the third SPA. | G2, 1.6b, 1.7, 6.5 | G3: F-01 to F-06 and F-08 deployed, and P-04 to P-06. G4: the deployed negative subset. G5: F-07. |

## Dispatch order

What an orchestrator can start, and when. I-7 and 6.8 run throughout, so they have no row. Tasks in a row run in parallel unless one names another under "Needs".

| Opens when | Tasks |
| --- | --- |
| Day one | I-1, I-2, 1.0, 1.6a, 5.4 |
| Their inputs land, before G1a | I-3, I-4, I-5 and 5.5. In workstream 1: 1.2 and 1.5, then 1.1, 1.3, 1.8, 1.4, 1.6b and 1.9. |
| 1.8 and 1.6a are done | The Cognito part of 3.0, which completes G0. It does not wait for G1a. |
| G1a | Workstream 2. 4.1 and 4.3 to 4.6. 5.1 to 5.3. 6.4 and 6.6. Then 6.1 once 5.3 exists, with 6.2, 6.3 and 6.5 behind it, and 1.7 once 2.2, 2.3, 2.6, 5.1 and 5.3 exist. |
| G1a and G0 | I-6, which is G1b |
| G1b | 3.1 to 3.9, and 4.2 |
| G2 | 6.9, which carries G3, G4 and G5 |
| G3 | 6.7 |

The longest chain is I-2, 5.5, 1.1, 1.8, 3.0, I-6, 3.1 to 3.3, 3.8, 3.9, then 6.9 through the gates. Everything else has slack against it.
