# What `fixtures/TestIdp` needs to register N clients behind one host, and how its claims differ per client

- **Ticket:** #768, "What fixtures/TestIdp needs to register N clients behind one host, and how its
  claims differ per client" (`wayfinder:research`), on the map #765.
- **Date:** 2026-11-03. **Branch:** `research/testidp-n-clients-one-host`, pushed and never merged, as
  the map rules.
- **Method:** reading at commit `e7ffb47` (origin/main on 2026-11-03): `fixtures/TestIdp`,
  `fixtures/MockApi`, `fixtures/Fixtures.Tests`, `fixtures/apps`, `src/Platform.Testing`,
  `tests/Gateway.TestHarness`, the composed suites under `tests/`, `src/Platform.Identity` (read only,
  for what the gateway sends; A-01 holds and nothing here changes it), `infra/envs/poc/apps/main.tf`,
  `docs/plan/04-tests.md`, `contracts/test-ids.md`, `contracts/upstream-contract.md`, `GLOSSARY.md`,
  ADR-0001, ADR-0011, ADR-0017 and the G0 Cognito run (`spikes/g0/cognito/`). Three pages of the
  Amazon Cognito developer guide were fetched on 2026-11-03 (authorize endpoint, access token,
  resource servers, logout endpoint); the "user pool sign-out" page returned only its title, so the
  Cognito logout facts rest on the logout-endpoint page and on ADR-0017's C-2.14 and C-2.15. Section
  8 lists what I executed against what I read. SDK 10.0.401 (`global.json` pins 10.0.100 with
  `rollForward: latestFeature`). No container engine on this machine; no AWS credential or API was
  used. No fixture and no tracked file other than this document was changed; the probe lives in the
  scratchpad and is reported, not committed. No token value appears in this document or in the
  probe's output: the fixture's own `Label` names tokens by serial.

## Answer in brief

1. **Registration today.** A client is `TestIdpClient(ClientId, Secret, RedirectUris, PostLogoutUris)`:
   two ordinal string sets, matched exactly, keyed by client id in a `ConcurrentDictionary`; no
   scopes, no resource and no host are registered per client, and registration is live while the
   provider runs (`fixtures/TestIdp/TestIdpRecords.cs:29-41`,
   `fixtures/TestIdp/TestIdentityProvider.cs:53,144-174`). The hermetic harness registers one client
   per fixture app, `client-{appId}`, with `https://{host}/signin-oidc` and two post-logout URIs
   (`tests/Gateway.TestHarness/GatewayHarness.cs:694-720`), and the apps root creates the Cognito
   client the same way, one callback and one logout URL per app (`infra/envs/poc/apps/main.tf:66-87`).
2. **N clients under one host are expressible at the fixture today, with no change.** Nothing checks
   that a redirect URI is unique across clients, so N clients may all register
   `https://shell/signin-oidc`, or each its own `https://shell/signin-oidc/{fragmentId}`; both forms
   were exercised by the probe (section 8, items 1 and 2). What is *not* expressible is the wiring
   around the fixture: `GatewayHarness` is one client per `appId` and `HarnessApp` has one
   `ClientId`; `EntryFor` builds an entry with no `fragments[]`; `MockApiOptions.Default` and
   `fixtures/MockApi/Program.cs` take exactly `App1Client` and `App2Client` (section 2). The gateway's
   side, `CallbackPath` compared by `==` to `/signin-oidc`
   (`src/Platform.Identity/OidcOptionsSetup.cs:28,76`;
   `src/Platform.Identity/CallbackBlindCookieManager.cs:28-36`), is the dispatch prototype's question
   and not this ticket's.
3. **The fixture does not model a provider session, so "silent" is trivial rather than proven.**
   `/authorize` has no login page, sets no cookie, reads no cookie and ignores `prompt`; every valid
   request is answered with a code for `login_hint` or `DefaultSubject`
   (`TestIdentityProvider.cs:308-337`; probe items 1, 7 and 8). A second client's login therefore
   completes without a prompt, as Cognito's did in C-2.6, but for a different reason: the fixture
   cannot produce `login_required`, cannot re-prompt after a logout (C-2.14), and has no one-hour
   session cookie. The gateway sends neither `prompt` nor `login_hint` (no occurrence in
   `src/Platform.Identity`). A test of "the fragment's login completes silently *because* the shell's
   login left a session" needs a fixture switch that does not exist yet: a provider session cookie
   set at `/authorize`, `prompt=none` answered with `login_required` without it, and both logout
   endpoints clearing it. That is workstream 5's change to file, if the spec wants the behaviour
   proven and not assumed.
