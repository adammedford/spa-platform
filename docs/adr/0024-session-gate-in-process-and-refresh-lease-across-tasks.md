# ADR-0024: One refresh per session across tasks: a session gate in process and a refresh lease across tasks

- **Status:** Accepted
- **Date:** 2026-10-20
- **Deciders:** integrator, on Adam's answers to
  https://github.com/adammedford/spa-platform/issues/218 (wayfinder map #215)
- **Plan ref:** `docs/plan/06-phase-2-and-risks.md` → P2-3; change request
  `contracts/changes/p2-3-session-lease-abstraction.md`. Extends ADR-0017 decision 1 across tasks.

## Context

ADR-0017 adopted the reserve refresh design for one task: `AppSessions` serialises refreshes per
session key in process, one holder reads, refreshes and writes the record one version on, and the
callers queued behind it take its outcome. Its header says the limit: two gateway tasks share no
gate. Across two tasks both can reach the provider's refresh grant; the second grant invalidates the
first grant's refresh token inside the rotation grace window (C-2.17), the task that loses the
versioned replace hands back the winner's access token while the record keeps a superseded refresh
token, and the next refresh is refused `invalid_grant` and ends the session. So the claim on a
refresh has to be taken before the provider is called, and the other task has to see it.

`ISessionStore` froze at G1b, so the claim cannot be a member of it. Phase 2's store is DynamoDB,
which can take the claim with one conditional write; the memory store cannot see another process.

## Decision

1. **Two mechanisms, named apart.** The **session gate** is the in-process, per-session-key
   serialisation of refreshes, `AppSessions`, and it stays. The **refresh lease** is the cross-task
   claim on one session's refresh, held by one task for a bounded time and given back when its
   refresh is written. Fifty calls on one task queue on the gate and take one lease; the task that
   holds the lease is the holder, a task that could not take it is a loser and re-reads the record.
2. **The lease is a contract type**, `IRefreshLeases` with `RefreshLease(Token, ExpiresAt)`, in
   `Platform.Abstractions`, with its double and contract suite in `Platform.Testing`, frozen on
   landing. The interface fixes the semantics and not the placement: the DynamoDB store is free to
   keep the lease on the session item with one conditional write.
3. **The lease is independent of the session record.** Acquiring on a key with no record succeeds,
   and the contract suite needs no store. The loser's loop, not the lease, handles a record that is
   gone.
4. **Time to live is the provider deadline plus a margin**, one derived value (10 s + 5 s today),
   the margin covering the versioned write after the refresh and clock skew between tasks. A holder
   that dies frees the session after at most that long; a refresh it gave up on may still have run,
   and only the provider's grace window covers that retry (T-D10).
5. **Release is fenced by the token and never thrown.** A stale token changes nothing, so an
   overrunning holder cannot free its successor's lease; a failed release is logged and the lease
   expires on its own.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| (b) A new member on `ISessionStore` | The interface froze at G1b; a member changes every implementer and waits for the gate P2-3 belongs to. |
| (c) An attribute private to the DynamoDB store, taken by a conditional write of the store's own | Correct and cheap at run time, but the memory store then has no cross-host lease, so workstream 6's composed suite could run the fifty-call acceptance only over the real store, which puts a DynamoDB protocol fake into `Platform.Testing`. Every behaviour here is proved first against the doubles and again composed; (a) keeps that, and loses nothing of (c)'s one-write cost. |
| A lease that carries the session key in its record | A positional record prints every member; the session key is never logged (N-16). The key is a parameter. |
| An extend or renew operation | A holder that overruns is the T-D10 case and gets no second lease. |

## Consequences

- `PublicSurfaceTests.Contracted` gains two names; `Platform.Testing` gains `InMemoryRefreshLeases`
  and `RefreshLeasesContract`. One integrator ticket on the Phase 2 spec issue.
- `AccessTokenSource` takes the lease under the gate, and losers re-read every 250 ms until the
  version moves or the lease expires: `Unauthenticated` when the record is gone, take over on
  expiry. The re-read before a refusal (T-B8) stays.
- `AppSessions` renames its held handle, today `AppSessions.Lease`, so "lease" has one meaning.
- The two-host proofs run at grace zero and assert exactly one grant: workstream 3's two
  `IdentityGateway`s first, workstream 6's two-gateway composed harness second. The DynamoDB fake
  stays in workstream 3's test project, which settles where it lives for P2-1 as well.
- The session index for back-channel logout (`p2-6-pingfederate-profile.md` item 7) copies this
  placement: a new type beside `ISessionStore`, never a member of it.
- `GLOSSARY.md` gains *Session gate* and *Refresh lease*.

## Evidence

Change request `contracts/changes/p2-3-session-lease-abstraction.md` and its Decision section; the
grilling record and answer on https://github.com/adammedford/spa-platform/issues/218; ADR-0017
decision 1 and evidence C-2.17; T-D1 to T-D10 in `docs/plan/07-evidence.md`.
