# ADR-0008: Require a session before serving any asset

- **Status:** Accepted
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D8 (Doc revision 36)

## Context

A backend-for-frontend can either serve a public SPA shell that starts the login itself, or require
authentication before any file is served. These are internal applications.

## Decision

The gateway requires a session before serving any asset. An unauthenticated navigation is
challenged; an unauthenticated fetch gets 401, never a redirect. A public shell per app is deferred
as a possible per-app option.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Public shell with SPA-side login | Leaks the application's existence and front-end content before login, and each SPA would need login code. Deferred, not ruled out. |

## Consequences

- Internal apps leak nothing before login and need no login code.
- It forces `SameSite=Lax`, which ADR-0007 already assumes.
- Every asset response is `private`, because every asset needs a session.
- SPAs must treat 401 as "navigate to `/bff/login`" (assumption A3).

## Evidence

Request path stage 7 (shell gate) in `docs/plan/01-plan.md`. Browser contract in
`docs/plan/02-contracts.md`.
