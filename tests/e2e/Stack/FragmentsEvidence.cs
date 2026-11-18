using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The rows of the deployed fragments class (#1005, T14 of #961), each by the name of its evidence
/// file, and each row's bounds: at most <see cref="RowBound.Attempts"/> attempts and
/// <see cref="RowBound.Wall"/> of wall time, reruns included (#1005 "Bounds"; #358's Q144 and Q148
/// shape). The bounds are constants of the class, so a sitting cannot widen one by an argument.
/// </summary>
public static class FragmentsBounds
{
    public const string O1 = "o1-redirects";
    public const string O2 = "o2-refresh-contention";
    public const string O3 = "o3-successor-revocation";
    public const string O4 = "o4-record-size";
    public const string O5 = "o5-import-map";
    public const string O8 = "o8-fallback";
    public const string O9 = "o9-header-limits";
    public const string O12 = "o12-entry-import";
    public const string O13Chain = "o13-chain";
    public const string O13Refusal = "o13-refusal";
    public const string FT5 = "f-t5-asset-race";
    public const string Row10 = "row10-pre-fragments-refusal";

    /// <summary>
    /// Cognito's managed-login session: a sign-in through managed login sets a session cookie valid for
    /// one hour, inside which the authorization endpoint answers a second client with no page
    /// (https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-managed-login.html;
    /// #961 O8, the "managed-login hour").
    /// </summary>
    public static readonly TimeSpan ManagedLoginHour = TimeSpan.FromHours(1);

    /// <summary>
    /// <c>ChainLifetime</c>, five minutes from minting (#961 "<c>/bff/chain</c>"): N-46's expired step is
    /// sent once this has passed.
    /// </summary>
    public static readonly TimeSpan ChainLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Each row's bound. O8 waits out <see cref="ManagedLoginHour"/> in one attempt, so its wall bound
    /// is the hour and half an hour for the two halves around it (#1005 "Bounds"). O1 runs three engines
    /// at seven values of N with an interactive sign-in each. O3 is a race and gets the most attempts.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RowBound> ByRow = new Dictionary<string, RowBound>(StringComparer.Ordinal)
    {
        [O1] = new(2, TimeSpan.FromMinutes(120)),
        [O2] = new(2, TimeSpan.FromMinutes(90)),
        [O3] = new(10, TimeSpan.FromMinutes(20)),
        [O4] = new(2, TimeSpan.FromMinutes(30)),
        [O5] = new(3, TimeSpan.FromMinutes(20)),
        [O8] = new(1, ManagedLoginHour + TimeSpan.FromMinutes(30)),
        [O9] = new(3, TimeSpan.FromMinutes(20)),
        [O12] = new(3, TimeSpan.FromMinutes(15)),
        [O13Chain] = new(3, TimeSpan.FromMinutes(20)),
        [O13Refusal] = new(3, ChainLifetime + TimeSpan.FromMinutes(15)),
        [FT5] = new(3, TimeSpan.FromMinutes(20)),
        [Row10] = new(1, TimeSpan.FromMinutes(60)),
    };

    public static RowBound Of(string row) =>
        ByRow.TryGetValue(row, out RowBound? bound) ? bound : throw new ArgumentException($"{row} is no row of the fragments class.", nameof(row));
}

/// <summary>One row's bounds, or what is left of them after its earlier runs.</summary>
public sealed record RowBound(int Attempts, TimeSpan Wall)
{
    public bool Spent => Attempts <= 0 || Wall <= TimeSpan.Zero;
}

/// <summary>
/// What every fragments row records beside its figures (#1005 "The rows", acceptance): N, the number of
/// fragments the shell's live registry entry names, read by the driver; the user agents the row ran in,
/// where a browser runs it; and the gateway tasks involved. A missing N is null and is never read as 0.
/// </summary>
public sealed record FragmentsContext(int? N, IReadOnlyList<string> UserAgents, IReadOnlyList<string> TaskIds);

/// <summary>One attempt of a fragments row: its context, the requests it sent, and its recording as the observer read it.</summary>
public sealed record FragmentsAttempt(int Number, FragmentsContext Context, IReadOnlyList<Step> Steps, JsonElement Record);

/// <summary>
/// What a fragments row writes when it ends: its attempts and wall time so far, reruns included, its
/// verdict and observations, the last attempt's N, user agents and task ids, and every attempt. No
/// field is meant to hold a cookie, token, code, state value, continuation, session key or secret,
/// and <see cref="FragmentsEvidence.Unsafe"/> refuses a file that holds one's shape anyway (N-16).
/// </summary>
public sealed record FragmentsRowEvidence(
    string Row,
    int Attempts,
    double WallSeconds,
    Verdict Verdict,
    string Reason,
    IReadOnlyList<string> Observations,
    int? N,
    IReadOnlyList<string> UserAgents,
    IReadOnlyList<string> TaskIds,
    IReadOnlyList<FragmentsAttempt> Runs);

/// <summary>
/// The fragments rows' evidence directory, <c>SPA_E2E_EVIDENCE</c> (#1005 "Evidence"), one JSON file per
/// row. As <see cref="ContinuityEvidence"/> does, a path <c>git check-ignore</c> does not report is
/// refused before anything is read or written, and a rerun reads the row's earlier file and spends only
/// what is left of its bound.
/// </summary>
public sealed partial class FragmentsEvidence(string directory, ICommandRunner runner)
{
    public const string Variable = ContinuityEvidence.Variable;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string PathOf(string row) => Path.Combine(Path.GetFullPath(directory), row + ".json");

    public async Task<FragmentsRowEvidence?> EarlierAsync(string row, CancellationToken ct)
    {
        await RefuseUnlessIgnoredAsync(row, ct);
        string path = PathOf(row);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<FragmentsRowEvidence>(await File.ReadAllTextAsync(path, ct), Json)
                ?? throw new InvalidOperationException($"{path} holds no evidence.")
            : null;
    }

    public async Task<RowBound> BudgetAsync(string row, CancellationToken ct) => Left(row, await EarlierAsync(row, ct));

    /// <summary>The row's bound less the earlier runs' attempts and wall time.</summary>
    public static RowBound Left(string row, FragmentsRowEvidence? earlier)
    {
        RowBound bound = FragmentsBounds.Of(row);
        return earlier is null ? bound : new RowBound(bound.Attempts - earlier.Attempts, bound.Wall - TimeSpan.FromSeconds(earlier.WallSeconds));
    }

    /// <summary>
    /// Writes the row's file: this run's attempts and wall time added to the earlier file's, its attempts
    /// after the earlier ones. A file whose text holds a credential's shape is refused whole, naming the
    /// kind and never the value (<see cref="Unsafe"/>).
    /// </summary>
    public async Task<FragmentsRowEvidence> WriteAsync(string row, RowResult result, IReadOnlyList<FragmentsAttempt> attempts, int counted, TimeSpan wall, CancellationToken ct)
    {
        FragmentsRowEvidence? earlier = await EarlierAsync(row, ct);
        FragmentsContext last = attempts.Count > 0 ? attempts[^1].Context : new FragmentsContext(earlier?.N, earlier?.UserAgents ?? [], earlier?.TaskIds ?? []);
        FragmentsRowEvidence evidence = new(
            row,
            (earlier?.Attempts ?? 0) + counted,
            (earlier?.WallSeconds ?? 0) + wall.TotalSeconds,
            result.Verdict,
            result.Reason,
            result.Observations,
            last.N,
            last.UserAgents,
            last.TaskIds,
            [.. earlier?.Runs ?? [], .. attempts]);
        string text = JsonSerializer.Serialize(evidence, Json);
        if (Unsafe(text) is { } kind)
        {
            throw new UnsafeEvidenceException(PathOf(row), kind);
        }

        Directory.CreateDirectory(Path.GetFullPath(directory));
        await File.WriteAllTextAsync(PathOf(row), text, ct);
        return evidence;
    }

    /// <summary>
    /// The kind of credential whose shape <paramref name="text"/> holds, or null: a JWT
    /// (<see cref="TokenShape.Jwt"/>), an ASP.NET Core data-protection payload, which every session
    /// cookie, ticket and continuation the gateway issues is (its base64url form starts <c>CfDJ8</c>,
    /// the magic header 0x09F0C9F0 of
    /// https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/authenticated-encryption-details),
    /// a query parameter that carries a code, state, continuation or token (its <c>&amp;</c> as System.Text.Json
    /// escapes it, <c>\u0026</c>, too), or a <c>__Host-</c> cookie
    /// with a value. Printed and written text alike passes through this (N-16's canaries).
    /// </summary>
    public static string? Unsafe(string text) =>
        TokenShape.Jwt().IsMatch(text) ? "a JWT-shaped value"
        : text.Contains("CfDJ8", StringComparison.Ordinal) ? "a data-protection payload (a cookie, ticket or continuation)"
        : SecretParameter().IsMatch(text) ? "a query parameter carrying a code, state, continuation or token"
        : HostCookie().IsMatch(text) ? "a __Host- cookie with its value"
        : null;

    [GeneratedRegex(@"(?:[?&#]|\\u0026)(code|state|session_state|continuation|c|token|access_token|refresh_token|id_token)=", RegexOptions.IgnoreCase, RegexTimeout.Milliseconds)]
    private static partial Regex SecretParameter();

    [GeneratedRegex(@"__Host-[A-Za-z0-9_-]+=[^;\s""]", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex HostCookie();

    private async Task RefuseUnlessIgnoredAsync(string row, CancellationToken ct)
    {
        string path = PathOf(row);
        string from = Path.GetDirectoryName(path)!;
        while (!Directory.Exists(from) && Path.GetDirectoryName(from) is { } parent)
        {
            from = parent;
        }

        // https://git-scm.com/docs/git-check-ignore#_exit_status: 0 for an ignored path, 1 for one it does not report.
        CommandResult answer = await runner.RunAsync(new Command("git", ["-C", from, "check-ignore", "-q", "--", path]) { AcceptedExits = [0, 1] }, ct);
        if (answer.ExitCode != 0)
        {
            throw new InvalidOperationException($"{path} is not ignored by git: {Variable} must name a directory git check-ignore reports, such as one under .scratch/sitting/log/.");
        }
    }
}

/// <summary>An evidence file refused because its text held a credential's shape; the message names the kind, never the value.</summary>
public sealed class UnsafeEvidenceException(string path, string kind)
    : InvalidOperationException($"{path} was not written: the evidence held {kind}.")
{
    public string Kind { get; } = kind;
}
