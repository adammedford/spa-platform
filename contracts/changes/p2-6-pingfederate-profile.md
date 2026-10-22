# Change request P2-6: a `pingfederate` profile, `PrivateKeyJwt`, and the row's six decisions

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 3 (OAuth and
  sessions) and workstream 5 (Fixtures), for the Phase 2 spec issue (to be filed)
- **Tasks:** P2-6; 3.7 (the profiles), 5.4 (the test identity provider), 4.3 (the request gate),
  6.2 and 6.3 (the composed suites), 6.9 (the deployed runs)
- **Date:** 2026-10-19
- **Status:** decided, 2026-10-20, items 1 to 6; item 7, 2026-10-21, on https://github.com/adammedford/spa-platform/issues/224; item 8 open until the harness branch that selects the profile exists. See the Decision section below.

## The gap

Row P2-6 of `docs/plan/06-phase-2-and-risks.md` asks for "a profile for PingFederate, with
decisions on `form_post`, pushed authorization, `private_key_jwt`, audience, logout and
back-channel logout", accepted when "the functional and negative suites pass against PingFederate".
ADR-0011 put the provider's part behind `IIdpProfile` so that "PingFederate becomes a profile, not
a redesign", and most of the package is workstream 3's and 5's own code. Three things are not:

- **The profile cannot be selected.** `Issuers:{name}:Profile` admits `standard` and `cognito`
  only (`contracts/platform-config.schema.json`, the `Profile` enum, frozen at G1b by the
  `contracts-v1` tag), and `tests/Contracts.Tests/PlatformConfigurationTests.cs` ->
  `Profile_is_an_IIdpProfile_name` asserts that `pingfederate` is refused.
- **The credential style has no `private_key_jwt`.**
  `src/Platform.Abstractions/ClientCredentialStyle.cs` has `PostBody` and `AuthorizationHeader`,
  frozen at G1b, and ADR-0017 decision 2 makes the profile's one style govern the code exchange,
  the refresh and the revocation alike.
- **The callback path takes `GET` only.** `contracts/request-gate.md` -> Rules by path gives
  `/signin-oidc` and `/signout-callback-oidc` the method `GET`, and
  `src/Platform.Security/RequestGate.cs` answers a `POST` there 405 under `gate.method`. A
  `form_post` callback is a cross-site `POST`.

Nothing here was built: `dotnet` was not available where this was drafted, so each library
capability below names the source it was read from, the handler at `dotnet/aspnetcore`
`release/10.0` and the token library at tag `atm-4.2.0` of `DuendeSoftware/foss`, and what could
not be read is marked for the profile ticket's first commit. The vendor documentation sites were
not reachable from the drafting sandbox.

## The change

1. **`Issuers:{name}:Profile` admits `pingfederate`.** In `contracts/platform-config.schema.json`
   the issuer object's `Profile` enum becomes `["standard", "cognito", "pingfederate"]`, and
   `Profile_is_an_IIdpProfile_name` in `tests/Contracts.Tests/PlatformConfigurationTests.cs`
   moves that name from its refused case to an accepted one.

   This lands between gates, on the integrator's decision, by the rule `contracts/README.md` ->
   "What may land between gates" states: an added enum value is a widening, so every platform
   configuration that validates today still validates, nothing compiled against the frozen schema
   changes, and no stage, owner, signature, rule id or field name moves. It is the added-type case
   of change request 4.2, not the narrowed-schema case. One thing follows for the landing:
   `tests/Platform.Identity.Tests/IdpProfileTests.cs` ->
   `AddPlatformIdentity_registers_one_profile_for_each_name_platform_configuration_permits` reads
   the enum from the schema and requires exactly one registered profile per name, so the enum edit
   and workstream 3's registration of the profile must reach `main` together. Workstream 3's branch
   merges the integrator's decision commit before it lands, which `OWNERSHIP.md` allows: a history
   containing a merge keeps each ordinary commit within one owner's set.

