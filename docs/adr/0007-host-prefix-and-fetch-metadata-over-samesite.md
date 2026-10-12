# ADR-0007: Defend across apps with __Host- cookies, Fetch Metadata and antiforgery, not SameSite

- **Status:** Accepted
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D7 (Doc revision 36)

## Context

Sibling SPAs share a registrable domain. To a browser they are same-site, and a sibling can set
cookies for the parent domain. `SameSite` therefore does not separate them. The review of the first
draft found three real defects from assuming otherwise: login cookies a sibling could plant, a
logout endpoint the framework enables by default, and a session-fixation gap at the callback.

## Decision

Cross-app defence is four mechanisms together, and `SameSite` is not one of them:

- **`__Host-` on every cookie** the platform sets: session, antiforgery, correlation and nonce. Only the app's own host can set such a cookie, and the gateway refuses case variants of its own cookie names on any path.
- **Fetch Metadata**: `/api` and `/bff` accept same-origin browser requests only, judged by `Sec-Fetch-Site` and `Origin`.
- **Antiforgery tokens** on unsafe `/api` methods via `X-CSRF-TOKEN`. `/bff/logout` is a form post and relies on the request gate instead.
- **No CORS**, plus `frame-ancestors 'none'` and `Cross-Origin-Resource-Policy: same-origin`.

Four framework defaults are overridden to make this hold: `ResponseType = code` with
`ResponseMode = query` (a form-post callback is a cross-site POST that arrives without
`SameSite=Lax` login cookies); correlation and nonce cookies renamed to `__Host-spa-corr.` and
`__Host-spa-nonce.` with `Path=/` and `SameSite=Lax`; `RemoteSignOutPath` cleared, so logout is the
same-origin `POST /bff/logout` only; and the antiforgery cookie's `SecurePolicy = Always`, applied
after forwarded headers, because a `__Host-` cookie sent without `Secure` is discarded.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Relying on `SameSite` | Siblings on one parent domain are same-site to a browser, so it separates nothing between them. |

## Consequences

- Assumption A2 is load-bearing: browsers must be current and managed, sending Fetch Metadata and honouring `__Host-`. If that is wrong, the request gate needs an `Origin`-only fallback and a sibling may be able to plant cookies.
- `__Host-` requires `Path=/` and `Secure`, which rules out scoping login cookies to the callback path.
- One app's cookie is never sent to another host, and a sibling cannot plant one. This invariant is enforced by the browser, not by gateway code, and is checked in a real browser (N-25).
- ADR-0008's "session before any asset" forces `SameSite=Lax`, which this decision already assumes.
- The authorization code still appears in URLs and ALB access logs, accepted in Phase 1: codes are single-use, client-bound and PKCE-protected, and Cognito documents no `form_post` mode. PingFederate supports `form_post`, which Phase 2 can use.

### Logging rules this forces

Because state, codes and cookie values pass through the query string and headers, query strings are
never logged, the framework's default request log is replaced, and the authentication loggers are
held at Information or above (the OIDC handler logs the callback URL at Trace and redirect headers
at Debug). Test N-16 searches every log line for canary values.

## Evidence

Threats T2, T12, T17, T18. Tests N-06, N-07, N-16, N-18, N-24 to N-27. Overrides confirmed in the
real-handler run; see `docs/plan/01-plan.md` → Defaults the design overrides.
