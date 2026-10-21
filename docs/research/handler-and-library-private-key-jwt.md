# What the handler and the token library do for `private_key_jwt`, `form_post` and pushed authorization

- **Ticket:** #217, part of #215; feeds #223
- **Request checked:** `contracts/changes/p2-6-pingfederate-profile.md` (items 2, 3, 4, "Packages",
  "What workstreams 3 and 5 can do meanwhile")
- **Date:** 2026-10-20
- **How:** every claim was read in the pinned sources and, where `dotnet` allowed, executed in a
  scratch harness against a recording stub provider. "Executed" below names the harness check that
  ran; "Read" names the file and symbol. The harness is described at the end and was not committed.

## Verdicts in one table

| # | Claim | Verdict | Evidence |
| --- | --- | --- | --- |
| 1 | Code exchange: the event can set the assertion with `ClientSecret` cleared; `RedeemAuthorizationCodeAsync` posts the message as it stands; `Validate` needs no secret | **Confirmed**, with one precision: "cleared" must be `null`, not `""` | Executed A6-A8, B3, B4, H1; read |
| 2 | Refresh: `UserTokenRequestParameters.Assertion` is preferred to `IClientAssertionService` and sent as `PostBody` with `client_assertion` | **Corrected**: the mechanism holds, but the library also posts `client_secret` beside the assertion whenever the scheme's `OpenIdConnectOptions.ClientSecret` is set, which `OidcOptionsSetup` sets for every app | Executed L1-L6; read |
| 3 | Revocation: `TokenRevocationRequest.ClientAssertion` is honoured | **Confirmed**, with the same `ClientSecret` caveat and one constraint: the style on that request must be `PostBody` or `Prepare()` throws | Executed L7-L11; read |
| 4 | `form_post` callback: a `POST` with `application/x-www-form-urlencoded` is read through `ReadFormAsync`; a body without `state` fails; `CallbackBlindCookieManager` still hides the session cookie | **Confirmed** for the handler (executed) and the cookie manager (read); the browser's `SameSite` behaviour and the request gate were not in the harness | Executed A6, A9-A12, C4; read |
| 5 | Pushed authorization: default `UseIfAvailable`; `Require` fails without the endpoint; `Disable` fails when discovery requires it; `HandleClientAuthentication()` suppresses `client_secret` | **Confirmed**, with two precisions: both failures are an `InvalidOperationException` thrown from the challenge, and the authorize URL after a push carries two telemetry parameters besides `client_id` and `request_uri` | Executed A2-A4, B1-B2, C1, D1, E1, F1, G1; read |
| 6 | Packages: `Microsoft.IdentityModel.JsonWebTokens` 8.19.2 is in `Platform.Identity`'s closure; `Duende.IdentityModel`'s `ClientAssertion` is reachable | **Confirmed**, with one precision about where the 10.0.12 pin enters the closure | Executed (package listing, trial build, architecture tests); read |

## Versions and sources

