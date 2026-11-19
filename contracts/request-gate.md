# Request gate

- **Freezes at:** G1a, with the browser contract. `docs/plan/02-contracts.md` gives the gate its own
  section under the browser contract's freeze; this repository gives it its own file, because
  workstream 4 owns it and `contracts/http-contract.md` is read by SPA authors.
- **Consumed by:** workstream 4 (`UsePlatformRequestGate`), and workstream 6's negative suite
- **Source:** `docs/plan/02-contracts.md` → Request gate; ADR-0007

The gate runs before authentication (`UsePlatformRequestGate` in `contracts/pipeline.md`). The CSRF
token check runs after it, because that one needs the user. Each rejection is logged under a rule
id, and counted under it too. A rejection is an answer the application gives: a response some
stage ends the request with. Every one reaches the request line and `security.rejections`,
because `UsePlatformTelemetry` is the first stage (ADR-0018). Both method-checking stages write
`gate.method` to `IRequestTelemetryFeature`; `contracts/telemetry.md` says the same. What Kestrel refuses before any stage runs, the 431 for oversized headers in
`contracts/http-contract.md` → Errors, is the server's answer, carries no rule id, and is outside
this sentence.

## Rules by path

| Path | Methods | `Sec-Fetch-Site` | Also required |
| --- | --- | --- | --- |
| `/api/**` | Any | `same-origin` | `Origin`, when sent, equals the app origin. Unsafe methods carry a valid `X-CSRF-TOKEN`. |
| `/fragments/{seg}/api/**` | Any | `same-origin` | `Origin`, when sent, equals the app origin. Unsafe methods carry a valid `X-CSRF-TOKEN`. |
| `/bff/session` | `GET` | `same-origin` | Nothing more |
| `/bff/fragments` | `GET` | `same-origin` | Nothing more |
| `/bff/login` | `GET` | `same-origin` or `none` | `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document` |
| `/bff/chain` | `GET` | Any | `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document`. No token: the continuation `c` binds the step to the session. |
| `/bff/logout` | `POST` | `same-origin` | `Origin` equals the app origin. No token, because a form cannot send a header. |
| `/signin-oidc`, `/signin-oidc/{fragmentId}`, `/signout-callback-oidc` | `GET` | Any | Nothing here. The framework checks state, correlation and nonce. |
| `/signout-oidc` | None | Any | Always 404. The framework's remote sign-out is switched off. |
| Everything else | `GET`, `HEAD` | Any | A session. Navigations are challenged. Other requests get 401. |

A missing `Sec-Fetch-Site` on `/api`, `/fragments/{seg}/api` or `/bff` is a rejection, not a
pass, except on `/bff/chain`, whose row reads no `Sec-Fetch-Site`. `same-site` is rejected like
`cross-site`: that value is what a sibling SPA produces. Test clients send the header explicitly.

`/fragments/{seg}/api/**` and `/signin-oidc/{fragmentId}` are classified by path shape, as
`/api/**` is: `{seg}` and `{fragmentId}` are any one path segment, and the row applies whether or
not the segment names a fragment of the app, on a host with fragments or without. What such a
path then answers is the routes' (`contracts/http-contract.md` → Endpoints). The antiforgery token
is one per app: a fragment API call carries the same `X-CSRF-TOKEN` as a call on `/api/**`.

`/bff/chain` accepts any `Sec-Fetch-Site` because Fetch Metadata computes it over a navigation's
whole redirect chain: a step reached through the provider's redirect is `cross-site`, or
`same-site` under a custom domain sharing the site, at every hop after the provider, so requiring
`same-origin` or `none` would refuse every legitimate step. `Sec-Fetch-Mode` and `Sec-Fetch-Dest`
survive redirects, so the row requires them. The step's protection is its continuation, as a
callback's is its state: the step needs the session, the session's key must equal the
continuation's, and a missing, unreadable, expired or mismatched continuation ends under
`auth.chain` (`contracts/http-contract.md` → Fragments → "`/bff/chain`").

## The rule that covers every path

A cookie name that begins `__Host-spa-` in any other letter case is refused with 400, and so is a
platform cookie sent twice. Browsers enforce the prefix. This is the server's own check, because
ASP.NET Core matches request cookie names without regard to case.

## Rule ids

