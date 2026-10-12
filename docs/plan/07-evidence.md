# Evidence

The mechanism ran twice on .NET 10.0.12. The stand-in run passed 34 of 34 checks. The real-handler run held 32 of 32 expectations and confirmed nine findings, which changed the plan. On 11 October 2026 the hardened real-handler run was repeated with the shipped package and gave the same verdict on every check. The same day a third harness ran the token-management library and YARP on their shipped packages: 43 expectations held and none failed. No run has used a real provider. G0 items 2 and 3 do.

## The two runs

|  | Stand-in run | Real-handler run |
| --- | --- | --- |
| Run by | Me | An independent reviewer. I rebuilt it, re-ran it and got the same result. |
| OIDC handler | A stand-in derived from `RemoteAuthenticationHandler<T>`, because the sandbox could not reach NuGet | `OpenIdConnectHandler`, compiled from unmodified `release/10.0` source |
| Provider | A fake authorization server: codes and rotating refresh tokens | A hermetic OpenID Connect provider: discovery, signing keys, PKCE, signed ID tokens, nonce, end session |
| Also covers | Refresh under concurrency, 303 apps, the stock-provider control | Antiforgery, framework defaults, failure caching |
| Result | 34 of 34, in seven consecutive runs | 32 of 32 expectations held. Nine probes confirmed findings. |
| Check ids | `S-` | `R-` |

Common to both runs:

- **Runtime:** .NET SDK 10.0.112 and runtime 10.0.12 from the Ubuntu 24.04 archive, on x64 Linux.
- **Shape:** one process holding the provider, the gateway and a test client. The servers listen on separate loopback ports. The client keeps cookies per host and follows redirects by hand.
- **Requests:** each carries the SPA's `Host` header and `X-Forwarded-Proto: https`, as a gateway sees them behind a load balancer that terminates TLS.
- **Unmodified framework parts:** `AuthenticationMiddleware`, `CookieAuthenticationHandler`, data protection, forwarded headers. In the real-handler run, antiforgery too.

### How the real handler was built

The sandbox could not download packages, so the review built the handler from source. What ran is the framework's code, but not the framework's shipped binary.

- **Handler:** every file under `src/Security/Authentication/OpenIdConnect/src` in a clean checkout of `dotnet/aspnetcore` at `release/10.0`, commit `9aaf828`. Two small support files were added, for resource strings and a string helper. No handler file was changed.
- **IdentityModel core:** the 8.19.2 assemblies that ship inside the .NET SDK, unmodified.
- **IdentityModel protocols:** `Microsoft.IdentityModel.Protocols` and `Protocols.OpenIdConnect`, built from the 8.19.2 tag. They use internals of the core assemblies, so the build adds an access-check shim and compiles against reference copies with those internals made visible. The assemblies that run are the originals.
- **My check:** both checkouts are clean against their commits. I rebuilt the harness, re-ran it and got the same 32 and 9. I then replayed the whole build from empty folders, using only the files in the zip, and both harnesses gave the same verdict on every check.

## What no run has shown

- **A real identity provider.** Every provider so far is an in-process double. Nothing has run against Cognito.
- **Cognito's rules for refresh tokens.** The token-and-proxy run used a test provider with a grace window of its own. The five questions under G0 item 2 are what it could not answer.
- **YARP's per-route timeout.** The token-and-proxy run used the activity timeout, which limits silence. The route timeout needs the request-timeouts middleware and was not exercised.
- **A browser, a load balancer or TLS.** Cookie attributes were asserted as text. The test client enforces no cookie prefix, which is why N-25 and N-26 need a real browser.
- **Three of the plan's corrections.** Clearing the antiforgery cookie at logout, evicting a failed options build, and the gateway's check on cookie names are in the plan and in no harness.
- **Absolute timings.** The request times are a loopback micro-measurement. Across seven runs the host-scoped provider took 142 to 172 microseconds a request and the stock provider 375 to 527. The ratio is the finding.

## Stand-in run: 34 checks

All passed in each of seven runs. Details are from the last run.

