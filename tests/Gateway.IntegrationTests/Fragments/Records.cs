using Microsoft.AspNetCore.Authentication;
using Platform.Abstractions;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// Reads a session record's ticket as the gateway wrote it: the framework's <see cref="TicketSerializer"/>,
/// the shell's tokens under <c>.Token.{name}</c>, and a fragment's under <c>.Token.{name}:{fragmentId}</c>
/// with its client under <c>.Fragment.Client:{fragmentId}</c> (workstream 3's <c>FragmentSets</c>). A
/// composed trace reads these only to state the record a trace ends with; what the gateway sends upstream
/// is read at the echo API. The names are workstream 3's, mirrored here because a composed suite
/// references no workstream's internals.
/// </summary>
internal static class Records
{
    public static AuthenticationProperties Properties(SessionRecord record) =>
        TicketSerializer.Default.Deserialize(record.Ticket.ToArray())!.Properties;

    /// <summary>The fragment's token <paramref name="name"/>, or null when the record holds none for it.</summary>
    public static string? FragmentToken(SessionRecord record, string fragmentId, string name) =>
        Properties(record).Items.TryGetValue(".Token." + name + ":" + fragmentId, out string? value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>The shell's token <paramref name="name"/>.</summary>
    public static string? ShellToken(SessionRecord record, string name) => Properties(record).GetTokenValue(name);

    /// <summary>
    /// Another writer's turn: the record one version on with the fragment's set removed, written through
    /// the shared store as a third task's write would land (C in Trace 4).
    /// </summary>
    public static async Task<SessionRecord> WithoutFragmentAsync(ISessionStore store, string key, string fragmentId, CancellationToken ct)
    {
        SessionRecord current = await store.GetAsync(key, ct) ?? throw new InvalidOperationException("no record to rewrite.");
        AuthenticationTicket ticket = TicketSerializer.Default.Deserialize(current.Ticket.ToArray())!;
        foreach (string item in ticket.Properties.Items.Keys
            .Where(k => k.EndsWith(":" + fragmentId, StringComparison.Ordinal)
                && (k.StartsWith(".Token.", StringComparison.Ordinal) || k.StartsWith(".Fragment.", StringComparison.Ordinal)))
            .ToList())
        {
            ticket.Properties.Items.Remove(item);
        }

        SessionRecord next = current with { Version = current.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) };
        if (!await store.ReplaceAsync(key, next, current.Version, ct))
        {
            throw new InvalidOperationException("the other writer's turn lost its own replace.");
        }

        return next;
    }

    /// <summary>Another writer's turn that changes nothing but the version: the record one version on.</summary>
    public static async Task BumpAsync(ISessionStore store, string key, CancellationToken ct)
    {
        SessionRecord current = await store.GetAsync(key, ct) ?? throw new InvalidOperationException("no record to bump.");
        if (!await store.ReplaceAsync(key, current with { Version = current.Version + 1 }, current.Version, ct))
        {
            throw new InvalidOperationException("the bump lost its own replace.");
        }
    }
}
