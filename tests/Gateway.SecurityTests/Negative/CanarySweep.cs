using System.Collections.Concurrent;
using Gateway.TestHarness;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-16: "Run the full suite with canary values for tokens, secrets, cookies, codes and state, then
/// search every log line. No canary appears."
///
/// <para>The row is a property of the whole run, not of one request, so it cannot be one test's
/// arrange-act-assert. Two things make the claim whole here:</para>
/// <list type="number">
/// <item><b>Every harness enrols itself.</b> <see cref="Suite.StartAsync"/> calls
/// <see cref="Watch"/>, so every line any harness in this project logged is in
/// <see cref="Lines"/>, whatever test produced it and whether or not that test looked at a log.</item>
/// <item><b>The sweep runs after the last test.</b> <see cref="SuiteSweep"/> is an assembly fixture,
/// which xUnit disposes once every test class has finished, so the final sweep reads the run's whole
/// log. <see cref="CanaryTests"/> also sweeps as a named test, so the row shows up in the Tests tab
/// and fails with a test's own message rather than only as a cleanup error.</item>
/// </list>
///
/// <para><b>Where the canaries come from.</b> Two sources, and both are per-run values rather than
/// constants, so a hit is this run's leak and not a word that happens to appear in a message:</para>
/// <list type="bullet">
/// <item>The <b>client secrets</b> the harness minted. <c>GatewayHarness.Register</c> builds each as
/// <c>secret-{appId}-{16 random bytes}</c> "so a secret seen in a log line is this run's and no
/// other's (N-16)", and the gateway reads them through the harness's
/// <c>FakeClientSecretSource</c>. Nothing in this project types a secret: they are collected from
/// <see cref="GatewayHarness.Apps"/>, so an app the harness adds later is covered without this file
/// changing.</item>
/// <item>The <b>credentials the suite sends</b>: <see cref="Canaries"/>, each tagged with the run's
/// own identifier. They travel as a cookie value, a bearer token, a CSRF header and an authorization
/// code and state in a query string — the five kinds the row names.</item>
/// <item>The <b>tokens the test provider issued</b> to every enrolled harness: each access and refresh
/// token a signed-in row obtained, rotated ones included. These are the real credentials the gateway
/// held, so a line that logged one would be the leak the row is about, not a planted value.</item>
/// </list>
///
/// <para>The search is ordinal and case-sensitive over the <b>rendered</b> line, which is what
/// <c>CapturedLog.Rendered</c> holds: the bytes the deployed log stream would carry, formatter and
/// redaction included, not the structured state behind them.</para>
/// </summary>
internal static class CanarySweep
{
    private static readonly ConcurrentBag<GatewayHarness> watched = [];

    /// <summary>Enrols one harness: its log is swept, and its client secrets become canaries.</summary>
    public static void Watch(GatewayHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        watched.Add(harness);
    }

    /// <summary>How many harnesses have been enrolled, which the sweep reports so an empty sweep cannot pass quietly.</summary>
    public static int Harnesses => watched.Count;

    /// <summary>Every line every enrolled harness has logged so far, as its formatter rendered it.</summary>
    public static IReadOnlyList<string> Lines => [.. watched.SelectMany(harness => harness.Log.Rendered)];

    /// <summary>
    /// Every value that may not appear in a log line: the suite's own canaries, every client secret
    /// any enrolled harness minted, and every token its provider issued.
    /// </summary>
    public static IReadOnlyList<string> Values =>
    [
        .. Canaries.All,
        .. watched.SelectMany(harness => harness.Apps.Values.Select(app => app.ClientSecret)),
        .. watched.SelectMany(harness => harness.Idp.IssuedAccessTokens.Concat(harness.Idp.IssuedRefreshTokens)),
    ];

