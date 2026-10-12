# Enterprise SPA Platform: Architecture and Parallel Execution Plan

Oct 11, 2026 · @Adam Medford

## Verdict

The feasibility gate passes. ASP.NET Core 10 can serve a distinct OAuth client per SPA host, and onboard, rotate and remove clients at runtime, using the framework's own authentication middleware. No restart, no custom protocol code and no custom authentication middleware are needed.

The mechanism was run twice on .NET 10.0.12. My spike used a stand-in for `OpenIdConnectHandler`, because the sandbox could not reach NuGet: 34 of 34 checks passed. An independent review then compiled the real handler from unmodified `release/10.0` source and ran the same mechanism against a hermetic OpenID Connect provider. There, 32 of 32 expectations held, covering discovery, PKCE, signed ID tokens, nonce and logout. I reproduced that run, then repeated it with the plan's option overrides switched on.

Three of Gate G0's five items passed on 11 October 2026. The same run against the shipped package gave the same verdict on every check, and a second harness passed the token-management library and YARP on their shipped packages. Nothing has yet run against Cognito. G0 covers that before any identity work builds on the result.

The same review found three security defects in the first draft of this plan: login cookies a sibling SPA could plant, a logout endpoint the framework enables by default, and a session-fixation gap at the callback. It also confirmed two framework defaults that break login and the antiforgery cookie unless they are overridden. All of it is corrected here, and the Evidence tab lists each item with the probe that showed it.

The plan that follows commits to this architecture:

- **Schemes are derived, not registered.** A replacement `IAuthenticationSchemeProvider` resolves `oidc:{appId}` and `cookie:{appId}` from the request host and the current registry snapshot. A request on one host cannot name another app's scheme.
- **Options are built from the registry by scheme name** and evicted when an entry changes. The framework then derives state and cookie encryption keys from the scheme name, so apps are cryptographically separate without extra code.
- **Tokens stay in a server-side session** behind `ITicketStore`. The browser cookie carries a session id and nothing else.
- **Six workstreams run in parallel behind three early gates.** G1a freezes the shared skeleton, G0 confirms the mechanism, and G1b freezes the identity contracts. Identity is the critical path; everything else builds against fakes.

The gate work also changed three things in the brief's design, each covered below. Sibling SPAs on one parent domain are same-site, so `SameSite` cookies do not separate them. Cognito's logout is not standard OpenID Connect. Registering every app's schemes on the stock provider costs one handler per app on every request.

This tab is the plan. The detail agents work from is in the other tabs: Contracts, Workstreams and tasks, Tests, Infrastructure and cost, Phase 2 and risks and Evidence.

## Feasibility gate

The framework supports runtime OAuth clients. At this scale, and with per-app isolation, that means scoping schemes to the request host. Registering every app's schemes globally costs a handler per app on every request and lets any host name any scheme. `IOptionsMonitor` alone adds no schemes at all.

### What the framework does

Read from the ASP.NET Core `release/10.0` source. Each row is a behaviour the design depends on or must work around.

| Behaviour | Where | Consequence |
| --- | --- | --- |
| Every request, the middleware asks the scheme provider for all request-handling schemes and constructs each handler. | `AuthenticationMiddleware.Invoke` | Globally registered, N apps cost N handlers per request, and each handler sees every host's traffic. |
| A remote handler claims a request when the path equals its `CallbackPath`. | `RemoteAuthenticationHandler` | On the stock provider every app's handler inspects every callback. With default options the first one fails the login. With `SkipUnrecognizedRequests` each tries to decrypt the state in turn. |
| A handler reads its options by scheme name once per request. | `AuthenticationHandler.InitializeAsync` | Options can change between requests. A request in flight keeps one consistent set. |
| A named options instance is rebuilt only when its cache entry is evicted. Options cannot add or remove schemes. | `OptionsMonitor`, `OptionsCache` | The brief's warning holds. Eviction must be explicit: `IOptionsMonitorCache<T>.TryRemove(name)`. |
| State and cookie protectors are derived from the scheme name. | `OpenIdConnectPostConfigureOptions`, `PostConfigureCookieAuthenticationOptions` | Per-app scheme names give per-app encryption purposes with no extra code. |
| Each OIDC options instance creates its own `HttpClient` and metadata manager unless given one. | `OpenIdConnectPostConfigureOptions` | Hundreds of apps on one issuer must share one backchannel and one `ConfigurationManager`. |
| Options validation and the default sign-in scheme are wired only for statically named schemes. | `AuthenticationBuilder` | The platform calls `Validate` itself and sets `SignInScheme` per app. |
| With `SessionStore` set, the cookie holds a session-id claim. The ticket and saved tokens stay in the store. | `CookieAuthenticationHandler` | `SaveTokens` with `ITicketStore` meets "tokens server-side only". |

### Defaults the design overrides