The `code` in a response says little on purpose. The rule id in the log line says which rule
fired, and so does the tag on `security.rejections`.

This table is the normative list, and `Platform.Abstractions.RuleIds` holds the same strings as
constants so that no owner keeps a copy of its own: A-03 stops one workstream reading another's,
and four workstreams raise these ids. A test in `tests/Platform.Abstractions.Tests` reads the
table below and fails if the two drift. ADR-0015 decided it.

| Rule id | Fires when | `code` |
| --- | --- | --- |
| `host.unknown` | The request authority matches no registry `host` | `not_found` |
| `path.invalid` | The path failed normalisation | `invalid_request` |
| `path.reserved` | A reserved path that is switched off | `not_found` |
| `gate.cookie-name` | A cookie name imitates a platform cookie, in another letter case or sent twice | `invalid_request` |
| `gate.fetch-site` | `Sec-Fetch-Site` is absent, `same-site` or `cross-site` where `same-origin` is required | `origin_rejected` |
| `gate.origin` | `Origin` is present and is not the app origin | `origin_rejected` |
| `gate.method` | The path does not accept the method | `method_not_allowed` |
| `gate.navigation` | `/bff/login` or `/bff/chain` without `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document` | `origin_rejected` |
| `csrf.token` | The antiforgery token was missing or wrong | `csrf_failed` |
| `auth.callback` | Any refused login callback | — |
| `auth.session` | A cookie that resolves to no session for this app | `unauthenticated` |
| `api.unknown` | The app lists no upstream under that name, on `/api/{name}/`, or the fragment the path names lists none, on `/fragments/{fragmentId}/api/{name}/` | `unknown_api` |
| `auth.store` | A session store operation failed or ran out `StoreDeadline` (the `continuity` spec's F17), the login callback's create, a fragment callback's replace and a `/bff/chain` step's included, or a refresh ended at the store's bound, 2 × TTL, with the session kept (its D2, a distinct cause); not the session remove `POST /bff/logout` makes, which answers the usual logout (its F11) | `session_store_unavailable` |
| `auth.chain` | A `/bff/chain` step whose continuation is missing, unreadable, expired, or minted for another session or another app | — |

`auth.callback` ends on no problem body: the OpenID Connect handler's remote-failure event writes
the static failure page `contracts/http-contract.md` → Errors describes, under that id. Neither
does `auth.chain`, which workstream 3's `/bff/chain` endpoint ends with a 302 to `/`, the app's
root, changing nothing in the session (`contracts/http-contract.md` → Fragments).
`auth.session` is noted by workstream 3 wherever a cookie resolves to no session for this app,
except at `/bff/login` and `/bff/logout`, which do what they were asked over a dead cookie and end
in no rejection; elsewhere another writer then ends the response: the `/bff/session` and
`/bff/fragments` endpoints answer `401 unauthenticated`; the access-token step behind
`MapPlatformProxy` answers `401 unauthenticated`, on a fragment's route as on `/api`; and
`UsePlatformShellGate`'s challenge answers a fetch with `401 unauthenticated` and a navigation,
a `/bff/chain` step included, with a redirect to the identity provider, which has no problem
body. A refusal by an upstream API is the API's to log.

`auth.store` is ended or noted by workstream 3 wherever a session store operation fails or runs
out `StoreDeadline`, and wherever a refresh ends at the store's bound, and no cookie is cleared.
A failure inside `UseAuthentication` (the cookie handler's read on any path, the login callback's
create, and a fragment callback's replace at `/signin-oidc/{fragmentId}`) is ended by workstream
3's schemes and shell gate: the login callback's create by the app's OpenID Connect scheme and a
fragment callback's replace by that fragment's, each with the problem body, not the static
failure page; any other by `UsePlatformShellGate`, on every path, `/bff` and `/api` included,
before the antiforgery check sees the request. A failure after that is ended by the writer that
meets it: the `/bff/session` endpoint, `GET /bff/login`'s discard, a `/bff/chain` step and
`POST /bff/logout`'s read before the remove each answer `503 session_store_unavailable`, and
behind `MapPlatformProxy` the access-token source notes the rule and returns `StoreUnavailable`,
which the access-token step answers with that body. The session remove `POST /bff/logout` makes
is the exception: unknown at `StoreDeadline`, it answers the usual logout (the `continuity`
spec's F11).
