using System.Globalization;
using System.Text.RegularExpressions;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// What O2 and O4 read from the gateway's own lines (#1005 O2, O4), turned into the figures their
/// records hold: each refresh write's versioned replaces and outcome, each fragment sign-in write's, and
/// the requests and sessions in a window that a workload did not send. The wording is
/// src/Platform.Identity's (AccessTokenSource's "Refresh write …" line and FragmentEvents' "Fragment
/// sign-in … write ended …" line); FragmentsLinesTests reads each from those files and fails when one
/// no longer holds it, as FragmentsObserverTests does the registry's (#872's shape).
/// </summary>
public static partial class FragmentsLines
{
    public const string RefreshWritePrefix = "Refresh write for app ";

    public const string FragmentWritePrefix = "Fragment sign-in for app ";

    /// <summary>The outcome a write ends <c>written</c> with; every other outcome lost its last replace too.</summary>
    public const string Written = "written";

    public const string Exhausted = "exhausted";

    /// <summary>
    /// One refresh write's counts: its versioned replaces, the conflicts among them (every replace but a
    /// final <c>written</c> one lost to another writer, or found the record gone), and whether its budget ran
    /// out. Null for a line that is not a refresh write's.
    /// </summary>
    public static WriteCount? RefreshWrite(GatewayEvent line) =>
        line.Field("msg") is { } msg && msg.StartsWith(RefreshWritePrefix, StringComparison.Ordinal) && Ended().Match(msg) is { Success: true } ended
            ? Count(ended)
            : null;

    /// <summary>A fragment sign-in write's writer (<c>chain</c> or <c>fallback</c>) and its versioned replaces; null for any other line.</summary>
    public static (string Writer, int Attempts)? FragmentWrite(GatewayEvent line) =>
        line.Field("msg") is { } msg && msg.StartsWith(FragmentWritePrefix, StringComparison.Ordinal) && Writer().Match(msg) is { Success: true } write
            ? (write.Groups["writer"].Value, int.Parse(write.Groups["attempts"].Value, CultureInfo.InvariantCulture))
            : null;

    private static WriteCount Count(Match ended)
    {
        int attempts = int.Parse(ended.Groups["attempts"].Value, CultureInfo.InvariantCulture);
        string outcome = ended.Groups["outcome"].Value;
        return new WriteCount(attempts, outcome == Written ? Math.Max(0, attempts - 1) : attempts, outcome == Exhausted);
    }

    /// <summary>The session references a set of steps reached, by their request lines (<see cref="GatewayCapture.SessionRefOf"/>).</summary>
    public static IReadOnlySet<string> SessionsOf(IEnumerable<Step> steps, GatewayCapture capture) =>
        steps.Select(capture.SessionRefOf).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>Every refresh write on the sessions' lines, in the capture's order.</summary>
    public static IReadOnlyList<WriteCount> RefreshWrites(IReadOnlySet<string> sessions, GatewayCapture capture) =>
        [.. capture.Events.Where(line => line.Field("session_ref") is { } session && sessions.Contains(session)).Select(RefreshWrite).OfType<WriteCount>()];

    /// <summary>
    /// The request lines inside <paramref name="from"/> to <paramref name="to"/> that none of
    /// <paramref name="ours"/> sent, each named by its trace id and route: another client's traffic, which
    /// contaminates a capacity sample (#1005 O4: "verify there are no … overlapping workloads").
    /// </summary>
    public static IReadOnlyList<string> Foreign(IEnumerable<Step> ours, GatewayCapture capture, DateTimeOffset from, DateTimeOffset to)
    {
        HashSet<string> traces = ours.Select(step => step.TraceId).ToHashSet(StringComparer.Ordinal);
        return [.. capture.Events
            .Where(line => line.IsRequest && (line.Ts ?? line.Timestamp) >= from && (line.Ts ?? line.Timestamp) < to)
            .Where(line => line.Field("trace_id") is not { } trace || !traces.Contains(trace))
            .Select(line => $"trace {line.Field("trace_id") ?? "none"} route {line.Field("route") ?? "none"} on {line.TaskId}")
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The sessions with lines inside the window that none of <paramref name="ours"/> reached: a writer of
    /// the table other than the workload's sign-ins, as far as the gateway's lines can show one. A writer
    /// that is not the gateway writes no line, so it shows only in the quiet controls the observer reads.
    /// </summary>
    public static IReadOnlyList<string> OtherSessions(IReadOnlySet<string> ours, GatewayCapture capture, DateTimeOffset from, DateTimeOffset to) =>
        [.. capture.Events
            .Where(line => (line.Ts ?? line.Timestamp) >= from && (line.Ts ?? line.Timestamp) < to)
            .Select(line => line.Field("session_ref"))
            .OfType<string>()
            .Where(session => !ours.Contains(session))
            .Distinct(StringComparer.Ordinal)
            .Select(session => $"session {session}")];

    [GeneratedRegex(@": ended (?<outcome>[a-z_]+) after (?<attempts>\d+) versioned replaces\.$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Ended();

    [GeneratedRegex(@": the (?<writer>[a-z]+) write ended (?<outcome>[a-z_]+) after (?<attempts>\d+) versioned replaces\.$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Writer();
}
