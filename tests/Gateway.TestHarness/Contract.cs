using System.Collections.Immutable;

namespace Gateway.TestHarness;

/// <summary>
/// The contract reader: the contracts under <c>contracts/</c>, read into typed values for the
/// negative, integration and end-to-end suites to assert against, so an assertion compares the
/// gateway with the document rather than with a copy of it. CODING_STANDARDS.md -> "A guard reads the
/// thing it guards": a cookie name, an error code or a header list repeated beside an assertion would
/// still agree with itself after the contract changed.
///
/// <para>One reader in the harness, which all three suites reference and which is workstream 6's,
/// replaces the copy each suite kept (#139). The suites still share the harness and nothing else, so
/// no suite depends on another's internals: the reader is in neither.</para>
///
/// <para>Each member reads its table once, lazily, from the repository's <c>contracts/</c> through
/// <see cref="FixtureFiles.RepositoryRoot"/>. A table that no longer parses fails only the tests that
/// read it, with an <see cref="InvalidOperationException"/> naming the file and the section, where
/// static initialisers would fail every test that touched the class with a type-initialisation
/// error. No member returns empty: a shape that is not there throws, because an empty list would make
/// every assertion built on it vacuous (CODING_STANDARDS.md -> "An assertion must be able to fail").
/// The parsing is <see cref="ContractParser"/>'s, which <c>Gateway.IntegrationTests/Harness/ContractTests</c>
/// feeds planted text; <see cref="ContractParser"/>'s summary names the parsers that text breaks and
/// the ones it does not.</para>
///
/// <para>Rule ids are not here, deliberately: they come from <see cref="Platform.Abstractions.RuleIds"/>,
/// which ADR-0015 put in the abstractions so four workstreams cannot keep four copies, and
/// <c>RuleIdsTests</c> already holds that type to the request gate's table. Reading the table twice
/// would add nothing.</para>
/// </summary>
public static class Contract
{
    private static readonly Lazy<ImmutableArray<string>> CookieNames =
        new(() => ContractParser.CookieNames(Read(ContractParser.BrowserContract)));

    private static readonly Lazy<string> CookiePrefix =
        new(() => ContractParser.CookiePrefix(Read(ContractParser.RequestGate)));

    private static readonly Lazy<string> CsrfHeader =
        new(() => ContractParser.CsrfHeaderName(Read(ContractParser.BrowserContract)));

    private static readonly Lazy<ImmutableDictionary<string, string>> FixedHeaders =
        new(() => ContractParser.FixedResponseHeaders(Read(ContractParser.BrowserContract)));

    private static readonly Lazy<string> ForbiddenPrefix =
        new(() => ContractParser.ForbiddenHeaderPrefix(Read(ContractParser.BrowserContract)));

    private static readonly Lazy<string> Baseline =
        new(() => ContractParser.BaselineCspTemplate(Read(ContractParser.BrowserContract)));

    private static readonly Lazy<ImmutableArray<ErrorRow>> ErrorTable =
        new(() => ContractParser.ErrorRows(Read(ContractParser.BrowserContract)));

    private static readonly Lazy<string> AssetNotFoundCaching =
        new(() => ContractParser.AssetNotFoundCacheControl(Read(ContractParser.BrowserContract)));

    private static readonly Lazy<ImmutableArray<string>> LogFieldNames =
        new(() => ContractParser.TelemetryNames(Read(ContractParser.TelemetryContract), "Log fields"));

    private static readonly Lazy<ImmutableArray<string>> RouteValueNames =
        new(() => ContractParser.TelemetryNames(Read(ContractParser.TelemetryContract), "`route` values"));

    private static readonly Lazy<ImmutableArray<string>> MetricNameList =
        new(() => ContractParser.TelemetryNames(Read(ContractParser.TelemetryContract), "Metrics"));

    /// <summary>
    /// The platform cookie names, from the <b>rows</b> of <c>contracts/http-contract.md</c> -> Cookies,
    /// in table order. A wildcard name (<c>__Host-spa-corr.*</c>) keeps its star: a test substitutes a
    /// suffix for it, as the framework does per login.
    /// </summary>
    public static ImmutableArray<string> PlatformCookieNames => CookieNames.Value;

    /// <summary>
    /// The prefix the gate's cookie-name rule is written against, from <c>contracts/request-gate.md</c>
    /// -> "The rule that covers every path": the subject of the rule N-25 exercises.
    /// </summary>
    public static string PlatformCookiePrefix => CookiePrefix.Value;