Six framework defaults are wrong for this platform. Each was confirmed in the real-handler run. Four are fixed by one setting each, and two need a few lines of code.

| Default | What it allows | Override |
| --- | --- | --- |
| `ResponseType` is `id_token` and `ResponseMode` is `form_post` | A form-post callback is a cross-site POST. It arrives without `SameSite=Lax` login cookies, and the login fails. | `ResponseType = code`, `ResponseMode = query` |
| Correlation and nonce cookies carry framework names and are scoped to the callback path | A sibling SPA can plant them for the parent domain and sign a victim in as the attacker's user | Names `__Host-spa-corr.` and `__Host-spa-nonce.`, with `Path=/` and `SameSite=Lax` |
| `RemoteSignOutPath` is `/signout-oidc` | A plain GET signs the user out, with no token and no origin check | Cleared. Logout is the same-origin `POST /bff/logout` only. |
| Sign-in renews an existing session key when a valid session cookie is present | A session key planted before the callback survives the login and then carries the victim's identity | The session cookie is not read on the callback path, so every sign-in creates a new key |
| The antiforgery cookie's `SecurePolicy` is `None` | A `__Host-` cookie sent without `Secure` is discarded by browsers | `SecurePolicy = Always`, after forwarded headers are applied |
| A failed options build is cached under its name | One transient failure repeats on every request until the name is evicted | No I/O in options setup, and the name is evicted on failure |

### The mechanism

Three small classes, about 150 lines in the spike. No handler is subclassed and no middleware is added.

1. **`RegistrySchemeProvider`** replaces the stock `IAuthenticationSchemeProvider`. It resolves the app from the request host, pinned once per request, and returns that app's two schemes as the defaults. Any other scheme name resolves to nothing.
2. **Named options setup** implements `IConfigureNamedOptions` for OIDC and cookie options. It reads the registry entry behind `oidc:{appId}` or `cookie:{appId}`. The framework's own post-configuration then runs as usual.
3. **`OptionsInvalidator`** runs after each registry swap and evicts cached options for entries whose fingerprint changed or disappeared. The order is fixed: swap the snapshot, then evict. Once eviction returns, no stale entry remains. For the instant between the two, a request can still see the previous options.

### Evidence from two runs

Both ran on .NET 10.0.12 in one process: an authorization server, the gateway and a cookie-carrying test client. Check ids starting `S-` come from the stand-in spike, and `R-` from the real-handler run. Both sets are listed in the Evidence tab.

| Requirement in the brief | Stand-in run | Real-handler run | Result |
| --- | --- | --- | --- |
| Host-based scheme selection, challenge and callback dispatch | S-A1 to S-A7 | R-A1 to R-A6 | Pass. Each host challenges with its own client and redirect URI on a shared callback path. Unknown hosts get 404 on every path. One handler per request. |
| Independent client IDs, secrets, scopes, redirects, cookies | S-A1, S-A2, S-B1 to S-B5, S-E3 | R-A1, R-A2, R-A5, R-A10 | Pass. Scope and `resource` differ per app. One app's scheme cannot decrypt another's cookie. |
| State and correlation during configuration changes | S-C1 to S-C3, S-D4, S-D5 | R-B1 to R-B4, R-C3, R-C4 | Pass. A login that straddles a reload completes, with its nonce and PKCE verifier intact. A client replaced mid-login is refused. |
| Application-bound sessions, no cross-client authentication | S-B6 to S-B8, S-C1 to S-C4, S-D6 | R-A10, R-B1 to R-B6 | Pass. Replayed cookies, forged session keys, replayed callbacks and injected codes are rejected. |
| Onboarding, rotation, removal without restart | S-D1 to S-D3, S-D7, S-D8 | R-C1, R-C2, R-C5 | Pass. Same process throughout. An invalid registry is rejected whole. |
| Server-side tokens, refresh, logout | S-B3, S-B4, S-E1 to S-E4 | R-A8, R-C6 | Pass. Access, refresh and ID tokens sit in the store. In these two runs refresh ran in the stand-in only, where 25 concurrent calls caused one grant. G0 item 4 has since run it through the real token library. |
| One metadata fetch for every app | Not covered | R-A7 | Pass. Discovery and signing keys were fetched once. |
| Control: stock provider, every scheme registered | S-F2, S-F3 | R-D7 | 303 handlers per request at 303 apps, and 2.3 to 3.6 times the request time. A shared callback path works only with `SkipUnrecognizedRequests`. |

### Alternatives compared

