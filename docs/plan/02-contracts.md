# Contracts

These files freeze at G1a, and their identity parts at G1b. After that, a change is a written request to the integrator, never an edit.

## What freezes

| Contract | File | Freezes at | Consumed by |
| --- | --- | --- | --- |
| Registry document | `contracts/registry.schema.json`, `contracts/registry.example.json` | G1a, except the `oauth` section at G1b | Workstreams 2, 3, 4 and `spactl` |
| Platform configuration | `contracts/platform-config.schema.json` | G1a | Workstream 1 for the task environment, then everyone |
| Records and interfaces | `src/Platform.Abstractions` | G1a, except the identity interfaces at G1b | Workstreams 2, 3, 4, 6 |
| Test doubles | `src/Platform.Testing` | With the interface each one stands in for | Everyone |
| Pipeline and entry points | `contracts/pipeline.md`, `src/Gateway/Program.cs` | G1a | Everyone |
| Browser contract | `contracts/http-contract.md` | G1a | Workstreams 3, 4, 5, 6 |
| Upstream contract | `contracts/upstream-contract.md` | G1a | Workstream 4 and the mock API |
| S3 layout | `contracts/s3-layout.md` | G1a | Workstreams 1 and 2 |
| Infrastructure outputs | `contracts/infra-outputs.schema.json` | G1a | Workstream 1, the scripts, workstream 6 |
| Telemetry | `contracts/telemetry.md` | G1a | Everyone |

The integrator writes every file in this table. Workstreams 1 and 6 propose the infrastructure outputs and telemetry contracts; they do not edit them. The identity parts wait for G0: scheme names, the session record, `IAccessTokenSource`, `IIdpProfile` and the `oauth` section of an entry.

## Registry document

One JSON object at `registry/registry.json`. The gateway accepts or rejects it whole.

```json
{
  "schemaVersion": 1,
  "revision": 42,
  "apps": [
    {
      "appId": "app1",
      "host": "app1.spa-poc.test:8443",
      "release": "2026-10-10.1",
      "oauth": {
        "issuer": "cognito-poc",
        "clientId": "1example23456789",
        "clientSecretRef": "ssm:/spa-platform/poc/apps/app1/client-secret:1",
        "scopes": ["openid", "profile", "https://orders.spa-poc.test/read"],
        "resource": "https://orders.spa-poc.test"
      },
      "apis": [
        { "name": "orders", "upstream": "http://127.0.0.1:5080", "timeoutSeconds": 30 }
      ],
      "csp": { "img-src": ["data:"] },
      "session": { "lifetimeMinutes": 480 }
    }
  ]
}
```

Validation rules. Workstream 2 implements all of them in one library, which the gateway and `spactl` share. Each has a passing and a failing test case.

- **`revision`** must be greater than the live one. A rollback is the old content under a new revision.
- **`appId`** is 3 to 40 lowercase letters, digits or hyphens, and unique.
- **`host`** is the exact authority the browser sends, lowercase, with the port when one is used. It is unique and ends with a suffix that platform policy allows.
- **`oauth.issuer`** names an issuer in platform configuration. **`clientId`** is unique across apps.
- **`clientSecretRef`** names a parameter and a version. A snapshot that cannot read one is rejected and the live snapshot stays. At a cold start there is nothing to keep, so the affected apps are left out and reported, and the rest go live.
- **`scopes`** includes `openid`. **`resource`**, when present, is one `https` URL.
- **`apis[].name`** is 1 to 32 lowercase letters, digits or hyphens, and unique within the app. **`upstream`** matches platform upstream policy and has no path, query or credentials. More than one upstream origin on an entry is a warning.
- **`csp`** adds sources to the baseline for the listed directives. `*`, `'unsafe-eval'`, and `'unsafe-inline'` or `data:` in `script-src` are rejected.
- **`release`** is 1 to 64 letters, digits, dots, hyphens or underscores. `spactl` checks that the prefix exists; the gateway does not.
- **`session.lifetimeMinutes`** is between 5 and 720.

The registry computes a fingerprint for each entry from its canonical JSON. An app's cached options are evicted when its fingerprint changes.

An app definition file is an entry without `clientId`, `clientSecretRef` and `release`. Terraform reads it to create the OAuth client. `spactl app add` completes it from the infrastructure outputs and applies the registry, so each app is described once.

## Platform configuration

Set at deployment through the task environment. It bounds what any registry entry may say, so a registry writer cannot widen it.

