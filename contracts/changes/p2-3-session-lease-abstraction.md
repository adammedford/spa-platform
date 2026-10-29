# Change request P2-3: where the cross-task refresh lease lives

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 3 (OAuth and
  sessions)
- **Tasks:** P2-3; P2-1, whose store carries the lease when it sits on the item; 6.2 and 6.3, whose
  composed suites prove it again (#60)
- **Date:** 2026-10-19
- **Status:** decided, 2026-10-20. See the Decision section below. Item 3's margin amended
  2026-10-24, on https://github.com/adammedford/spa-platform/issues/332#issuecomment-6389235562.
  Amended a second time 2026-10-29, the budget end to end (#445), on
  https://github.com/adammedford/spa-platform/issues/476#issuecomment-6474906480: the figures stay, two
  bullets of the first amendment are corrected in place, and the shutdown promise is pending.

## The gap

Plan row P2-3: "One refresh per session across tasks: a short lease taken with a conditional write,
and losers re-read. The provider's rotation grace period is the backstop. Expiry and revocation
hold on every task at once", accepted when "fifty parallel calls split across two tasks cause one
refresh grant and no refused grant" and "a session ended through one task is refused by the other
on its next request" (`docs/plan/06-phase-2-and-risks.md:13`).

What one task does today is the adopted reserve design (ADR-0017 decision 1, which amends ADR-0005;
T-D1 to T-D10). `SessionGates` serialises refreshes per session key in process, and its header
states the limit: "In process only. Two gateway tasks share no gate; the re-read before a refusal
ends a session (T-B8) and the session store's versioned replace are what cover that case"
(`src/Platform.Identity/SessionGates.cs:18-19`). Under the gate, `AccessTokenSource` reads the
record, refreshes, writes it back one version on, and when the versioned replace loses, returns the
winner's stored token (`src/Platform.Identity/AccessTokenSource.cs:80-116, 201-224`).

Across two tasks that is not enough, and the reason is the finding that adopted the design. Two
tasks can both reach the provider's refresh grant; the second grant succeeds inside the grace
window and invalidates the first grant's refresh token (C-2.17). The task that loses the versioned
replace hands back the winner's access token, which is fine, while the record keeps the winner's
refresh token, which the provider has just superseded. The next refresh is refused `invalid_grant`,
the re-read finds the same token, and the session ends (`AccessTokenSource.cs:119-141`): two tasks,
two grants, one refused grant and one lost session, which is the acceptance's negative. So the
lease has to be taken before the provider is called, not at the write, and the other task has to
see it.

The second half of the acceptance holds already, provided the P2-1 store caches nothing:
`SessionEnd` removes the record under the gate (`src/Platform.Identity/SessionEnd.cs:36-58`), and
every call on the other task reads the store (`AccessTokenSource.OwnedAsync`,
`AppBoundTicketStore.Owned`). It needs a test, not a design.

## Where the lease can live

**(a) A new type in `src/Platform.Abstractions`**, with its double in `src/Platform.Testing` and a
contract suite beside `SessionStoreContract`. A shape, the names being the integrator's:

```csharp
/// <summary>One holder per session key for a bounded time: the cross-task half of the refresh gate (P2-3).</summary>
public interface ISessionLeases
{
    /// <summary>Takes the lease on a session for at most <paramref name="ttl"/>. Null when another holder has it.</summary>
    Task<SessionLease?> TryAcquireAsync(string sessionKey, TimeSpan ttl, CancellationToken ct);

    /// <summary>Gives the lease back. A lease that has expired, or that another holder has since taken, is left alone.</summary>
    Task ReleaseAsync(SessionLease lease, CancellationToken ct);
}

public sealed record SessionLease(string SessionKey, string Token, DateTimeOffset ExpiresAt);
```

The token fences the release: a holder that overran its lease must not free the next holder's.
The contract suite asserts that a free key can be taken, a held key cannot, a released key can, an
expired lease can be taken over, a stale release changes nothing, and two keys are independent.
`InMemorySessionLeases` reads the `TimeProvider`, so the expiry test moves a clock as the memory
store's tests do.

This is a new type that changes no existing stage, owner or entry-point signature, so it lands on
the integrator's decision between gates (`contracts/README.md` -> "What may land between gates"),
as `IRequestTelemetryFeature` did under change request 6.4.
`tests/Platform.Abstractions.Tests/PublicSurfaceTests.cs:20-46` gains two names, which is what
makes the addition a decision rather than a convenience.

**(b) A new member on `ISessionStore`.** Rejected. `ISessionStore` froze at G1b
(`contracts/README.md` -> "What G1b froze"); a member changes a frozen signature and every
implementer, `InMemorySessionStore`, `MemorySessionStore` and the DynamoDB store, so it waits for a
gate, and the gate it would wait for is the one P2-3 belongs to.

**(c) The lease as an attribute on the DynamoDB item, taken with a conditional write the store
makes on its own.** It works without a new contract. The DynamoDB store gains an internal method,
or implements an internal interface of `Platform.Identity`'s own, that takes the lease with one
`UpdateItem` under
`attribute_exists(pk) AND (attribute_not_exists(leaseUntil) OR leaseUntil < :now)` and releases
it under `leaseToken = :token`, and `AccessTokenSource` calls it when the store offers it. The
memory store offers nothing, and `SessionGates` stays the whole lease in one process, which is
correct for one process. The lease is then atomic with the item it guards and costs one write,
and `ReplaceAsync`'s expected version is untouched by it.

Weighed against (a):

| | (a) a contract type | (c) store-private |
| --- | --- | --- |
| Integrator paths | `src/Platform.Abstractions`, `src/Platform.Testing`, the surface test | None for the lease itself |
| Where the two-host proof runs | Workstream 6's composed harness over `Platform.Testing` doubles: two gateways sharing one `InMemorySessionStore`, one `InMemorySessionLeases` and one key repository, replaced as the harness replaces `IObjectStore` today (`tests/Gateway.TestHarness/GatewayHarness.cs:190-195`) | Only over the real DynamoDB store, because the memory store has no cross-host lease. The fake DynamoDB server then has to be reachable from `tests/Gateway.TestHarness`, which references no `tests/*.Tests` project, so it moves to `src/Platform.Testing`: an integrator path after all, and a protocol fake of some two hundred lines in the doubles project in place of a double of forty |
| What the acceptance test proves | The orchestration in `AccessTokenSource` against the lease's contract, in both owners' projects, and the DynamoDB lease against the fake in workstream 3's | The same orchestration, but only where the real store runs |
| Runtime cost | A lease write and a release per refresh, on the item or on a lease item of its own; a refresh is already a provider round trip | One conditional write |
| `SessionGates` | Stays, as the in-process half: fifty calls on one task take one lease, not fifty, and waiters share the holder's outcome (T-D6) | The same |

**Recommendation: (a).** The deciding row is the second. Every behaviour in this repository is
proved first against the doubles in its owner's project and again in workstream 6's composed
suites (AGENTS.md -> "Rules for every task"), and (c) is the one design under which the composed
suite cannot run the acceptance without a protocol fake moving into the integrator's doubles
project. (a) leaves the DynamoDB store free to implement `ISessionLeases` on the session item
exactly as (c) describes, so the one-write cost is not lost: the interface fixes the semantics, not
the placement. The time to live is the holder's `ProviderDeadline` (10 s,
`AccessTokenSource.cs:300`) plus a margin, 15 s say, so a holder that dies mid-refresh frees the
session for the next caller, under the caveat T-D10 records: a refresh given up on may still have
been carried out, and only the grace window covers the retry.

## What `AccessTokenSource` then does

Under the in-process gate, take the lease. The holder reads, decides, refreshes, writes under the
expected version and releases. A caller that cannot take it is a loser: it re-reads the record
until the version moves or the lease's time runs out, then uses the token it finds when that token
is not due, or takes the lease itself when it is. "Losers re-read" is the row's own wording, and
the re-read before a refusal (T-B8) stays as it is, for the window no lease closes.

## The acceptance, and how a hermetic test proves it

The plan's two sentences, as tests.

1. **Fifty parallel calls split across two tasks cause one refresh grant and no refused grant.**
   Two gateway hosts in one test process; one shared store, one shared lease table, one shared key
   repository; one test identity provider counting grants, with its grace window at zero so a
   replay cannot be forgiven. Twenty-five calls to each host on a due token. The provider counts
   one grant and no refusal, all fifty calls succeed, and the record holds the one rotated refresh
   token. The mutation that lets it fail: run it with two lease tables, and the provider counts two
   grants and refuses one, which is today's behaviour.
   `Fifty_parallel_calls_on_a_due_token_share_one_refresh_and_the_session_survives`
   (`tests/Platform.Identity.Tests/AccessTokenSourceTests.cs:150`) is the one-host version and F-10
   (`tests/Gateway.IntegrationTests/Functional/RefreshTests.cs:90`) the composed one-host version;
   neither can fail for want of a cross-task lease.
2. **A session ended through one task is refused by the other on its next request.** Log out
   through the first host; the next request with the same cookie to the second host gets 401
   `unauthenticated` from the token path, since the session is gone rather than refused on a
   refresh (`contracts/http-contract.md` -> Errors), and a challenge from the shell gate. The memory
   stores pass
   this today only because there is one process; the shared-store version is what P2-1 and P2-3
   together make true.

Two places can hold the two-host test. Workstream 6's composed harness in
`tests/Gateway.TestHarness`, its own path, already replaces services per gateway and starts the
provider and the mock API once per harness; a two-gateway harness shares those and the three
doubles between two `GatewayFactory`s.
A lighter identity-only version is two `IdentityGateway`s with a shared `RecordingSessionStore`
over one `InMemorySessionStore` and a shared `InMemorySessionLeases`, in workstream 3's project. The
recommendation is both, in that order of weight: workstream 3 proves the orchestration where it is
written, workstream 6 proves the composition, as for every other row. The DynamoDB lease itself is
proved against the fake of change request `p2-1-dynamodb-session-store.md`, in workstream 3's
project.

## What workstream 3 can do meanwhile

The in-process gate and the orchestration exist, so the loser's re-read loop can be written against
an internal lease interface and the memory store, with the contract type swapped in when it lands.
Nothing of that is wasted under (a), and all of it is the whole of (c), so the design is not blocked
on the decision; the composed proof and the public type are.

## Why this is a request rather than an edit

`src/Platform.Abstractions` and `src/Platform.Testing` are the integrator's, and `ISessionStore`
froze at G1b (AGENTS.md, OWNERSHIP.md, `contracts/README.md`).

## Blocking

The decision between (a) and (c) blocks the ticket split on the Phase 2 spec issue (to be filed):
under (a) workstream 6 gets a two-host composed ticket over doubles; under (c) the fake's move to
`src/Platform.Testing` becomes an item of the P2-1 request. The deployed acceptance, two real tasks,
waits on P2-4 and on the gate runs of #63 reaching G5.

## Decision

Integrator, 2026-10-20, on Adam's answers to the ten questions of
https://github.com/adammedford/spa-platform/issues/218 (all accepted as recommended). **Option (a).**

1. **A contract type.** `IRefreshLeases` and `RefreshLease(string Token, DateTimeOffset ExpiresAt)`
   go into `src/Platform.Abstractions`: `Task<RefreshLease?> TryAcquireAsync(string sessionKey,
   TimeSpan ttl, CancellationToken ct)` and `Task ReleaseAsync(string sessionKey, RefreshLease
   lease, CancellationToken ct)`, and no other member: no extend, no renew, no "is held" query. The
   record carries no session key, because a positional record's `ToString()` prints every member
   and the key is on the never-logged list (N-16); the key travels as a parameter. The names differ
   from the request's `ISessionLeases` / `SessionLease` for the glossary reason in item 8.
2. **The lease is independent of the session record.** Acquiring on a key with no record succeeds;
   the contract suite proves the lease with no store in the test. Where the DynamoDB store keeps it
   is the store ticket's choice (#221): a lease item of its own, or the session item under
   `UpdateItem` for both the versioned replace and the lease, with the rule that an item with no
   `ticket` never reads as a session.
3. **Time to live** is one derived value, `AccessTokenSourceOptions.ProviderDeadline +
   AccessTokenSourceOptions.LeaseMargin`, 10 s + 5 s today. The margin covers the versioned write
   after the refresh returns and clock skew between tasks, since the acquiring task's clock is
   compared with the holder's. A holder that dies mid-refresh frees the session after at most
   15 s, under T-D10's caveat that the refresh it gave up on may still have run. **Amended
   2026-10-24:** the margin is `2 × StoreDeadline + 1 s` and covers taking the lease as well, so the
   time to live is 10 s + 11 s and a dead holder frees the session after at most 21 s; see the
   amendment below.
4. **The loser's loop.** A caller that cannot take the lease re-reads the record every 250 ms,
   under `CancellationToken.None` like every store call on this path (T-B15, T-B16), until the
   version moves or the lease expires. A record that vanishes mid-loop answers
   `AccessTokenResult.Unauthenticated`, as the re-read after a lost replace does today. A lease that
   expires with the version unmoved is taken over and the refresh made: the T-D10 case, the grace
   window's job, never `ProviderUnavailable` on a lease alone. The re-read before a refusal (T-B8)
   stays as it is.
5. **Release** is fenced by the token (a stale token changes nothing), made in a `finally` on every
   outcome, `Refreshed`, `Unavailable` and `Ended` alike, and a failed release is logged at Warning
   by outcome, without the token, and never reaches the caller: the lease expires on its own. The
   token fences one release and authenticates nobody, so it joins no never-logged list, and no log
   line carries it.
6. **Landing.** Between gates as the added-type case (`contracts/README.md`), frozen as soon as it
   lands with no marker, as `IRequestTelemetryFeature` did under change request 6.4; the two names
   join `PublicSurfaceTests.Contracted`; `src/Platform.Testing` gains `InMemoryRefreshLeases` over
   `TimeProvider` and an abstract `RefreshLeasesContract` asserting: a free key can be taken, a held
   key cannot, a released key can, an expired lease can be taken over, a stale release changes
   nothing, two keys are independent, and no store is needed. All of it is one integrator ticket on
   the Phase 2 spec issue. This four-part landing is the pattern the session index of
   `p2-6-pingfederate-profile.md` item 7 copies (#224): a new type beside `ISessionStore`, never a
   member of it.
7. **Proof, in order of weight.** Workstream 3 first: two `IdentityGateway`s in
   `tests/Platform.Identity.Tests` over one `InMemorySessionStore` and one `InMemoryRefreshLeases`
   (`IdentityGateway.StartAsync` builds its own store today, so sharing one is a parameter of its
   own), and the DynamoDB lease against the fake of `p2-1-dynamodb-session-store.md`, whose
   evaluator gains the two lease conditions. Workstream 6 second: a two-gateway composed harness in
   `tests/Gateway.TestHarness` sharing the store, the leases and one key repository, with the two
   acceptance tests. Both run the fifty-call test **at grace zero** asserting exactly one grant (the
   mutation "two lease tables" then fails it) and a second case at the 10 s grace proving the
   backstop; the one-host F-10 keeps its replay tolerance. **The fake therefore stays in
   `tests/Platform.Identity.Tests`**, which answers item 5 of the P2-1 request. The G6 split is two
   tickets, one per workstream, as named.
8. **Vocabulary.** The in-process gate is `AppSessions` (`src/Platform.Identity/AppSessions.cs`,
   whose header at lines 35-36 states the in-process limit this request quoted as
   `SessionGates.cs:18-19`; no such file exists), and it stays as the in-process half: fifty calls
   on one task take one lease, and waiters share the holder's outcome (T-D6). Its held handle is
   today a type named `AppSessions.Lease`; workstream 3 renames it in the P2-3 ticket so that
   "lease" means one thing. `GLOSSARY.md` gains **Session gate** and **Refresh lease**, with holder
   and loser defined inside the latter; "lease token" is a member name, not a term.
9. **An ADR of its own**, ADR-0024, carries the (a)/(b)/(c) table, item 2 and item 3 as its
   decisions, with a status-line pointer on ADR-0017 and no edit to its body.

Two citations in the request are corrected by this record: `ProviderDeadline` is
`AccessTokenSource.cs:298`, and `SessionEnd` ends the session under the gate at lines 33-48.

### Amendment, 2026-10-24: item 3's margin

On Adam's answer G1 on #332, confirmed in https://github.com/adammedford/spa-platform/issues/332#issuecomment-6389235562. Item 3's "10 s + 5 s today" no longer holds.
#332's D2 has the holder check, just before the provider call, that `ProviderDeadline +
StoreDeadline + 1 s` (16 s) remains on its lease, so at 15 s no refresh could ever pass, however fast
its acquisition (#341's validation, https://github.com/adammedford/spa-platform/issues/341#issuecomment-6379673480).
Taking the lease is itself a store operation bounded by `StoreDeadline`, so the margin covers it as
well:

- `LeaseMargin` is `2 × StoreDeadline + 1 s`, stated as that formula: 11 s, and the time to live
  21 s, today. The time to live is still the one derived value `ProviderDeadline + LeaseMargin`.
- A holder that dies frees the session after at most 21 s. F3's recovery window is (21 s, r + G)
  after acquisition: open at the 60 s grace the cell runs since #339, empty at 10 s, which is
  accepted.
- The worst-case refresh, acquisition included, is 21 s, inside F1's `ShutdownTimeout` of 25 s.
  **Corrected 2026-10-29 (second amendment):** 21 s bounds the lease, not the request. What
  finishes under the 25 s is the durable write of a refresh admitted before the stop, at most
  20 s from its acquire's send; a loser's request is not bounded by it.
- D2's check passes when "at least" its reserve remains, so the boundary passes: an acquisition
  that takes its whole 5 s leaves 21 s - 5 s = 16 s, which is the reserve.
  **Corrected 2026-10-29 (second amendment):** the check is timed from the acquire's send and
  the re-read under the lease counts too, so the acquire and the re-read together may take at
  most 5 s, not the acquisition alone; and "the reserve" is the lease budget, below.

So the margin covers taking the lease, the versioned write after the refresh returns, and clock
skew between tasks. Item 3 carries an inline pointer to this amendment, and ADR-0024 a status-line
one; decision 4's body is unchanged, as G1 asked.

### Amendment, 2026-10-29: the budget end to end

On #445, whose ten questions Adam confirmed as Overnight 2026-10-28's Q13 to Q22 with overrides on
Q16, Q17, Q19, Q21 and Q22, in https://github.com/adammedford/spa-platform/issues/476#issuecomment-6474906480.
#341's review observed that item 3's margin does not budget the re-read under the lease, and the
first amendment's last two bullets stated the boundary wrongly; they are corrected in place above,
not deleted. The figures do not move: the margin stays `2 × StoreDeadline + 1 s`, the time to
live 21 s and `ShutdownTimeout` 25 s. What changes is the promise, stated below as a decision.

**The budget, once.** S is `StoreDeadline` (5 s), P is `ProviderDeadline` (10 s), and 1 s is the
allowance for clock skew between tasks. Every figure is derived from these three and nowhere
else.

| Step | Bound | Formula | Today |
| --- | --- | --- | --- |
| The lease budget: what must remain of the lease just before the provider is called, for the call, the versioned write after it and skew | the property `ReservedBudget` | P + S + 1 s | 16 s |
| The time to live, `ProviderDeadline + LeaseMargin` | the lease | P + 2S + 1 s | 21 s |
| The acquire and the re-read under the lease, together, timed from the acquire's send | D2's check passes only while this much of the lease has gone | TTL − lease budget = S | 5 s |
| The provider call | `ProviderDeadline`, and `BackchannelTimeout` = P | P | 10 s |
| The versioned write | `StoreDeadline` | S | 5 s |
| Modelled work after admission, the call and the write | from the check passing | P + S | 15 s |
| The durable write, from the acquire's send | the holder's write deadline | TTL − 1 s | 20 s |
| `ShutdownTimeout` (ws2, `PlatformHostingLimits`) | exceeds the write deadline | > TTL − 1 s | 25 s |
| Exhaustion: the threshold a pass is checked against after it ends without a grant | not a request deadline | 2 × TTL | 42 s |

The lease protects from the acquire's send until the rotated token is durable, plus skew (Q14).
The lost-replace re-read, the refusal's re-read and remove, and the release run outside it, since
a successor taking over after the write costs no session the holder would have kept. The check
keeps #341's choice: it counts the re-read under the lease, timed from the acquire's send (Q15).
"Lease budget" is the name for `P + S + 1 s` in prose and records from here on (Q18); "the
reserve" is `GLOSSARY.md`'s Reserve design, ADR-0017's locking, and is not used for this figure.
The property may keep its name `ReservedBudget`, or be renamed in the workstream 3 ticket below.

**Decision 1, G1's limit (Q16).** The figures stay. The check passes only when the acquire and
the re-read together take at most S, so two 3 s operations fail it while each is inside its own
deadline; a pass that fails the check gives the lease back and waits as a loser; 503 with the
session kept comes only when an unsuccessful pass, no lease taken or the check failed, ends after
2 × TTL has elapsed. 2 × TTL is the exhaustion threshold checked after an unsuccessful pass and
not a request deadline, and no end-to-end request bound is claimed. This latency is below F17's
threshold, a failed or timed-out operation, so it is an accepted availability limit and not an
outage F17 covers. Weighed and declined: widening the margin to `3 × S + 1 s` (TTL 26 s, which
moves `ShutdownTimeout` to 28 s or so and lengthens a dead holder's recovery by 5 s, across three
owners), and shrinking S to 4 s (which moves D4's 5 s through F10, F11, F12, F17 and the DynamoDB
adapter's default).

**Decision 2, what F1's shutdown bound promises (Q17).** A successful refresh admitted before the
host is told to stop writes before it stops, with at most 15 s of modelled work after admission
and 20 s from its acquire's send under the 25 s `ShutdownTimeout`; the durable write is the
milestone, not the request's completion. F3b's failed or unknown write and F4's check-to-send
pause stay as exceptions. A loser's wait, about 2 × TTL plus one pass, is F2's and F3's, not F1's:
an aborted loser holds no lease and has no provider call in flight, so it costs the request, not
the session. A refresh admitted after the stop is decision 3.

**Decision 3, the admission rule at shutdown (Q19), pending.** Once `ApplicationStopping` is
observed at admission, after the awaited acquire and re-read and before the provider call, no
provider refresh starts on either refresh path; the refusal releases any acquired lease and
answers `ProviderUnavailable` with the session kept; an admitted refresh keeps its own provider
deadline and writes with no shutdown cancellation. The residual case, a refresh refused at the
stop whose token was due, is F3's bounded recovery, a retry reaching the provider before r + G,
not an automatic recovery within 60 s. This rule is built in the workstream 3 ticket raised by
#445, and the shutdown promise of decision 2 is pending until that ticket lands (Q21): until
then, a loser that takes the lease after the stop may be aborted mid-refresh.

**The store-at-bound answer (Q20).** It stays 503 `idp_unavailable` now. #332's deferred
`503 session_store_unavailable` row carries the note that shutdown refusal, lease exhaustion and
provider failure all answer `ProviderUnavailable` today and must stay distinguishable causes when
the row is designed, at the next gate.

**Where it is recorded (Q21) and what follows (Q22).** This section; a status-line pointer on
ADR-0024 and no new ADR, since the figures stay and a corrected statement of reversible constants
does not clear ADR-0024's threshold; #332's F1, D1 and D2 rows reworded through the tracker route.
Two tickets, each `**Raised by:** #445`: workstream 3's carries the `MayCallProvider` and
`LeaseMargin` comments, decision 3's gate on both refresh paths, and its tests (admission at S and
refusal at S + ε through `AccessTokenSource`; sustained failure reaching the exhaustion response
with the session kept and no provider grant; a stop observed while the acquire or the re-read is
pending refuses admission and releases the lease; a refresh admitted just before the stop writes
despite the signal; the no-lease path observes the same rule; at least one test drives the host
lifetime signal production wiring uses), with the `ReservedBudget` rename optional; workstream
2's restates `PlatformHostingLimits.ShutdownTimeout`'s comment against the 20 s write deadline,
figure unchanged. None for workstreams 6 or 1: T8's rows and the spec's 30 s `stopTimeout` do not
change.
