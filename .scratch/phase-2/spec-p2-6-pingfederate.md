**Owner:** integrator
**Plan row:** P2-6

## Problem Statement

The gateway works against one identity provider, Cognito, and a hand-written stand-in. ADR-0011 put
every provider difference behind `IIdpProfile` so that "PingFederate becomes a profile, not a
redesign", and the roadmap's stage S9 says the only proof that the seam is a seam is a second
implementation: "a seam with one implementation is a guess". Two of Phase 1's standing hazards are
provider properties that PingFederate would turn into decisions: the authorization code in the URL
and the balancer's access logs (threat T13, accepted because Cognito documents no `form_post`), and
one client secret per app (which `private_key_jwt` with one gateway key would remove).

Today the profile cannot even be selected: `Issuers:{name}:Profile` admits `standard` and `cognito`
only, `ClientCredentialStyle` has no `private_key_jwt`, and the request gate answers a `POST` on
`/signin-oidc` with 405, which is what a `form_post` callback is. The stand-in in `fixtures/TestIdp`
has no pushed authorization, no `form_post`, no client assertion, no `sid` and no back-channel
logout, so nothing hermetic could exercise the profile if it existed. Nobody has a PingFederate
instance, and the row's acceptance, "the functional and negative suites pass against PingFederate",
is a deployed proof.

## Solution

Build the profile and its stand-in hermetically at G2, as the ADR draft
`.scratch/phase-2/adr-draft-phase-2-hermetic-work-opens-at-g2.md` decides, and leave the run against
a real PingFederate for the deployed tier. The row's six decisions are made now and recorded in
`contracts/changes/p2-6-pingfederate-profile.md`:

- **`form_post`:** on for `pingfederate`, off for `standard` and `cognito`. The profile's
  `Configure` sets `ResponseMode = form_post` and `SameSite=None` on the two login-state cookies;
  the gate's `/signin-oidc` row takes `POST` for every profile, since the gate cannot see the
  issuer's profile and does not need to: a `POST` callback with no valid state and correlation
  cookie is refused by the handler exactly as a `GET` is.
- **Pushed authorization:** `PushedAuthorizationBehavior.Require` on `pingfederate`, with
  `OnPushAuthorization` supplying the client assertion; the handler's default elsewhere. No contract
  change.
- **`private_key_jwt`:** `ClientCredentialStyle.PrivateKeyJwt`; one gateway key per cell per issuer,
  referenced from `Issuers:{name}:ClientAssertion:KeyRef` and read through `IClientSecretSource`,
  preloaded at start-up; the assertion built with `JsonWebTokenHandler` and passed per call on the
  code exchange, the refresh and the revocation. `clientSecretRef` stays required for now.
- **Audience:** the PingFederate deployment issues JWT access tokens whose `aud` follows `resource`
  where the provider supports it; the registry entry and the profile send what they send today;
  opaque tokens are out of scope.
- **Logout:** RP-initiated from discovery, as `standard`; revocation from discovery with the
  assertion. Nothing new.
- **Back-channel logout:** decided now, built after G6: a provider-facing path
  `/signout-backchannel-oidc`, a gate row, a rule id `auth.backchannel` that waits for a gate, and a
  session index as a new type `ISessionIndex` keyed on app and subject, with `SessionRecord`
  unchanged.

The composed suites gain a profile matrix so that every hermetic F- and N- row runs under `standard`
and under `pingfederate` against the stand-in, in one CI run each. That is the rehearsal; the real
provider is the proof.

## User Stories

1. As a platform administrator adopting PingFederate, I want to select it with one configuration
   value, `Issuers:{name}:Profile = pingfederate`, so that the provider is a profile and not a
   redesign.
2. As a security reviewer, I want the authorization code to leave the URL and the balancer's access
   logs, so that threat T13 stops being an accepted risk.
3. As a security reviewer, I want one gateway key per cell to authenticate every client, so that
   per-app secrets stop being a category of thing to rotate.
4. As workstream 6, I want the hermetic suites to run under both profiles, so that a profile only
   one test selects is never the profile nothing proves.
5. As workstream 3, I want the stand-in to speak PingFederate's dialect before the profile exists,
   so that the profile's first commit is proved against something.
6. As the integrator, I want back-channel logout's shape decided now, so that the durable store and
   the lease abstraction of G6 leave room for a session index.