| Key | Example | Meaning |
| --- | --- | --- |
| `Platform:Cell` | `poc` | Cell name. Part of every resource name and of the data-protection application name. |
| `Registry:Bucket`, `Registry:Key` | `spa-platform-poc`, `registry/registry.json` | Where the registry lives |
| `Registry:PollSeconds` | `15` | Poll interval, jittered by up to 20% |
| `Assets:Bucket` | `spa-platform-poc` | Where releases live |
| `Issuers:{name}:Authority` | `https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE` | Issuer an entry may name |
| `Issuers:{name}:Profile` | `cognito` | Which `IIdpProfile` applies. Set here, never in an entry. |
| `Issuers:{name}:BrowserOrigin` | `https://spa-poc.auth.us-east-1.amazoncognito.com` | The origin the browser visits at the provider. It goes into `form-action` and is the base of Cognito's logout URL. |
| `Policy:AllowedHostSuffixes` | `.spa-poc.test:8443` | Hosts an entry may claim |
| `Policy:AllowedUpstreams` | `http://127.0.0.1:5080`, `http://127.0.0.1:5081` | Upstream origins an entry may name. Phase 2 uses `https` patterns only. |
| `ForwardedHeaders:TrustedNetworks` | `10.20.0.0/16` | The ALB subnets. Applied to `KnownIPNetworks`; `KnownNetworks` is obsolete in .NET 10. |
| `Session:Store` | `memory` | `dynamodb` in Phase 2 |
| `Session:MaxSessions` | `10000` | Bound on the in-memory store |

## Interfaces

The whole of `Platform.Abstractions`. `Platform.Testing` holds a double for each interface, plus a fake clock and an echo upstream.

```csharp
namespace Platform.Abstractions;

public sealed record AppDefinition(
    string AppId, string Host, string Release,
    OAuthDefinition OAuth, ImmutableArray<ApiDefinition> Apis,
    ImmutableDictionary<string, ImmutableArray<string>> Csp, TimeSpan SessionLifetime,
    string Fingerprint);                            // hash of the entry's canonical JSON

public sealed record OAuthDefinition(
    string Issuer, string ClientId, string ClientSecretRef,
    ImmutableArray<string> Scopes, string? Resource);

public sealed record ApiDefinition(string Name, Uri Upstream, TimeSpan Timeout);

public sealed record IssuerDefinition(string Authority, string Profile, Uri BrowserOrigin);

// Platform configuration, as the registry validator and the options setup read it.
public sealed record PlatformPolicy(
    ImmutableArray<string> AllowedHostSuffixes,
    ImmutableArray<string> AllowedUpstreams,
    ImmutableDictionary<string, IssuerDefinition> Issuers);

public sealed record RegistrySnapshot(
    long Revision, string ETag,
    FrozenDictionary<string, AppDefinition> ByHost,
    FrozenDictionary<string, AppDefinition> ByAppId);

public interface IRegistry
{
    RegistrySnapshot Current { get; }

    // Raised after the swap, never before. Subscribers evict caches here.
    IDisposable OnSwapped(Action<RegistrySnapshot, RegistrySnapshot> oldThenNew);
}

// Set as a request feature by host resolution. Everything after it reads this.
public interface IAppFeature
{
    AppDefinition App { get; }
    RegistrySnapshot Snapshot { get; }
}

// S3 in the gateway and in spactl, memory in tests.
public interface IObjectStore
{
    // Null means the key does not exist.
    Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct);

    // Writes only if the key is absent (ifMatch null) or still has that ETag. False means it did not.
    // Only spactl calls this. The gateway's role cannot write.
    Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct);
}

public sealed record StoredObject(Stream? Body, string ETag, long? Length, bool NotModified);

public interface IClientSecretSource
{
    Task PreloadAsync(IEnumerable<string> secretRefs, CancellationToken ct);  // before a swap
    string Get(string secretRef);                                             // never does I/O
}

public sealed record SessionRecord(
    string AppId, string ClientId, string Subject,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    long Version, ReadOnlyMemory<byte> Ticket);

public interface ISessionStore
{
    Task<string> CreateAsync(SessionRecord record, CancellationToken ct);   // returns the key
    Task<SessionRecord?> GetAsync(string key, CancellationToken ct);
    Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct);
    Task RemoveAsync(string key, CancellationToken ct);
    Task RemoveAppAsync(string appId, CancellationToken ct);
}

public abstract record AccessTokenResult
{
    public sealed record Token(string Value) : AccessTokenResult;
    public sealed record Unauthenticated : AccessTokenResult;
    public sealed record SessionEnded : AccessTokenResult;   // refresh failed, session removed
}

public interface IAccessTokenSource
{
    ValueTask<AccessTokenResult> GetAsync(HttpContext context, CancellationToken ct);
}

public interface ICsrfTokens
{
    string Issue(HttpContext context);              // sets the cookie, returns the request token
    Task<bool> IsValidAsync(HttpContext context);
    void Clear(HttpContext context);                // at logout
}

public interface IIdpProfile
{
    string Name { get; }                            // "standard" or "cognito"
    void Configure(OpenIdConnectOptions options, AppDefinition app, IssuerDefinition issuer);
    Task RedirectToSignOutAsync(RedirectContext context, AppDefinition app, IssuerDefinition issuer);
}

public static class SchemeNames
{
    public static string Oidc(string appId) => "oidc:" + appId;
    public static string Cookie(string appId) => "cookie:" + appId;
}
```

