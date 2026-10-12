namespace Platform.Abstractions;

/// <summary>
/// Resolves the client secret a registry entry names. SSM Parameter Store in the gateway (ADR-0003,
/// task 2.5), memory in tests. Source: docs/plan/02-contracts.md -> Interfaces. Freezes at G1a.
/// </summary>
public interface IClientSecretSource
{
    /// <summary>
    /// Loads every reference a candidate snapshot names, before the swap. A reference that cannot be
    /// read does not fail the batch: <see cref="Get"/> throws for that one, and the snapshot leaves
    /// the affected app out and reports it (task 2.2).
    /// </summary>
    Task PreloadAsync(IEnumerable<string> secretRefs, CancellationToken ct);

    /// <summary>
    /// Reads a preloaded secret. <b>Never does I/O</b>, because options setup in ASP.NET Core is
    /// synchronous. Throws when the reference was not preloaded or could not be read, and the
    /// message never carries a value (task 2.5, test N-16).
    /// </summary>
    string Get(string secretRef);
}
