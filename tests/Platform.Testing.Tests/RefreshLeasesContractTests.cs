using System.Collections.Concurrent;
using Platform.Abstractions;
using Platform.Testing;
using Xunit.Sdk;

namespace Platform.Testing.Tests;

/// <summary>
/// Each case of <see cref="RefreshLeasesContract"/> can fail (CODING_STANDARDS.md -> "An assertion
/// must be able to fail"), against the mutation its body names (#337). A mutant is the double's
/// logic with one rule broken; the unbroken mutant passes the whole suite, so each red below is the
/// broken rule's and not the mutant's.
/// </summary>
public sealed class RefreshLeasesContractTests
{
    [Fact]
    public async Task The_unbroken_mutant_passes_every_case()
    {
        Probe suite = new(Mutation.None);

        await suite.A_free_key_can_be_taken();
        await suite.A_held_key_cannot_be_taken();
        await suite.A_released_key_can_be_taken();
        await suite.An_expired_lease_can_be_taken_over();
        await suite.A_release_by_a_holder_whose_lease_was_taken_over_changes_nothing();
        await suite.A_second_release_of_the_same_lease_changes_nothing();
        await suite.Two_keys_are_independent();
        await suite.A_key_with_no_session_record_can_be_taken_and_no_store_is_needed();
    }

    [Fact]
    public async Task Granting_a_held_key_fails_the_held_key_case()
    {
        Probe suite = new(Mutation.GrantHeldKey);

        await Assert.ThrowsAnyAsync<XunitException>(suite.A_held_key_cannot_be_taken);
    }

    [Fact]
    public async Task A_release_that_changes_nothing_fails_the_released_key_case()
    {
        Probe suite = new(Mutation.ReleaseDoesNothing);

        await Assert.ThrowsAnyAsync<XunitException>(suite.A_released_key_can_be_taken);
    }

    [Fact]
    public async Task A_lease_that_never_expires_fails_the_takeover_case()
    {
        Probe suite = new(Mutation.NeverExpire);

        await Assert.ThrowsAnyAsync<XunitException>(suite.An_expired_lease_can_be_taken_over);
    }

    [Fact]
    public async Task A_stale_release_that_frees_the_key_fails_both_stale_release_cases()
    {
        Probe suite = new(Mutation.ReleaseIgnoresToken);

        await Assert.ThrowsAnyAsync<XunitException>(suite.A_release_by_a_holder_whose_lease_was_taken_over_changes_nothing);
        await Assert.ThrowsAnyAsync<XunitException>(suite.A_second_release_of_the_same_lease_changes_nothing);
    }

    [Fact]
    public async Task One_lease_shared_across_keys_fails_the_independence_case()
    {
        Probe suite = new(Mutation.ShareStateAcrossKeys);

        await Assert.ThrowsAnyAsync<XunitException>(suite.Two_keys_are_independent);
    }

    [Fact]
    public async Task Requiring_a_session_record_fails_the_no_store_case()
    {
        Probe suite = new(Mutation.RequireSessionRecord);

        await Assert.ThrowsAnyAsync<XunitException>(suite.A_key_with_no_session_record_can_be_taken_and_no_store_is_needed);
    }

    [Fact]
    public async Task Refusing_every_acquire_fails_the_free_key_case()
    {
        Probe suite = new(Mutation.RefuseEveryAcquire);

        await Assert.ThrowsAnyAsync<XunitException>(suite.A_free_key_can_be_taken);
    }

    private enum Mutation
    {
        None,
        GrantHeldKey,
        ReleaseDoesNothing,
        NeverExpire,
        ReleaseIgnoresToken,
        ShareStateAcrossKeys,
        RequireSessionRecord,
        RefuseEveryAcquire,
    }

    /// <summary>
    /// The suite over a mutant. Private, so the runner does not discover the suite's own facts on it,
    /// as <c>SessionStoreContractTests</c> does.
    /// </summary>
    private sealed class Probe(Mutation mutation) : RefreshLeasesContract
    {
        protected override IRefreshLeases CreateLeases(TimeProvider clock) => new Mutant(clock, mutation);
    }

    private sealed class Mutant(TimeProvider clock, Mutation mutation) : IRefreshLeases
    {
        private readonly ConcurrentDictionary<string, RefreshLease> leases = new(StringComparer.Ordinal);

        public Task<RefreshLease?> TryAcquireAsync(string sessionKey, TimeSpan ttl, CancellationToken ct)
        {
            string key = Slot(sessionKey);
            DateTimeOffset now = clock.GetUtcNow();
            RefreshLease fresh = new(Guid.NewGuid().ToString("n"), now + ttl);

            if (mutation is Mutation.RefuseEveryAcquire ||
                (mutation is Mutation.RequireSessionRecord && sessionKey.StartsWith("no-session", StringComparison.Ordinal)))
            {
                return Task.FromResult<RefreshLease?>(null);
            }

            bool free =
                !leases.TryGetValue(key, out RefreshLease? current) ||
                (mutation is not Mutation.NeverExpire && current.ExpiresAt < now) ||
                mutation is Mutation.GrantHeldKey;

            if (!free)
            {
                return Task.FromResult<RefreshLease?>(null);
            }

            leases[key] = fresh;
            return Task.FromResult<RefreshLease?>(fresh);
        }

        public Task ReleaseAsync(string sessionKey, RefreshLease lease, CancellationToken ct)
        {
            string key = Slot(sessionKey);

            switch (mutation)
            {
                case Mutation.ReleaseDoesNothing:
                    break;
                case Mutation.ReleaseIgnoresToken:
                    leases.TryRemove(key, out _);
                    break;
                default:
                    leases.TryRemove(KeyValuePair.Create(key, lease));
                    break;
            }

            return Task.CompletedTask;
        }

        private string Slot(string sessionKey) =>
            mutation is Mutation.ShareStateAcrossKeys ? "one-slot" : sessionKey;
    }
}