    /// <summary>
    /// The header an unsafe <c>/api</c> call carries its antiforgery token in, from the one row of
    /// <c>contracts/http-contract.md</c> -> "Request headers the SPA sends" (N-06).
    /// </summary>
    public static string CsrfHeaderName => CsrfHeader.Value;

    /// <summary>
    /// The response headers with a fixed value, from the <c>Name: value</c> bullets under "Response
    /// headers", names compared case-insensitively. The <c>Content-Security-Policy</c> bullet names no
    /// value and is left out, and so is the bullet <see cref="ForbiddenHeaderPrefix"/> reads.
    /// </summary>
    public static ImmutableDictionary<string, string> FixedResponseHeaders => FixedHeaders.Value;

    /// <summary>
    /// No header under this prefix is ever sent, from the "Response headers" bullet that forbids
    /// <c>Access-Control-*</c>, read from the bullet like everything else here.
    /// </summary>
    public static string ForbiddenHeaderPrefix => ForbiddenPrefix.Value;

    /// <summary>
    /// The baseline content security policy as one line, from the fenced block under "The baseline
    /// content security policy", with the provider-origin placeholder still in it.
    /// </summary>
    public static string BaselineCspTemplate => Baseline.Value;

    /// <summary>
    /// The <c>Cache-Control</c> value every 404 the asset endpoint answers carries, from
    /// <c>contracts/http-contract.md</c> -> Endpoints (change request issue-381-object-read-refusal, item 4).
    /// </summary>
    public static string AssetNotFoundCacheControl => AssetNotFoundCaching.Value;

    /// <summary>The telemetry contract's request-line field names, in the order its "Log fields" row lists them.</summary>
    public static ImmutableArray<string> LogFields => LogFieldNames.Value;

    /// <summary>The <c>route</c> values the telemetry contract's table allows.</summary>
    public static ImmutableArray<string> RouteValues => RouteValueNames.Value;

    /// <summary>The metric names the telemetry contract's table allows.</summary>
    public static ImmutableArray<string> MetricNames => MetricNameList.Value;

    /// <summary>
    /// The platform cookie whose name ends <paramref name="suffix"/>, from <see cref="PlatformCookieNames"/>.
    /// None or several throws, since either would leave a test holding another cookie than it meant.
    /// </summary>
    public static string PlatformCookie(string suffix)
    {
        string[] matched = [.. PlatformCookieNames.Where(name => name.EndsWith(suffix, StringComparison.Ordinal))];
        return matched.Length == 1
            ? matched[0]
            : throw new InvalidOperationException(
                $"{ContractParser.BrowserContract} -> Cookies has {matched.Length} names ending '{suffix}'.");
    }

    /// <summary>The baseline with the issuer browser origin the gateway was configured with spliced in for its placeholder.</summary>
    public static string BaselineCsp(string issuerBrowserOrigin) =>
        ContractParser.BaselineCsp(BaselineCspTemplate, issuerBrowserOrigin);

    /// <summary>
    /// One policy as its directives, keyed by directive name, in the order they were sent. It splits a
    /// response's policy, not a contract, and is here because comparing a response with the baseline
    /// needs both sides in one shape.
    /// </summary>
    public static ImmutableArray<KeyValuePair<string, string>> Directives(string policy) =>
        [.. policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split(' ', 2))
            .Select(parts => new KeyValuePair<string, string>(parts[0], parts.Length > 1 ? parts[1] : string.Empty))];

    /// <summary>
    /// The row of <c>contracts/http-contract.md</c> -> Errors whose <c>code</c> cell is
    /// <paramref name="code"/>, exactly. The contract calls the code stable and promises nothing of the
    /// "When" prose, so a test names the case by its code, the lookup proves the code is in the table,
    /// and the status is read from the row. A code the table does not hold throws, naming the table.
    /// </summary>
    public static ErrorRow Error(string code) => ContractParser.Error(ErrorTable.Value, code);

    /// <summary>
    /// One row of the Errors table: the status, the problem body's <c>code</c>, and the case its "When"
    /// column names. A row of the table, not a rejection: the table also holds answers such as
    /// <c>session_ended</c> and the 5xx rows.
    /// </summary>
    public sealed record ErrorRow(int Status, string Code, string When);

    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, file));
}
