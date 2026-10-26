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
| `/bff/session` | `GET` | `same-origin` | Nothing more |
| `/bff/login` | `GET` | `same-origin` or `none` | `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document` |
| `/bff/logout` | `POST` | `same-origin` | `Origin` equals the app origin. No token, because a form cannot send a header. |
| `/signin-oidc`, `/signout-callback-oidc` | `GET` | Any | Nothing here. The framework checks state, correlation and nonce. |
| `/signout-oidc` | None | Any | Always 404. The framework's remote sign-out is switched off. |
| Everything else | `GET`, `HEAD` | Any | A session. Navigations are challenged. Other requests get 401. |

A missing `Sec-Fetch-Site` on `/api` or `/bff` is a rejection, not a pass. `same-site` is rejected
like `cross-site`: that value is what a sibling SPA produces. Test clients send the header
explicitly.

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
| `gate.navigation` | `/bff/login` without `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document` | `origin_rejected` |
| `csrf.token` | The antiforgery token was missing or wrong | `csrf_failed` |
| `auth.callback` | Any refused login callback | — |
| `auth.session` | A cookie that resolves to no session for this app | `unauthenticated` |
| `api.unknown` | The app lists no upstream under that name | `unknown_api` |

`auth.callback` ends on no problem body: the OpenID Connect handler's remote-failure event writes
the static failure page `contracts/http-contract.md` → Errors describes, under that id.
`auth.session` is noted by workstream 3 wherever a cookie resolves to no session for this app,
except at `/bff/login` and `/bff/logout`, which do what they were asked over a dead cookie and end
in no rejection; elsewhere another writer then ends the response: the `/bff/session` endpoint
answers `401 unauthenticated`; the access-token step behind `MapPlatformProxy` answers
`401 unauthenticated`; and `UsePlatformShellGate`'s challenge answers a fetch with
`401 unauthenticated` and a navigation with a redirect to the identity provider, which has no
problem body. A refusal by an upstream API is the API's to log.