| ID | Check | Detail |
| --- | --- | --- |
| S-A1 | App 1's host challenges with client 1 and its own redirect URI |  |
| S-A2 | App 2's host challenges with client 2 and its own redirect URI |  |
| S-A3 | An unknown host gets 404 on every path, including the callback path | 404, 404, 404 |
| S-A4 | A full login on app 1 ends on app 1's shell | Four hops |
| S-A5 | A full login on app 2, on the same callback path, ends on app 2's shell |  |
| S-A6 | Each host authenticates through its own default scheme and client | The middleware set the user from the default scheme |
| S-A7 | Exactly one remote handler is created per request | 1 per request |
| S-B1 | The session cookie is `__Host-` prefixed, Secure, HttpOnly, `SameSite=Lax`, `Path=/`, no Domain |  |
| S-B2 | After login the browser holds one cookie per host | The session cookie |
| S-B3 | The cookie ticket carries a session id and nothing else | Cookie length 326 |
| S-B4 | Tokens live in the server-side session, bound to app and client |  |
| S-B5 | App 2's scheme cannot decrypt app 1's cookie |  |
| S-B6 | App 1's cookie presented to app 2 is anonymous | 401 on the API, a fresh challenge on navigation |
| S-B7 | A validly encrypted app 2 cookie naming app 1's session key is anonymous | The store is bound to the app |
| S-B8 | On app 1's host, app 2's scheme names do not resolve |  |
| S-C1 | App 1's callback replayed on app 2 is rejected | State invalid for this scheme |
| S-C2 | A code issued to client 1, injected into app 2's login, is refused | Rejected at the token endpoint |
| S-C3 | A valid callback without the correlation cookie is rejected | Correlation failed |
| S-C4 | None of the rejected callbacks created a session |  |
| S-D1 | A third app is onboarded at runtime | 404 before, full login after, same process and server instance |
| S-D2 | Apps that did not change keep their cached options instance | Same object reference |
| S-D3 | A client secret is rotated at runtime | The stale secret fails, the new one is picked up, existing sessions survive |
| S-D4 | A login started before a reload completes after it | State and correlation survive |
| S-D5 | A client replaced mid-login fails closed | Through a guard in the stand-in. The real handler needs none: see R-C4. |
| S-D6 | Sessions issued under the replaced client stop authenticating |  |
| S-D7 | An app is removed at runtime | Its host returns 404, its sessions are purged, other apps are unaffected |
| S-D8 | A registry that claims an existing host twice is rejected whole | The last good snapshot stays live |
| S-E1 | Twenty-five concurrent calls on an expired token | One refresh grant, none refused, all succeed with the new token |
| S-E2 | Refresh rewrites the same server-side session | The cookie issued at login still identifies it |
| S-E3 | The token attached for each app was issued to that app's client |  |
| S-E4 | Logout deletes the server-side session | Replaying the old, still decryptable cookie is anonymous |
| S-F1 | Host-scoped provider with 303 apps | One handler per request, 142 to 172 microseconds, any app can log in |
| S-F2 | Control: stock provider with 303 apps | 303 handlers per request, 2.3 to 3.6 times the request time |
| S-F3 | Control: stock provider with a shared callback path | Only the first-registered app completes a login. That is the default-option result: see R-D7. |

## Real-handler run: 32 expectations

All held in the reviewer's run, in my re-run, and in three further runs with the plan's overrides switched on.