## Implementation Decisions

Items 1 to 8 of `contracts/changes/p2-6-pingfederate-profile.md`, decided by the integrator before
P2-6c opens. The ones that touch an integrator path:

1. The `Profile` enum gains `pingfederate` (a widening; lands between gates). The enum edit and
   workstream 3's registration must reach `main` together, because `IdpProfileTests` requires
   exactly one registered profile per name the schema permits; workstream 3's branch merges the
   decision commit before it lands.
2. `ClientCredentialStyle` gains `PrivateKeyJwt = 2`, mapped by name to the library's `PostBody`
   with a `ClientAssertion` on the request, which is what the library does for itself. No
   `IIdpProfile` member changes.
3. `Issuers:{name}:ClientAssertion` as an optional closed object (`KeyRef`, `Algorithm`, `KeyId`) in
   `platform-config.schema.json`; `IssuerDefinition` gains no member, and workstream 3 reads the
   object from `IConfiguration` as `SecurityHeaders` reads `BrowserOrigin`.
4. `contracts/request-gate.md`: `/signin-oidc` takes `GET` and `POST`; `/signout-callback-oidc`
   keeps `GET`. `contracts/http-contract.md`'s cookie table says `SameSite=None` for the two
   login-state cookies under a `form_post` profile. An ADR amendment records when ADR-0007's
   "`form_post` off" stopped being universal.
5. No new package: the assertion is signed with `Microsoft.IdentityModel.JsonWebTokens` 8.19.2,
   already pinned and in `Platform.Identity`'s closure; `docs/dependencies.md`'s row gains
   `Platform.Identity`.
6. CI runs the hermetic suites a second time under the `pingfederate` profile, once the switch
   exists in `tests/Gateway.TestHarness`.

## Testing Decisions

- **The stand-in is proved first**, in `fixtures/Fixtures.Tests`, by conformance tests per gain, and
  `TestIdpDiscoveryTests` grows with the discovery document. The grant record names the client
  authentication method, so a test asserts which was used, as `IdpProfileTests` does today.
- **The profile is proved in workstream 3's project** against the stand-in in pingfederate mode: a
  login through `/par` and a `POST` callback, a refresh and a logout that each record
  `PrivateKeyJwt`, and `standard` and `cognito` logins byte-for-byte what they were (R-A1: no
  `response_mode`, no `request_uri`). The three calls are proved first in a `spikes/g0/` harness,
  cited by evidence id in the profile's first commit, because none of the library behaviour was run
  while this was drafted.
- **The composed suites run a profile matrix.** `GatewayHarnessOptions.Profile` readable from the
  environment; `TestBrowser` follows a `form_post` page as a cross-site `POST` navigation with
  `Origin`; the rows that read the authorize URL, replay a callback or send a canary callback read
  the stand-in's record or post a body instead; N-16's canaries include the assertion key. Under
  `pingfederate` the authorize URL carries only `client_id` and `request_uri`, the callback is a
  `POST`, and no code appears in any request line or URL the browser recorded.
- **Every new invariant test names its mutation** in the merge body, as for every other row.
- **The deployed run records what only the provider can answer:** whether `aud` survives a refresh
  without `resource`, whether the token endpoint takes `resource`, which `aud` the assertion needs,
  whether `/par` takes the assertion, what `end_session_endpoint` does to the provider's session,
  and whether a refresh answer carries an ID token (T-B13).

## Out of Scope

- A PingFederate instance, and the identity root and app clients for it: the deployed tier, which
  needs an instance nobody has.
- Removing `clientSecretRef` under `private_key_jwt`: five things assume the value
  (`OAuthDefinition`, the validator and the secret preload, `spactl app add`, the apps root counted
  by P-02, and `OidcOptionsSetup`); a later request once they agree.
- Building back-channel logout: after G6, blocked by P2-1 and P2-3 and by a gate for its rule id.
- Opaque access tokens and introspection in the API: criterion 7 makes the API owner's.
- A JWKS endpoint on the gateway: deferred with back-channel logout; the public half is registered
  on each client at the provider.
- `sid` on the session record: a `sid`-only logout token is refused until a gate adds it.

## Further Notes

**The ticket graph.** Owners are `OWNERSHIP.md`'s role names; "Blocked by" names a change request by
file name (its decision), a ticket by id, and what the deployed tier needs.