Four choices in here are deliberate.

- **`ReplaceAsync` takes an expected version.** The in-memory store implements it trivially. The Phase 2 store needs it for optimistic concurrency, and having it now means Phase 2 changes no caller.
- **`IClientSecretSource.Get` is synchronous.** Options setup in ASP.NET Core is synchronous, so secrets are resolved before a snapshot goes live and read from memory afterwards.
- **`SessionRecord.Ticket` is bytes.** The store never holds a live ticket object that two requests could mutate.
- **`Fingerprint` decides eviction.** Record equality cannot, because immutable collections compare by reference and every reload builds new ones.

## Pipeline and entry points

`Program.cs` calls these, in this order, and nothing else. A workstream adds no other entry point.

| Stage | Entry point | Owner |
| --- | --- | --- |
| Services | `AddPlatformTelemetry`, `AddPlatformRegistry`, `AddPlatformHosting`, `AddPlatformIdentity`, `AddPlatformProxy`, `AddPlatformSecurity` | 6, 2, 2, 3, 4, 4 |
| 1 Forwarded headers | `UsePlatformForwardedHeaders` | 4 |
| 2 Request log and trace scope | `UsePlatformTelemetry` | 6 |
| 3 Health | `UsePlatformHealth`, which answers `/healthz` and `/readyz` on any host | 2 |
| 4 Host resolution | `UsePlatformHostResolution` | 2 |
| 5 Security headers | `UsePlatformSecurityHeaders` | 4 |
| 6 Request gate | `UsePlatformRequestGate` | 4 |
| 7 Routing and authentication | `UseRouting` and `UseAuthentication`, from the framework | Schemes come from 3 |
| 8 Shell gate | `UsePlatformShellGate` | 3 |
| 9 Antiforgery | `UsePlatformAntiforgery` | 4 |
| 10 Endpoints | `MapPlatformBff`, `MapPlatformProxy`, `MapPlatformAssets` | 3, 4, 2 |

`/healthz` answers 200 while the process runs. `/readyz` answers 200 with `{"revision": N}` once a snapshot is live, and 503 before that. The ALB health check and `spactl wait` both read `/readyz`.

## Browser contract

What an SPA author needs to know. The fixture SPAs and `bff-client.js` are the reference implementation.

### Endpoints

| Request | Made as | Behaviour |
| --- | --- | --- |
| `GET /bff/session` | Fetch | 200 with `sub`, `name`, `email`, `expiresAt` and `csrfToken`. 401 when there is no session. Never contains a token. |
| `GET /bff/login?returnUrl=/path` | Navigation | Discards any existing session, then redirects to the provider. `returnUrl` must be a local path, or it becomes `/`. |
| `POST /bff/logout` | Form navigation | Revokes the refresh token, removes the session, then redirects through the provider's logout back to `/`. |
| Any method on `/api/{name}/{rest}` | Fetch | Proxied to the app's upstream called `name`, with `/api/{name}` removed. 404 for a name the app does not list. |
| `GET` or `HEAD` on anything else | Navigation or subresource | The asset at that path. For a path with no extension that accepts HTML, `index.html`. |

An SPA cannot serve content under `/bff/`, `/api/`, `/signin-oidc`, `/signout-callback-oidc`, `/signout-oidc`, `/healthz` or `/readyz`.

### Cookies

