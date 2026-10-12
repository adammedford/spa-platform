# ADR-0004: Keep sessions in the cookie handler with ITicketStore and SaveTokens

- **Status:** Accepted
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D4 (Doc revision 36)

## Context

Phase 1 acceptance criterion 3 requires that the browser receive a session cookie and no OAuth
tokens, and threat T1 is page script stealing tokens. The gateway is a backend-for-frontend, so
tokens must stay on the server while the browser still carries something that identifies the
session.

## Decision

Use the framework's cookie handler with `SessionStore` set to an `ITicketStore` implementation, and
`SaveTokens` on. With `SessionStore` set, the cookie holds a session-id claim only
(`CookieAuthenticationHandler`); the ticket and the saved access, refresh and ID tokens stay in the
store. One serialized record per session, bound to app and client.

Phase 1 runs an in-memory store bounded by `Session:MaxSessions`. Phase 2 moves it to DynamoDB.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Tokens in the cookie | Puts token material in the browser, which criterion 3 and T1 forbid. |
| A separate token vault | A second mechanism beside the one the framework already provides at this seam. |

## Consequences

- The framework-native path: `SaveTokens` with `ITicketStore` satisfies "tokens server-side only" with no custom code.
- A record bound to app and client means a session from one app cannot authenticate at another (T3).
- Every sign-in creates a new session key, because the session cookie is not read on the callback path. This closes the session-fixation gap the review found (T12, N-18).
- `session_ref` in logs is the first 12 hex characters of a SHA-256 of the session key, so a session's requests correlate without the log carrying a usable credential.
- Phase 1 keeps sessions in memory on one replica, so every deployment and restart signs users out. Phase 1 claims nothing about availability or durability.

## Evidence

Stand-in run S-B3, S-B4, S-B6 to S-B8, S-C1 to S-C4, S-D6; real-handler run R-A8, R-A10, R-B1 to
R-B6. Tests F-05, N-18, N-23.