2. **`ClientCredentialStyle` gains `PrivateKeyJwt`.** In
   `src/Platform.Abstractions/ClientCredentialStyle.cs`: `PrivateKeyJwt = 2`, documented as "a
   signed JWT in `client_assertion`, with `client_assertion_type`
   `urn:ietf:params:oauth:client-assertion-type:jwt-bearer` (RFC 7523 section 2.2), in place of a
   secret, on the code exchange, the refresh and the revocation alike". The header's "Map by name,
   never by cast" paragraph gains one sentence: the library's enum has no third member, and
   `PrivateKeyJwt` maps to the library's `PostBody` with a `ClientAssertion` on the request, which
   is what the library does for itself (`Internal/OpenIdConnectUserTokenEndpoint.cs` at
   `atm-4.2.0`: `request.ClientAssertion = assertion; request.ClientCredentialStyle =
   ClientCredentialStyle.PostBody;`).

   Between gates for the same reason as item 1: a new member on a frozen enum changes no
   signature. What switches on it today fails closed until workstream 3 maps it:
   `ProfileTokenEndpoints.For` and `LibraryStyle` in `src/Platform.Identity` throw
   `ArgumentOutOfRangeException` for a member they do not name, and `StubIdpProfile` keeps
   `PostBody` as its default. `PublicSurfaceTests` lists types, not members, so no freeze test
   moves. No `IIdpProfile` member changes: `Configure(OpenIdConnectOptions, ...)` sets the response
   mode, the pushed-authorization behaviour, the login-state cookies and the events, and the style
   tells `Platform.Identity`'s mapping which assertion to build. An interface member for the
   assertion was considered and rejected: it is a changed signature, which waits for a gate, and
   the mapping needs nothing from the profile but the style.

   **Where the key lives.** One gateway key per cell per issuer replaces every per-app secret, so
   it is platform configuration, not a registry value. Proposed: an optional object
   `Issuers:{name}:ClientAssertion` beside `Authority`, `Profile` and `BrowserOrigin`, in
   `platform-config.schema.json#/properties/Issuers/additionalProperties/properties`, closed like
   its parent:

   | Property | Required | Form | Meaning |
   | --- | --- | --- | --- |
   | `KeyRef` | yes | the `clientSecretRef` pattern, `ssm:{name}:{version}` | A Parameter Store parameter holding the private key as PKCS#8 PEM, one per cell per issuer (ADR-0003, ADR-0012). Never the key itself. |
   | `Algorithm` | no, default `RS256` | enum `RS256`, `PS256`, `ES256` | The JWS algorithm of the assertion |
   | `KeyId` | no | string | The `kid` the assertion carries; absent, the RFC 7638 thumbprint of the public key, which Microsoft.IdentityModel computes |

   The issuer object is closed and frozen at G1a; an optional property is a widening, as above.
   `IssuerDefinition` in `src/Platform.Abstractions/IIdpProfile.cs` gains no member: workstream 3
   reads the object from `IConfiguration`, as `src/Platform.Security/SecurityHeaders.cs` reads
   `Issuers:{name}:BrowserOrigin` for `form-action`. The parameter is read through
   `IClientSecretSource`: preloaded once at start-up by workstream 3, because options setup does no
   I/O (R-D10), and a pingfederate issuer whose key cannot be read fails the start rather than the
   first login. The public half is registered on each app's client at the provider; the gateway
   serves no JWKS endpoint in P2-6, since a public path costs the same reserved-path and gate work
   as item 7 and is deferred with it.

   **`clientSecretRef` under `private_key_jwt`.** `registry.schema.json#/$defs/oauth` requires it.
   Loosening `required` is a widening, but five things assume the value: `OAuthDefinition` (frozen
   at G1b), the validator and the secret preload of tasks 2.1 and 2.2, `spactl app add` (2.6), the
   apps root (1.8: one parameter per app, counted by P-02) and `OidcOptionsSetup`, which calls
   `secrets.Get(app.OAuth.ClientSecretRef)` for every app. Asked: defer. In P2-6 an app on a
   pingfederate issuer still carries a reference that resolves, and the profile never sends the
   value. The category removal `docs/plan/06-phase-2-and-risks.md` promises ("`private_key_jwt`
   with one gateway key replaces every per-app client secret") completes in a later request, once
   those five agree; it is recorded here so it is not read as forgotten.

3. **`form_post`: `/signin-oidc` accepts `POST`.** `contracts/request-gate.md` -> Rules by path
   splits the callback row: `/signin-oidc` takes `GET` and `POST`; `/signout-callback-oidc` keeps
   `GET`; `Sec-Fetch-Site` stays "Any" and the last cell stays "Nothing here. The framework checks
   state, correlation and nonce." Workstream 4 implements it in
   `src/Platform.Security/RequestGate.cs` (the `Row.Callback` arm answers `GET` only today) and
   flips the `POST /signin-oidc` case in `tests/Platform.Security.Tests/RequestGateTests.cs`.
   Prose: the cookie table in `contracts/http-contract.md` says `SameSite=Lax` for
   `__Host-spa-corr.*` and `__Host-spa-nonce.*`; under a profile that uses `form_post` they are
   `SameSite=None`, still `Secure`, `HttpOnly`, `Path=/`, `__Host-` and minutes long. ADR-0007's
   consequence line and the plan's "Defaults the design overrides" row read as if `form_post` were
   always off; an ADR amendment records the date that stopped being true, as change request 4.2
   did for ADR-0005.

   Recommendation: on for `pingfederate`, off for `standard` and `cognito`. The profile's
   `Configure` sets `options.ResponseMode = form_post` and `SameSite=None` on the two login-state
   cookies; it runs after `OidcOptionsSetup` sets `query` and `Lax`, so no shared code changes.
   What it buys is threat T13: the code leaves the URL and the load balancer's access logs, which
   P2-7 switches on. What it costs is bounded: `Lax` already sends those cookies on every
   top-level `GET` navigation, the callback included, so `None` adds the `POST` navigation and
   nothing else, and `__Host-` is still what stops a sibling planting them (ADR-0007, N-25, N-26).
   The handler reads a form-encoded `POST` callback itself (`OpenIdConnectHandler.cs`, the
   callback read in `HandleRemoteAuthenticateAsync`: a `POST` with
   `application/x-www-form-urlencoded` goes through `ReadFormAsync`), and
   `CallbackBlindCookieManager` compares the path alone, so the session cookie stays unread on it.

   Why the gate takes `POST` for every profile rather than the pingfederate one: the gate cannot
   see the issuer's profile (A-03 keeps `IssuerDirectory` out of workstream 4), and it does not
   need to. A `POST` callback with no valid state and correlation cookie is refused by the handler
   exactly as a `GET` is: 400, the constant failure page, `auth.callback`. The gate's `Origin` and
   fetch-site rules do not apply to the callback row today and still do not. The content security
   policy is unchanged: the auto-submitting form is the provider's page, under the provider's
   policy, and the gateway's `form-action 'self' <provider origin>` governs the logout form as
   before.

4. **Pushed authorization: no contract change.** Confirmed from the handler's source.
   `OpenIdConnectOptions.PushedAuthorizationBehavior` defaults to `UseIfAvailable`; `Require`
   fails when discovery lists no `pushed_authorization_request_endpoint`, and `Disable` fails when
   discovery says `require_pushed_authorization_requests` (`OpenIdConnectHandler.cs`, the
   challenge's switch on that behaviour). The push posts the authorize parameters to that endpoint
   over the shared backchannel, adds `client_secret` to the body unless `OnPushAuthorization`
   calls `HandleClientAuthentication()`, and sends the browser to the authorize endpoint with
   `client_id` and `request_uri` only (`PushAuthorizationRequest`). Recommendation: the
   pingfederate profile sets `Require`, so a discovery document that drops the endpoint fails
   closed rather than falling back to front-channel parameters, and its `OnPushAuthorization` adds
   the client assertion and marks client authentication handled. `standard` and `cognito` leave
   the handler's default, which pushes nothing today because neither Cognito nor the test identity
   provider advertises an endpoint (R-A1 holds). All of it is `Configure`'s, workstream 3's own.
   One telemetry note, not a change: the pushed body carries the login's `state`, `nonce` and
   `code_challenge`; the handler logs backchannel detail at Debug and Trace, which ADR-0007 already
   holds at Information, and N-16's sweep covers the run.

5. **Audience: no contract change; the decision is the provider's token format.** `oauth.resource`
   (`registry.schema.json#/$defs/resource`, one https URL) reaches the authorize request as
   `options.Resource` (`OidcOptionsSetup`, R-A5), survives the code exchange (`CodeExchange` keeps
   every parameter the handler supplied) and is left off the refresh (`AccessTokenSource`, T-B12,
   C-2.9). Cognito maps it to `aud` through a resource server. PingFederate issues either
   reference tokens, which are opaque, or JWTs, from an access token manager configured at the
   provider; which, and what sets `aud`, is provider configuration, not a gateway parameter.
   Recommendation: the PingFederate deployment issues JWT access tokens whose `aud` follows
   `resource` (RFC 8707) where the provider supports it, and the access token manager's audience
   otherwise; the registry entry and the profile send what they send today. Opaque tokens would
   move validation to introspection in the API, which criterion 7 makes the API owner's and P2-6
   does not take on. Two questions are the deployed run's: whether `aud` survives a refresh that
   names no `resource`, as C-2.9 found for Cognito, and whether the token endpoint accepts
   `resource` at all. Both go into the profile's comments, with the provider document, when the run
   answers them.

6. **Logout: nothing new, with one caveat.** PingFederate advertises `end_session_endpoint`, and
   the pingfederate profile keeps the handler's RP-initiated request as the standard profile does
   (`src/Platform.Identity/IdpProfiles.cs`: `id_token_hint`, `post_logout_redirect_uri`, the
   handler's `state`, back through `/signout-callback-oidc` to `/`, R-C6). Revocation finds its
   endpoint in discovery (ADR-0017, C-2.3) and presents the assertion where the style says so;
   `SessionEnd` builds the request from the library's parts and gains the assertion there. The
   caveat is ADR-0017 decision 4's own: the standard profile's RP-initiated logout has never run
   against a real provider, and T-B13 (a refresh answer without an ID token blanks the hint) is
   named for the PingFederate gate. The deployed run is where both are first exercised.

7. **Back-channel logout: decided now, built later.** Asked: record the decision and defer the
   build to a ticket cut after this request is decided, blocked by P2-1 and P2-3, with these
   shapes, so the durable store and the lease abstraction leave room for it.

   - **The path is provider-facing, not `/bff`:** `/signout-backchannel-oidc`, beside
     `/signin-oidc`, `/signout-callback-oidc` and `/signout-oidc`. `/bff` is held to same-origin
     browser requests by the request gate (`contracts/request-gate.md`, ADR-0007, the glossary's
     "Request gate"), and a provider's server-to-server `POST` carries no fetch metadata and no
     cookie; under `/bff` that universal would gain an exception, and the endpoint would sit in the
     table `contracts/http-contract.md` keeps for SPA authors. The cost of the other path is one
     entry in each reserved-path list: `contracts/http-contract.md` (the paths an SPA cannot
     serve), `src/Platform.Hosting/Assets.cs` (workstream 2), `src/Platform.Identity/ShellGate.cs`
     (workstream 3), `src/Platform.Security/RequestGate.cs` (workstream 4) and
     `src/Platform.Telemetry/RequestTelemetry.cs`, where it is a `callback` route (workstream 6);
     the `route` values of `contracts/telemetry.md` are unchanged.
   - **The gate row:** `/signout-backchannel-oidc` | `POST` | Any | Nothing here: the endpoint
     validates the logout token (OpenID Connect Back-Channel Logout 1.0, section 2.6): signature
     against the issuer's keys, `iss`, `aud` equal to the app's client id, `iat`, an unseen `jti`,
     the `events` claim and `sub`; no cookie is read.
   - **A refused logout token is a rejection and needs a rule id**, `auth.backchannel`, which is a
     new id and waits for a gate under `contracts/README.md`'s rule. That is the firmest reason the
     build waits: it cannot land between gates.
   - **The session index is a new type, not a member of `ISessionStore`**, in the shape change
     request `p2-3-session-lease-abstraction.md` proposes for the lease: `ISessionIndex` in
     `src/Platform.Abstractions`, `Task<IReadOnlyList<string>> KeysAsync(string appId, string
     subject, CancellationToken ct)`, "every live session key of one subject on one app, and never
     another app's", implemented by the store that implements `ISessionStore` and named in
     `PublicSurfaceTests`. `SessionRecord` already carries `AppId` and `Subject`, so the record
     does not change; `MemorySessionStore` scans as `RemoveAppAsync` does, and the P2-1 store keys
     a secondary index on the pair. A logout token that carries `sid` and no `sub` is refused as
     unsupported until a gate adds `sid` to the record, which is the one shape this decision leaves
     for later. The runbook of task 6.7, "ending every session for one user", uses the same index.
   - **The endpoint is workstream 3's**, mapped by `MapPlatformBff` beside the three `/bff`
     endpoints; it ends each key through `SessionEnd`, under the session's gate.

8. **CI runs the hermetic suites a second time under the pingfederate profile.** One more run of
   the functional and negative suites in `.github/workflows/ci.yml` with the harness profile set to
   `pingfederate`; the switch's name is workstream 6's to fix in `tests/Gateway.TestHarness`.
   Asked here so the shape is complete; decided when that branch exists, since the row's
   acceptance is the suites passing and a profile only one test selects is a profile nothing
   proves.

## What the test identity provider gains

Workstream 5's path, so no request; listed so the integrator sees the whole shape and the composed
suites can run the pingfederate profile hermetically before any PingFederate exists. Today
`fixtures/TestIdp/TestIdentityProvider.cs` advertises `response_modes_supported: ["query"]` and
`token_endpoint_auth_methods_supported: ["client_secret_basic", "client_secret_post"]`, serves
discovery, JWKS, `/authorize` (PKCE required), `/token` (the code and refresh grants, rotation with
a grace window), `/revoke`, `/endsession` and Cognito's `/logout`, and has no pushed authorization,
no `form_post`, no client assertion, no `sid` and no back-channel logout.

| Gain | Where | Shape |
| --- | --- | --- |
| Pushed authorization | `/par`; discovery `pushed_authorization_request_endpoint`, and `require_pushed_authorization_requests` from a switch | The authorize parameters and client authentication as at `/token`; answers a single-use `request_uri` and `expires_in`; `/authorize` resolves `request_uri`, refuses an unknown, used or expired one and a `client_id` that differs, and with the switch on refuses a request that pushed nothing (RFC 9126) |
| `form_post` | `/authorize` when `response_mode=form_post`; discovery `response_modes_supported` gains it | A `text/html` page whose form posts `code` and `state` to the redirect URI, as OAuth 2.0 Form Post Response Mode describes; `query` unchanged |
| `private_key_jwt` | `/token`, `/revoke`, `/par`; `TestIdpClient` gains a public JWK; `ClientAuth` gains `PrivateKeyJwt`; discovery lists the method | `client_assertion` validated with `JsonWebTokenHandler` against the client's key: `iss` and `sub` the client id, `aud` the issuer or the endpoint, `exp`, a `jti` not seen before. The grant record names the method, so a test asserts which was used, as `IdpProfileTests` does today |
| `sid` | Every ID token | One per authorization, so a logout token can name a session |
| Back-channel logout | `TestIdpClient` gains a logout URI; a method posts a signed logout token (`events`, `sub`, `sid`, `jti`) to it and records the answer; switches for an unpublished key and a missing `events` claim | What the deferred endpoint of item 7 is proved against; the sender needs no gateway and can land first |

Conformance tests for each sit in `fixtures/Fixtures.Tests` beside the existing grant tests, and
`TestIdpDiscoveryTests` grows with the document. The harness (`tests/Gateway.TestHarness`,
workstream 6) turns the switches on when `GatewayHarnessOptions.Profile` is `pingfederate`, as it
selects Cognito's logout style today.

## Packages

None new. The assertion is signed with `JsonWebTokenHandler` from
`Microsoft.IdentityModel.JsonWebTokens` 8.19.2, pinned in `Directory.Packages.props` for the
fixtures and already in `Platform.Identity`'s closure:
`Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12 depends on
`Microsoft.IdentityModel.Protocols.OpenIdConnect` 8.19.2, which depends on
`System.IdentityModel.Tokens.Jwt` 8.19.2, which depends on it (the four package files on nuget.org,
read 2026-10-19). A direct `<PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" />`
in `src/Platform.Identity/Platform.Identity.csproj` is what `Directory.Packages.props` permits for
a pinned package, and A-04 restricts only `Duende.AccessTokenManagement.OpenIdConnect`
(`tests/Gateway.ArchitectureTests/ReferenceRules.cs`). The library's `ClientAssertion` type is
`Duende.IdentityModel` 8.1.0's, pinned and transitive. Signing and key import go through
Microsoft.IdentityModel and `System.Security.Cryptography`, so AGENTS.md's rule on custom
cryptography holds. One prose cell: `docs/dependencies.md`'s row for the package says "Expected in
`fixtures/TestIdp`", and gains `Platform.Identity`.

## What workstreams 3 and 5 can do meanwhile

Workstream 5 can build everything in its table now. Workstream 3 cannot land the profile: a profile
registered under a name the enum does not admit fails `IdpProfileTests`, and a profile the enum
does not admit cannot be selected by any issuer, so nothing in the composed suites can reach it.
It can prepare `PingFederateIdpProfile` and its tests on a branch, and it can prove the three calls
with `private_key_jwt` against the stand-in in a harness under `spikes/g0/`, its own path, in the
shape of the token-and-proxy harness: the code exchange through `OnAuthorizationCodeReceived`
(`TokenEndpointRequest.ClientAssertion` and `ClientAssertionType`, with `ClientSecret` cleared;
`RedeemAuthorizationCodeAsync` posts the message's parameters as they stand, and `Validate`
requires no secret), the refresh through `UserTokenRequestParameters.Assertion` (which the
library's endpoint prefers to its `IClientAssertionService`, `atm-4.2.0`) and the revocation
through `TokenRevocationRequest.ClientAssertion`. Those findings, with their evidence ids, are what
the profile ticket's first commit carries.

## Why this is a request rather than an edit

`contracts/`, `src/Platform.Abstractions`, `docs/adr/`, `docs/dependencies.md`, `.github/` and
`tests/Contracts.Tests` are the integrator's (AGENTS.md, OWNERSHIP.md), and the `Profile` enum and
`ClientCredentialStyle` froze at G1b with the `contracts-v1` tag (`contracts/README.md` -> What G1b
froze). The request gate's rules table is a frozen contract workstream 4 implements and does not
edit.

## Blocking

The row's acceptance is a deployed proof, and nobody has a PingFederate. The functional and
negative suites against the stand-in, in pingfederate mode, are the rehearsal and the only proof
available today; the deployed run needs an instance, a client per app with the gateway's public
key and `post_logout_redirect_uri` registered, pushed authorization required, and an identity root
for it beside task 1.8's, in the shape of the deployed gate runs (#60, #61, #63) with
`Issuers:{name}:Profile` set to `pingfederate`. Until items 1 and 2 are decided, workstream 3's
profile cannot be selected and row P2-6 cannot start; workstream 5's half does not wait. The
deferred items, `clientSecretRef` and back-channel logout, hold nothing here open: each is its own
later request.

## Decision

Integrator, 2026-10-20, on Adam's answers to the eleven questions of
https://github.com/adammedford/spa-platform/issues/223 (all accepted as recommended), after the
handler and library facts were verified on https://github.com/adammedford/spa-platform/issues/217
(branch `research/handler-and-library-private-key-jwt`; claims cited by number). Items 1 to 6 are
accepted as asked, with the corrections below. Item 7 is decided on #224; item 8 waits for the
harness branch.

**Two landings.** This decision record, with the prose that only records it (the ADR amendment, the
glossary entries, the `docs/dependencies.md` cell), lands now and alone. The frozen-artifact edits
(the enum value, the contracts test, the enum member, the schema property) are the Phase 2 spec
issue's integrator ticket, under the constraint in item 1.

1. **`Issuers:{name}:Profile` admits `pingfederate`**, a widening between gates, and **the enum edit
   lands only in a merge that also registers the profile**: workstream 3's branch merges the
   integrator's commit and one landing brings both, as `OWNERSHIP.md` allows. `main` never carries
   the value alone. Not only because `IdpProfileTests` would go red: `CredentialStyles.Of` answers
   `PostBody` for a name with no registration and `OidcOptionsSetup` applies no profile, so an
   issuer naming `pingfederate` on such a `main` would run as `standard` with the form-body secret,
   silently.
2. **`ClientCredentialStyle.PrivateKeyJwt = 2`**, documented as: a signed JWT in `client_assertion`
   with `client_assertion_type` `urn:ietf:params:oauth:client-assertion-type:jwt-bearer` (RFC 7523
   section 2.2), in place of a secret, on the code exchange, the refresh, the revocation **and the
   pushed authorization request** alike; maps to the library's `PostBody` with a `ClientAssertion`
   on the request, **and to `null` for the scheme's `ClientSecret`, never an empty string**: the
   library copies that option onto its requests, so a non-null value is posted beside the assertion
   (#217 claims 1 to 3). The request's "maps to `PostBody` plus a `ClientAssertion`" was necessary,
   not sufficient. The invariant lives in **the shared mapping in `Platform.Identity`, keyed on the
   style, not in the profile**: `IssuerDefinition` carries no key and item 2 adds no member, so the
   profile cannot build an assertion, and the request's item 4 ("its `OnPushAuthorization` adds the
   client assertion") is corrected to the mapping. The mapping owns `OnAuthorizationCodeReceived`,
   `OnPushAuthorization` with `HandleClientAuthentication()`, the refresh's
   `UserTokenRequestParameters.Assertion` and `SessionEnd`'s `ClientAssertion`;
   `ProfileTokenEndpoints.LibraryStyle(PrivateKeyJwt)` answers `PostBody` and reuses that endpoint
   (an assertion on an `AuthorizationHeader` request throws); `OidcOptionsSetup` skips
   `secrets.Get` for an issuer whose profile states `PrivateKeyJwt`. The profile's `Configure` sets
   only what is the provider's. Two doc comments change with the member: the type "presents its
   **credential**", not its secret, and the calls are **four**, not three.
3. **The `ClientAssertion` object**, optional on the issuer object as proposed (`KeyRef` in the
   `clientSecretRef` form, `Algorithm` default `RS256`, `KeyId` default the RFC 7638 thumbprint),
   schema-optional with no `if`/`then` now (a candidate for MR-8's validator). The key is a property
   of this deployment of the provider, so the issuer object is its home and the registry entry is
   not. Workstream 3 reads it from `IConfiguration` as `SecurityHeaders` reads `BrowserOrigin`, and
   enforces at start: a `pingfederate` issuer with no `ClientAssertion`, or whose `KeyRef` cannot be
   read (`PreloadAsync` then `Get`), fails the start. Reusing `IClientSecretSource` for a private key
   is accepted as the no-new-abstraction route and named as such.
4. **`clientSecretRef` stays required**, deferred as written. Under `PrivateKeyJwt` the parameter
   exists and resolves; its value is never placed on the scheme's options nor sent.
5. **`form_post`.** `contracts/request-gate.md` → Rules by path splits the callback row:
   `/signin-oidc` takes `GET` and `POST` for every profile, `/signout-callback-oidc` keeps `GET`,
   `Sec-Fetch-Site` Any, the last cell unchanged. Workstream 4 implements it and flips the
   `POST /signin-oidc` case in `RequestGateTests`. The gate needs no profile: a `POST` without
   `state` fails as a `GET` does (400, the constant page, `auth.callback`), a non-form `POST` is "No
   message", a `state` with no cookie is "Correlation failed", and `CallbackBlindCookieManager` is
   path-only (#217 claim 4). The pingfederate profile's `Configure` sets `ResponseMode = form_post`
   and `SameSite=None` on the two login-state cookies; `standard` and `cognito` keep `query` and
   `Lax`. The one unexecuted part, that `SameSite=None; Secure` cookies ride the cross-site `POST`
   in a browser, is the composed suites' under the pingfederate profile, in N-25's shape.
6. **The prose that moves with it**, in the 4.2 form and with no new ADR: ADR-0007's status line,
   overrides paragraph and T13 consequence gain a dated note; `contracts/http-contract.md`'s cookie
   row reads `SameSite=Lax` (`SameSite=None` under a profile that uses `form_post`: `pingfederate`);
   ADR-0011's PingFederate bullet and ADR-0017's matching consequence gain a dated line that the
   credential-style question is answered by `PrivateKeyJwt`. ADR-0007's decision holds: `SameSite`
   was never one of the four defences. A new ADR becomes warranted only if a second profile wants
   `form_post`.
7. **Pushed authorization**: no contract change. The pingfederate profile sets `Require`; the other
   two keep the handler's default. The fail-closed path is an `InvalidOperationException` thrown
   from the challenge inside `GET /bff/login`, answered as a bare 500 with a request line whose
   `rule` is null, not a remote failure and not a rejection (#217 claim 5); no new problem code and
   no start-up check, since discovery is lazy by design (R-D10). The profile ticket's hermetic test
   asserts exactly that with the test provider's switch off. The authorize URL after a push carries
   `client_id`, `request_uri` and the handler's two telemetry parameters. One note for the day
   Cognito advertises `/par`: the handler's default pushed body carries `client_secret` in the form,
   which the cognito profile's Basic-header style says it should not send.
8. **Audience and logout**: no contract change; the recommendation that the deployment issues JWT
   access tokens with `aud` following `resource` stands, as does the T-B13 caveat. Four facts are
   the deployed run's to record on the profile ticket: whether `aud` survives a refresh naming no
   `resource`; whether the token endpoint accepts `resource`; RP-initiated logout against a real
   `end_session_endpoint`; and the provider's acceptance of the gateway's assertion (`aud`, `alg`,
   `kid`, lifetime, `jti`), since the research's stub recorded the assertion and did not validate
   it, and the test provider's `private_key_jwt` gain is the only validator before a deployed run.
9. **Packages**: none new (#217 claim 6). The `docs/dependencies.md` row for
   `Microsoft.IdentityModel.JsonWebTokens` now reads `fixtures/TestIdp`, `Platform.Identity`; the
   direct reference in `Platform.Identity.csproj` needs no request. The 10.0.12 handler enters
   `Platform.Identity`'s closure through `Platform.Abstractions`' direct reference, not through the
   token library, whose own floor is 10.0.4.
10. **Vocabulary**: `GLOSSARY.md` gains **Client credential style**, **Client assertion**, **Pushed
    authorization** and **Login-state cookies**; `form_post` is a protocol term and gets no entry.

What this decides for the spec issue: one integrator ticket (the enum value and the contracts test,
`PrivateKeyJwt`, the `ClientAssertion` property, under item 1's coupled landing); workstream 3's
profile and mapping ticket; workstream 5's test-provider ticket, which does not wait; workstream 4's
gate ticket for `POST /signin-oidc`.

### Item 7: back-channel logout

Integrator, 2026-10-21, on Adam's answers to the fourteen questions of
https://github.com/adammedford/spa-platform/issues/224. Ten are accepted as recommended. Adam
changed Q8, so the session index waits for G7, and Q6, Q11 and Q14 follow from that change. Item 7
is accepted with the shapes below and three corrections to the request. Citations of OpenID Connect
Back-Channel Logout 1.0 (Final, incorporating errata set 1, 2023-12-15) give its section and, for
section 2.6, its step.

**Three corrections to item 7 as asked.**

- **"One entry in each reserved-path list" is four lists and a gate row.** The lists are
  `contracts/http-contract.md:21-22`, `Assets.Reserved` (`src/Platform.Hosting/Assets.cs:42-45`),
  `ShellGate.Ungated` (`src/Platform.Identity/ShellGate.cs:28-31`) and `RequestTelemetry.RouteOf`
  (`src/Platform.Telemetry/RequestTelemetry.cs:129-130`). `RequestGate` keeps no list: it
  classifies named paths into a `Row`, so its change is a new row and a new arm. The gate table is
  `contracts/request-gate.md` → Rules by path, not `contracts/pipeline.md`.
- **The validation list is the spec's, which is longer** (Q3 below). The request's list lacks
  `exp`, the `alg` rule and the absence of `nonce`. Its "an unseen `jti`" runs two rules together:
  the `jti` claim is required (section 2.4), and only the check that it has not been seen before is
  optional (section 2.6 step 8).
- **`KeysAsync` cannot be built on P2-1's table.** The table never holds a session key, only its
  SHA-256 (`p2-1-dynamodb-session-store.md:201`; plan row P2-1, "The key is a hash of the session
  key"), so an index on the pair can return hashes and nothing else. `SessionEnd.EndAsync(app, key)`
  (`src/Platform.Identity/SessionEnd.cs:33`) and the session gate both take the key. The index
  removes by subject instead (Q7).

**The decisions, by question.**

- **The path (Q1).** `/signout-backchannel-oidc`, outside `/bff`, on each app's own host. Under
  `/bff` the request gate would refuse a provider's server-to-server `POST` as `gate.fetch-site`,
  because it carries no fetch metadata. Host resolution pins the app before the endpoint runs, so
  each provider client registers `https://{host}/signout-backchannel-oidc`, one per app, and the
  `aud` check against that app's client id means something. `ShellGate.Ungated` gains the entry for
  consistency only, since the shell gate gates `GET` and `HEAD` alone. `RouteOf` names it a
  `callback` route, and the `route` values of `contracts/telemetry.md` are unchanged. The path
  matches `/signout-oidc` in no segment, so the switched-off remote sign-out is untouched.
- **The gate row (Q2).** One row after the callback row (`contracts/request-gate.md:26`):
  `/signout-backchannel-oidc` | `POST` | Any | Nothing here: the endpoint validates the logout
  token; no cookie is read. Workstream 4 adds the `Row` arm, which answers `POST` only, and a `GET`
  gets 405 `gate.method` with `Allow: POST`. The cookie-name rule still runs first and costs
  nothing, because a provider sends no platform cookie. The form content type (section 2.5) is the
  endpoint's to check, not the gate's.
- **Validation (Q3).** The spec's mandatory steps: the signature (section 2.6 step 2); `alg` as for
  an ID token and never `none` (step 3); `iss`, `aud` equal to the app's client id, `iat` and `exp`
  (step 4); `sub` present (step 5, narrowed by Q9); an `events` claim holding the member
  `http://schemas.openid.net/event/backchannel-logout` (step 6); no `nonce` (step 7). Section 2.4
  makes `iat` and `jti` required claims. `typ` is absent or `logout+jwt` (explicit typing, section
  2.4). `JsonWebTokenHandler.ValidateTokenAsync` from the already-pinned
  `Microsoft.IdentityModel.JsonWebTokens` validates the token against the scheme's own
  configuration: keys and issuer from discovery through the issuer's shared metadata manager, the
  app's client id as audience, `exp` required, and the ID-token algorithms discovery lists. It
  covers neither `iat` nor `jti`: at 8.19.2 its lifetime check reads `nbf` and `exp` alone
  (`JsonWebTokenHandler.ValidateToken.cs:660-663`,
  https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/8.19.2/src/Microsoft.IdentityModel.JsonWebTokens/JsonWebTokenHandler.ValidateToken.cs#L660-L663),
  and nothing in it requires either claim. So the endpoint checks six claims itself, with nothing
  cryptographic in them: `events`, no `nonce`, `sub`, `typ`, `iat` present and no later than now
  plus the clock skew the handler allows on `exp`, and `jti` present as a non-empty string. A token
  missing `iat` or `jti`, or whose `iat` is later than that, is refused under `auth.backchannel`.
  The shipped handler has no back-channel support, so this is the bounded route the
  no-hand-written-protocol-code rule allows.
- **`jti` (Q4).** The claim is required (Q3). The check that it has not been seen before is
  optional (step 8), and this is how far it goes. Each task remembers every `jti` it accepted until
  that token's `exp`, in memory and capped in count; at the cap the oldest entry goes and the token
  is still accepted. A repeat on the same task is refused under `auth.backchannel`. Replay across
  tasks is accepted: within the token's lifetime, which providers are asked to keep within two
  minutes (section 4), it ends the same subject's sessions on the same app a second time, the safe
  direction for a logout.
- **The answers (Q5).** 200 with an empty body for any valid token, whether or not it matched a
  session (sections 2.7 and 2.8). 400 `application/problem+json` with `code: invalid_request`
  (`contracts/http-contract.md:92`) for a refused token: a failed step, a missing `iat` or `jti`,
  a missing `logout_token`, a non-form body, a `jti` already seen on this task, or a `sid`-only
  token. That is a rejection under `auth.backchannel`, and the Errors table gains one sentence, not
  a code. 400 with no rule id when
  the token was valid and the store failed, logged at Error by outcome, since no rule fired.
  `Cache-Control: no-store` on both. The logout token never reaches a log line: "tokens" is already
  on the never-logged row (`contracts/telemetry.md:16`), and N-16 covers it once the sender posts a
  canary.
- **Landing (Q6, following Q8).** One landing at G7, P2-6's gate, carries the rule id
  (`request-gate.md` → Rule ids, and `RuleIds.AuthBackchannel` in `RuleIds.All`,
  `src/Platform.Abstractions/RuleIds.cs:67`), the gate row, the reserved path and the Errors
  sentence in `http-contract.md`, the stage prose at `contracts/pipeline.md:40` (`MapPlatformBff`
  then maps more than `/bff/*`), and the session index. "The next gate" read literally on 2026-10-21
  was G3, the trunk's next open gate; G7 is the gate the row belongs to. A new rule id
  waits for a gate, the reserved path narrows what an SPA may serve, and the gate row edits a table
  frozen at G1a, so the parts wait together. Workstreams 2, 3, 4 and 6 bring their list entries in
  the same landing, in the multi-id merge form item 1 uses.
- **The session index's shape (Q7).** `ISessionIndex.RemoveSubjectAsync(string appId, string
  subject, CancellationToken ct)`, returning `IReadOnlyList<SessionRecord>`: it "removes every live
  session of one subject on one app, never another app's, and returns the records it removed". It is
  `RemoveAppAsync` one level down (`src/Platform.Abstractions/ISessionStore.cs:46-50`), and the
  DynamoDB store can do it on hashes alone. Removing without the session gate is safe against a
  refresh in flight: the holder's versioned replace finds no record and its re-read answers
  `Unauthenticated`. The endpoint revokes each returned record's refresh token through
  `SessionEnd`'s revocation, factored to take a record (section 2.7). What it gives up is revoking a
  token a concurrent refresh produced in the same instant; the provider started the logout and
  holds that grant. Task 6.7's runbook, "ending every session for one user", is the same call.
  ADR-0028 holds the options weighed.
- **The session index's contract and when it lands (Q8, changed by Adam).** The contract is recorded
  now and lands at G7, with the endpoint, not between gates before P2-1's store. It takes the
  placement ADR-0024's consequence names: a new type beside `ISessionStore` in
  `Platform.Abstractions`, frozen on landing, its name in `PublicSurfaceTests.Contracted`.
  - The double is `Platform.Testing.InMemorySessionStore`, implementing both interfaces.
  - `SessionIndexContract`, abstract, takes one factory that returns an object implementing both. It
    asserts that a subject's sessions go and another subject's stay; that another app's sessions
    under the same subject stay; that an expired session is not returned; that a subject with no
    sessions returns empty; and that the records returned are the ones removed.
  - `ISessionIndex` resolves to the `ISessionStore` singleton
    (`src/Platform.Identity/PlatformIdentityExtensions.cs:94`), and a store that does not implement
    it fails the start. `RecordingSessionStore` gains the forwarding member.
  - The contract states its limit: P2-1's index queries are eventually consistent
    (`p2-1-dynamodb-session-store.md:154`), so a session created in the same second as the logout
    may survive it.

  Why it waits: nothing needs the index before G7, and a frozen type landed now is a one-way
  contract on the identity critical path. Waiting costs little. P2-1's item already carries `appId`
  and `subject` (`p2-1-dynamodb-session-store.md:202`), and DynamoDB adds a global secondary index
  to a live table and backfills it while the table stays available
  (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/GSI.OnlineOps.html). So P2-1's
  store does not implement the index at G6, and at G7 the pair index is added with no item
  rewritten.
- **`sid` (Q9).** `SessionRecord` holds `Subject`, from the provider's `sub`, and no session id
  (`ISessionStore.cs:15-22`), and it froze at G1b. The handler keeps a provider's `sid` in the
  ticket principal, but only inside ticket bytes the store treats as opaque and P2-1 encrypts, so
  no index can read it. A logout token with `sid` and no `sub`, which section 2.4 allows, is
  refused under `auth.backchannel`. With both, every session of that `sub` on the app ends, which
  may be wider than the one provider session `sid` names; that is the safe direction for a logout.
  Each app's provider client registers `backchannel_logout_session_required` as false (section 2.2,
  the default). Whether PingFederate then sends `sub` joins decision 8's deployed-run facts, because
  a provider that sends `sid` alone leaves the feature unable to work.
- **The endpoint (Q10).** Workstream 3's, mapped by `MapPlatformBff`
  (`src/Platform.Identity/PlatformIdentityExtensions.cs:127-128`), since the pipeline contract
  allows no new entry point (`contracts/pipeline.md:11-12`). It is profile-blind and mapped for
  every app: the gate cannot see a profile (A-03), and a provider that never sends a logout token,
  Cognito among them, never reaches it. It validates (Q3, Q4, Q9), calls
  `RemoveSubjectAsync(app.AppId, sub)`, and attempts revocation for each record returned, under the
  provider deadline and never the request's token, logging by outcome and count.
- **The build and its blockers (Q11, following Q8).** The endpoint ticket is blocked by the index's
  integrator ticket, by the change that makes P2-1's store implement the index (with workstream 1's
  pair index on the table), and by workstream 5's test-provider ticket. P2-3 is not a blocker: the
  endpoint takes no refresh lease. Workstream 5's ticket does not wait. It adds `sid` to every ID
  token, a back-channel URI on `TestIdpClient` (`fixtures/TestIdp/TestIdpRecords.cs:30`, which
  holds redirect and post-logout URIs only), the signed-token sender with switches for an
  unpublished key, a missing `events`, a missing `iat` and a missing `jti`, and a canary `jti` for
  N-16. Two facts are for the deployed run: whether the provider reaches the app host on the
  internal ALB over TLS it trusts, and whether PingFederate sends `sub`. Two test ids are the
  integrator's to name at G7: a functional "a logout token ends the subject's sessions on its app"
  and a negative "each refused logout token is a 400 under `auth.backchannel`", whose cases include
  a token missing `iat`, one missing `jti` and one whose `iat` is in the future.
- **Vocabulary (Q12).** `GLOSSARY.md` gains **Back-channel logout** and **Session index** under
  Sessions. "Logout token" is a protocol term and gets no entry. The **Request gate** entry stays
  true, which is one more reason the path is outside `/bff`.
- **The ADR (Q13).** ADR-0028 records the path outside `/bff`, removal by subject through the
  session index, the index's landing at G7, the accepted cross-task replay and the `sid`-only
  refusal. No status line changes on ADR-0007, ADR-0011, ADR-0017 or ADR-0024: ADR-0024's
  consequence still holds, since the index copies its placement and only the timing is ADR-0028's.
- **This record (Q14, following Q8).** The `Status` line above and this item. It lands now, with
  ADR-0028 and the two glossary entries; nothing in it moves a frozen artifact.

What item 7 decides for the spec issue:

- **The index's integrator ticket**: the type, its `Contracted` name, the double and the contract
  suite, in the G7 integrator commit. It blocks the endpoint ticket, not P2-1's store ticket.
- **The G7 integrator commit**: the rule id, the gate row, the reserved path, the Errors sentence and
  the stage prose (Q6).
- **Workstream 3's endpoint ticket**, which also makes P2-1's store implement the index.
- **Workstream 1's pair index** on the session table, on its infra ticket, at G7.
- **Workstreams 2, 4 and 6's one-line entries**, in the same landing as the G7 commit.
- **Workstream 5's sender**, which does not wait.
- **For P2-1** (#221): the item keeps `appId` and `subject` as attributes, so the pair index needs
  no rewrite.
- **For the profile ticket's deployed run**: whether PingFederate sends `sub`, and whether it reaches
  the internal ALB.