| ID | Expectation | Detail |
| --- | --- | --- |
| R-A1 | App 1's challenge carries its own client, redirect URI, code flow, PKCE, nonce, scope and `resource`, and no `response_mode` |  |
| R-A2 | App 2's challenge carries client 2 on the same callback path |  |
| R-A3 | A full login on app 1 ends on app 1's shell | Discovery, signing keys, PKCE, a signed ID token and nonce, all through the real handler |
| R-A4 | A full login on app 2, on the same callback path, ends on app 2's shell |  |
| R-A5 | Each host authenticates through its own scheme, with tokens issued to its own client | `resource` reached the provider |
| R-A6 | Exactly one `OpenIdConnectHandler` is created per request | 1 per request |
| R-A7 | Discovery and signing keys are fetched once for both apps | One shared metadata manager |
| R-A8 | With `SaveTokens` and a ticket store, the cookie holds only the session id | Access, refresh and ID tokens sit in the store. Cookie length 326. |
| R-A9 | After login the browser holds only the session cookie | The callback deleted the login-state cookies |
| R-A10 | App 1's cookie on app 2 is anonymous | App 2's scheme cannot decrypt it |
| R-B1 | App 1's callback replayed on app 2 is rejected | The state cannot be decrypted |
| R-B2 | A code issued to client 1, injected into app 2's login, is refused | `invalid_grant` at the token endpoint |
| R-B3 | A callback without the correlation cookie is rejected | Correlation failed |
| R-B4 | A callback without the nonce cookie is rejected | Refused by the protocol validator |
| R-B5 | None of the rejected callbacks created a session | 0 sessions |
| R-B6 | On app 1's host, app 2's scheme name does not resolve |  |
| R-C1 | A third app is onboarded at runtime | 404 before, full login after. App 1's cached options instance is untouched. |
| R-C2 | A secret is rotated at runtime | The stale secret fails closed, the new one is used after eviction, the existing session survives |
| R-C3 | A login started before a reload completes after it | State, correlation, nonce and the PKCE verifier survive eviction |
| R-C4 | A client replaced mid-login is refused, with no custom guard | The provider refuses the code. Old-client sessions stop. A fresh login works. |
| R-C5 | An app is removed at runtime | Its host and callback return 404, its sessions are purged, others are unaffected |
| R-C6 | Logout removes the session and goes through the provider's end-session endpoint | With `id_token_hint` and `state`, then back to `/` |
| R-C7 | `/signout-callback-oidc` without `state` answers 200 with an empty body | The reason Cognito's logout must land on `/` |
| R-D2 | With `RemoteSignOutPath` cleared, `/signout-oidc` is not claimed | 404. The session survives. |
| R-D4 | Login completes with `__Host-` login-state cookies on `Path=/` |  |
| R-D6 | With the session cookie hidden on the callback path, sign-in creates a new key | Two keys after the callback. The planted one keeps its old identity. |
| R-D9 | No validation runs for a dynamically named scheme | `Get` succeeds with no client id. Calling `Validate` throws. |
| R-E1 | Antiforgery works under the host-scoped provider | Token issued on the session endpoint, header token accepted, body unread, missing token refused |
| R-E2 | App 1's request token alone is refused on app 2 | It does not pair with app 2's cookie token |
| R-E3b | With the app id bound into the token, app 1's pair is refused on app 2 | 403 |
| R-E5 | The antiforgery cookie is emitted without `Secure` unless `SecurePolicy` is `Always` | Browsers discard a `__Host-` cookie without it |
| R-E6 | With body reading suppressed, a form-field token is refused and bodies are never read | A header token works either way |

## Findings, and what the plan does

Nine probes confirmed a finding. Three are the security defects named on the Plan tab: R-D1, R-D3 and R-D5.

| ID | Finding | What the plan does |
| --- | --- | --- |
| R-D1 | `GET /signout-oidc` signs the user out. The framework enables the path by default, with no token and no origin check. | Clears `RemoteSignOutPath`, and the gate answers 404. Test N-27. |
| R-D3 | Login CSRF. A callback carrying an attacker's code and state is accepted from a browser that holds planted login-state cookies. | Puts the `__Host-` prefix on those cookies, so a sibling cannot set them. The hermetic client enforces no prefix, so the probe still succeeds there. N-26 runs in a real browser. |
| R-D5 | Session fixation. Signing out in `/bff/login` is not enough: the callback renews a session key planted before it returns. | Hides the session cookie from the cookie handler on the callback path, as in R-D6. Test N-18. |
| R-D7 | On the stock provider, every app completes a login on a shared callback path once `SkipUnrecognizedRequests` is on. | Withdraws the claim that only the first app can log in. The case against the stock provider is its cost per request and its global scheme names. |
| R-D8 | The default `ResponseMode` is `form_post`. The cross-site POST arrives without the `SameSite=Lax` correlation cookie, and the login fails. | Sets `ResponseMode = query`, which the handler then leaves out of the request, as in R-A1. |
| R-D10 | An exception while building named options is cached with the entry. Every later read rethrows it until the name is evicted. | No I/O in options setup, and a name whose build threw is evicted before the next request. |
| R-E3 | Without app binding, app 1's antiforgery pair validates on app 2 for the same user. Only the host-only cookie keeps the apps apart. | Binds the app id into the token, as in R-E3b. |
| R-E4 | A request token from the user's previous session is accepted in the next one. The token is bound to the user and the cookie token, not to the session. | Clears the antiforgery cookie at logout, and N-06 is reworded to match. |
| R-E7 | With body reading on, a request without a header token has its body consumed, and a form-field token is accepted. | Keeps `SuppressReadingTokenFromFormBody` on. |

