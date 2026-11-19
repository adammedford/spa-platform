# ADR-0043: The gated session rules live in one module above `ISessionStore`; the ticket store stays an ungated adapter

- **Status:** Accepted. Landed by the merges of #164, #165, #166 and #167 (`3f301bf`, `3fb9c15`,
  `d62acc4`, `8741792`).
- **Date:** 2026-11-18
- **Deciders:** integrator
- **Plan ref:** No decision row. The app-sessions spec #141: its grill, Q1 to Q14, and the four
  additions accepted on it; ticket #168.

## Context

A session's rules were kept by convention across four files. The app-and-client ownership check was
written four times, in the ticket store, the token source, logout's session end and `/bff/session`.
Two of the four paths took the session gate, and two went straight to the store. A session ended in
four places under different rules, and the ticket store's renew that carries a request was a dead,
ungated writer. The framework's cookie handler reads the ticket store on every pinned request that
presents a session cookie, and removes through it when it finds an expired ticket and when it signs
out. Those calls are the framework's, and no module of ours can own them.

## Decision

1. **`AppSessions`, the grown form of `SessionGates` in `Platform.Identity`, is the one module
   through which the gateway opens, reads, changes and ends a session.** It holds the
   app-and-client ownership check once and takes the app as an argument. `EnterAsync` waits for the
   session gate and returns the turn, `AppSessions.Turn`: the ownership-checked read taken under
   the gate (`Owned`), the versioned replace at that record's version (`ReplaceAsync`), and the end
   that runs a pre-removal action and removes in a `finally` (`EndAsync`). It owns the request's
   session key channel, read through `KeyOf`, and is the one writer of `session_ref`. It has no
   extracted interface.
2. **Every change or end the gateway itself makes to a live session runs under that session's
   gate**, one holder at a time, in process, with `CancellationToken.None`. The exceptions are
   named: the cookie handler's two removals through the ticket store (the expiry removal during
   authentication and the sign-out's removal) and the options invalidator's bulk purge of an app's
   sessions on a registry swap.
3. **The ticket store stays the `ITicketStore` adapter ADR-0004 requires, an ungated door.** It
   reaches the module only through its ungated members: `OpenAsync`, the ownership-checked read
   `ResolveAsync`, and the owned remove `RemoveAsync`. It refuses the renew that carries a request.
   - The read is ungated because gating it would hold every request on a session behind a refresh
     of up to the provider deadline.
   - The remove is ungated because the session gate is not re-entrant. The token source ends a
     session and then signs out while it still holds its turn, and a gated remove reached through
     that sign-out would wait on itself.
   - On the refresh and re-login paths that remove is a second, idempotent call that finds
     nothing. After logout's end it makes no store call at all (`MarkEnded`, #386 Q192). The module
     owns removal; the ticket store's remove is the framework reaching the same remove body.
4. **This sits inside ADR-0004, ADR-0005 as amended by ADR-0017, and ADR-0017.** The session gate
   is the reserve design's serialisation of refreshes, widened to every change and end, and
   "revoke, then remove whatever the provider said" (ADR-0017 decision 4) is the turn's end. No
   frozen signature moved: `ISessionStore`, `SessionRecord`, `IAccessTokenSource`,
   `AccessTokenResult`, `SchemeNames` and the pipeline's entry points stay as `contracts-v1` froze
   them.

## Alternatives rejected

- **One owner of all session-store access**, as the survey card proposed. The framework's
  `ITicketStore` reads and removals are not ours to own.
- **Gate the per-request read.** Every request on a session would wait behind a refresh.
- **Gate the remove the sign-out reaches.** The token source holds its turn when it signs out, so
  this deadlocks.
- **An extracted `IAppSessions`.** With one implementation it is a hypothetical seam. The real seam
  is `ISessionStore`, with two adapters, and the module is tested across it.
- **Revoke on re-login, for parity with logout.** `/bff/login` is frozen at G1b and promises a
  discard. Revoking would change the contract's behaviour and needs a change request; it would also
  put the provider deadline on every re-login. The discard runs through the turn's end with no
  pre-removal action.
- **Keep the four copies.** Each rule change would mean finding every copy, and "revoke, then
  remove" could not be read as one operation.

## Consequences

- A session rule (ownership, what a lost versioned replace means, how a session ends) changes in
  one place.
- The three tests that counted store reads retired (#167). The versioned-replace race is proved at
  the module's interface, and the refresh recovery through the provider's token delay.
- The session gate holds in one process only. The cross-task half is the refresh lease of ADR-0024,
  as amended by ADR-0038, which the token source takes while it holds its turn; this ADR decides
  nothing about it. "Lease" means the refresh lease alone, so the held gate is a turn.

## Evidence

- The merges of #164 to #167: `3f301bf`, `3fb9c15`, `d62acc4`, `8741792`.
- #141's grilling record, its decisions shared with the other specs, and the four additions with
  their acceptance (comments 1, 2, 3 and 4 on #141).
- `src/Platform.Identity/AppSessions.cs` and `AppBoundTicketStore.cs` on main at 16247c8, whose
  member names this ADR uses.
