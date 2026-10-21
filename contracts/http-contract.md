# Browser contract

- **Freezes at:** G1a. The cookie table and the `/bff` endpoints describe identity behaviour, so
  those parts freeze at G1b, recorded by the annotated `contracts-v1` tag (task I-6, ADR-0017).
- **Consumed by:** workstreams 3, 4, 5 and 6
- **Source:** `docs/plan/02-contracts.md` → Browser contract; ADR-0007, ADR-0008

What an SPA author needs to know. The fixture SPAs and `bff-client.js` are the reference
implementation.

## Endpoints

| Request | Made as | Behaviour |
| --- | --- | --- |
| `GET /bff/session` | Fetch | 200 with `sub`, `name`, `email`, `expiresAt` and `csrfToken`; `name` and `email` may be null. `expiresAt` is `SessionRecord.ExpiresAt`, the session expiry, not the access-token expiry. 401 when there is no session. Never contains a token. |
| `GET /bff/login?returnUrl=/path` | Navigation | Discards any existing session, then redirects to the provider. `returnUrl` must be a local path, or it becomes `/`. |
| `POST /bff/logout` | Form navigation | Attempts to revoke the session's refresh token, always removes the session even if revocation fails, then redirects through the provider's logout back to `/`. |
| Any method on `/api/{name}/{rest}` | Fetch | Proxied to the app's upstream called `name`, with `/api/{name}` removed. 404 for a name the app does not list. |
| `GET` or `HEAD` on anything else | Navigation or subresource | The asset at that path. For a path with no extension that accepts HTML, `index.html`. |

An SPA cannot serve content under `/bff/`, `/api/`, `/signin-oidc`, `/signout-callback-oidc`,
`/signout-oidc`, `/healthz` or `/readyz`.

The three `/bff` endpoints and session record share the G1b freeze (ADR-0017). The annotated
`contracts-v1` tag is the freeze record; identity implementation stays blocked until it exists.

## Cookies

The cookie names and attributes belong to G1a; the session record behind `__Host-spa-session`
belongs to G1b (ADR-0017).

| Cookie | Attributes | Holds |
| --- | --- | --- |
| `__Host-spa-session` | `Secure; HttpOnly; SameSite=Lax; Path=/`, no `Domain` | A session id, encrypted for this app's scheme |
| `__Host-spa-csrf` | `Secure; HttpOnly; SameSite=Strict; Path=/`, no `Domain` | The antiforgery cookie token. Cleared at logout. |
| `__Host-spa-corr.*` and `__Host-spa-nonce.*` | `Secure; HttpOnly; SameSite=Lax; Path=/`, no `Domain`, minutes (`SameSite=None` under a profile that uses `form_post`: `pingfederate`, change request P2-6) | Login state. Deleted by the callback. |

Every cookie carries the `__Host-` prefix, so only the app's own host can set it. That includes the
login-state cookies: under their framework names a sibling SPA could plant them and complete a
login as another user.

Two cookies persist in the browser, the session cookie and the antiforgery cookie, and neither holds
token material. Correlation and nonce cookies exist only while a login is in flight.

## Request headers the SPA sends

| Header | On | Value |
| --- | --- | --- |
| `X-CSRF-TOKEN` | Every unsafe method on `/api/**`: anything but `GET`, `HEAD` and `OPTIONS` | The request token, read from `csrfToken` in the `GET /bff/session` response |

The token travels in this header and nowhere else. It is never a form field, because the gateway
would then have to read a request body it is about to proxy, so antiforgery runs with form-body
reading suppressed (`contracts/request-gate.md`, rule `csrf.token`). A request missing it, or
carrying another app's, is refused with 403 and `csrf_failed`.

## Response headers

Set on every response, including proxied ones, where they overwrite what the upstream sent.

- `Content-Security-Policy`: the baseline below plus the entry's additions
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `Cross-Origin-Opener-Policy: same-origin`
- `Cross-Origin-Resource-Policy: same-origin`
- `Strict-Transport-Security: max-age=31536000`
- No `Access-Control-*` header, ever. One sent by an upstream is stripped.

### The baseline content security policy

```text
default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self';
connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none';
form-action 'self' <identity provider origin>
```

The provider origin is `Issuers:{name}:BrowserOrigin`. It is in `form-action` because logout is a
form post that ends in a redirect to the provider; some browsers apply `form-action` to that
redirect, and Chromium has done so. The deployed logout test settles it for the managed browser.

An entry's `csp` adds sources to these ten directives and to no others, which is why
`registry.schema.json` lists exactly them. `*` and `'unsafe-eval'` are rejected in every directive;
`'unsafe-inline'` and `data:` are rejected in `script-src`. `data:` elsewhere is allowed, and the
example entry uses it in `img-src`.

## Errors

Errors are `application/problem+json` with a stable `code`. `/api` and `/bff/session` never answer
with a redirect. Kestrel refuses oversized headers itself, with 431 and no body.

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
| 503 | `idp_unavailable` | The identity provider could not be reached to refresh the token; the session is kept |
| 504 | `upstream_timeout` | The upstream exceeded the entry's timeout |

One refusal is not a problem body. A login callback the OpenID Connect handler refuses (a bad state,
correlation or nonce, or a provider error) is answered with the one static failure page: HTML, 400,
`Cache-Control: no-store`, nothing reflected from the request, rule id `auth.callback`. It is a page
because the browser arrived by navigation and a person reads it (task 3.5, N-05, N-18).

The `code` says little on purpose. Which rule fired is in the log line and in
`security.rejections`, under the rule ids in `contracts/request-gate.md`. Both the method
allowlist and the request gate write `gate.method` for their 405 rejections.