| Cookie | Attributes | Holds |
| --- | --- | --- |
| `__Host-spa-session` | `Secure; HttpOnly; SameSite=Lax; Path=/`, no `Domain` | A session id, encrypted for this app's scheme |
| `__Host-spa-csrf` | `Secure; HttpOnly; SameSite=Strict; Path=/`, no `Domain` | The antiforgery cookie token. Cleared at logout. |
| `__Host-spa-corr.*` and `__Host-spa-nonce.*` | `Secure; HttpOnly; SameSite=Lax; Path=/`, no `Domain`, minutes | Login state. Deleted by the callback. |

Every cookie carries the `__Host-` prefix, so only the app's own host can set it. That includes the login-state cookies. Under their framework names a sibling SPA could plant them and complete a login as another user.

### Response headers

Set on every response, including proxied ones, where they overwrite what the upstream sent.

- `Content-Security-Policy`: the baseline below plus the entry's additions
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `Cross-Origin-Opener-Policy: same-origin`
- `Cross-Origin-Resource-Policy: same-origin`
- `Strict-Transport-Security: max-age=31536000`
- No `Access-Control-*` header, ever. One sent by an upstream is stripped.

```text
default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self';
connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none';
form-action 'self' <identity provider origin>
```

The provider origin is in `form-action` because logout is a form post that ends in a redirect to the provider. Some browsers apply `form-action` to that redirect, and Chromium has done so. The deployed logout test settles it for the managed browser.

### Errors

Errors are `application/problem+json` with a stable `code`. `/api` and `/bff/session` never answer with a redirect. Kestrel refuses oversized headers itself, with 431 and no body.

| Status | `code` | When |
| --- | --- | --- |
| 400 | `invalid_request` | The path failed normalisation, or a cookie name imitates a platform cookie |
| 401 | `unauthenticated` | No session |
| 401 | `session_ended` | The refresh token was refused and the session was removed |
| 403 | `origin_rejected` | The request gate refused the request |
| 403 | `csrf_failed` | The antiforgery token was missing or wrong |
| 404 | `not_found` | Unknown host, missing asset, or a reserved path that is switched off |
| 404 | `unknown_api` | The app lists no upstream under that name |
| 405 | `method_not_allowed` | The path does not accept the method |
| 502 | `upstream_unavailable` | The upstream could not be reached |
| 504 | `upstream_timeout` | The upstream exceeded the entry's timeout |

## Request gate

The gate runs before authentication. The CSRF token check runs after it, because it needs the user. Each rejection is logged and counted under a rule id.

| Path | Methods | `Sec-Fetch-Site` | Also required |
| --- | --- | --- | --- |
| `/api/**` | Any | `same-origin` | `Origin`, when sent, equals the app origin. Unsafe methods carry a valid `X-CSRF-TOKEN`. |
| `/bff/session` | `GET` | `same-origin` | Nothing more |
| `/bff/login` | `GET` | `same-origin` or `none` | `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document` |
| `/bff/logout` | `POST` | `same-origin` | `Origin` equals the app origin. No token, because a form cannot send a header. |
| `/signin-oidc`, `/signout-callback-oidc` | `GET` | Any | Nothing here. The framework checks state, correlation and nonce. |
| `/signout-oidc` | None | Any | Always 404. The framework's remote sign-out is switched off. |
| Everything else | `GET`, `HEAD` | Any | A session. Navigations are challenged. Other requests get 401. |

A missing `Sec-Fetch-Site` on `/api` or `/bff` is a rejection, not a pass. `same-site` is rejected like `cross-site`: that value is what a sibling SPA produces. Test clients send the header explicitly.

One rule covers every path. A cookie name that begins `__Host-spa-` in any other letter case is refused with 400, and so is a platform cookie sent twice. Browsers enforce the prefix. This is the server's own check, because ASP.NET Core matches request cookie names without regard to case.

The `code` in a response says little on purpose. The rule id in the log line and in `security.rejections` says which rule fired: `host.unknown`, `path.invalid`, `path.reserved`, `gate.cookie-name`, `gate.fetch-site`, `gate.origin`, `gate.method`, `gate.navigation`, `csrf.token`, `auth.callback`, `auth.session`, `api.unknown`. `auth.callback` covers every refused login callback. `auth.session` is a cookie that resolves to no session for this app. Tests assert on both the code and the rule id. A refusal by an upstream API is the API's to log.

## Upstream contract

What an API behind the gateway receives, and what it must check for itself.