| Option | Finding | Decision |
| --- | --- | --- |
| Host-scoped provider with per-app named schemes | Framework-native. Isolation comes from scheme names the framework already keys on. | Chosen |
| Named schemes added to the stock provider at runtime | The documented API. Every app's handler runs on every request, and any host can address any scheme by name. | Rejected |
| `IOptionsMonitor` reload on one scheme | One scheme is one client. Reload changes it for every host at once. | Rejected |
| Our own tenant-aware middleware ahead of authentication | It would re-implement default-scheme selection and callback dispatch, which the scheme provider already offers as a seam. Custom security code with no natural bound. | Rejected |
| One shared scheme with per-tenant options, as in Finbuckle.MultiTenant 10.1 (Apache-2.0) | Workable, but the scheme name is shared, so protector purposes are too unless reconfigured. Version 10.1.2 (July 2026) fixed cross-tenant options invalidation and callback state handling. | Not chosen. First fallback if G0 fails. |
| Duende BFF v4 multi-frontend (December 2025, commercial) | Uses the same mechanism: a custom scheme provider, per-frontend named schemes, cache eviction on change. Its source confirms the design. | Buy-instead option. It would replace the identity workstream and part of the proxy workstream. |
| A dedicated tenant-aware OIDC client stack, outside the framework's handler | Not needed while the native route works. Not evaluated in depth. | Not pursued. Compared if G0 fails. |

### What Gate G0 must still prove

G0 is the first gate on the critical path. It needs NuGet, a Cognito user pool with two app clients, and a workstation that can serve two HTTPS hostnames.

