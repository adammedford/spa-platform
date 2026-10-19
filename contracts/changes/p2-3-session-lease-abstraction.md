# Change request P2-3: where the cross-task refresh lease lives

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 3 (OAuth and
  sessions)
- **Tasks:** P2-3; P2-1, whose store carries the lease when it sits on the item; 6.2 and 6.3, whose
  composed suites prove it again (#60)
- **Date:** 2026-10-19
- **Status:** open

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