| Direction | Rule |
| --- | --- |
| Sent upstream | Method, query and body unchanged. Path with `/api/{name}` removed. `Authorization: Bearer` with the session's access token. `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` set by the gateway. `traceparent`. |
| Never sent upstream | `Cookie`. Any `Authorization` the browser sent. `X-CSRF-TOKEN`. Forwarding headers the browser supplied: `Forwarded`, `Via`, `X-Real-IP`, and every `X-Forwarded-*` and `X-Original-*`. A list of names is never complete, so an API trusts only the three headers the gateway sets. |
| Checked by the API | Signature against the issuer's keys. `iss`. `exp`. For Cognito, `token_use` is `access`. `client_id` is on the API's own allowlist. The scope the route needs. `aud` equals the API's identifier when resource binding is on. |
| Returned to the browser | Status, headers and body from the upstream, minus `Set-Cookie` and `Access-Control-*`, whatever the upstream's status. The gateway's security headers replace the upstream's. Redirects are passed through, never followed. |

Phase 1 gives each app one access token, so one audience. An app with several upstreams sends the same token to each. That is safe only when those upstreams share a trust domain, and the validator warns when an entry lists more than one upstream origin.

## S3 layout

```text
registry/registry.json                         one object, every version kept
apps/{appId}/releases/{release}/index.html     required
apps/{appId}/releases/{release}/assets/**      immutable, cached for a year
apps/{appId}/releases/{release}/**             other files, revalidated on every use
```

- **A release prefix is written once.** `spactl` sends `If-None-Match: *` on every object, in single-part uploads. The bucket policy requires that header under `apps/` and denies deletes there to everyone but the teardown role. Without the delete rule, a delete followed by a write would replace a release.
- **Activation is a registry change.** Setting `release` on an entry makes it live within one poll interval. Rolling back is the same change in reverse.
- **Content type comes from the file extension.** The gateway ignores the type stored in S3 and sends `nosniff`. An unknown extension is served as `application/octet-stream`.
- **Cache policy follows the path.** `index.html` and non-asset files are `no-cache` with an ETag. Files under `assets/` get one year and `immutable`. Every response is `private`, because every asset needs a session.

## Infrastructure outputs

Workstream 1 writes `infra/envs/poc/outputs.json` after every apply. Scripts and the deployed tests read it, and none of them reads Terraform state.

```json
{
  "region": "us-east-1",
  "cell": "poc",
  "bucket": "spa-platform-poc",
  "albDnsName": "internal-spa-poc-123456789.us-east-1.elb.amazonaws.com",
  "ecsCluster": "spa-poc",
  "ecsService": "gateway",
  "ecrGateway": "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/gateway",
  "ecrMockApi": "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api",
  "gatewayImage": "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/gateway@sha256:0123abcd",
  "issuer": "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE",
  "cognitoDomain": "spa-poc.auth.us-east-1.amazoncognito.com",
  "clients": {
    "app1": {
      "clientId": "1example23456789",
      "secretParameter": "/spa-platform/poc/apps/app1/client-secret",
      "secretVersion": 1
    }
  },
  "testUsers": {
    "alice": { "passwordParameter": "/spa-platform/poc/test-users/alice" }
  },
  "logGroup": "/spa-platform/poc/gateway"
}
```

## Telemetry

One JSON log line per request, and one event line for each login, refresh, logout and registry swap. Traces carry W3C context to upstreams, and metrics are tagged by app.

| Kind | Names |
| --- | --- |
| Log fields | `ts`, `level`, `msg`, `trace_id`, `span_id`, `cell`, `app_id`, `registry_rev`, `route`, `rule`, `session_ref`, `status`, `duration_ms` |
| `route` values | `asset`, `api`, `bff`, `callback`, `health` |
| Metrics | `gateway.requests`, `auth.logins`, `auth.refreshes`, `auth.sessions.active`, `security.rejections`, `registry.revision`, `registry.age_seconds`, `registry.rejected`, `proxy.upstream.duration` |
| Never logged | `Authorization`, `Cookie`, `Set-Cookie`, query strings, tokens, authorization codes, state, client secrets, session keys |

`session_ref` is the first 12 hex characters of a SHA-256 of the session key. It correlates a session's requests without being usable as a credential.

Query strings are dropped because the callback carries the authorization code and state there. The framework's default request log includes the query string, so workstream 6 replaces it. The OIDC handler also logs the callback URL at Trace and redirect headers at Debug, so the authentication loggers are held at Information or above.

Phase 1 exports the log stream and nothing else. OpenTelemetry tracing and metrics are wired in, with the OTLP exporter switched on by `OTEL_EXPORTER_OTLP_ENDPOINT`. Phase 1 leaves it unset, so there is no collector to run or pay for. Tests read spans and metrics through the in-memory exporter.