| Ticket | Owner | Paths | What to build | Acceptance | Blocked by | Tier |
| --- | --- | --- | --- | --- | --- | --- |
| P2-6a The stand-in speaks PingFederate's dialect | ws5-fixtures | `fixtures/TestIdp/`, `fixtures/Fixtures.Tests/` | `/par` with a `RequirePar` switch and `request_uri` resolution at `/authorize`; `response_mode=form_post`; `private_key_jwt` at `/token`, `/revoke` and `/par` against a public JWK on `TestIdpClient`, recorded as `ClientAuth.PrivateKeyJwt`; `sid` in every ID token; a back-channel logout sender with a logout URI per client and two fault switches; discovery advertises each | [ ] `/par` answers a single-use `request_uri`; `/authorize` with it issues a code, and refuses a used, expired or foreign one; with `RequirePar` on, an authorize that pushed nothing is refused `invalid_request`. [ ] `response_mode=form_post` answers an HTML form posting `code` and `state` to the redirect URI; `query` is unchanged. [ ] A grant with a valid assertion records `PrivateKeyJwt`; a wrong key, wrong `aud`, expired or replayed `jti` is refused `invalid_client` at all three endpoints. [ ] Every ID token carries `sid`. [ ] The sender posts a logout token with `events`, `sub`, `sid` and `jti` and records the answer; the unpublished-key and no-`events` switches produce tokens an endpoint must refuse. [ ] `TestIdpDiscoveryTests` lists the new fields. Each assertion's failing mutation named in the merge body | none | hermetic |
| P2-6b Decide the P2-6 change request | integrator | `contracts/`, `src/Platform.Abstractions/`, `tests/Contracts.Tests/`, `docs/adr/`, `docs/dependencies.md` | Items 1 to 3 of `contracts/changes/p2-6-pingfederate-profile.md`; record items 4 to 8 | [ ] The `Profile` enum admits `pingfederate` and the contract test flips. [ ] `ClientCredentialStyle.PrivateKeyJwt` with its header sentence. [ ] The `Issuers:{name}:ClientAssertion` schema object. [ ] The `/signin-oidc` gate row takes `POST`. [ ] The cookie table's `SameSite` prose. [ ] An ADR amendment for ADR-0007 and ADR-0011. [ ] The Decision section written beside the request, deferrals included | none | decision |
| P2-6c The pingfederate profile | ws3-identity | `src/Platform.Identity/`, `tests/Platform.Identity.Tests/`, `spikes/g0/` | `PingFederateIdpProfile`: `Name`, `PrivateKeyJwt`, `Configure` sets `ResponseMode = form_post`, `SameSite=None` on the two login-state cookies, `PushedAuthorizationBehavior.Require`, an `OnPushAuthorization` that adds the assertion and calls `HandleClientAuthentication()`, an `OnAuthorizationCodeReceived` that sets `TokenEndpointRequest.ClientAssertion` and `ClientAssertionType` and clears `ClientSecret`; sign-out as `standard`. An assertion builder over `JsonWebTokenHandler` (`iss` and `sub` the client id, `aud` the issuer, `jti`, a five-minute `exp`), the key from `Issuers:{name}:ClientAssertion:KeyRef` through `IClientSecretSource`, preloaded at start-up. `ProfileTokenEndpoints` and `CredentialStyles` map the third style; `AccessTokenSource.RefreshAsync` passes `UserTokenRequestParameters.Assertion`; `SessionEnd.RevokeAsync` sets `TokenRevocationRequest.ClientAssertion`. Registration, merged with P2-6b's enum commit | [ ] `IdpProfileTests`: three names, three styles. [ ] A login on the stand-in in pingfederate mode completes through `/par` and a `POST` callback; the grant records `PrivateKeyJwt` at `/par` and `/token`. [ ] A refresh and a logout record `PrivateKeyJwt` at `/token` and `/revoke`. [ ] `standard` and `cognito` logins are byte-for-byte what they were (R-A1). [ ] A pingfederate issuer with no readable key fails start-up naming the setting and no value. [ ] The three calls proved first in a `spikes/g0/` harness, cited by evidence id in the first commit. Mutations named | P2-6a, P2-6b | hermetic |
| P2-6d The callback takes a POST | ws4-proxy | `src/Platform.Security/`, `tests/Platform.Security.Tests/` | `Row.Callback` splits: `/signin-oidc` passes `GET` and `POST`; `/signout-callback-oidc` passes `GET` | [ ] `POST /signin-oidc` with `Sec-Fetch-Site: cross-site` and a foreign `Origin` reaches the next stage. [ ] `POST /signout-callback-oidc` is still 405 `gate.method`. [ ] `PUT /signin-oidc` is 405 with `Allow: GET, POST`. [ ] N-07 and N-27 unchanged. Mutation: swapping the two paths' method sets fails the two new cases | P2-6b | hermetic |
| P2-6e The composed suites run under a profile matrix | ws6-verification | `tests/Gateway.TestHarness/`, `tests/Gateway.IntegrationTests/`, `tests/Gateway.SecurityTests/`, `tests/e2e/`, `docs/gaps.md` | `GatewayHarnessOptions.Profile` readable from the environment, with `pingfederate` turning on the stand-in's switches and registering each client's JWK; `TestBrowser` follows a `form_post` page as a cross-site `POST` navigation with `Origin`; the rows that read the authorize URL, replay a callback or send a canary callback read the stand-in's record or post a body instead; N-16's canaries include the assertion key | [ ] Every F- and N- row the hermetic tier runs passes under `standard` and under `pingfederate`, in one CI run each. [ ] Under `pingfederate` the authorize URL carries only `client_id` and `request_uri`, the callback is a `POST`, and no code appears in any request line or URL the browser recorded. [ ] N-03 replays the posted body and is refused with no session. [ ] N-16: the private key, the assertion and the pushed body appear in no log line. [ ] `docs/gaps.md` names what the stand-in cannot show about PingFederate | P2-6a, P2-6c, P2-6d | hermetic |
| P2-6f The deployed run against PingFederate | ws6-verification, with ws1-infra for `infra/` and `tools/scripts/` | `tests/e2e/`, `docs/runbooks/`, a second identity root under `infra/envs/poc/` or its equivalent, `infra/envs/poc/apps/` | A PingFederate instance reachable from the cell, one client per app with the gateway's public key, the callback and post-logout URIs registered and PAR required; a private key parameter per cell; the task environment with `Issuers:{name}:Profile = pingfederate` and `ClientAssertion:KeyRef`; the 6.9 shape repeated | [ ] F-01 to F-06 and F-08 deployed. [ ] The deployed negative subset, N-26 included. [ ] Recorded, with the provider's document: the six questions under Testing Decisions. [ ] The runbook for rotating the gateway key. [ ] The annotated `g7-pingfederate` tag naming the run | a PingFederate instance, which nobody has; P2-6e; the deployed gate runs (#60, #61, #63) | needs a real provider |
| P2-6g Back-channel logout (deferred) | ws3-identity for the endpoint; integrator for `ISessionIndex`, the gate row and the rule id; ws2, ws4 and ws6 for one reserved-path entry each | `src/Platform.Identity/`, `src/Platform.Abstractions/`, `contracts/`, `src/Platform.Hosting/Assets.cs`, `src/Platform.Security/RequestGate.cs`, `src/Platform.Telemetry/RequestTelemetry.cs` | `POST /signout-backchannel-oidc`; the logout token validated per Back-Channel Logout 1.0 section 2.6; `ISessionIndex.KeysAsync(appId, subject)` on both stores; every key ended through `SessionEnd`; rule id `auth.backchannel` | [ ] A valid logout token from the stand-in's sender ends every session of that subject on that app, on every task, and no other app's. [ ] A token with a wrong key, wrong `aud`, no `events`, a replayed `jti` or `sid` alone is refused under `auth.backchannel` and ends nothing. [ ] The store contract suite covers the index on both stores | item 7 of `contracts/changes/p2-6-pingfederate-profile.md`; `contracts/changes/p2-3-session-lease-abstraction.md`; P2-1a; a gate for the new rule id | hermetic, then deployed |

**Landing order.** P2-6a and P2-6b in parallel; P2-6c merges P2-6b's decision commit; P2-6d beside
it; P2-6e once the three are on `main`; P2-6f when an instance exists; P2-6g after G6 and the gate
decision on its rule id.

**What can start on the first night:** P2-6a (ws5) and P2-6b (the integrator's decision).