    /// <summary>
    /// The leaks in <paramref name="lines"/>, one description per line-and-canary pair. Separated
    /// from <see cref="Assert"/> so the search itself can be held to finding something: a sweep that
    /// is only ever run over a clean log passes whether or not it works
    /// (CODING_STANDARDS.md -> "A check that is vacuous today").
    /// </summary>
    public static IReadOnlyList<string> Leaks(IEnumerable<string> lines, IEnumerable<string> values)
    {
        string[] canaries = [.. values];
        List<string> leaks = [];
        foreach (string line in lines)
        {
            foreach (string value in canaries)
            {
                if (line.Contains(value, StringComparison.Ordinal))
                {
                    leaks.Add(Describe(line, value));
                }
            }
        }

        return leaks;
    }

    /// <summary>
    /// Sweeps every line of every enrolled harness for every canary. The failure message names the
    /// canary's kind and where in the line it sat, never the canary: a test runner's output is a log
    /// too, and this one is read by whoever is debugging the leak.
    /// </summary>
    public static void Assert(string what)
    {
        IReadOnlyList<string> lines = Lines;
        IReadOnlyList<string> values = Values;

        // An empty sweep is the hazard CODING_STANDARDS.md -> "A check that is vacuous today" names:
        // with no lines or no canaries, a sweep reports an absence it never looked for. Per harness,
        // too: one whose console formatter went missing renders nothing, and the others' lines would
        // hide that it was never searched.
        Xunit.Assert.NotEmpty(lines);
        Xunit.Assert.NotEmpty(values);
        Xunit.Assert.True(
            watched.All(harness => harness.Log.Rendered.Count > 0),
            $"{what}: an enrolled harness rendered no log line, so the sweep could not search it.");

        IReadOnlyList<string> leaks = Leaks(lines, values);
        Xunit.Assert.True(
            leaks.Count == 0,
            $"a canary reached a log line. {what}: {lines.Count} lines from {Harnesses} harnesses, "
            + $"{values.Count} canaries. " + string.Join("; ", leaks.Take(5)));
    }

    /// <summary>
    /// Names a leak without repeating the credential: which canary kind was found, and the line's own
    /// identifying fields. Printing the line would put the credential in the test output.
    /// </summary>
    private static string Describe(string line, string value)
    {
        string kind = Canaries.KindOf(value) ?? "a client secret or an issued token";
        int at = line.IndexOf(value, StringComparison.Ordinal);
        return $"{kind} appears at offset {at} of a {line.Length}-character line";
    }
}

/// <summary>
/// The credentials this suite sends, so that a line carrying one is a leak rather than a coincidence.
/// Each is tagged with <see cref="Run"/>, a value minted once per test process.
/// </summary>
internal static class Canaries
{
    /// <summary>This run's tag. Random, so a canary cannot match a word in a message by accident.</summary>
    private static readonly string Run = Guid.NewGuid().ToString("N");

    /// <summary>A session cookie's value, as a browser would send it.</summary>
    public static string Cookie { get; } = "canary-cookie-" + Run;

    /// <summary>A bearer token the caller sends, which no upstream and no log may see.</summary>
    public static string Token { get; } = "canary-token-" + Run;

    /// <summary>An antiforgery token in <c>X-CSRF-TOKEN</c>.</summary>
    public static string Csrf { get; } = "canary-csrf-" + Run;

    /// <summary>An authorization code in a query string (contracts/telemetry.md -> Why query strings are dropped).</summary>
    public static string Code { get; } = "canary-code-" + Run;

    /// <summary>An OAuth <c>state</c> in a query string.</summary>
    public static string State { get; } = "canary-state-" + Run;

    public static IReadOnlyList<string> All => [Cookie, Token, Csrf, Code, State];

    /// <summary>The kind of one canary, for a failure message that need not quote it.</summary>
    public static string? KindOf(string value) =>
        value == Cookie ? "a cookie value"
        : value == Token ? "a bearer token"
        : value == Csrf ? "a CSRF token"
        : value == Code ? "an authorization code"
        : value == State ? "an OAuth state"
        : null;
}
