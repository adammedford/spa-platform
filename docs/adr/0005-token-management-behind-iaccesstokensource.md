# ADR-0005: Use Duende.AccessTokenManagement behind our own IAccessTokenSource

- **Status:** Accepted. Confirmed by Gate G0 item 4 on 11 October 2026. **Amended 2026-10-16 by
  [ADR-0017](0017-g0-findings-fold-into-the-identity-contracts.md): the reserve refresh design is
  now adopted, because G0 item 2 found Cognito's grace window insufficient (C-2.17).**
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
- The library refuses a refresh token over 4,096 characters. G0 item 2 measured it against Cognito: 1,752 characters at login and 1,845 after refreshes for app1, 1,690 and 1,782 for app2 (C-2.20). The hazard does not arise at this scale.
- Gateway-owned locking over the library's refresh call was the **reserve design**. **It is now adopted** (ADR-0017): G0 item 2 found that a replay 2.0 seconds inside Cognito's 10-second grace window is granted and leaves the original successor refused `400 invalid_grant` (C-2.17), which is the trigger this line named. Task 3.8 implements one refresh at a time per session rather than resting on the library's token manager alone, and the reserve's evidence ids T-D1 onward become the design's. A late replay is refused and the successor survives (C-2.18), so the exposure is bounded to the window and locking is sufficient.
- The client secret's credential style is a provider difference, not a call-site choice: Cognito's revocation endpoint refuses a form-body secret with `401` and accepts a Basic header, while the code exchange and refresh accept either (C-2.21). `IIdpProfile` states the style and all three calls use it (ADR-0011, ADR-0017).
- A refresh token that logout revoked may still be honoured by the provider until it expires (C-2.13). Task 3.9 records the revocation's outcome rather than treating the call's return as proof; the library sends the revocation in the form body and does not return the provider's answer.
- The contracts had no outcome for an unreachable provider; `AccessTokenResult` and the error table each gained one (503 `idp_unavailable`, session kept).

## Evidence

G0 items 4 and 5 ran in one harness: 43 expectations held, none failed. Reserve design T-D1 to
T-D10 in `docs/plan/07-evidence.md` → The reserve design.
