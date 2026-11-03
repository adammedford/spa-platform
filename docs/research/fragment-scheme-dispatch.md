# Scheme and callback dispatch for N clients on one host

- **Ticket:** #766, "Scheme and callback dispatch for N clients on one host: what the shipped OpenID
  Connect handler and RegistrySchemeProvider allow" (`wayfinder:research`), on the map #765.
- **Date:** 2026-11-03. **Branch:** `research/fragment-scheme-dispatch`, pushed and never merged, as
  the map rules.
- **Method:** primary sources only: the ASP.NET Core source at tag `v10.0.0` (commit `7387de9`),
  diffed against tag `v10.0.12` (commit `cb21a42`), the version `Directory.Packages.props` pins; the
  Duende.AccessTokenManagement source at tag `atm-4.2.0` (commit `f647f49`); this repository's
  `src/Platform.Identity`, `src/Platform.Security/RequestGate.cs`, `src/Platform.Hosting/Assets.cs`,
  `src/Platform.Registry/EntryFingerprint.cs`, `spikes/g0/`, `docs/plan/07-evidence.md`, ADR-0001,
  ADR-0017 and `contracts/http-contract.md`. Every source was read on 2026-11-03. Section 8 lists
  what I ran and what I only read. No container engine is on this machine; the .NET 10 SDK is
  (`dotnet --version`: 10.0.401). Nothing was built or run beyond that: the prototype is #771, and
  the one framework behaviour this question turns on was already run by the G0 harness (R-D7,
  S-F3), so I cite those runs instead of repeating them. Line numbers below are the `v10.0.0`
  files'; of the twelve framework files compared, only `CookieAuthenticationHandler.cs` differs at
  `v10.0.12`, in a URL helper that none of this touches.

## Answer in brief

1. **N schemes can share `/signin-oidc`, but only with `SkipUnrecognizedRequests = true`, and the
   handler is picked by state unprotection in the provider's order, not by path.**
   `ShouldHandleRequestAsync` is an exact path comparison, so every scheme with the same
   `CallbackPath` claims the request; the middleware calls them in the order
   `GetRequestHandlerSchemesAsync` returns, and the first whose `StateDataFormat` unprotects the
   `state` wins. The state's protector purpose is the scheme name, so a sibling's state does not
   unprotect. With the option off (the default), the first handler fails the callback and writes the
   failure page: that is S-F3, "only the first-registered app completes a login". With it on, the
   handler returns "skip" and the next one is tried: that is R-D7, every app logs in. The costs: N+1
   handlers built on the callback (the provider can confine that to the callback path), and a
   callback no scheme recognises raises no `RemoteFailure` at all but falls through the middleware
   to the asset route's `path.reserved` 404, unless the shell's scheme comes last with the option
   off. **A per-fragment `CallbackPath`, `/signin-oidc/{fragmentId}`, needs none of this**: exact
   path dispatch, one handler per request, and the failure page as today. Its cost is one more
   redirect URI, `https://{host}/signin-oidc/{fragmentId}`, which the fragment's client registers;
   the handler builds it from the request host and `CallbackPath` and sends the same value at the
   code exchange. The request gate, the shell gate and the asset route already match
   `/signin-oidc` by leading segment, so the sub-path is already a GET-only callback and already a
   reserved path. **Recommendation: per-fragment paths**; shared-path-with-skip is the control the
   prototype can run beside it.
2. **The login-state cookies already keep N in-flight logins apart; the scheme name is in the
   protector purposes, not in the cookie names.** The purposes are `(OpenIdConnectHandler, name,
   "v1")` for state, `(OpenIdConnectHandler, System.String, name, "v1")` for the nonce and
   `(CookieAuthenticationMiddleware, name, "v2")` for the session ticket, so a new name
   `oidc:{appId}:{fragmentId}` gets its own purposes with no code. The correlation cookie is
   `{prefix}{32 random bytes}` with the constant value `N`, and the id travels in the state under
   `.xsrf`; the nonce cookie is `{prefix}{Protect(nonce)}`, the suffix protected under the scheme's
   purpose, and the callback deletes only the one whose suffix unprotects to the ID token's nonce
   and passes the rest over. The prefixes (`__Host-spa-corr.` and `__Host-spa-nonce.` here;
   `.AspNetCore.Correlation.` and `.AspNetCore.OpenIdConnect.Nonce.` in the framework) are the same
   for every scheme, so nothing in the cookie table or the gate's name check changes.