4. **The one claim that identifies the issuing client is `client_id`.** Per client the access token
   differs in `client_id` always; in `aud` only if the request's `resource` differed, because
   `resource` maps to `aud` with no per-client allowlist; in `scope` only if the request asked for a
   different scope, because the fixture echoes whatever `scope` the request sent and validates none
   (`TestIdentityProvider.cs:313-331,502-524`; probe items 3 and 4: a fragment client obtained the
   shell's `aud`). The ID token's `aud` is the client id (`:564`). So a fragment's upstream refuses a
   token issued to another client by `client_id` admission (`fixtures/MockApi/Routes.cs:81-98,111`,
   403 `client_not_admitted`), which is what N-17's first half and F-07 already prove; the audience
   check proves resource binding, not client identity. Cognito's access token is the same shape:
   `client_id` is "the user pool app client that authenticated your user" and `aud` is "present only
   if your application requested a resource binding" (section 4).
5. **Revocation is per authenticating client; logout touches nothing.** `/revoke` marks a refresh token
   revoked only when it belongs to the authenticating client (`known`:
   `TestIdentityProvider.cs:425`), answers 200 either way, and records the client; one client cannot
   revoke another's token (probe item 5; `fixtures/Fixtures.Tests/TestIdpRevocationTests.cs:56`).
   `/endsession` and `/logout` validate the client and the return URI and redirect: they read and
   write no session and revoke no token, so one client's logout leaves every client's refresh tokens
   redeemable and the next `/authorize` still answers a code (`:447-499`; probe items 6 and 7). The
   gateway revokes with the app's own client credentials (`src/Platform.Identity/SessionEnd.cs:83-104`),
   so the map's "one logout revokes every token set" is N revocation calls, each under its fragment's
   client id and secret, and `Revocations[].ClientId` lets a test assert exactly that.
6. **The mock API checks audience per listener, and that is enough for one listener per fragment
   API.** `ValidateAudience`/`ValidAudience` are one value per listener (`fixtures/MockApi/MockApiHost.cs:135-136`),
   admission is one `ClientAdmission` per listener (`:88,108-109`), and `/whoami` reports `clientId`
   and `audience`. A fragment API that is its own listener needs no mock API change beyond
   configuration: a third `ApiListenerOptions` with its own `Audience` and `AdmittedClients`, which
   `MockApiOptions` already expresses and `MockApiOptions.Default`/`Program.cs` do not. The case the
   map allows, "the same upstream origin under the shell and under a fragment, with different
   tokens", is what needs code: a listener that accepts two audiences (`ValidAudiences`) and a
   route-level `aud` check in `Guard`, plus an admission set per route rather than per listener.

## 1. How clients are registered today

### The fixture

- `TestIdpClient` is a record of a client id, a secret, a set of redirect URIs and a set of
  post-logout URIs, both `StringComparer.Ordinal`, "matched exactly"
  (`fixtures/TestIdp/TestIdpRecords.cs:29-41`). The two `Create` overloads take one redirect URI and
  one or several post-logout URIs.
- `AddClient` upserts by client id into a `ConcurrentDictionary`, "safe while the provider is running,
  which is what F-07 onboards into"; `RemoveClient` and `SetClientSecret` exist for rotation
  (`fixtures/TestIdp/TestIdentityProvider.cs:144-174`).
- Nothing registers scopes, a resource or a host. Discovery advertises `scopes_supported: openid,
  profile` as a static list (`:291`) and the authorize endpoint never reads it.
- `/authorize` checks, in order: client known, `redirect_uri` in the client's set, `response_type=code`,
  PKCE S256 present; then it issues a code bound to the client, the redirect URI, the subject, the
  nonce, the challenge, the scope and the resource (`:324-336`, `AuthorizationCode` at `:40`).
- `/token` authenticates the client by `client_secret_basic` or `client_secret_post` (`ReadClient`,
  `:630-655`) and refuses a code issued to another client or another redirect URI (`:362-366`, the
  N-03 and N-04 half the provider owns).

### The hermetic harness

- `GatewayHarness.Register` gives each app of `GatewayHarnessOptions.Apps` (default `app1`, `app2`)
  the client `client-{appId}`, a random secret, the redirect URI `https://{host}/signin-oidc` and two
  post-logout URIs, `/signout-callback-oidc` for the standard profile and `/` for the Cognito profile
  (`tests/Gateway.TestHarness/GatewayHarness.cs:694-720`). The host is the fixture definition's
  (`fixtures/apps/app1.json:3`, `app2.json:3`, `fixtures/onboarding/app3.json:3`), one host per app.
- `OnboardAsync` (F-07) and `RotateClientAsync` (N-29 to N-31) register the same way, one client per
  app (`:596-631`).
- `EntryFor` writes `oauth.issuer`, `oauth.clientId` and `oauth.clientSecretRef` into the fixture
  definition and repoints `apis[].upstream` at the harness's listeners (`:640-672`). It knows no
  `fragments[]`, because the schema has none (`contracts/registry.schema.json`, `$defs/oauth`, one
  `clientId` per entry).
- The mock API gets "the two listeners the plan fixes": `MockApiOptions.Default(idp.Issuer,
  idp.DiscoveryUri, ClientIdFor("app1"), ClientIdFor("app2"))` with the ports zeroed (`:181-183`).
- `Platform.Testing` has no client registration of its own: `TestApps.Entry` builds an
  `AppDefinition` with `ClientId: "client-" + appId` (`src/Platform.Testing/TestApps.cs:39`) and
  `StubIdpProfile` records calls; neither talks to the fixture, which is right, since a workstream
  project builds against the doubles and the composed suites against the provider.
- The e2e local stack reaches the same harness and switches the user by `Harness.Idp.DefaultSubject`
  (`tests/e2e/Stack/LocalStack.cs:173-174`).

### The Cognito side, for comparison

- One `aws_cognito_user_pool_client` per app: `generate_secret = true`, `allowed_oauth_flows =
  ["code"]`, `allowed_oauth_scopes = each.value.oauth.scopes`, `callback_urls =
  ["https://{host}/signin-oidc"]`, `logout_urls = ["https://{host}/"]`
  (`infra/envs/poc/apps/main.tf:66-87`). The scopes are registered per client at Cognito; at the fixture
  they are not.

## 2. Whether N clients under one host are expressible

**At the fixture: yes, today, in either form.** Redirect URIs are per-client sets with no cross-client
uniqueness; the lookup is "this client's set contains this URI" (`TestIdentityProvider.cs:325`). The
probe registered three clients on `https://shell.spa-poc.test:8443`: the shell and one fragment both on
`/signin-oidc`, and a second fragment on `/signin-oidc/orders-fragment`. All three authorize requests
returned 302 with a code, the log shows each client against its own redirect URI, and the fragment
registered on its own path was refused `unregistered_redirect_uri` when it asked for the shared one
(section 8, items 1 and 2). Post-logout URIs behave the same way, and a hint-less standard logout
matches the URI against every client's list (`RegistersPostLogoutUri`, `:614-617`), which under one host
means any client's logout may return to any URI some client on that host registered. That is lenient but
harmless in a fixture, and `Logouts[].ClientId` still records which client the ID token hint named.

**What would have to change is around the fixture, not in it:**

| Where | Today | For N clients under one host |
| --- | --- | --- |
| `GatewayHarness.Register` / `HarnessApp` | One client per `appId`, one `ClientId` | A fragment registration: `AddClient` per fragment with the shell's host and the chosen callback form, a secret per fragment in `FakeClientSecretSource`, and a way to name a fragment's client in assertions (`harness.App("app1")` returns one client) |
| `GatewayHarness.EntryFor` | Writes one `oauth` block | Writes `fragments[]` once the schema has it (the map's nested shape, ADR-0019 migration) |
| `MockApiOptions.Default`, `fixtures/MockApi/Program.cs` | Exactly two listeners, `App1Client` and `App2Client` by name | A listener for the fragment's API admitting the fragment's client, or an admission list per route (section 6) |
| `IdpHarness` (`fixtures/Fixtures.Tests/IdpHarness.cs:56-63`) | Two constants, one host each | Conformance tests for two clients on one host, if workstream 5 wants the property under test rather than assumed |
| `contracts/http-contract.md:21` and `ShellGate` (`src/Platform.Identity/ShellGate.cs:30,56-58`) | `/signin-oidc` reserved; the gate matches by `StartsWithSegments`, so `/signin-oidc/{fragmentId}` is already ungated | Nothing for the fixture; the callback-dispatch prototype decides which form the gateway answers |

The gateway's handler matches its callback by `Options.CallbackPath == Request.Path`
(`CallbackBlindCookieManager.cs:28-36`, quoting `RemoteAuthenticationHandler.ShouldHandleRequestAsync`),
so under one host the `oidc:{appId}` scheme and `/signin-oidc` cannot tell N clients apart by path or
host today. That is the "scheme and callback dispatch" item the map sends to research and prototype,
and nothing in the fixture constrains the answer: the fixture will redirect to whichever registered URI
the request names.

## 3. Whether a second client's login is silent

### What the fixture does

- "There is no login page. Whoever the test says is typing is `?login_hint=bob`, and `DefaultSubject`
  otherwise" (`TestIdentityProvider.cs:308-316`). The endpoint reads no cookie and sets none; the probe
  saw no `Set-Cookie` on any of three authorize answers or on either logout (items 1 and 6).
- `prompt` is not read. `prompt=none` and `prompt=login` both got a code (item 8). The gateway sends
  neither: `OidcOptionsSetup` sets no `Prompt` and no `login_hint`, and the string `prompt` does not
  occur in `src/Platform.Identity` or `src/Platform.Abstractions`.
- The subject is per request. The composed suites switch users by `harness.Idp.DefaultSubject`
  between browsers (`tests/Gateway.SecurityTests/Negative/LoginTests.cs:167,175`). Under one host with
  N clients, a test that moved `DefaultSubject` between the shell's login and a fragment's would get two
  subjects in one browser session record; Cognito would not, since the provider session carries the
  user. A fragment test must therefore hold `DefaultSubject` still or pass the same `login_hint`, and
  nothing in the fixture enforces that.

So a second client's authorize request completes "silently" in the sense the ticket asks, but it would
complete silently in a fresh browser too. The fixture proves the gateway's sequence (F-02 proves
each app's redirect and callback, `tests/Gateway.IntegrationTests/Functional/SignInTests.cs:60-87`);
it cannot prove that the fragment's login rides on the shell's provider session.

### What Cognito does

- Managed login "sets browser cookies that are valid for one hour", and "users must sign in unless
  their browser has a valid managed login session cookie"; `prompt=none` "silently continues
  authentication for users who have a valid authenticated session. With this prompt, users can
  silently authenticate between different app clients in your user pool", else `login_required`;
  `prompt=login` forces re-authentication ([access token][cog-at], [authorize endpoint][cog-authz]).
- G0 observed it: app 2's login "asked for no password: the provider's session from app1's login made it
  silent" (C-2.6), and after app 1's logout "the next login to app1 asks for a password: the provider's
  logout ended its session for every client in that browser" (C-2.14)
  (`spikes/g0/cognito/output-cognito.txt:48-53,72-75`; ADR-0017 decision 3; `GLOSSARY.md:70-76`).

### What a fidelity switch would need

If the spec wants "a fragment's login completes silently once the browser holds the shell's provider
session" as a proven behaviour rather than an assumption carried from C-2.6, the fixture needs, behind a
switch so every existing suite keeps its stateless provider:

1. a provider session cookie set at `/authorize` on the first login and read on later ones, scoped to
   the provider's origin (the test browser keeps cookies per host, `tests/Gateway.TestHarness/TestBrowser.cs:60-61`,
   so it would carry it);
2. `prompt=none` answered with `error=login_required` on the redirect URI when no session exists, and
   `prompt=login` prompting (in a fixture, "prompting" can only be a recorded outcome, since there is no
   page);
3. both logout endpoints clearing that cookie, so the C-2.14 re-prompt and the map's "one logout" can be
   asserted through `Authorizations[].Outcome`;
4. the subject bound to the session rather than to each request, so N logins in one browser share one
   `sub`.

That is a change to `fixtures/TestIdp`, owned by workstream 5, and outside this ticket.

## 4. Which claims differ per client

Access token, from `Issue` (`TestIdentityProvider.cs:502-524`), and ID token (`:546-567`):

| Claim | Access token | ID token | Differs between two clients of one subject? |
| --- | --- | --- | --- |
| `iss` | provider base URL (or `IssuerOverride`) | same | No |
| `sub`, `username` | the subject | `sub`, `name`, `email` | No, if the same subject logged in |
| `client_id` | the authenticating client | not present | **Yes, always** |
| `aud` | the `resource` the authorize request named, or absent | the client id | Access token: only if the requests named different resources. ID token: yes, always |
| `scope` | the authorize request's `scope`, verbatim | not present | Only if the requests asked for different scopes |
| `token_use` | `access` (`Faults.AccessTokenUse`) | `id` | No |
| `jti` | a serial | `id-` + serial | Yes, per token |
| `nonce`, `at_hash`, `auth_time` | not present | present | per login |

Three facts for the audience design:

- **`aud` is caller-chosen.** The fixture validates `resource` against nothing: a fragment client that
  names the shell's resource gets the shell's `aud` (probe item 4). `MockApiTests.A_token_bound_to_another_api_is_refused_by_orders`
  (`fixtures/Fixtures.Tests/MockApiTests.cs:212-226`) already relies on this to mint a wrong-audience
  token. Cognito's documented rule is the same in kind: `resource` "must be a URL" and "If the
  requested resource is a user pool resource server, the resource server identifier must be in a URL
  format"; the page names no per-app-client allowlist of resources ([resource servers][cog-rs]). So
  at both providers, `aud` tells an API which *resource* the token was minted for, not which *client*
  asked.
- **`scope` is caller-chosen at the fixture and policed at Cognito.** The fixture copies the request's
  `scope` into the token (`:314,331,520`). Cognito issues only scopes associated with the app client:
  "Any scope used must be associated with the client, or it will be ignored at runtime"
  ([authorize endpoint][cog-authz]) and "If you request a scope that you haven't associated with your
  app client ... authentication fails" ([resource servers][cog-rs]); the apps root sets
  `allowed_oauth_scopes` per client. A hermetic test cannot therefore show that a fragment client is
  *refused* a scope the shell holds; it can only show what the mock API does with the scopes a token
  carries.
- **`client_id` is the discriminator.** It is in every access token, it is what `ClientAdmission`
  checks (`Routes.cs:111`, "Cognito puts the client in `client_id`"), and the upstream contract lists it
  among what the API checks (`contracts/upstream-contract.md:15`). The map's negative test, "a
  fragment's API rejects a token issued to another client of the same pool", is the existing
  `client_not_admitted` refusal pointed at a fragment's client; N-17's first half
  (`tests/Gateway.SecurityTests/Negative/SignedInApiTests.cs:90-103`) and F-07's admission step
  (`fixtures/Fixtures.Tests/MockApiTests.cs:243-264`) are its shape.

The G0 run saw the same claim set from Cognito: "app1 aud=https://orders.spa-poc.test, client_id=present,
scope=openid profile https://orders.spa-poc.test/read, token_use=access ... app2 aud=(none)"
(`spikes/g0/cognito/report-cognito.md:50`), and the developer guide's sample payload carries `client_id`,
`aud`, `scope`, `token_use`, `origin_jti` and `username` ([access token][cog-at]). The fixture lacks
`origin_jti`, `version`, `event_id` and `cognito:groups`, none of which the mock API reads.

## 5. Revocation and logout per client

**Revocation** (`/revoke`, `TestIdentityProvider.cs:409-444`):

- Client authentication is required; an unauthenticated or wrong-secret call is 401 `invalid_client`
  (`:418-419`; `TestIdpRevocationTests.cs:41`).
- A token is `known` only when `_refresh[token].ClientId == clientId` (`:425`). An authenticated client
  revoking another client's token gets 200, `Known=false`, `Revoked=0`, and the token stays redeemable
  (probe item 5; `TestIdpRevocationTests.cs:56`). With `RevokeCascadesToFamily` the family is
  revoked, and a family never crosses clients because `RefreshState.ClientId` is fixed at issue
  (`:531-538`).
- `RevokeAllForClient(clientId)` is the test-side "sign this user out everywhere" and is per client
  (`:191-203`); there is no per-subject revocation.
- The gateway's `SessionEnd.RevokeAsync` builds the request from the app's own scheme configuration,
  `client.ClientId` and `client.ClientSecret` in the profile's credential style
  (`src/Platform.Identity/SessionEnd.cs:83-104`). Under the map's one session record holding N token
  sets, one `POST /bff/logout` is therefore N revocations, each authenticated as the fragment whose
  refresh token it presents; a revocation sent under the shell's credentials for a fragment's token is
  a 200 that revokes nothing, at the fixture and, by RFC 7009's "the client ... must be authorized to
  revoke the token", at a real provider. `Revocations[].ClientId` and `.Known` let a test assert each
  one landed under the right client.

**Logout** (`/endsession` `:447-477`, `/logout` `:480-499`):

- Standard: identifies the client from `client_id` or the ID token hint's `aud`, refuses an unknown
  client or an unregistered `post_logout_redirect_uri` (another client's URI is refused when the client
  is known, `TestIdpEndSessionTests.cs:68`), answers a hint-less, URI-less logout with "signed out", and
  redirects with `state`.
- Cognito style: `client_id` and `logout_uri` registered on that client, no state.
- Neither endpoint reads or writes `_refresh`, `_codes` or any session: after the shell's logout in
  either style, the shell's and the fragment's refresh tokens were both still redeemable and the next
  authorize for the fragment answered a code for `alice` (probe items 6 and 7).

So at the fixture, one client's logout does nothing to the other clients, in two senses that Cognito
separates: it leaves their tokens alone, which matches C-2.15 and the glossary ("the other app's session
still answers and still refreshes"), and it leaves their *provider session* alone, which does not match
C-2.14 (Cognito's `/logout` "signs out the user" for the browser, every app client in the pool). The map's
"one logout: revoke every token set, remove the record, then one provider logout" is expressible against
the fixture as N revocations plus one `/endsession` or `/logout` under whichever client the gateway
chooses; the fixture will accept the logout under any registered client whose return URI is registered,
and records `Logouts[].ClientId`.

## 6. What the mock API would need to check audience per route

Today (`fixtures/MockApi/MockApiHost.cs:85-171`, `Routes.cs:81-98`):

- One listener per API, "because the two APIs differ in how they validate ... that is a property of the
  listener's token validation rather than of a route" (`MockApiHost.cs:23-25`).
- Per listener: `ValidIssuer`, `ValidateAudience = listener.Audience is not null`, `ValidAudience =
  listener.Audience` (`:123-137`), `token_use == access` (`:141-153`), then per route `Guard`:
  authenticated, `client_id` admitted, route scope present (`Routes.cs:81-98`). `/whoami` reports
  `api`, `clientId`, `subject`, `scopes`, `audience` (`:36-44`).
- Configuration: `MockApiOptions(Issuer, MetadataAddress, Listeners)` with any number of
  `ApiListenerOptions(Name, Port, ScopeIdentifier, Audience, AdmittedClients)`
  (`MockApiOptions.cs:18-73`), but `Default` and `Program.cs` build exactly `orders` and `reports`
  from `App1Client` and `App2Client` (`MockApiOptions.cs:28-47`, `Program.cs:21-29`).

Three shapes, in increasing cost:

1. **A fragment API as its own listener: no code change.** `new ApiListenerOptions("fragment-orders",
   0, "https://orders-fragment.spa-poc.test", Audience: "https://orders-fragment.spa-poc.test",
   AdmittedClients: [fragmentClient])`. The gateway's `/fragments/{fragmentId}/api/{name}/` route
   forwards the fragment's token to it; a shell token there is 403 `client_not_admitted` (or 401 if its
   `aud` differs), and a fragment token at `orders` is likewise refused. Only `MockApiOptions.Default`,
   `Program.cs` (a third configured client) and the harness's listener list change. This is enough for
   the map's negative test.
2. **The same upstream origin under the shell and under a fragment, with different tokens.** One
   listener then receives two clients' tokens, possibly with two audiences. Needs: `ValidAudiences`
   (plural) or `ValidateAudience` off at the handler with an `aud` check moved into `Guard`; an
   admission set per route or per `(route, audience)` rather than per listener; and `/whoami` already
   reports enough to assert which token arrived. `ApiListenerOptions` would grow a route table, which is
   a shape change to a fixture the plan fixed as "the two listeners the plan fixes".
3. **Audience per route in the strict sense** (`/whoami` wants `aud` A, `/admin` wants `aud` B on one
   listener): the same work as 2 with the audience on the route entry; nothing in the map asks for it.

In every shape the API's refusal of another client's token is admission by `client_id`; the audience
check only adds that a token minted for another resource is refused even by an API that would admit the
client. The mock API hand-writes no token validation and would not start: `ValidAudiences` and a claim
check in `OnTokenValidated` or `Guard` are the library's and the API owner's parts respectively.

## 7. Fidelity against Cognito, for the spec's test ids

| Behaviour | `fixtures/TestIdp` | Cognito (source) | For a fragment test |
| --- | --- | --- | --- |
| N clients, one host, one callback path | Expressible | Expressible: callback URLs are per app client; nothing on the page forbids two clients sharing one ([authorize endpoint][cog-authz] redirect rules) | Hermetic test can run; the deployed apps root would need `callback_urls` per fragment client |
| Second client's login without a prompt | Always, stateless | With a managed-login session cookie, one hour ([access token][cog-at]); C-2.6 | Hermetic tier cannot prove the "because"; the deployed tier can (C-2.6's shape) |
| `prompt=none` / `login_required` | Ignored | Honoured in managed login ([authorize endpoint][cog-authz]) | Not usable hermetically today |
| `client_id` in access token | Yes | Yes ([access token][cog-at]) | The discriminator |
| `aud` from `resource` | Yes, any string, one per request | Yes, any URL, one per request, carried over on refresh ([resource servers][cog-rs]); C-2.7, C-2.9 | Resource binding, not client identity |
| `scope` policed per client | No | Yes, `allowed_oauth_scopes` ([authorize endpoint][cog-authz]) | A scope refusal cannot be shown hermetically |
| Revoke another client's token | 200, nothing revoked | Authenticated as the owning client, in a Basic header (ADR-0017 decision 2; RFC 7009 section 2.1) | N revocations under N credentials |
| One client's logout and the others' tokens | Untouched | Untouched (C-2.15) | Matches |
| One client's logout and the provider session | No session to end | Ended for every client in the browser (C-2.14) | The re-prompt cannot be shown hermetically |
| Subject per login | Per request (`login_hint`/`DefaultSubject`) | Per provider session | Hold `DefaultSubject` still across shell and fragment logins |

## 8. What I executed and what I read

**Executed**, on 2026-11-03, SDK 10.0.401, macOS, in the worktree at `e7ffb47`:

- `dotnet test --project fixtures/Fixtures.Tests` with `--filter-class` for `MockApiTests`,
  `TestIdpCodeGrantTests`, `TestIdpEndSessionTests`, `TestIdpRevocationTests`,
  `TestIdpRefreshGrantTests` and `TestIdpDiscoveryTests`: 80 tests, 80 passed, 8.8 s. (A first run with
  a wildcard filter matched nothing and exited 5; the exact class names were then used.)
- A throwaway console probe in the scratchpad
  (`/private/tmp/claude-501/-Users-adammedford-Projects-spa-platform/c9fe588c-d75b-4c6d-a359-33e004857a6b/scratchpad/probe/Program.cs`,
  a `net10.0` project referencing `fixtures/TestIdp/TestIdp.csproj` by path, with central package
  management off; not committed). It started `TestIdentityProvider`, registered `client-shell` and
  `client-frag-shared` on `https://shell.spa-poc.test:8443/signin-oidc` and `client-frag-own` on
  `https://shell.spa-poc.test:8443/signin-oidc/orders-fragment`, and observed:
  1. all three authorize requests 302 with a code and no `Set-Cookie`; the log names each client with
     its own redirect URI;
  2. `client-frag-own` asking for the shared callback: 400 `unregistered_redirect_uri`;
  3. access tokens with `client_id` per client, `aud` and `scope` as requested, `sub=alice`,
     `token_use=access`; ID tokens with `aud` = the client id;
  4. `client-frag-own` requesting `resource=https://orders.spa-poc.test`: `aud` = the shell's resource;
  5. `/revoke` of the fragment's refresh token authenticated as the shell: 200, `Known=false`,
     `Revoked=0`, token still redeemable;
  6. `/endsession` then `/logout` for the shell: 302 to the registered URIs, no `Set-Cookie`, both
     clients' refresh tokens still redeemable;
  7. a later authorize for the fragment: 302 `ok`, subject `alice`;
  8. `prompt=none` and `prompt=login`: both 302 with a code.
  The probe printed claim values and statuses only; no token value was written anywhere.

**Read only** (file paths at `e7ffb47` unless noted):

- `fixtures/TestIdp/TestIdentityProvider.cs` (whole file; cited lines above), `TestIdpRecords.cs`,
  `TestIdpFaults.cs`, `TestIdp.csproj`.
- `fixtures/MockApi/MockApiHost.cs`, `MockApiOptions.cs`, `Routes.cs`, `ClientAdmission.cs`,
  `Program.cs`.
- `fixtures/Fixtures.Tests/IdpHarness.cs:50-140`, `MockApiHarness.cs`, `MockApiTests.cs`,
  `TestIdpCodeGrantTests.cs`, `TestIdpEndSessionTests.cs`, `TestIdpRevocationTests.cs` (names and
  comments).
- `fixtures/apps/app1.json`, `app2.json`, `fixtures/onboarding/app3.json`, `fixtures/cognito/pool.json`.
- `src/Platform.Testing/TestApps.cs`, `StubIdpProfile.cs`, `StageHost.cs:1-80`.
- `tests/Gateway.TestHarness/GatewayHarness.cs` (lines 100-260, 455-500, 586-740), `GatewayHarnessOptions.cs`,
  `TestBrowser.cs:60-79`.
- `tests/Gateway.IntegrationTests/Functional/SignInTests.cs:60-110`, `LogoutTests.cs`,
  `MockApiRouteTests.cs`, `OnboardingTests.cs`; `tests/Gateway.SecurityTests/Negative/SignedInApiTests.cs`,
  `LoginTests.cs:160-180`; `tests/e2e/Stack/LocalStack.cs:165-180` (grep hits and their context).
- `src/Platform.Identity/OidcOptionsSetup.cs:20-130`, `IdpProfiles.cs:1-80`, `SessionEnd.cs:40-112`,
  `ShellGate.cs:20-45`, `CallbackBlindCookieManager.cs:25-40`; `src/Platform.Abstractions/ISessionStore.cs:15-22`.
- `infra/envs/poc/apps/main.tf:60-95`.
- `docs/plan/04-tests.md:20-56`, `contracts/test-ids.md`, `contracts/upstream-contract.md:15-30`,
  `contracts/http-contract.md:15-25`, `contracts/registry.schema.json` (`$defs/oauth`),
  `GLOSSARY.md:60-80`, `docs/plan/03-workstreams-and-tasks.md:169-172`.
- ADR-0001, ADR-0011, ADR-0017; `spikes/g0/cognito/report-cognito.md:33,50,52`,
  `spikes/g0/cognito/output-cognito.txt:48-53,72-75,106-107`.
- Amazon Cognito developer guide, fetched 2026-11-03: [the authorize endpoint][cog-authz],
  [understanding the access token][cog-at], [scopes, M2M and resource servers][cog-rs],
  [the logout endpoint][cog-logout]. The "user pool sign-out" page returned only its title and is not
  relied on.

**Not verified:** Cognito's behaviour for two app clients sharing one callback URL under one host was
not exercised (G0 ran two hosts), and whether Cognito restricts `resource` per app client beyond "a
URL" was read from the developer guide, not probed. Both belong to the deployed stage this map adds.

[cog-authz]: https://docs.aws.amazon.com/cognito/latest/developerguide/authorization-endpoint.html
[cog-at]: https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-access-token.html
[cog-rs]: https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-define-resource-servers.html
[cog-logout]: https://docs.aws.amazon.com/cognito/latest/developerguide/logout-endpoint.html