| Package (pinned in `Directory.Packages.props`) | Source read | Identity of the source |
| --- | --- | --- |
| `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12 | `dotnet/aspnetcore` tag `v10.0.12` (commit `cb21a42e`), `src/Security/Authentication/OpenIdConnect/src/` and `src/Security/Authentication/Core/src/` | The four files cited (`OpenIdConnectHandler.cs`, `OpenIdConnectOptions.cs`, `PushedAuthorizationBehavior.cs`, `RemoteAuthenticationHandler.cs`) were each diffed between `v10.0.12` and `release/10.0` at commit `a3bb8c0e` (2026-10-20) and are byte-identical, so the request's `release/10.0` citations and these `v10.0.12` ones name the same text |
| `Duende.AccessTokenManagement.OpenIdConnect` 4.2.0 | `DuendeSoftware/foss` tag `atm-4.2.0`, `access-token-management/src/` | The package's nuspec depends on `Duende.IdentityModel` 8.1.0 |
| `Duende.IdentityModel` 8.1.0 | `DuendeSoftware/foss` tag `im-8.1.0` (commit `9f24e82e`), `identity-model/src/IdentityModel/` | The IdentityModel files were read at their own tag because the monorepo's `atm-4.2.0` snapshot (commit `9c06a0fc`) is not 8.1.0's: `ProtocolRequest.cs` differs between the two (`im-8.1.0` adds a `ClientAssertionFactory` for DPoP retries; the `Prepare()` logic cited below is the same in both), while `HttpClientTokenRevocationExtensions.cs` and `Parameters.cs` are identical |
| `Microsoft.IdentityModel.*` 8.19.2 | `AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet` tag `8.19.2` | Only for `SetParameter` and the telemetry parameters |

Line numbers below are the `v10.0.12` file's.

## Claim 1: the code exchange

**Read.** `OpenIdConnectHandler.RunAuthorizationCodeReceivedEventAsync` (lines 1254-1294) builds the
`TokenEndpointRequest` as an `OpenIdConnectMessage` with `ClientId`, `ClientSecret = Options.ClientSecret`
(line 1261), `Code`, `GrantType`, `RedirectUri` and the PKCE `code_verifier`, then raises
`Events.AuthorizationCodeReceived` with it. Back in `HandleRemoteAuthenticateAsync` (lines 816-824),
unless the event set `HandledCodeRedemption`, `RedeemAuthorizationCodeAsync(tokenEndpointRequest)`
runs; that method (lines 983-1026) posts `new FormUrlEncodedContent(tokenEndpointRequest.Parameters)`
to the token endpoint over `Backchannel`, with no further edit of the parameters, and parses the
answer. `OpenIdConnectOptions.Validate` (lines 91-112) requires `ClientId`, `CallbackPath` and a
configuration source; it does not mention `ClientSecret`.

`OpenIdConnectMessage.ClientSecret`, `ClientAssertion` and `ClientAssertionType` are each
`SetParameter` on the message's dictionary (`OpenIdConnectMessage.cs` lines 240-271 at 8.19.2), and
`AuthenticationProtocolMessage.SetParameter` (lines 186-199) **removes** the parameter when the value
is `null` and **stores** it otherwise, an empty string included.

**Executed.** Scheme `pkj-secret` kept `Options.ClientSecret` set (as `OidcOptionsSetup` leaves it)
and its `OnAuthorizationCodeReceived` set `TokenEndpointRequest.ClientSecret = null`,
`ClientAssertionType` and `ClientAssertion`. The stub's token endpoint received
`client_assertion, client_assertion_type, client_id, code, code_verifier, grant_type, redirect_uri`,
no `client_secret` key and no `Authorization` header (A7, A8), and the login completed: the handler
validated the ID token and nonce and signed in to the cookie scheme (A6). Scheme `pkj-nosecret` had
`Options.ClientSecret = null` and never touched `ClientSecret` on the message; the posted form was the
same (B3), and the scheme passed `Validate`, challenged and signed in with no secret at all (B4).
Scheme `pkj-emptysecret` set `ClientSecret = ""`: the form then carried `client_secret=` with an empty
value beside the assertion (H1). The control scheme `secret-query` posted `client_secret` and no
assertion (C3).

**Consequence for the request.** The paragraph in "What workstreams 3 and 5 can do meanwhile" is
right, and one word needs sharpening: "with `ClientSecret` cleared" means set to `null`; an empty
string is posted. Where the hook lives: `CodeExchange.Apply` (`src/Platform.Identity/CodeExchange.cs`)
returns early for any style but `AuthorizationHeader`, so `PrivateKeyJwt` needs either a new arm there
or the profile's own `OnAuthorizationCodeReceived` set in `Configure`, which `OidcOptionsSetup` runs
first (`OidcOptionsSetup.cs` lines 104-105: `profile.Configure(...)` then `CodeExchange.Apply(...)`).
If the profile's `Configure` also sets `options.ClientSecret = null` (see claim 2), the message needs
no clearing at all.

## Claim 2: the refresh

**Read.** `OpenIdConnectUserTokenEndpoint.RefreshAccessTokenAsync`
(`Internal/OpenIdConnectUserTokenEndpoint.cs` at `atm-4.2.0`) builds a `RefreshTokenRequest` with
`ClientId = oidc.ClientId.ToString()`, **`ClientSecret = oidc.ClientSecret.ToString()`** and
`ClientCredentialStyle = options.Value.ClientCredentialStyle`; then, `if (parameters.Assertion != null)`
it sets `request.ClientAssertion = parameters.Assertion; request.ClientCredentialStyle =
ClientCredentialStyle.PostBody;` and only in the `else` branch asks `IClientAssertionService`. So the
parameter is preferred, as the request says, and the style is forced to `PostBody`, as the request
says. What the request did not read is that `ClientSecret` stays on the request. `oidc.ClientSecret`
comes from `OpenIdConnectConfigurationService.GetOpenIdConnectConfigurationAsync`
(`Internal/OpenIdConnectConfigurationService.cs`, line 59: `options.ClientSecret == null ? null :
ClientSecret.Parse(options.ClientSecret)`), that is, from the scheme's `OpenIdConnectOptions`.

The send is `Duende.IdentityModel`'s: `HttpClientTokenRequestExtensions.RequestRefreshTokenAsync`
clones the request and calls `ProtocolRequest.Prepare()` (`Client/Messages/ProtocolRequest.cs` at
`im-8.1.0`). Under `PostBody`, `Prepare` does `Parameters.AddRequired(client_id, ClientId)` and
`Parameters.AddOptional(client_secret, ClientSecret)`; then, if `ClientAssertion.Type` and `.Value`
are set, it adds `client_assertion_type` and `client_assertion`. `Parameters.AddOptional`
(`Client/Messages/Parameters.cs`) returns without adding when the value is null or empty. Under
`AuthorizationHeader` with a `ClientId`, an assertion makes `Prepare` throw
`InvalidOperationException("CredentialStyle.AuthorizationHeader and client assertions are not compatible")`.

**Executed.** With the library's registered endpoint type
(`Duende.AccessTokenManagement.OpenIdConnect.Internal.OpenIdConnectUserTokenEndpoint`, found the way
`ProfileTokenEndpoints.RegisteredEndpointType` finds it) built for `PostBody` as
`ProfileTokenEndpoints.Build` builds it:

- Scheme with `ClientSecret` set, `Assertion` given: the refresh succeeded and the stub received
  `client_assertion, client_assertion_type, client_id, client_secret, grant_type, refresh_token` (L1,
  L2). **Both credentials in one body.**
- Scheme with `ClientSecret = null`, `Assertion` given: `client_assertion, client_assertion_type,
  client_id, grant_type, refresh_token`; no `client_secret` (L3, L6).
- Endpoint built for `AuthorizationHeader`, `Assertion` given: no `Authorization` header, assertion
  in the body, `client_id` in the body (L4). The style on the endpoint is overridden by the assertion.
- Control, `AuthorizationHeader` and no assertion: `Basic` header, body `grant_type, refresh_token` (L5).

**Consequence for the request.** Item 2's sentence "`PrivateKeyJwt` maps to the library's `PostBody`
with a `ClientAssertion` on the request, which is what the library does for itself" describes the
mechanism correctly and is **necessary but not sufficient**. As the gateway stands,
`OidcOptionsSetup.Configure` sets `options.ClientSecret = secrets.Get(app.OAuth.ClientSecretRef)` for
every app, and the `clientSecretRef` paragraph of item 2 keeps that reference resolving under
`pingfederate` while promising that "the profile never sends the value". With the library as read and
run, that promise holds **only if** something nulls `options.ClientSecret` for a pingfederate issuer.
The one place that fixes the refresh, the library's revocation, the handler's code exchange and the
handler's pushed body alike is the profile's `Configure`, which runs after `OidcOptionsSetup`'s
assignment; `Validate` does not need the value (claim 1, B4). The request should say so: the style
maps to `PostBody` plus an assertion **and** an empty client secret on the scheme's options.

Two smaller points for the mapping in `src/Platform.Identity`:

- `ProfileTokenEndpoints.For(PrivateKeyJwt)` can return the existing `PostBody` endpoint; a third
  instance is not needed, because the assertion sets the style on the request (L4). The style that
  matters is the one on `SessionEnd`'s own `TokenRevocationRequest` (claim 3).
- `TokenRequestParameters.Assertion` is `init`-only on a record, and
  `AccessTokenSource.RefreshAsync` already builds a fresh `UserTokenRequestParameters` per call, so
  a fresh assertion (new `jti`, short `exp`) per refresh is the natural shape. The library's
  alternative seam, an `IClientAssertionService` keyed by `oidc.Scheme.ToClientName()`, is
  process-wide per client name and is asked only when the parameter is absent; the per-call
  parameter is the per-issuer seam ADR-0017 wants.

## Claim 3: the revocation

**Read.** `OpenIdConnectUserTokenEndpoint.RevokeRefreshTokenAsync` builds a `TokenRevocationRequest`
in the same shape as the refresh (`ClientSecret = oidc.ClientSecret.ToString()`, then the same
`Assertion` branch forcing `PostBody`) and calls `HttpClientTokenRevocationExtensions.RevokeTokenAsync`
(`Client/HttpClientTokenRevocationExtensions.cs` at `im-8.1.0`), which clones, adds `token` and
`token_type_hint`, and calls `Prepare()`, the method read under claim 2. `SessionEnd.RevokeAsync`
(`src/Platform.Identity/SessionEnd.cs`) bypasses the library's endpoint and builds that same
`TokenRevocationRequest` itself from `OpenIdConnectClientConfiguration`, with `ClientSecret =
client.ClientSecret?.ToString()` and `ClientCredentialStyle = ProfileTokenEndpoints.LibraryStyle(style)`,
and calls the same `RevokeTokenAsync`. `TokenRevocationRequest` adds only `Token` and `TokenTypeHint`
to `ProtocolRequest`, whose `ClientAssertion` property it inherits.

**Executed.**

- Library endpoint, `Assertion` given, scheme secret set: `client_assertion, client_assertion_type,
  client_id, client_secret, token, token_type_hint` (L7). Scheme secret null: the same without
  `client_secret` (L8).
- `SessionEnd`'s shape, `new TokenRevocationRequest { ClientAssertion = ..., ClientCredentialStyle =
  PostBody, ClientSecret = null, Token, TokenTypeHint }` through `HttpClient.RevokeTokenAsync`: the
  stub received `client_assertion, client_assertion_type, client_id, token, token_type_hint`, no
  `client_secret`, no header, and the call reported no error (L9).
- Same with `ClientSecret` set: both credentials posted (L10).
- `ClientCredentialStyle = AuthorizationHeader` with an assertion: `Prepare()` threw
  "CredentialStyle.AuthorizationHeader and client assertions are not compatible" (L11).

**Consequence for the request.** Item 6's "`SessionEnd` builds the request from the library's parts
and gains the assertion there" holds. Two constraints follow for workstream 3's mapping:
`ProfileTokenEndpoints.LibraryStyle(PrivateKeyJwt)` must answer `PostBody`, because `SessionEnd`
passes it into the request and `AuthorizationHeader` throws with an assertion (L11); and the request's
`ClientSecret` must be `null`, which it is when `options.ClientSecret` is null (the library's
configuration reads it from there, claim 2) or when `SessionEnd` leaves it off for this style.

## Claim 4: the `form_post` callback

**Read.** `OpenIdConnectHandler.HandleRemoteAuthenticateAsync` (lines 629-725). A `GET` builds the
message from `Request.Query` (lines 635-640). Otherwise, `if (HttpMethods.IsPost(Request.Method) &&
!string.IsNullOrEmpty(Request.ContentType) &&
Request.ContentType.StartsWith("application/x-www-form-urlencoded", OrdinalIgnoreCase) &&
Request.Body.CanRead)` the message is built from `await Request.ReadFormAsync(...)` (lines 656-667).
Any other request yields `authorizationResponse == null` and `HandleRequestResults.NoMessage`
(lines 669-677), or `SkipHandler()` when `SkipUnrecognizedRequests` is set. With a message,
`ReadPropertiesAndClearState` unprotects `state`; when that yields nothing, `if
(string.IsNullOrEmpty(authorizationResponse.State))` logs `NullOrEmptyAuthorizationResponseState` and
returns `HandleRequestResult.Fail(Resources.MessageStateIsNullOrEmpty)` (lines 693-706); a `state`
that does not unprotect fails with `MessageStateIsInvalid`; then `ValidateCorrelationId(properties)`
fails with "Correlation failed." (line 725). The method is not consulted in any of that: the `POST`
and `GET` arms join before the `state` check. Every `Fail` reaches
`RemoteAuthenticationHandler.HandleRequestAsync`, which raises `Events.RemoteFailure`
(`RemoteAuthenticationHandler.cs` lines 116-120), and the gateway's `OnRemoteFailure`
(`src/Platform.Identity/AuthenticationEvents.cs`) writes the constant page, 400, and notes
`auth.callback`.

`CallbackBlindCookieManager.GetRequestCookie` (`src/Platform.Identity/CallbackBlindCookieManager.cs`)
returns `null` when `context.Request.Path == callbackPath`, the same `PathString` comparison as
`RemoteAuthenticationHandler.ShouldHandleRequestAsync` (line 68-69: `Options.CallbackPath ==
Request.Path`). Neither looks at the method, so a `POST` to `/signin-oidc` leaves the session cookie
unread exactly as a `GET` does. Read, not executed: the harness host had no cookie scheme with that
manager.

**Executed.** Scheme `pkj-secret` used `ResponseMode = form_post`. The pushed body carried
`response_mode=form_post` (A4). The harness posted `code` and `state` as
`application/x-www-form-urlencoded` to the callback with the correlation and nonce cookies: the
handler read the form, redeemed the code, validated the ID token and signed in, answering 302 to `/`
with a session cookie (A6). Then:

- `POST` with `code` but no `state`, no cookies: 400 through `OnRemoteFailure` with the failure
  "OpenIdConnectAuthenticationHandler: message.State is null or empty." (A9). A `GET` without `state`
  gave the identical failure (C4).
- `POST` with an empty url-encoded body: the same `state` failure (A11).
- `POST` with a JSON body: "No message." (A12), the `authorizationResponse == null` arm.
- `POST` with a valid `state` but no cookies: "Correlation failed." (A10).

**Consequence for the request.** Item 3's reading of the handler holds, and so does its claim that a
`POST` callback with no valid state and correlation cookie "is refused by the handler exactly as a
`GET` is: 400, the constant failure page, `auth.callback`". Two things were **not** executed and the
request should keep saying they rest on reading: the browser sending `SameSite=None` cookies on the
cross-site `POST` (the harness is an `HttpClient`, which sends whatever it is told), and the request
gate, which is not in the harness host and today answers `POST /signin-oidc` 405
(`src/Platform.Security/RequestGate.cs` line 88-89, `Row.Callback`: `MethodIs(context,
HttpMethods.Get) ? next(context) : RejectMethodAsync(...)`). One small addition for item 3's prose:
a `POST` whose content type is not a form (JSON, multipart, none) is "No message", not a `state`
failure; both end at `auth.callback`.

## Claim 5: pushed authorization

**Read.** `OpenIdConnectOptions.PushedAuthorizationBehavior` defaults to `UseIfAvailable` (line 419).
`HandleChallengeAsyncInternal` reads `_configuration?.PushedAuthorizationRequestEndpoint` and
switches on the behaviour (lines 488-518): `UseIfAvailable` pushes when the endpoint is non-empty;
`Disable` throws `InvalidOperationException("Pushed authorization is required by the OpenId Connect
provider, but disabled by the OpenIdConnectOptions.PushedAuthorizationBehavior.")` when
`_configuration?.RequirePushedAuthorizationRequests == true` and otherwise does nothing; `Require`
throws `InvalidOperationException("Pushed authorization is required by the
OpenIdConnectOptions.PushedAuthorizationBehavior, but no pushed authorization endpoint is available.")`
when the endpoint is empty and otherwise pushes. `PushAuthorizationRequest` (lines 552-601) clones the
authorize message, raises `Events.PushAuthorization`, and `if (context.HandledClientAuthentication)`
adds nothing, else adds `client_secret = Options.ClientSecret` when it is non-empty (lines 561-573);
unless the event skipped or handled the push, it posts `FormUrlEncodedContent(parRequest.Parameters)`
to the endpoint over `Backchannel`, requires an `application/json` answer with `request_uri`
(`GetPushedAuthorizationRequestUri`, lines 604-623), then clears the authorize parameters and adds
`client_id` and `request_uri` (lines 599-601). `OpenIdConnectMessage.CreateAuthenticationRequestUrl`
(8.19.2, lines 172-199) then adds `x-client-SKU` and `x-client-ver` when `EnableTelemetryParameters`
is on; the handler sets it to `!Options.DisableTelemetry` on the challenge message (line 401).

`_configuration` is whatever the configuration manager returns, so the harness used a static
`OpenIdConnectConfiguration` per scheme in place of a discovery document; the switch reads the same
two properties either way.

**Executed.**

- `Require`, endpoint present, `OnPushAuthorization` adds the assertion and calls
  `HandleClientAuthentication()`, `Options.ClientSecret` set: the stub's `/par` received
  `client_assertion, client_assertion_type, client_id, code_challenge, code_challenge_method, nonce,
  redirect_uri, response_mode, response_type, scope, state` and **no `client_secret`** (A3, A4); the
  browser was sent to the authorize endpoint with `client_id, request_uri, x-client-SKU, x-client-ver`
  and nothing else (A2); the login then completed (A6).
- `Require`, discovery requires pushed requests, assertion added **without**
  `HandleClientAuthentication()`, `Options.ClientSecret = null`: pushed, no `client_secret` (B1, B2).
- `UseIfAvailable`, endpoint present, no event: pushed, body carries `client_secret` (D1).
- `UseIfAvailable`, no endpoint: not pushed; the authorize URL carries the full parameters and no
  `request_uri` (C1), and `response_mode` is absent for `query` (C2).
- `Require`, no endpoint: the challenge threw the first `InvalidOperationException` above (E1).
- `Disable`, discovery requires pushed requests: the challenge threw the second (F1).
- `Disable`, endpoint present but not required: not pushed (G1).

**Consequence for the request.** Item 4's four facts hold. Two precisions for its prose:

- "fails" is an unhandled `InvalidOperationException` thrown from `HandleChallengeAsync`. On the
  gateway that is the `/bff/login` request, so it surfaces through whatever handles exceptions
  there, not through `RemoteFailure`, the failure page or `auth.callback`. "Fails closed" is right;
  the profile ticket should know which path it fails on and what the client sees.
- The browser goes to the authorize endpoint with `client_id`, `request_uri` **and the handler's
  telemetry parameters** `x-client-SKU` and `x-client-ver` (A2), unless `DisableTelemetry` is set.
  Harmless, but "`client_id` and `request_uri` only" is not what is sent.

The telemetry note in item 4 is confirmed as far as the harness sees: the pushed body carries `state`,
`nonce`, `code_challenge` and, under the profile, `client_assertion`.

## Claim 6: packages

**Read.** The nuspecs in `~/.nuget/packages`: `Microsoft.AspNetCore.Authentication.OpenIdConnect`
10.0.12 depends on `Microsoft.IdentityModel.Protocols.OpenIdConnect` 8.19.2, which depends on
`Microsoft.IdentityModel.Protocols` 8.19.2 and `System.IdentityModel.Tokens.Jwt` 8.19.2, which depends
on `Microsoft.IdentityModel.JsonWebTokens` 8.19.2 and `Microsoft.IdentityModel.Tokens` 8.19.2. The
chain the request names is right. One precision about how it enters `Platform.Identity`:
`src/Platform.Identity/Platform.Identity.csproj` references only
`Duende.AccessTokenManagement.OpenIdConnect` and `Platform.Abstractions`; the handler package is a
direct reference of `src/Platform.Abstractions/Platform.Abstractions.csproj`, and that is where the
10.0.12 pin enters the closure (`Duende.AccessTokenManagement.OpenIdConnect` 4.2.0's own dependency
on the handler package is 10.0.4 for `net10.0`). `Directory.Packages.props` pins
`Microsoft.IdentityModel.JsonWebTokens` 8.19.2 with the comment "Same IdentityModel version the two
ASP.NET Core authentication packages depend on". `tests/Gateway.ArchitectureTests/ReferenceRules.cs`
names `Duende.AccessTokenManagement.OpenIdConnect` as the one package A-04 restricts.

**Executed.** In the trial worktree at `origin/main` (`ad87e3c`):

- `dotnet list src/Platform.Identity/Platform.Identity.csproj package --include-transitive` listed
  `Microsoft.IdentityModel.JsonWebTokens 8.19.2`, `System.IdentityModel.Tokens.Jwt 8.19.2`,
  `Microsoft.IdentityModel.Protocols.OpenIdConnect 8.19.2`,
  `Microsoft.AspNetCore.Authentication.OpenIdConnect 10.0.12` and `Duende.IdentityModel 8.1.0` as
  transitive packages of `Platform.Identity`.
- Adding `<PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" />` to
  `Platform.Identity.csproj` restored it as a top-level package at 8.19.2 with no new pin, built with
  0 errors, and `dotnet test --project tests/Gateway.ArchitectureTests` passed all 21 tests, A-04
  among them. (The edit lives only in the trial worktree.)
- `Duende.IdentityModel.Client.ClientAssertion` is the type the harness used on
  `UserTokenRequestParameters.Assertion` and on `TokenRevocationRequest.ClientAssertion`; in
  `Platform.Identity` it is reachable today because `CodeExchange.cs` and `SessionEnd.cs` already
  compile against `Duende.IdentityModel.Client`.

**Consequence for the request.** "Packages: none new" holds. The prose can name
`Platform.Abstractions` as the project whose reference brings the 10.0.12 handler into the closure.

## What could not be executed

- **A real PingFederate**, and so whether it accepts the assertion the gateway would sign (its `aud`,
  `alg`, `kid`, lifetime and `jti` rules). The stub recorded the assertion and did not validate it.
- **`fixtures/TestIdp`** was not usable for these claims: its `/token` and `/revoke` validate
  `client_secret_basic` and `client_secret_post` only and it has no `/par`, which is the gain the
  request lists for workstream 5. The harness used its own recording stub instead.
- **Discovery-served documents.** The pushed-authorization variants used a static
  `OpenIdConnectConfiguration` per scheme; the handler reads the same two properties from the
  discovery document, which the test identity provider will serve once it has the endpoint.
- **The browser's `SameSite` behaviour** on the cross-site `POST`, the request gate's handling of
  `POST /signin-oidc`, and `CallbackBlindCookieManager` on a `POST`: all read, none run.
- **A `PrivateKeyJwt` arm in `src/Platform.Identity`.** The harness exercised the library and the
  handler, not the gateway's mapping; the seams the mapping would use are named under each claim.

## The harness

`spikes/scratch-pkj/` in a scratch worktree at `origin/main` (`ad87e3c`), never committed:
`ScratchPkj.csproj` (`net10.0`, explicit `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12
and `Duende.AccessTokenManagement.OpenIdConnect` 4.2.0, under `spikes/Directory.Build.props` so the
solution's settings do not apply), `Stub.cs`, `Assertion.cs`, `Program.cs`, about 330 lines in all.

- **`Stub`** is a Kestrel app on `127.0.0.1` with `POST /token` (records the form; answers an access
  token, a refresh token and an RS256 ID token carrying the login's `nonce`), `POST /par` (records;
  answers `request_uri` and `expires_in` as `application/json`) and `POST /revoke` (records; 200).
  It mints the authorization code a real `/authorize` would, bound to the nonce from the URL or the
  pushed body. Its `Configuration(parEndpoint, requirePar)` returns a static
  `OpenIdConnectConfiguration` with its signing key.
- **The handler host** registers eight `AddOpenIdConnect` schemes, one per variant, each with its own
  `CallbackPath`, the stub's configuration, a proxy-free `Backchannel`, `OnRemoteFailure` answering
  400 with the failure message, and `GET /login/{scheme}` challenging inside a `try` that reports a
  thrown exception as 500 with its type and message.
- **The browser** is an `HttpClient` with redirects off and no cookie jar: it reads the 302's
  `Location` and `Set-Cookie`, asks the stub for a code, and posts (or gets) the callback with the
  cookies it was given.
- **The library** is driven through a `ServiceCollection` with two schemes (secret set, secret null)
  and `AddOpenIdConnectAccessTokenManagement`; the endpoint is built per style the way
  `ProfileTokenEndpoints.Build` does it, and `RefreshAccessTokenAsync` and `RevokeRefreshTokenAsync`
  are called with and without `Assertion`; `SessionEnd`'s shape is reproduced with a
  `TokenRevocationRequest` through `HttpClient.RevokeTokenAsync`.
- **Output** prints one `HELD` or `FAILED` line per check (36 in the final run, all held). Every
  recorded form is described by its key names and by presence flags; the assertion is summarised as
  `jwt(alg, iss, sub, aud, kid)`; no secret, code, token or cookie value is printed.