### With the overrides on

I copied the harness, made four overrides the defaults and ran it three times: 32 of 32 each time. The four are the cleared sign-out path, the `__Host-` login cookies, the hidden session cookie on the callback, and app binding in antiforgery.

R-D1 stopped reproducing. R-D3 still reports, because the test client enforces no cookie prefix. R-D10 and R-E4 still report, because evicting a failed options build and clearing the antiforgery cookie at logout are not in the harness. The other five probes set their own options to show the stock or unpatched behaviour.

### With the shipped package

This is the first item of Gate G0. On 11 October 2026 the hardened harness ran against `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12 from nuget.org, on a Mac with Apple silicon, .NET SDK 10.0.401 and runtime 10.0.12.

All 32 expectations held. Each of the 41 checks gave the same verdict as the source build. The detail lines differ only in the handler's assembly name, random cookie suffixes, timestamps and port numbers.

The package resolved all seven IdentityModel assemblies at 8.19.2, so the two protocol assemblies were shipped binaries as well. The output is kept in the repository as `spikes/g0/real-handler/RealOidcNuGet/output-nuget.txt`.

### Read from source afterwards

ASP.NET Core matches request cookie names without regard to case: `RequestCookieCollection` uses a case-insensitive comparer. A cookie named `__host-spa-session` would be read as the session cookie. The request gate therefore rejects case variants of platform cookie names itself. Neither run exercised this. N-25 does, in the gateway and in a browser.

## Token-and-proxy run: G0 items 4 and 5

On 11 October 2026 a third harness ran on a Mac with Apple silicon and .NET runtime 10.0.12, against the shipped packages: `Duende.AccessTokenManagement.OpenIdConnect` 4.2.0, built for .NET 10, and `Yarp.ReverseProxy` 2.3.0, built for .NET 8. 43 expectations held and none failed. 23 probes confirmed a finding, and one did not reproduce. Check ids start `T-`.

The harness is `spikes/g0/token-and-proxy/` in the repository, and `output-token-and-proxy.txt` beside it is this run. It reuses the hardened gateway of the real-handler run and adds a test provider with rotating refresh tokens, a grace window and revocation. It ran first on Linux, against source builds of both libraries at their release tags. Every check gave the same verdict there, apart from T-A0 and T-C0, which pass only on shipped packages, and T-C14. An independent reviewer went through the checks twice.

### The gate checks

| ID | Check | Result |
| --- | --- | --- |
| T-A0 | The token-management assemblies are the shipped 4.2.0 builds for .NET 10 | Held |
| T-A1 | The pinning test: the first request is on app 2, then a refresh on app 1 uses client 1 and app 1's refresh token | Held |
| T-A2 | With the sentinel, the library's options hook attaches to no app, and refresh uses the shared backchannel and metadata manager | Held. Discovery was fetched once. |
| T-A3 | With the sentinel, the options hook and an app's options build outside a request | Held |
| T-B1 | Without the sentinel, the options hook cannot be built outside a request | Confirmed. It asks for the default challenge scheme, and the host-scoped provider has none to give. |
| T-B2 | Without the sentinel and built inside a request, the hook binds to that request's app | Confirmed. App 2 got the library's DPoP and client-assertion wiring, and app 1 never did. |
| T-B3 | Without the sentinel and without scheme names, each app's refresh still uses its own client | Confirmed. The client is found through the default challenge scheme at call time. |
| T-C0 | The YARP assembly is the shipped 2.3.0 build for .NET 8, loaded on the .NET 10 runtime | Held |
| T-C1 | A GET is proxied: host and `/api/{name}` select the route, the prefix is removed, the query is kept | Held |
| T-C2 | A POST is checked for the antiforgery header in the proxy pipeline, and its body is forwarded whole | Held. 5,022 of 5,022 characters arrived. |

Item 4 passes on T-A0 to T-A3, and T-B1 to T-B3 show what the sentinel is for. Item 5 passes on T-C0 to T-C2.

### Findings that changed the plan

| ID | Finding | What the plan does |
| --- | --- | --- |
| T-A7 | With the sentinel, a call that leaves the scheme names off works while the token is fresh, then throws when a refresh is due | Task 3.8 names both schemes on every call |
| T-A8, T-A9, T-A15 | The library reports a refused refresh, a 503 and an unreachable provider the same way, as a failed result. Only the error text `invalid_grant` marks a refusal, and the library ends no session itself. | Task 3.8 treats only `invalid_grant` as a refusal. Any other failure leaves the session. |
| T-B6, T-C14 | The library's single-flight does not cover a request that read the session before another request's refresh finished. That request replays the rotated-out refresh token, and a provider with no grace window refuses it. Unprompted, in fifty parallel calls at a provider that answers at once, it showed in 3 to 18 rounds of 100 on the Linux source builds and in none of 100 on the Mac. | Cognito's 10-second grace window from task 1.8, the re-read in task 3.8, and the new wording of F-10 |
| T-B7, T-B8 | A grace window grants the late request. With no window, re-reading the session on a refusal finds the newer token and keeps the session. | The same |
| T-B15, T-B16 | A browser that disconnects mid-refresh strands the rotated token when the token call carries the request's cancellation token. Without that token the refresh completes and is stored. | Task 3.8 never passes the request's cancellation token |
| T-C20, T-C7 | YARP's header-removal transform acts on successful responses only by default, so an upstream's 500 delivers its `Set-Cookie`. With `ResponseCondition.Always` nothing gets through. | Task 4.2, test N-12 and the upstream contract |
| T-C22, T-C5, T-C3 | YARP's defaults replace `X-Forwarded-For`, `-Host`, `-Proto` and `-Prefix` and drop `Forwarded`. Every other forwarding header goes upstream as the browser sent it. Removing them by name works for the names listed, and only for those. | Task 4.2, test N-11 and the upstream contract, which now names the list and tells APIs to trust only the three headers the gateway sets |
| T-C21 | The token step works as a pipeline step and as a YARP request transform | The pipeline step stays, as workstream 4 says |

### Findings for the tasks they touch

These change no decision. Each is something the named task would otherwise meet on its own.

| ID | Finding | For |
| --- | --- | --- |
| T-B4, T-B5 | The handler's code exchange sends the client secret in the form body, and the library's refresh sends it in a Basic header. `ClientCredentialStyle.PostBody` makes them the same. | Task 3.8. G0 item 2 exercises both. |
| T-B9, T-C13 | Every refresh re-issues the session cookie and marks the response `no-store`, because the library saves new tokens by signing in again. On a proxied call that cookie is removed with the upstream's, which is harmless: the browser's cookie holds only the session key. | Tasks 3.8 and 4.2 |
| T-B10 | A new token's expiry is computed from the system clock, not the injected `TimeProvider`. A test clock moved past the token lifetime makes every call refresh. | Tests F-03 and F-10: shorten token lifetimes at the test provider instead of moving a clock |
| T-B11 | The library refuses a refresh token longer than 4,096 characters | G0 item 2 |
| T-B12 | Naming the resource on the token call costs a refresh grant after every login | Task 3.8 leaves it off |
| T-B13 | A refresh response with no ID token blanks the stored one, and logout then sends an empty `id_token_hint` | Task 3.9 and the PingFederate gate |
| T-B14 | When discovery lists no revocation endpoint the library's revoke call throws, and a logout that revokes first leaves the session in place | Task 3.9 removes the session whether or not revocation worked. G0 item 2. |
| T-B17 | A ticket store that honours the request's cancellation token drops the save when the browser has gone | The Phase 2 session store |
| T-B18 | A logout that arrives while a refresh's answer is on its way revokes the rotated-out token and not its successor | G0 item 2 and task 3.9 |
| T-C4 | Enumerating a YARP request's typed headers rewrites some of their values. The transform that removes headers reads them unvalidated. | Task 4.2 |
| T-C9, T-C10 | An unlisted API name matches no route, and the 404 `unknown_api` comes from a catch-all behind the proxy. YARP forwards an encoded slash or backslash, a `..;` segment and a doubled slash to the app's own upstream. None reached another app's upstream. | Task 4.1, and the request gate in task 4.3 for the escape paths. Test N-10. |
| T-C17, T-C18 | YARP's activity timeout limits silence, not the whole exchange. 504 covers silence before the upstream's headers, and headers followed by silence give 502. | Task 4.1 and the `upstream_timeout` row of the browser contract |
| T-A9, T-A15 | The contracts have no outcome for a provider that cannot be reached. `AccessTokenResult` and the error table each need one. The harness answered 503 `idp_unavailable` and kept the session. | Task I-6 |

The other expectations that held cover rotation, logout with revocation, a secret rotated and an app onboarded at runtime, route changes, redirects, timeouts and tracing. The output file lists them.

### The reserve design

The last section of the harness, T-D1 to T-D10, has the gateway own the locking and call the library only for the refresh and revoke requests. With no grace window it removed the late replay in 100 of 100 rounds, kept the rotated token when a browser disconnected, shared one failed request among fifty waiting calls, and revoked the right token at a logout that raced a refresh. One hazard remains: a refresh the gateway gives up on may still have been carried out, so a deadline is safe only with a grace window that covers the retry (T-D10).

The plan does not adopt it. Task 3.8 stays on the library's token manager. This design reopens if G0 item 2 shows that Cognito's grace window does not cover a replayed refresh token, or at the PingFederate gate.

## The mechanism, as run

These are the classes the stand-in run exercised, unchanged. `StandInOidcHandler` and `StandInOidcOptions` stand where `OpenIdConnectHandler` and `OpenIdConnectOptions` go. The real-handler run used the same three classes with the real types. Both harnesses are in the zip attached to the conversation.

### Scheme provider

```csharp
public sealed class RegistrySchemeProvider(IHttpContextAccessor accessor, Registry registry) : IAuthenticationSchemeProvider
{
    private static readonly Task<AuthenticationScheme?> None = Task.FromResult<AuthenticationScheme?>(null);
    private static readonly Task<IEnumerable<AuthenticationScheme>> Empty = Task.FromResult<IEnumerable<AuthenticationScheme>>([]);

