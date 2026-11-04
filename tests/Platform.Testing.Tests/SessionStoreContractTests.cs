using Platform.Abstractions;
using Platform.Testing;
using Xunit.Sdk;

namespace Platform.Testing.Tests;

/// <summary>
/// The store suite's first assertion can fail (CODING_STANDARDS.md -> "An assertion must be able to
/// fail"). It must accept a store that serializes the record, whose ticket comes back in a new array,
/// and refuse one that loses part of it. Change request P2-1 Decision 5.
/// </summary>
public sealed class SessionStoreContractTests
{
    [Fact]
    public async Task A_store_that_hands_back_the_ticket_in_a_new_array_of_equal_content_passes()
    {
        Probe suite = new(record => record with { Ticket = record.Ticket.ToArray() });

        await suite.A_created_session_reads_back_under_the_key_it_returned();
    }

    [Fact]
    public async Task A_store_that_truncates_the_creation_time_to_whole_seconds_fails()
    {
        Probe suite = new(record => record with { CreatedAt = WholeSeconds(record.CreatedAt) });

        await Assert.ThrowsAnyAsync<XunitException>(suite.A_created_session_reads_back_under_the_key_it_returned);
    }

    [Fact]
    public async Task A_store_that_hands_back_a_ticket_one_byte_shorter_fails()
    {
        Probe suite = new(record => record with { Ticket = record.Ticket[..^1].ToArray() });

        await Assert.ThrowsAnyAsync<XunitException>(suite.A_created_session_reads_back_under_the_key_it_returned);
    }

    private static DateTimeOffset WholeSeconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);

    /// <summary>
    /// The suite over a store whose reads pass through <c>onRead</c>. Private, so the runner does not
    /// discover the suite's own facts on it: each case above runs the one fact it is about, and a store
    /// built to fail it does not show up as a red test.
    /// </summary>
    private sealed class Probe(Func<SessionRecord, SessionRecord> onRead) : SessionStoreContract
    {
        protected override ISessionStore CreateStore() => new ReadThrough(new InMemorySessionStore(), onRead);
    }

    private sealed class ReadThrough(ISessionStore inner, Func<SessionRecord, SessionRecord> onRead) : ISessionStore
    {
        public Task<string> CreateAsync(SessionRecord record, CancellationToken ct) => inner.CreateAsync(record, ct);

        public async Task<SessionRecord?> GetAsync(string key, CancellationToken ct) =>
            await inner.GetAsync(key, ct) is { } record ? onRead(record) : null;

        public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct) =>
            inner.ReplaceAsync(key, record, expectedVersion, ct);

        public Task RemoveAsync(string key, CancellationToken ct) => inner.RemoveAsync(key, ct);

        public Task RemoveAppAsync(string appId, CancellationToken ct) => inner.RemoveAppAsync(appId, ct);
    }
}
