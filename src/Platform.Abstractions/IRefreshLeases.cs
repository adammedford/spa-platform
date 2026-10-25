namespace Platform.Abstractions;

/// <summary>
/// The refresh lease: one holder per session key for a bounded time, the cross-task half of the
/// refresh gate (ADR-0024; change request contracts/changes/p2-3-session-lease-abstraction.md,
/// Decision items 1 to 6; the <c>continuity</c> spec, #332). The session gate, <c>AppSessions</c>,
/// stays the in-process half: fifty calls on one task queue on the gate and take one lease.
///
/// <para>The lease is independent of the session record (ADR-0024 decision 3): acquiring on a key
/// with no record succeeds, and the loser's loop, not the lease, handles a record that is gone. The
/// interface fixes the semantics and not the placement, so a store is free to keep the lease on the
/// session item (decision 2).</para>
///
/// <para>Two members and no others: no extend, no renew, no "is held" query. A holder that overruns
/// its lease is the T-D10 case and gets no second lease (ADR-0024 → Alternatives rejected).</para>
///
/// <para>Freezes on landing, with no marker, as <see cref="IRequestTelemetryFeature"/> did: a new
/// type that changes no existing stage, owner or signature (contracts/README.md → "What may land
/// between gates"). <c>Platform.Testing</c> holds the double and the contract suite every
/// implementation passes.</para>
/// </summary>
public interface IRefreshLeases
{
    /// <summary>
    /// Takes the lease on <paramref name="sessionKey"/> for at most <paramref name="ttl"/>. Null when
    /// another holder has it and its lease has not expired; a lease past its expiry is taken over.
    /// </summary>
    Task<RefreshLease?> TryAcquireAsync(string sessionKey, TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Gives the lease on <paramref name="sessionKey"/> back. Fenced by the lease's token: a lease
    /// that has expired and been taken by another holder, or one already released, is left alone,
    /// so an overrunning holder cannot free its successor's lease (ADR-0024 decision 5).
    /// </summary>
    Task ReleaseAsync(string sessionKey, RefreshLease lease, CancellationToken ct);
}

/// <summary>
/// One held refresh lease. It carries no session key: a positional record's <c>ToString()</c> prints
/// every member, and the session key is never logged (contracts/telemetry.md; N-16), so the key
/// travels as a parameter of <see cref="IRefreshLeases"/> instead (change request P2-3 Decision
/// item 1).
/// </summary>
/// <param name="Token">
/// What fences the release: unique to one acquisition. It authenticates nobody, so it joins no
/// never-logged list, and still no log line carries it (P2-3 Decision item 5).
/// </param>
/// <param name="ExpiresAt">When the lease lapses and another holder may take it over.</param>
public sealed record RefreshLease(string Token, DateTimeOffset ExpiresAt);