    private AppEntry? App => accessor.HttpContext is { } ctx ? RequestApp.Resolve(ctx, registry) : null;

    private static AuthenticationScheme Cookie(AppEntry a) => new(SchemeNames.Cookie(a.AppId), null, typeof(CookieAuthenticationHandler));
    private static AuthenticationScheme Oidc(AppEntry a) => new(SchemeNames.Oidc(a.AppId), null, typeof(StandInOidcHandler));
    private static Task<AuthenticationScheme?> T(AuthenticationScheme? s) => s is null ? None : Task.FromResult<AuthenticationScheme?>(s);

    public Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultSignInSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultSignOutSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => T(App is { } a ? Oidc(a) : null);

    public Task<AuthenticationScheme?> GetSchemeAsync(string name)
    {
        if (App is not { } a) return None;
        if (string.Equals(name, SchemeNames.Cookie(a.AppId), StringComparison.Ordinal)) return T(Cookie(a));
        if (string.Equals(name, SchemeNames.Oidc(a.AppId), StringComparison.Ordinal)) return T(Oidc(a));
        return None; // another app's scheme name does not exist on this host
    }

    // AuthenticationMiddleware asks this on EVERY request and instantiates each handler returned.
    public Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
        => App is { } a ? Task.FromResult<IEnumerable<AuthenticationScheme>>([Oidc(a)]) : Empty;

