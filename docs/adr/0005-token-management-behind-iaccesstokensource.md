# ADR-0005: Use Duende.AccessTokenManagement behind our own IAccessTokenSource

- **Status:** Accepted. Confirmed by Gate G0 item 4 on 11 October 2026.
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D5 (Doc revision 36)

## Context

Sessions hold refresh tokens, so the gateway must perform the refresh grant and revocation. Writing
that protocol step by hand is the kind of custom OAuth code the platform's rules forbid where a
supported library does the job. Duende.AccessTokenManagement is Apache-2.0.

## Decision

Use Duende.AccessTokenManagement 4.x for refresh and revocation, behind the platform's own
`IAccessTokenSource` interface. Only `Platform.Identity` may reference the package (checked by
A-04).

A fixed sentinel scheme is registered, and every call passes both `ChallengeScheme` and
`SignInScheme`.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Hand-written refresh | Custom protocol code where a maintained library covers the step. Kept as the fallback if G0 item 4 had failed: one in-house class over Duende.IdentityModel. |

## Consequences

- Our interface keeps the library replaceable.
- G0 item 4 passed on the shipped packages, under derived schemes: a refresh on app 1 used client 1 even with the first request on app 2. The hazard is narrower than the plan first said, because the library finds the client through the default challenge scheme at call time.
- The sentinel scheme stays regardless: what reads the default challenge scheme once is the library's options hook, which cannot be built outside a request and inside one binds to the first caller's app. A call that omits the schemes works until the first refresh, then throws.
- The library refuses a refresh token over 4,096 characters, which G0 item 2 must measure against Cognito.
- Gateway-owned locking over the library's refresh call is the **reserve design**, not adopted. Task 3.8 stays on the library's token manager. The reserve reopens if G0 item 2 shows Cognito's grace window does not cover a replayed refresh token, or at the PingFederate gate.
- The contracts had no outcome for an unreachable provider; `AccessTokenResult` and the error table each gained one (503 `idp_unavailable`, session kept).

## Evidence

G0 items 4 and 5 ran in one harness: 43 expectations held, none failed. Reserve design T-D1 to
T-D10 in `docs/plan/07-evidence.md` → The reserve design.
