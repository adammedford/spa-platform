using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The Phase 1 <see cref="ISessionStore"/> (plan row 3.4, ADR-0004): serialized tickets in memory on
/// the one replica, a versioned replace, a bound of <c>Session:MaxSessions</c>, an expiry sweep and a
/// purge by app. It passes <c>Platform.Testing.SessionStoreContract</c> unchanged, as the
/// Phase 2 store must.
///
/// One lock guards the whole map rather than a concurrent dictionary, because the bound is a
/// check-then-act: two logins at once must not both see a free place. At 10,000 sessions the
/// eviction scan under that lock is cheap next to the provider round trip that precedes a login.
///
/// A session past its <see cref="SessionRecord.ExpiresAt"/> reads as absent at once, and the sweep
/// frees its memory within <see cref="SweepInterval"/> whether or not anyone reads it again.
/// </summary>
internal sealed class MemorySessionStore : ISessionStore, IDisposable
{
    /// <summary>How often the sweep runs.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, SessionRecord> sessions = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly int maxSessions;
    private readonly TimeProvider clock;
    private readonly ITimer sweep;

    public MemorySessionStore(int maxSessions, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSessions, 1);
        ArgumentNullException.ThrowIfNull(clock);
        this.maxSessions = maxSessions;
        this.clock = clock;
        sweep = clock.CreateTimer(_ => Sweep(), state: null, SweepInterval, SweepInterval);
    }

    /// <summary>
    /// The store <c>Session:MaxSessions</c> describes (contracts/platform-config.schema.json), once
    /// <see cref="SessionStores"/> has read <c>Session:Store</c> as <c>memory</c>. A bound this store
    /// cannot honour is refused rather than guessed at.
    /// </summary>
    internal static MemorySessionStore FromConfiguration(IServiceProvider services)
    {
        IConfiguration configuration = services.GetRequiredService<IConfiguration>();

        string? max = configuration["Session:MaxSessions"];
        if (!int.TryParse(max, NumberStyles.None, CultureInfo.InvariantCulture, out int maxSessions) || maxSessions < 1)
        {
            throw new InvalidOperationException(
                $"Session:MaxSessions is '{max}'. It must be a whole number of at least 1.");
        }

        return new MemorySessionStore(maxSessions, services.GetService<TimeProvider>() ?? TimeProvider.System);
    }

    /// <summary>How many sessions the store holds, expired ones the sweep has not reached included.</summary>
    internal int Count
    {
        get
        {
            lock (gate)
            {
                return sessions.Count;
            }
        }
    }

    public void Dispose() => sweep.Dispose();

    public Task<string> CreateAsync(SessionRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 256 bits from the framework's CSPRNG, as the G0 spike's store issued them: the key is the
        // only thing the session cookie carries, so it is a credential (contracts/telemetry.md lists
        // it as never logged; SessionRef.Of is what may be).
        string key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            if (sessions.Count >= maxSessions)
            {
                EvictNearestExpiry();
            }

            // The record is kept as given. Its ticket is the bytes TicketSerializer has just produced,
            // which nothing else holds, so a copy would buy nothing.
            sessions.Add(key, record);
        }

        return Task.FromResult(key);
    }

    public Task<SessionRecord?> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(Live(key));
        }
    }

    public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (Live(key) is not { } current || current.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            sessions[key] = record;
            return Task.FromResult(true);
        }
    }

    public Task RemoveAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            sessions.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAppAsync(string appId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            // Removing inside the foreach is safe: see RemoveExpired.
            foreach (KeyValuePair<string, SessionRecord> session in sessions)
            {
                if (string.Equals(session.Value.AppId, appId, StringComparison.Ordinal))
                {
                    sessions.Remove(session.Key);
                }
            }
        }

        return Task.CompletedTask;
    }

    private void Sweep()
    {
        lock (gate)
        {
            RemoveExpired();
        }
    }

    /// <summary>Removes every expired session. Call under the lock.</summary>
    /// <remarks>
    /// Removing from the map while enumerating it does not invalidate the enumerator on .NET Core 3.0
    /// and later: "This mutating method may be safely called without invalidating active enumerators"
    /// (https://learn.microsoft.com/dotnet/api/system.collections.generic.dictionary-2.remove).
    /// It is not thread safety; the lock is still what makes it safe here.
    /// </remarks>
    private void RemoveExpired()
    {
        DateTimeOffset now = clock.GetUtcNow();
        foreach (KeyValuePair<string, SessionRecord> session in sessions)
        {
            if (Expired(session.Value, now))
            {
                sessions.Remove(session.Key);
            }
        }
    }

    /// <summary>
    /// Frees one place by removing the session nearest its expiry, which is an expired one whenever
    /// the sweep has not yet reached it. Call under the lock.
    /// </summary>
    /// <remarks>
    /// Decided at task 3.4, where the plan names a bound but not what happens at it: a login at the
    /// bound succeeds and the oldest-ending session gives way, rather than the login failing on the
    /// callback. Either way a flood of logins costs someone a session; this way it is never the
    /// user who is signing in now.
    /// </remarks>
    private void EvictNearestExpiry()
    {
        KeyValuePair<string, SessionRecord> nearest = sessions.MinBy(session => session.Value.ExpiresAt);
        sessions.Remove(nearest.Key);
    }

    /// <summary>The record under a key, or null when there is none or it has expired. Call under the lock.</summary>
    private SessionRecord? Live(string key)
    {
        if (!sessions.TryGetValue(key, out SessionRecord? record))
        {
            return null;
        }

        if (Expired(record, clock.GetUtcNow()))
        {
            sessions.Remove(key);
            return null;
        }

        return record;
    }

    /// <summary>
    /// A session has ended at its expiry instant. One rule for the read, the replace, the sweep and
    /// eviction, so no path keeps a session another path has already ended.
    /// </summary>
    private static bool Expired(SessionRecord record, DateTimeOffset now) => record.ExpiresAt <= now;
}