    public Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync()
        => App is { } a ? Task.FromResult<IEnumerable<AuthenticationScheme>>([Cookie(a), Oidc(a)]) : Empty;

    public void AddScheme(AuthenticationScheme scheme) => throw new NotSupportedException("Schemes come from the registry only.");
    public void RemoveScheme(string name) => throw new NotSupportedException("Schemes come from the registry only.");
}
```

### Options from the registry

The OIDC setup class has the same shape and also sets `SignInScheme` and a shared backchannel.

```csharp
public sealed class CookieOptionsSetup(Registry registry, SessionStore store) : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(CookieAuthenticationOptions options) { }

    public void Configure(string? name, CookieAuthenticationOptions o)
    {
        if (!SchemeNames.TryParseCookie(name, out var appId) || !registry.Current.ByAppId.TryGetValue(appId, out var app)) return;
        o.Cookie.Name = "__Host-spa-session";            // host-only by prefix rule: Secure, Path=/, no Domain
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.Path = "/";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.SessionStore = new AppBoundTicketStore(app.AppId, app.ClientId, store);
        // TicketDataFormat is left null on purpose: the framework's post-configure derives the protector
        // purpose from the scheme name, so app A's cookie cannot be unprotected under app B's scheme.
    }
}
```

### Eviction after a swap

```csharp
public sealed class OptionsInvalidator
{
    public int Evictions;