1. Done on 11 October 2026. The real-handler run passed with the shipped `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12 package and every override above switched on, on a Mac with Apple silicon. All 32 expectations held, and each of the 41 checks gave the same verdict as the source build. The package resolved IdentityModel 8.19.2, the version the source build ran on.
2. Two Cognito clients complete login, callback, refresh and logout. Resource binding sets the access token `aud`. Refresh rotation returns a new refresh token. G0 records what Cognito's discovery document advertises for logout. It also answers five questions that item 4 raised:
   - What does a rotated-out refresh token get inside the 10-second grace window, and does a replay after the window harm the current token?
   - Does revoking a refresh token revoke its successor?
   - How long are refresh tokens, at login and after a refresh? The token library refuses one over 4,096 characters.
   - Does `aud` survive a refresh that sends no `resource`?
   - Does discovery list a revocation endpoint?
3. Cognito accepts a callback URL with a port, and managed login works for a client that Terraform created.
4. Done on 11 October 2026. Duende.AccessTokenManagement 4.2.0 works under derived schemes, on its shipped packages: with the first request on app 2, a refresh on app 1 used client 1. The hazard is narrower than this plan first said. The library finds the client through the default challenge scheme at call time, so that test passes with or without a sentinel. What reads the default challenge scheme once is the library's options hook. Without a sentinel it cannot be built outside a request, and inside one it binds to the first caller's app. The fixed sentinel scheme stays for that reason, with both `ChallengeScheme` and `SignInScheme` on every call. A call that omits them works until the first refresh, then throws.
5. Done on 11 October 2026. The shipped Yarp.ReverseProxy 2.3.0 assembly, built for .NET 8, proxied GET and POST requests on the .NET 10.0.12 runtime. Routes followed the registry with no restart, and each app's call carried its own client's token.

Items 4 and 5 ran in one harness: 43 expectations held and none failed. The Evidence tab lists the checks, what they changed in tasks 3.8 and 4.2, and the reserve design for refresh.

Each item has a way out. If item 3 fails on the port, the proof of concept binds local port 443 instead. If a client that Terraform made cannot use managed login, its branding style is created through the AWS CLI. If item 4 fails, the fallback is one in-house class that calls the refresh grant through Duende.IdentityModel. If item 5 fails, YARP's main branch is built from source and tried before any proxy work continues. That branch is a 3.0 preview and is not on nuget.org.

If item 1 or 2 fails with no fix inside the task, identity work stops. The integrator then compares what remains: Finbuckle's per-tenant options, buying Duende BFF v4, and a dedicated tenant-aware OIDC client stack. Unbounded custom middleware is not on that list.

The brief asks that feasibility be resolved before the architecture is committed. Nothing identity-shaped starts before G0. Nearly all the work that starts earlier is needed whichever way G0 goes. The exception was the YARP route provider. Item 5 has passed, so only a move to Duende BFF would change it.

## Architecture

One internal ALB, one gateway service and one private bucket serve every SPA. An app exists only as a registry entry, a release prefix in S3 and an OAuth client.

&#91;embedded content: request path · shared components and per-app data\]

Every host takes the same path from top to bottom. The three stages the diagram numbers run before any endpoint, and the request path below lists every stage. Only the highlighted band differs per app.

### Request path

The order is security-relevant, so it is frozen with the contracts at G1a.

1. **ALB.** Terminates TLS on a wildcard certificate and forwards to the single target group with the `Host` header preserved.
2. **Forwarded headers.** `X-Forwarded-Proto` and `X-Forwarded-For` are trusted from the ALB subnets only. `X-Forwarded-Host` is never read.
3. **Host resolution.** The request authority must equal a registry `host` exactly. The app and the registry snapshot are pinned for the request. Any other host gets 404 before routing runs.
4. **Security headers.** The app's content security policy and the fixed header set are attached to every response.
5. **Request gate.** `/api` and `/bff` accept same-origin browser requests only, judged by `Sec-Fetch-Site` and `Origin`. A cookie name that imitates a platform cookie is refused on any path.
6. **Authentication.** The framework middleware runs with this host's two schemes. The OIDC callback is handled here. The framework's remote sign-out path is switched off.
7. **Shell gate.** An unauthenticated navigation is challenged. An unauthenticated fetch gets 401, never a redirect.
8. **Antiforgery.** Unsafe methods on `/api` need a valid header token. `/bff/logout` is a form post, so it relies on the request gate instead.
9. **Endpoint.** `/bff/*` serves session, login and logout. `/api/{name}/*` goes through YARP to one approved upstream with the session's access token. Everything else is an S3 asset or the SPA fallback.

The Contracts tab names the entry point and owner for each stage.

### Decisions

| # | Decision | Rejected | Why |
| --- | --- | --- | --- |
| D1 | Per-app schemes resolved by host through a registry-backed scheme provider | Global schemes; one scheme with reloaded options | See the feasibility gate. |
| D2 | Registry is one versioned JSON document in S3, polled with a conditional GET, validated, then swapped whole | DynamoDB table; AppConfig; one object per app | Atomic by construction and no new resource. S3 versioning gives history and rollback. |
| D3 | Client secrets live in SSM Parameter Store, referenced by name and version, and are resolved before a snapshot goes live | Secrets in the registry; Secrets Manager | Standard parameters cost nothing. A versioned reference makes rotation a registry change. A snapshot with a missing secret never replaces a working one. |
| D4 | Sessions use the cookie handler with `ITicketStore` and `SaveTokens` | Tokens in the cookie; a separate token vault | The framework-native path. One serialized record per session, bound to app and client. |
| D5 | Refresh and revocation use Duende.AccessTokenManagement behind our own `IAccessTokenSource` | Hand-written refresh | Apache-2.0 library for the protocol step. Our interface keeps it replaceable. G0 item 4 passed on the library's token manager as shipped. Gateway-owned locking over the library's refresh call is the reserve design, in the Evidence tab. |
| D6 | YARP routes are generated from the registry: host plus `/api/{name}/**` to a fixed upstream | Destinations derived from the request | No request input reaches destination selection, so the proxy cannot be steered. |
| D7 | Cross-app defence is `__Host-` on every cookie, Fetch Metadata `same-origin`, antiforgery tokens and no CORS | Relying on `SameSite` | Sibling SPAs share a registrable domain. To a browser they are same-site, and a sibling can set cookies for the parent domain. |
| D8 | The gateway requires a session before serving any asset | Public shell with SPA-side login, deferred as a per-app option | Internal apps leak nothing before login and need no login code. It forces `SameSite=Lax`, which D7 already assumes. |
| D9 | Releases are immutable prefixes, `apps/{appId}/releases/{release}/`, and the registry names the live one | A mutable "current" prefix | Release and rollback are registry changes, live within one poll interval. |
| D10 | Wildcard DNS and a wildcard certificate on one listener, with no listener rules | Per-app rules, records or certificates | Onboarding never touches ingress. |
| D11 | Identity-provider differences sit behind `IIdpProfile`, chosen per issuer in platform configuration | Cognito specifics in the options setup; a profile chosen per app | Cognito's logout takes `client_id` and `logout_uri`, not the RP-initiated parameters. An entry cannot pick the wrong profile. PingFederate becomes a profile, not a redesign. |
| D12 | The deployment unit is a cell, and every module takes a cell name from day one | Assuming one deployment forever | Phase 2 isolates risk classes by instantiating the same stack again. |

### Isolation model

Each SPA gets a logical boundary: its host, OAuth client, scheme names, encryption purposes, session records, S3 prefix, API list and content security policy. The substitution checks in both runs exercise the identity parts of that boundary.

The process is shared, and so are its memory, task role, data-protection key ring and registry. Code execution inside the gateway reaches every session and every client secret in that deployment. Logical isolation does not change that.

Cells are the answer. A cell is the whole stack: ALB, service, bucket, registry, secrets prefix and session store. Phase 1 runs one cell. Phase 2 assigns apps to cells by data classification, and "no per-SPA runtime resources" still holds inside each cell.

### Invariants held outside application code

The properties that matter most do not depend on gateway code being correct. Each is enforced by something the gateway cannot change, and checked by a test that runs outside it.

| Invariant | Enforced by | Checked by |
| --- | --- | --- |
| Only the ALB reaches the gateway | The task security group | P-06 |
| The gateway reads only its own cell's registry, releases and secrets, and cannot write the registry | The task role | P-03 |
| A published release cannot be altered | A bucket policy that requires conditional writes and denies deletes | P-04 |
| Onboarding creates no runtime resource | A separate Terraform root with a resource-type allowlist | P-02 |
| One app's cookie is never sent to another host, and a sibling cannot plant one | The `__Host-` prefix, enforced by the browser | N-25, in a real browser |
| A code issued to one client cannot be redeemed by another | The identity provider | N-04, against Cognito |
| An API accepts only tokens meant for it | The API's own validation | F-08, N-17 |
| The mock API shares no code with the gateway | A project reference rule | A-02 |
| No workstream changes a shared file unnoticed | Code owners, and a path-ownership check on every change | A-05 |

## Trust boundaries and threats

The gateway trusts the registry and the identity provider, and nothing that arrives from a browser. Test numbers refer to the Tests tab.

### Trust boundaries

| Boundary | Trusted for | Enforced by |
| --- | --- | --- |
| Browser to ALB | Nothing. Page script may be hostile: an XSS, or a malicious sibling SPA. | TLS, and every rule below |
| ALB to gateway task | The ALB, for the request scheme and client address only | Task security group admits the ALB only. Forwarded headers are read from ALB subnets only. |
| Gateway to identity provider | The provider, for identity and tokens, after signature, issuer, audience and nonce checks | TLS, an issuer fixed in platform configuration, one client secret per app |
| Gateway to upstream APIs | Upstreams, to validate the token themselves. They are not trusted to set cookies. | Registry allowlist bounded by platform policy. `Set-Cookie` is stripped. |
| Gateway to S3 and Parameter Store | AWS, under a task role scoped to this cell's prefixes | IAM |
| Registry writers to the registry | Platform administrators, for routing, OAuth and policy configuration | IAM on one object, S3 versioning, validation, last-known-good. Phase 2 adds review and audit. |
| SPA publishers to a release prefix | One team, for the content of its own app | Conditional writes make a release immutable. Phase 1 has a single publisher, the deployer. IAM per prefix comes in Phase 2. |

### Threats

| # | Threat | Mitigation | Tests |
| --- | --- | --- | --- |
| T1 | Script in an SPA steals tokens | Tokens never reach the browser. `connect-src 'self'` limits where a page can send data. | F-05, N-23 |
| T2 | A sibling SPA attacks another: CSRF, cookie tossing, framing, embedding | Fetch Metadata `same-origin`, antiforgery tokens, `__Host-` on session, antiforgery and login cookies, `frame-ancestors 'none'`, `Cross-Origin-Resource-Policy: same-origin`, no CORS | N-06, N-07, N-24, N-25 |
| T3 | A session, cookie, state or code from one client is used at another | Per-scheme protectors, a store bound to app and client, host-scoped schemes, PKCE and client-bound codes at the provider | N-01 to N-05 |
| T4 | Host header or routing confusion | Exact authority match. Unknown hosts get 404. `X-Forwarded-Host` is unread. | N-08 |
| T5 | The proxy is steered to an unapproved destination | Routes come from the registry only, inside a platform policy on upstream names. Redirects are not followed. | N-10 |
| T6 | Forwarded headers are spoofed | Known networks, a forward limit of one, and the task security group | N-09, P-06 |
| T7 | The registry is tampered with, or a bad entry is published | IAM, validation, atomic swap, last-known-good, versioning | N-14 |
| T8 | Secrets or tokens leak into logs and errors | Secrets by reference, redaction, no query strings logged, framework authentication loggers held at Information, and a canary test | N-16 |
| T9 | Open redirect through `returnUrl` | Only local paths are accepted | N-15 |
| T10 | A compromised upstream plants a cookie or content | `Set-Cookie` and `Access-Control-*` stripped from proxied responses, security headers overwritten | N-12 |
| T11 | A token is replayed from one upstream to another | Resource binding sets `aud`. Each API checks audience, client and scope. One audience per app in Phase 1. | N-17, F-08 |
| T12 | Session fixation | Every sign-in creates a new session key, because the session cookie is not read on the callback path. `__Host-` stops a sibling planting a cookie. | N-18 |
| T13 | The authorization code appears in URLs and ALB access logs | Accepted in Phase 1. Codes are single-use, client-bound and protected by PKCE. Cognito documents no `form_post` mode. PingFederate supports `form_post`, which Phase 2 can use. | None |
| T14 | The shared gateway process is compromised | Least-privilege IAM only in Phase 1. Cells in Phase 2. | None |
| T15 | Operators reach a shell in the gateway through ECS Exec | Proof of concept only, because the tunnel needs it. Phase 2 removes it and makes the root filesystem read-only. | None |
| T16 | Denial of service: login storms, a registry or S3 outage | The registry keeps serving the last good snapshot. S3 is a hard dependency for assets in Phase 1. Phase 2 adds caching and limits. | F-09 |
| T17 | Login CSRF: a sibling plants login-state cookies and replays its own callback | Correlation and nonce cookies carry the `__Host-` prefix, so only the app's own host can set them. The gateway also refuses case variants of its cookie names. | N-25, N-26 |
| T18 | Forced logout through a framework default | The remote sign-out path is cleared. Logout is a same-origin POST checked by the request gate. | N-27 |

## Execution

Six workstreams run in parallel once the shared skeleton exists. Identity-shaped work waits for G0. Nothing else does.

&#91;embedded content: roadmap · 7 lanes, 7 gates\]

Work outside identity starts at G1a and never waits for G0. After G2 the path runs through deployment, the full test run and the third-SPA onboarding.

### Gates

| Gate | Proves | Exit test | Unblocks |
| --- | --- | --- | --- |
| G1a Skeleton frozen | Every shared file exists and compiles: the non-identity contracts, abstractions, test doubles, and the composition root with stub entry points | The solution builds. The gateway starts on stubs and answers 404 to an unknown host. | Every task that does not touch identity |
| G0 Mechanism confirmed | The shipped handler, Cognito, token management and YARP behave as the two runs predict | The five items under the feasibility gate | The identity contracts |
| G1b Identity contracts frozen | G0's findings are folded into the scheme names, the session record, the token source and the profile | The freeze is tagged | Workstream 3 and token attachment in the proxy |
| G2 Local vertical slice | Both SPAs work end to end on a workstation: login, shell, API call, refresh, logout | F-01 to F-06 and F-08 to F-11, hermetic. A-01 to A-05. | The first gateway deployment |
| G3 Deployed slice | The same through the ALB, over the tunnel, against Cognito | F-01 to F-06 and F-08, deployed. P-01 to P-06. | The deployed negative run |
| G4 Negative suite green | Every substitution, CSRF, routing and leakage attempt is rejected | N-01 to N-25 and N-27, hermetic. The deployed subset, which adds N-26. | The onboarding proof |
| G5 Phase 1 accepted | A third SPA is onboarded with no restart and no runtime infrastructure change | F-07 in both tiers, and all seven acceptance criteria signed off | Phase 2 |

Phase 2 adds G6 for multi-task continuity, G7 for PingFederate and G8 for enterprise readiness. The Phase 2 tab defines them.

### Critical path

Cognito pool and app clients, then G0, then G1b, then the identity core, then G2, G3, G4 and G5. Everything else has slack.

Work that is the same under every outcome of G0 starts as soon as G1a is tagged. That covers the registry validator, asset serving, the request gate, security headers, the SPAs and the test harness. The state bootstrap, the certificate script and the test identity provider depend on nothing and start on day one.

### Workstreams

| Workstream | Owner | Owns | Builds against | First needed at |
| --- | --- | --- | --- | --- |
| 1 AWS and IaC | Infra agent | `infra/`, `tools/scripts/` | A public sample container image | G0 for Cognito, G3 for the rest |
| 2 Hosting and registry | Hosting agent | `src/Platform.Registry`, `src/Platform.Hosting`, `tools/spactl` | An in-memory object store | G2 |
| 3 OAuth and sessions | Identity agent | `src/Platform.Identity`, `spikes/g0` | The test identity provider, in-memory registry, fake secrets | G0, then G2 |
| 4 API and security | Proxy agent | `src/Platform.Proxy`, `src/Platform.Security` | A fake token source and an echo upstream | G2, then G4 |
| 5 Fixtures | Fixtures agent | `fixtures/` | Stub session responses | G0 for the app definitions, G2 for the rest |
| 6 Verification and operations | Verification agent | The gateway test projects, `tests/e2e`, `src/Platform.Telemetry`, `docs/runbooks`, `docs/gaps.md` | Every fake | G2 onward |
| Integrator | You, or a lead agent | `contracts/`, `src/Platform.Abstractions`, `src/Platform.Testing`, `src/Gateway`, `docs/adr`, CI workflows, package versions, the solution file | Nothing | G1a and every gate after |

The brief places deployment and teardown under workstream 6. Here the scripts live beside the Terraform they drive, so workstream 1 writes them and workstream 6 runs them at each gate.

### Rules that keep agents apart

- **One owner per path.** An agent writes only inside its own paths. CI rejects a change that touches another owner's path.
- **The integrator's paths are off limits:** the contracts, the abstractions, the test doubles, the composition root and the central package versions. A change is a written request, never an edit.
- **Entry points are fixed.** The pipeline contract names each `AddPlatform…`, `UsePlatform…` and `MapPlatform…` call and its owner. `Program.cs` calls them in the frozen order and is written once, at G1a.
- **Every interface ships with a test double at G1a**, in `Platform.Testing`. No workstream waits for another's implementation.
- **A test id in a task names a behaviour.** The owning workstream proves it in its own test project. Workstream 6 proves it again in the composed suites at the gates.
- **Registry validation lives in one library.** Workstream 2 implements every rule in the contract. The gateway and `spactl` both call it, so they cannot disagree.

## Phase 1 acceptance

Each criterion has an automated test and a recorded piece of evidence. Hermetic versions run on every change; deployed versions run through the tunnel against Cognito.

| # | Criterion | Proven by | Evidence kept |
| --- | --- | --- | --- |
| 1 | Both SPAs served through the same ALB and gateway | F-01 | Both hostnames reach one ALB through the tunnel and are answered by one task. The plan allowlist shows one ALB and one service. |
| 2 | Distinct OAuth clients; login, callback, refresh, logout | F-02, F-03, F-04, F-10 | Access tokens last five minutes in the proof of concept, so refresh happens during the run. The mock API reports a different `client_id` per app. |
| 3 | Browser receives the session cookie only; no OAuth tokens | F-05, N-23 | A dump of cookies, web storage and every response body from the browser run, scanned for token-shaped strings. |
| 4 | CSRF, cross-client substitution and unauthorized upstream access rejected | N-01 to N-07, N-10, N-17, N-25 to N-27 | One assertion per attempt. Each rejection the gateway makes is logged under a rule id. |
| 5 | Per-app CSP, headers, SPA fallback, release routing | F-06 | Header snapshots per app, the fallback matrix, and a release switch and rollback with no deployment. |
| 6 | Third SPA onboarded without new runtime infrastructure or a gateway restart | F-07 | Task id, start time and task definition revision before and after. The onboarding plan contains one Cognito client, its login branding style and one parameter. The platform plan is empty. |
| 7 | Backend independently verifies client and scopes; audience where bound | F-08, N-17 | The mock API shares no code with the gateway. It runs two listeners. One checks `aud` for app 1, which uses resource binding. The other serves app 2 without it. |

Criterion 3 needs one clarification. Two cookies persist in the browser: the session cookie and the antiforgery cookie. Neither holds token material. Correlation and nonce cookies exist only while a login is in flight.

Phase 1 claims nothing about availability or durability. It runs one replica with in-memory sessions, so every deployment and restart signs users out. The task has a public address with restricted inbound access, and ECS Exec is on for the tunnel.

## Assumptions and defaults taken

The brief left these open. Each default is the reading the plan uses. Changing one before G1a is cheap. After it, the change is a contract change.

### Assumptions

| # | Assumption | If it is wrong |
| --- | --- | --- |
| A1 | SPA hosts are single-label names under one internal zone per cell, so one wildcard certificate and one wildcard DNS record cover them. | Onboarding starts touching certificates or DNS. |
| A2 | Browsers are current and managed. They send Fetch Metadata headers and honour the `__Host-` cookie prefix. | The request gate needs an `Origin`-only fallback, and a sibling SPA may be able to plant cookies. |
| A3 | SPAs call only their own origin under `/api`, send `X-CSRF-TOKEN` on unsafe requests, and treat 401 as "navigate to `/bff/login`". | An SPA that calls APIs cross-origin does not fit a backend-for-frontend. |
| A4 | There is one identity provider issuer per environment, one confidential client per SPA, and one token audience per app in Phase 1. | Several audiences per app needs token exchange, which the brief defers. |
| A5 | Upstream APIs accept a bearer token and validate it themselves. | The gateway becomes the only check, which criterion 7 rules out. |
| A6 | Coding agents run where nuget.org is reachable. The workstation reaches it. The two sandboxes used for the spike and the skeleton did not. | G0 cannot run. |
| A7 | Three kinds of run touch the AWS account: every Terraform apply, G0, and the deployed test runs. Whoever does them has AWS credentials, the Session Manager plugin, a local certificate authority and a scriptable browser. CI has a read-only role for plans. Workstreams 2, 4 and 5 need no AWS access. | The deployed tier needs a person at a workstation. |

### Defaults taken

| Choice | Default | Alternative |
| --- | --- | --- |
| Proof-of-concept hostnames | `app1.spa-poc.test:8443` and siblings, with the port in the registry `host` and in the Cognito callback URLs | Bind local port 443 with elevated rights and drop the port |
| Shell access | A session is required before any asset is served | A public shell per app, deferred |
| Token management | Duende.AccessTokenManagement 4.x, Apache-2.0 | One in-house class over Duende.IdentityModel |
| Test identity provider | Hand-written and test-only, with signing done by Microsoft.IdentityModel | OpenIddict in process, if a hand-written double is not acceptable |
| Compute | Fargate on ARM64, 0.25 vCPU and 1 GB, with the mock API as a second container in the same task | x86 at about 25% more |
| Region | us-east-1; every price in the cost model is for that region | Any region with Cognito managed login |
| Terraform state | S3 backend with native lock file, Terraform 1.11 or later, in a bootstrap bucket outside the platform | Local state, which blocks agents working from separate checkouts |
| CI | GitHub Actions | Any runner that can build .NET 10 and run Terraform |

## Sources

Pages and source files opened for this plan, on 10 and 11 October 2026.

**ASP.NET Core and .NET**

- ASP.NET Core `release/10.0` source: [AuthenticationMiddleware](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/Core/src/AuthenticationMiddleware.cs), [AuthenticationSchemeProvider](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Http/Authentication.Core/src/AuthenticationSchemeProvider.cs), [AuthenticationHandler](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/Core/src/AuthenticationHandler.cs), [RemoteAuthenticationHandler](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/Core/src/RemoteAuthenticationHandler.cs), [RemoteAuthenticationOptions](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/Core/src/RemoteAuthenticationOptions.cs), [AuthenticationBuilder](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/Core/src/AuthenticationBuilder.cs), [OpenIdConnectHandler](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs), [OpenIdConnectOptions](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectOptions.cs), [OpenIdConnectPostConfigureOptions](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectPostConfigureOptions.cs), [CookieAuthenticationHandler](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/Cookies/src/CookieAuthenticationHandler.cs), [PostConfigureCookieAuthenticationOptions](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Security/Authentication/Cookies/src/PostConfigureCookieAuthenticationOptions.cs), [RequestCookieCollection](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Http/Http/src/Internal/RequestCookieCollection.cs), [ForwardedHeadersOptions](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Middleware/HttpOverrides/src/ForwardedHeadersOptions.cs), [DefaultAntiforgery](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Antiforgery/src/Internal/DefaultAntiforgery.cs), [AntiforgeryOptions](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Antiforgery/src/AntiforgeryOptions.cs)
- .NET runtime `release/10.0` source: [OptionsMonitor](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/Microsoft.Extensions.Options/src/OptionsMonitor.cs), [OptionsCache](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/Microsoft.Extensions.Options/src/OptionsCache.cs)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): .NET 10 is supported until 14 November 2028
- [Microsoft.IdentityModel](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet) at tag 8.19.2, built from source for the real-handler run

**Libraries compared**

- Duende BFF v4: [multi-frontend architecture](https://docs.duendesoftware.com/bff/architecture/multi-frontend/), [release announcement](https://duendesoftware.com/blog/20251202-duende-bffv4-now-available-multi-frontend-opentelemetry-and-simplified-security), and the scheme provider in the [products repository](https://github.com/DuendeSoftware/products)
- Duende.AccessTokenManagement: [documentation](https://docs.duendesoftware.com/accesstokenmanagement/), [licence statement](https://duendesoftware.com/products/opensource), and source in the [foss repository](https://github.com/DuendeSoftware/foss)
- [Finbuckle.MultiTenant](https://github.com/Finbuckle/Finbuckle.MultiTenant) changelog and licence
- [YARP](https://github.com/dotnet/yarp) tags and target frameworks

**Amazon Cognito**

- [Scopes and resource servers](https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-define-resource-servers.html), including resource binding
- [Authorize endpoint](https://docs.aws.amazon.com/cognito/latest/developerguide/authorization-endpoint.html)
- [Logout endpoint](https://docs.aws.amazon.com/cognito/latest/developerguide/logout-endpoint.html)
- [Refresh tokens and rotation](https://docs.aws.eu/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-refresh-token.html)
- [Managed login branding](https://docs.aws.amazon.com/cognito/latest/developerguide/managed-login-branding.html) and the [CreateManagedLoginBranding](https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_CreateManagedLoginBranding.html) API
- [Client secret rotation announcement](https://aws.amazon.com/about-aws/whats-new/2026/02/amazon-cognito-client-secret-lifecycle), 26 February 2026
- [Pricing](https://aws.amazon.com/cognito/pricing/)

**AWS infrastructure**

- [Load balancer attributes](https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_LoadBalancerAttribute.html)
- [Elastic Load Balancing pricing](https://aws.amazon.com/cn//elasticloadbalancing/pricing)
- [Public IPv4 address charge](https://aws.amazon.com/blogs/aws/new-aws-public-ipv4-address-charge-public-ip-insights)
- [Fargate task pricing, a third-party summary of list prices](https://www.factualminds.com/blog/amazon-fargate-ecs-task-pricing-2026/)
- [Enforcing conditional writes on S3 buckets](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes-enforce.html)
- [ECS Exec requirements](https://www.repost.aws/knowledge-center/fargate-ecs-exec-errors)
- [Port forwarding through a Fargate task](https://dev.to/aws-builders/access-your-aws-database-using-local-port-forwarding-on-your-ecsfargate-container-4nk4)
