using System.Text.Json;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// What a continuity row writes when it ends (#358 Q142): its attempts and wall time so far, reruns
/// included, its verdict, what it observed and the steps it sent. A step holds a name, a client trace
/// id, a time, a status and an exception's type; an observation holds task ids, trace ids and
/// <c>session_ref</c>, which contracts/telemetry.md makes safe to log. No field can hold a cookie, a
/// token, a session key or a ticket.
/// </summary>
public sealed record RowEvidence(
    string Row,
    int Attempts,
    double WallSeconds,
    Verdict Verdict,
    string Reason,
    IReadOnlyList<string> Observations,
    IReadOnlyList<Attempt> Steps);

/// <summary>What is left of a row's bounds (Q144, Q148).</summary>
public sealed record RowBudget(int Attempts, TimeSpan Wall)
{
    public bool Spent => Attempts <= 0 || Wall <= TimeSpan.Zero;

    public static RowBudget Full => new(ContinuityBounds.MaxAttempts, ContinuityBounds.MaxWall);
}

/// <summary>
/// The evidence directory <c>SPA_E2E_EVIDENCE</c> names, one JSON file per row (Q142). A path
/// <c>git check-ignore</c> does not report is refused before anything is written, so evidence never
/// lands where a commit could take it (#63 Q75's convention). A rerun reads the row's earlier file and
/// spends only what is left (Q148); a fresh budget needs a fresh directory.
/// </summary>
public sealed class ContinuityEvidence(string directory, ICommandRunner runner)
{
    public const string Variable = "SPA_E2E_EVIDENCE";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string PathOf(string row) => Path.Combine(Path.GetFullPath(directory), row + ".json");

    /// <summary>The row's earlier evidence, if a run wrote any into this directory.</summary>
    public async Task<RowEvidence?> EarlierAsync(string row, CancellationToken ct)
    {
        await RefuseUnlessIgnoredAsync(row, ct);
        string path = PathOf(row);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<RowEvidence>(await File.ReadAllTextAsync(path, ct), Json)
                ?? throw new InvalidOperationException($"{path} holds no evidence.")
            : null;
    }

    /// <summary>What is left of the row's bounds once the earlier runs' attempts and wall time are taken off.</summary>
    public async Task<RowBudget> BudgetAsync(string row, CancellationToken ct) => Left(await EarlierAsync(row, ct));

    public static RowBudget Left(RowEvidence? earlier) => earlier is null
        ? RowBudget.Full
        : new RowBudget(ContinuityBounds.MaxAttempts - earlier.Attempts, ContinuityBounds.MaxWall - TimeSpan.FromSeconds(earlier.WallSeconds));

    /// <summary>
    /// Writes the row's file: this run's attempts and wall time added to the earlier file's, and this
    /// run's steps after the earlier ones.
    /// </summary>
    public async Task<RowEvidence> WriteAsync(string row, RowResult result, IReadOnlyList<Attempt> attempts, TimeSpan wall, CancellationToken ct)
    {
        RowEvidence? earlier = await EarlierAsync(row, ct);
        RowEvidence evidence = new(
            row,
            (earlier?.Attempts ?? 0) + attempts.Count,
            (earlier?.WallSeconds ?? 0) + wall.TotalSeconds,
            result.Verdict,
            result.Reason,
            result.Observations,
            [.. earlier?.Steps ?? [], .. attempts]);
        Directory.CreateDirectory(Path.GetFullPath(directory));
        await File.WriteAllTextAsync(PathOf(row), JsonSerializer.Serialize(evidence, Json), ct);
        return evidence;
    }

    /// <summary>
    /// <c>git check-ignore -q</c> exits 0 for an ignored path and 1 for one it does not report
    /// (https://git-scm.com/docs/git-check-ignore#_exit_status); it runs from the nearest directory that
    /// exists, since the evidence directory may not yet.
    /// </summary>
    private async Task RefuseUnlessIgnoredAsync(string row, CancellationToken ct)
    {
        string path = PathOf(row);
        string from = Path.GetDirectoryName(path)!;
        while (!Directory.Exists(from) && Path.GetDirectoryName(from) is { } parent)
        {
            from = parent;
        }

        CommandResult answer = await runner.RunAsync(new Command("git", ["-C", from, "check-ignore", "-q", "--", path]) { AcceptedExits = [0, 1] }, ct);
        if (answer.ExitCode != 0)
        {
            throw new InvalidOperationException($"{path} is not ignored by git: {Variable} must name a directory git check-ignore reports, such as one under .scratch/sitting/log/.");
        }
    }
}