    public OptionsInvalidator(Registry registry, SessionStore store,
        IOptionsMonitorCache<CookieAuthenticationOptions> cookies, IOptionsMonitorCache<StandInOidcOptions> oidc)
    {
        registry.Swapped += (old, next) =>
        {
            foreach (var appId in old.ByAppId.Keys.Union(next.ByAppId.Keys))
            {
                old.ByAppId.TryGetValue(appId, out var before);
                next.ByAppId.TryGetValue(appId, out var after);
                if (Equals(before, after)) continue;       // record equality: untouched apps keep their cached options
                cookies.TryRemove(SchemeNames.Cookie(appId));
                oidc.TryRemove(SchemeNames.Oidc(appId));
                Interlocked.Increment(ref Evictions);
                if (after is null) store.RemoveApp(appId); // app removed: drop its sessions too
            }
        };
    }
}
```

The spike's entries hold only strings, so record equality is enough there. A real entry holds immutable collections, which compare by reference, so the product compares fingerprints instead.

### Service registration

```csharp
s.AddAuthentication();   // authentication core, data protection, encoders, TimeProvider

// Handler services are registered once and belong to no scheme in particular.
s.AddTransient<CookieAuthenticationHandler>();
s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureCookieAuthenticationOptions>());
s.AddTransient<StandInOidcHandler>();
s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<StandInOidcOptions>, StandInPostConfigure>());

// Per-app options are built from the registry, by scheme name, the first time the framework asks.
s.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, CookieOptionsSetup>();
s.AddSingleton<IConfigureOptions<StandInOidcOptions>, OidcOptionsSetup>();
s.AddSingleton<OptionsInvalidator>();

if (mode == Mode.HostScoped)
    s.Replace(ServiceDescriptor.Singleton<IAuthenticationSchemeProvider, RegistrySchemeProvider>());
```

### The overrides

From the real-handler harness, with its test switches removed. These lines are what the corrections on the Plan tab come down to.

```csharp
// OpenIdConnectOptions, per app
o.ResponseType = OpenIdConnectResponseType.Code;
o.ResponseMode = "query";                       // the default is form_post
o.UsePkce = true;
o.SaveTokens = true;
o.MapInboundClaims = false;
o.Resource = app.Resource;
o.CallbackPath = "/signin-oidc";                // the same path for every app
o.SignInScheme = SchemeNames.Cookie(app.AppId);
o.Backchannel = shared.Backchannel;             // one HttpClient and one metadata manager per issuer
o.ConfigurationManager = shared.Metadata;
o.CorrelationCookie.SameSite = SameSiteMode.Lax;
o.NonceCookie.SameSite = SameSiteMode.Lax;
o.CorrelationCookie.Name = "__Host-spa-corr.";  o.CorrelationCookie.Path = "/";
o.NonceCookie.Name = "__Host-spa-nonce.";       o.NonceCookie.Path = "/";
o.RemoteSignOutPath = default;                  // the default is /signout-oidc

// CookieAuthenticationOptions, per app
o.CookieManager = new CallbackBlindCookieManager(new ChunkingCookieManager(), "/signin-oidc");

// On the callback the cookie handler never sees an incoming session cookie,
// so sign-in stores a new session instead of renewing a planted one.
public sealed class CallbackBlindCookieManager(ICookieManager inner, PathString callbackPath) : ICookieManager
{
    public string? GetRequestCookie(HttpContext context, string key)
        => context.Request.Path == callbackPath ? null : inner.GetRequestCookie(context, key);
    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
        => inner.AppendResponseCookie(context, key, value, options);
    public void DeleteCookie(HttpContext context, string key, CookieOptions options)
        => inner.DeleteCookie(context, key, options);
}
```