3. **N OIDC schemes may name one `cookie:{appId}` as `SignInScheme` (the framework calls it "shared
   with multiple providers"), but the stock sign-in cannot add a token set to an existing record.**
   The remote handler ends a callback with `SignInAsync(SignInScheme, principal, properties)`, the
   properties carrying `.AuthScheme`, `.issued`, `.expires` and the flat token items
   `.Token.access_token`, `.Token.id_token`, `.Token.refresh_token`, `.Token.token_type`,
   `.Token.expires_at` and `.TokenNames`. The cookie handler then either stores a new ticket under a
   new key (what the callback-blind cookie manager forces today) or renews the whole ticket under
   the key it read (which `AppBoundTicketStore` refuses). Neither merges. `StoreTokens` clears every
   named token before writing, so the fragment's tokens must live under their own names; Duende's
   `UseChallengeSchemeScopedTokens` convention, `{name}||{challengeScheme}`, is a model to borrow
   (the gateway writes the ticket itself and does not use Duende's store). The seam is
   `OnTicketReceived`: read the shell's session, merge the fragment's token set into its ticket
   under the session gate, refusing a record whose subject is not the ID token's, `HandleResponse()`
   and redirect; the cookie is never reissued. That merge is the prototype's main problem.
4. **The scheme provider must answer `GetSchemeAsync` for every fragment name of the pinned entry,
   keep the shell's pair as the five defaults, and return as request handlers only the schemes
   whose callback path is the request's.** Challenge, sign-in and sign-out resolve through
   `GetSchemeAsync(name)`, never through the request-handler list, so narrowing that list is free
   and keeps ADR-0001's one handler per request. Today's setups and cache treat everything after
   `oidc:` as an app id, so a fragment name is left unconfigured and refused by validation, cached
   under no fingerprint, and never evicted. `OptionsInvalidator` must evict every
   `oidc:{appId}:{fragmentId}` of the old and new entry when the entry's fingerprint moves (a nested
   `fragments[]` edit moves it, since the fingerprint hashes the whole entry), and its "new client"
   purge, which compares only the shell's client id, needs a per-fragment rule. `SchemeNames` is in
   `Platform.Abstractions`, so the name form itself is a change request.
5. **The logout callbacks are first-match with no skip, so fragment schemes should claim none.**
   The handler checks `RemoteSignOutPath` and `SignedOutCallbackPath` before the login callback, by
   exact path; a signed-out callback whose state does not unprotect is still answered, with an
   empty response. Under one logout only the shell's scheme ever signs out at the provider, so
   fragments set `SignedOutCallbackPath = PathString.Empty` as every scheme already sets
   `RemoteSignOutPath`, and register no logout URI. `POST /bff/logout` revokes each token set with
   its own client (the Duende configuration service reads the named options by scheme,
   `oidcOptionsMonitor.Get(scheme)`, so `oidc:{appId}:{fragmentId}` works once its options exist), removes the record, and makes the
   shell's one provider logout as today.
6. **What the prototype (#771) must settle**: the merge sign-in through `OnTicketReceived` with the
   callback-blind manager scoped to the per-fragment paths; the provider's per-path request-handler
   set and the handler count per request; a tampered fragment callback ending on the failure page;
   two in-flight logins in one browser completing independently; and, as the control, the shared
   path with skip, including what an unrecognised callback ends on.

## 1. Callback dispatch: how a handler is picked

### 1.1 The framework's loop

`AuthenticationMiddleware.Invoke` asks the scheme provider for the request-handler schemes, builds
a handler for each through `IAuthenticationHandlerProvider`, and calls `HandleRequestAsync` on each
in that order; the first to return `true` ends the pipeline ([AuthenticationMiddleware.cs][mw]
lines 48–56). `AuthenticationHandlerProvider.GetHandlerAsync` resolves the scheme by name, activates
the handler type and calls `InitializeAsync`, which reads the options as
`OptionsMonitor.Get(Scheme.Name)` ([AuthenticationHandlerProvider.cs][hp] lines 30–50;
[AuthenticationHandler.cs][ah] lines 147–160). One handler instance per scheme per request, cached
in the provider's per-request map.

`RemoteAuthenticationHandler.ShouldHandleRequestAsync` is `Options.CallbackPath == Request.Path`
([RemoteAuthenticationHandler.cs][rah] lines 68–69): `PathString` equality, which ignores case, and
exact, not by segment. `OpenIdConnectHandler.HandleRequestAsync` first checks `RemoteSignOutPath`
and `SignedOutCallbackPath` the same way, then falls to the base ([OpenIdConnectHandler.cs][oh]
lines 89–100).

### 1.2 Inside `HandleRemoteAuthenticateAsync`: the state decides

On a GET the handler reads the authorization response from the query; on a form POST from the
body; anything else is "no message" ([oh] lines 635–673). It then calls
`ReadPropertiesAndClearState`, which is `Options.StateDataFormat.Unprotect(message.State)` ([oh]
lines 948–961). `StateDataFormat` is set by `OpenIdConnectPostConfigureOptions` as a
`PropertiesDataFormat` over `CreateProtector(typeof(OpenIdConnectHandler).FullName, name, "v1")`,
where `name` is the scheme name ([OpenIdConnectPostConfigureOptions.cs][pc] lines 48–53). A state
protected by scheme A does not unprotect under scheme B's protector, so `properties` is null for
every scheme but the one that issued the challenge.

What happens next is the whole question ([oh] lines 701–716):

```csharp
if (properties == null)
{
    Logger.UnableToReadAuthorizationResponseState();
    if (Options.SkipUnrecognizedRequests)
    {
        // Not for us?
        return HandleRequestResult.SkipHandler();
    }

    // if state exists and we failed to 'unprotect' this is not a message we should process.
    return HandleRequestResult.Fail(Resources.MessageStateIsInvalid);
}
```

- **`Fail`**: `RemoteAuthenticationHandler.HandleRequestAsync` raises `RemoteFailure` ([rah] lines
  113–142). This repository's `OnRemoteFailure` writes the static 400 page, notes `auth.callback` and
  handles the response (`AuthenticationEvents.Apply`), so `HandleRequestAsync` returns `true` and no
  later scheme is tried. On a shared path with the default option, the first scheme in the list
  therefore ends every callback that is not its own. That is the stand-in spike's control S-F3,
  "only the first-registered app completes a login" (`docs/plan/07-evidence.md`).
- **`SkipHandler`**: `HandleRequestAsync` returns `false` ([rah] lines 96–99) and the middleware
  tries the next scheme. The real-handler probe D7 ran the stock provider with five apps on one
  `/signin-oidc` and `SkipUnrecognizedRequests` on: every app completed its login
  (`spikes/g0/real-handler/RealOidcHardened/Program.cs` lines 350–372; the recorded line is in
  `RealOidcNuGet/output-nuget.txt` line 64 and `output-hardened.txt` line 63). R-D7 in the evidence
  table records it as "a default-option result, not a framework limit".

So on a shared path the dispatch is: every handler says yes to the path, and the state's protector
purpose, which is the scheme name, picks the one that proceeds. `SkipUnrecognizedRequests` is what
turns "the first handler refuses" into "the next handler is asked". The option's own remark says it
exists for "requests to the CallbackPath [that] may also be for other components" and that "a
dedicated endpoint may provide better error handling" ([OpenIdConnectOptions.cs][oo] lines
297–303).

### 1.3 What the shared path costs

1. **Handlers per request.** On the callback the middleware builds one handler per scheme in the
   list, N+1 for a shell with N fragments. Today's `RegistrySchemeProvider` returns `[oidc]` on
   every request, so if it returned all N+1 on every request the per-request cost ADR-0001 measured
   (303 handlers at 303 apps on the stock provider) comes back in proportion to N. The provider holds
   the `HttpContext`, so it can return the request-handler set only when the path is a callback
   (section 4), which confines the N+1 to the callback itself.
2. **The unrecognised callback.** When no scheme unprotects the state (tampered, replayed onto the
   wrong host, or stale after a client change), every handler skips and the middleware runs its
   default authenticate and `_next` ([mw] lines 58–76). On this pipeline `/signin-oidc` is in the
   shell gate's ungated list (`ShellGate.Ungated`), so the request reaches the asset route, which
   answers `path.reserved` with 404 and `Cache-Control: private, no-store`
   (`src/Platform.Hosting/Assets.cs` lines 85–90). No `RemoteFailure`, no failure page, no
   `auth.callback` rule id, and `ValidateCorrelationId` never ran, so the correlation cookie is not
   deleted (it expires on its own). The fix is an order: fragments first with the option on, the
   shell last with it off, so the shell's handler is the one that fails and writes the page. That
   order is a coupling the spec would have to state and a test would have to hold.
3. **Other skips.** With the option on, a GET whose query carries `id_token` or `access_token`, and
   any request that is neither a GET nor a form POST, also return `SkipHandler` instead of a failure
   ([oh] lines 643–651, 667–673), so those too fall through under the same order rule.

### 1.4 The per-fragment path

With `CallbackPath = /signin-oidc/{fragmentId}` on the fragment's scheme, `ShouldHandleRequestAsync`
is true for exactly one scheme, `SkipUnrecognizedRequests` stays off, and a bad state ends in `Fail`
and the failure page as today.

- **The redirect URI.** The challenge sends `RedirectUri = BuildRedirectUri(Options.CallbackPath)`,
  which is `Request.Scheme + "://" + Request.Host + OriginalPathBase + CallbackPath` ([oh] line 403;
  [ah] lines 196–197), and stores the same value in the state under
  `OpenIdConnectDefaults.RedirectUriForCodePropertiesKey` for the code exchange ([oh] lines 478,
  1265). So the value a fragment's client registers at the provider is
  `https://{host}/signin-oidc/{fragmentId}`, exactly. The handler compares the path ignoring case;
  the provider compares the registered URI as a string, so the fragment id's spelling in the
  registry is the one that must be registered.
- **Already a callback and already reserved.** `RequestGate.Classify` returns `Row.Callback` for
  `path.StartsWithSegments("/signin-oidc")` (`src/Platform.Security/RequestGate.cs` lines 152–154),
  so `/signin-oidc/{fragmentId}` is held to GET and skips the fetch-site check as the shell's
  callback does. `Assets.IsReserved` and `ShellGate.Gated` use the same segment comparison
  (`Assets.cs` lines 213–219; `ShellGate.cs` lines 56–58), so no asset is ever served there and the
  shell gate lets it through. `contracts/http-contract.md` names `/signin-oidc` in the sentence on paths an SPA cannot
  serve under; its code already means the prefix, and the spec may say so in a line.
- **`CallbackBlindCookieManager`** compares `context.Request.Path == callbackPath` exactly, with the
  shell's one path. On a per-fragment path the session cookie is therefore visible to the cookie
  handler, which section 3 shows is what the merge needs, and which the manager's scope must be
  decided against deliberately rather than by default.
- **What it needs from the fragment id**: a path-safe spelling. `registry.schema.json` would give
  `fragmentId` a pattern, as it gives `appId` one.

**Verdict.** Per-fragment paths keep every property this pipeline relies on (exact dispatch, one
handler per request, the failure page, no ordering rule) at the price of one redirect URI per
client, and the client's owner registers it anyway: ownership was the reason for a separate client.
The shared path works, R-D7 proved it, but degrades each of those properties a little. The
prototype should build the per-fragment path and keep the shared path as its control.

## 2. Login-state cookies and protector purposes

### 2.1 Purposes derive from the scheme name

- State: `CreateProtector(typeof(OpenIdConnectHandler).FullName, name, "v1")` ([pc] lines 48–53).
- Nonce (`StringDataFormat`): `CreateProtector(typeof(OpenIdConnectHandler).FullName,
  typeof(string).FullName, name, "v1")` ([pc] lines 55–64).
- Session ticket: `CreateProtector("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", name, "v2")`
  ([PostConfigureCookieAuthenticationOptions.cs][pcc] lines 40–44).

This repository registers both post-configurations unbound to a name
(`PlatformIdentityExtensions.AddPlatformIdentity`), so they run for every named options instance.
A scheme named `oidc:{appId}:{fragmentId}` gets its own state and nonce purposes, and the shell's
`cookie:{appId}` keeps the one ticket purpose for the one cookie: what ADR-0001 says, confirmed at
the source. R-B1 ("app 1's callback replayed on app 2 is rejected: the state cannot be decrypted")
is this property at work.

### 2.2 The correlation cookie

`GenerateCorrelationId` draws 32 random bytes, base64url-encodes them, stores the id in
`properties.Items[".xsrf"]` (so it rides in the protected state) and appends a cookie named
`Options.CorrelationCookie.Name + correlationId` with the constant value `N` ([rah] lines
226–241). `ValidateCorrelationId` reads `.xsrf` back from the unprotected state, looks up that one
cookie, deletes it and checks the value ([rah] lines 248–280). The name prefix is the framework's
`.AspNetCore.Correlation.` by default ([RemoteAuthenticationOptions.cs][rao] lines 15, 24–26) and
`__Host-spa-corr.` for every scheme here (`OidcOptionsSetup`). The scheme name is in neither.
What keeps N in-flight logins apart is the random id, and what binds a callback to its own id is
the state, which only unprotects under the issuing scheme's purpose. So a shell login and a
fragment login in flight together hold two correlation cookies; each callback deletes its own.

### 2.3 The nonce cookie

`WriteNonceCookie` appends a cookie named `Options.NonceCookie.Name +
Options.StringDataFormat.Protect(nonce)` with the constant value `N` ([oh] lines 1154–1166).
`ReadNonceCookie` walks every request cookie whose name starts with the prefix, unprotects the
suffix under this scheme's `StringDataFormat`, and deletes and returns only the one whose
unprotected value equals the ID token's nonce; a suffix that does not unprotect, or unprotects to
another value, is passed over, with any exception caught and logged ([oh] lines 1173–1200). The
prefix is `.AspNetCore.OpenIdConnect.Nonce.` by default ([oo] lines 79–85) and `__Host-spa-nonce.`
for every scheme here. A sibling scheme's nonce cookie is therefore invisible to a callback, not
fatal to it, and each callback removes only its own.

### 2.4 What this means for the contract

The cookie table in `contracts/http-contract.md` (`__Host-spa-corr.*`, `__Host-spa-nonce.*`,
"login state, deleted by the callback") stays true for N schemes, and the request gate's
cookie-name imitation check sees the same prefixes. One sizing note, not a blocker: each login in
flight adds two cookies on the host for up to `RemoteAuthenticationTimeout`, 15 minutes by default
and the correlation cookie's `Expires` ([rao] lines 117, 175), so N parallel lazy logins add 2N
cookies for those minutes. The design's lazy login starts
one at a time from a 401, so N in flight at once is the exception.

## 3. Sign-in to one cookie scheme and one record

### 3.1 What the framework allows

`RemoteAuthenticationHandler.HandleRequestAsync` ends a successful callback with
`Context.SignInAsync(SignInScheme, principal, properties)` after setting
`properties.Items[".AuthScheme"] = Scheme.Name` "so we can cross-check later" ([rah] lines
153–172). `AuthenticationService.SignInAsync` resolves the named scheme's handler and requires it
to be an `IAuthenticationSignInHandler` ([AuthenticationService.cs][as] lines 155–188); the cookie
handler is one. `RemoteAuthenticationOptions.Validate(scheme)` forbids only `SignInScheme ==
self` ([rao] lines 38–45). The comment at [rah] line 202, "The SignInScheme may be shared with
multiple providers, make sure this provider issued the identity", says N remote schemes on one
cookie scheme is the designed case, and `.AuthScheme` is how `AuthenticateAsync(oidc:…)` tells
whose identity the cookie holds ([rah] lines 192–216). This gateway authenticates the cookie scheme
directly everywhere (`ShellGate`, `Bff`, `AccessTokenSource`) and never `AuthenticateAsync` on an
OIDC scheme, so the single `.AuthScheme` value in a merged ticket costs nothing; Duende's session
store also authenticates the sign-in scheme, not the challenge scheme
([AuthenticationSessionUserAccessTokenStore.cs][dstore] lines 36–45).

### 3.2 What the cookie sign-in does, and why it cannot merge

`CookieAuthenticationHandler.HandleSignInAsync` reads the request cookie (`EnsureCookieTicket`),
builds a new `AuthenticationTicket` from the principal and properties it was given, and with a
`SessionStore` set either renews under the key it read or stores under a new key
([CookieAuthenticationHandler.cs][ch] lines 291–347):

```csharp
if (_sessionKey != null)
{
    await Options.SessionStore.RenewAsync(_sessionKey, ticket, Context, Context.RequestAborted);
}
else
{
    _sessionKey = await Options.SessionStore.StoreAsync(ticket, Context, Context.RequestAborted);
}
```

Both paths replace the whole ticket; neither adds to one. In this repository:

- `CallbackBlindCookieManager` returns no cookie on the callback path, so `_sessionKey` is null and
  every callback sign-in is a `StoreAsync`: a new record under a new key and a new cookie (R-D6, the
  session-fixation fix). A fragment's login finishing this way would open a second record and
  orphan the shell's, with its tokens.
- `AppBoundTicketStore.RenewAsync` throws by design (#141 Q11), because a renew writes the ticket
  this request started with over whatever a concurrent refresh stored. A fragment callback on a
  path the blind manager does not cover would reach exactly that throw.

So the fragment's token set cannot arrive through the stock sign-in. The seam the handler offers is
`TicketReceived`, raised after the tokens are saved into the properties and before `SignInAsync`,
with `HandleResponse()` making `HandleRequestAsync` return `true` without signing in ([rah] lines
146–170, "SignInHandled"). In that event the gateway can: read the shell's session key from the
request cookie (visible on a per-fragment path, or read through the real cookie manager), enter the
session gate (`AppSessions.EnterAsync`), merge the fragment's token set into the record's ticket
under fragment-keyed names, make the versioned write, and redirect to `context.ReturnUri` itself.
No cookie is issued and the session key stays, as a refresh keeps it today (S-E2).

Two guards belong to that merge. **The merge binds on identity, not on the key alone.** Reading the
session cookie on a callback is the read the blind manager exists to prevent (R-D5, R-D6: a cookie
planted before the login would receive the new identity), and the merge re-opens it on the fragment
path on purpose. So the merge refuses a record whose `Subject` is not the fragment ID token's `sub`
(and whose `AppId` is not the pinned app's): a planted cookie then receives nothing, and in the honest
case, one issuer and one browser, the subjects match. **A fragment callback that finds no shell
session** is the case the spec decides (refuse, or fall back to a full sign-in); the lazy-login flow
starts from a 401 on a fragment route, which presupposes a session.

### 3.3 What `AuthenticationProperties` carry

After a successful callback with `SaveTokens` on, the properties the handler passes hold ([oh]
lines 1110–1146; [TokenExtensions.cs][te] lines 13–51; `AuthenticationProperties.cs` lines
14–17):

| Item | Written by |
| --- | --- |
| `.Token.access_token`, `.Token.id_token`, `.Token.refresh_token`, `.Token.token_type`, `.Token.expires_at` | `SaveTokens` → `StoreTokens`; `expires_at` is the handler's clock plus `expires_in`, in round-trip format |
| `.TokenNames` = `access_token;id_token;refresh_token;token_type;expires_at` | `StoreTokens` |
| `.AuthScheme` | `RemoteAuthenticationHandler.HandleRequestAsync`, the OIDC scheme's name |
| `.issued`, `.expires` | the cookie handler at sign-in, from `ExpireTimeSpan` (the entry's lifetime) |
| `.redirect` | the challenge's `RedirectUri`; cleared before sign-in and moved to `TicketReceivedContext.ReturnUri` |
| `.xsrf` | the correlation id; removed by `ValidateCorrelationId` |
| the PKCE verifier and the redirect URI the code was issued to | the challenge; used at the code exchange |
| `session_state`, `check_session_iframe` | only when the provider sends them |

Two facts matter for N token sets. First, the names are flat: `StoreTokens` removes every name in
`.TokenNames` before writing the new set ([te] lines 21–51), so a second `SaveTokens` into the same
properties wipes the first. The gateway's own refresh write-back (`AccessTokenSource.StoreAsync`)
reads `GetTokens()`, replaces four names and calls `StoreTokens` again, so it too assumes one set.
Second, the library the gateway uses for the refresh call has a convention for exactly this: with
`UserTokenManagementOptions.UseChallengeSchemeScopedTokens` on, every token name is suffixed
`{name}||{challengeScheme}`, for example `.Token.access_token||oidc:app:frag`, and a read falls back
to the unsuffixed name ([StoreTokensInAuthenticationProperties.cs][dprops] lines 41–45, 150–165,
243–262; [UserTokenManagementOptions.cs][dopts] lines 21–25). The gateway writes and reads the
ticket with the framework's `StoreTokens` and `GetTokens`, not through Duende's store, so this is a
naming convention to borrow, not a switch to flip; it keeps the shell's set unsuffixed and
backward-compatible.

Where the sets live: `SessionRecord` is frozen with one `ClientId`, and `AppSessions` owns a record
by `AppId` and `ClientId` (`src/Platform.Abstractions/ISessionStore.cs` lines 15–22;
`AppSessions.cs` lines 216–217). N token sets therefore live inside the `Ticket` bytes, keyed by
fragment or client, with the record's `ClientId` still the shell's; or the record shape changes by
change request. The session gate and the refresh lease are per session key, so N token sets in one
record refresh one at a time; whether that is acceptable or the gate's key becomes (session,
client) is a spec question. The map's "session record growth" line already holds the size question.

## 4. The scheme provider, the options caches and the invalidator

### 4.1 What the provider must return

`RegistrySchemeProvider` today builds one `SchemePair` per pinned app: the five defaults are the
shell's (`cookie:{appId}` for authenticate, sign-in, sign-out and forbid; `oidc:{appId}` for
challenge), `GetSchemeAsync` answers those two names, and `GetRequestHandlerSchemesAsync` returns
`[oidc:{appId}]` on every request. For a host with fragments:

- **Defaults stay the shell's.** Every default is read by name-less calls (`AuthenticateAsync()`,
  the middleware's default authenticate) and those are the shell's today; a fragment never needs
  to be a default. `/bff/login?fragment={fragmentId}` challenges `oidc:{appId}:{fragmentId}` by
  name, and challenges over the live session: today's `Bff.LoginAsync` ends the session
  (`turn.EndAsync()`, then `SignOutAsync(cookie:{appId})`) before it challenges, which is the shell
  login's discard; run unchanged with `?fragment=` it would remove the very record the merge needs.
- **`GetSchemeAsync` answers every fragment name** of the pinned entry with an
  `OpenIdConnectHandler` scheme, and nothing else. `ChallengeAsync`, `SignInAsync` and
  `SignOutAsync` all go through `IAuthenticationHandlerProvider.GetHandlerAsync`, which calls
  `GetSchemeAsync(name)` ([hp] lines 30–50; [as] lines 181–187, 209–215). A name that resolves to
  nothing throws `InvalidOperationException` from the service, so `/bff/login` must check the
  fragment id against the entry before it challenges.
- **`GetRequestHandlerSchemesAsync` returns only what may claim the request.** The middleware is
  the only caller ([mw] line 50), so the list can be empty off the callback paths and hold exactly
  the scheme whose `CallbackPath` is the request's path on them. That preserves S-A7 and R-A6 (one
  handler per request) on the callback and improves on them elsewhere: today one handler is built
  on every request and immediately says no. With the shared path instead, the list on
  `/signin-oidc` is all N+1 in the order of section 1.3, fragments first, shell last.
- **`GetAllSchemesAsync`** returns everything; the service uses it only to compose error messages.

### 4.2 What the options setups and the cache assume

`OidcOptionsSetup.AppFor` takes everything after `oidc:` as an app id, finds no app for
`oidc:{appId}:{fragmentId}`, leaves the options unconfigured, and `OidcOptionsValidation` then
refuses them (empty `ClientId`, R-D9). `AppOptionsCache.Fingerprint` strips the same prefix and
looks the remainder up, so a fragment name is cached under no fingerprint: served to any request,
never checked against the pinned entry, and evicted only by an explicit `TryRemove`. Both need to
parse `{appId}:{fragmentId}` and resolve `fragments[].oauth`; `appId` is `^[a-z0-9-]+$`
(`contracts/registry.schema.json`, `$defs/appId`), so a colon is a safe separator provided
`fragmentId` takes the same pattern.

A fragment scheme's options are the shell's with five differences: `ClientId` and the secret from
the fragment's `oauth`; its scopes and `Resource`; `CallbackPath = /signin-oidc/{fragmentId}`;
`SignInScheme = cookie:{appId}` (the shell's); `SignedOutCallbackPath = PathString.Empty` (section
5). The issuer is the shell's by decision, so the shared backchannel and metadata manager (R-A7)
are shared across the fragments too. `OidcOptionsSetup.CallbackPath` is a static today and becomes
per scheme; `CookieOptionsSetup` hands it to `CallbackBlindCookieManager`, whose scope section 3.2
discussed.

### 4.3 What the invalidator must evict

`OptionsInvalidator.Swapped` compares each app's `Fingerprint` before and after the swap and, when
it differs, evicts `cookie:{appId}` and `oidc:{appId}`, purging the app's sessions when the entry
went or `OAuth.ClientId` changed. `EntryFingerprint.Of` hashes the entry's canonical JSON, nested
members included (`src/Platform.Registry/EntryFingerprint.cs`), so a fragment-only change moves
the shell entry's fingerprint and the shell's pair is evicted today; harmless, the purposes do not
change (S-D4, R-C3). What it does not do:

- **Evict the fragment names.** It must remove `oidc:{appId}:{fragmentId}` for every fragment in
  the old entry and in the new one (their union) whenever the fingerprint moves. A removed fragment
  loses its options; an added one has none yet; a changed one is rebuilt on next use. One eviction
  per name, no I/O.
- **Purge for a fragment's new client.** The "new client" rule compares the shell's `ClientId`
  only. A fragment whose client changed leaves token sets issued to the old client in every record
  of the app; those refresh tokens fail at the provider as `invalid_grant` and end the whole session
  under today's rule, which is the wrong grain. The spec chooses between dropping that fragment's
  set from each record and purging the app's sessions; either is a new rule here.
- **A removed fragment's token sets** stay in records until the session ends. Its routes are gone,
  so nothing reads them; the sweep or the size budget decides whether to care.

`AppBoundTicketStore` stays per app. `SchemeNames.Oidc(appId)` in `Platform.Abstractions` gains a
fragment form, which is the integrator's and so a change request, as the map says of every contract
on this route.

## 5. The logout callbacks

`OpenIdConnectHandler.HandleRequestAsync` claims `RemoteSignOutPath` (default `/signout-oidc`) and
`SignedOutCallbackPath` (default `/signout-callback-oidc`) by exact path before the login callback
([oh] lines 89–100; defaults at [oo] lines 44–46). `HandleSignOutCallbackAsync` unprotects the
`state` with its own `StateDataFormat`, raises `SignedOutCallbackRedirect`, redirects to
`properties.RedirectUri` when there is one, and returns `true` either way ([oh] lines 312–358).
There is no `SkipUnrecognizedRequests` branch: a signed-out callback whose state belongs to another
scheme is answered with an empty response, not passed on. So on a shared `/signout-callback-oidc`
the first request-handler scheme wins, and a fragment scheme ahead of the shell's would answer the
shell's logout callback with a blank page instead of the redirect to `/`.

Under the map's one logout, only the shell's scheme ever signs out at the provider:
`Bff.LogoutAsync` calls `SignOutAsync(cookie:{appId})` then `SignOutAsync(oidc:{appId})`, and the
handler's `SignOutAsync` sends `post_logout_redirect_uri = BuildRedirectUriIfRelative(SignedOutCallbackPath)`
with the scheme's own protected state and the sign-out scheme's `id_token` as `id_token_hint`
([oh] lines 208–310). Fragments therefore:

- set `SignedOutCallbackPath = PathString.Empty`, as every scheme already sets `RemoteSignOutPath`
  (R-D1, R-D2), so they claim neither path;
- register no logout URI at the provider: the Cognito profile sends `logout_uri` = the host root
  and marks the response handled (`CognitoIdpProfile.RedirectToSignOutAsync`), and the standard
  profile's `post_logout_redirect_uri` is the shell's callback;
- are revoked before the record goes. `SessionEnd.RevokeAsync` asks the Duende configuration
  service for the endpoint, client and backchannel of `Scheme.Parse(oidc:{appId})`; that service
  reads the named OpenID Connect options ([OpenIdConnectConfigurationService.cs][dconf] line 39), so `oidc:{appId}:{fragmentId}` resolves once section
  4.2's setup builds them, and the credential style is the issuer's, the same for every fragment.
  One revocation per token set, then the removal, then the shell's one provider logout, as
  `POST /bff/logout` promises today.

With per-fragment login paths this leaves no ordering rule at all; with the shared path it removes
the one conflict between "fragments first" (section 1.3) and "shell first" (this section).
`/signout-oidc` stays cleared on every scheme and 404 at the gate.

## 6. What this asks of the contracts

Named for the spec's change requests, not decided here: `SchemeNames.Oidc(appId, fragmentId)` in
`Platform.Abstractions`; `fragmentId` with a path-safe pattern in `registry.schema.json`; the
http-contract's sentence on `/signin-oidc` saying the prefix, which the code already means, and
`/bff/login?fragment=`; N token sets inside `SessionRecord.Ticket` under suffixed names, or a record
shape change; `/bff/logout` revoking each set. The cookie table, the request gate's rows and the
asset route's reserved list need nothing.

## 7. What the prototype (#771) must settle

1. **The merge sign-in**: a fragment callback on `/signin-oidc/{fragmentId}` that, in
   `OnTicketReceived`, reads the shell's session, merges the fragment's token set under suffixed
   names under the session gate, handles the response and redirects; the same cookie before and
   after, two sets in one record, `GET /bff/session` reporting both. The merge refuses a record
   whose subject is not the ID token's (a planted cookie gets nothing). And the scope of
   `CallbackBlindCookieManager` against that path.
2. **Handlers per request** with a path-aware `GetRequestHandlerSchemesAsync`: zero off the callback
   paths, one on each (the R-A6 counter is in the spike's `Gateway.OidcHandlersCreated`).
3. **A tampered fragment callback** ends on the failure page with `auth.callback`, and the shell's
   callback is untouched by the fragment's scheme.
4. **Two logins in flight** in one browser, shell and fragment, each completing and each deleting
   only its own two cookies.
5. **A fragment-only registry change** evicts the fragment's options and leaves the shell's session
   and a login in flight intact (the R-C3 shape).
6. **The control**: the shared path with `SkipUnrecognizedRequests` on, fragments first and the
   shell last, including what a callback no scheme recognises ends on, so the spec can cite the
   difference rather than this reading of it.

## 8. What I executed and what I read

**Executed** (this session, 2026-11-03):

- `gh issue edit 766 --add-assignee @me`; `gh issue view 766`, `765`, `584`; `git fetch origin`;
  `git worktree add … -b research/fragment-scheme-dispatch origin/main`; `dotnet --version`
  (10.0.401).
- `gh api` reads of the framework source at `v10.0.0` (commit
  `7387de91234d3ef751fa50b3d1bfede4130213ff`; the tag is annotated) and at `v10.0.12` (commit
  `cb21a42eafcd44cc35fad48d99dc82ff7512ce2f`; a lightweight tag), and `diff` of the twelve
  framework files linked below between them: identical except `CookieAuthenticationHandler.cs`, whose
  `IsHostRelative` helper now calls a shared URL helper (lines 449–459 at `v10.0.0`), which nothing
  here uses. `gh api` reads of the Duende source at `atm-4.2.0` (commit
  `f647f494ba9eb27c8985f4e8be05326cba7e3f44`).
- Nothing was built or run. No prototype was written. No AWS call was made. No repository setting
  or tracker item other than #766's assignee, the resolution comment on #766 and its closure was
  changed.

**Read only:** everything else: the files above, `spikes/g0/real-handler/RealOidcHardened/`
(`Program.cs`, `Auth.cs`) and the recorded `output-nuget.txt` and `output-hardened.txt`, the
evidence table in `docs/plan/07-evidence.md`, the Identity, Security, Hosting and Registry sources
named in the Method, ADR-0001, ADR-0017, `contracts/http-contract.md`, `contracts/pipeline.md`,
`contracts/request-gate.md` and `GLOSSARY.md` → "Logical boundary".

**Not verified here, to settle in the prototype or the Cognito research:** whether
`SecureDataFormat<string>.Unprotect` returns null or throws on a foreign nonce suffix (both land in
"passed over" by [oh] lines 1181–1197, so the conclusion stands either way); the provider's limit
on callback URLs per app client, which the Cognito research ticket on the map owns; and the
browser's per-host cookie cap against 2N login cookies, a sizing note only.

[mw]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Security/Authentication/Core/src/AuthenticationMiddleware.cs
[hp]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Http/Authentication.Core/src/AuthenticationHandlerProvider.cs
[ah]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Security/Authentication/Core/src/AuthenticationHandler.cs
[rah]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Security/Authentication/Core/src/RemoteAuthenticationHandler.cs
[rao]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Security/Authentication/Core/src/RemoteAuthenticationOptions.cs
[oh]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs
[oo]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectOptions.cs
[pc]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectPostConfigureOptions.cs
[pcc]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Security/Authentication/Cookies/src/PostConfigureCookieAuthenticationOptions.cs
[ch]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Security/Authentication/Cookies/src/CookieAuthenticationHandler.cs
[as]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Http/Authentication.Core/src/AuthenticationService.cs
[te]: https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Http/Authentication.Abstractions/src/TokenExtensions.cs
[dstore]: https://github.com/DuendeSoftware/foss/blob/f647f494ba9eb27c8985f4e8be05326cba7e3f44/access-token-management/src/AccessTokenManagement.OpenIdConnect/Internal/AuthenticationSessionUserAccessTokenStore.cs
[dprops]: https://github.com/DuendeSoftware/foss/blob/f647f494ba9eb27c8985f4e8be05326cba7e3f44/access-token-management/src/AccessTokenManagement.OpenIdConnect/Internal/StoreTokensInAuthenticationProperties.cs
[dconf]: https://github.com/DuendeSoftware/foss/blob/f647f494ba9eb27c8985f4e8be05326cba7e3f44/access-token-management/src/AccessTokenManagement.OpenIdConnect/Internal/OpenIdConnectConfigurationService.cs
[dopts]: https://github.com/DuendeSoftware/foss/blob/f647f494ba9eb27c8985f4e8be05326cba7e3f44/access-token-management/src/AccessTokenManagement.OpenIdConnect/UserTokenManagementOptions.cs
